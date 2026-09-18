using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace DeskBox.Services;

/// <summary>托盘图标与菜单。</summary>
public sealed class TrayService : IDisposable
{
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly WinForms.ContextMenuStrip _menu;

    public TrayService()
    {
        _menu = new WinForms.ContextMenuStrip
        {
            Renderer = new TrayRenderer(),
            ShowImageMargin = false,
            Font = new Drawing.Font("Microsoft YaHei UI", 9f),
            Padding = new WinForms.Padding(0, 4, 0, 4),
        };

        ItemArrange = Add("整理盒子（排列整齐）", "arrange");
        ItemNewBox = Add("新建盒子", "newbox");
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        ItemOrganize = Add("整理桌面文件到盒子", "organize");
        ItemUndo = Add("撤销上次整理", "undo");
        ItemToggleIcons = Add("显示 / 隐藏桌面图标", "toggleicons");
        ItemBoxRoot = Add("打开盒子文件夹", "openroot");
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        Add("设置", "settings");
        Add("关于", "about");
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        Add("退出", "exit");

        _menu.Opening += (_, _) => MenuOpening?.Invoke();

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = TrayIconFactory.Create(),
            Text = "DeskBox · 桌面整理",
            Visible = true,
            ContextMenuStrip = _menu,
        };

        _notifyIcon.DoubleClick += (_, _) => Command?.Invoke("settings");
    }

    public event Action<string>? Command;

    public event Action? MenuOpening;

    public WinForms.ToolStripMenuItem ItemOrganize { get; }

    public WinForms.ToolStripMenuItem ItemArrange { get; }

    public WinForms.ToolStripMenuItem ItemNewBox { get; }

    public WinForms.ToolStripMenuItem ItemUndo { get; }

    public WinForms.ToolStripMenuItem ItemToggleIcons { get; }

    public WinForms.ToolStripMenuItem ItemBoxRoot { get; }

    public void ApplyTheme()
    {
        _menu.Renderer = new TrayRenderer();
        _menu.BackColor = ThemeManager.TrayBackColor;
        foreach (WinForms.ToolStripItem item in _menu.Items)
        {
            item.ForeColor = ThemeManager.TrayTextColor;
        }
    }

    public void ShowInfo(string title, string message)
    {
        try
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = message;
            _notifyIcon.ShowBalloonTip(4000);
        }
        catch
        {
            // 忽略
        }
    }

    private WinForms.ToolStripMenuItem Add(string text, string command)
    {
        var item = new WinForms.ToolStripMenuItem(text) { Tag = command };
        item.Click += (_, _) => Command?.Invoke(command);
        _menu.Items.Add(item);
        return item;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
    }

    private sealed class TrayRenderer : WinForms.ToolStripProfessionalRenderer
    {
        public TrayRenderer()
            : base(new TrayColors())
        {
            RoundedEdges = true;
        }

        protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item?.Enabled == false
                ? Drawing.Color.FromArgb(140, ThemeManager.TrayTextColor)
                : ThemeManager.TrayTextColor;
            base.OnRenderItemText(e);
        }
    }

    private sealed class TrayColors : WinForms.ProfessionalColorTable
    {
        private static Drawing.Color Back => ThemeManager.TrayBackColor;

        public override Drawing.Color ToolStripDropDownBackground => Back;

        public override Drawing.Color MenuItemSelected => ThemeManager.IsDark
            ? Drawing.Color.FromArgb(58, 58, 64)
            : Drawing.Color.FromArgb(238, 238, 242);

        public override Drawing.Color MenuItemBorder => Drawing.Color.Transparent;

        public override Drawing.Color MenuBorder => ThemeManager.IsDark
            ? Drawing.Color.FromArgb(70, 70, 78)
            : Drawing.Color.FromArgb(220, 220, 226);

        public override Drawing.Color SeparatorDark => Drawing.Color.Transparent;

        public override Drawing.Color SeparatorLight => ThemeManager.IsDark
            ? Drawing.Color.FromArgb(60, 60, 68)
            : Drawing.Color.FromArgb(232, 232, 236);

        public override Drawing.Color ImageMarginGradientBegin => Back;

        public override Drawing.Color ImageMarginGradientMiddle => Back;

        public override Drawing.Color ImageMarginGradientEnd => Back;
    }
}

internal static class TrayIconFactory
{
    /// <summary>运行时画一个图标：圆角方块 + 2x2 网格，免去二进制资源。</summary>
    public static Drawing.Icon Create(int size = 32)
    {
        var bitmap = new Drawing.Bitmap(size, size);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);

            var accent = ThemeManager.TrayAccentColor;
            var rect = new Drawing.RectangleF(1, 1, size - 2, size - 2);
            using var brush = new Drawing.Drawing2D.LinearGradientBrush(
                rect,
                ControlLight(accent, 0.18f),
                accent,
                Drawing.Drawing2D.LinearGradientMode.ForwardDiagonal);

            using var path = RoundedRect(rect, size * 0.26f);
            g.FillPath(brush, path);

            var pad = size * 0.28f;
            var cell = (size - pad * 2 - size * 0.08f) / 2f;
            var gap = size * 0.08f;

            using var cellBrush = new Drawing.SolidBrush(Drawing.Color.White);
            for (var row = 0; row < 2; row++)
            {
                for (var col = 0; col < 2; col++)
                {
                    var x = pad + col * (cell + gap);
                    var y = pad + row * (cell + gap);
                    using var cellPath = RoundedRect(new Drawing.RectangleF(x, y, cell, cell), cell * 0.28f);
                    g.FillPath(cellBrush, cellPath);
                }
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Drawing.Icon.FromHandle(handle);
            return (Drawing.Icon)temp.Clone();
        }
        finally
        {
            NativeDestroyIcon(handle);
            bitmap.Dispose();
        }
    }

    private static Drawing.Color ControlLight(Drawing.Color color, float amount) =>
        Drawing.Color.FromArgb(
            color.A,
            (int)Math.Min(255, color.R + (255 - color.R) * amount),
            (int)Math.Min(255, color.G + (255 - color.G) * amount),
            (int)Math.Min(255, color.B + (255 - color.B) * amount));

    internal static Drawing.Drawing2D.GraphicsPath RoundedRect(Drawing.RectangleF rect, float radius)
    {
        var path = new Drawing.Drawing2D.GraphicsPath();
        var diameter = radius * 2;

        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private static void NativeDestroyIcon(IntPtr handle) => DestroyIcon(handle);
}
