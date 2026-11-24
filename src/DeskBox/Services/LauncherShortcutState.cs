using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using DeskBox.Interop;

namespace DeskBox.Services;

internal enum LauncherShortcutState { Unverified, MissingFile, Invalid }

internal static class LauncherShortcutCheck
{
    private static readonly Guid ShellLinkClass = new("00021401-0000-0000-C000-000000000046");
    private const FileAttributes DeferredFile = FileAttributes.Offline | FileAttributes.ReparsePoint;

    internal static LauncherShortcutState Read(string path)
    {
        try
        {
            if (!LaunchEntry.IsShortcutFile(path) || VirtualGroups.IsShell(path) || !IsOnReadyFixedDrive(path))
                return LauncherShortcutState.Unverified;

            var attributes = ReadLocalAttributes(path);
            if (attributes is null) return LauncherShortcutState.MissingFile;
            if ((attributes & (FileAttributes.Directory | DeferredFile)) != 0)
                return LauncherShortcutState.Unverified;

            var invalid = Path.GetExtension(path).ToUpperInvariant() switch
            {
                ".URL" => ShortcutIdentity.ReadInternetAddress(path) is not { } address ||
                    (address.IsFile && IsMissingLocalPath(address.LocalPath)),
                ".LNK" => IsInvalidShellLink(path),
                _ => false,
            };
            return invalid ? LauncherShortcutState.Invalid : LauncherShortcutState.Unverified;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException)
        {
            return LauncherShortcutState.Unverified;
        }
    }

    private static bool IsInvalidShellLink(string path)
    {
        const int headerSize = 0x4c;
        const uint hasDarwinId = 0x1000, hasTargetIdList = 1, hasLinkInfo = 2, hasRelativePath = 8, hasExpString = 0x0200;
        const uint rawPath = 4, noUi = 1, noUpdate = 8, resolveTimeoutMs = 200;
        Span<byte> header = stackalloc byte[headerSize];
        using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.ReadAtLeast(header, headerSize, throwOnEndOfStream: false) != headerSize ||
                BinaryPrimitives.ReadUInt32LittleEndian(header) != headerSize ||
                new Guid(header.Slice(4, 16)) != ShellLinkClass) return true;
        }
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        if ((flags & hasDarwinId) != 0) return false; // Windows Installer advertised shortcut.

        using var com = ShellItemInterop.EnsureComInitialized();
        var link = ShellItemInterop.CreateShellLink();
        try
        {
            if (((ShellItemInterop.IPersistFile)link).Load(path, 0) != ShellItemInterop.S_OK) return false;
            var target = new StringBuilder(32768);
            if (link.GetPath(target, target.Capacity, IntPtr.Zero, rawPath) != ShellItemInterop.S_OK || target.Length == 0)
                return (flags & (hasTargetIdList | hasLinkInfo | hasRelativePath | hasExpString)) == 0;

            if (!IsMissingLocalPath(Environment.ExpandEnvironmentVariables(target.ToString()))) return false;
            // Explorer may still be able to find a moved target. Resolve without prompts or rewriting the link.
            return link.Resolve(IntPtr.Zero, (resolveTimeoutMs << 16) | noUi | noUpdate) != ShellItemInterop.S_OK;
        }
        finally { Marshal.ReleaseComObject(link); }
    }

    private static bool IsMissingLocalPath(string path) =>
        IsOnReadyFixedDrive(path) && ReadLocalAttributes(path) is null;

    // Null means absent on a local disk. Offline is also used for paths whose absence cannot be verified.
    private static FileAttributes? ReadLocalAttributes(string path)
    {
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        try
        {
            return File.GetAttributes(parent);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }

        if (path.Contains('%')) return FileAttributes.Offline;
        // A missing child under a redirected folder may only be temporarily inaccessible.
        while ((parent = Path.GetDirectoryName(parent)) is not null)
        {
            try
            {
                if ((File.GetAttributes(parent) & DeferredFile) != 0) return FileAttributes.Offline;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        }
        return null;
    }

    private static bool IsOnReadyFixedDrive(string path)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetPathRoot(path) is not { Length: 3 } root || root[1] != ':')
            return false;
        var drive = new DriveInfo(root);
        return drive.DriveType == DriveType.Fixed && drive.IsReady;
    }
}
