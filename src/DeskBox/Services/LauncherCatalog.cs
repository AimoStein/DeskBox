using System.Globalization;
using System.Runtime.InteropServices;
using DeskBox.Interop;
using DeskBox.Models;

namespace DeskBox.Services;

internal sealed record LaunchEntry(string Path, string Name)
{
    internal string OriginalName { get; init; } = Name;

    internal static LaunchEntry FromPath(string path, string? label = null) =>
        new(path, string.IsNullOrWhiteSpace(label) ? NameOf(path) : label);

    internal bool IsShortcut => !VirtualGroups.IsShell(Path) && IsShortcutFile(Path);

    internal string? ResolveTargetFile()
    {
        if (!IsShortcut || !ShortcutIcon.IsLink(Path) ||
            !ShortcutFactory.TryGetTargetParsingName(Path, out var target)) return null;
        target = Environment.ExpandEnvironmentVariables(target);
        return System.IO.Path.IsPathFullyQualified(target) && FsUtil.PathExists(target) ? target : null;
    }

    internal bool TryRecycleShortcut(IntPtr owner) =>
        IsShortcut && System.IO.Path.IsPathFullyQualified(Path) && File.Exists(Path) &&
        NativeMethods.DeleteToRecycleBin(Path, owner, warnIfPermanent: true);

    internal static string? LinkRewriteSource(string path)
    {
        if (VirtualGroups.IsShell(path) || !System.IO.Path.IsPathFullyQualified(path) ||
            !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return null;
        var name = System.IO.Path.GetFileName(path);
        var marker = name.LastIndexOf(".lnk~RF", StringComparison.OrdinalIgnoreCase);
        const int markerLength = 7, extensionLength = 4;
        if (marker <= 0 || marker + markerLength >= name.Length - extensionLength) return null;
        foreach (var character in name.AsSpan((marker + markerLength)..^extensionLength))
            if (!char.IsAsciiHexDigit(character)) return null;
        return path[..(path.Length - name.Length + marker + 4)];
    }

    private static string NameOf(string path)
    {
        if (VirtualGroups.IsShell(path)) return path;
        var name = System.IO.Path.GetFileName(PathWithoutTrailingSeparator(path));
        return IsShortcutFile(path) ? System.IO.Path.GetFileNameWithoutExtension(name) : name;
    }

    private static string PathWithoutTrailingSeparator(string path) => System.IO.Path.TrimEndingDirectorySeparator(path);

    internal static bool IsShortcutFile(string path) =>
        System.IO.Path.GetExtension(path).ToUpperInvariant() is ".LNK" or ".URL" or ".APPREF-MS";
}

internal sealed record LauncherCatalog(IReadOnlyList<LaunchEntry> Desktop, IReadOnlyList<LaunchEntry> Installed)
{
    internal IReadOnlyDictionary<string, LauncherShortcutState> UnavailableShortcuts { get; init; } =
        new Dictionary<string, LauncherShortcutState>(StringComparer.OrdinalIgnoreCase);

    internal static string[] ProgramsFolders() => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
    }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static Task<LauncherCatalog> ScanAsync(DesktopShell shell, string excluded, bool showHidden,
        IReadOnlyList<string> references, IReadOnlyList<string> legacyFolders)
    {
        var result = new TaskCompletionSource<LauncherCatalog>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { result.SetResult(Scan(shell, excluded, showHidden, references, legacyFolders)); }
            catch (Exception ex) { result.SetException(ex); }
        }) { IsBackground = true, Name = "DeskBox Launcher Catalog" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    private static LauncherCatalog Scan(DesktopShell shell, string excluded, bool showHidden,
        IReadOnlyList<string> references, IReadOnlyList<string> legacyFolders)
    {
        var desktop = shell.EnumerateItems(excluded, showHidden).Select(path => LaunchEntry.FromPath(path)).ToList();
        if (DesktopViewInterop.ReadLayout() is { } layout)
            desktop.AddRange(layout.Where(item => VirtualGroups.IsShell(item.Path))
                .Select(item => LaunchEntry.FromPath(item.Path, item.Name)));

        var installed = ProgramsFolders().Where(Directory.Exists).SelectMany(path => Directory.EnumerateFiles(path, "*",
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint }))
            .Where(LaunchEntry.IsShortcutFile).Select(path => LaunchEntry.FromPath(path)).ToList();
        var unavailable = FindUnavailableShortcuts(desktop.Concat(installed).Select(item => item.Path)
            .Concat(references).Concat(references.Select(LaunchEntry.LinkRewriteSource).OfType<string>())
            .Concat(legacyFolders.SelectMany(ReadLegacyBox).Select(item => item.Path)));
        var shortcutNames = installed.Where(item => !unavailable.ContainsKey(item.Path))
            .Select(item => item.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        installed.AddRange(ReadAppsFolder().Where(item => !shortcutNames.Contains(item.Name)));
        return new LauncherCatalog(desktop.DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            installed.DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray())
            { UnavailableShortcuts = unavailable };
    }

    internal static IReadOnlyDictionary<string, LauncherShortcutState> FindUnavailableShortcuts(IEnumerable<string> paths)
    {
        var unavailable = new Dictionary<string, LauncherShortcutState>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var state = LauncherShortcutCheck.Read(path);
            if (state != LauncherShortcutState.Unverified) unavailable.Add(path, state);
        }
        return unavailable;
    }

    private static IReadOnlyList<LaunchEntry> ReadAppsFolder()
    {
        var result = new List<LaunchEntry>();
        object? shell = null, folder = null, items = null;
        try
        {
            if (Type.GetTypeFromProgID("Shell.Application") is not { } type) return result;
            shell = Activator.CreateInstance(type);
            if (shell is null) return result;
            folder = ((dynamic)shell).NameSpace("shell:AppsFolder");
            if (folder is null) return result;
            items = ((dynamic)folder).Items();
            if (items is null) return result;
            var count = (int)((dynamic)items).Count;
            for (var i = 0; i < count; i++)
            {
                object? item = ((dynamic)items).Item(i);
                if (item is null) continue;
                try
                {
                    string name = ((dynamic)item).Name;
                    string id = ((dynamic)item).Path;
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
                    var path = Path.IsPathFullyQualified(id) && File.Exists(id) ? id : "shell:AppsFolder\\" + id;
                    result.Add(new LaunchEntry(path, name));
                }
                finally { Release(item); }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取 Windows 应用列表失败: " + ex.Message);
        }
        finally
        {
            Release(items);
            Release(folder);
            Release(shell);
        }
        return result;
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    internal LauncherCatalog WithoutPath(string path) => this with
    {
        Desktop = Desktop.Where(item => !string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)).ToArray(),
        Installed = Installed.Where(item => !string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)).ToArray(),
    };

    internal static bool RenameReference(AppConfig config, string oldPath, string newPath) =>
        LaunchEntry.LinkRewriteSource(oldPath) is null && LaunchEntry.LinkRewriteSource(newPath) is null &&
        VirtualGroups.RenameReference(config, oldPath, newPath);

    internal bool RestoreRewrittenShortcuts(AppConfig config)
    {
        var boxes = config.Boxes.Where(box => box.IsVirtual).ToArray();
        var repairs = VirtualGroups.References(config).Select(entry => entry.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (Path: path, Source: LaunchEntry.LinkRewriteSource(path)))
            .Where(item => item.Source is not null)
            .GroupBy(item => item.Source!, StringComparer.OrdinalIgnoreCase);
        var restored = 0;
        foreach (var repair in repairs)
        {
            var source = repair.Key;
            if (UnavailableShortcuts.ContainsKey(source) || !File.Exists(source)) continue;

            var paths = repair.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            paths.Add(source);
            // Prefer an existing placement of the real shortcut, then the first grouped reference.
            var grouped = boxes.SelectMany(box => box.Entries).Where(entry => paths.Contains(entry.Path)).ToArray();
            var winner = grouped.FirstOrDefault(entry => SamePath(entry.Path, source)) ?? grouped.FirstOrDefault() ??
                config.LauncherUngroupedEntries.FirstOrDefault(entry => SamePath(entry.Path, source)) ??
                config.LauncherUngroupedEntries.First(entry => paths.Contains(entry.Path));

            foreach (var item in repair)
                VirtualGroups.RenameReference(config, item.Path, source);

            foreach (var box in boxes)
            {
                RemoveDuplicates(box.Entries);
                var keepPosition = box.Entries.Contains(winner);
                box.ItemOrder.RemoveAll(path =>
                {
                    if (!SamePath(path, source)) return false;
                    if (!keepPosition) return true;
                    keepPosition = false;
                    return false;
                });
            }
            RemoveDuplicates(config.LauncherUngroupedEntries);
            restored++;

            void RemoveDuplicates(List<GroupEntry> entries) => entries.RemoveAll(entry =>
            {
                if (entry == winner || !SamePath(entry.Path, source)) return false;
                if (string.IsNullOrWhiteSpace(winner.DisplayName)) winner.DisplayName = entry.DisplayName;
                winner.ShortcutIdentity ??= entry.ShortcutIdentity;
                winner.ShortcutUpgradeIdentity ??= entry.ShortcutUpgradeIdentity;
                return true;
            });
        }
        if (restored > 0) Log.Info($"开始面板：恢复了 {restored} 个更新时被记录为临时文件的快捷方式。");
        return restored > 0;
    }

    private static bool SamePath(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    internal bool PruneMissingReferences(AppConfig config, LauncherCatalog previous)
    {
        // Require two scans so an Explorer rewrite or brief move doesn't drop a saved reference.
        var missing = VirtualGroups.References(config).Select(entry => entry.Path)
            .Where(path => UnavailableShortcuts.GetValueOrDefault(path) == LauncherShortcutState.MissingFile &&
                previous.UnavailableShortcuts.GetValueOrDefault(path) == LauncherShortcutState.MissingFile)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (missing.Count == 0) return false;

        foreach (var box in config.Boxes.Where(box => box.IsVirtual))
            VirtualGroups.Remove(box, missing);
        config.LauncherUngroupedEntries.RemoveAll(entry => missing.Contains(entry.Path));
        foreach (var path in missing)
        {
            config.LauncherOpenCounts.Remove(path);
            config.LauncherDisplayNames.Remove(path);
        }
        Log.Info($"开始面板：清理了 {missing.Count} 个已不存在的快捷方式引用。");
        return true;
    }

    internal static IOrderedEnumerable<LaunchEntry> OrderByUsage(IEnumerable<LaunchEntry> items, AppConfig config) =>
        items.OrderByDescending(item => config.LauncherOpenCounts.GetValueOrDefault(item.Path))
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase);

    internal static LaunchEntry[] Search(IEnumerable<LaunchEntry> items, string query, AppConfig config)
    {
        query = query.Trim();
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        var compactQuery = WithoutSeparators(query);
        return items.DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(item => (Item: item, Match: MatchName(item.Name, query, compactQuery, compare)))
            .Where(result => result.Match != NameMatch.None)
            .GroupBy(result => result.Match)
            .OrderByDescending(group => group.Key)
            .SelectMany(group => OrderByUsage(group.Select(result => result.Item), config))
            .ToArray();
    }

    private enum NameMatch { None, Initials, Contains, Prefix, CompactExact, Exact }

    private static NameMatch MatchName(string name, string query, string compactQuery, CompareInfo compare)
    {
        if (query.Length == 0) return NameMatch.None;

        const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth;
        if (compare.Compare(name, query, options) == 0) return NameMatch.Exact;

        // Microsoft Pinyin can insert apostrophes between uncommitted syllables, e.g. b'l.
        var compactName = WithoutSeparators(name);
        if (compactQuery.Length > 0 && compare.Compare(compactName, compactQuery, options) == 0)
            return NameMatch.CompactExact;
        if (compare.IsPrefix(name, query, options) ||
            (compactQuery.Length > 0 && compare.IsPrefix(compactName, compactQuery, options))) return NameMatch.Prefix;
        if (compare.IndexOf(name, query, options) >= 0 ||
            (compactQuery.Length > 0 && compare.IndexOf(compactName, compactQuery, options) >= 0)) return NameMatch.Contains;
        if (compactQuery.Length < 2) return NameMatch.None;

        var initials = new string(name.Where((character, index) => char.IsLetterOrDigit(character) &&
            (index == 0 || !char.IsLetterOrDigit(name[index - 1]) ||
                (char.IsUpper(character) && char.IsLower(name[index - 1])))).ToArray());
        return compare.IsPrefix(initials, compactQuery, options) ? NameMatch.Initials : NameMatch.None;
    }

    private static string WithoutSeparators(string text) => text.Any(IsSearchSeparator)
        ? new string(text.Where(character => !IsSearchSeparator(character)).ToArray()) : text;

    private static bool IsSearchSeparator(char character) => char.IsWhiteSpace(character) ||
        character is '\'' or '‘' or '’' or 'ʼ' or '＇' or '·' ||
        char.GetUnicodeCategory(character) is UnicodeCategory.DashPunctuation or UnicodeCategory.ConnectorPunctuation;

    private static LaunchEntry WithDisplayName(LaunchEntry item, AppConfig config) =>
        config.LauncherDisplayNames.TryGetValue(item.Path, out var name) ? item with { Name = name } : item;

    internal static IReadOnlyList<LaunchEntry> ExcludeGroupedApps(IReadOnlyList<LaunchEntry> apps,
        IEnumerable<LaunchSection> sections)
    {
        var grouped = sections.Where(section => section.Group is not null).SelectMany(section => section.Items)
            .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (grouped.Length == 0) return apps;

        var groupedPaths = grouped.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var identities = grouped.Select(item => (item.OriginalName, Identity: LauncherAppIdentity.Read(item.Path))).ToArray();
        return apps.Where(app =>
        {
            if (groupedPaths.Contains(app.Path)) return false;
            var identity = LauncherAppIdentity.Read(app.Path);
            return !identities.Any(group => group.Identity.Matches(identity) ||
                (string.Equals(group.OriginalName, app.OriginalName, StringComparison.CurrentCultureIgnoreCase) &&
                    group.Identity.MatchesNamedLauncher(identity)));
        }).ToArray();
    }

    internal IReadOnlyList<LaunchSection> Sections(AppConfig config)
    {
        var sections = new List<LaunchSection>();
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var box in config.Boxes)
        {
            var entries = box.IsVirtual
                ? box.Entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
                    .Select(entry => LaunchEntry.FromPath(entry.Path, entry.DisplayName)).ToList()
                : ReadLegacyBox(LegacyBoxFolder(box, config));
            assigned.UnionWith(entries.Select(entry => entry.Path));
            sections.Add(new LaunchSection(box, box.Title, SortEntries(entries)));
        }
        var ungrouped = Desktop.Concat(config.LauncherUngroupedEntries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
                .Select(entry => LaunchEntry.FromPath(entry.Path, entry.DisplayName)))
            .Where(item => !assigned.Contains(item.Path))
            .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase);
        sections.Add(new LaunchSection(null, "未分组", SortEntries(ungrouped)));
        sections.Add(new LaunchSection(null, "所有应用", SortEntries(Installed)));
        return sections;

        LaunchEntry[] SortEntries(IEnumerable<LaunchEntry> items) =>
            OrderByUsage(items.Where(item => LaunchEntry.LinkRewriteSource(item.Path) is null &&
                    !UnavailableShortcuts.ContainsKey(item.Path))
                .Select(item => WithDisplayName(item, config)), config).ToArray();
    }

    internal static string LegacyBoxFolder(BoxConfig box, AppConfig config) => string.IsNullOrWhiteSpace(box.Folder)
        ? Path.Combine(config.EffectiveBoxRoot, FsUtil.Sanitize(box.Title)) : box.Folder;

    private static List<LaunchEntry> ReadLegacyBox(string folder)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(folder)
                .Where(path => !FsUtil.IsHiddenOrSystem(path)).Select(path => LaunchEntry.FromPath(path)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(); }
    }
}

internal sealed record LaunchSection(BoxConfig? Group, string Name, IReadOnlyList<LaunchEntry> Items);
