using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace DeskBox.Services;

/// <summary>直接使用 .url 的 IconFile，避免 Shell 返回通用 Internet 图标。</summary>
internal static class InternetShortcutIcon
{
    internal static bool IsShortcut(string path) =>
        Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase);

    internal static (string Path, int Index)? ReadLocation(string shortcut)
    {
        if (!IsShortcut(shortcut)) return null;
        try
        {
            string? icon = null;
            var index = 0;
            var inSection = false;
            foreach (var line in File.ReadLines(shortcut))
            {
                var text = line.Trim();
                if (text.StartsWith('['))
                {
                    inSection = text.Equals("[InternetShortcut]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inSection || text.StartsWith(';')) continue;
                var equals = text.IndexOf('=');
                if (equals < 0) continue;
                var key = text[..equals].Trim();
                var value = text[(equals + 1)..].Trim();
                if (key.Equals("IconFile", StringComparison.OrdinalIgnoreCase)) icon = value.Trim('"');
                if (key.Equals("IconIndex", StringComparison.OrdinalIgnoreCase)) int.TryParse(value, out index);
            }
            if (string.IsNullOrWhiteSpace(icon)) return null;
            icon = Environment.ExpandEnvironmentVariables(icon);
            // 图标提取只访问本地文件，不因快捷方式中的网络地址发起连接。
            if (icon.StartsWith(@"\\") || icon.Contains("://")) return null;
            icon = Path.GetFullPath(icon, Path.GetDirectoryName(Path.GetFullPath(shortcut))!);
            return (icon, index);
        }
        catch { return null; }
    }

    internal static string CacheSignature(string shortcut)
    {
        var location = ReadLocation(shortcut);
        if (location is null) return "|url-icon-v1";
        try
        {
            var file = new FileInfo(location.Value.Path);
            return $"|url-icon-v1|{location.Value.Path}|{location.Value.Index}|{(file.Exists ? file.LastWriteTimeUtc.Ticks : 0)}|{(file.Exists ? file.Length : 0)}";
        }
        catch { return "|url-icon-v1"; }
    }

    internal static BitmapSource? Load(string shortcut, int size)
    {
        var location = ReadLocation(shortcut);
        if (location is null || !File.Exists(location.Value.Path)) return null;
        IntPtr[] icons = new IntPtr[1];
        try
        {
            var count = PrivateExtractIcons(location.Value.Path, location.Value.Index,
                size, size, icons, new uint[1], 1, 0);
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
