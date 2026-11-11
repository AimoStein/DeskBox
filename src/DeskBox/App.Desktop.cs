using System.IO;
using System.Windows.Threading;
using DeskBox.Interop;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Views;

namespace DeskBox;

public partial class App
{
    private DesktopIconLayer? _iconLayer;
    private StartupDesktop? _startupDesktop;
    private DesktopIconWindow? _desktopWindow;
    private DesktopCatalog? _desktopCatalog;
    private DesktopCatalog.Presentation? _desktopPresentation;
    private readonly List<string[]> _startupCommands = new();
    private readonly Dictionary<string, FileSystemWatcher> _desktopWatchers = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _desktopScanTimer;
    private DispatcherTimer? _desktopFallbackTimer;
    private DispatcherTimer? _desktopHandoffTimer;
    private bool _desktopActive;
    private bool _desktopCanActivate;
    private bool _desktopScanRunning;
    private bool _desktopScanPending;

    private async Task<DesktopCatalog?> ScanDesktopAsync()
    {
        var grouped = _config.Boxes.Where(box => box.IsVirtual).SelectMany(box => box.Entries)
            .Select(entry => entry.Path).ToList();
        var root = _config.EffectiveBoxRoot;
        var hidden = _config.ShowHiddenFiles;
        var previous = _desktopCatalog;
        try
        {
            return await Task.Run(() => DesktopCatalog.Scan(_shell, root, hidden, grouped, previous));
        }
        catch (Exception ex)
        {
            Log.Warn("扫描桌面失败: " + ex.Message);
            return previous;
        }
    }

    private bool IsBoxItemVisible(BoxConfig box, string path)
    {
        if (_desktopPresentation?.Hidden.Contains((box.Id, path)) == true) return false;
        // 新入组的快捷方式要等身份解析完成再上屏。
        return _desktopCatalog is null || !ShortcutIdentity.IsShortcut(path) ||
            _desktopCatalog.HasReadShortcut(path) || !File.Exists(path);
    }

    private void UpdateDesktopPresentation(bool reloadBoxes = true, bool forceReload = false)
    {
        if (_desktopCatalog is null) return;
        var next = _desktopCatalog.Reconcile(_config);
        var changed = _desktopPresentation is null || !_desktopPresentation.Hidden.SetEquals(next.Hidden) ||
            !_desktopPresentation.Desktop.SequenceEqual(next.Desktop);
        _desktopPresentation = next;
        if (reloadBoxes && (forceReload || changed || next.ConfigChanged))
            foreach (var box in _boxes) box.ReloadItems();
        _desktopWindow?.UpdateItems(next.Desktop, _config.Boxes);
        if (changed || next.ConfigChanged) ScheduleSave();
    }

    private void EnsureDesktopIconWindow()
    {
        if (_desktopPresentation is null) return;
        if (_desktopWindow is { } existing && NativeMethods.IsWindow(existing.Handle)) return;
        var window = new DesktopIconWindow(MergeShortcut) { Opacity = 0 };
        _desktopWindow = window;
        window.Closed += (_, _) =>
        {
            if (_desktopWindow == window) _desktopWindow = null;
            if (!_exiting) ScheduleDesktopSweep();
        };
        window.Show();
        window.Attach(NativeMethods.DesktopOwnerWindow());
        window.UpdateItems(_desktopPresentation.Desktop, _config.Boxes);
    }

    private void CompleteDesktopStartup(bool firstRun, string[] args)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _startupDesktop?.Stop();
            _desktopHandoffTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(25),
            };
            _desktopHandoffTimer.Tick += (_, _) =>
            {
                if (_startupDesktop?.IsFinished == false) return;
                _desktopHandoffTimer.Stop();
                if (_exiting) return;
                _desktopCanActivate = true;
                RefreshDesktopLayer();
                try { FinishStartupWork(firstRun, args); }
                catch (Exception ex) { Log.Warn("启动收尾失败: " + ex); }
            };
            _desktopHandoffTimer.Start();
        }));
    }

    private void RefreshDesktopLayer()
    {
        if (!_desktopCanActivate || _desktopPresentation is null || _exiting) return;
        var wasActive = _desktopActive;
        _desktopActive = _iconLayer?.Suppress() == true;
        EnsureDesktopIconWindow();
        UpdateDesktopVisibility();
        if (_desktopActive && !wasActive)
            Log.Info($"[启动] 桌面与盒子完成显示交接，自进程启动 {StartupTrace.SinceProcessStartMs}ms，桌面 {_desktopPresentation.Desktop.Count} 项，盒内已折叠重复 {_desktopPresentation.Hidden.Count} 项");
    }

    private void UpdateDesktopVisibility()
    {
        if (_desktopWindow is not null)
            _desktopWindow.Opacity = _desktopActive && !_config.IconsHiddenByApp ? 1 : 0;
        foreach (var box in _boxes)
            box.Opacity = _desktopActive ? 1 : 0;
    }

    private void StartDesktopWatch()
    {
        _desktopScanTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(240),
        };
        _desktopScanTimer.Tick += async (_, _) =>
        {
            _desktopScanTimer.Stop();
            if (_desktopScanRunning || !_desktopScanPending || _exiting) return;
            _desktopScanPending = false;
            _desktopScanRunning = true;
            try
            {
                _desktopCatalog = await ScanDesktopAsync();
                if (_exiting) return;
                // 解析完成后，让等候身份校验的新快捷方式上屏。
                UpdateDesktopPresentation(forceReload: true);
                RefreshDesktopWatchers();
                RefreshDesktopLayer();
            }
            finally
            {
                _desktopScanRunning = false;
                if (_desktopScanPending && !_exiting) _desktopScanTimer.Start();
            }
        };
        _desktopFallbackTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(10),
        };
        _desktopFallbackTimer.Tick += (_, _) => RequestDesktopScan();
        _desktopFallbackTimer.Start();
        RefreshDesktopWatchers();
        RequestDesktopScan();
    }

    private void RefreshDesktopWatchers()
    {
        var folders = _config.Boxes.Where(box => box.IsVirtual).SelectMany(box => box.Entries)
            .Where(entry => !VirtualGroups.IsShell(entry.Path)).Select(entry => Path.GetDirectoryName(entry.Path))
            .Concat(new[] { _shell.DesktopPath, _shell.PublicDesktopPath })
            .Where(Directory.Exists).Select(path => path!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _desktopWatchers.Keys.Where(path => !folders.Contains(path)).ToArray())
        {
            _desktopWatchers[path].Dispose();
            _desktopWatchers.Remove(path);
        }
        foreach (var path in folders.Where(path => !_desktopWatchers.ContainsKey(path)))
        {
            try
            {
                var watcher = new FileSystemWatcher(path) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
                watcher.Created += DesktopChanged;
                watcher.Changed += DesktopChanged;
                watcher.Deleted += DesktopChanged;
                watcher.Renamed += DesktopChanged;
                watcher.Error += (_, _) => Dispatcher.BeginInvoke(() =>
                {
                    if (_desktopWatchers.Remove(path, out var failed)) failed.Dispose();
                    RequestDesktopScan();
                });
                try
                {
                    watcher.EnableRaisingEvents = true;
                    _desktopWatchers.Add(path, watcher);
                }
                catch { watcher.Dispose(); throw; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Log.Warn("监听桌面项目失败: " + ex.Message);
            }
        }
    }

    private void DesktopChanged(object sender, FileSystemEventArgs args) =>
        Dispatcher.BeginInvoke(RequestDesktopScan);

    private void RequestDesktopScan()
    {
        if (_exiting || _desktopScanTimer is null) return;
        _desktopScanPending = true;
        _desktopScanTimer.Stop();
        _desktopScanTimer.Start();
    }

    private void StopDesktopWatch()
    {
        _exiting = true;
        _desktopScanTimer?.Stop();
        _desktopFallbackTimer?.Stop();
        _desktopHandoffTimer?.Stop();
        _startupDesktop?.Stop();
        foreach (var watcher in _desktopWatchers.Values) watcher.Dispose();
        _desktopWatchers.Clear();
    }

    private void SaveDesktopSnapshot()
    {
        if (!_desktopActive) return;
        var boxes = _boxes.Where(box => box.IsVisible).Select(box => box.Snapshot()).ToList();
        var items = !_config.IconsHiddenByApp && _desktopWindow is not null
            ? _desktopWindow.SnapshotItems().ToList() : new List<StartupDesktopItem>();
        new StartupDesktopLayout(boxes, items).Save();
    }

    private void MergeShortcut(string preferred)
    {
        if (_desktopCatalog is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要与此入口合并的快捷方式",
            Filter = "快捷方式|*.lnk;*.url",
            InitialDirectory = _shell.DesktopPath,
            CheckFileExists = true,
            DereferenceLinks = false,
        };
        if (dialog.ShowDialog() != true || !ShortcutIdentity.IsShortcut(dialog.FileName) ||
            string.Equals(preferred, dialog.FileName, StringComparison.OrdinalIgnoreCase)) return;
        var key = _desktopCatalog.AliasFor(preferred);
        var alias = _desktopCatalog.AliasFor(dialog.FileName);
        if (key == alias) return;
        _config.ShortcutAliases.Remove(key);
        _config.ShortcutAliases.Remove(DesktopCatalog.PathKey(preferred));
        _config.ShortcutAliases[DesktopCatalog.PathKey(dialog.FileName)] = key;
        _config.ShortcutAliases[alias] = key;
        UpdateDesktopPresentation();
        RequestDesktopScan();
        ScheduleSave();
    }
}
