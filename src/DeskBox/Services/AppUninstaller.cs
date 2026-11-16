using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DeskBox.Services;

internal sealed record AppUninstaller(string FileName, string Arguments)
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly Regex ExecutableEnd = new(@"\.(exe|com|bat|cmd)(?=\s|$)", RegexOptions.IgnoreCase);
    private static readonly HashSet<string> SharedFolders = new(new[]
    {
        Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
        Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86,
        Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.LocalApplicationData,
        Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86,
    }.Select(folder => Environment.GetFolderPath(folder)), PathComparer);

    internal static AppUninstaller? Find(LaunchEntry entry)
    {
        if (!entry.IsShortcut || !ShortcutIcon.IsLink(entry.Path) || !File.Exists(entry.Path)) return null;
        try
        {
            if (ReadMsiShortcut(entry.Path) is { } msi)
                return FindRegistered(null, msi.Product);

            var target = FullPath(entry.ResolveTargetFile() ?? "");
            if (target is null || !Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(target)) return null;

            return FindRegistered(target, null) ??
                (FindAdvertisedProduct(target) is { } product ? FindRegistered(null, product) : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            Log.Warn($"查找卸载程序失败 {entry.Path}：{ex.Message}");
            return null;
        }
    }

    internal void Start()
    {
        using var process = Process.Start(new ProcessStartInfo(FileName, Arguments)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(FileName)!,
        });
    }

    private static AppUninstaller? FindRegistered(string? target, Guid? product)
    {
        var matches = new List<(int Score, string Command, Guid? MsiProduct)>();
        var views = Environment.Is64BitOperatingSystem
            ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : new[] { RegistryView.Registry32 };
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in views)
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(UninstallKey);
            if (uninstall is null) continue;
            foreach (var name in uninstall.GetSubKeyNames())
            {
                var hasProductCode = Guid.TryParseExact(name, "B", out var code);
                if (product.HasValue && (!hasProductCode || product.Value != code)) continue;
                using var app = uninstall.OpenSubKey(name);
                if (app is null || app.GetValue("SystemComponent") is 1 || app.GetValue("NoRemove") is 1 ||
                    app.GetValue("ParentKeyName") is string { Length: > 0 } ||
                    app.GetValue("DisplayName") is not string { Length: > 0 } ||
                    app.GetValue("UninstallString") is not string command || string.IsNullOrWhiteSpace(command))
                    continue;

                Guid? msiProduct = hasProductCode && app.GetValue("WindowsInstaller") is 1 ? code : null;
                var score = target is null ? (msiProduct == product ? 1 : 0) : PathScore(app, target);
                if (score > 0) matches.Add((score, command, msiProduct));
            }
        }
        if (matches.Count == 0) return null;

        // 同一可执行文件或安装目录可能对应多个产品。只有卸载命令唯一时才提供操作。
        var bestScore = matches.Max(match => match.Score);
        var commands = matches.Where(match => match.Score == bestScore)
            .Select(match => match.MsiProduct is { } code
                ? new AppUninstaller(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), $"/x {code:B}")
                : ParseCommand(match.Command))
            .Distinct().ToArray();
        return commands.Length == 1 ? commands[0] : null;
    }

    private static int PathScore(RegistryKey app, string target)
    {
        var icon = app.GetValue("DisplayIcon") as string ?? "";
        var comma = icon.LastIndexOf(',');
        if (comma >= 0 && int.TryParse(icon[(comma + 1)..], out _)) icon = icon[..comma];
        if (PathComparer.Equals(FullPath(icon), target)) return int.MaxValue;

        var folder = FullPath(app.GetValue("InstallLocation") as string ?? "");
        if (folder is null || PathComparer.Equals(folder, Path.GetPathRoot(folder)) || SharedFolders.Contains(folder))
            return 0;
        return target.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? folder.Length : 0;
    }

    private static string? FullPath(string value)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        if (!Path.IsPathFullyQualified(expanded)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded)); }
        catch (ArgumentException) { return null; }
    }

    private static AppUninstaller? ParseCommand(string command)
    {
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        if (command.StartsWith('"'))
        {
            var closingQuote = command.IndexOf('"', 1);
            return closingQuote > 1 ? CreateCommand(command[1..closingQuote], command[(closingQuote + 1)..]) : null;
        }

        // 有些安装程序在注册表里省略了路径引号。保留可执行文件后面的原始参数。
        var end = ExecutableEnd.Match(command);
        if (!end.Success) return null;
        var length = end.Index + end.Length;
        return CreateCommand(command[..length], command[length..]);
    }

    private static AppUninstaller? CreateCommand(string executable, string arguments)
    {
        var path = FullPath(executable);
        if (path is null && Path.GetFileName(executable) == executable)
            path = Path.Combine(Environment.SystemDirectory, executable);
        return File.Exists(path) ? new AppUninstaller(path!, arguments.TrimStart()) : null;
    }

    private static Guid? FindAdvertisedProduct(string target)
    {
        // 普通快捷方式可能指向 MSI 安装的程序。通过开始菜单入口验证目标和产品代码。
        var products = new HashSet<Guid>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };
        foreach (var folder in LauncherCatalog.ProgramsFolders().Where(Directory.Exists))
        foreach (var link in Directory.EnumerateFiles(folder, "*.lnk", options))
        {
            if (ReadMsiShortcut(link) is not { } msi) continue;
            if (PathComparer.Equals(ResolveMsiComponent(msi), target))
                products.Add(msi.Product);
        }
        return products.Count == 1 ? products.Single() : null;
    }

    internal static string? ResolveAdvertisedShortcut(string path) =>
        ReadMsiShortcut(path) is { } msi ? ResolveMsiComponent(msi) : null;

    private static string? ResolveMsiComponent((Guid Product, string Component) msi)
    {
        var path = new StringBuilder(32768);
        var length = (uint)path.Capacity;
        const int installStateLocal = 3;
        return MsiGetComponentPath(msi.Product.ToString("B"), msi.Component, path, ref length) == installStateLocal
            ? FullPath(path.ToString()) : null;
    }

    private static (Guid Product, string Component)? ReadMsiShortcut(string path)
    {
        var product = new StringBuilder(39);
        var component = new StringBuilder(39);
        return MsiGetShortcutTarget(path, product, IntPtr.Zero, component) == 0 &&
            Guid.TryParseExact(product.ToString(), "B", out var code) ? (code, component.ToString()) : null;
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiGetShortcutTarget(string shortcut, StringBuilder product, IntPtr feature,
        StringBuilder component);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern int MsiGetComponentPath(string product, string component, StringBuilder path, ref uint length);
}
