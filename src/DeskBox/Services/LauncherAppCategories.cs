using System.Text.RegularExpressions;

namespace DeskBox.Services;

internal static class LauncherAppCategories
{
    private static readonly (string Name, Regex Pattern)[] Categories =
    {
        ("办公与效率", Pattern(@"office|microsoft 365|word|excel|powerpoint|onenote|outlook|access|publisher|wps|pdf|notepad|marktext|obsidian|xmind|笔记|办公|记事本|便笺|日历|邮件|计算器")),
        ("网络与沟通", Pattern(@"edge|chrome|firefox|browser|safari|opera|wechat|weixin|telegram|discord|teams|skype|qq|飞书|钉钉|微信|浏览器|远程|onedrive|google drive|网络|向日葵|todesk")),
        ("设计与媒体", Pattern(@"adobe|photoshop|lightroom|illustrator|premiere|corel|blender|houdini|cinema 4d|davinci|affinity|figma|paint|photo|filmcraft|photocraft|media|spotify|potplayer|vlc|音乐|视频|照片|相机|录音|截图|画图|剪辑|听歌")),
        ("开发与工程", Pattern(@"visual studio|vscode|code|python|node\.js|git|powershell|terminal|docker|sdk|sql|odbc|developer|debug|arduino|autocad|unreal|godot|unity|终端|开发|调试|命令提示符|数据库|codex")),
        ("游戏与娱乐", Pattern(@"steam|epic games|xbox|game|minecraft|battle\.net|游戏|纸牌|扫雷")),
        ("系统与实用工具", Pattern(@"windows|system|settings|control panel|explorer|defender|winrar|7-zip|bandizip|everything|task manager|registry|nvidia|intel|设置|控制面板|管理|磁盘|系统|注册表|安全|压缩|防火墙|资源管理器|任务计划|工具")),
        ("安装、卸载与帮助", Pattern(@"^install|^uninstall|installer|setup|help|manuals?|documentation|release notes|卸载|安装程序|帮助|使用手册")),
    };

    internal static (string Name, LaunchEntry[] Items)[] Group(IReadOnlyList<LaunchEntry> apps, IReadOnlyList<string> programsFolders)
    {
        var buckets = new List<LaunchEntry>[Categories.Length + 1];
        foreach (var app in apps)
        {
            var sourceFolder = programsFolders.FirstOrDefault(folder =>
                app.Path.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            var parent = sourceFolder is null ? "" : Path.GetDirectoryName(Path.GetRelativePath(sourceFolder, app.Path));
            // Start menu subfolders give related shortcuts a shared product hint.
            var hints = $"{app.OriginalName} {parent} {Path.GetFileNameWithoutExtension(app.Path)}";
            var category = Categories[^1].Pattern.IsMatch(app.OriginalName) ? Categories.Length - 1 :
                Array.FindIndex(Categories, candidate => candidate.Pattern.IsMatch(app.OriginalName));
            if (category < 0) category = Array.FindIndex(Categories, candidate => candidate.Pattern.IsMatch(hints));
            (buckets[category < 0 ? Categories.Length : category] ??= new List<LaunchEntry>()).Add(app);
        }
        var result = new List<(string Name, LaunchEntry[] Items)>();
        for (var index = 0; index < Categories.Length; index++)
            if (buckets[index] is { } items) result.Add((Categories[index].Name, items.ToArray()));

        if (buckets[^1] is { } other)
        {
            if (other.Count <= 24) result.Add(("其他应用", other.ToArray()));
            else
            {
                // Split long lists by initial without changing the usage order within each range.
                foreach (var range in other.GroupBy(app => AlphabeticalRange(app.OriginalName)).OrderBy(group => group.Key))
                {
                    var name = range.Key switch { 0 => "A–F", 1 => "G–L", 2 => "M–R", 3 => "S–Z", _ => "中文与其他" };
                    result.Add(($"其他应用 · {name}", range.ToArray()));
                }
            }
        }
        return result.ToArray();
    }

    private static int AlphabeticalRange(string name) => char.ToUpperInvariant(name.TrimStart().FirstOrDefault()) switch
    {
        >= 'A' and <= 'F' => 0,
        >= 'G' and <= 'L' => 1,
        >= 'M' and <= 'R' => 2,
        >= 'S' and <= 'Z' => 3,
        _ => 4,
    };

    private static Regex Pattern(string terms) => new(
        string.Join("|", terms.Split('|').Select(term =>
            term.Any(ch => ch >= '\u4e00' && ch <= '\u9fff') ? term : @"(?<![a-z0-9])" + term + @"(?![a-z0-9])")),
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
