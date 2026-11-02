using System.IO;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>桌面被 OneDrive 重定向后，只重绑已搬走的目录，不移动任何文件。</summary>
public static class DesktopFolderRecovery
{
    public static bool Recover(AppConfig config) => Recover(config,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop"),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));

    internal static bool Recover(AppConfig config, string previousDesktop, string currentDesktop)
    {
        try
        {
            var root = Path.GetFullPath(config.EffectiveBoxRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(root), Path.GetFullPath(previousDesktop).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                return false; // 不猜测或改写用户自定义的其他存储位置。
            var targetRoot = Path.Combine(currentDesktop, Path.GetFileName(root));
            if (string.Equals(root, targetRoot, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(targetRoot))
                return false;

            var changed = false;
            foreach (var box in config.Boxes)
            {
                if (string.IsNullOrWhiteSpace(box.Folder)) continue;
                var relative = Path.GetRelativePath(root, box.Folder);
                if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)) continue;
                var target = Path.Combine(targetRoot, relative);
                if (!Directory.Exists(target)) continue;
                // 旧目录仍有内容时保留原绑定，避免选中另一套同名文件。
                if (Directory.Exists(box.Folder) && Directory.EnumerateFileSystemEntries(box.Folder).Any()) continue;
                Log.Info($"桌面目录迁移，恢复盒子「{box.Title}」：{box.Folder} → {target}");
                box.Folder = target;
                changed = true;
            }
            if (changed && !config.Boxes.Any(box => !string.IsNullOrWhiteSpace(box.Folder) &&
                Path.GetFullPath(box.Folder).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                config.BoxRoot = targetRoot;
            return changed;
        }
        catch (Exception ex)
        {
            Log.Warn("检查桌面目录迁移失败: " + ex.Message);
            return false;
        }
    }
}
