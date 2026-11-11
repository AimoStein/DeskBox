using System.IO;
using System.Text.Json;
using DeskBox.Interop;
using DeskBox.Models;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace DeskBox.Services;

internal sealed record StartupDesktopItem(string Path, string Label, double X, double Y, double Width,
    double Height, int IconSize, string IconFile)
{
    internal static StartupDesktopItem Create(string path, string label, double x, double y,
        double width, double height, int size) =>
        new(path, label, x, y, width, height, size,
            System.IO.Path.GetFileName(IconDiskCache.PathFor(IconLoader.BuildKey(path, size))));
}

internal sealed record StartupDesktopBox(string Title, double X, double Y, double Width, double Height,
    bool ListView, IReadOnlyList<StartupDesktopItem> Items);

internal sealed record StartupDesktopLayout(IReadOnlyList<StartupDesktopBox> Boxes, IReadOnlyList<StartupDesktopItem> Items)
{
    private static string SnapshotPath => Path.Combine(ConfigStore.Directory, "desktop-startup.json");

    internal static StartupDesktopLayout? Read()
    {
        try
        {
            var file = new FileInfo(SnapshotPath);
            if (!file.Exists || file.Length > 4 * 1024 * 1024) return null;
            return JsonSerializer.Deserialize<StartupDesktopLayout>(File.ReadAllText(file.FullName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal void Save()
    {
        try
        {
            var path = SnapshotPath;
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn("保存启动桌面快照失败: " + ex.Message);
        }
    }
}

/// <summary>从磁盘快照在独立 STA 上绘制，主窗口构造期间仍能打开快捷方式。</summary>
internal sealed class StartupDesktop
{
    private readonly DesktopIconLayer _layer;
    private volatile bool _stop;
    private volatile bool _finished;

    internal StartupDesktop(DesktopIconLayer layer)
    {
        _layer = layer;
        var thread = new Thread(Run) { IsBackground = true, Name = "DeskBox startup desktop" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    internal bool IsFinished => _finished;
    internal void Stop() => _stop = true;

    private void Run()
    {
        var windows = new List<PreviewWindow>();
        try
        {
            var layout = StartupDesktopLayout.Read();
            if (_stop || layout is null || (layout.Boxes.Count == 0 && layout.Items.Count == 0)) return;
            var virtualScreen = WinForms.SystemInformation.VirtualScreen;
            using var graphics = Drawing.Graphics.FromHwnd(IntPtr.Zero);
            var scale = graphics.DpiX / 96;
            if (layout.Items.Count > 0)
                windows.Add(new PreviewWindow(virtualScreen, layout.Items, null, scale));
            foreach (var box in layout.Boxes)
            {
                var bounds = Drawing.Rectangle.Round(new Drawing.RectangleF((float)box.X * scale, (float)box.Y * scale,
                    (float)box.Width * scale, (float)box.Height * scale));
                if (bounds.Width > 0 && bounds.Height > 0 && bounds.IntersectsWith(virtualScreen))
                    windows.Add(new PreviewWindow(bounds, box.Items, box, scale));
            }
            if (_stop || windows.Count == 0) return;

            using var loop = new WinForms.ApplicationContext();
            using var timer = new WinForms.Timer { Interval = 50 };
            var shown = false;
            void Update()
            {
                if (_stop)
                {
                    timer.Stop();
                    foreach (var window in windows) window.Hide();
                    loop.ExitThread();
                    return;
                }
                if (!_layer.Suppress())
                {
                    foreach (var window in windows) window.Hide();
                    shown = false;
                    return;
                }
                var desktop = NativeMethods.DesktopOwnerWindow();
                foreach (var window in windows)
                {
                    if (!window.Visible) window.Show();
                    if (desktop != IntPtr.Zero && NativeMethods.GetWindowOwner(window.Handle) != desktop)
                    {
                        NativeMethods.SetWindowOwner(window.Handle, desktop);
                        NativeMethods.PushToBottom(window.Handle);
                    }
                }
                if (!shown)
                {
                    foreach (var window in windows) window.Update();
                    Log.Info($"[启动] 快照图标首帧自进程启动 {StartupTrace.SinceProcessStartMs}ms");
                    shown = true;
                }
            }

            timer.Tick += (_, _) => Update();
            Update();
            if (!_stop)
            {
                timer.Start();
                WinForms.Application.Run(loop);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("启动桌面快照显示失败: " + ex.Message);
        }
        finally
        {
            foreach (var window in windows) window.Dispose();
            _finished = true;
        }
    }

    private sealed class PreviewWindow : WinForms.Form
    {
        private readonly List<(StartupDesktopItem Item, Drawing.Rectangle Rect, Drawing.Image? Image)> _tiles;
        private readonly StartupDesktopBox? _box;
        private readonly float _scale;

        internal PreviewWindow(Drawing.Rectangle bounds, IReadOnlyList<StartupDesktopItem> items,
            StartupDesktopBox? box, float scale)
        {
            _box = box;
            _scale = scale;
            _tiles = new(items.Count);
            StartPosition = WinForms.FormStartPosition.Manual;
            Bounds = bounds;
            FormBorderStyle = WinForms.FormBorderStyle.None;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            BackColor = box is null ? Drawing.Color.Fuchsia : Drawing.Color.FromArgb(49, 50, 55);
            if (box is null) TransparencyKey = BackColor;

            foreach (var item in items)
            {
                var x = (float)item.X * scale - (box is null ? bounds.X : 0);
                var y = (float)item.Y * scale - (box is null ? bounds.Y : 0);
                var tile = Drawing.Rectangle.Round(new Drawing.RectangleF(x, y,
                    (float)item.Width * scale, (float)item.Height * scale));
                if (tile.IsEmpty || !ClientRectangle.IntersectsWith(tile)) continue;
                _tiles.Add((item, tile, ReadIcon(item.IconFile)));
            }
        }

        protected override bool ShowWithoutActivation => true;

        protected override void OnPaint(WinForms.PaintEventArgs e)
        {
            if (_box is not null)
                WinForms.TextRenderer.DrawText(e.Graphics, _box.Title, Font,
                    new Drawing.Rectangle(13, 0, Math.Max(0, Width - 26), (int)(34 * _scale)), Drawing.Color.White,
                    WinForms.TextFormatFlags.VerticalCenter | WinForms.TextFormatFlags.EndEllipsis);

            foreach (var (item, rect, image) in _tiles)
            {
                if (_box is null) e.Graphics.FillRectangle(Drawing.Brushes.Black, rect);
                var size = Math.Min(rect.Height, (int)(item.IconSize * _scale));
                var list = _box?.ListView == true;
                var icon = new Drawing.Rectangle(list ? rect.Left + 4 : rect.Left + (rect.Width - size) / 2,
                    rect.Top + 2, size, size);
                if (image is not null) e.Graphics.DrawImage(image, icon);
                else WinForms.ControlPaint.DrawBorder(e.Graphics, icon, Drawing.Color.Silver, WinForms.ButtonBorderStyle.Solid);
                var text = list
                    ? Drawing.Rectangle.FromLTRB(icon.Right + 6, rect.Top, rect.Right, rect.Bottom)
                    : Drawing.Rectangle.FromLTRB(rect.Left, icon.Bottom + 2, rect.Right, rect.Bottom);
                var alignment = list ? WinForms.TextFormatFlags.VerticalCenter : WinForms.TextFormatFlags.HorizontalCenter;
                WinForms.TextRenderer.DrawText(e.Graphics, item.Label, Font, text, Drawing.Color.White,
                    alignment | WinForms.TextFormatFlags.WordBreak | WinForms.TextFormatFlags.EndEllipsis);
            }
        }

        protected override void OnMouseDoubleClick(WinForms.MouseEventArgs e)
        {
            if (e.Button != WinForms.MouseButtons.Left) return;
            var tile = _tiles.FirstOrDefault(tile => tile.Rect.Contains(e.Location));
            if (tile.Item is not null) FsUtil.Open(tile.Item.Path);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                foreach (var tile in _tiles) tile.Image?.Dispose();
            base.Dispose(disposing);
        }

        private static Drawing.Image? ReadIcon(string leaf)
        {
            if (leaf.Length != 44 || Path.GetFileName(leaf) != leaf) return null;
            try
            {
                using var source = Drawing.Image.FromFile(Path.Combine(IconDiskCache.Directory, leaf));
                return new Drawing.Bitmap(source);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or OutOfMemoryException)
            {
                return null;
            }
        }
    }
}
