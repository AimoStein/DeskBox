using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using DeskBox.Interop;
using DeskBox.Models;
using DeskBox.Services;
using WinForms = System.Windows.Forms;

namespace DeskBox.Views;

internal sealed class DesktopIconWindow : Window
{
    private const int TileWidth = 78;
    private const int TileHeight = 78;
    private const int IconSize = 40;
    private readonly Canvas _canvas = new();
    private readonly Action<string> _merge;
    private IReadOnlyList<DesktopViewInterop.Item> _items = Array.Empty<DesktopViewInterop.Item>();
    private readonly List<(DesktopViewInterop.Item Item, Point Origin)> _placements = new();
    private Point? _dragStart;

    internal DesktopIconWindow(Action<string> merge)
    {
        _merge = merge;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Content = _canvas;
        SourceInitialized += (_, _) => Position();
    }

    internal IntPtr Handle => new WindowInteropHelper(this).Handle;

    internal void Attach(IntPtr desktop)
    {
        if (desktop == IntPtr.Zero || Handle == IntPtr.Zero || NativeMethods.GetWindowOwner(Handle) == desktop) return;
        NativeMethods.SetWindowOwner(Handle, desktop);
        NativeMethods.PushToBottom(Handle);
    }

    internal void UpdateItems(IReadOnlyList<DesktopViewInterop.Item> items, IReadOnlyList<BoxConfig> boxes)
    {
        if (_items.SequenceEqual(items) && _canvas.Children.Count > 0) return;
        _items = items;
        _placements.Clear();
        _canvas.Children.Clear();
        if (Handle == IntPtr.Zero) return;
        Position();
        var desktop = WinForms.SystemInformation.VirtualScreen;
        var area = WinForms.Screen.PrimaryScreen!.WorkingArea;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var occupied = boxes.Where(box => box.Visible)
            .Select(box => new Rect(box.X - Left, box.Y - Top, box.Width, box.Collapsed ? 40 : box.Height)).ToList();
        var start = transform.Transform(new Point(area.Left - desktop.Left + 6, area.Top - desktop.Top + 4));
        var rows = Math.Max(1, (int)(transform.Transform(new Vector(0, area.Height)).Y / TileHeight));
        var columns = Math.Max(1, (int)(transform.Transform(new Vector(area.Width, 0)).X / TileWidth));
        var slots = Enumerable.Range(0, rows * columns)
            .Select(i => new Point(start.X + (i / rows) * TileWidth, start.Y + (i % rows) * TileHeight)).ToArray();
        foreach (var item in items.OrderByDescending(item => item.X.HasValue))
        {
            var original = item.X is { } x && item.Y is { } y && desktop.Contains(x, y)
                ? transform.Transform(new Point(x - desktop.Left, y - desktop.Top)) : (Point?)null;
            var point = original is { } p && Free(p)
                ? p : slots.FirstOrDefault(Free, slots[_placements.Count % slots.Length]);
            occupied.Add(new Rect(point, new Size(TileWidth, TileHeight)));
            _placements.Add((item, point));
            AddTile(item, point);
        }

        bool Free(Point point) => !occupied.Any(rect => rect.IntersectsWith(new Rect(point, new Size(TileWidth, TileHeight))));
    }

    internal IEnumerable<StartupDesktopItem> SnapshotItems()
    {
        foreach (var (item, origin) in _placements)
            yield return StartupDesktopItem.Create(item.Path, item.Name, Left + origin.X, Top + origin.Y, TileWidth, TileHeight, IconSize);
    }

    private void Position()
    {
        var area = WinForms.SystemInformation.VirtualScreen;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var origin = transform.Transform(new Point(area.Left, area.Top));
        var size = transform.Transform(new Vector(area.Width, area.Height));
        Left = origin.X;
        Top = origin.Y;
        Width = size.X;
        Height = size.Y;
        NativeMethods.SetWindowPos(Handle, IntPtr.Zero, area.Left, area.Top, area.Width, area.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER);
    }

    private void AddTile(DesktopViewInterop.Item item, Point point)
    {
        var image = new Image
        {
            Width = IconSize, Height = IconSize,
            Source = IconCache.TryGetFast(item.Path, IconSize) ?? IconCache.Placeholder(IconSize),
        };
        var label = new TextBlock
        {
            Text = item.Name, FontSize = 12, Foreground = Brushes.White, TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, MaxHeight = 31, TextTrimming = TextTrimming.CharacterEllipsis,
            Effect = new DropShadowEffect { Color = Colors.Black, ShadowDepth = 1, BlurRadius = 4, Opacity = 1 },
        };
        var stack = new StackPanel { Margin = new Thickness(2, 2, 2, 0) };
        stack.Children.Add(image);
        stack.Children.Add(label);
        var tile = new Border
        {
            Width = TileWidth, Height = TileHeight, Child = stack, ToolTip = item.Path,
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), Focusable = true,
        };
        Canvas.SetLeft(tile, point.X);
        Canvas.SetTop(tile, point.Y);
        _canvas.Children.Add(tile);
        tile.MouseLeftButtonDown += (_, e) =>
        {
            tile.Focus();
            _dragStart = e.GetPosition(tile);
            if (e.ClickCount == 2)
            {
                _dragStart = null;
                FsUtil.Open(item.Path);
            }
        };
        tile.MouseMove += (_, e) =>
        {
            if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed ||
                (e.GetPosition(tile) - start).Length < SystemParameters.MinimumHorizontalDragDistance) return;
            _dragStart = null;
            var data = VirtualGroups.IsShell(item.Path)
                ? ShellItemInterop.CreateDataObjectFromParsingName(item.Path)
                : new DataObject(DataFormats.FileDrop, new[] { item.Path });
            if (data is null) return;
            try { DragDrop.DoDragDrop(tile, data, DragDropEffects.Copy | DragDropEffects.Link); }
            finally { App.RequestDesktopSweep(); }
        };
        tile.MouseLeftButtonUp += (_, _) => _dragStart = null;
        tile.KeyDown += (_, e) => { if (e.Key == Key.Enter) FsUtil.Open(item.Path); };
        var menu = new ContextMenu();
        AddMenu(menu, "打开", () => FsUtil.Open(item.Path));
        if (!VirtualGroups.IsShell(item.Path)) AddMenu(menu, "打开文件所在位置", () => FsUtil.Reveal(item.Path));
        if (ShortcutIdentity.IsShortcut(item.Path)) AddMenu(menu, "合并同应用的快捷方式…", () => _merge(item.Path));
        tile.ContextMenu = menu;
        _ = LoadIconAsync(item.Path, image);
    }

    private static void AddMenu(ContextMenu menu, string text, Action action)
    {
        var item = new MenuItem { Header = text };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private static async Task LoadIconAsync(string path, Image image)
    {
        if (await IconCache.GetAsync(path, IconSize) is { } icon) image.Source = icon;
    }
}
