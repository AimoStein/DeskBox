using System.Diagnostics;
using System.IO;
using System.Text;
using DeskBox.Interop;

namespace DeskBox.Services;

public static class FsUtil
{
    private static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    /// <summary>同一个目录 200ms 内只通知 shell 一次，整批移动不会刷屏。</summary>
    private static readonly Dictionary<string, DateTime> LastShellNotify = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object ShellNotifyLock = new();

    public static string Sanitize(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.Trim())
        {
            sb.Append(Array.IndexOf(Invalid, ch) >= 0 ? '_' : ch);
        }

        var result = sb.ToString().Trim().TrimEnd('.');
        if (result.Length == 0)
        {
            result = "盒子";
        }

        return result.Length > 64 ? result[..64] : result;
    }

    /// <summary>同目录下取一个不冲突的路径，重名时追加 " (2)" 这样的后缀。</summary>
    public static string UniquePath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            return candidate;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);

        for (var i = 2; i < 10000; i++)
        {
            candidate = Path.Combine(directory, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{stem} ({Guid.NewGuid():N}){ext}");
    }

    public static string UniqueFolder(string parent, string name)
    {
        var candidate = Path.Combine(parent, name);
        if (!Directory.Exists(candidate) && !File.Exists(candidate))
        {
            return candidate;
        }

        for (var i = 2; i < 10000; i++)
        {
            candidate = Path.Combine(parent, $"{name} ({i})");
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(parent, $"{name} ({Guid.NewGuid():N})");
    }

    /// <summary>把文件/文件夹移动进目标目录，返回落点路径；失败抛出异常。</summary>
    public static string MoveInto(string sourcePath, string targetDirectory)
    {
        System.IO.Directory.CreateDirectory(targetDirectory);

        var name = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar));
        var target = UniquePath(targetDirectory, name);
        var isDirectory = Directory.Exists(sourcePath);

        if (isDirectory)
        {
            Directory.Move(sourcePath, target);
        }
        else
        {
            File.Move(sourcePath, target);
        }

        // 逐项通知实际移动，避免目录整刷新导致全部格子闪烁，或 200ms 合并窗口漏掉批量末项。
        try
        {
            NativeMethods.SHChangeNotify(isDirectory ? 0x10u : 0x4u,
                NativeMethods.SHCNF_PATHW | 0x1000,
                Path.GetFullPath(sourcePath), null); // SHCNE_RMDIR / DELETE + SHCNF_FLUSH
            NativeMethods.SHChangeNotify(isDirectory ? 0x8u : 0x2u,
                NativeMethods.SHCNF_PATHW | 0x1000,
                Path.GetFullPath(target), null); // SHCNE_MKDIR / CREATE
            var sourceFullPath = Path.GetFullPath(sourcePath);
            var parent = Path.GetDirectoryName(sourceFullPath);
            if (string.Equals(parent, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(parent, Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), StringComparison.OrdinalIgnoreCase))
                DesktopViewInterop.NotifyRemovedPaths(new[] { sourceFullPath });
        }
        catch (Exception ex) { Log.Warn("通知 Shell 文件移动失败：" + ex.Message); }

        return target;
    }

    /// <summary>
    /// 告诉 shell"这个目录的内容变了"，让资源管理器立刻刷新（桌面图标也就立刻跟着变）。
    /// 只做通知，任何失败只记日志，绝不影响移动结果。
    /// </summary>
    public static void NotifyShellDirectoryChanged(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            var full = Path.GetFullPath(directory);

            lock (ShellNotifyLock)
            {
                if (LastShellNotify.TryGetValue(full, out var last) &&
                    DateTime.UtcNow - last < TimeSpan.FromMilliseconds(200))
                {
                    return;
                }

                if (LastShellNotify.Count > 64)
                {
                    LastShellNotify.Clear();
                }

                LastShellNotify[full] = DateTime.UtcNow;
            }

            NativeMethods.SHChangeNotify(
                NativeMethods.SHCNE_UPDATEDIR,
                NativeMethods.SHCNF_PATHW | NativeMethods.SHCNF_FLUSHNOWAIT,
                full,
                null);
        }
        catch (Exception ex)
        {
            Log.Warn($"通知资源管理器刷新失败 {directory}: {ex.Message}");
        }
    }

    public static bool IsHiddenOrSystem(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0)
            {
                return true;
            }

            // .lnk 本身不隐藏，但放在桌面上的 .lnk 常指向隐藏目标，这里只看文件本身
            var info = new FileInfo(path);
            if (info.Exists && info.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (Directory.Exists(path) && info.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    /// <summary>用系统默认程序打开。</summary>
    public static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开失败 {path}: {ex.Message}");
        }
    }

    /// <summary>在资源管理器中定位选中。</summary>
    public static void Reveal(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"定位失败 {path}: {ex.Message}");
        }
    }

    public static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    public static void OpenFolder(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开目录失败 {path}: {ex.Message}");
        }
    }

    public static string Quote(string value) => "\"" + value + "\"";
}
