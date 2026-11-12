using System.IO;
using DeskBox.Interop;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>分组只保存引用；这些操作从不搬动、删除或改写源文件。</summary>
public static class VirtualGroups
{
    public static bool IsShell(string path) => ShellItemInterop.LooksLikeParsingName(path);

    public static string Key(string path) => IsShell(path) ? path.Trim() : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>一次性快照旧文件夹内容。枚举失败就保留旧盒子，稍后重试，不能当空盒子迁移。</summary>
    public static bool Migrate(AppConfig config)
    {
        var changed = false;
        foreach (var box in config.Boxes.Where(box => !box.IsVirtual))
            changed |= Convert(box, config.EffectiveBoxRoot);
        if (config.Version < 2 && config.Boxes.Any(box => box.IsVirtual))
        {
            config.Version = 2;
            changed = true;
        }
        return changed;
    }

    public static bool Convert(BoxConfig box, string root)
    {
        if (box.IsVirtual) return false;
        var folder = string.IsNullOrWhiteSpace(box.Folder) ? Path.Combine(root, FsUtil.Sanitize(box.Title)) : box.Folder;
        try
        {
            // 不用 Directory.Exists 吞掉拒绝访问；未挂载的磁盘也不能被误迁移成空组。
            var paths = Directory.GetFileSystemEntries(folder)
                .Where(path => !Path.GetFileName(path).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Directory.Exists(path) ? 0 : 1)
                .ThenBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase).ToList();
            paths = ItemOrder.Sort(paths, box.ItemOrder, Path.GetFileName);
            box.Entries = paths.Select(path => new GroupEntry { Path = Key(path) }).ToList();
            box.ItemOrder = box.Entries.Select(entry => entry.Path).ToList();
            box.Folder = folder;
            box.IsVirtual = true;
            Log.Info($"虚拟分组：已转换「{box.Title}」，保留原位置的 {paths.Count} 项。");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"虚拟分组转换暂不可用，保留旧盒子「{box.Title}」：{ex.Message}");
            return false;
        }
    }

    public static IReadOnlyList<string> Paths(BoxConfig box, bool includeHidden)
    {
        var paths = box.IsVirtual
            ? box.Entries.Select(entry => entry.Path)
            : Directory.EnumerateFileSystemEntries(box.Folder);
        return paths.Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => IsShell(path) || (!Path.GetFileName(path).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) &&
                (includeHidden || !FsUtil.IsHiddenOrSystem(path)))).ToList();
    }

    /// <summary>同一原路径只归属一个组；不同目录的同名文件是不同条目。</summary>
    public static int Add(AppConfig config, BoxConfig target, IEnumerable<GroupEntry> entries)
    {
        if (!target.IsVirtual) throw new InvalidOperationException("目标尚未转换为虚拟分组。");
        var changed = 0;
        foreach (var entry in entries)
        {
            var path = Key(entry.Path);
            var existing = config.Boxes.Where(box => box.IsVirtual)
                .SelectMany(box => box.Entries)
                .FirstOrDefault(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
            foreach (var other in config.Boxes.Where(box => box.IsVirtual && box != target))
                Remove(other, new[] { path });
            if (target.Entries.Any(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            target.Entries.Add(new GroupEntry
            {
                Path = path,
                DisplayName = entry.DisplayName,
                ShortcutIdentity = entry.ShortcutIdentity ?? existing?.ShortcutIdentity,
                ShortcutUpgradeIdentity = entry.ShortcutUpgradeIdentity ?? existing?.ShortcutUpgradeIdentity,
            });
            target.ItemOrder.Add(path);
            changed++;
        }
        return changed;
    }

    public static int Remove(BoxConfig box, IEnumerable<string> paths)
    {
        var removed = paths.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        box.ItemOrder.RemoveAll(removed.Contains);
        return box.Entries.RemoveAll(entry => removed.Contains(entry.Path));
    }

    public static bool RenameReference(AppConfig config, string oldPath, string newPath)
    {
        var oldKey = Key(oldPath);
        var newKey = Key(newPath);
        string Remap(string path) => string.Equals(path, oldKey, StringComparison.OrdinalIgnoreCase) ? newKey :
            !IsShell(path) && path.StartsWith(oldKey + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? newKey + path[oldKey.Length..] : path;
        var changed = false;
        foreach (var box in config.Boxes.Where(box => box.IsVirtual))
        {
            foreach (var entry in box.Entries)
                if (Remap(entry.Path) is var replacement && replacement != entry.Path)
                {
                    entry.Path = replacement;
                    changed = true;
                }
            for (var i = 0; i < box.ItemOrder.Count; i++)
                box.ItemOrder[i] = Remap(box.ItemOrder[i]);
        }
        foreach (var (path, count) in config.LauncherOpenCounts.ToArray())
        {
            var replacement = Remap(path);
            if (string.Equals(path, replacement, StringComparison.OrdinalIgnoreCase)) continue;
            config.LauncherOpenCounts.Remove(path);
            config.LauncherOpenCounts[replacement] = Math.Max(config.LauncherOpenCounts.GetValueOrDefault(replacement), count);
            changed = true;
        }
        // 外部重命名后，撤销分组仍应指向当前对象，而不是已经失效的旧名字。
        foreach (var record in config.LastOrganize.Where(record => !string.IsNullOrEmpty(record.GroupId)))
            record.From = record.To = Remap(record.To);
        return changed;
    }

    public static OrganizeResult Organize(AppConfig config, IReadOnlyList<(string Source, string Category)> plan)
    {
        var result = new OrganizeResult();
        var assigned = config.Boxes.Where(box => box.IsVirtual).SelectMany(box => box.Entries)
            .Select(entry => entry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, category) in plan)
        {
            try
            {
                var path = Key(source);
                if (assigned.Contains(path) || !FsUtil.PathExists(path)) { result.Skipped.Add(source); continue; }
                var box = config.Boxes.FirstOrDefault(box => box.IsVirtual && box.Title == category);
                if (box is null)
                {
                    box = new BoxConfig { Title = category, IsVirtual = true };
                    config.Boxes.Add(box);
                }
                Add(config, box, new[] { new GroupEntry { Path = path } });
                assigned.Add(path);
                result.Moves.Add(new MoveRecord { From = path, To = path, GroupId = box.Id });
                if (!result.Categories.Contains(category)) result.Categories.Add(category);
            }
            catch (Exception ex) { Log.Warn($"归组失败 {source}: {ex.Message}"); result.Failed.Add(source); }
        }
        return result;
    }

    public static int Undo(AppConfig config, IReadOnlyList<MoveRecord> records)
    {
        var restored = 0;
        foreach (var record in records.Reverse().ToList())
        {
            if (!string.IsNullOrEmpty(record.GroupId))
            {
                var box = config.Boxes.FirstOrDefault(box => box.Id == record.GroupId && box.IsVirtual);
                if (box is not null) restored += Remove(box, new[] { record.To });
                config.LastOrganize.Remove(record);
            }
            else
            {
                // 兼容升级前的物理整理记录；沿用原有冲突处理，成功后将引用同步到真实落点。
                if (!FsUtil.PathExists(record.To)) { config.LastOrganize.Remove(record); continue; }
                try
                {
                    var parent = Path.GetDirectoryName(record.From)!;
                    Directory.CreateDirectory(parent);
                    var target = FsUtil.UniquePath(parent, Path.GetFileName(record.From));
                    if (Directory.Exists(record.To)) Directory.Move(record.To, target);
                    else File.Move(record.To, target);
                    FsUtil.NotifyShellDirectoryChanged(parent);
                    FsUtil.NotifyShellDirectoryChanged(Path.GetDirectoryName(record.To));
                    RenameReference(config, record.To, target);
                    restored++;
                    config.LastOrganize.Remove(record);
                }
                catch (Exception ex) { Log.Warn($"撤销旧整理失败 {record.To}: {ex.Message}"); }
            }
        }
        return restored;
    }
}
