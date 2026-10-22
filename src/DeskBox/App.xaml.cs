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

    private readonly List<BoxWindow> _boxes = new();

    private bool _startupComplete;
    private DispatcherTimer? _desktopSweepTimer;
    private DispatcherTimer? _startupSweepTimer;
    private DispatcherTimer? _boxRecoveryTimer;
    private DateTime _lastZOrderAction = DateTime.MinValue;
    private DateTime _lastCoveredLog = DateTime.MinValue;
    private DateTime _lastDiagnostics = DateTime.MinValue;
    private DateTime _lastNormalizeLog = DateTime.MinValue;

    /// <summary>当前是不是「显示桌面（Win+D / 任务栏最右按钮）」状态：桌面窗口在前台。</summary>
    private bool _desktopShown;

    /// <summary>显示桌面结束后的层叠自校验定时器：确认盒子真的贴在桌面之上，必要时再压一次。</summary>
    private DispatcherTimer? _desktopLayerWatchTimer;
    private int _desktopLayerWatchTicks;

    /// <summary>本轮自校验里补压的次数（最多 2 次，避免窗口链上有别的窗口时反复重排）。</summary>
    private int _desktopLayerCompensations;

    /// <summary>上一次「飘在普通窗口层」自愈压底的时间，节流用。</summary>
    private DateTime _lastFloatingFix = DateTime.MinValue;

    /// <summary>被系统销毁的盒子窗口重建尝试次数。</summary>
    private int _boxRecoveryAttempts;

    /// <summary>重建盒子窗口最多尝试几次（Explorer 重启一般 1~2 秒内就绪）。</summary>
    private const int BoxRecoveryMaxAttempts = 10;

    /// <summary>桌面窗口显示日志的限流时间，避免壁纸类 WorkerW 反复显示时刷屏。</summary>
    private DateTime _lastDesktopShowLog = DateTime.MinValue;
    private int _startupSweepsLeft;

    private AppConfig _config = new();
    private DesktopShell _shell = new();
    private AppInstance? _instance;
    private TrayService? _tray;
    private DesktopMouseWatcher? _mouseWatcher;
    private DesktopTaskbar? _taskbar;
    private SettingsWindow? _settings;
    private DispatcherTimer? _saveTimer;
    private NativeMethods.WinEventProc? _foregroundProc;
    private IntPtr _foregroundHook;
    private NativeMethods.WinEventProc? _desktopShowProc;
    private IntPtr _desktopShowHook;
    private bool _lastShowHiddenFiles;
    private bool _exiting;

    /// <summary>只有真正持有单实例锁的进程才有权写配置，否则第二次启动会把配置覆盖成默认值。</summary>
    private bool _ownsConfig;

    public App()
    {
        StartupTrace.Mark("App 构造（WPF 入口）");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        LogStartupStage("进程入口");
        LogBootContext();


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

        // 开机时十几个自启动程序一起抢 CPU/磁盘：启动阶段临时提一档优先级，盒子出来就还原。
        RaiseStartupPriority();

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Warn("未处理异常: " + args.Exception);
            args.Handled = true;
        };

        _config = ConfigStore.Load();
        _lastShowHiddenFiles = _config.ShowHiddenFiles;
        LogStartupStage($"配置读取（盒子 {_config.Boxes.Count} 个）");

        // 盒子还没建，先把各盒子目录里的图标从磁盘缓存解码进内存：
        // 这样等窗口真正显示时，格内图标已经就绪，不会先闪一排占位图再换。
        PrewarmBoxIcons();

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

        // 监听要最先挂上：任务计划程序登录任务可能比 Explorer 还早启动，
        // 桌面窗口随后出现时会盖住盒子，越早收到"桌面显示"事件越能第一时间插回去。
        WatchForegroundChanges();
        WatchDesktopWindowAppearance();
        LogStartupStage("前台/桌面监听");

        // 先把格子显示出来；托盘、鼠标钩子、开机自启同步都排到首帧之后，缩短"看不见"的时间
        RestoreBoxes();
        LogStartupStage("盒子显示");

        if (_config.IconsHiddenByApp && _config.HideBoxesWithIcons)
        {
            foreach (var box in _boxes)
            {
                box.SetVisible(false);
            }
        }

        // 到这里才算启动完成：在那之前不允许任何 z 序巡检，免得开机阶段反复重排窗口
        _startupComplete = true;
        RestoreStartupPriority();
        LogStartupStage("启动完成（已允许桌面保持巡检）");
        Log.Info($"盒子窗口归属桌面：{_boxes.Count(box => box.IsOwnedByDesktop)}/{_boxes.Count}（归属后 Win+D 不会盖住盒子）");
        LogStartupDesktopState();

        // 开机时 Explorer 可能比我们晚创建桌面窗口：启动完成后立刻巡检一次，
        // 再在头几秒里用短周期巡检兜住"桌面刚出现又盖住盒子"的窗口期，
        // 不必等每个盒子 1.5s 的常规定时器。
        App.RequestDesktopSweep();
        BeginStartupSweeps();

        Log.Info("DeskBox 已启动。");
        Log.Info($"[启动] 启动阶段同步写日志合计 {StartupTrace.LogWriteMs}ms");

        // 首帧、首批图标、开机时桌面是否后出现，都要等 Dispatcher 转起来才看得到；
        // 继续观测一小段时间，到点自动关掉启动日志。
        StartupTrace.EndStartupAfter(TimeSpan.FromMilliseconds(2500));

        // 托盘、鼠标钩子、右键菜单清理、自启同步都不影响"盒子出现"，排到首帧渲染之后的空闲优先级，
        // 别和第一个窗口抢 UI 线程。
        var startupFirstRun = firstRun;
        var startupArgs = e.Args;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try
            {
                FinishStartupWork(startupFirstRun, startupArgs);
            }
            catch (Exception ex)
            {
                Log.Warn("启动收尾失败: " + ex);
            }
        }));
    }

    /// <summary>
    /// 首帧之后的启动收尾：托盘、鼠标钩子、历史右键菜单清理、开机自启同步、参数处理。
    /// 这些都不挡盒子显示，放在 Background 优先级做。
    /// </summary>
    private void FinishStartupWork(bool firstRun, string[] args)
    {
        if (firstRun && _boxes.Count == 0)
        {
            CreateNewBox();
        }

        if (_tray is null)
        {
            CreateTray();
        }

        _mouseWatcher = new DesktopMouseWatcher { Enabled = _config.HideIconsOnDoubleClick };
        _mouseWatcher.EmptyDesktopDoubleClicked += (_, _) => Dispatcher.BeginInvoke(ToggleIconsFromUser);
        _mouseWatcher.Start();
        SyncTaskbarVisibility();
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

        // 老版本用 HKCU Run 自启，开机会被 Windows 延后几十秒；这里后台迁移到任务计划程序的登录任务。
        var autoStart = _config.AutoStart;
        _ = Task.Run(() => AutoStart.EnsureCurrent(autoStart));

        if (args.Length > 0)
        {
            HandleArgs(args);
        }
        else if (firstRun)
        {
            _tray?.ShowInfo(
                "DeskBox 已就绪",
                "托盘图标右键就能一键整理盒子、整理桌面文件、新建盒子；双击桌面空白处可以隐藏所有图标。");
        }
        else
        {
            ShowSettings();
        }
    }

    /// <summary>
    /// 创建托盘图标。任务计划程序登录任务可能比 Explorer 早启动，这时任务栏还没建好；
    /// WinForms 的 NotifyIcon 会在任务栏创建（TaskbarCreated 广播）后自己补上，失败时这里再重试几次。
    /// </summary>
    private void CreateTray()
    {
        try
        {
            _tray = new TrayService();
            _tray.Command += HandleCommand;
            _tray.MenuOpening += UpdateTrayState;
            _tray.ApplyTheme();
            LogStartupStage("托盘图标");
        }
        catch (Exception ex)
        {
            Log.Warn("创建托盘图标失败: " + ex.Message);

            var attempts = 0;
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                if (_exiting || ++attempts > 5)
                {
                    timer.Stop();
                    return;
                }

                try
                {
                    _tray = new TrayService();
                    _tray.Command += HandleCommand;
                    _tray.MenuOpening += UpdateTrayState;
                    _tray.ApplyTheme();
                    timer.Stop();
                    Log.Info("托盘图标已补建。");
                }
                catch
                {
                    // 下一轮再试
                }
            };
            timer.Start();
        }
    }

    /// <summary>
    /// 启动完成时的桌面现场：盒子是不是已经被桌面窗口盖住了（开机时 Explorer 可能比我们后创建桌面窗口）。
    /// 这条日志用来判断"开机后盒子隔几秒才出现"是不是 z 序竞态造成的。
    /// </summary>
    private void LogStartupDesktopState()
    {
        try
        {
            var desktopRoot = NativeMethods.DesktopRoot();
            var foreground = NativeMethods.GetForegroundWindow();
            var covered = _boxes.Where(box => box.IsCoveredByDesktopNow()).ToList();

            Log.Info(
                $"[启动] 桌面现场：桌面根={desktopRoot}({NativeMethods.ClassNameOf(desktopRoot)}) " +
                $"其上邻={NativeMethods.ClassNameOf(NativeMethods.GetWindow(desktopRoot, NativeMethods.GW_HWNDPREV))} " +
                $"前台={NativeMethods.ClassNameOf(foreground)} 桌面在前台={NativeMethods.DesktopIsForeground()} " +
                $"被桌面盖住的盒子={covered.Count}/{_boxes.Count}" +
                (covered.Count > 0 ? $"（{string.Join("、", covered.Select(box => box.Config.Title))}）" : string.Empty));
        }
        catch (Exception ex)
        {
            Log.Warn("启动桌面现场检查失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 开机上下文：系统已启动多久、Explorer 是否已经起来。
    /// 用来判断"盒子是不是比桌面晚出来"——如果 Explorer 已运行很久我们才被拉起，那问题在自启动时机；
    /// 如果 Explorer 还没起来，说明盒子先于桌面创建，问题会变成"桌面后出现把盒子盖住"。
    /// </summary>
    private static void LogBootContext()
    {
        try
        {
            var bootMs = Environment.TickCount64;
            var explorer = "未知";

            try
            {
                var started = Process.GetProcessesByName("explorer")
                    .Select(process =>
                    {
                        try
                        {
                            return process.StartTime;
                        }
                        catch
                        {
                            return DateTime.MinValue;
                        }
                    })
                    .Where(time => time != DateTime.MinValue)
                    .DefaultIfEmpty(DateTime.MinValue)
                    .Min();

                explorer = started == DateTime.MinValue
                    ? "尚未启动"
                    : $"已运行 {(long)(DateTime.Now - started).TotalMilliseconds}ms";
            }
            catch
            {
                // 取不到就算了
            }

            Log.Info($"[启动] 开机上下文：系统已启动 {bootMs}ms，Explorer {explorer}，本进程自启动 {StartupTrace.SinceProcessStartMs}ms");
        }
        catch (Exception ex)
        {
            Log.Warn("启动上下文记录失败: " + ex.Message);
        }
    }

    /// <summary>启动阶段临时提高进程优先级：开机时有十几个自启动程序一起抢 CPU，盒子出来就还原。</summary>
    private static void RaiseStartupPriority()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            if (process.PriorityClass == ProcessPriorityClass.Normal)
            {
                process.PriorityClass = ProcessPriorityClass.AboveNormal;
            }
        }
        catch
        {
            // 权限不允许就算了
        }
    }

    private static void RestoreStartupPriority()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            if (process.PriorityClass == ProcessPriorityClass.AboveNormal)
            {
                process.PriorityClass = ProcessPriorityClass.Normal;
            }
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 盒子还没建之前，后台把各盒子目录里的图标从磁盘缓存解码进内存。
    /// 这样窗口第一次显示时图标已经就绪，不会先闪一排占位图再逐个换掉。
    /// </summary>
    private void PrewarmBoxIcons()
    {
        var boxes = _config.Boxes.ToList();
        var root = _config.EffectiveBoxRoot;
        var size = (int)_config.IconSize;
        var includeHidden = _config.ShowHiddenFiles;

        _ = Task.Run(() =>
        {
            try
            {
                var paths = new List<string>();

                foreach (var box in boxes)
                {
                    var folder = string.IsNullOrWhiteSpace(box.Folder)
                        ? Path.Combine(root, FsUtil.Sanitize(box.Title))
                        : box.Folder;

                    try
                    {
                        if (!Directory.Exists(folder))
                        {
                            continue;
                        }

                        foreach (var path in Directory.EnumerateFileSystemEntries(folder))
                        {
                            if (Path.GetFileName(path).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            if (!includeHidden && FsUtil.IsHiddenOrSystem(path))
                            {
                                continue;
                            }

                            paths.Add(path);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"图标预热读取目录失败 {folder}: {ex.Message}");
                    }
                }

                if (paths.Count > 0)
                {
                    IconCache.Prewarm(paths, size);
                    Log.Info($"[启动] 图标预热排队 {paths.Count} 项，自进程启动 {StartupTrace.SinceProcessStartMs}ms");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("图标预热失败: " + ex.Message);
            }
        });
    }

    /// <summary>
    /// 监听桌面窗口自己被显示：开机时 Explorer 若比我们晚创建桌面窗口，桌面会盖住盒子；
    /// 这里不等 1.5s 定时器，收到事件就立刻安排一次巡检把盒子插回桌面之上。
    /// Explorer 重启（新 Progman / WorkerW）同样走这条路径。
    /// </summary>
    private void WatchDesktopWindowAppearance()
    {
        _desktopShowProc = (_, _, hwnd, idObject, idChild, _, _) =>
        {
            // 只要窗口对象本身（OBJID_WINDOW / CHILDID_SELF）
            if (idObject != 0 || idChild != 0 || !NativeMethods.IsDesktopWindow(hwnd))
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_exiting || !_startupComplete)
                {
                    return;
                }

                if (DateTime.UtcNow - _lastDesktopShowLog >= TimeSpan.FromMilliseconds(DesktopLogIntervalMs))
                {
                    _lastDesktopShowLog = DateTime.UtcNow;
                    Log.Info($"[启动] 桌面窗口显示（{NativeMethods.ClassNameOf(hwnd)}），立刻巡检盒子 z 序。");
                }

                // 新的桌面窗口出现（Explorer 重启 / 开机晚于盒子）：把盒子窗口重新归属到它上面，
                // 并把旧的、已经被系统销毁的盒子窗口重建出来。
                EnsureDesktopOwners(NativeMethods.DesktopOwnerWindow());
                RecoverLostBoxesNow();

                ScheduleDesktopSweep();
            }));
        };

        _desktopShowHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_CREATE,
            NativeMethods.EVENT_OBJECT_SHOW,
            IntPtr.Zero,
            _desktopShowProc,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        if (_desktopShowHook == IntPtr.Zero)
        {
            Log.Warn("注册桌面窗口显示监听失败，开机时会退回短周期巡检。");
        }
    }

    /// <summary>
    /// 启动完成后的头几秒用约 90ms 的短周期巡检兜住 z 序竞态：
    /// 登录任务可能比 Explorer 还早启动，桌面窗口随后出现会盖住盒子；一旦发现就马上插回去，
    /// 而不是等每个盒子 1.5s 的常规定时器。覆盖登录后桌面/壁纸窗口陆续创建的那段时间。
    /// </summary>
    private void BeginStartupSweeps()
    {
        const int sweepCount = 55; // 55 × 90ms ≈ 5s

        _startupSweepsLeft = sweepCount;

        if (_startupSweepTimer is null)
        {
            _startupSweepTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(90),
            };
            _startupSweepTimer.Tick += (_, _) =>
            {
                if (_exiting || _startupSweepsLeft <= 0)
                {
                    _startupSweepTimer!.Stop();
                    return;
                }

                _startupSweepsLeft--;
                RunDesktopSweep();
            };
        }

        _startupSweepTimer.Start();
    }

    /// <summary>启动阶段埋点：统一 `[启动]` 前缀，给出本阶段、累计以及"自进程启动"的毫秒数。</summary>
    private static void LogStartupStage(string stage) => StartupTrace.Mark(stage);

    #region 盒子管理

    /// <summary>
    /// 盯着前台窗口的变化，只做两件事：
    ///
    /// 1. 桌面窗口成为前台（Win+D / 任务栏「显示桌面」）→ 记一个状态，不做任何 z 序动作。
    ///    盒子窗口已经归属桌面根窗口（见 BoxWindow.AttachToDesktop），owned 窗口会被窗口管理器
    ///    一起带上桌面层，全过程不会出现「桌面盖住盒子」的帧，所以这里不需要抢 z 序。
    ///    只留一次兜底巡检：万一归属关系没挂上，还能按旧路径把盒子插回桌面之上。
    ///
    /// 2. 前台离开桌面窗口（再按一次 Win+D / 点任务栏窗口 / 点开始菜单）→ 同步把跟着桌面
    ///    被抬到高处的盒子压回「桌面之上、普通窗口之下」。这一步必须同步做（不再 Dispatcher
    ///    BeginInvoke + 防抖），否则恢复桌面后盒子会在普通窗口上面多停几帧，看起来还是闪。
    ///
    /// 点任务栏 / 开始菜单这类不会盖住盒子的前台变化，在恢复完层叠后直接跳过，不触发巡检。
    /// </summary>
    private void WatchForegroundChanges()
    {
        // WinEvent 回调就是在装钩子的线程（UI 线程）上送达的，这里直接做 z 序处理。
        _foregroundProc = (_, _, hwnd, _, _, _, _) =>
        {
            if (_exiting)
            {
                return;
            }

            var foreground = hwnd != IntPtr.Zero ? hwnd : NativeMethods.GetForegroundWindow();

            if (NativeMethods.IsDesktopWindow(foreground))
            {
                _desktopShown = true;

                // 兜底：归属关系没生效时，仍然走原来的「巡检把盒子插回桌面之上」。
                Dispatcher.BeginInvoke(new Action(ScheduleDesktopSweep), DispatcherPriority.Background);
                return;
            }

            if (_desktopShown)
            {
                _desktopShown = false;
                NormalizeBoxesToDesktopLayer();
            }
            else
            {
                // 兜底自愈：某些系统时序（例如快速连按 Win+D、第三方显示桌面工具）会让盒子
                // 停在普通窗口层高处，而前台事件又不是「桌面 -> 普通窗口」的标准路径。
                // 这里只在前台是普通窗口时做一次廉价校验，发现盒子没贴住桌面层才压底。
                EnsureBoxesAtDesktopLayerIfFloating();
            }

            // 点任务栏 / 开始菜单 / 任务栏缩略图不会盖住盒子：不需要额外巡检
            if (NativeMethods.IsShellTrayWindow(foreground))
            {
                return;
            }

            foreach (var box in _boxes)
            {
                box.KeepVisibleOnDesktop();
            }
        };

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
    /// 显示桌面结束后，把跟着桌面被抬上去的盒子同步压回「桌面之上、普通窗口之下」。
    /// 只动盒子自己的 z 序，不碰桌面窗口（改桌面的 z 序会触发整屏重绘，那才是真正的闪烁来源）。
    /// </summary>
    private void NormalizeBoxesToDesktopLayer()
    {
        if (_exiting || !_startupComplete || BoxWindow.DesktopInteractionActive)
        {
            return;
        }

        PushBoxesToDesktopLayer();
        WatchDesktopLayerRestored();
    }

    /// <summary>把每个可见盒子的 z 序压回「桌面根窗口正上方」。</summary>
    private void PushBoxesToDesktopLayer()
    {
        // owner 必须是 DesktopOwnerWindow（SHELLDLL_DefView）：直接挂 Progman 会让外壳在盒子
        // 成为前台窗口后误判「桌面已显示」，再按 Win+D 就没有反应。
        var desktopOwner = NativeMethods.DesktopOwnerWindow();
        if (desktopOwner == IntPtr.Zero)
        {
            return;
        }

        foreach (var box in _boxes)
        {
            box.AttachToDesktop(desktopOwner);
            box.NormalizeToDesktopLayer();
        }

        if (DateTime.UtcNow - _lastNormalizeLog >= TimeSpan.FromMilliseconds(DesktopLogIntervalMs))
        {
            _lastNormalizeLog = DateTime.UtcNow;
            Log.Info($"显示桌面结束：已把 {_boxes.Count} 个盒子同步压回桌面之上。");
        }
    }

    /// <summary>
    /// 显示桌面结束后做一小段自校验：系统降下桌面窗口的时序不一定早于前台事件。
    /// 如果某次压底发生在这之前，盒子会停在当时还高高的桌面根窗口上方，等桌面降下去就飘在
    /// 普通窗口层高处。这里每隔 60ms 检查一次「桌面根窗口正上方是不是这些盒子」，
    /// 不对就再压一次；最多检查约 800ms，避免异常情况下无限重排。
    /// </summary>
    private void WatchDesktopLayerRestored()
    {
        if (_exiting)
        {
            return;
        }

        if (_desktopLayerWatchTimer is null)
        {
            _desktopLayerWatchTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(60),
            };
            _desktopLayerWatchTimer.Tick += (_, _) => VerifyDesktopLayerRestored();
        }

        _desktopLayerWatchTicks = 0;
        _desktopLayerCompensations = 0;
        _desktopLayerWatchTimer.Start();
    }

    private void VerifyDesktopLayerRestored()
    {
        _desktopLayerWatchTicks++;

        if (_exiting || _desktopLayerWatchTicks > 16)
        {
            _desktopLayerWatchTimer?.Stop();
            return;
        }

        // 还在显示桌面状态：等桌面降下来再看情况。
        if (NativeMethods.DesktopIsForeground())
        {
            return;
        }

        var desktopRoot = NativeMethods.DesktopRoot();
        if (desktopRoot == IntPtr.Zero)
        {
            return;
        }

        var handles = _boxes
            .Where(box => box.IsVisible)
            .Select(box => box.BoxHandle)
            .Where(handle => handle != IntPtr.Zero)
            .ToList();

        if (handles.Count == 0)
        {
            _desktopLayerWatchTimer?.Stop();
            return;
        }

        if (NativeMethods.AreBoxesAtDesktopLayer(handles, desktopRoot))
        {
            // 注意：这里不能提前停止。桌面根窗口可能还没降下来，此时盒子贴在它上方是「暂时正确」的；
            // 等桌面降下去，盒子就飘在普通窗口层高处了，必须继续观察到窗口期结束。
            return;
        }

        // 只允许补压两次，避免窗口链上有别的窗口时反复重排。
        if (_desktopLayerCompensations >= 2)
        {
            _desktopLayerWatchTimer?.Stop();
            return;
        }

        _desktopLayerCompensations++;
        PushBoxesToDesktopLayer();
    }

    /// <summary>
    /// 廉价自愈：前台已经是普通窗口时，确认盒子是否真的紧贴桌面根窗口上方；
    /// 只有确实飘到普通窗口层高处时才重新压底（不碰桌面窗口，不产生多余重排）。
    /// 前台是盒子自己时跳过，避免把用户正在操作的盒子压下去。
    /// </summary>
    private void EnsureBoxesAtDesktopLayerIfFloating()
    {
        if (_exiting || !_startupComplete || BoxWindow.DesktopInteractionActive)
        {
            return;
        }

        if (NativeMethods.DesktopIsForeground())
        {
            return;
        }

        var desktopRoot = NativeMethods.DesktopRoot();
        if (desktopRoot == IntPtr.Zero)
        {
            return;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        foreach (var box in _boxes)
        {
            if (box.BoxHandle == foreground)
            {
                return;
            }
        }

        var handles = _boxes
            .Where(box => box.IsVisible)
            .Select(box => box.BoxHandle)
            .Where(handle => handle != IntPtr.Zero)
            .ToList();

        if (handles.Count == 0 || NativeMethods.AreBoxesAtDesktopLayer(handles, desktopRoot))
        {
            return;
        }

        // 节流：真正需要补压时最多 2 秒一次，避免启动阶段或频繁切前台时反复重排。
        if (DateTime.UtcNow - _lastFloatingFix < TimeSpan.FromSeconds(2))
        {
            return;
        }

        _lastFloatingFix = DateTime.UtcNow;
        PushBoxesToDesktopLayer();
    }

    /// <summary>确保每个盒子窗口的 owner 都是当前的桌面窗口（Explorer 重启后桌面窗口会换）。</summary>
    private void EnsureDesktopOwners(IntPtr desktopOwner)
    {
        if (desktopOwner == IntPtr.Zero)
        {
            return;
        }

        foreach (var box in _boxes)
        {
            box.AttachToDesktop(desktopOwner);
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

        // 安全网：盒子如果因为系统时序飘到了普通窗口层高处（而不是贴在桌面根窗口上方），压回去。
        EnsureBoxesAtDesktopLayerIfFloating();

        // 桌面窗口可能刚被 Explorer 重建：把盒子窗口的 owner 重新挂到当前桌面窗口上。
        var desktopRoot = NativeMethods.DesktopRoot();
        EnsureDesktopOwners(NativeMethods.DesktopOwnerWindow());

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

        if (_desktopShowHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_desktopShowHook);
            _desktopShowHook = IntPtr.Zero;
        }

        _foregroundProc = null;
        _desktopShowProc = null;
        _startupSweepTimer?.Stop();
        _desktopLayerWatchTimer?.Stop();
        _desktopLayerWatchTimer = null;
        _boxRecoveryTimer?.Stop();
        _boxRecoveryTimer = null;
    }

    private void RestoreBoxes()
    {
        var pending = new List<BoxWindow>(_config.Boxes.Count);

        // 先把所有盒子窗口构造出来（各自挂文件监视、条目与图标预热），再一起显示。
        // 第一个窗口要付 WPF 渲染初始化的一次性成本，这样"亮出盒子"这一步只等一次，
        // 不会出现"第一个盒子先出来、后面几个再一个个补"的延迟。
        foreach (var boxConfig in _config.Boxes.ToList())
        {
            if (string.IsNullOrWhiteSpace(boxConfig.Folder) || !Directory.Exists(boxConfig.Folder))
            {
                Log.Warn($"盒子目录不存在，移除盒子记录：{boxConfig.Title}");
                _config.Boxes.Remove(boxConfig);
                continue;
            }

            pending.Add(CreateBoxWindow(boxConfig, show: false));
        }

        foreach (var window in pending)
        {
            var watch = Stopwatch.StartNew();
            window.Show();

            if (!_startupComplete)
            {
                Log.Info($"[启动] 盒子「{window.Config.Title}」显示 {watch.ElapsedMilliseconds}ms，自进程启动 {StartupTrace.SinceProcessStartMs}ms");
            }
        }
    }

    private BoxWindow CreateBoxWindow(BoxConfig boxConfig, bool show = true)
    {
        var watch = Stopwatch.StartNew();

        var window = new BoxWindow(_config, boxConfig, _shell, PeerRects)
        {
            ShowActivated = false,
        };

        window.Changed += (_, _) => ScheduleSave();
        window.RemovalRequested += (sender, _) => RemoveBox((BoxWindow)sender!);
        window.Closed += OnBoxWindowClosed;

        _boxes.Add(window);

        if (!_startupComplete)
        {
            Log.Info($"[启动] 盒子「{boxConfig.Title}」构造 {watch.ElapsedMilliseconds}ms，自进程启动 {StartupTrace.SinceProcessStartMs}ms");
        }

        if (show)
        {
            window.Show();
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

    /// <summary>
    /// 盒子窗口关闭。用户主动关闭（解散盒子、退出程序）不处理；
    /// 被系统销毁——典型是 Explorer 重启导致桌面窗口（窗口的 owner）被销毁，
    /// owned 窗口会被系统一起销毁——则自动把窗口重建回来，避免整排盒子消失。
    /// </summary>
    private void OnBoxWindowClosed(object? sender, EventArgs e)
    {
        if (_exiting || sender is not BoxWindow window)
        {
            return;
        }

        if (!_boxes.Contains(window))
        {
            return; // RemoveBox 先移除再 Close，属于预期关闭
        }

        _boxes.Remove(window);

        if (window.ClosedByRequest)
        {
            Log.Info($"盒子「{window.Config.Title}」窗口已关闭。");
            return;
        }

        Log.Warn($"盒子「{window.Config.Title}」窗口被系统销毁（多半是 Explorer 重启），准备自动重建。");

        // 全部盒子窗口一起被销毁时，WPF 默认会在最后一个窗口关闭后退出进程；
        // 切到显式退出把进程留住，等重建定时器把新的盒子窗口建出来。
        if (Application.Current.ShutdownMode == ShutdownMode.OnLastWindowClose)
        {
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        ScheduleBoxRecovery();
    }

    /// <summary>安排一次盒子窗口重建（防抖：一批窗口被销毁只跑一个定时器）。</summary>
    private void ScheduleBoxRecovery()
    {
        if (_boxRecoveryTimer is null)
        {
            _boxRecoveryTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(700),
            };
            _boxRecoveryTimer.Tick += (_, _) => RecoverLostBoxesNow();
        }

        if (!_boxRecoveryTimer.IsEnabled)
        {
            _boxRecoveryAttempts = 0;
            _boxRecoveryTimer.Start();
        }
    }

    /// <summary>
    /// 把配置里存在、但当前没有窗口的盒子重新建出来。Explorer 重启后需要等新的桌面窗口就绪，
    /// 所以由定时器反复尝试；连续失败到上限后停手（进程仍在托盘，用户可以手动新建盒子）。
    /// </summary>
    private void RecoverLostBoxesNow()
    {
        if (_exiting)
        {
            _boxRecoveryTimer?.Stop();
            return;
        }

        var missing = _config.Boxes
            .Where(config => !_boxes.Any(window => ReferenceEquals(window.Config, config)))
            .ToList();

        if (missing.Count == 0)
        {
            if (_boxRecoveryTimer is not null)
            {
                _boxRecoveryTimer.Stop();
            }

            if (Application.Current.ShutdownMode == ShutdownMode.OnExplicitShutdown)
            {
                Application.Current.ShutdownMode = ShutdownMode.OnLastWindowClose;
            }

            return;
        }

        _boxRecoveryAttempts++;
        if (_boxRecoveryAttempts > BoxRecoveryMaxAttempts)
        {
            _boxRecoveryTimer?.Stop();
            Log.Warn($"盒子窗口自动重建失败（{BoxRecoveryMaxAttempts} 次未成功），剩余 {missing.Count} 个盒子未被重建。");
            return;
        }

        var pending = new List<BoxWindow>(missing.Count);
        foreach (var config in missing)
        {
            if (string.IsNullOrWhiteSpace(config.Folder) || !Directory.Exists(config.Folder))
            {
                Log.Warn($"盒子目录不存在，跳过重建：「{config.Title}」");
                continue;
            }

            try
            {
                pending.Add(CreateBoxWindow(config, show: false));
            }
            catch (Exception ex)
            {
                Log.Warn($"重建盒子「{config.Title}」失败: {ex.Message}");
            }
        }

        foreach (var window in pending)
        {
            window.Show();
            window.AttachToDesktop();
            window.NormalizeToDesktopLayer();
        }

        if (pending.Count > 0)
        {
            Log.Info($"已重建 {pending.Count} 个盒子窗口（第 {_boxRecoveryAttempts} 次尝试）。");
            ScheduleDesktopSweep();
        }
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
        if (!_shell.SetIconsVisible(wantVisible))
        {
            return;
        }
        _config.IconsHiddenByApp = !wantVisible;
        SyncTaskbarVisibility();

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
        _taskbar?.Dispose();

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
        _taskbar?.Dispose();
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
        SyncTaskbarVisibility();

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

    private void SyncTaskbarVisibility()
    {
        var hidden = _config.HideTaskbarWithIcons && _config.IconsHiddenByApp && !_shell.IconsVisible;
        if (hidden)
        {
            _taskbar ??= new DesktopTaskbar();
        }
        _taskbar?.SetHidden(hidden);
    }

    private void ScheduleSave()
    {
        _saveTimer?.Stop();
        _saveTimer?.Start();
    }

    #endregion
}
