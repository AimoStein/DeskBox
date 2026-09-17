using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JianZhuo.Models;

public enum ThemeMode
{
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>一个分区盒子的持久化状态。</summary>
public sealed class BoxConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "新盒子";

    /// <summary>盒子对应的真实文件夹。为空时按 BoxRoot\Title 推导。</summary>
    public string Folder { get; set; } = "";

    public double X { get; set; } = 120;
    public double Y { get; set; } = 120;
    public double Width { get; set; } = 330;
    public double Height { get; set; } = 270;
    public double ExpandedHeight { get; set; } = 270;
    public bool Collapsed { get; set; }
    public bool Locked { get; set; }
    public bool ListView { get; set; }
    public bool Visible { get; set; } = true;

    [JsonIgnore]
    public string EffectiveFolder => Folder;
}

public sealed class MoveRecord
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

public sealed class AppConfig
{
    public int Version { get; set; } = 1;

    /// <summary>盒子内容存放的根目录，默认「桌面\桌面整理」。</summary>
    public string BoxRoot { get; set; } = "";

    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public double BoxOpacity { get; set; } = 0.94;
    public double IconSize { get; set; } = 48;
    public double CornerRadius { get; set; } = 10;

    public bool AutoStart { get; set; }
    public bool HideIconsOnDoubleClick { get; set; } = true;
    public bool HideBoxesWithIcons { get; set; } = true;
    public bool RestoreIconsOnExit { get; set; } = true;
    public bool ShowHiddenFiles { get; set; }
    public bool OrganizeShortcuts { get; set; }

    /// <summary>由本程序隐藏了桌面图标（退出时据此还原）。</summary>
    public bool IconsHiddenByApp { get; set; }

    public bool FirstRun { get; set; } = true;

    public List<BoxConfig> Boxes { get; set; } = new();
    public List<MoveRecord> LastOrganize { get; set; } = new();
    public string LastOrganizeTime { get; set; } = "";

    [JsonIgnore]
    public string EffectiveBoxRoot =>
        string.IsNullOrWhiteSpace(BoxRoot) ? DefaultBoxRoot() : BoxRoot;

    public static string DefaultBoxRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "桌面整理");
}

/// <summary>配置读写：原子写入 + 备份回退，配置坏了也不会丢桌面布局。</summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JianZhuo");

    public static string FilePath => Path.Combine(Directory, "config.json");
    private static string BackupPath => Path.Combine(Directory, "config.backup.json");
    private static string TempPath => Path.Combine(Directory, "config.tmp.json");

    public static AppConfig Load()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
        }
        catch
        {
            // 忽略：后面还有回退逻辑
        }

        var cfg = TryRead(FilePath) ?? TryRead(BackupPath);
        if (cfg is null)
        {
            if (File.Exists(FilePath) || File.Exists(BackupPath))
            {
                Log.Warn("配置损坏且备份不可用，已重建默认配置。");
            }
            else
            {
                Log.Info("未找到配置文件，创建默认配置。");
            }

            var fresh = new AppConfig();
            Save(fresh);
            return fresh;
        }

        Log.Info($"配置已加载：{cfg.Boxes.Count} 个盒子，首次运行={cfg.FirstRun}");
        Normalize(cfg);
        return cfg;
    }

    private static AppConfig? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<AppConfig>(json, Options);
        }
        catch (Exception ex)
        {
            Log.Warn($"解析配置失败 {path}: {ex.Message}");
            return null;
        }
    }

    private static void Normalize(AppConfig cfg)
    {
        cfg.Boxes ??= new List<BoxConfig>();
        cfg.LastOrganize ??= new List<MoveRecord>();

        if (string.IsNullOrWhiteSpace(cfg.BoxRoot))
        {
            cfg.BoxRoot = AppConfig.DefaultBoxRoot();
        }

        cfg.BoxOpacity = Math.Clamp(cfg.BoxOpacity, 0.35, 1.0);
        cfg.IconSize = Math.Clamp(cfg.IconSize, 32, 96);
        cfg.CornerRadius = Math.Clamp(cfg.CornerRadius, 0, 22);

        foreach (var box in cfg.Boxes)
        {
            if (box.Width < 180) box.Width = 180;
            if (box.Height < 120) box.Height = 120;
            if (string.IsNullOrWhiteSpace(box.Title)) box.Title = "新盒子";
        }
    }

    public static void Save(AppConfig cfg)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            var json = JsonSerializer.Serialize(cfg, Options);
            File.WriteAllText(TempPath, json);

            if (File.Exists(FilePath))
            {
                File.Copy(FilePath, BackupPath, overwrite: true);
            }

            File.Move(TempPath, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn("保存配置失败: " + ex.Message);
        }
    }
}
