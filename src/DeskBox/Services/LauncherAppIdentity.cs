using System.Text.RegularExpressions;

namespace DeskBox.Services;

internal sealed record LauncherAppIdentity(ShortcutIdentity? Shortcut, string? Target, string Arguments, string? AppId)
{
    private const string AppsFolder = @"shell:AppsFolder\";
    private const string ControlPanel = "::{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}";
    private static readonly Regex LaunchSource = new(@"(?<!\S)--launch-from=\S+(?=\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static LauncherAppIdentity Read(string path)
    {
        var isShell = VirtualGroups.IsShell(path);
        var shortcut = !isShell && ShortcutIdentity.IsShortcut(path) ? ShortcutIdentity.Read(path) : null;
        var target = shortcut?.Target;
        if (!isShell && ShortcutIcon.IsLink(path))
            target = AppUninstaller.ResolveAdvertisedShortcut(path) ?? target;
        else if (!LaunchEntry.IsShortcutFile(path))
            target = path;
        return new(shortcut, target, LaunchSource.Replace(shortcut?.Arguments ?? "", "").Trim(),
            NormalizeAppId(shortcut?.AppId ?? target));
    }

    internal bool Matches(LauncherAppIdentity other)
    {
        if (Shortcut is { } left && other.Shortcut is { } right &&
            (left.Key == right.Key || (left.UpgradeKey is not null && left.UpgradeKey == right.UpgradeKey))) return true;
        if (!string.Equals(Arguments, other.Arguments, StringComparison.Ordinal)) return false;
        return (AppId is not null && string.Equals(AppId, other.AppId, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(Target) && string.Equals(Target, other.Target, StringComparison.OrdinalIgnoreCase));
    }

    internal bool MatchesNamedLauncher(LauncherAppIdentity other)
    {
        if (Target is null || other.Target is null || Arguments != other.Arguments ||
            !Path.IsPathFullyQualified(Target) || !Path.IsPathFullyQualified(other.Target)) return false;
        var targetFolder = Path.GetDirectoryName(Target);
        var otherFolder = Path.GetDirectoryName(other.Target);
        if (targetFolder is null || otherFolder is null) return false;
        return (IsScript(Target) && IsExecutable(other.Target) && IsInFolder(otherFolder, targetFolder)) ||
            (IsScript(other.Target) && IsExecutable(Target) && IsInFolder(targetFolder, otherFolder));
    }

    private static bool IsInFolder(string path, string folder) =>
        string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool IsScript(string path) => Path.GetExtension(path).ToUpperInvariant() is ".BAT" or ".CMD";

    private static bool IsExecutable(string path) => Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeAppId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Equals(ControlPanel, StringComparison.OrdinalIgnoreCase)) return "Microsoft.Windows.ControlPanel";
        if (value.StartsWith(AppsFolder, StringComparison.OrdinalIgnoreCase)) return value[AppsFolder.Length..];
        if (value.StartsWith("::{4234d49b-0245-4df3-b780-3893943456e1}\\", StringComparison.OrdinalIgnoreCase))
            return value[(value.IndexOf('\\') + 1)..];
        return Path.IsPathFullyQualified(value) || VirtualGroups.IsShell(value) ? null : value;
    }
}
