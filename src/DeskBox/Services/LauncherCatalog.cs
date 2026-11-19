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

    internal static string? MissingLinkRewriteSource(string path)
    {
        var marker = path.LastIndexOf(".lnk~RF", StringComparison.OrdinalIgnoreCase);
        return marker >= 0 && path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
            !File.Exists(path) ? path[..(marker + 4)] : null;
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
    internal static string[] ProgramsFolders() => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
    }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static Task<LauncherCatalog> ScanAsync(DesktopShell shell, string excluded, bool showHidden)
    {
        var result = new TaskCompletionSource<LauncherCatalog>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { result.SetResult(Scan(shell, excluded, showHidden)); }
            catch (Exception ex) { result.SetException(ex); }
        }) { IsBackground = true, Name = "DeskBox Launcher Catalog" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    private static LauncherCatalog Scan(DesktopShell shell, string excluded, bool showHidden)
    {
        var desktop = shell.EnumerateItems(excluded, showHidden).Select(path => LaunchEntry.FromPath(path)).ToList();
        if (DesktopViewInterop.ReadLayout() is { } layout)
            desktop.AddRange(layout.Where(item => VirtualGroups.IsShell(item.Path))
                .Select(item => LaunchEntry.FromPath(item.Path, item.Name)));

        var installed = ProgramsFolders().Where(Directory.Exists).SelectMany(path => Directory.EnumerateFiles(path, "*",
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint }))
            .Where(LaunchEntry.IsShortcutFile).Select(path => LaunchEntry.FromPath(path)).ToList();
        var shortcutNames = installed.Select(item => item.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        installed.AddRange(ReadAppsFolder().Where(item => !shortcutNames.Contains(item.Name)));
        return new LauncherCatalog(desktop.DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            installed.DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray());
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

    internal LauncherCatalog WithoutPath(string path) =>
        new(Desktop.Where(item => !string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)).ToArray(),
            Installed.Where(item => !string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)).ToArray());

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
                : ReadLegacyBox(box, config);
            assigned.UnionWith(entries.Select(entry => entry.Path));
            // An interrupted .lnk rewrite can leave a vanished .lnk~RF*.TMP
            // beside the real shortcut. Keep genuine entries, including broken links.
            var groupPaths = entries.Select(entry => entry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            entries.RemoveAll(entry => LaunchEntry.MissingLinkRewriteSource(entry.Path) is { } source &&
                groupPaths.Contains(source) && File.Exists(source));
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
            OrderByUsage(items.Select(item => WithDisplayName(item, config)), config).ToArray();
    }

    private static List<LaunchEntry> ReadLegacyBox(BoxConfig box, AppConfig config)
    {
        var folder = string.IsNullOrWhiteSpace(box.Folder)
            ? Path.Combine(config.EffectiveBoxRoot, FsUtil.Sanitize(box.Title)) : box.Folder;
        try
        {
            return Directory.EnumerateFileSystemEntries(folder)
                .Where(path => !FsUtil.IsHiddenOrSystem(path)).Select(path => LaunchEntry.FromPath(path)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(); }
    }
}

internal sealed record LaunchSection(BoxConfig? Group, string Name, IReadOnlyList<LaunchEntry> Items);
