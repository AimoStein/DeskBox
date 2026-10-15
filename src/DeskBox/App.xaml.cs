using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using DeskBox.Interop;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Views;

namespace DeskBox;

public partial class App : Application
{
    /// <summary>前台事件后的防抖时间：连续切换只跑最后一次"桌面保持"巡检。</summary>
    private const int DesktopSweepDebounceMs = 160;

    /// <summary>两批 z 序动作之间的最小间隔，避免系统反复提桌面时抖成一串。</summary>
    private const int ZOrderActionIntervalMs = 600;

    /// <summary>同一现场的两条诊断日志各自的最小间隔，避免 UI 线程频繁写文件。</summary>
    private const int DesktopLogIntervalMs = 10000;

    /// <summary>进程启动时间，用来在启动日志里给出"自进程启动以来的毫秒数"。</summary>
    private static readonly DateTime ProcessStartTime = GetProcessStartTime();

    private readonly List<BoxWindow> _boxes = new();

    /// <summary>启动阶段埋点：从 App 构造时开始计时。</summary>
    private readonly Stopwatch _startupWatch = Stopwatch.StartNew();

    private long _startupStageMs;
    private bool _startupComplete;
    private DispatcherTimer? _desktopSweepTimer;
    private DateTime _lastZOrderAction = DateTime.MinValue;
    private DateTime _lastCoveredLog = DateTime.MinValue;
    private DateTime _lastDiagnostics = DateTime.MinValue;

    private AppConfig _config = new();
    private DesktopShell _shell = new();
    private AppInstance? _instance;
    private TrayService? _tray;
    private DesktopMouseWatcher? _mouseWatcher;
    private SettingsWindow? _settings;
    private DispatcherTimer? _saveTimer;
    private NativeMethods.WinEventProc? _foregroundProc;
    private IntPtr _foregroundHook;
    private bool _lastShowHiddenFiles;
    private bool _exiting;

    /// <summary>只有真正持有单实例锁的进程才有权写配置，否则第二次启动会把配置覆盖成默认值。</summary>
    private bool _ownsConfig;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        LogStartupStage("进程入口");

        if (e.Args.Contains("--desktop-icons-probe"))
        {
            if (e.Args.Contains("--refresh"))
            {
                DesktopViewInterop.RefreshForDiagnostics();
                Thread.Sleep(600);
            }
            var names = DesktopViewInterop.ReadItems();
            Log.Info(names is null ? "桌面图标核对失败" : $"桌面当前条目 ({names.Count})：{string.Join("; ", names)}");
            Shutdown();
            return;
        }

        var glassSmoke = e.Args.FirstOrDefault(a => a.StartsWith("--glass-smoketest=", StringComparison.OrdinalIgnoreCase));
        if (glassSmoke is not null)
        {
            Rendering.GlassSmokeTest.Run(glassSmoke["--glass-smoketest=".Length..]);
            return;
        }

        var glassProbe = e.Args.FirstOrDefault(a => a.StartsWith("--glass-probe=", StringComparison.OrdinalIgnoreCase));
        if (glassProbe is not null || e.Args.Contains("--glass-preview"))
        {
            Rendering.GlassPreview.Run(glassProbe?["--glass-probe=".Length..]);
            return;
        }

        if (e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            // 自检的系统图标只写临时注册表，后台自愈不能越过测试作用域碰真实桌面。
            SystemIconGuard.SuppressChecks = true;
            SelfTest.Run();
            Shutdown();
            return;
        }

        _instance = new AppInstance();
        if (!_instance.TryAcquire())
        {
            // 已经有实例在跑：把这次的参数交给它
            AppInstance.SendToPrimary(e.Args);
            Shutdown();
            return;
        }

        _instance.CommandReceived += args => Dispatcher.Invoke(() => HandleArgs(args));
        _instance.StartServer();
        _ownsConfig = true;
        LogStartupStage("单实例锁");

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Warn("未处理异常: " + args.Exception);
            args.Handled = true;
        };

        _config = ConfigStore.Load();
        _lastShowHiddenFiles = _config.ShowHiddenFiles;
        LogStartupStage($"配置读取（盒子 {_config.Boxes.Count} 个）");

        ThemeManager.Apply(_config.Theme);
        LogStartupStage("主题应用");

        _shell = new DesktopShell();
        try
        {
            Directory.CreateDirectory(_config.EffectiveBoxRoot);
        }
        catch (Exception ex)
        {
            Log.Warn("创建盒子根目录失败: " + ex.Message);
        }

        LogStartupStage("桌面外壳准备");

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(700),
        };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer!.Stop();
            ConfigStore.Save(_config);
        };

        var firstRun = _config.FirstRun;
        if (firstRun)
        {
            // 先在内存里置位，等盒子显示出来之后再落盘，别让写配置挡在盒子前面
            _config.FirstRun = false;
        }

        Log.Info($"启动：首次运行={firstRun}，已有盒子={_config.Boxes.Count}，根目录={_config.EffectiveBoxRoot}");

        // 先把格子显示出来；托盘、鼠标钩子、前台监听都排到盒子出现之后，缩短"看不见"的时间
        RestoreBoxes();
        LogStartupStage("盒子显示");

        if (_config.IconsHiddenByApp && _config.HideBoxesWithIcons)
        {
            foreach (var box in _boxes)
            {
                box.SetVisible(false);
            }
        }

        if (firstRun && _boxes.Count == 0)
        {
            CreateNewBox();
        }

        _tray = new TrayService();
        _tray.Command += HandleCommand;
        _tray.MenuOpening += UpdateTrayState;
        _tray.ApplyTheme();
        LogStartupStage("托盘图标");

        _mouseWatcher = new DesktopMouseWatcher { Enabled = _config.HideIconsOnDoubleClick };
        _mouseWatcher.EmptyDesktopDoubleClicked += (_, _) => Dispatcher.BeginInvoke(ToggleIconsFromUser);
        _mouseWatcher.Start();
        LogStartupStage("鼠标钩子");

        // 右键菜单改由设置窗口承担，这里把历史版本写过的注册项清掉
        if (ContextMenuRegistrar.IsRegistered())
        {
            ContextMenuRegistrar.Unregister();
        }

        if (firstRun)
        {
            ConfigStore.Save(_config);
        }

        LogStartupStage("首次运行收尾与右键菜单清理");

        WatchForegroundChanges();
        LogStartupStage("前台监听");

        // 到这里才算启动完成：在那之前不允许任何 z 序巡检，免得开机阶段反复重排窗口
        _startupComplete = true;
        LogStartupStage("启动完成（已允许桌面保持巡检）");

        if (e.Args.Length > 0)
        {
            HandleArgs(e.Args);
        }
        else if (firstRun)
        {
            _tray.ShowInfo(
                "DeskBox 已就绪",
                "托盘图标右键就能一键整理盒子、整理桌面文件、新建盒子；双击桌面空白处可以隐藏所有图标。");
        }
        else
        {
            ShowSettings();
        }

        Log.Info("DeskBox 已启动。");
    }

    /// <summary>启动阶段埋点：统一 `[启动]` 前缀，给出本阶段、累计以及"自进程启动"的毫秒数。</summary>
    private void LogStartupStage(string stage)
    {
        var elapsed = _startupWatch.ElapsedMilliseconds;
        Log.Info($"[启动] {stage}：本阶段 {elapsed - _startupStageMs}ms，累计 {elapsed}ms，自进程启动 {MillisecondsSinceProcessStart()}ms");
        _startupStageMs = elapsed;
    }

    /// <summary>自进程启动以来的毫秒数；取不到进程启动时间时返回 -1。</summary>
    private static long MillisecondsSinceProcessStart()
    {
        return ProcessStartTime == DateTime.MinValue
            ? -1
            : (long)(DateTime.Now - ProcessStartTime).TotalMilliseconds;
    }

    private static DateTime GetProcessStartTime()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.StartTime;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    #region 盒子管理

    /// <summary>
    /// 盯着前台窗口的变化：按 Win+D 时桌面会成为前台窗口，盒子需要立刻把自己抬回桌面之上。
    /// 这里只触发一次防抖巡检；点任务栏之类的前台变化直接跳过，不产生任何 z 序动作。
    /// </summary>
    private void WatchForegroundChanges()
    {
        _foregroundProc = (_, _, _, _, _, _, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            // 点任务栏 / 开始菜单 / 任务栏缩略图不会盖住盒子：完全跳过
            if (NativeMethods.IsShellTrayWindow(NativeMethods.GetForegroundWindow()))
            {
                return;
            }

            foreach (var box in _boxes)
            {
                box.KeepVisibleOnDesktop();
            }
        }));

        _foregroundHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _foregroundProc,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);

        if (_foregroundHook == IntPtr.Zero)
        {
            Log.Warn("注册前台窗口监听失败，Win+D 后的恢复会退回定时器处理。");
        }
    }

    /// <summary>
    /// 各盒子、前台事件都通过这里请求一次"桌面保持"巡检。
    /// 应用还没启动完成时直接忽略：启动阶段不该有任何 z 序动作。
    /// </summary>
    internal static void RequestDesktopSweep()
    {
        (Current as App)?.ScheduleDesktopSweep();
    }

    /// <summary>防抖调度：一串前台变化只跑最后一次巡检。</summary>
    private void ScheduleDesktopSweep()
    {
        if (_exiting || !_startupComplete)
        {
            return;
        }

        if (_desktopSweepTimer is null)
        {
            _desktopSweepTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(DesktopSweepDebounceMs),
            };
            _desktopSweepTimer.Tick += (_, _) =>
            {
                _desktopSweepTimer!.Stop();
                RunDesktopSweep();
            };
        }

        // 重启计时 = 防抖：连续触发只会合并成最后一次
        _desktopSweepTimer.Stop();
        _desktopSweepTimer.Start();
    }

    /// <summary>
    /// 桌面保持巡检：只有"某个盒子确实被桌面窗口盖住"时才处理，而且一次巡检每个盒子至多动一次。
    ///
    /// 处理方式是**把被盖住的盒子插回桌面之上**，不动桌面窗口本身；
    /// 只有插不回去（拿不到 insertAfter 或自校验没过）时才退化为把桌面压回底层。
    /// 改桌面（Progman）的 z 序会触发整屏重绘 —— 用户看到的就是"所有格子闪一下"。
    /// </summary>
    private void RunDesktopSweep()
    {
        if (_exiting || !_startupComplete)
        {
            return;
        }

        // 拖拽中间不抢 z 序：拖拽结束（DoDragDrop 返回）后会再请求一次巡检
        if (BoxWindow.DesktopInteractionActive)
        {
            return;
        }

        SystemIconGuard.Reconcile();

        // 点任务栏 / 开始菜单 / 任务栏缩略图时完全跳过（安全网定时器也可能在这时触发）
        if (NativeMethods.IsShellTrayWindow(NativeMethods.GetForegroundWindow()))
        {
            return;
        }

        var covered = _boxes.Where(box => box.IsCoveredByDesktopNow()).ToList();

        if (covered.Count == 0)
        {
            LogDesktopDiagnostics();
            return;
        }

        // 限流：一批 z 序动作至少间隔 600ms，别每 160ms 就重排一次
        if (DateTime.UtcNow - _lastZOrderAction < TimeSpan.FromMilliseconds(ZOrderActionIntervalMs))
        {
            return;
        }

        _lastZOrderAction = DateTime.UtcNow;

        var desktopRoot = NativeMethods.DesktopRoot();
        var desktopPushed = false;
        var details = new List<string>(covered.Count);

        foreach (var box in covered)
        {
            var points = box.ContentSamplePoints();
            if (points.Count == 0)
            {
                continue;
            }

            // 同一批里"压桌面"最多做一次：压一次桌面所有盒子都会露出来，重复压只会多重排几次
            var action = NativeMethods.PlaceBoxAboveDesktop(
                box.BoxHandle,
                desktopRoot,
                points[0],
                allowDesktopFallback: !desktopPushed);

            if (action is NativeMethods.ZOrderAction.FellBackNoInsertAfter or NativeMethods.ZOrderAction.FellBackVerifyFailed)
            {
                desktopPushed = true;
            }

            details.Add($"「{box.Config.Title}」{DescribeZOrderAction(action)}");
        }

        if (details.Count > 0 &&
            DateTime.UtcNow - _lastCoveredLog >= TimeSpan.FromMilliseconds(DesktopLogIntervalMs))
        {
            _lastCoveredLog = DateTime.UtcNow;
            Log.Info(
                $"桌面挡住 {covered.Count} 个盒子（前台={NativeMethods.ClassNameOf(NativeMethods.GetForegroundWindow())}），" +
                $"z 序处理：{string.Join("；", details)}");
        }

        LogDesktopDiagnostics();
    }

    /// <summary>z 序动作路径的中文说明（写日志用，方便核对 insertAfter 的语义）。</summary>
    private static string DescribeZOrderAction(NativeMethods.ZOrderAction action) => action switch
    {
        NativeMethods.ZOrderAction.AlreadyAbove => "已在桌面之上（没动 z 序）",
        NativeMethods.ZOrderAction.PlacedAboveDesktop => "已插回桌面之上（自校验通过）",
        NativeMethods.ZOrderAction.FellBackNoInsertAfter => "桌面之上没有可插位置 → 已退回压桌面",
        NativeMethods.ZOrderAction.FellBackVerifyFailed => "插回后自校验未通过 → 已退回压桌面",
        NativeMethods.ZOrderAction.FallbackSuppressed => "本批已压过桌面，跳过",
        _ => "无有效句柄，跳过",
    };

    /// <summary>
    /// 只在"某个盒子被桌面层盖住"时记一条现场信息，用来定位 Win+D（显示桌面）到底把盒子怎么了。
    /// 同一现场 10s 内只记一次：这条日志要做全屏扫描 + 写文件，不能跟着每次前台切换跑。
    /// </summary>
    private void LogDesktopDiagnostics()
    {
        if (DateTime.UtcNow - _lastDiagnostics < TimeSpan.FromMilliseconds(DesktopLogIntervalMs))
        {
            return;
        }

        _lastDiagnostics = DateTime.UtcNow;

        try
        {
            var foregroundClass = NativeMethods.ClassNameOf(NativeMethods.GetForegroundWindow());

            // 和巡检用同一套判定（内容区多点采样），免得日志和实际动作对不上
            var covered = _boxes.Where(box => box.IsCoveredByDesktopNow()).ToList();

            if (covered.Count == 0)
            {
                // 盒子都正常（被普通窗口挡住属于正常层叠），不记录，免得刷日志
                return;
            }

            var detail = string.Join("；", covered.Select(box =>
            {
                var handle = box.BoxHandle;
                NativeMethods.GetWindowRect(handle, out var rect);
                var points = box.ContentSamplePoints();
                var centerHit = points.Count == 0
                    ? "(无采样点)"
                    : NativeMethods.ClassNameOf(NativeMethods.WindowFromPoint(points[0]));

                return $"「{box.Config.Title}」visible={NativeMethods.IsWindowVisible(handle)} " +
                       $"iconic={NativeMethods.IsIconic(handle)} " +
                       $"rect={rect.Left},{rect.Top} {rect.Right - rect.Left}x{rect.Bottom - rect.Top} " +
                       $"中心命中={centerHit}";
            }));

            var desktopRoot = NativeMethods.DesktopRoot();

            Log.Info($"桌面诊断：前台={foregroundClass} 桌面在前台={NativeMethods.DesktopIsForeground()} " +
                     $"桌面根={desktopRoot}({NativeMethods.ClassNameOf(desktopRoot)}) " +
                     $"其上邻={NativeMethods.ClassNameOf(NativeMethods.GetWindow(desktopRoot, NativeMethods.GW_HWNDPREV))} " +
                     $"被桌面盖住的盒子={covered.Count} {detail}");
        }
        catch (Exception ex)
        {
            Log.Warn("桌面诊断失败: " + ex.Message);
        }
    }

    private void StopWatchingForeground()
    {
        if (_foregroundHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }

        _foregroundProc = null;
    }

    private void RestoreBoxes()
    {
        foreach (var boxConfig in _config.Boxes.ToList())
        {
            if (string.IsNullOrWhiteSpace(boxConfig.Folder) || !Directory.Exists(boxConfig.Folder))
            {
                Log.Warn($"盒子目录不存在，移除盒子记录：{boxConfig.Title}");
                _config.Boxes.Remove(boxConfig);
                continue;
            }

            CreateBoxWindow(boxConfig);
        }
    }

    private BoxWindow CreateBoxWindow(BoxConfig boxConfig)
    {
        var watch = Stopwatch.StartNew();

        var window = new BoxWindow(_config, boxConfig, _shell, PeerRects)
        {
            ShowActivated = false,
        };

        window.Changed += (_, _) => ScheduleSave();
        window.RemovalRequested += (sender, _) => RemoveBox((BoxWindow)sender!);

        _boxes.Add(window);
        window.Show();
        watch.Stop();

        if (!_startupComplete)
        {
            Log.Info($"[启动] 盒子「{boxConfig.Title}」构造+显示：{watch.ElapsedMilliseconds}ms，自进程启动 {MillisecondsSinceProcessStart()}ms");
        }

        return window;
    }

    /// <summary>其他盒子的实时矩形，供拖动对齐与落位避让使用。</summary>
    private IReadOnlyList<BoxRect> PeerRects(string excludeId)
    {
        var list = new List<BoxRect>(_boxes.Count);

        foreach (var window in _boxes)
        {
            if (window.Config.Id == excludeId || !window.IsVisible)
            {
                continue;
            }

            list.Add(window.ContentBox);
        }

        return list;
    }

    private void RemoveBox(BoxWindow window)
    {
        _boxes.Remove(window);
        _config.Boxes.Remove(window.Config);
        window.Close();
        ConfigStore.Save(_config);
    }

    private void CreateNewBox()
    {
        var title = "新盒子";
        var index = 2;
        while (_config.Boxes.Any(b => string.Equals(b.Title, title, StringComparison.OrdinalIgnoreCase)))
        {
            title = $"新盒子 {index++}";
        }

        var folder = FsUtil.UniqueFolder(_config.EffectiveBoxRoot, FsUtil.Sanitize(title));
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            Log.Warn("创建盒子目录失败: " + ex.Message);
            return;
        }

        var boxConfig = new BoxConfig
        {
            Title = title,
            Folder = folder,
            Width = 330,
            Height = 260,
            ExpandedHeight = 260,
        };

        PlaceNewBox(boxConfig);
        _config.Boxes.Add(boxConfig);
        ConfigStore.Save(_config);

        var window = CreateBoxWindow(boxConfig);
        window.Activate();
    }

    private void PlaceNewBox(BoxConfig boxConfig)
    {
        var area = SystemParameters.WorkArea;
        var index = _boxes.Count;
        var column = index % 3;
        var row = index / 3;

        var x = area.Right - 50 - boxConfig.Width - column * (boxConfig.Width + 18);
        var y = area.Top + 70 + row * 46;

        if (x < area.Left + 20)
        {
            x = area.Left + 20 + column * 26;
        }

        if (y + boxConfig.Height > area.Bottom)
        {
            y = Math.Max(area.Top + 20, area.Bottom - boxConfig.Height - 40);
        }

        if (_config.AvoidBoxOverlap)
        {
            // 新建的盒子不要压在已有盒子上
            var desired = new BoxRect(boxConfig.Id, x, y, boxConfig.Width, boxConfig.Height);
            var spot = BoxLayout.FindFreeSpot(desired, PeerRects(boxConfig.Id), area);
            x = spot.X;
            y = spot.Y;
        }

        boxConfig.X = x;
        boxConfig.Y = y;
    }

    private void EnsureBox(string title, string folder)
    {
        var existing = _boxes.FirstOrDefault(b =>
            string.Equals(b.Config.Title, title, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            if (!existing.IsVisible)
            {
                existing.SetVisible(true);
            }

            return;
        }

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            Log.Warn("创建盒子目录失败: " + ex.Message);
            return;
        }

        var boxConfig = new BoxConfig
        {
            Title = title,
            Folder = folder,
            Width = 330,
            Height = 260,
            ExpandedHeight = 260,
        };

        PlaceNewBox(boxConfig);
        _config.Boxes.Add(boxConfig);
        CreateBoxWindow(boxConfig);
    }

    #endregion

    #region 命令

    private void HandleCommand(string command) => HandleCommand(command, null);

    private void HandleCommand(string command, string? argument)
    {
        switch (command)
        {
            case "arrange":
                ArrangeBoxes();
                break;

            case "organize":
                RunOrganize();
                break;

            case "newbox":
                CreateNewBox();
                break;

            case "undo":
                RunUndo();
                break;

            case "toggleicons":
                ToggleIconsFromUser();
                break;

            case "settings":
                ShowSettings();
                break;

            case "about":
                ShowAbout();
                break;

            case "openroot":
                FsUtil.OpenFolder(_config.EffectiveBoxRoot);
                break;

            case "openconfig":
                FsUtil.OpenFolder(ConfigStore.Directory);
                break;

            case "openlog":
                if (File.Exists(Log.FilePath))
                {
                    FsUtil.Open(Log.FilePath);
                }
                else
                {
                    System.Windows.MessageBox.Show("暂时还没有日志。", "DeskBox", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                break;

            case "exit":
                ExitApp();
                break;

            default:
                Log.Info($"未知命令：{command}");
                break;
        }
    }

    private void HandleArgs(string[] args)
    {
        if (args.Length == 0)
        {
            ShowSettings();
            return;
        }

        var action = string.Empty;
        string? paths = null;
        var isStartup = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg.Equals("--startup", StringComparison.OrdinalIgnoreCase))
            {
                isStartup = true;
            }
            else if (arg.StartsWith("--action=", StringComparison.OrdinalIgnoreCase))
            {
                action = arg["--action=".Length..].Trim('"');
            }
            else if (arg.StartsWith("--paths=", StringComparison.OrdinalIgnoreCase))
            {
                paths = arg["--paths=".Length..].Trim('"');
            }
            else if (arg.Equals("--action", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                action = args[++i];
            }
            else if (arg.Equals("--paths", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                paths = args[++i];
            }
        }

        if (action.Length == 0)
        {
            if (!isStartup)
            {
                ShowSettings();
            }

            return;
        }

        if (action.Equals("addto", StringComparison.OrdinalIgnoreCase))
        {
            AddToBox(paths);
            return;
        }

        if (action.Equals("glassdiagnose", StringComparison.OrdinalIgnoreCase))
        {
            var output = paths ?? Path.Combine(Path.GetTempPath(), "DeskBoxGlassDiagnostic");
            Directory.CreateDirectory(output);
            Rendering.LiquidGlassEngine.Current?.SaveDiagnostics(output);
            foreach (var box in _boxes.Where(b => b.IsVisible))
                Rendering.GlassPreview.SaveVisual(box, Path.Combine(output, $"box-{box.Config.Id}.png"));
            Log.Info($"玻璃诊断已导出：{output}");
            return;
        }

        HandleCommand(action.ToLowerInvariant());
    }

    #endregion

    #region 功能

    /// <summary>
    /// 一键整理盒子：把桌面上的盒子按「先上后左」的顺序排成整齐的行，
    /// 从屏幕左上角开始摆，间距和盒子之间的距离一致；只动位置，不动盒子里的东西。
    /// </summary>
    private void ArrangeBoxes()
    {
        var boxes = _boxes.Where(b => b.IsVisible).ToList();

        if (boxes.Count == 0)
        {
            const string none = "现在还没有盒子，先在托盘菜单里新建一个吧。";
            _settings?.SetArrangeStatus(none);
            _tray?.ShowInfo("DeskBox", none);
            return;
        }

        var area = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        var placed = BoxArranger.Arrange(boxes.Select(b => b.ContentBox).ToList(), area);

        foreach (var spot in placed)
        {
            boxes.FirstOrDefault(b => b.Config.Id == spot.Id)?.MoveContentTo(spot.X, spot.Y);
        }

        ConfigStore.Save(_config);

        var text = $"已把 {placed.Count} 个盒子排整齐。";
        _settings?.SetArrangeStatus(text);
        _tray?.ShowInfo("DeskBox", text);
    }

    private void RunOrganize()
    {
        var root = _config.EffectiveBoxRoot;
        var items = _shell.EnumerateItems(root, _config.ShowHiddenFiles);
        var plan = Organizer.BuildPlan(items, _config.OrganizeShortcuts, out var skipped);

        if (plan.Count == 0)
        {
            const string nothing = "桌面上已经很干净，没有需要整理的文件。";
            _settings?.SetOrganizeStatus(nothing);
            _tray?.ShowInfo("DeskBox", nothing);
            return;
        }

        var groups = plan
            .GroupBy(p => p.Category)
            .OrderByDescending(g => g.Count())
            .Select(g => $"· {g.Key}：{g.Count()} 项");

        var message =
            $"将把桌面上的 {plan.Count} 项整理到：\n{root}\n\n" +
            string.Join("\n", groups) +
            (skipped.Count > 0 ? $"\n\n（{skipped.Count} 个快捷方式 / 程序保持不变）" : string.Empty) +
            "\n\n文件会被移动进对应文件夹，随时可以用「撤销上次整理」还原。";

        if (System.Windows.MessageBox.Show(message, "DeskBox · 一键整理", MessageBoxButton.OKCancel, MessageBoxImage.Question)
            != MessageBoxResult.OK)
        {
            _settings?.SetOrganizeStatus("已取消整理。");
            return;
        }

        var result = Organizer.Execute(plan, root);

        foreach (var category in result.Categories)
        {
            EnsureBox(category, Path.Combine(root, category));
        }

        _config.LastOrganize = result.Moves;
        _config.LastOrganizeTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        ConfigStore.Save(_config);

        foreach (var box in _boxes)
        {
            box.ReloadItems();
        }

        var text = $"已整理 {result.Moves.Count} 项，分成 {result.Categories.Count} 个盒子。";
        if (result.Failed.Count > 0)
        {
            text += $" 有 {result.Failed.Count} 项因为被占用没有移动。";
        }

        _settings?.RefreshState();
        _settings?.SetOrganizeStatus(text);
        _tray?.ShowInfo("DeskBox", text);
    }

    private void RunUndo()
    {
        if (_config.LastOrganize.Count == 0)
        {
            _tray?.ShowInfo("DeskBox", "没有可以撤销的整理记录。");
            return;
        }

        var count = _config.LastOrganize.Count;
        if (System.Windows.MessageBox.Show(
                $"把上次整理的 {count} 项移回原来的位置？",
                "DeskBox · 撤销整理",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        var restored = Organizer.Undo(_config.LastOrganize);
        _config.LastOrganize.Clear();
        _config.LastOrganizeTime = string.Empty;
        ConfigStore.Save(_config);

        foreach (var box in _boxes.ToList())
        {
            box.ReloadItems();
        }

        _settings?.RefreshState();
        _settings?.SetOrganizeStatus($"已把上次整理的 {restored} 项还原回桌面。");
        _tray?.ShowInfo("DeskBox", $"已还原 {restored} 项。");
    }

    private void ToggleIconsFromUser()
    {
        var wantVisible = !_shell.IconsVisible;
        _shell.SetIconsVisible(wantVisible);
        _config.IconsHiddenByApp = !wantVisible;

        if (_config.HideBoxesWithIcons)
        {
            foreach (var box in _boxes)
            {
                box.SetVisible(wantVisible);
            }
        }

        ScheduleSave();
    }

    private void AddToBox(string? rawPaths)
    {
        if (string.IsNullOrWhiteSpace(rawPaths))
        {
            return;
        }

        var paths = rawPaths
            .Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().Trim('"'))
            .Where(p => p.Length > 0)
            .ToArray();

        if (paths.Length == 0)
        {
            return;
        }

        var menu = new System.Windows.Controls.ContextMenu
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
        };

        foreach (var box in _boxes)
        {
            var item = new System.Windows.Controls.MenuItem { Header = box.Config.Title };
            var target = box;
            item.Click += (_, _) => target.MoveIntoBox(paths);
            menu.Items.Add(item);
        }

        if (_boxes.Count == 0)
        {
            var item = new System.Windows.Controls.MenuItem { Header = "新建盒子" };
            item.Click += (_, _) =>
            {
                CreateNewBox();
                _boxes.LastOrDefault()?.MoveIntoBox(paths);
            };
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void ShowSettings()
    {
        if (_settings is null)
        {
            _settings = new SettingsWindow(_config);
            _settings.Applied += (_, _) =>
            {
                ApplyConfig();
                ScheduleSave();
            };
            _settings.CommandRequested += HandleCommand;
            _settings.Closed += (_, _) => _settings = null;
        }

        if (!_settings.IsVisible)
        {
            _settings.Show();
        }

        _settings.RefreshState();
        _settings.BringToFront();
    }

    private static void ShowAbout()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

        System.Windows.MessageBox.Show(
            $"DeskBox v{version}\n\n" +
            "离线桌面整理工具：分区盒子、一键整理、双击隐藏图标、桌面右键菜单。\n\n" +
            "· 不联网、无账号、无同步、无遥测\n" +
            "· 盒子对应磁盘上的真实文件夹，拆除盒子不会删除文件\n" +
            "· 配置位置：exe 旁边的 config.json（不可写时回退到 %AppData%\\DeskBox\\）",
            "关于 DeskBox",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void ExitApp()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _saveTimer?.Stop();

        if (_config.RestoreIconsOnExit && _config.IconsHiddenByApp)
        {
            _shell.SetIconsVisible(true);
            _config.IconsHiddenByApp = false;
        }

        ConfigStore.Save(_config);

        StopWatchingForeground();
        _mouseWatcher?.Dispose();
        _tray?.Dispose();
        _instance?.Dispose();

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Rendering.LiquidGlassEngine.Shutdown();
        if (_ownsConfig && !_exiting)
        {
            ConfigStore.Save(_config);
            StopWatchingForeground();
            _mouseWatcher?.Dispose();
            _tray?.Dispose();
            _instance?.Dispose();
        }

        base.OnExit(e);
    }

    #endregion

    #region 状态同步

    private void UpdateTrayState()
    {
        if (_tray is null)
        {
            return;
        }

        _tray.ItemToggleIcons.Text = _shell.IconsVisible ? "隐藏桌面图标" : "显示桌面图标";
        _tray.ItemUndo.Enabled = _config.LastOrganize.Count > 0;
        _tray.ItemBoxRoot.Text = $"打开盒子文件夹（{_boxes.Count} 个盒子）";
    }

    private void ApplyConfig()
    {
        ThemeManager.Apply(_config.Theme);
        _tray?.ApplyTheme();

        if (_mouseWatcher is not null)
        {
            _mouseWatcher.Enabled = _config.HideIconsOnDoubleClick;
        }

        var hiddenFilesChanged = _config.ShowHiddenFiles != _lastShowHiddenFiles;
        _lastShowHiddenFiles = _config.ShowHiddenFiles;

        foreach (var box in _boxes)
        {
            box.RefreshFromConfig(hiddenFilesChanged);
        }
    }

    private void ScheduleSave()
    {
        _saveTimer?.Stop();
        _saveTimer?.Start();
    }

    #endregion
}
