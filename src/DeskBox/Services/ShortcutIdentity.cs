using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskBox.Interop;

namespace DeskBox.Services;

internal sealed record ShortcutIdentity(string Key, string? UpgradeKey, bool CanLaunch)
{
    private static readonly Regex VersionDirectory = new(
        @"(?<=\\)(?:APP-)?[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?(?=\\|$)",
        RegexOptions.CultureInvariant);

    internal static bool IsShortcut(string path) => Path.GetExtension(path).ToUpperInvariant() is ".LNK" or ".URL";

    internal static ShortcutIdentity? Read(string path)
    {
        try
        {
            if (Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase))
                return ReadUrl(path);

            using var com = ShellItemInterop.EnsureComInitialized();
            var link = ShellItemInterop.CreateShellLink();
            try
            {
                if (((ShellItemInterop.IPersistFile)link).Load(path, 0) != 0) return null;
                var target = new StringBuilder(32768);
                var args = new StringBuilder(32768);
                var directory = new StringBuilder(32768);
                link.GetPath(target, target.Capacity, IntPtr.Zero, 4); // SLGP_RAWPATH，仅读取。
                if (link.GetArguments(args, args.Capacity) < 0 ||
                    link.GetWorkingDirectory(directory, directory.Capacity) < 0)
                    return null;

                var command = NormalizePath(target.ToString());
                var workingDirectory = NormalizePath(directory.ToString());
                var appId = ReadAppId(link);
                if (command.Length == 0)
                {
                    if (link.GetIDList(out var pidl) != 0 || pidl == IntPtr.Zero) return null;
                    try
                    {
                        command = ShellItemInterop.GetName(pidl, ShellItemInterop.SIGDN.DESKTOPABSOLUTEPARSING) ?? "";
                    }
                    finally { ShellItemInterop.FreePidl(pidl); }
                }
                if (command.Length == 0 && appId is null) return null;

                var launchable = command.Length == 0 || !Path.IsPathFullyQualified(command) ||
                    File.Exists(command) || Directory.Exists(command);
                var key = JsonSerializer.Serialize(new[] { "link", command, args.ToString(), workingDirectory });
                var upgradeKey = appId is null
                    ? VersionedKey(command, args.ToString(), workingDirectory)
                    : JsonSerializer.Serialize(new[] { "app", appId, args.ToString(), VersionDirectory.Replace(workingDirectory, "{VERSION}") });
                return new(key, upgradeKey, launchable);
            }
            finally { Marshal.ReleaseComObject(link); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException)
        {
            return null;
        }
    }

    private static ShortcutIdentity? ReadUrl(string path)
    {
        var inSection = false;
        foreach (var line in File.ReadLines(path))
        {
            var text = line.Trim();
            if (text.StartsWith('['))
                inSection = text.Equals("[InternetShortcut]", StringComparison.OrdinalIgnoreCase);
            else if (inSection && text.StartsWith("URL=", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(text[4..].Trim(), UriKind.Absolute, out var uri))
                return new(JsonSerializer.Serialize(new[] { "url", uri.AbsoluteUri }), null, true);
        }
        return null;
    }

    private static string NormalizePath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        return Path.IsPathFullyQualified(expanded)
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded)).ToUpperInvariant()
            : expanded;
    }

    private static string? VersionedKey(string command, string arguments, string directory)
    {
        var versionless = VersionDirectory.Replace(command, "{VERSION}");
        return versionless == command ? null :
            JsonSerializer.Serialize(new[] { "version", versionless, arguments, VersionDirectory.Replace(directory, "{VERSION}") });
    }

    private static string? ReadAppId(ShellItemInterop.IShellLinkW link)
    {
        if (link is not IPropertyStore store) return null;
        var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
        if (store.GetValue(ref key, out var value) < 0) return null;
        try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null; }
        finally { PropVariantClear(ref value); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid Format;
        public uint Id;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropertyValue
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropertyValue value);
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropertyValue value);
}
