using System.Windows;
using System.Windows.Threading;
using DeskBox.Interop;
using DeskBox.Services;
using DeskBox.Views;

namespace DeskBox;

public partial class App
{
    private bool _launcherMode;
    private LauncherWindow? _launcher;
    private WindowsKeyLauncher? _winKey;
    private LauncherCatalog _launcherCatalog = new(Array.Empty<LaunchEntry>(), Array.Empty<LaunchEntry>());
    private DispatcherTimer? _launcherRefresh;
    private DispatcherTimer? _launcherDebounce;
    private bool _launcherScanning;
    private bool _launcherScanPending;
    private int _launcherCatalogRevision;
    private bool _launcherTaskbarHidden;
    private readonly Dictionary<string, FileSystemWatcher> _launcherWatchers = new(StringComparer.OrdinalIgnoreCase);

    private void StartLauncher(bool firstRun, string[] args)
    {
        _iconLayer?.Suppress();
        _launcher = new LauncherWindow(_config);
        _launcher.ConfigChanged += LauncherConfigChanged;
        _launcher.UsageChanged += () =>
        {
            RefreshLauncher();
            ScheduleSave();
        };
        _launcher.ShortcutDeleted += RemoveLauncherShortcut;
        _launcher.SettingsRequested += ShowSettings;
        RefreshLauncher();
        RefreshWinKey();
        _mouseWatcher = new DesktopMouseWatcher(iconsSuppressed: true);
        _mouseWatcher.EmptyDesktopDoubleClicked += (_, _) => Dispatcher.BeginInvoke(ToggleLauncherTaskbar);
        _mouseWatcher.Start();
        WatchLauncher(hideDesktop: true);
        _startupComplete = true;
        RestoreStartupPriority();
        LogStartupStage("开始面板已就绪");
        StartupTrace.EndStartupAfter(TimeSpan.FromSeconds(2));
        FinishStartupWork(firstRun, args);
    }

    private void PreviewLauncher()
    {
        _config = Models.ConfigStore.Load();
        ThemeManager.Apply(_config.Theme);
        _launcher = new LauncherWindow(_config, preview: true);
        _launcher.ConfigChanged += RefreshLauncher;
        _launcher.UsageChanged += RefreshLauncher;
        _launcher.ShortcutDeleted += RemoveLauncherShortcut;
        _launcher.Closed += (_, _) => Shutdown();
        RefreshLauncher();
        WatchLauncher(hideDesktop: false);
        _launcher.Open();
    }

    private void WatchLauncher(bool hideDesktop)
    {
        var suppressed = false;
        void HideDesktop()
        {
            if (_iconLayer?.Suppress() != true || suppressed) return;
            suppressed = true;
            Log.Info("开始面板：Explorer 桌面图标层已隐藏。");
        }
        if (hideDesktop) HideDesktop();
        _launcherDebounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _launcherDebounce.Tick += async (_, _) =>
        {
            _launcherDebounce.Stop();
            await ScanLauncherAsync();
        };
        var checks = 0;
        _launcherRefresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _launcherRefresh.Tick += (_, _) =>
        {
            // Explorer may start after DeskBox or restart during the session.
            if (hideDesktop) HideDesktop();
            if (++checks % 8 == 0) RequestLauncherScan();
        };
        _launcherRefresh.Start();
        RefreshLauncherWatchers();
        RequestLauncherScan();
    }

    private void OpenLauncher()
    {
        if (_exiting || _launcher is null) return;
        _launcher.Open();
    }

    private void ToggleLauncher()
    {
        if (_exiting || _launcher is null) return;
        if (_launcher.IsVisible && _launcher.IsActive) _launcher.Hide();
        else OpenLauncher();
    }

    private void ToggleLauncherTaskbar()
    {
        if (_exiting) return;
        _launcherTaskbarHidden = !_launcherTaskbarHidden;
        _taskbar ??= new DesktopTaskbar();
        _taskbar.SetHidden(_launcherTaskbarHidden);
        var windows = NativeMethods.FindTaskbarWindows();
        Log.Info($"开始面板：{(_launcherTaskbarHidden ? "隐藏" : "显示")}任务栏，当前可见 {windows.Count(NativeMethods.IsWindowVisible)}/{windows.Count}。");
    }

    private void RefreshWinKey()
    {
        if (_config.LauncherInterceptWinKey)
        {
            if (_winKey is not null) return;
            var hook = new WindowsKeyLauncher(Dispatcher, ToggleLauncher);
            if (hook.Start()) _winKey = hook;
            else hook.Dispose();
        }
        else
        {
            _winKey?.Dispose();
            _winKey = null;
        }
    }

    private void LauncherConfigChanged()
    {
        RefreshLauncher();
        RefreshLauncherWatchers();
        RefreshWinKey();
        ScheduleSave();
    }

    private void RefreshLauncher() => _launcher?.SetSections(_launcherCatalog.Sections(_config));

    private void RemoveLauncherShortcut(string path)
    {
        _launcherCatalogRevision++;
        _launcherCatalog = _launcherCatalog.WithoutPath(path);
        IconCache.Invalidate(path);
        RequestLauncherScan();
    }

    private void RequestLauncherScan()
    {
        if (_exiting || _launcherDebounce is null) return;
        _launcherScanPending = true;
        _launcherDebounce.Stop();
        _launcherDebounce.Start();
    }

    private async Task ScanLauncherAsync()
    {
        if (_exiting || _launcherScanning || !_launcherScanPending) return;
        _launcherScanning = true;
        _launcherScanPending = false;
        var revision = _launcherCatalogRevision;
        try
        {
            var catalog = await LauncherCatalog.ScanAsync(_shell, _config.EffectiveBoxRoot, _config.ShowHiddenFiles);
            if (_exiting || revision != _launcherCatalogRevision) return;
            _launcherCatalog = catalog;
            RefreshLauncherWatchers();
            RefreshLauncher();
        }
        catch (Exception ex) { Log.Warn("刷新开始面板失败: " + ex.Message); }
        finally
        {
            _launcherScanning = false;
            if (_launcherScanPending && !_exiting) _launcherDebounce?.Start();
        }
    }

    private void RefreshLauncherWatchers()
    {
        if (_exiting) return;
        var programs = LauncherCatalog.ProgramsFolders().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roots = _config.Boxes.Where(box => box.IsVirtual).SelectMany(box => box.Entries)
            .Where(entry => !VirtualGroups.IsShell(entry.Path))
            .Select(entry => Path.GetDirectoryName(entry.Path))
            .Concat(new[] { _shell.DesktopPath, _shell.PublicDesktopPath }).Concat(programs)
            .Where(Directory.Exists).Select(path => path!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var root in _launcherWatchers.Keys.Where(root => !roots.Contains(root)).ToArray())
        {
            _launcherWatchers[root].Dispose();
            _launcherWatchers.Remove(root);
        }
        foreach (var root in roots.Where(root => !_launcherWatchers.ContainsKey(root)))
        {
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = programs.Contains(root),
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                };
                watcher.Created += LauncherFilesChanged;
                watcher.Changed += LauncherFilesChanged;
                watcher.Deleted += LauncherFilesChanged;
                watcher.Renamed += LauncherFilesChanged;
                watcher.Error += (_, _) => Dispatcher.BeginInvoke(() =>
                {
                    if (_launcherWatchers.Remove(root, out var failed)) failed.Dispose();
                    RequestLauncherScan();
                });
                watcher.EnableRaisingEvents = true;
                _launcherWatchers.Add(root, watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                watcher?.Dispose();
                Log.Warn("监听面板项目失败: " + ex.Message);
            }
        }
    }

    private void LauncherFilesChanged(object sender, FileSystemEventArgs args) => Dispatcher.BeginInvoke(() =>
    {
        if (_exiting) return;
        if (args is RenamedEventArgs rename && VirtualGroups.RenameReference(_config, rename.OldFullPath, rename.FullPath))
            LauncherConfigChanged();
        IconCache.Invalidate(args.FullPath);
        RequestLauncherScan();
    });

    private void StopLauncher()
    {
        _winKey?.Dispose();
        _launcherRefresh?.Stop();
        _launcherDebounce?.Stop();
        foreach (var watcher in _launcherWatchers.Values) watcher.Dispose();
        _launcherWatchers.Clear();
    }
}
