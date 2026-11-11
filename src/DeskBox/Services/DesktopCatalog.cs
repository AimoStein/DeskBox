using System.IO;
using DeskBox.Interop;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>从同一份桌面清单生成盒子与桌面的互斥显示结果。</summary>
internal sealed class DesktopCatalog
{
    private readonly IReadOnlyList<DesktopViewInterop.Item> _desktop;
    private readonly Dictionary<string, ShortcutIdentity?> _shortcuts;

    private DesktopCatalog(IReadOnlyList<DesktopViewInterop.Item> desktop,
        Dictionary<string, ShortcutIdentity?> shortcuts)
    {
        _desktop = desktop;
        _shortcuts = shortcuts;
    }

    internal sealed record Presentation(
        HashSet<(string Box, string Path)> Hidden,
        IReadOnlyList<DesktopViewInterop.Item> Desktop,
        bool ConfigChanged);

    internal static DesktopCatalog Scan(DesktopShell shell, string root, bool includeHidden,
        IReadOnlyList<string> grouped, DesktopCatalog? previous)
    {
        var paths = shell.EnumerateItems(root, includeHidden);
        var layout = (NativeMethods.FindDesktopDefView() == IntPtr.Zero ? null : DesktopViewInterop.ReadLayout())
            ?? previous?._desktop ?? Array.Empty<DesktopViewInterop.Item>();
        var desktop = layout.DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        var items = paths.Select(path =>
        {
            var shortcut = ShortcutIdentity.IsShortcut(path);
            var item = desktop.GetValueOrDefault(path) ?? new DesktopViewInterop.Item(path,
                shortcut ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path));
            return shortcut ? item with { Revision = File.GetLastWriteTimeUtc(path).Ticks } : item;
        }).ToList();
        items.AddRange(desktop.Values.Where(item => VirtualGroups.IsShell(item.Path)));

        var shortcuts = new Dictionary<string, ShortcutIdentity?>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Concat(grouped).Distinct(StringComparer.OrdinalIgnoreCase))
            if (ShortcutIdentity.IsShortcut(path) && File.Exists(path))
                shortcuts[path] = ShortcutIdentity.Read(path);
        return new DesktopCatalog(items, shortcuts);
    }

    internal string AliasFor(string path) =>
        _shortcuts.GetValueOrDefault(path) is { } identity ? LogicalKey(identity.Key, identity.UpgradeKey) : PathKey(path);

    internal bool HasReadShortcut(string path) => _shortcuts.ContainsKey(path);

    internal Presentation Reconcile(AppConfig config)
    {
        // 先汇总同一条命令的已知稳定 ID。旧快捷方式可能没有写 AppUserModelID。
        var identityAliases = _shortcuts.Values.Where(identity => identity?.UpgradeKey is not null)
            .Select(identity => identity!).GroupBy(identity => identity.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().UpgradeKey!, StringComparer.Ordinal);
        string Match(string identity, string? upgrade) => identityAliases.GetValueOrDefault(identity) ?? LogicalKey(identity, upgrade);
        var candidates = new List<(string Path, string Key, BoxConfig? Box, GroupEntry? Entry)>();
        var changed = false;
        foreach (var box in config.Boxes.Where(box => box.IsVirtual))
        {
            foreach (var entry in box.Entries)
            {
                if (_shortcuts.GetValueOrDefault(entry.Path) is { } resolved &&
                    (entry.ShortcutIdentity != resolved.Key || entry.ShortcutUpgradeIdentity != resolved.UpgradeKey))
                {
                    entry.ShortcutIdentity = resolved.Key;
                    entry.ShortcutUpgradeIdentity = resolved.UpgradeKey;
                    changed = true;
                }
                var key = entry.ShortcutIdentity is { } identity
                    ? Match(identity, entry.ShortcutUpgradeIdentity) : PathKey(entry.Path);
                candidates.Add((entry.Path, FollowAlias(config, entry.Path, key), box, entry));
            }
        }
        foreach (var item in _desktop)
        {
            var key = _shortcuts.GetValueOrDefault(item.Path) is { } identity
                ? Match(identity.Key, identity.UpgradeKey) : PathKey(item.Path);
            candidates.Add((item.Path, FollowAlias(config, item.Path, key), null, null));
        }

        var hidden = new HashSet<(string Box, string Path)>();
        var desktopPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in candidates.GroupBy(item => item.Key, StringComparer.Ordinal))
        {
            var entries = group.Where(item => item.Box is not null).ToList();
            var winner = entries.FirstOrDefault();
            var preferred = group.FirstOrDefault(item => Launchable(item.Path));
            if (winner.Entry is null || winner.Box is null)
            {
                desktopPaths.Add(preferred.Path ?? group.First().Path);
                continue;
            }

            var keepPath = winner.Path;
            if (ShortcutIdentity.IsShortcut(keepPath) && !Launchable(keepPath) && preferred.Path is { } replacement)
            {
                if (winner.Box.Entries.All(item => !SamePath(item.Path, replacement)))
                {
                    var title = string.IsNullOrEmpty(winner.Entry.DisplayName)
                        ? Path.GetFileNameWithoutExtension(keepPath) : winner.Entry.DisplayName;
                    VirtualGroups.RenameReference(config, keepPath, replacement);
                    winner.Entry.DisplayName = title;
                    if (_shortcuts.GetValueOrDefault(replacement) is { } identity)
                    {
                        winner.Entry.ShortcutIdentity = identity.Key;
                        winner.Entry.ShortcutUpgradeIdentity = identity.UpgradeKey;
                    }
                    keepPath = replacement;
                    changed = true;
                }
                else if (preferred.Entry is not null && preferred.Box == winner.Box)
                {
                    winner = preferred;
                    keepPath = winner.Path;
                }
            }
            foreach (var alias in entries)
                if (alias.Entry != winner.Entry && (alias.Box != winner.Box || !SamePath(alias.Entry!.Path, keepPath)))
                    hidden.Add((alias.Box!.Id, alias.Entry!.Path));
        }

        return new Presentation(hidden, _desktop.Where(item => desktopPaths.Contains(item.Path)).ToList(), changed);
    }

    private bool Launchable(string path) => !ShortcutIdentity.IsShortcut(path) ||
        (_shortcuts.TryGetValue(path, out var identity) && identity?.CanLaunch == true);

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string LogicalKey(string identity, string? upgrade) => upgrade ?? identity;

    internal static string PathKey(string path) => "path:" + path.ToUpperInvariant();

    private static string FollowAlias(AppConfig config, string path, string key)
    {
        if (config.ShortcutAliases.TryGetValue(PathKey(path), out var mapped)) key = mapped;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (visited.Add(key) && config.ShortcutAliases.TryGetValue(key, out var target))
            key = target;
        return key;
    }
}
