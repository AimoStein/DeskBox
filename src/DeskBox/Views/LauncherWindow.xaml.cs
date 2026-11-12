using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
    private static readonly Brush TextBrush = MakeBrush(255, 241, 244, 247);
    private static readonly Brush DimBrush = MakeBrush(255, 189, 201, 211);
    private static readonly Brush CardBrush = MakeBrush(181, 27, 36, 46);
    private readonly AppConfig _config;
    private readonly bool _preview;
    private readonly HashSet<LauncherBackgroundLayer> _backgroundLayers = new();
    private IReadOnlyList<LaunchSection> _sections = Array.Empty<LaunchSection>();
    private LaunchEntry? _firstResult;
    private string? _selected;
    private bool _searching;
    private bool _interacting;
    private bool _draggingEntries;
    private bool _sectionVisualsDirty;
    private bool _sectionRefreshQueued;
    private LauncherColor? _backgroundColor;
    private SettingsWindow? _dockedSettings;

    internal bool IsSessionActive => IsActive || _dockedSettings?.IsActive == true;

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
        SceneHost.Clip = Geometry.Empty;
        Search.TextChanged += (_, _) => Render();
        Overview.SizeChanged += (_, _) => RenderOverview();
        MainArea.SizeChanged += (_, _) => SizeDetail();
        DockPlate.SizeChanged += (_, _) => SizeDock();
        UngroupedButton.Click += (_, _) => ShowSection("desktop");
        AllApps.Click += (_, _) => ShowSection("installed");
        Back.Click += (_, _) => ShowSection(null);
        WireDrop(DockScroll, null);
        DockScroll.PreviewMouseWheel += (_, e) =>
        {
            if (DockScroll.ScrollableWidth <= 0) return;
            DockScroll.ScrollToHorizontalOffset(DockScroll.HorizontalOffset - e.Delta);
            e.Handled = true;
        };
        AddBackground(DockPlate);
        AddBackground(DetailPlate);
        SettingsButton.Click += (_, _) => SettingsRequested?.Invoke();
        AddGroup.Click += (_, _) => CreateGroup();
        PreviewKeyDown += OnKeyDown;
        PreviewTextInput += (_, e) =>
        {
            if (_searching || string.IsNullOrWhiteSpace(e.Text) || e.Text.Any(char.IsControl)) return;
            OpenSearch();
            Search.Text = e.Text;
            Search.CaretIndex = Search.Text.Length;
            e.Handled = true;
        };
        Deactivated += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (!preview && IsVisible && !IsActive && !_interacting && _dockedSettings is null) Hide();
        });
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) _dockedSettings?.Close();
        };
        LayoutUpdated += (_, _) => AlignBackgroundLayers();
        ItemsScroll.ScrollChanged += (_, _) => AlignBackgroundLayers();
        ApplyBackgroundColor();
    }

    internal void Open(POINT? anchor = null)
    {
        if (IsVisible)
        {
            Activate();
            if (_searching) Search.Focus();
            return;
        }
        var foreground = NativeMethods.GetForegroundWindow();
        if (anchor is { } point)
        {
            var screen = WinForms.Screen.FromPoint(new System.Drawing.Point(point.X, point.Y));
            PositionCentered(screen, DpiScale(MonitorFromPoint(point, 2)));
        }
        else
        {
            var screen = foreground == IntPtr.Zero
                ? WinForms.Screen.FromPoint(WinForms.Cursor.Position) : WinForms.Screen.FromHandle(foreground);
            PositionCentered(screen, MonitorScale(foreground));
        }
        ShowSection(null);
        Topmost = true;
        Show();
        Activate();
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
            PositionCentered(WinForms.Screen.FromHandle(launcherHandle), MonitorScale(launcherHandle));
            Topmost = true;
            Activate();
        };

        ShowSection(null);
        var handle = new WindowInteropHelper(this).Handle;
        var area = WinForms.Screen.FromHandle(handle).WorkingArea;
        var scale = MonitorScale(handle);
        var margin = (int)Math.Round(18 * scale);
        var gap = (int)Math.Round(24 * scale);
        var availableWidth = area.Width - 2 * margin - gap;
        var preferredSettingsWidth = Math.Clamp(availableWidth / scale * 0.30, 390, 555);
        var settingsWidth = (int)Math.Round(Math.Min(preferredSettingsWidth * scale, availableWidth * 0.52));
        var settingsHeight = Math.Min((int)Math.Round(920 * scale), area.Height - 2 * margin);
        var (width, height) = MeasureLauncher(availableWidth - settingsWidth, area.Height, scale);
        var x = area.Left + (area.Width - width - gap - settingsWidth) / 2;
        var y = area.Top + (area.Height - height) / 2;
        var settingsY = Math.Max(area.Top + margin, area.Top + (area.Height - settingsHeight) / 2 - (int)(36 * scale));
        Topmost = false;
        PlaceWindow(this, x, y, width, height, scale);
        PlaceWindow(settings, x + width + gap, settingsY, settingsWidth, settingsHeight, scale);
    }

    private void PositionCentered(WinForms.Screen screen, double scale)
    {
        var area = screen.WorkingArea;
        var (width, height) = MeasureLauncher(area.Width, area.Height, scale);
        PlaceWindow(this, area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2,
            width, height, scale);
    }

    private static (int Width, int Height) MeasureLauncher(int width, int height, double scale)
    {
        var diameter = Math.Min(1480, Math.Min(width * 0.97 / scale - 24, height * 0.99 / scale - 108));
        return ((int)Math.Round((diameter + 24) * scale), (int)Math.Round((diameter + 108) * scale));
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
        if (!_sectionVisualsDirty || _draggingEntries || _sectionRefreshQueued) return;
        _sectionRefreshQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            _sectionRefreshQueued = false;
            if (!_sectionVisualsDirty || _draggingEntries) return;
            _sectionVisualsDirty = false;
            RenderDock();
            Render();
        }));
    }

    internal void RefreshAppearance()
    {
        ApplyBackgroundColor();
        RenderDock();
        Render();
    }

    private void Render()
    {
        var query = Search.Text.Trim();
        SearchHint.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_selected is not null && !_sections.Any(section => Id(section) == _selected)) _selected = null;
        Cards.Children.Clear();
        _firstResult = null;
        var showOverview = !_searching && _selected is null;
        OverviewPanel.Visibility = showOverview ? Visibility.Visible : Visibility.Collapsed;
        Detail.Visibility = showOverview ? Visibility.Collapsed : Visibility.Visible;
        Back.Visibility = showOverview ? Visibility.Collapsed : Visibility.Visible;
        SearchBar.Visibility = _searching ? Visibility.Visible : Visibility.Collapsed;
        if (showOverview)
        {
            RenderOverview();
            return;
        }

        if (_searching)
        {
            if (query.Length == 0) return;
            var candidates = _sections.SelectMany(section => section.Items)
                .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .Where(item => item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase));
            var matches = LauncherCatalog.OrderByUsage(candidates, _config)
                .OrderByDescending(item => item.Name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
            var search = new LaunchSection(null, "搜索结果", matches);
            Cards.Children.Add(BuildCard(search, false));
        }
        else
        {
            foreach (var section in _sections.Where(section => Id(section) == _selected))
                Cards.Children.Add(BuildCard(section, true));
        }
        if (Cards.Children.Count == 0) Cards.Children.Add(Subtitle("正在读取快捷方式…"));
    }

    private void ShowSection(string? id)
    {
        _searching = false;
        _selected = id;
        if (Search.Text.Length != 0) Search.Clear();
        else Render();
        ItemsScroll.ScrollToTop();
    }

    private void OpenSearch()
    {
        _searching = true;
        Render();
        Search.Focus();
        ItemsScroll.ScrollToTop();
    }

    private FrameworkElement BuildCard(LaunchSection section, bool canDrop)
    {
        var card = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        card.Children.Add(new Border { Background = CardBrush, CornerRadius = new CornerRadius(17) });
        card.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(17), BorderThickness = new Thickness(1),
            BorderBrush = MakeBrush(100, 125, 147, 164), IsHitTestVisible = false,
        });

        var content = new StackPanel { Margin = new Thickness(12, 9, 12, 9) };
        var heading = new TextBlock
        {
            Text = section.Name, Foreground = TextBrush, FontSize = 15, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(6, 2, 6, 5),
        };
        if (section.Group is { IsVirtual: true } group) heading.ContextMenu = GroupMenu(group);
        content.Children.Add(heading);
        if (section.Items.Count == 0)
            content.Children.Add(Subtitle(section.Group is not null ? "将快捷方式拖到这里" :
                canDrop ? "暂无项目" : "没有匹配的项目"));
        else
        {
            var tiles = new WrapPanel();
            foreach (var entry in section.Items)
            {
                _firstResult ??= entry;
                var tile = BuildTile(entry);
                if (canDrop && section.Group is { IsVirtual: true }) WireDrop(tile, section.Group);
                tiles.Children.Add(tile);
            }
            content.Children.Add(tiles);
        }
        card.Children.Add(content);
        if (canDrop && (section.Group is { IsVirtual: true } || Id(section) == "desktop")) WireDrop(card, section.Group);
        return card;
    }

    private void AddBackground(Grid parent, UIElement? anchor = null, Rect region = default)
    {
        var layer = new LauncherBackgroundLayer(Scene, anchor, region);
        layer.Loaded += (_, _) =>
        {
            _backgroundLayers.Add(layer);
            layer.Align();
        };
        layer.Unloaded += (_, _) => _backgroundLayers.Remove(layer);
        parent.Children.Add(layer);
    }

    private Button BuildTile(LaunchEntry item, Size? compact = null)
    {
        var size = compact is { } bounds
            ? LauncherWheelLayout.CompactIconSize(bounds, _config.IconSize)
            : Math.Clamp((int)_config.IconSize + 8, 40, 96);
        var image = new Image { Width = size, Height = size, Source = IconCache.TryGetFast(item.Path, size) ?? IconCache.Placeholder(size) };
        var stack = new StackPanel();
        stack.Children.Add(image);
        var label = new TextBlock
        {
            Text = item.Name, FontSize = compact is null ? 11.5 : 11, TextAlignment = TextAlignment.Center,
            TextWrapping = compact is null ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 32,
            Margin = new Thickness(0, compact is null ? 4 : LauncherWheelLayout.CompactLabelGap, 0, 0),
        };
        if (compact is not null)
        {
            label.LineHeight = LauncherWheelLayout.CompactLabelHeight;
            label.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        }
        stack.Children.Add(label);
        var tile = PanelButton(stack);
        tile.Width = compact?.Width ?? Math.Max(76, size + 20);
        tile.Height = compact?.Height ?? size + 48;
        tile.Padding = compact is null ? new Thickness(3, 5, 3, 3) : new Thickness(2, 1, 2, 1);
        tile.VerticalContentAlignment = VerticalAlignment.Top;
        var description = item.Name + "\n" + item.Path;
        tile.ToolTip = description;
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
            if (origin is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
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
        tile.ContextMenuOpening += (_, _) => tile.ContextMenu = EntryMenu(item);
        tile.ContextMenu = new ContextMenu();
        return tile;
    }

    private ContextMenu EntryMenu(LaunchEntry item)
    {
        var menu = new ContextMenu();
        AddMenu(menu, "打开", () => Launch(item));
        if (!VirtualGroups.IsShell(item.Path))
        {
            if (!item.IsShortcut || ShortcutIcon.IsLink(item.Path))
                AddMenu(menu, "打开文件所在位置", () => RevealEntry(item, resolveTarget: item.IsShortcut));
            if (item.IsShortcut)
                AddMenu(menu, "打开快捷方式所在位置", () => RevealEntry(item, resolveTarget: false));
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
            AddMenu(menu, "删除快捷方式", () => DeleteShortcut(item));
        }
        return menu;
    }

    private void RevealEntry(LaunchEntry item, bool resolveTarget) => Interact(() =>
    {
        var path = resolveTarget ? item.ResolveTargetFile() : item.Path;
        if (path is null || !FsUtil.PathExists(path))
        {
            ShowEntryWarning(resolveTarget ? "找不到快捷方式指向的文件或文件夹。" :
                item.IsShortcut ? "找不到快捷方式文件。" : "找不到这个文件或文件夹。");
            return;
        }
        if (ShellItemInterop.RevealItem(path)) Hide();
        else ShowEntryWarning("无法在资源管理器中打开这个位置。");
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
        _config.LauncherOpenCounts.Remove(path);
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
            if (!e.Data.GetDataPresent(EntryFormat, false) && (group is null || !ShellDrop.CanAccept(e.Data))) return;
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        };
        target.Drop += (_, e) =>
        {
            if (e.Data.GetData(EntryFormat, false) is LaunchEntry entry)
                MoveEntries(group, new[] { entry });
            else if (group is not null && ShellDrop.CanAccept(e.Data))
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
        var paths = items.Select(item => item.Path).ToArray();
        if (target is null)
        {
            foreach (var box in _config.Boxes.Where(box => box.IsVirtual))
                VirtualGroups.Remove(box, paths);
        }
        else
        {
            VirtualGroups.Add(_config, target, items.Select(item => new GroupEntry
            {
                Path = item.Path,
                DisplayName = VirtualGroups.IsShell(item.Path) ? item.Name : "",
            }));
        }
        SaveChange();
    }

    internal void CreateGroup() => Interact(() =>
    {
        var name = PromptDialog.Show(this, "新建分组", "分组名称", "")?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        var box = new BoxConfig { Title = name, IsVirtual = true };
        _config.Boxes.Add(box);
        _selected = box.Id;
        Search.Clear();
        SaveChange();
    });

    private void AlignBackgroundLayers()
    {
        if (!IsVisible) return;
        foreach (var surface in _backgroundLayers) surface.Align();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_searching || _selected is not null) ShowSection(null);
            else if (_preview) Close();
            else Hide();
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OpenSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Search.IsKeyboardFocusWithin && Search.Text.Trim().Length != 0 &&
                 _firstResult is { } item)
        {
            Launch(item);
            e.Handled = true;
        }
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

    private Button PanelButton(object content) => new() { Content = content, Style = (Style)FindResource("PanelButton") };
    private static string Id(LaunchSection section) => section.Group?.Id ?? (section.Name == "未分组" ? "desktop" : "installed");
    private static TextBlock Subtitle(string value) => new()
    {
        Text = value, Foreground = DimBrush, Margin = new Thickness(6, 4, 6, 7), FontSize = 12,
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
