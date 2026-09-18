using System.IO;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed record Category(string Name, string[] Extensions);

public sealed class OrganizeResult
{
    public List<MoveRecord> Moves { get; } = new();
    public List<string> Categories { get; } = new();
    public List<string> Skipped { get; } = new();
    public List<string> Failed { get; } = new();
}

/// <summary>一键整理：按类型把桌面条目归入对应盒子文件夹。</summary>
public static class Organizer
{
    public static readonly Category[] Categories =
    {
        new("文档", new[]
        {
            ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".pdf", ".txt", ".rtf", ".md",
            ".csv", ".wps", ".et", ".dps", ".odt", ".ods", ".odp", ".epub", ".mobi",
        }),
        new("图片", new[]
        {
            ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".svg", ".ico",
            ".heic", ".raw", ".psd", ".ai", ".cr2", ".nef", ".avif",
        }),
        new("视频", new[]
        {
            ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".rmvb", ".webm", ".m4v",
            ".mpg", ".mpeg", ".ts", ".3gp", ".rm",
        }),
        new("音乐", new[] { ".mp3", ".wav", ".flac", ".ape", ".aac", ".ogg", ".wma", ".m4a", ".mid" }),
        new("压缩包", new[] { ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso", ".cab", ".lz4", ".zst" }),
        new("程序", new[] { ".exe", ".msi", ".bat", ".cmd", ".ps1", ".reg", ".apk", ".jar", ".py", ".vbs" }),
        new("快捷方式", new[] { ".lnk", ".url", ".desktop" }),
    };

    public const string FolderCategory = "文件夹";
    public const string OtherCategory = "其他";

    public static string Classify(string path)
    {
        if (Directory.Exists(path))
        {
            return FolderCategory;
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext.Length == 0)
        {
            return OtherCategory;
        }

        foreach (var category in Categories)
        {
            if (Array.IndexOf(category.Extensions, ext) >= 0)
            {
                return category.Name;
            }
        }

        return OtherCategory;
    }

    /// <summary>生成整理计划；快捷方式默认不动（与腾讯桌面整理的默认行为一致）。</summary>
    public static List<(string Source, string Category)> BuildPlan(
        IEnumerable<string> items,
        bool includeShortcuts,
        out List<string> skipped)
    {
        var plan = new List<(string, string)>();
        skipped = new List<string>();

        foreach (var item in items)
        {
            var category = Classify(item);

            // 与腾讯桌面整理一致：快捷方式和可执行程序不自动整理，避免误伤
            if (!includeShortcuts && (category == "快捷方式" || category == "程序"))
            {
                skipped.Add(item);
                continue;
            }

            plan.Add((item, category));
        }

        return plan;
    }

    public static OrganizeResult Execute(
        IReadOnlyList<(string Source, string Category)> plan,
        string boxRoot)
    {
        var result = new OrganizeResult();
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (source, category) in plan)
        {
            try
            {
                if (!FsUtil.PathExists(source))
                {
                    result.Skipped.Add(source);
                    continue;
                }

                var targetDirectory = Path.Combine(boxRoot, category);
                var target = FsUtil.MoveInto(source, targetDirectory);

                result.Moves.Add(new MoveRecord { From = source, To = target });
                touched.Add(category);
            }
            catch (Exception ex)
            {
                Log.Warn($"整理失败 {source}: {ex.Message}");
                result.Failed.Add(source);
            }
        }

        result.Categories.AddRange(touched.OrderBy(CategoryOrder));
        return result;
    }

    /// <summary>让整理出来的盒子顺序稳定：按分类表顺序，文件夹与「其他」排在最后。</summary>
    private static int CategoryOrder(string category)
    {
        if (category == FolderCategory)
        {
            return 90;
        }

        if (category == OtherCategory)
        {
            return 91;
        }

        for (var i = 0; i < Categories.Length; i++)
        {
            if (Categories[i].Name == category)
            {
                return i;
            }
        }

        return 50;
    }

    /// <summary>撤销最近一次整理：把文件搬回原位置。</summary>
    public static int Undo(IReadOnlyList<MoveRecord> records)
    {
        var restored = 0;

        for (var i = records.Count - 1; i >= 0; i--)
        {
            var record = records[i];
            try
            {
                if (!FsUtil.PathExists(record.To))
                {
                    continue;
                }

                var originalDirectory = Path.GetDirectoryName(record.From);
                if (string.IsNullOrEmpty(originalDirectory))
                {
                    continue;
                }

                Directory.CreateDirectory(originalDirectory);
                var name = Path.GetFileName(record.From);
                var target = FsUtil.UniquePath(originalDirectory, name);

                if (Directory.Exists(record.To))
                {
                    Directory.Move(record.To, target);
                }
                else
                {
                    File.Move(record.To, target);
                }

                restored++;
            }
            catch (Exception ex)
            {
                Log.Warn($"撤销失败 {record.To}: {ex.Message}");
            }
        }

        return restored;
    }
}
