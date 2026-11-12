using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using DeskBox.Interop;

namespace DeskBox.Services;

internal static class ShortcutIcon
{
    internal static bool IsLink(string path) => Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase);

    internal static (string? IconPath, int IconIndex, string? Target) ReadLink(string path)
    {
        try
        {
            using var com = ShellItemInterop.EnsureComInitialized();
            var link = ShellItemInterop.CreateShellLink();
            try
            {
                if (((ShellItemInterop.IPersistFile)link).Load(path, 0) != 0) return default;
                var buffer = new StringBuilder(32768);
                var iconPath = link.GetIconLocation(buffer, buffer.Capacity, out var index) == 0
                    ? Expand(buffer.ToString()) : null;
                buffer.Clear();
                var target = link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0) == 0
                    ? Expand(buffer.ToString()) : null;
                return (iconPath, index, target);
            }
            finally { Marshal.ReleaseComObject(link); }
        }
        catch (Exception ex) when (ex is COMException or IOException or ArgumentException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    private static string? Expand(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : Environment.ExpandEnvironmentVariables(value.Trim('"'));

    internal static BitmapSource? ExtractResource(string file, int index, int size)
    {
        if (!Path.IsPathFullyQualified(file) || file.StartsWith(@"\\") || !File.Exists(file)) return null;
        IntPtr[] icons = new IntPtr[1];
        try
        {
            var count = PrivateExtractIcons(file, index, size, size, icons, new uint[1], 1, 0);
            if (count != 1 || icons[0] == IntPtr.Zero) return null;
            var bitmap = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                icons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
        finally
        {
            if (icons[0] != IntPtr.Zero) DestroyIcon(icons[0]);
        }
    }

    [DllImport("user32.dll", EntryPoint = "PrivateExtractIconsW", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string file, int index, int width, int height,
        [Out] IntPtr[] icons, [Out] uint[] ids, uint count, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
