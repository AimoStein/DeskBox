using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DeskBox.Interop;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Views;

public partial class BoxWindow : Window
{
    public const double ShadowPad = 12;
    private const double TitleHeight = 34;

    /// <summary>盒内拖动时捎带的来源标记，用来区分「盒内排序」和「跨盒子移动」。</summary>
    internal const string BoxDragFormat = "DeskBox.BoxItems";

    private readonly AppConfig _config;
    private readonly BoxConfig _box;
    private readonly DesktopShell _shell;
    private readonly Func<string, IReadOnlyList<BoxRect>>? _peers;
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

    private AlignGuideWindow? _guides;
    private bool _moving;
    private Point _moveStart;
    private Rect _moveOrigin;
    private bool _wantVisible = true;

    // 缩放顿挫：按下时量一次格子 / 留白，整段拖动都用这一份，避免越拖越漂
    private double _resizeCellWidth;
    private double _resizeCellHeight;
    private double _resizeChromeWidth;
    private double _resizeChromeHeight;
    private int _resizeColumns;
    private int _resizeRows;

    public BoxWindow(AppConfig config, BoxConfig box, DesktopShell shell, Func<string, IReadOnlyList<BoxRect>>? peers = null)
    {
        _config = config;
        _box = box;
        _shell = shell;
        _peers = peers;

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

    /// <summary>盒子内容区在桌面坐标系里的矩形（不含投影留白）。</summary>
    public Rect ContentRect => new(
        Left + ShadowPad,
        Top + ShadowPad,
        Math.Max(1, Width - ShadowPad * 2),
        Math.Max(1, Height - ShadowPad * 2));

    /// <summary>窗口在屏幕上的矩形（DIP）。</summary>
    public Rect ScreenBounds => new(Left, Top, Width, Height);

    public BoxRect ContentBox =>
        new(_box.Id, ContentRect.X, ContentRect.Y, ContentRect.Width, ContentRect.Height);

    /// <summary>盒子能待在的区域：整个虚拟桌面（多显示器时含所有屏幕），单位 DIP。</summary>
    private static Rect DesktopBounds => new(
        SystemParameters.VirtualScreenLeft,
        SystemParameters.VirtualScreenTop,
        SystemParameters.VirtualScreenWidth,
        SystemParameters.VirtualScreenHeight);

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
        _wantVisible = visible;

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

    /// <summary>把盒子挪到指定的桌面坐标（一键整理盒子时用），同样不会跑出屏幕。</summary>
    internal void MoveContentTo(double x, double y)
    {
        var bounded = BoxLayout.ClampToArea(
            new BoxRect(_box.Id, x, y, ContentRect.Width, ContentRect.Height),
            DesktopBounds);

        SetScreenBounds(bounded.X - ShadowPad, bounded.Y - ShadowPad, Width, Height);
        PersistGeometry();
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
            RestoreIfShellHidTheBox();
            KeepVisibleOnDesktop();
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
        SourceInitialized += (_, _) =>
        {
            PushDown();

            // 拦掉"最小化"：Win+D（显示桌面）会让系统最小化所有窗口，盒子不该跟着消失
            if (PresentationSource.FromVisual(this) is HwndSource source)
            {
                source.AddHook(WndProc);
            }
        };
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
        DragOver += Box_DragOver;
        DragLeave += (_, _) => ClearDropFeedback();
        Drop += Box_Drop;

        PreviewMouseWheel += Box_PreviewMouseWheel;

        ItemsHost.PreviewMouseLeftButtonDown += Items_PreviewMouseLeftButtonDown;
        ItemsHost.PreviewMouseMove += Items_PreviewMouseMove;
        ItemsHost.PreviewMouseRightButtonDown += Items_PreviewMouseRightButtonDown;
        ItemsHost.MouseDoubleClick += Items_MouseDoubleClick;
    }

    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MINIMIZE = 0xF020;
    private const int WM_SIZE = 0x0005;
    private const int SIZE_MINIMIZED = 1;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == SC_MINIMIZE)
        {
            // Win+D 走 SC_MINIMIZE 时直接拒掉
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WM_SIZE && wParam.ToInt64() == SIZE_MINIMIZED)
        {
            // 已经被最小化了（比如显示桌面的其他路径）：立刻还原，不要等定时器
            Dispatcher.BeginInvoke(
                new Action(() => RestoreIfShellHidTheBox()),
                DispatcherPriority.Background);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 被系统藏起来（Win+D / 显示桌面）就自己回来。
    /// 用户主动隐藏（双击桌面空白处隐藏图标）时不还原。
    /// </summary>
    internal void RestoreIfShellHidTheBox()
    {
        if (!_wantVisible || _dragging || _exiting)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        var minimized = WindowState != WindowState.Normal;
        var hidden = handle != IntPtr.Zero && !NativeMethods.IsWindowVisible(handle);

        if (!minimized && !hidden)
        {
            _restoreLogged = false; // 一切正常，下次被藏起来还能再记一条日志
            return;
        }

        if (minimized)
        {
            WindowState = WindowState.Normal;
        }

        if (hidden)
        {
            NativeMethods.ShowWindow(handle, NativeMethods.SW_SHOWNOACTIVATE);
            NativeMethods.PushToBottom(handle);
        }

        if (!_restoreLogged)
        {
            _restoreLogged = true;
            Log.Info($"盒子「{_box.Title}」被系统{(minimized ? "最小化" : "隐藏")}，已自动还原。");
        }
    }

    private bool _restoreLogged;
    private bool _raisedLogged;
    private DateTime _lastDesktopPush = DateTime.MinValue;
    private bool _exiting;

    /// <summary>
    /// 按 Win+D（显示桌面）时，系统会把桌面窗口提到盒子上面，盒子因此被盖住
    /// （看起来就是"盒子连同图标一起消失了"）。盒子已经贴着 z 序底部、受 z 序带次限制，
    /// 抬高自己没有用（实测抬不动），所以这里反过来把桌面窗口压回最底层；
    /// 桌面退出前台、没有压住盒子时，再把盒子压回"桌面之上、普通窗口之下"。
    /// </summary>
    internal void KeepVisibleOnDesktop()
    {
        if (_exiting || !_wantVisible || _dragging)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        if (!NativeMethods.DesktopIsForeground() && !NativeMethods.IsCoveredByDesktop(handle))
        {
            _raisedLogged = false;

            if (!IsActive && IsVisible)
            {
                PushDown();
            }

            return;
        }

        NativeMethods.PushDesktopToBottom();

        // 桌面可能被系统反复提起，这里只在"隔了一阵又发生"时记一条，免得刷日志
        if (!_raisedLogged || DateTime.UtcNow - _lastDesktopPush > TimeSpan.FromSeconds(30))
        {
            _raisedLogged = true;
            _lastDesktopPush = DateTime.UtcNow;

            NativeMethods.GetWindowRect(handle, out var rect);
            var center = new POINT(rect.Left + (rect.Right - rect.Left) / 2, rect.Top + (rect.Bottom - rect.Top) / 2);

            Log.Info(
                $"桌面挡住盒子「{_box.Title}」（桌面在前台={NativeMethods.DesktopIsForeground()}，" +
                $"中心命中={NativeMethods.ClassNameOf(NativeMethods.WindowFromPoint(center))}），已把桌面压回最底层。");
        }
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

        // 配置里的坐标可能来自别的显示器布局（换屏 / 分辨率变化），启动时先拉回屏幕内
        var bounded = BoxLayout.ClampToArea(
            new BoxRect(_box.Id, _box.X, _box.Y, Width - ShadowPad * 2, Height - ShadowPad * 2),
            DesktopBounds);

        Left = bounded.X - ShadowPad;
        Top = bounded.Y - ShadowPad;

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

    /// <summary>窗口关闭时停掉所有还会碰这个窗口的定时器和监听。</summary>
    private void StopShowing()
    {
        _exiting = true;
        _zOrderTimer?.Stop();
        _persistTimer?.Stop();
        _reloadTimer?.Stop();
        _watcher?.Dispose();
        _watcher = null;

        try
        {
            _guides?.Close();
        }
        catch (Exception ex)
        {
            Log.Warn("关闭参考线失败: " + ex.Message);
        }
    }

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

        // 用户手动拖出来的顺序优先，没记录过的（新放进来的）按上面的默认规则排在后面
        paths = ItemOrder.Sort(paths, _box.ItemOrder, Path.GetFileName);

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

    /// <summary>拖动经过时除了高亮整个盒子，还标出「会落到第几条前面」。</summary>
    private void Box_DragOver(object sender, DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        DropHighlight.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        ShowInsertMark(ok ? InsertIndexFor(e.GetPosition(ItemsHost)) : -1);
        e.Handled = true;
    }

    private void Box_Drop(object sender, DragEventArgs e)
    {
        var insertIndex = InsertIndexFor(e.GetPosition(ItemsHost));
        ClearDropFeedback();

        if (e.Data is { } data)
        {
            HandleDrop(data, insertIndex);
        }

        e.Handled = true;
    }

    /// <summary>当前显示顺序（自检用）。</summary>
    internal IReadOnlyList<string> ItemNames =>
        _items.Select(item => Path.GetFileName(item.Path)).ToList();

    /// <summary>
    /// 落到这个盒子上：盒内拖动 = 调整顺序（不碰磁盘），跨盒子 / 从外面拖 = 搬进来并放到落点位置。
    /// </summary>
    internal void HandleDrop(IDataObject data, int insertIndex)
    {
        if (data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            Log.Warn("拖动落点：没有拿到文件列表，忽略。");
            return;
        }

        Log.Info($"拖动落点：{paths.Length} 项 → 「{_box.Title}」第 {insertIndex} 位");

        // 同一个盒子内部拖动：只调整显示顺序，磁盘上的文件不动
        if (data.GetDataPresent(BoxDragFormat) &&
            data.GetData(BoxDragFormat) is string sourceFolder &&
            IsSameFolder(sourceFolder, Folder))
        {
            var moving = _items
                .Where(item => paths.Contains(item.Path, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (moving.Count > 0)
            {
                ApplyOrder(moving, insertIndex);
            }

            return;
        }

        MoveIntoBoxAt(paths, insertIndex);
    }

    private void ClearDropFeedback()
    {
        DropHighlight.Visibility = Visibility.Collapsed;
        ShowInsertMark(-1);
    }

    /// <summary>把插入标记画在第 index 条前面；index 为 -1 时全部清掉。</summary>
    private void ShowInsertMark(int index)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            _items[i].InsertMark = i == index;
        }
    }

    /// <summary>算出光标落在第几个位置（0 ~ 条目数）：图标视图按行优先，列表视图按上下。</summary>
    private int InsertIndexFor(Point point)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container ||
                container.RenderSize.Width <= 0 ||
                container.RenderSize.Height <= 0)
            {
                continue;
            }

            var origin = container.TransformToAncestor(ItemsHost).Transform(new Point(0, 0));

            // 光标还在这一条所在行的上面 → 就插在它前面
            if (point.Y < origin.Y)
            {
                return i;
            }

            // 同一行：落在这一条的左半边就插在它前面，右半边继续看下一列
            if (point.Y <= origin.Y + container.RenderSize.Height)
            {
                if (point.X < origin.X + container.RenderSize.Width / 2)
                {
                    return i;
                }

                continue;
            }
        }

        return _items.Count;
    }

    /// <summary>按拖拽落点重排条目，并把顺序写回配置。</summary>
    private void ApplyOrder(IReadOnlyList<BoxItem> moving, int insertIndex)
    {
        var names = _items.Select(item => Path.GetFileName(item.Path)).ToList();
        var movingNames = moving.Select(item => Path.GetFileName(item.Path)).ToList();
        var ordered = ItemOrder.Apply(names, movingNames, insertIndex);

        var byName = _items
            .GroupBy(item => Path.GetFileName(item.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var rebuilt = ordered.Where(byName.ContainsKey).Select(name => byName[name]).ToList();

        if (rebuilt.Count != _items.Count)
        {
            return;
        }

        for (var i = 0; i < rebuilt.Count; i++)
        {
            if (ReferenceEquals(_items[i], rebuilt[i]))
            {
                continue;
            }

            // 顺序真的变了才重建一遍，避免白刷控件
            _items.Clear();
            foreach (var item in rebuilt)
            {
                _items.Add(item);
            }

            PersistOrder();
            return;
        }
    }

    private void PersistOrder()
    {
        _box.ItemOrder = _items.Select(item => Path.GetFileName(item.Path)).ToList();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsSameFolder(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把条目搬进这个盒子，并放到第 index 个位置。</summary>
    private void MoveIntoBoxAt(IEnumerable<string> paths, int insertIndex)
    {
        var pathList = paths.ToList();
        var before = new HashSet<string>(_items.Select(item => item.Path), StringComparer.OrdinalIgnoreCase);

        MoveIntoBox(pathList);

        var added = _items.Where(item => !before.Contains(item.Path)).ToList();
        if (added.Count > 0)
        {
            ApplyOrder(added, insertIndex);
        }
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
                "DeskBox",
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

        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, paths);
        data.SetData(BoxDragFormat, Folder);
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
                BeginMove(ItemsHost, e);
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
                    "DeskBox",
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
            System.Windows.MessageBox.Show("重命名失败：" + ex.Message, "DeskBox", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        BeginMove((IInputElement)sender, e);
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

    #region 移动与对齐

    /// <summary>
    /// 自己实现拖动（而不是 DragMove），因为要在拖动过程中吸附对齐其他盒子。
    /// </summary>
    private void BeginMove(IInputElement capture, MouseButtonEventArgs e) =>
        BeginMove(capture, ScreenPoint(e.GetPosition(this)));

    /// <summary>开始拖动。capture 为 null 表示由自检直接驱动，不接管鼠标。</summary>
    internal void BeginMove(IInputElement? capture, Point start)
    {
        if (_moving || _box.Locked)
        {
            return;
        }

        _moving = true;
        _moveStart = start;
        _moveOrigin = ScreenBounds;

        if (capture is null)
        {
            return;
        }

        Mouse.Capture(capture);
        MouseMove += Box_MoveMove;
        MouseLeftButtonUp += Box_MoveUp;
        LostMouseCapture += Box_LostMouseCapture;
    }

    private void Box_MoveMove(object sender, MouseEventArgs e)
    {
        if (!_moving)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndMove();
            return;
        }

        MoveTo(ScreenPoint(e.GetPosition(this)));
    }

    /// <summary>把盒子拖到屏幕坐标（吸附对齐在这里生效）。</summary>
    internal void MoveTo(Point screenPoint)
    {
        if (!_moving)
        {
            return;
        }

        var left = _moveOrigin.Left + (screenPoint.X - _moveStart.X);
        var top = _moveOrigin.Top + (screenPoint.Y - _moveStart.Y);

        var desired = new BoxRect(_box.Id, left + ShadowPad, top + ShadowPad, ContentRect.Width, ContentRect.Height);
        BoxRect placed;

        if (_config.AutoAlignBoxes && _peers is not null)
        {
            var snap = BoxLayout.Snap(desired, _peers(_box.Id));
            var snapped = desired.WithPosition(snap.X, snap.Y);

            // 屏幕边缘优先：被边界挡住时盒子会留在边缘内侧，参考线也就不再成立
            placed = BoxLayout.ClampToArea(snapped, DesktopBounds);

            if (Math.Abs(placed.X - snapped.X) > 0.01 || Math.Abs(placed.Y - snapped.Y) > 0.01)
            {
                HideGuides();
            }
            else
            {
                ShowGuides(snap);
            }
        }
        else
        {
            placed = BoxLayout.ClampToArea(desired, DesktopBounds);
        }

        left += placed.X - desired.X;
        top += placed.Y - desired.Y;

        _suppressPersist = true;
        SetScreenBounds(left, top, Width, Height);
    }

    private void Box_MoveUp(object sender, MouseButtonEventArgs e) => EndMove();

    private void Box_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_moving)
        {
            EndMove();
        }
    }

    /// <summary>结束拖动：按需避让重叠，然后保存位置。</summary>
    internal void EndMove()
    {
        if (!_moving)
        {
            return;
        }

        _moving = false;
        MouseMove -= Box_MoveMove;
        MouseLeftButtonUp -= Box_MoveUp;
        LostMouseCapture -= Box_LostMouseCapture;
        Mouse.Capture(null);
        HideGuides();

        if (_config.AvoidBoxOverlap && _peers is not null)
        {
            var desired = ContentBox;
            var others = _peers(_box.Id);

            if (BoxLayout.AnyTooClose(desired, others))
            {
                // 只在盒子当前所在的范围内找空位，避免为了避让把它甩到别的显示器
                var bounds = Rect.Union(SystemParameters.WorkArea, ScreenBounds);
                var spot = BoxLayout.FindFreeSpot(desired, others, bounds);

                SetScreenBounds(spot.X - ShadowPad, spot.Y - ShadowPad, Width, Height);
            }
        }

        // 收尾兜底：避让之后也不该把盒子留在屏幕外
        var current = ContentBox;
        var bounded = BoxLayout.ClampToArea(current, DesktopBounds);

        if (Math.Abs(bounded.X - current.X) > 0.01 || Math.Abs(bounded.Y - current.Y) > 0.01)
        {
            SetScreenBounds(bounded.X - ShadowPad, bounded.Y - ShadowPad, Width, Height);
        }

        // 只是点了一下、位置没变就不用写配置
        var moved = Math.Abs(Left - _moveOrigin.Left) > 0.01 || Math.Abs(Top - _moveOrigin.Top) > 0.01;

        _suppressPersist = false;

        if (moved)
        {
            PersistGeometry();
        }

    }

    private void ShowGuides(SnapResult snap)
    {
        if (!snap.Snapped)
        {
            HideGuides();
            return;
        }

        _guides ??= new AlignGuideWindow();

        var guides = new List<AlignGuide>(2);

        if (snap.Vertical is { } vertical)
        {
            guides.Add(vertical);
        }

        if (snap.Horizontal is { } horizontal)
        {
            guides.Add(horizontal);
        }

        _guides.ShowGuides(guides);
    }

    private void HideGuides() => _guides?.HideGuides();

    #endregion

    #region 缩放与位置

    /// <summary>按屏幕坐标摆放窗口（盒子是顶层窗口，屏幕坐标就是窗口坐标）。</summary>
    private void SetScreenBounds(double x, double y, double width, double height)
    {
        var previous = _suppressPersist;
        _suppressPersist = true;

        Width = width;
        Height = height;
        Left = x;
        Top = y;

        _suppressPersist = previous;
    }

    private void Resize_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_box.Locked || e.ClickCount != 1)
        {
            return;
        }

        _resizeDirection = (sender as FrameworkElement)?.Tag as string ?? string.Empty;
        _resizeStart = ScreenPoint(e.GetPosition(this));
        _resizeOrigin = ScreenBounds;
        MeasureResizeGrid();

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

        // 按图标格子顿挫：宽度整列跳，高度整行跳；加宽时高度自动收成刚好装下的行数
        if (!_box.Collapsed && _resizeCellWidth > 1 && _resizeCellHeight > 1)
        {
            var snapped = CellGrid.SnapResize(
                _resizeOrigin, left, top, width, height, _resizeDirection,
                _resizeCellWidth, _resizeCellHeight, _resizeChromeWidth, _resizeChromeHeight,
                _resizeColumns, _resizeRows, Math.Max(1, _items.Count),
                snapColumns: !_box.ListView);

            left = snapped.X;
            top = snapped.Y;
            width = snapped.Width;
            height = snapped.Height;
        }

        _suppressPersist = true;
        SetScreenBounds(left, top, width, height);
    }

    /// <summary>
    /// 按下缩放条时量一次：一个条目占的格子、以及标题栏 / 边框那些不属于格子的留白。
    /// 之后整段拖动都用这一份，否则每动一下都用新尺寸重算，框会越拖越漂。
    /// </summary>
    private void MeasureResizeGrid()
    {
        _resizeCellWidth = 0;
        _resizeCellHeight = 0;
        _resizeChromeWidth = 0;
        _resizeChromeHeight = 0;
        _resizeColumns = 0;
        _resizeRows = 0;

        if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(0) is not FrameworkElement container ||
            container.ActualWidth <= 4 ||
            container.ActualHeight <= 4 ||
            _items.Count == 0)
        {
            return;
        }

        _resizeCellWidth = container.ActualWidth;
        _resizeCellHeight = container.ActualHeight;
        _resizeColumns = CellGrid.Count(ItemsHost.ActualWidth, _resizeCellWidth);
        _resizeRows = CellGrid.CellsFor(_items.Count, _resizeColumns);
        _resizeChromeWidth = _resizeOrigin.Width - _resizeColumns * _resizeCellWidth;
        _resizeChromeHeight = Scroller.ActualHeight > 8
            ? _resizeOrigin.Height - (Scroller.ActualHeight - 4)
            : _resizeOrigin.Height - _resizeRows * _resizeCellHeight;
    }

    private void Resize_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndResize();

    private void EndResize()
    {
        MouseMove -= Resize_MouseMove;
        MouseLeftButtonUp -= Resize_MouseLeftButtonUp;
        Mouse.Capture(null);
        _resizeDirection = string.Empty;

        // 拉大盒子时右 / 下边缘可能出屏，收尾把整个盒子挪回屏幕内
        var current = ContentBox;
        var bounded = BoxLayout.ClampToArea(current, DesktopBounds);

        if (Math.Abs(bounded.X - current.X) > 0.01 || Math.Abs(bounded.Y - current.Y) > 0.01)
        {
            SetScreenBounds(bounded.X - ShadowPad, bounded.Y - ShadowPad, Width, Height);
        }

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
        // 直接解散：内容是移回桌面，不删东西，所以不再弹确认框
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
        StopShowing();
    }
}
