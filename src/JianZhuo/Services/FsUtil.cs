using System.Diagnostics;
using System.IO;
using System.Text;

namespace JianZhuo.Services;

public static class FsUtil
{
    private static readonly char[] Invalid = Path.GetInvalidFileNameChars();

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

        if (Directory.Exists(sourcePath))
        {
            Directory.Move(sourcePath, target);
        }
        else
        {
            File.Move(sourcePath, target);
        }

        return target;
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
