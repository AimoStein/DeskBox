using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using JianZhuo.Interop;
using JianZhuo.Models;
using JianZhuo.Services;

namespace JianZhuo.Views;

public partial class BoxWindow : Window
{
    public const double ShadowPad = 12;
    private const double TitleHeight = 34;

    private readonly AppConfig _config;
    private readonly BoxConfig _box;
    private readonly DesktopShell _shell;
    private readonly ObservableCollection<BoxItem> _items = new();

    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _zOrderTimer;
    private DispatcherTimer? _persistTimer;
    private DispatcherTimer? _reloadTimer;
    private string _resizeDirection = string.Empty;
    private Point _resizeStart;
    private Rect _resizeOrigin;
    private Point? _dragOrigin;
    private bool _dragging;
    private bool _suppressPersist;
    private bool? _appliedListView;
    private int _lastSelectedIndex = -1;

    public BoxWindow(AppConfig config, BoxConfig box, DesktopShell shell)
    {
        _config = config;
        _box = box;
        _shell = shell;

        InitializeComponent();

        ItemsHost.ItemsSource = _items;
        TitleText.Text = box.Title;
        Title = box.Title;

        ApplyVisualSettings();
        ApplyGeometry();
        ApplyViewMode();
        ApplyLockState();
        ApplyCollapseState();

        AttachWatcher();
        ReloadItems();

        SetupTimers();
        WireEvents();
    }

    /// <summary>需要保存配置。</summary>
    public event EventHandler? Changed;

    /// <summary>用户要求删除这个盒子。</summary>
    public event EventHandler? RemovalRequested;

    public BoxConfig Config => _box;

    public string Folder
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_box.Folder))
            {
                return _box.Folder;
            }

            _box.Folder = Path.Combine(_config.EffectiveBoxRoot, FsUtil.Sanitize(_box.Title));
            return _box.Folder;
        }
    }

    public void SetVisible(bool visible)
    {
        if (visible)
        {
            Show();
            NativeMethods.PushToBottom(new WindowInteropHelper(this).Handle);
        }
        else
        {
            Hide();
        }
    }

    public void RefreshFromConfig(bool reloadItems)
    {
        ApplyVisualSettings();
        ApplyViewMode();

        if (reloadItems)
        {
            ReloadItems();
        }
    }

    #region 初始化

    private void SetupTimers()
    {
        _zOrderTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1500),
        };
        _zOrderTimer.Tick += (_, _) =>
        {
            if (!IsActive && IsVisible && !_dragging)
            {
                PushDown();
            }
        };
        _zOrderTimer.Start();

        _persistTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _persistTimer.Tick += (_, _) =>
        {
            _persistTimer!.Stop();
            PersistGeometry();
        };

        _reloadTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350),
        };
        _reloadTimer.Tick += (_, _) =>
        {
            _reloadTimer!.Stop();
            ReloadItems();
        };
    }

    private void WireEvents()
    {
        SourceInitialized += (_, _) => PushDown();
        Deactivated += (_, _) => PushDown();

        LocationChanged += (_, _) => SchedulePersist();
        SizeChanged += (_, _) =>
        {
            UpdateClip();
            SchedulePersist();
        };

        MouseEnter += (_, _) => FadeActions(1);
        MouseLeave += (_, _) => FadeActions(_box.Locked ? 1 : 0);

        AllowDrop = true;
        DragEnter += Box_DragEnter;
        DragOver += Box_DragEnter;
        DragLeave += (_, _) => DropHighlight.Visibility = Visibility.Collapsed;
        Drop += Box_Drop;

        PreviewMouseWheel += Box_PreviewMouseWheel;

        ItemsHost.PreviewMouseLeftButtonDown += Items_PreviewMouseLeftButtonDown;
        ItemsHost.PreviewMouseMove += Items_PreviewMouseMove;
        ItemsHost.PreviewMouseRightButtonDown += Items_PreviewMouseRightButtonDown;
        ItemsHost.MouseDoubleClick += Items_MouseDoubleClick;
    }

    private void UpdateClip()
    {
        var radius = _config.CornerRadius;
        Frame.Clip = new RectangleGeometry(new Rect(0, 0, Frame.ActualWidth, Frame.ActualHeight), radius, radius);
    }

    private void ApplyVisualSettings()
    {
        Backdrop.Opacity = _config.BoxOpacity;

        var radius = new CornerRadius(_config.CornerRadius);
        Backdrop.CornerRadius = radius;
        Frame.CornerRadius = radius;
        UpdateClip();
    }

    private void ApplyGeometry()
    {
        _suppressPersist = true;

        Width = _box.Width + ShadowPad * 2;
        Height = (_box.Collapsed ? TitleHeight + 8 : _box.Height) + ShadowPad * 2;
        Left = _box.X - ShadowPad;
        Top = _box.Y - ShadowPad;

        _suppressPersist = false;
    }

    private void ApplyCollapseState()
    {
        ContentRow.Height = _box.Collapsed ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        CollapseArrow.RenderTransform = _box.Collapsed
            ? new RotateTransform(-90, 4.5, 2.25)
            : Transform.Identity;
    }

    private void ApplyViewMode()
    {
        if (_appliedListView == _box.ListView)
        {
            return;
        }

        _appliedListView = _box.ListView;

        if (_box.ListView)
        {
            ItemsHost.ItemsPanel = (ItemsPanelTemplate)FindResource("ListPanel");
            ItemsHost.ItemTemplate = (DataTemplate)FindResource("ListItemTemplate");
            Scroller.Padding = new Thickness(0, 2, 0, 2);
            ViewIcon.Fill = null;
            ViewIcon.Stroke = (Brush)FindResource("TextSecondaryBrush");
            ViewIcon.Data = Geometry.Parse("M0,1.2 H12 M0,6 H12 M0,10.8 H12");
        }
        else
        {
            ItemsHost.ItemsPanel = (ItemsPanelTemplate)FindResource("IconPanel");
            ItemsHost.ItemTemplate = (DataTemplate)FindResource("IconItemTemplate");
            Scroller.Padding = new Thickness(2, 2, 2, 2);
            ViewIcon.Stroke = null;
            ViewIcon.Fill = (Brush)FindResource("TextSecondaryBrush");
            ViewIcon.Data = Geometry.Parse("M0,0 H4.6 V4.6 H0 Z M7.2,0 H11.8 V4.6 H7.2 Z M0,7.2 H4.6 V11.8 H0 Z M7.2,7.2 H11.8 V11.8 H7.2 Z");
        }
    }

    private void ApplyLockState()
    {
        var accent = (Brush)FindResource("AccentBrush");
        var secondary = (Brush)FindResource("TextSecondaryBrush");

        LockBody.Fill = _box.Locked ? accent : secondary;
        LockShackle.Stroke = _box.Locked ? accent : secondary;
        LockButton.ToolTip = _box.Locked ? "解除锁定" : "锁定位置";
        Actions.Opacity = _box.Locked ? 1 : Actions.Opacity;
    }

    private void FadeActions(double target)
    {
        if (Math.Abs(Actions.Opacity - target) < 0.01)
        {
            return;
        }

        Actions.BeginAnimation(OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(130))
        {
            FillBehavior = FillBehavior.Stop,
        });
        Actions.Opacity = target;
    }

    private void PushDown()
    {
        try
        {
            NativeMethods.PushToBottom(new WindowInteropHelper(this).Handle);
        }
        catch (Exception ex)
        {
            Log.Warn("调整层叠失败: " + ex.Message);
        }
    }

    #endregion

    #region 内容加载

    private void AttachWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;

        try
        {
            var folder = Folder;
            Directory.CreateDirectory(folder);

            _watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };

            _watcher.Created += OnFolderChanged;
            _watcher.Deleted += OnFolderChanged;
            _watcher.Renamed += OnFolderChanged;
            _watcher.Changed += OnFolderChanged;
        }
        catch (Exception ex)
        {
            Log.Warn($"监视盒子目录失败 {Folder}: {ex.Message}");
        }
    }

    private void OnFolderChanged(object sender, FileSystemEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _reloadTimer?.Stop();
            _reloadTimer?.Start();
        });
    }

    public void ReloadItems()
    {
        var selected = new HashSet<string>(
            _items.Where(i => i.IsSelected).Select(i => i.Path),
            StringComparer.OrdinalIgnoreCase);

        var paths = new List<string>();
        try
        {
            var folder = Folder;
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            {
                if (Path.GetFileName(path).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!_config.ShowHiddenFiles && FsUtil.IsHiddenOrSystem(path))
                {
                    continue;
                }

                paths.Add(path);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取盒子内容失败 {Folder}: {ex.Message}");
        }

        paths.Sort((a, b) =>
        {
            var aDir = Directory.Exists(a);
            var bDir = Directory.Exists(b);
            if (aDir != bDir)
            {
                return aDir ? -1 : 1;
            }

            return string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.CurrentCultureIgnoreCase);
        });

        _items.Clear();
        var size = (int)_config.IconSize;

        foreach (var path in paths)
        {
            var item = new BoxItem(path, size) { IsSelected = selected.Contains(path) };
            _items.Add(item);
            _ = LoadIconAsync(item);
        }

        EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static async Task LoadIconAsync(BoxItem item)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var icon = await IconCache.GetAsync(item.Path, item.IconSize);
                if (icon is not null)
                {
                    if (!ReferenceEquals(item.Icon, icon))
                    {
                        item.Icon = icon;
                    }

                    return;
                }
            }
            catch
            {
                // 保留占位图标
                return;
            }

            await Task.Delay(900);
        }
    }

    #endregion

    #region 拖拽

    private void Box_DragEnter(object sender, DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        DropHighlight.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void Box_Drop(object sender, DragEventArgs e)
    {
        DropHighlight.Visibility = Visibility.Collapsed;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return;
        }

        MoveIntoBox(paths);
        e.Handled = true;
    }

    public void MoveIntoBox(IEnumerable<string> paths)
    {
        var folder = Folder;
        var moved = 0;
        var failed = new List<string>();

        foreach (var path in paths)
        {
            try
            {
                var parent = Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar));
                if (parent is not null && string.Equals(
                        Path.GetFullPath(parent),
                        Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                FsUtil.MoveInto(path, folder);
                moved++;
            }
            catch (Exception ex)
            {
                Log.Warn($"移入盒子失败 {path}: {ex.Message}");
                failed.Add(Path.GetFileName(path));
            }
        }

        ReloadItems();

        if (moved > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        if (failed.Count > 0)
        {
            System.Windows.MessageBox.Show(
                $"以下 {failed.Count} 项未能移动（可能正在被占用）：\n\n{string.Join("\n", failed.Take(8))}",
                "简桌",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void Items_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragOrigin is null || _dragging)
        {
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragOrigin.Value.X) < 6 && Math.Abs(current.Y - _dragOrigin.Value.Y) < 6)
        {
            return;
        }

        var paths = _items.Where(i => i.IsSelected).Select(i => i.Path).ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        var data = new DataObject(DataFormats.FileDrop, paths);
        _dragging = true;
        try
        {
            DragDrop.DoDragDrop(ItemsHost, data, DragDropEffects.Move | DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            Log.Warn("拖出失败: " + ex.Message);
        }
        finally
        {
            _dragging = false;
            _dragOrigin = null;
        }

        ReloadItems();
    }

    #endregion

    #region 选择与菜单

    private BoxItem? ItemAt(object? source)
    {
        var current = source as DependencyObject;

        while (current is not null)
        {
            if (current is FrameworkElement element && element.DataContext is BoxItem item)
            {
                return item;
            }

            if (ReferenceEquals(current, ItemsHost))
            {
                return null;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private void Items_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = ItemAt(e.OriginalSource);

        if (item is null)
        {
            ClearSelection();
            _dragOrigin = null;

            if (!_box.Locked)
            {
                try
                {
                    DragMove();
                }
                catch
                {
                    // 拖动过程中被取消，忽略
                }
            }

            return;
        }

        var index = _items.IndexOf(item);

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            item.IsSelected = !item.IsSelected;
            _lastSelectedIndex = index;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _lastSelectedIndex >= 0)
        {
            var from = Math.Min(_lastSelectedIndex, index);
            var to = Math.Max(_lastSelectedIndex, index);
            for (var i = from; i <= to; i++)
            {
                _items[i].IsSelected = true;
            }
        }
        else if (!item.IsSelected)
        {
            ClearSelection();
            item.IsSelected = true;
            _lastSelectedIndex = index;
        }

        _dragOrigin = e.GetPosition(this);
    }

    private void Items_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = ItemAt(e.OriginalSource);

        if (item is null)
        {
            ItemsHost.ContextMenu = BuildBoxMenu();
            return;
        }

        if (!item.IsSelected)
        {
            ClearSelection();
            item.IsSelected = true;
        }

        ItemsHost.ContextMenu = BuildItemMenu();
    }

    private void Items_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var item = ItemAt(e.OriginalSource);
        if (item is null)
        {
            return;
        }

        FsUtil.Open(item.Path);
        e.Handled = true;
    }

    private void ClearSelection()
    {
        foreach (var item in _items)
        {
            item.IsSelected = false;
        }

        _lastSelectedIndex = -1;
    }

    private List<string> SelectedPaths() =>
        _items.Where(i => i.IsSelected).Select(i => i.Path).ToList();

    private ContextMenu BuildItemMenu()
    {
        var menu = new ContextMenu();
        var paths = SelectedPaths();
        var single = paths.Count == 1 ? paths[0] : null;

        menu.Items.Add(MenuItem("打开", () =>
        {
            foreach (var path in paths)
            {
                FsUtil.Open(path);
            }
        }));

        if (single is not null)
        {
            menu.Items.Add(MenuItem("打开文件所在位置", () => FsUtil.Reveal(single)));
            menu.Items.Add(MenuItem("重命名…", () => RenameItem(single)));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("移动到桌面", () =>
        {
            MoveToDesktop(paths);
        }));
        menu.Items.Add(MenuItem($"删除（共 {paths.Count} 项）", () =>
        {
            if (System.Windows.MessageBox.Show(
                    $"把选中的 {paths.Count} 项放入回收站？",
                    "简桌",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question) != MessageBoxResult.OK)
            {
                return;
            }

            foreach (var path in paths)
            {
                if (!NativeMethods.DeleteToRecycleBin(path))
                {
                    Log.Warn($"删除失败 {path}");
                }
            }

            ReloadItems();
        }));

        return menu;
    }

    private ContextMenu BuildBoxMenu()
    {
        var menu = new ContextMenu
        {
            Items =
            {
                MenuItem("重命名盒子", BeginRename),
                MenuItem(_box.ListView ? "切换到图标视图" : "切换到列表视图", () =>
                {
                    _box.ListView = !_box.ListView;
                    ApplyViewMode();
                    Changed?.Invoke(this, EventArgs.Empty);
                }),
                MenuItem(_box.Locked ? "解除锁定" : "锁定位置", () =>
                {
                    _box.Locked = !_box.Locked;
                    ApplyLockState();
                    Changed?.Invoke(this, EventArgs.Empty);
                }),
                MenuItem(_box.Collapsed ? "展开" : "收起", ToggleCollapse),
                new Separator(),
                MenuItem("打开盒子文件夹", () => FsUtil.OpenFolder(Folder)),
                MenuItem("解散盒子（内容移回桌面）", Dissolve),
            },
        };

        return menu;
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void MoveToDesktop(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                FsUtil.MoveInto(path, _shell.DesktopPath);
            }
            catch (Exception ex)
            {
                Log.Warn($"移动到桌面失败 {path}: {ex.Message}");
            }
        }

        ReloadItems();
    }

    private void RenameItem(string path)
    {
        var current = Path.GetFileName(path);
        var input = PromptDialog.Show(this, "重命名", "新的名称：", current);
        if (string.IsNullOrWhiteSpace(input) || input == current)
        {
            return;
        }

        try
        {
            var safe = FsUtil.Sanitize(input);
            var directory = Path.GetDirectoryName(path)!;
            var target = Path.Combine(directory, safe + Path.GetExtension(path));

            if (Directory.Exists(path))
            {
                target = Path.Combine(directory, safe);
                Directory.Move(path, target);
            }
            else
            {
                File.Move(path, target);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("重命名失败：" + ex.Message, "简桌", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        ReloadItems();
    }

    #endregion

    #region 标题栏交互

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            BeginRename();
            e.Handled = true;
            return;
        }

        if (_box.Locked)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // 忽略
        }
    }

    private void BeginRename()
    {
        TitleBox.Text = TitleText.Text;
        TitleText.Visibility = Visibility.Collapsed;
        TitleBox.Visibility = Visibility.Visible;
        TitleBox.Focus();
        TitleBox.SelectAll();
    }

    private void TitleBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitRename();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            TitleBox.Visibility = Visibility.Collapsed;
            TitleText.Visibility = Visibility.Visible;
            e.Handled = true;
        }
    }

    private void TitleBox_LostFocus(object sender, RoutedEventArgs e) => CommitRename();

    private void CommitRename()
    {
        if (TitleBox.Visibility != Visibility.Visible)
        {
            return;
        }

        TitleBox.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;

        var title = TitleBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title) || title == _box.Title)
        {
            return;
        }

        var oldFolder = Folder;
        _box.Title = title;

        try
        {
            var parent = Path.GetDirectoryName(oldFolder);
            if (Directory.Exists(oldFolder) && !string.IsNullOrEmpty(parent))
            {
                var target = FsUtil.UniqueFolder(parent, FsUtil.Sanitize(title));
                if (!string.Equals(target, oldFolder, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Move(oldFolder, target);
                    _box.Folder = target;
                    AttachWatcher();
                }
            }
            else
            {
                _box.Folder = Path.Combine(_config.EffectiveBoxRoot, FsUtil.Sanitize(title));
                AttachWatcher();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"重命名盒子目录失败: {ex.Message}");
        }

        TitleText.Text = _box.Title;
        Title = _box.Title;
        ReloadItems();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Collapse_Click(object sender, RoutedEventArgs e) => ToggleCollapse();

    private void ToggleCollapse()
    {
        if (!_box.Collapsed)
        {
            _box.ExpandedHeight = Height - ShadowPad * 2;
            _box.Collapsed = true;
        }
        else
        {
            _box.Collapsed = false;
        }

        _suppressPersist = true;
        Height = (_box.Collapsed ? TitleHeight + 8 : Math.Max(150, _box.ExpandedHeight)) + ShadowPad * 2;
        _suppressPersist = false;

        ApplyCollapseState();
        PersistGeometry();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void View_Click(object sender, RoutedEventArgs e)
    {
        _box.ListView = !_box.ListView;
        ApplyViewMode();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Lock_Click(object sender, RoutedEventArgs e)
    {
        _box.Locked = !_box.Locked;
        ApplyLockState();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        var menu = BuildBoxMenu();
        menu.PlacementTarget = MenuButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    #endregion

    #region 缩放与位置

    private void Resize_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_box.Locked || e.ClickCount != 1)
        {
            return;
        }

        _resizeDirection = (sender as FrameworkElement)?.Tag as string ?? string.Empty;
        _resizeStart = ScreenPoint(e.GetPosition(this));
        _resizeOrigin = new Rect(Left, Top, Width, Height);

        Mouse.Capture((IInputElement)sender);
        MouseMove += Resize_MouseMove;
        MouseLeftButtonUp += Resize_MouseLeftButtonUp;
    }

    private void Resize_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndResize();
            return;
        }

        var current = ScreenPoint(e.GetPosition(this));
        var dx = current.X - _resizeStart.X;
        var dy = current.Y - _resizeStart.Y;

        const double minWidth = 200;
        const double minHeight = 120;

        var left = _resizeOrigin.Left;
        var top = _resizeOrigin.Top;
        var width = _resizeOrigin.Width;
        var height = _resizeOrigin.Height;

        if (_resizeDirection.Contains('L'))
        {
            var newLeft = Math.Min(_resizeOrigin.Left + dx, _resizeOrigin.Right - minWidth);
            width = _resizeOrigin.Right - newLeft;
            left = newLeft;
        }
        else if (_resizeDirection.Contains('R'))
        {
            width = Math.Max(minWidth, _resizeOrigin.Width + dx);
        }

        if (_resizeDirection.Contains('T'))
        {
            var newTop = Math.Min(_resizeOrigin.Top + dy, _resizeOrigin.Bottom - minHeight);
            height = _resizeOrigin.Bottom - newTop;
            top = newTop;
        }
        else if (_resizeDirection.Contains('B'))
        {
            height = Math.Max(minHeight, _resizeOrigin.Height + dy);
        }

        Left = left;
        Top = top;
        Width = width;
        Height = height;
        _suppressPersist = true;
    }

    private void Resize_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndResize();

    private void EndResize()
    {
        MouseMove -= Resize_MouseMove;
        MouseLeftButtonUp -= Resize_MouseLeftButtonUp;
        Mouse.Capture(null);
        _resizeDirection = string.Empty;
        _suppressPersist = false;
        PersistGeometry();
    }

    private Point ScreenPoint(Point windowPoint)
    {
        var device = PointToScreen(windowPoint);
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point(device.X / dpi.DpiScaleX, device.Y / dpi.DpiScaleY);
    }

    private void SchedulePersist()
    {
        if (_suppressPersist)
        {
            return;
        }

        _persistTimer?.Stop();
        _persistTimer?.Start();
    }

    private void PersistGeometry()
    {
        if (_suppressPersist)
        {
            return;
        }

        _box.X = Left + ShadowPad;
        _box.Y = Top + ShadowPad;
        _box.Width = Math.Max(200, Width - ShadowPad * 2);

        if (!_box.Collapsed)
        {
            _box.Height = Math.Max(120, Height - ShadowPad * 2);
            _box.ExpandedHeight = _box.Height;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Box_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_box.Collapsed)
        {
            return;
        }

        var target = Scroller.VerticalOffset - e.Delta / 1.6;
        Scroller.ScrollToVerticalOffset(Math.Max(0, Math.Min(Scroller.ScrollableHeight, target)));
        e.Handled = true;
    }

    #endregion

    #region 解散

    public void Dissolve()
    {
        var count = _items.Count;
        var message = count == 0
            ? "解散这个盒子？"
            : $"解散盒子并把里面的 {count} 项移回桌面？";

        if (System.Windows.MessageBox.Show(message, "简桌", MessageBoxButton.OKCancel, MessageBoxImage.Question)
            != MessageBoxResult.OK)
        {
            return;
        }

        MoveToDesktop(_items.Select(i => i.Path).ToList());

        try
        {
            var folder = Folder;
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder, recursive: false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"删除盒子目录失败: {ex.Message}");
        }

        RemovalRequested?.Invoke(this, EventArgs.Empty);
    }

    #endregion

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _zOrderTimer?.Stop();
        _persistTimer?.Stop();
        _reloadTimer?.Stop();
        _watcher?.Dispose();
    }
}
