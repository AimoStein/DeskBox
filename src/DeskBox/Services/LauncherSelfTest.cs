using DeskBox.Models;
using DeskBox.Views;
using System.Windows;
using System.Windows.Media;

namespace DeskBox.Services;

internal static class LauncherSelfTest
{
    internal static void Run(Action<string, bool, string> check, string sandbox)
    {
        const int win = 0x5B, rightWin = 0x5C;
        var candidate = 0;
        bool Press(int key, bool repeated = false, bool modified = false) =>
            WindowsKeyLauncher.Observe(ref candidate, key, true, repeated, modified);
        bool Release(int key) => WindowsKeyLauncher.Observe(ref candidate, key, false, false, false);

        check("开始面板-松开单独 Win 键时只触发一次",
            !Press(win) && !Press(win, repeated: true) && Release(win) && !Release(win), "");
        check("开始面板-Win 组合键和双 Win 键不触发面板",
            new[] { 0x44, 0x45, 0x4C, 0x09, rightWin }.All(key =>
                !Press(win) && !Press(key, modified: true) && !Release(key) && !Release(win)), "");
        check("开始面板-支持右 Win 单键且预按 Shift 不触发",
            !Press(rightWin, modified: true) && !Release(rightWin) && !Press(rightWin) && Release(rightWin), "");

        var personal = LaunchEntry.FromPath(Path.Combine(sandbox, "desktop", "编辑器.lnk"));
        var shared = LaunchEntry.FromPath(Path.Combine(sandbox, "public", "编辑器.lnk"));
        var app = LaunchEntry.FromPath(@"shell:AppsFolder\Example.App!App", "示例应用");
        var catalog = new LauncherCatalog(new[] { personal, shared }, new[] { app });
        var config = new AppConfig();
        var group = new BoxConfig { IsVirtual = true, Title = "工作" };
        config.Boxes.Add(group);
        VirtualGroups.Add(config, group, new[] { new GroupEntry { Path = personal.Path } });
        var sections = catalog.Sections(config);
        check("开始面板-仅按原路径归组，保留同名桌面入口和已安装应用",
            sections[0].Items.SequenceEqual(new[] { personal }) && sections[1].Items.SequenceEqual(new[] { shared }) &&
            sections[2].Items.SequenceEqual(new[] { app }), "");
        VirtualGroups.Remove(group, new[] { personal.Path });
        check("开始面板-移出后恢复到未分组", catalog.Sections(config)[1].Items.Count == 2, "");

        var alpha = LaunchEntry.FromPath(Path.Combine(sandbox, "Alpha.lnk"));
        var beta = LaunchEntry.FromPath(Path.Combine(sandbox, "beta.lnk"));
        var zulu = LaunchEntry.FromPath(Path.Combine(sandbox, "Zulu.lnk"));
        var idle = LaunchEntry.FromPath(Path.Combine(sandbox, "Aardvark.lnk"));
        var usageItems = new[] { idle, beta, zulu, alpha };
        VirtualGroups.Add(config, group, usageItems.Select(item => new GroupEntry { Path = item.Path }));
        config.RecordLauncherOpen(alpha.Path);
        config.RecordLauncherOpen(beta.Path);
        config.RecordLauncherOpen(zulu.Path);
        config.RecordLauncherOpen(zulu.Path.ToUpperInvariant());
        var usageCatalog = new LauncherCatalog(usageItems, Array.Empty<LaunchEntry>());
        var expected = new[] { zulu, alpha, beta, idle };
        check("开始面板-同组优先打开次数，次数相同按名称排序",
            usageCatalog.Sections(config)[0].Items.SequenceEqual(expected), "");
        VirtualGroups.Remove(group, usageItems.Select(item => item.Path));
        check("开始面板-移出分组保留打开次数，底栏按同一规则排序",
            usageCatalog.Sections(config)[1].Items.SequenceEqual(expected), "");
        var renamed = Path.Combine(sandbox, "Renamed.lnk");
        check("开始面板-快捷方式改名保留打开次数",
            VirtualGroups.RenameReference(config, zulu.Path, renamed) &&
            config.LauncherOpenCounts.GetValueOrDefault(renamed) == 2 &&
            !config.LauncherOpenCounts.ContainsKey(zulu.Path), "");

        var counts = new[] { 18, 8, 12, 31, 7 };
        var layout = LauncherWheelLayout.Arrange(new Size(880, 880), counts, 66);
        check("开始面板-圆盘分组容纳各组快捷方式",
            layout.Sectors.Select(sector => sector.Slots.Count).SequenceEqual(counts),
            string.Join(", ", layout.Sectors.Select(sector => sector.Slots.Count)));
        check("开始面板-按排序从离圆心最近的位置向外分配图标",
            layout.Sectors.All(sector =>
            {
                var distances = sector.Slots.Select(point => (point - layout.Center).Length).ToArray();
                return distances.Zip(distances.Skip(1)).All(pair => pair.First <= pair.Second + 1e-6);
            }), "");
        check("开始面板-放射布局保留统一最小间距",
            layout.Sectors[3].Span > layout.Sectors[4].Span && layout.Sectors.All(sector =>
                sector.Slots.SelectMany((first, index) => sector.Slots.Skip(index + 1)
                    .Select(second => (second - first).Length)).All(distance => distance >= layout.Pitch - 0.001)), "");
        var work = layout.Sectors[0].Slots.Select(point => point - layout.Center).ToArray();
        var rows = work.GroupBy(vector => Math.Round(vector.Length, 5)).ToArray();
        var alignedRows = rows.Zip(rows.Skip(1)).Count(pair => pair.First.Any(inner => pair.Second.Any(outer =>
            Math.Abs(Vector.AngleBetween(inner, outer)) < 1e-5)));
        check("开始面板-工作组外排增密，图标沿固定直线对齐",
            rows.Length >= 3 && rows[^1].Count() > rows[0].Count() && alignedRows >= 2 &&
            rows.Zip(rows.Skip(1)).All(pair => pair.Second.Count() >= pair.First.Count()),
            $"各排数量 {string.Join(", ", rows.Select(row => row.Count()))}，相邻排对齐 {alignedRows} 次");
        check("开始面板-扇区独立留缝并为圆心搜索留位",
            layout.Sectors.All(sector => !sector.Shape.FillContains(layout.Center) &&
                !sector.Shape.FillContains(layout.Center + new Vector(0, -layout.Radius / 2)) &&
                sector.Slots.All(point => sector.Shape.FillContains(point))), "");
        check("开始面板-各组从内圈开始排布，分区名称位于圆盘外",
            layout.Sectors.All(sector => (sector.Slots[0] - layout.Center).Length < layout.Radius * 0.49 &&
                (new Point(Math.Clamp(layout.Center.X, sector.Title.Left, sector.Title.Right),
                    Math.Clamp(layout.Center.Y, sector.Title.Top, sector.Title.Bottom)) - layout.Center).Length > layout.Radius), "");

        var narrowCounts = new[] { 25, 1, 1, 1, 8, 1, 1, 1, 4, 1, 1, 1 };
        var narrow = LauncherWheelLayout.Arrange(new Size(1024, 1024), narrowCounts, 72);
        var radii = narrow.Sectors.SelectMany(sector => sector.Slots)
            .Select(point => (point - narrow.Center).Length).ToArray();
        var firstRing = radii.Min();
        check("开始面板-窄分区跳过放不下的内圈并与其他组共用同心环",
            narrow.Sectors.Select(sector => sector.Slots.Count).SequenceEqual(narrowCounts) &&
            narrow.Sectors.Any(sector => (sector.Slots[0] - narrow.Center).Length > firstRing + 1) &&
            radii.All(radius => Math.Abs((radius - firstRing) / narrow.Pitch -
                Math.Round((radius - firstRing) / narrow.Pitch)) < 1e-6), "");

        var sparse = LauncherWheelLayout.Arrange(new Size(880, 880), new[] { 1, 1, 1, 1, 1 }, 66);
        var lastRing = layout.Sectors.SelectMany(sector => sector.Slots).Max(point => (point - layout.Center).Length);
        check("开始面板-最外图标层决定渐隐范围，图标标签不进入渐隐区",
            sparse.Radius < layout.Radius && sparse.FadeStart < layout.FadeStart &&
            lastRing + new Vector(layout.TileSize.Width / 2, layout.TileSize.Height - layout.IconAnchor).Length
                < layout.FadeStart && layout.FadeStart < layout.Radius, "");

        var single = LauncherWheelLayout.Arrange(new Size(880, 880), new[] { 4 }, 66);
        var pen = new Pen(Brushes.White, 2);
        var start = -Math.PI / 2;
        var noOuterRim = layout.Sectors.All(sector =>
        {
            var middle = start + sector.Span / 2;
            start += sector.Span;
            var direction = new Vector(Math.Cos(middle), Math.Sin(middle));
            return sector.Shape.FillContains(layout.Center + direction * (layout.Radius - 1)) &&
                !sector.Outline.StrokeContains(pen, layout.Center + direction * layout.Radius);
        });
        check("开始面板-分区和单组圆盘均不绘制外缘描边",
            noOuterRim && !single.Sectors[0].Outline.StrokeContains(pen, single.Center + new Vector(single.Radius, 0)) &&
            single.Sectors[0].Outline.StrokeContains(pen, single.Center + new Vector(single.HubRadius + 9, 0)), "");
    }
}
