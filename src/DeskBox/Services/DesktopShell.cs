using System.IO;
using Microsoft.Win32;
using DeskBox.Interop;

namespace DeskBox.Services;

/// <summary>负责与「桌面」这个系统对象打交道：图标显隐、桌面条目枚举。</summary>
public sealed class DesktopShell
{
    public DesktopShell()
    {
        DesktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }

    public string DesktopPath { get; }

    public string PublicDesktopPath =>
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

    /// <summary>桌面图标当前是否可见（读取资源管理器自己的设置）。</summary>
    public bool IconsVisible
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
                var value = key?.GetValue("HideIcons");
                return value is not int i || i == 0;
            }
            catch
            {
                return true;
            }
        }
    }

    /// <summary>切换系统桌面图标显示，用的是资源管理器自己的菜单命令，不写坏系统设置。</summary>
    public bool ToggleIcons()
    {
        var defView = NativeMethods.FindDesktopDefView();
        if (defView == IntPtr.Zero)
        {
            Log.Warn("未找到桌面图标视图，无法切换图标显示。");
            return false;
        }

        NativeMethods.SendMessage(defView, NativeMethods.WM_COMMAND, NativeMethods.CMD_TOGGLE_DESKTOP_ICONS, IntPtr.Zero);
        Thread.Sleep(80);
        return true;
    }

    public bool SetIconsVisible(bool visible)
    {
        if (IconsVisible == visible)
        {
            return true;
        }

        return ToggleIcons();
    }

    /// <summary>枚举桌面上的条目（不含我们的盒子根目录与 desktop.ini）。</summary>
    public List<string> EnumerateItems(string excludeFolder, bool includeHidden)
    {
        var result = new List<string>();
        var excluded = Normalize(excludeFolder);

        foreach (var path in EnumerateSafe(DesktopPath))
        {
            if (excluded.Length > 0 && string.Equals(Normalize(path), excluded, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Path.GetFileName(path).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!includeHidden && FsUtil.IsHiddenOrSystem(path))
            {
                continue;
            }

            result.Add(path);
        }

        result.Sort((a, b) =>
        {
            var aDir = Directory.Exists(a);
            var bDir = Directory.Exists(b);
            if (aDir != bDir)
            {
                return aDir ? -1 : 1;
            }

            return string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.CurrentCultureIgnoreCase);
        });

        return result;
    }

    private static IEnumerable<string> EnumerateSafe(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory);
        }
        catch (Exception ex)
        {
            Log.Warn($"枚举桌面失败: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
