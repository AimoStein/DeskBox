using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DeskBox.Interop;
using DeskBox.Models;
using DeskBox.Rendering;
using DeskBox.Services;
using Path = System.IO.Path;
using WinForms = System.Windows.Forms;

namespace DeskBox.Views;

internal partial class LauncherWindow : Window
{
    private const string EntryFormat = "DeskBox.LauncherEntry";
    private const double TaskbarGap = 8;
    internal const double PanelHorizontalSpace = 48; // Content margins and scrollbar gutter.
    internal const double PanelVerticalSpace = 98; // Content margins and toolbar.
    internal const double ScrollbarGutter = 16;
    private static readonly Brush TextBrush = MakeBrush(255, 241, 244, 247);
    private static readonly Brush DimBrush = MakeBrush(255, 167, 177, 190);
    private static readonly Brush CardBrush = MakeBrush(12, 218, 230, 249);
    private static readonly Brush CardBorderBrush = MakeBrush(29, 218, 230, 249);
    private readonly AppConfig _config;
    private readonly bool _preview;
    private readonly DispatcherTimer _scrollbarTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private IReadOnlyList<LaunchSection> _sections = Array.Empty<LaunchSection>();
    private StackPanel? _groupsPage;
    private ContextMenu? _openEntryMenu;
    private TextBox? _nameEditor;
    private Action<bool>? _finishRename;
    private static readonly Lazy<string[]> ProgramsFolders = new(LauncherCatalog.ProgramsFolders);
    private LaunchEntry? _firstResult;
    private BoxConfig? _groupToReveal;
    private bool _interacting;
    private bool _draggingEntries;
    private bool _sectionVisualsDirty;
    private bool _sectionRefreshQueued;
    private LauncherColor? _backgroundColor;
    private SettingsWindow? _dockedSettings;

    internal bool IsSessionActive => IsActive || _openEntryMenu is not null || _dockedSettings?.IsActive == true;

    internal event Action? ConfigChanged;
    internal event Action? UsageChanged;
    internal event Action<string>? ShortcutDeleted;
    internal event Action? SettingsRequested;

    internal LauncherWindow(AppConfig config, bool preview = false)
    {
        _config = config;
        _preview = preview;
        InitializeComponent();
        ShowInTaskbar = preview;
        PanelRoot.SizeChanged += (_, _) =>
        {
            var bounds = new Rect(PanelRoot.RenderSize);
            PanelRoot.Clip = new RectangleGeometry(bounds, 24, 24);
        };
        Search.TextChanged += (_, _) =>
        {
            ItemsScroll.ScrollToTop();
            if (Search.Text.Trim().Length == 0) ResizeOverview();
            Render();
        };
        ItemsScroll.SizeChanged += (_, _) => Render();
        ItemsScroll.PreviewMouseWheel += OnOverviewMouseWheel;
        ItemsScroll.ScrollChanged += OnScrollChanged;
        _scrollbarTimer.Tick += (_, _) =>
        {
            if (ItemsScrollbar?.IsMouseCaptureWithin == true) return;
            HideScrollbar(fade: true);
        };
        SettingsButton.Click += (_, _) => SettingsRequested?.Invoke();
        AddGroup.Click += (_, _) => CreateGroup();
        PreviewKeyDown += OnKeyDown;
        PreviewTextInput += (_, e) =>
        {
            if (_nameEditor is not null || Search.IsKeyboardFocusWithin ||
                string.IsNullOrWhiteSpace(e.Text) || e.Text.Any(char.IsControl)) return;
            Search.Focus();
            Search.SelectedText = e.Text;
            Search.CaretIndex = Search.SelectionStart + Search.SelectionLength;
            e.Handled = true;
        };
        Deactivated += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (!preview && IsVisible && !IsActive && !_interacting && _dockedSettings is null &&
                _openEntryMenu is null) Hide();
        });
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) return;
            HideScrollbar(fade: false);
            ItemsScroll.ScrollToTop();
            _finishRename?.Invoke(true);
            _dockedSettings?.Close();
        };
        ApplyBackgroundColor();
    }

    internal void Open(POINT? anchor = null)
    {
        if (IsVisible)
        {
            Activate();
            return;
        }
        var foreground = NativeMethods.GetForegroundWindow();
        if (anchor is null && foreground != IntPtr.Zero)
            PositionNearTaskbar(WinForms.Screen.FromHandle(foreground), MonitorScale(foreground));
        else
        {
            var cursor = WinForms.Cursor.Position;
            var point = anchor ?? new POINT(cursor.X, cursor.Y);
            var screen = WinForms.Screen.FromPoint(new System.Drawing.Point(point.X, point.Y));
            PositionNearTaskbar(screen, DpiScale(MonitorFromPoint(point, 2)));
        }
        Search.Clear();
        ItemsScroll.ScrollToTop();
        Topmost = true;
        Show();
        Activate();
        Keyboard.Focus(PanelRoot);
    }

    internal void DockSettings(SettingsWindow settings)
    {
        if (_dockedSettings == settings) return;
        _dockedSettings = settings;
        settings.DockToLauncher(this);
        settings.Closed += (_, _) =>
        {
            _dockedSettings = null;
            if (!IsVisible) return;
            var launcherHandle = new WindowInteropHelper(this).Handle;
            if (launcherHandle == IntPtr.Zero) return;
            PositionNearTaskbar(WinForms.Screen.FromHandle(launcherHandle), MonitorScale(launcherHandle));
            Topmost = true;
            Activate();
        };

        Search.Clear();
        PositionDockedSettings(settings);
    }

    private void PositionDockedSettings(SettingsWindow settings)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var screen = WinForms.Screen.FromHandle(handle);
        var area = screen.WorkingArea;
        var scale = MonitorScale(handle);
        var margin = (int)Math.Round(18 * scale);
        var gap = (int)Math.Round(24 * scale);
        var availableWidth = area.Width - 2 * margin - gap;
        var preferredSettingsWidth = Math.Clamp(availableWidth / scale * 0.30, 390, 555);
        var settingsWidth = (int)Math.Round(Math.Min(preferredSettingsWidth * scale, availableWidth * 0.52));
        var settingsHeight = Math.Min((int)Math.Round(920 * scale), area.Height - 2 * margin);
        var (width, height) = MeasureLauncher(availableWidth - settingsWidth, area.Height, scale);
        var pairHeight = Math.Max(height, settingsHeight);
        var origin = NearTaskbar(screen.Bounds, area, width + gap + settingsWidth, pairHeight, scale);
        var alignBottom = TaskbarEdge(screen.Bounds, area) == Dock.Bottom;
        Topmost = false;
        PlaceWindow(this, origin.X, origin.Y + (alignBottom ? pairHeight - height : 0), width, height, scale);
        PlaceWindow(settings, origin.X + width + gap, origin.Y + (alignBottom ? pairHeight - settingsHeight : 0),
            settingsWidth, settingsHeight, scale);
    }

    private void PositionNearTaskbar(WinForms.Screen screen, double scale)
    {
        var area = screen.WorkingArea;
        var (width, height) = MeasureLauncher(area.Width, area.Height, scale);
        var origin = NearTaskbar(screen.Bounds, area, width, height, scale);
        PlaceWindow(this, origin.X, origin.Y, width, height, scale);
    }

    internal static System.Drawing.Point NearTaskbar(System.Drawing.Rectangle bounds, System.Drawing.Rectangle work,
        int width, int height, double scale)
    {
        var gap = (int)Math.Round(TaskbarGap * scale);
        var centerX = work.Left + (work.Width - width) / 2;
        var centerY = work.Top + (work.Height - height) / 2;
        return TaskbarEdge(bounds, work) switch
        {
            Dock.Top => new(centerX, work.Top + gap),
            Dock.Left => new(work.Left + gap, centerY),
            Dock.Right => new(work.Right - width - gap, centerY),
            _ => new(centerX, work.Bottom - height - gap),
        };
    }

    private static Dock TaskbarEdge(System.Drawing.Rectangle bounds, System.Drawing.Rectangle work)
    {
        var left = work.Left - bounds.Left;
        var right = bounds.Right - work.Right;
        var top = work.Top - bounds.Top;
        var bottom = bounds.Bottom - work.Bottom;
        var reserved = Math.Max(Math.Max(left, right), Math.Max(top, bottom));
        if (reserved == bottom) return Dock.Bottom;
        if (reserved == top) return Dock.Top;
        return reserved == left ? Dock.Left : Dock.Right;
    }

    private (int Width, int Height) MeasureLauncher(int width, int height, double scale)
    {
        var margin = (int)Math.Round(18 * scale);
        var available = new Size(Math.Max(1, Math.Floor((width - 2 * margin) / scale) - PanelHorizontalSpace),
            Math.Max(1, Math.Floor((height - 2 * margin) / scale) - PanelVerticalSpace));
        var viewport = LauncherGridLayout.MeasureViewport(available, OverviewGroups(),
            _config.LauncherIconSize, _config.LauncherLayerGap);
        return ((int)Math.Ceiling((viewport.Width + PanelHorizontalSpace) * scale),
            (int)Math.Ceiling((viewport.Height + PanelVerticalSpace) * scale));
    }

    private void ResizeOverview()
    {
        if (!IsVisible || Search.Text.Trim().Length != 0) return;
        if (_dockedSettings is { IsVisible: true } settings)
            PositionDockedSettings(settings);
        else
        {
            var handle = new WindowInteropHelper(this).Handle;
            PositionNearTaskbar(WinForms.Screen.FromHandle(handle), MonitorScale(handle));
        }
    }

    private static void PlaceWindow(Window window, int x, int y, int width, int height, double scale)
    {
        window.Width = width / scale;
        window.Height = height / scale;
        var handle = new WindowInteropHelper(window).EnsureHandle();
        NativeMethods.SetWindowPos(handle, IntPtr.Zero, x, y, width, height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER);
    }

    private static double MonitorScale(IntPtr window) => DpiScale(MonitorFromWindow(window, 2));

    private static double DpiScale(IntPtr monitor) =>
        monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1;

    internal void SetSections(IReadOnlyList<LaunchSection> sections)
    {
        if (SameSections(_sections, sections)) return;
        _sections = sections;
        _sectionVisualsDirty = true;
        QueueSectionRefresh();
    }

    private static bool SameSections(IReadOnlyList<LaunchSection> first, IReadOnlyList<LaunchSection> second) =>
        first.Count == second.Count && first.Zip(second).All(pair => pair.First.Name == pair.Second.Name &&
            pair.First.Group == pair.Second.Group && pair.First.Items.SequenceEqual(pair.Second.Items));

    private void QueueSectionRefresh()
    {
        if (!_sectionVisualsDirty || _draggingEntries || _sectionRefreshQueued || _openEntryMenu is not null || _nameEditor is not null) return;
        _sectionRefreshQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            _sectionRefreshQueued = false;
            if (!_sectionVisualsDirty || _draggingEntries || _openEntryMenu is not null || _nameEditor is not null) return;
            _sectionVisualsDirty = false;
            ResizeOverview();
            Render();
        }));
    }

    internal void RefreshAppearance()
    {
        ApplyBackgroundColor();
        ResizeOverview();
        Render();
    }

    private void Render()
    {
        _finishRename?.Invoke(true);
        var viewport = new Size(Math.Max(0, ItemsScroll.ActualWidth - ScrollbarGutter), ItemsScroll.ActualHeight);
        if (viewport.Width < 100 || viewport.Height < 1) return;

        var query = Search.Text.Trim();
        SearchHint.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Cards.Width = viewport.Width;
        Cards.Children.Clear();
        _groupsPage = null;
        _firstResult = null;
        if (query.Length == 0)
        {
            RenderGroups(viewport);
            return;
        }

        var candidates = _sections.SelectMany(section => section.Items)
            .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Where(item => item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        var matches = LauncherCatalog.OrderByUsage(candidates, _config)
            .OrderByDescending(item => item.Name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        _firstResult = matches.FirstOrDefault();
        var search = new LaunchSection(null, "搜索结果", matches);
        var layout = new LauncherGridLayout(_config.LauncherIconSize, 1, _config.LauncherLayerGap);
        Cards.Children.Add(BuildCard(search, layout, viewport.Width, emptyMessage: "没有匹配的项目"));
    }

    private void RenderGroups(Size viewport)
    {
        if (_sections.Count == 0)
        {
            Cards.Children.Add(Subtitle("正在读取快捷方式…"));
            return;
        }

        var installed = _sections.FirstOrDefault(IsAllApps);
        var groups = OverviewGroups();
        var layout = LauncherGridLayout.Fit(viewport, groups, _config.LauncherIconSize, _config.LauncherLayerGap);
        var groupsPage = new StackPanel { MinHeight = viewport.Height };
        Cards.Children.Add(groupsPage);
        var grid = new Grid();
        var columns = new StackPanel[layout.GroupColumns];
        for (var index = 0; index < columns.Length; index++)
        {
            if (index > 0) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.Gap) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            columns[index] = new StackPanel();
            Grid.SetColumn(columns[index], index * 2);
            grid.Children.Add(columns[index]);
        }
        groupsPage.Children.Add(grid);
        var width = layout.CardWidth(viewport.Width, columns.Length);
        var cardIndex = 0;
        foreach (var section in groups)
        {
            if (LauncherGridLayout.IsEmptyUngrouped(section))
            {
                groupsPage.Children.Add(BuildEmptyUngrouped(section.Name, cardIndex > 0 ? layout.Gap : 0));
                continue;
            }
            var column = columns[cardIndex++ % columns.Length];
            var card = BuildCard(section, layout, width, ungrouped: section.Group is null);
            if (column.Children.Count > 0) card.Margin = new Thickness(0, layout.Gap, 0, 0);
            column.Children.Add(card);
            if (_groupToReveal is not null && section.Group == _groupToReveal)
            {
                _groupToReveal = null;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => card.BringIntoView()));
            }
        }

        if (installed is null) return;
        _groupsPage = groupsPage;
        var appsPage = new StackPanel { MinHeight = viewport.Height };
        var appsLayout = new LauncherGridLayout(_config.LauncherIconSize, 1, _config.LauncherLayerGap);
        appsPage.Children.Add(BuildCard(installed, appsLayout, viewport.Width));
        Cards.Children.Add(appsPage);
    }

    private Border BuildEmptyUngrouped(string name, int gap)
    {
        var text = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        text.Inlines.Add(new System.Windows.Documents.Run(name) { FontWeight = FontWeights.SemiBold });
        text.Inlines.Add(new System.Windows.Documents.Run("   ·   将快捷方式拖到这里") { Foreground = DimBrush });
        var strip = new Border
        {
            Child = text, Height = LauncherGridLayout.EmptyUngroupedHeight, Margin = new Thickness(0, gap, 0, 0),
            Padding = new Thickness(14, 0, 14, 0), Background = CardBrush,
            CornerRadius = new CornerRadius(10), BorderBrush = CardBorderBrush, BorderThickness = new Thickness(1),
        };
        WireDrop(strip, null);
        return strip;
    }

    private LaunchSection[] OverviewGroups() => _sections.Where(section => !IsAllApps(section)).ToArray();

    private static bool IsAllApps(LaunchSection section) => section.Group is null && section.Name == "所有应用";

    private ScrollBar? ItemsScrollbar =>
        ItemsScroll.Template?.FindName("PART_VerticalScrollBar", ItemsScroll) as ScrollBar;

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource != ItemsScroll || !IsVisible) return;
        if (ItemsScroll.ScrollableHeight <= 0)
        {
            HideScrollbar(fade: false);
            return;
        }
        if (e.VerticalChange == 0 || ItemsScrollbar is not { } bar) return;
        bar.BeginAnimation(OpacityProperty, null);
        bar.Opacity = 1;
        bar.IsHitTestVisible = true;
        _scrollbarTimer.Stop();
        _scrollbarTimer.Start();
    }

    private void HideScrollbar(bool fade)
    {
        _scrollbarTimer.Stop();
        if (ItemsScrollbar is not { } bar) return;
        var opacity = bar.Opacity;
        bar.BeginAnimation(OpacityProperty, null);
        bar.IsHitTestVisible = false;
        bar.Opacity = 0;
        if (fade && opacity > 0)
            bar.BeginAnimation(OpacityProperty,
                new DoubleAnimation(opacity, 0, TimeSpan.FromMilliseconds(180)) { FillBehavior = FillBehavior.Stop });
    }

    private void OnOverviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_groupsPage is null || _groupsPage.ActualHeight == 0) return;
        var boundary = _groupsPage.ActualHeight;
        var offset = ItemsScroll.VerticalOffset;
        var lastGroupsOffset = Math.Max(0, boundary - ItemsScroll.ViewportHeight);

        // Skip the unused space after the last row of groups when moving between pages.
        if (e.Delta < 0 && offset >= lastGroupsOffset && offset < boundary)
            ItemsScroll.ScrollToVerticalOffset(boundary);
        else if (e.Delta > 0 && offset > lastGroupsOffset && offset <= boundary)
            ItemsScroll.ScrollToVerticalOffset(lastGroupsOffset);
        else return;
        e.Handled = true;
    }

    private FrameworkElement BuildCard(LaunchSection section, LauncherGridLayout layout, double width,
        bool ungrouped = false, string emptyMessage = "暂无项目")
    {
        var content = new StackPanel { Margin = new Thickness(LauncherGridLayout.CardPadding - 1) };
        var card = new Border
        {
            Background = CardBrush, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1),
            BorderBrush = CardBorderBrush, Child = content,
        };
        var heading = new TextBlock
        {
            Text = section.Name, Foreground = TextBrush, FontSize = 15, FontWeight = FontWeights.SemiBold,
            Height = LauncherGridLayout.HeadingHeight, Padding = new Thickness(5, 1, 5, 0),
            TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = section.Name,
        };
        var group = section.Group is { IsVirtual: true } ? section.Group : null;
        if (group is not null) card.ContextMenu = GroupMenu(group);
        if (group is not null || ungrouped) WireDrop(card, group);
        var body = new ContentControl();
        if (IsAllApps(section))
        {
            var filter = new CheckBox
            {
                Content = "隐藏已分组应用", IsChecked = _config.LauncherHideGroupedApps,
                Foreground = TextBrush, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 5, 0),
            };
            var header = new DockPanel { Height = LauncherGridLayout.HeadingHeight };
            DockPanel.SetDock(filter, Dock.Right);
            header.Children.Add(filter);
            header.Children.Add(heading);
            content.Children.Add(header);
            filter.Click += (_, _) =>
            {
                _config.LauncherHideGroupedApps = filter.IsChecked == true;
                RenderItems();
                SaveChange();
            };
        }
        else content.Children.Add(heading);
        content.Children.Add(body);
        RenderItems();
        return card;

        void RenderItems()
        {
            var filtered = IsAllApps(section) && _config.LauncherHideGroupedApps;
            var items = filtered ? LauncherCatalog.ExcludeGroupedApps(section.Items, _sections) : section.Items;
            if (items.Count == 0)
            {
                body.Content = Subtitle(group is not null ? "将快捷方式拖到这里" : filtered ? "没有未分组的应用" : emptyMessage);
                return;
            }

            if (IsAllApps(section))
            {
                var categories = new StackPanel();
                foreach (var (name, entries) in LauncherAppCategories.Group(items, ProgramsFolders.Value))
                {
                    categories.Children.Add(new TextBlock
                    {
                        Text = name, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = DimBrush,
                        Margin = new Thickness(5, 12, 5, 4),
                    });
                    categories.Children.Add(BuildTiles(entries));
                }
                body.Content = categories;
            }
            else body.Content = BuildTiles(items);
        }

        UniformGrid BuildTiles(IReadOnlyList<LaunchEntry> items)
        {
            var columns = Math.Min(items.Count, layout.TileColumns(width));
            var tiles = new UniformGrid
            {
                Columns = columns, Width = columns * layout.TileWidth,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            foreach (var entry in items)
            {
                var tile = BuildTile(entry, layout);
                if (group is not null || ungrouped) WireDrop(tile, group);
                tiles.Children.Add(tile);
            }
            return tiles;
        }
    }

    private Button BuildTile(LaunchEntry item, LauncherGridLayout layout)
    {
        var size = layout.IconSize;
        var scale = new ScaleTransform(1, 1);
        var image = new Image
        {
            Width = size, Height = size,
            Margin = new Thickness(0, LauncherGridLayout.HoverInset, 0, LauncherGridLayout.HoverInset),
            Source = IconCache.TryGetFast(item.Path, size) ?? IconCache.Placeholder(size),
            RenderTransform = scale, RenderTransformOrigin = new Point(0.5, 0.5),
        };
        var stack = new StackPanel();
        stack.Children.Add(image);
        var label = new TextBlock
        {
            Text = item.Name, FontSize = 12, TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = 14, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            MaxHeight = 28, MaxWidth = layout.TileWidth - 6, Margin = new Thickness(0, 5, 0, 0),
        };
        stack.Children.Add(label);
        var tile = new Button { Content = stack, Style = (Style)FindResource("LauncherTile"), Tag = item };
        tile.MouseEnter += (_, _) => AnimateIcon(1 + Math.Min(size * 0.1, 2 * LauncherGridLayout.HoverInset) / size);
        tile.MouseLeave += (_, _) => AnimateIcon(1);
        tile.Width = layout.TileWidth;
        tile.Height = layout.TileHeight;
        tile.Padding = new Thickness(2, 2, 2, 1);
        var description = item.Name + "\n" + item.Path;
        tile.ToolTip = description;
        ToolTipService.SetShowDuration(tile, 1000);
        System.Windows.Automation.AutomationProperties.SetName(tile, item.Name);
        tile.Click += (_, _) => Launch(item);
        tile.Loaded += async (_, _) =>
        {
            var icon = await IconCache.GetAsync(item.Path, size);
            image.Source = icon ?? IconCache.Unavailable(size);
            tile.ToolTip = icon is null ? description + "\n无法读取图标，快捷方式或目标可能已移动。" : description;
        };
        Point? origin = null;
        tile.PreviewMouseLeftButtonDown += (_, e) => origin = e.GetPosition(tile);
        tile.PreviewMouseLeftButtonUp += (_, _) => origin = null;
        tile.PreviewMouseMove += (_, e) =>
        {
            if (_nameEditor is not null || origin is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
            var delta = e.GetPosition(tile) - start;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            origin = null;
            e.Handled = true;
            tile.ReleaseMouseCapture();
            var data = new DataObject(EntryFormat, item);
            if (!VirtualGroups.IsShell(item.Path) && FsUtil.PathExists(item.Path))
                data.SetData(DataFormats.FileDrop, new[] { item.Path });
            _draggingEntries = true;
            try { Interact(() => DragDrop.DoDragDrop(tile, data, DragDropEffects.Copy)); }
            finally
            {
                _draggingEntries = false;
                QueueSectionRefresh();
            }
        };
        var contextMenu = new ContextMenu();
        tile.ContextMenu = contextMenu;
        tile.ContextMenuOpening += (_, e) =>
        {
            if (_nameEditor is not null)
            {
                e.Handled = true;
                return;
            }
            PopulateEntryMenu(contextMenu, item);
        };
        contextMenu.Opened += (_, _) => _openEntryMenu = contextMenu;
        contextMenu.Closed += (_, _) =>
        {
            if (_openEntryMenu == contextMenu) _openEntryMenu = null;
            QueueSectionRefresh();
        };
        return tile;

        void AnimateIcon(double targetScale)
        {
            var animation = new DoubleAnimation(scale.ScaleX, targetScale, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }
    }

    private void PopulateEntryMenu(ContextMenu menu, LaunchEntry item)
    {
        menu.Items.Clear();
        AddMenu(menu, "打开", () => Launch(item));
        if (!VirtualGroups.IsShell(item.Path))
        {
            if (!item.IsShortcut || ShortcutIcon.IsLink(item.Path))
                AddMenu(menu, "打开文件所在位置", () => QueueRevealEntry(item, resolveTarget: item.IsShortcut));
            if (item.IsShortcut)
                AddMenu(menu, "打开快捷方式所在位置", () => QueueRevealEntry(item, resolveTarget: false));
        }
        var source = _config.Boxes.FirstOrDefault(box => box.IsVirtual &&
            box.Entries.Any(entry => string.Equals(entry.Path, item.Path, StringComparison.OrdinalIgnoreCase)));
        if (source is not null) AddMenu(menu, "移出分组", () => MoveEntries(null, new[] { item }));
        var target = new MenuItem { Header = "移到分组" };
        foreach (var box in _config.Boxes.Where(box => box.IsVirtual && box != source))
            AddMenu(target, box.Title, () => MoveEntries(box, new[] { item }));
        if (target.Items.Count > 0) menu.Items.Add(target);
        if (item.IsShortcut)
        {
            menu.Items.Add(new Separator());
            if (AppUninstaller.Find(item) is { } uninstaller)
                AddMenu(menu, "卸载", () => UninstallEntry(item, uninstaller));
            AddMenu(menu, "删除快捷方式", () => DeleteShortcut(item));
        }
    }

    private void QueueRevealEntry(LaunchEntry item, bool resolveTarget) =>
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => RevealEntry(item, resolveTarget)));

    private void RevealEntry(LaunchEntry item, bool resolveTarget) => Interact(() =>
    {
        var path = resolveTarget ? item.ResolveTargetFile() : item.Path;
        if (path is null || !FsUtil.PathExists(path))
        {
            ShowEntryWarning(resolveTarget ? "找不到快捷方式指向的文件或文件夹。" :
                item.IsShortcut ? "找不到快捷方式文件。" : "找不到这个文件或文件夹。");
            return;
        }
        if (FsUtil.TryRevealInExplorer(path)) Hide();
        else ShowEntryWarning("无法在资源管理器中打开这个位置。");
    });

    private void BeginRename(Button tile, LaunchEntry item)
    {
        if (_nameEditor is not null || tile.Content is not StackPanel stack || stack.Children[1] is not TextBlock label) return;
        var editor = new TextBox
        {
            Text = item.Name, Width = tile.Width - 6, Height = 28, Margin = label.Margin,
            Padding = new Thickness(2, 0, 2, 0), FontSize = label.FontSize, TextAlignment = TextAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Top, Foreground = TextBrush, CaretBrush = TextBrush,
            Background = MakeBrush(255, 35, 43, 53), BorderBrush = MakeBrush(255, 129, 165, 200),
        };
        System.Windows.Automation.AutomationProperties.SetName(editor, "修改显示名称");
        _nameEditor = editor;
        _finishRename = Finish;
        ToolTipService.SetIsEnabled(tile, false);
        stack.Children.RemoveAt(1);
        stack.Children.Add(editor);
        editor.LostKeyboardFocus += (_, _) => Finish(true);
        editor.Focus();
        editor.SelectAll();

        void Finish(bool save)
        {
            if (_nameEditor != editor) return;
            _nameEditor = null;
            _finishRename = null;
            var name = editor.Text.Trim();
            var changed = save && name.Length != 0 && !string.Equals(name, item.Name, StringComparison.Ordinal);
            if (changed)
            {
                label.Text = name;
                _config.LauncherDisplayNames[item.Path] = name;
            }
            stack.Children.Remove(editor);
            stack.Children.Add(label);
            ToolTipService.SetIsEnabled(tile, true);
            if (changed) SaveChange();
            QueueSectionRefresh();
        }
    }

    private void UninstallEntry(LaunchEntry item, AppUninstaller uninstaller) => Interact(() =>
    {
        if (AppUninstaller.Find(item) != uninstaller)
        {
            ShowEntryWarning("找不到对应的卸载程序。");
            return;
        }
        try
        {
            uninstaller.Start();
            Hide();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { } // 用户取消了系统提示。
        catch (Win32Exception ex)
        {
            Log.Warn($"启动卸载程序失败 {item.Path}：{ex.Message}");
            ShowEntryWarning($"无法启动「{item.Name}」的卸载程序。");
        }
    });

    private void DeleteShortcut(LaunchEntry item) => Interact(() =>
    {
        var path = item.Path;
        if (!item.IsShortcut || !Path.IsPathFullyQualified(path)) return;

        bool missing;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
            {
                ShowEntryWarning("这个项目是文件夹，不能作为快捷方式删除。");
                return;
            }
            missing = false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { missing = true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"无法访问快捷方式 {path}：{ex.Message}");
            ShowEntryWarning("无法访问这个快捷方式。");
            return;
        }

        var question = missing ? $"找不到「{item.Name}」的快捷方式文件。要从面板移除这条记录吗？" :
            $"将「{item.Name}」的快捷方式放入回收站？原程序或文件会保留。\n\n{path}";
        if (MessageBox.Show(this, question, "删除快捷方式", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        if (!missing && !item.TryRecycleShortcut(new WindowInteropHelper(this).Handle))
        {
            ShowEntryWarning("快捷方式未删除。");
            return;
        }
        foreach (var box in _config.Boxes.Where(box => box.IsVirtual))
        {
            var staleCopies = box.Entries.Where(entry => string.Equals(LaunchEntry.MissingLinkRewriteSource(entry.Path),
                path, StringComparison.OrdinalIgnoreCase)).Select(entry => entry.Path);
            VirtualGroups.Remove(box, staleCopies.Append(path));
        }
        _config.LauncherUngroupedEntries.RemoveAll(entry =>
            string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(LaunchEntry.MissingLinkRewriteSource(entry.Path), path, StringComparison.OrdinalIgnoreCase));
        _config.LauncherOpenCounts.Remove(path);
        _config.LauncherDisplayNames.Remove(path);
        ShortcutDeleted?.Invoke(path);
        SaveChange();
    });

    private void ShowEntryWarning(string message) =>
        MessageBox.Show(this, message, "简桌", MessageBoxButton.OK, MessageBoxImage.Warning);

    private ContextMenu GroupMenu(BoxConfig group)
    {
        var menu = new ContextMenu();
        AddMenu(menu, "重命名分组", () =>
        {
            Interact(() =>
            {
                var name = PromptDialog.Show(this, "重命名分组", "分组名称", group.Title)?.Trim();
                if (string.IsNullOrEmpty(name)) return;
                group.Title = name;
                SaveChange();
            });
        });
        AddMenu(menu, "解散分组", () => Interact(() =>
        {
            if (MessageBox.Show(this, $"解散「{group.Title}」？组内项目会保留在原处。", "简桌",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            var entries = group.Entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Path)).ToArray();
            VirtualGroups.MoveToUngrouped(_config, entries);
            _config.Boxes.Remove(group);
            SaveChange();
        }));
        return menu;
    }

    private void WireDrop(UIElement target, BoxConfig? group)
    {
        target.AllowDrop = true;
        target.DragOver += (_, e) =>
        {
            if (!e.Data.GetDataPresent(EntryFormat, false) && !ShellDrop.CanAccept(e.Data)) return;
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        };
        target.Drop += (_, e) =>
        {
            if (e.Data.GetData(EntryFormat, false) is LaunchEntry entry)
                MoveEntries(group, new[] { entry });
            else if (ShellDrop.CanAccept(e.Data))
                MoveEntries(group, ShellDrop.Read(e.Data)
                    .Select(item => LaunchEntry.FromPath(item.FileSystemPath ?? item.ParsingName, item.DisplayName)).ToArray());
            else return;
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        };
    }

    private void MoveEntries(BoxConfig? target, IReadOnlyList<LaunchEntry> items)
    {
        if (items.Count == 0) return;
        var entries = items.Select(item => new GroupEntry
        {
            Path = item.Path,
            DisplayName = VirtualGroups.IsShell(item.Path) ? OriginalShellName(item) : "",
        });
        if (target is null) VirtualGroups.MoveToUngrouped(_config, entries);
        else VirtualGroups.Add(_config, target, entries);
        SaveChange();

        string OriginalShellName(LaunchEntry item) =>
            VirtualGroups.References(_config).FirstOrDefault(entry =>
                string.Equals(entry.Path, item.Path, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? item.Name;
    }

    internal void CreateGroup() => Interact(() =>
    {
        var name = PromptDialog.Show(this, "新建分组", "分组名称", "")?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        var box = new BoxConfig { Title = name, IsVirtual = true };
        _config.Boxes.Add(box);
        _groupToReveal = box;
        Search.Clear();
        SaveChange();
    });

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_nameEditor is not null)
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                e.Handled = true;
                _finishRename?.Invoke(e.Key == Key.Enter);
                Keyboard.Focus(PanelRoot);
            }
            return;
        }
        if (e.Key == Key.Escape)
        {
            if (Search.Text.Length != 0) Search.Clear();
            else if (_preview) Close();
            else Hide();
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Search.Focus();
            Search.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && Keyboard.Modifiers == ModifierKeys.None &&
                 (FindTile(Mouse.DirectlyOver) ?? FindTile(Keyboard.FocusedElement)) is { Tag: LaunchEntry entry } tile)
        {
            e.Handled = true;
            if (!e.IsRepeat) BeginRename(tile, entry);
        }
        else if (e.Key == Key.Enter && Search.IsKeyboardFocusWithin && Search.Text.Trim().Length != 0 &&
                 _firstResult is { } item)
        {
            Launch(item);
            e.Handled = true;
        }
    }

    private Button? FindTile(IInputElement? element)
    {
        for (var visual = element as Visual; visual is not null && visual != PanelRoot;
             visual = VisualTreeHelper.GetParent(visual) as Visual)
            if (visual is Button { Tag: LaunchEntry } tile && IsAncestorOf(tile))
                return tile;
        return null;
    }

    private void Launch(LaunchEntry item)
    {
        Hide();
        if (!FsUtil.TryOpen(item.Path)) return;
        _config.RecordLauncherOpen(item.Path);
        UsageChanged?.Invoke();
    }

    private void SaveChange() => ConfigChanged?.Invoke();

    private void Interact(Action action)
    {
        _interacting = true;
        try { action(); }
        finally { _interacting = false; }
    }

    private static TextBlock Subtitle(string value) => new()
    {
        Text = value, Foreground = DimBrush, Padding = new Thickness(5, 0, 5, 0), Height = 24, FontSize = 12,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static void AddMenu(ItemsControl menu, string label, Action action)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private static Brush MakeBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
}
