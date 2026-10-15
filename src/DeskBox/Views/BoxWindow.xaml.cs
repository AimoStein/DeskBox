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
    internal const double ContentHorizontalChrome = 18; // border + symmetric margin + scroll padding
    internal const double ContentVerticalChrome = 46;   // border + title + bottom margin + scroll padding
    private bool _fittingHeight;

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

    // 按下时固定列宽，整段拖动使用相同列宽。
    private double _resizeCellWidth;

    public BoxWindow(AppConfig config, BoxConfig box, DesktopShell shell, Func<string, IReadOnlyList<BoxRect>>? peers = null)
    {
        _config = config;
        _box = box;
        _shell = shell;
        _peers = peers;

        InitializeComponent();

        GlassBackground.Config = config;
        GlassBackground.AvailabilityChanged += available =>
        {
            if (available) Log.Info($"Liquid Glass: 盒子「{_box.Title}」实时玻璃已就绪。");
            Backdrop.Opacity = available ? 1 : _config.BoxOpacity;
            if (available) Frame.BorderBrush = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255));
            else Frame.SetResourceReference(Border.BorderBrushProperty, "BoxBorderBrush");
            ApplyGlassForeground(available);
        };

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
        else
        {
            foreach (var item in _items)
                if (item.ApplyIconSize((int)_config.IconSize)) _ = LoadIconAsync(item);
        }
        FitContentHeight();
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

            // 安全网：只请求 App 统一巡检（一次巡检最多压一次桌面），
            // 不再每个盒子各压一次，免得点任务栏时一串 SetWindowPos 把整屏重排成闪烁。
            App.RequestDesktopSweep();
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
        Deactivated += (_, _) =>
        {
            // 点任务栏 / 开始菜单 / 任务栏缩略图导致的失焦不动 z 序：这类前台变化没有盖住盒子，压底只会闪一下
            if (!NativeMethods.IsShellTrayWindow(NativeMethods.GetForegroundWindow()))
            {
                PushDown();
            }
        };
        Loaded += (_, _) => { FitContentHeight(); UpdateClip(); };

        LocationChanged += (_, _) => SchedulePersist();
        SizeChanged += (_, e) =>
        {
            if (e.WidthChanged) FitContentHeight();
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
        if (!_wantVisible || DesktopInteractionActive || _exiting)
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

    /// <summary>上一次真正压底的时间：盒子已经在底层时再来一次 SetWindowPos 只会引发重排/闪烁。</summary>
    private DateTime _lastPushDown = DateTime.MinValue;
    private bool _exiting;

    /// <summary>正在拖拽的盒子数：拖拽中间不抢 z 序（所有盒子都等拖拽结束后的安全网巡检）。</summary>
    private static int _dragCount;

    /// <summary>当前有没有盒子正在拖拽。</summary>
    internal static bool AnyDragging => _dragCount > 0;

    // 外部 Explorer 拖入也要保护：它不会经过本进程的 DoDragDrop。
    internal static bool DesktopInteractionActive => AnyDragging || NativeMethods.IsLeftMouseButtonDown();

    /// <summary>这个盒子窗口的 HWND（窗口还没创建时是 Zero）。</summary>
    internal IntPtr BoxHandle => new WindowInteropHelper(this).Handle;

    /// <summary>
    /// 盒子内容区里的采样点（物理像素、屏幕坐标）：中心 + 上下左右各 1/4 处。
    /// 用内容区而不是窗口矩形，是为了不把 12px 投影留白算进去。
    /// </summary>
    internal IReadOnlyList<POINT> ContentSamplePoints()
    {
        var handle = BoxHandle;
        if (handle == IntPtr.Zero || !NativeMethods.GetWindowRect(handle, out var rect))
        {
            return Array.Empty<POINT>();
        }

        // GetWindowRect 是物理像素；投影留白 ShadowPad 是 DIP，按窗口 DPI 换算
        var dpi = VisualTreeHelper.GetDpi(this);
        var padX = (int)Math.Round(ShadowPad * dpi.DpiScaleX);
        var padY = (int)Math.Round(ShadowPad * dpi.DpiScaleY);

        var left = rect.Left + padX;
        var top = rect.Top + padY;
        var right = rect.Right - padX;
        var bottom = rect.Bottom - padY;

        if (right - left < 8 || bottom - top < 8)
        {
            // 盒子小到留白都占满时（收起状态），退回整个窗口矩形
            left = rect.Left;
            right = rect.Right;
            top = rect.Top;
            bottom = rect.Bottom;
        }

        var cx = (left + right) / 2;
        var cy = (top + bottom) / 2;
        var dx = Math.Max(2, (right - left) / 4);
        var dy = Math.Max(2, (bottom - top) / 4);

        return new[]
        {
            new POINT(cx, cy),
            new POINT(cx - dx, cy),
            new POINT(cx + dx, cy),
            new POINT(cx, cy - dy),
            new POINT(cx, cy + dy),
        };
    }

    /// <summary>
    /// 这个盒子当前是不是真的被桌面窗口盖住（只读判定，不动 z 序）。
    /// 只看"桌面在前台"是不够的：盒子好好盖在桌面之上时桌面也会在前台，
    /// 那时候去动 z 序只是白白重排一遍分层窗口（看起来就是闪一下）。
    /// 判定要求内容区里的采样点全部命中桌面窗口；拖拽中间一律不算。
    /// </summary>
    internal bool IsCoveredByDesktopNow()
    {
        if (_exiting || !_wantVisible || DesktopInteractionActive)
        {
            return false;
        }

        return NativeMethods.IsCoveredByDesktop(ContentSamplePoints());
    }

    /// <summary>
    /// 请求一次"桌面保持"巡检：把判断和动作都交给 App 统一做（防抖 + 限流，一次巡检每个盒子至多动一次）。
    ///
    /// 按 Win+D（显示桌面）或拖拽时，资源管理器会把桌面窗口提到盒子上面，盒子因此被盖住
    /// （看起来就是"盒子连同图标一起消失了"）。恢复方式是**把被盖住的盒子插回桌面之上**，
    /// 不再去改桌面窗口的 z 序——改桌面会触发整屏重绘（所有格子闪一下）。
    /// 只有插不回去（退化路径）时才会把桌面压回底层。
    /// 单纯"桌面在前台 / 焦点切走"不算被盖住，不产生任何 z 序动作。
    /// </summary>
    internal void KeepVisibleOnDesktop()
    {
        App.RequestDesktopSweep();
    }

    private void UpdateClip()
    {
        var radius = _config.CornerRadius;
        Frame.Clip = new RectangleGeometry(new Rect(0, 0, Frame.ActualWidth, Frame.ActualHeight), radius, radius);
        GlassBackground.Clip = new RectangleGeometry(new Rect(0, 0, GlassBackground.ActualWidth, GlassBackground.ActualHeight), radius, radius);
    }

    private void ApplyVisualSettings()
    {
        GlassBackground.Refresh();
        Backdrop.Opacity = GlassBackground.IsAvailable ? 1 : _config.BoxOpacity;
        ApplyGlassForeground(GlassBackground.IsAvailable);

        var radius = new CornerRadius(_config.CornerRadius);
        Backdrop.CornerRadius = radius;
        Frame.CornerRadius = radius;
        UpdateClip();
    }

    private void ApplyGlassForeground(bool available)
    {
        if (available)
            Resources["TextSecondaryBrush"] = new SolidColorBrush(ThemeManager.IsDark
                ? Color.FromRgb(218, 222, 230) : Color.FromRgb(43, 49, 59));
        else Resources.Remove("TextSecondaryBrush");
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
        FitContentHeight();
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

    /// <summary>
    /// 把盒子压回"桌面之上、普通窗口之下"。
    /// 300ms 内不重复压：窗口本来就在底层时再压一次，只会让分层窗口重排一遍（看起来就是闪一下）。
    /// </summary>
    private void PushDown()
    {
        if (_exiting || !_wantVisible || DesktopInteractionActive)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now - _lastPushDown < TimeSpan.FromMilliseconds(300))
        {
            return;
        }

        _lastPushDown = now;

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
        EndMove();
        if (_resizeDirection.Length > 0) EndResize();
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

        // 复用现有条目和容器：重复的文件监视事件不能先清空整个盒子再重建。
        var existing = _items.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        var wanted = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        for (var i = _items.Count - 1; i >= 0; i--)
            if (!wanted.Contains(_items[i].Path)) _items.RemoveAt(i);
        var size = (int)_config.IconSize;

        // 已经取过的图标先同步铺上（刷新时不会先闪一下占位图），没命中的再交给后台加载器
        for (var index = 0; index < paths.Count; index++)
        {
            var path = paths[index];
            if (existing.TryGetValue(path, out var current))
            {
                var previous = _items.IndexOf(current);
                if (previous != index) _items.Move(previous, index);
                if (current.RefreshFileState(size) || ReferenceEquals(current.Icon, IconCache.Placeholder(size)))
                    _ = LoadIconAsync(current);
                continue;
            }

            var item = new BoxItem(path, size) { IsSelected = selected.Contains(path) };
            var cached = IconCache.TryGetFast(path, size);
            if (cached is not null)
            {
                item.Icon = cached;
            }

            _items.Insert(index, item);
            _ = LoadIconAsync(item);
        }

        EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 自愈：盒子里已经有指向系统项的快捷方式（回收站/控制面板/网络…）时，把桌面那一份也隐藏掉
        SystemIconGuard.EnsureHidden(paths);

        FitContentHeight();
    }

    private static async Task LoadIconAsync(BoxItem item)
    {
        var requestedSize = item.IconSize;
        // Let rapid slider updates settle before asking the shell for another bitmap.
        await Task.Delay(120);
        if (item.IconSize != requestedSize) return;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var icon = await IconCache.GetAsync(item.Path, requestedSize);
                if (item.IconSize != requestedSize) return;
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
        // 除了文件，也接受资源管理器拖过来的系统项（回收站 / 控制面板 / 网络…）
        var ok = ShellDrop.CanAccept(e.Data);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        DropHighlight.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    /// <summary>拖动经过时除了高亮整个盒子，还标出「会落到第几条前面」。</summary>
    private void Box_DragOver(object sender, DragEventArgs e)
    {
        var ok = ShellDrop.CanAccept(e.Data);
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
    /// 回收站 / 控制面板 / 网络这类系统图标没有磁盘路径，会在盒子目录里落成一个指向它的 .lnk。
    /// </summary>
    internal void HandleDrop(IDataObject data, int insertIndex)
    {
        var items = ShellDrop.Read(data);
        if (items.Count == 0)
        {
            Log.Warn("拖动落点：没有拿到文件或系统项，忽略。");
            return;
        }

        var paths = items
            .Where(item => !string.IsNullOrEmpty(item.FileSystemPath))
            .Select(item => item.FileSystemPath!)
            .ToList();

        var shellItems = items.Where(item => string.IsNullOrEmpty(item.FileSystemPath)).ToList();

        Log.Info($"拖动落点：{items.Count} 项（文件 {paths.Count} / 系统项 {shellItems.Count}）→ 「{_box.Title}」第 {insertIndex} 位");

        // 同一个盒子内部拖动：只调整显示顺序，磁盘上的文件不动
        if (shellItems.Count == 0 &&
            data.GetDataPresent(BoxDragFormat) &&
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

        // 系统项没法 Move，只能在盒子目录里建快捷方式；建好的 .lnk 和外部文件走同一条入盒路径
        var createdLinks = new List<string>();
        var hiddenNames = new List<string>();
        foreach (var item in shellItems)
        {
            try
            {
                var link = ShortcutFactory.CreateInFolder(Folder, item.ParsingName, item.DisplayName);
                paths.Add(link);
                createdLinks.Add(link);
                hiddenNames.Add(item.ParsingName);
            }
            catch (Exception ex)
            {
                Log.Warn($"系统项入盒失败 {item.DisplayName}: {ex.Message}");
            }
        }

        if (hiddenNames.Count > 0 && DesktopSystemIcon.SetHidden(hiddenNames, true))
            SystemIconGuard.RememberHandled(createdLinks, hiddenNames);

        if (paths.Count > 0)
        {
            MoveIntoBoxAt(paths, insertIndex);
        }
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
        _dragCount++;
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
            _dragCount--;
            _dragOrigin = null;
        }

        ReloadItems();

        // 拖拽中间不抢 z 序；拖完立刻请求一次巡检，好让被桌面盖住的盒子尽快回来
        App.RequestDesktopSweep();
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
                // 系统项：先把桌面那一份放回来，再把盒子里的 .lnk 删掉
                TryRestoreHiddenDesktopIcon(path);

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
                // 系统项的 .lnk：把桌面图标放回来就够了，这个 .lnk 不能再挪到桌面，否则又是两套
                if (TryRestoreHiddenDesktopIcon(path))
                {
                    if (!NativeMethods.DeleteToRecycleBin(path))
                    {
                        Log.Warn($"系统项快捷方式删除失败 {path}");
                    }

                    continue;
                }

                FsUtil.MoveInto(path, _shell.DesktopPath);
            }
            catch (Exception ex)
            {
                Log.Warn($"移动到桌面失败 {path}: {ex.Message}");
            }
        }

        ReloadItems();
    }

    /// <summary>
    /// 盒子里的 .lnk 如果指向 shell 命名空间项（回收站 / 控制面板 / 网络 / 此电脑），
    /// 说明它对应的桌面图标当初被入盒时隐藏了，这里把它恢复出来；返回是否属于这种情况。
    /// </summary>
    private static bool TryRestoreHiddenDesktopIcon(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!ShortcutFactory.TryGetTargetParsingName(path, out var parsingName) ||
            !DesktopSystemIcon.TryGetClsid(parsingName, out var clsid))
        {
            return false;
        }

        if (!SystemIconGuard.Restore(path, parsingName)) return false;
        Log.Info($"系统图标移出盒子：{Path.GetFileName(path)} → 已恢复桌面图标 {clsid}");
        return true;
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
        Height = (_box.Collapsed ? TitleHeight + 8 : _box.ExpandedHeight) + ShadowPad * 2;
        _suppressPersist = false;

        ApplyCollapseState();
        FitContentHeight();
        PersistGeometry();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void View_Click(object sender, RoutedEventArgs e)
    {
        _box.ListView = !_box.ListView;
        ApplyViewMode();
        FitContentHeight();
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
        _dragCount++;
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
        _dragCount--;
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

        App.RequestDesktopSweep();

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

    /// <summary>Only width is manual; row count determines the expanded height.</summary>
    internal void FitContentHeight()
    {
        if (_fittingHeight || !double.IsFinite(Width)) return;
        var size = (int)_config.IconSize;
        var columns = _box.ListView ? 1 : CellGrid.Count(Width - ShadowPad * 2 - ContentHorizontalChrome, size + 28);
        var rows = CellGrid.CellsFor(_items.Count, columns);
        var rowHeight = _box.ListView ? size + 12 : size + 50;
        var desired = Math.Clamp(ContentVerticalChrome + rows * rowHeight, 120,
            Math.Max(120, DesktopBounds.Height - BoxLayout.SnapGap * 2));
        var changed = Math.Abs(_box.Height - desired) > 0.01 || Math.Abs(_box.ExpandedHeight - desired) > 0.01;
        _box.Height = _box.ExpandedHeight = desired;
        var actualHeight = (_box.Collapsed ? TitleHeight + 8 : desired) + ShadowPad * 2;
        var previous = _suppressPersist;
        _fittingHeight = true; _suppressPersist = true;
        try
        {
            Height = actualHeight;
            if (IsLoaded && _resizeDirection.Length == 0)
            {
                var bounded = BoxLayout.ClampToArea(ContentBox, DesktopBounds);
                Left = bounded.X - ShadowPad; Top = bounded.Y - ShadowPad;
                _box.X = bounded.X; _box.Y = bounded.Y;
            }
        }
        finally { _suppressPersist = previous; _fittingHeight = false; }
        if (changed && !previous) Changed?.Invoke(this, EventArgs.Empty);
    }

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
        if (_box.Locked || e.ClickCount != 1 || _resizeDirection.Length > 0)
        {
            return;
        }

        _resizeDirection = (sender as FrameworkElement)?.Tag as string ?? string.Empty;
        if (_resizeDirection.Length == 0) return;
        _dragCount++;
        _resizeStart = ScreenPoint(e.GetPosition(this));
        _resizeOrigin = ScreenBounds;
        MeasureResizeGrid();

        Mouse.Capture((IInputElement)sender);
        MouseMove += Resize_MouseMove;
        MouseLeftButtonUp += Resize_MouseLeftButtonUp;
        LostMouseCapture += Resize_LostMouseCapture;
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
        const double minWidth = 200 + ShadowPad * 2;

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

        if (!_box.ListView && _resizeCellWidth > 1)
        {
            var chrome = ShadowPad * 2 + ContentHorizontalChrome;
            var minimumColumns = (int)Math.Ceiling((minWidth - chrome) / _resizeCellWidth);
            var columns = Math.Max(minimumColumns, (int)Math.Round((width - chrome) / _resizeCellWidth));
            width = chrome + columns * _resizeCellWidth;
            if (_resizeDirection.Contains('L')) left = _resizeOrigin.Right - width;
        }

        _suppressPersist = true;
        SetScreenBounds(left, top, width, height);
        FitContentHeight();
    }

    /// <summary>
    /// 固定当前图标列宽，空盒子也按同样的列宽缩放。
    /// </summary>
    private void MeasureResizeGrid()
    {
        _resizeCellWidth = (int)_config.IconSize + 28;
    }

    private void Resize_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndResize();

    private void Resize_LostMouseCapture(object sender, MouseEventArgs e) => EndResize();

    private void EndResize()
    {
        if (_resizeDirection.Length == 0) return;
        _dragCount--;
        MouseMove -= Resize_MouseMove;
        MouseLeftButtonUp -= Resize_MouseLeftButtonUp;
        LostMouseCapture -= Resize_LostMouseCapture;
        _resizeDirection = string.Empty;
        Mouse.Capture(null);

        // 拉大盒子时右 / 下边缘可能出屏，收尾把整个盒子挪回屏幕内
        var current = ContentBox;
        var bounded = BoxLayout.ClampToArea(current, DesktopBounds);

        if (Math.Abs(bounded.X - current.X) > 0.01 || Math.Abs(bounded.Y - current.Y) > 0.01)
        {
            SetScreenBounds(bounded.X - ShadowPad, bounded.Y - ShadowPad, Width, Height);
        }

        _suppressPersist = false;
        PersistGeometry();
        App.RequestDesktopSweep();
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
        GlassBackground.Dispose();
        base.OnClosed(e);
        StopShowing();
    }
}
