using System.IO;
using DeskBox.Interop;

namespace DeskBox.Services;

/// <summary>
/// 自愈：盒子里已经存在指向 shell 命名空间项（回收站 / 控制面板 / 网络 / 此电脑…）的 .lnk 时，
/// 把桌面上对应的那个系统图标隐藏掉，保证"同一个东西只出现在一个地方"。
///
/// 拖进来的那一刻 BoxWindow 已经会隐藏桌面那一份；这里补的是另外两种情况：
/// 一是这个 .lnk 是旧版本（还没有隐藏逻辑时）放进盒子的，
/// 二是注册表已隐藏，但上一次运行没有刷新桌面视图。
///
/// 全部在后台线程做：读 .lnk 目标要过 COM，不能在 UI 线程上堵着。
/// </summary>
public static class SystemIconGuard
{
    private static readonly HashSet<string> Checked = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Owned = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Released = new(StringComparer.OrdinalIgnoreCase);
    private static int _reconciling;
    private static long _nextReconcile;
    internal static bool SuppressChecks { get; set; }

    internal static void RememberHandled(IEnumerable<string> paths, IEnumerable<string> names)
    {
        lock (Gate)
            foreach (var (path, name) in paths.Zip(names))
            {
                var key = BuildKey(path);
                Checked.Add(key);
                Released.Remove(key);
                Owned[path] = name;
            }
    }

    internal static bool Restore(string path, string name)
    {
        lock (Gate)
        {
            Owned.Remove(path);
            Released.Add(BuildKey(path));
            return DesktopSystemIcon.SetHidden(name, false);
        }
    }

    /// <summary>只读核对实际视图；发现已归盒的系统项重新出现时，才发送逐项移出通知。</summary>
    internal static void Reconcile()
    {
        if (SuppressChecks || Environment.TickCount64 < Interlocked.Read(ref _nextReconcile)) return;
        lock (Gate) { if (Owned.Count == 0) return; }
        if (Interlocked.Exchange(ref _reconciling, 1) != 0) return;
        Interlocked.Exchange(ref _nextReconcile, Environment.TickCount64 + 1500);
        _ = Task.Run(() =>
        {
            try
            {
                var items = DesktopViewInterop.ReadItems();
                if (items is null) return;
                var visible = items.Select(name => DesktopSystemIcon.TryGetClsid(name, out var id) ? id : "")
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                lock (Gate)
                {
                    var names = Owned.Where(pair => File.Exists(pair.Key) &&
                        !Released.Contains(BuildKey(pair.Key)) && DesktopSystemIcon.IsHidden(pair.Value) &&
                        DesktopSystemIcon.TryGetClsid(pair.Value, out var id) && visible.Contains(id))
                        .Select(pair => pair.Value).ToList();
                    if (names.Count > 0) DesktopSystemIcon.SetHidden(names, true);
                }
            }
            catch (Exception ex) { Log.Warn("核对桌面系统图标失败：" + ex.Message); }
            finally { Interlocked.Exchange(ref _reconciling, 0); }
        });
    }

    /// <summary>把这一批盒子条目里还没检查过的快捷方式挑出来，后台隐藏桌面那一份。</summary>
    public static void EnsureHidden(IEnumerable<string> paths)
    {
        if (paths is null || SuppressChecks)
        {
            return;
        }

        var candidates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                !path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 同一个文件只核对一次；用"路径|修改时间"做键，快捷方式被换掉了还会再核对一次
            var key = BuildKey(path);
            lock (Gate)
            {
                if (!Checked.Add(key))
                {
                    continue;
                }
            }

            candidates.Add(path, key);
        }

        if (candidates.Count == 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, key) in candidates)
            {
                try
                {
                    if (!File.Exists(path) || !ShortcutFactory.TryGetTargetParsingName(path, out var name))
                    {
                        lock (Gate) Checked.Remove(key);
                        continue;
                    }
                    if (DesktopSystemIcon.TryGetClsid(name, out _)) resolved[path] = name;
                }
                catch (Exception ex)
                {
                    lock (Gate) Checked.Remove(key);
                    Log.Warn($"系统图标自愈失败 {path}: {ex.Message}");
                }
            }

            // 即便注册表已经是 1，也要刷新一次：设置写入不等于 Explorer 已经移除了旧图标。
            lock (Gate)
            {
                foreach (var (path, name) in resolved)
                    if (File.Exists(path) && !Released.Contains(candidates[path])) Owned[path] = name;
                var names = resolved.Where(pair => Owned.ContainsKey(pair.Key) &&
                    !Released.Contains(candidates[pair.Key])).Select(pair => pair.Value).ToList();
                if (names.Count > 0 && !DesktopSystemIcon.SetHidden(names, true))
                    foreach (var key in candidates.Values) Checked.Remove(key);
            }
        });
    }

    private static string BuildKey(string path)
    {
        try
        {
            return $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}";
        }
        catch
        {
            return path;
        }
    }

}
