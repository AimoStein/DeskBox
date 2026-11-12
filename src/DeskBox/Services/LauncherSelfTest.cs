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
        var layout = LauncherWheelLayout.Arrange(new Size(1480, 1480), counts, 32, 4);
        var footprint = (GeometryGroup)LauncherWheelLayout.CompactTileClip(32, new Size(48, LauncherWheelLayout.CompactLabelHeight));
        Geometry PlaceFootprint(Point point)
        {
            var clip = footprint.Clone();
            clip.Transform = new TranslateTransform(point.X - layout.TileSize.Width / 2, point.Y - layout.IconAnchor);
            return clip;
        }
        check("开始面板-圆盘分组容纳各组快捷方式",
            layout.Sectors.Select(sector => sector.Slots.Count).SequenceEqual(counts),
            string.Join(", ", layout.Sectors.Select(sector => sector.Slots.Count)));
        check("开始面板-按排序从离圆心最近的位置向外分配图标",
            layout.Sectors.All(sector =>
            {
                var distances = sector.Slots.Select(point => (point - layout.Center).Length).ToArray();
                return distances.Zip(distances.Skip(1)).All(pair => pair.First <= pair.Second + 1e-6);
            }), "");
        check("开始面板-放射布局按数量分配角度且图标标签互不重叠",
            layout.Sectors[3].Span > layout.Sectors[4].Span && layout.Sectors.All(sector =>
            {
                var bounds = sector.Slots.Select(PlaceFootprint).ToArray();
                return bounds.SelectMany((first, index) => bounds.Skip(index + 1)
                    .Select(second => first.FillContainsWithDetail(second) == IntersectionDetail.Empty)).All(separated => separated);
            }), "");
        check("开始面板-各层图标围绕扇区中轴线对称等距排列",
            layout.Sectors.All(sector =>
            {
                var axis = sector.Title.TopLeft + new Vector(sector.Title.Width / 2, sector.Title.Height / 2) - layout.Center;
                return sector.Slots.Select(point => point - layout.Center)
                    .GroupBy(vector => Math.Round(vector.Length, 5)).All(ring =>
                    {
                        var row = ring.ToArray();
                        var gaps = row.Zip(row.Skip(1)).Select(pair => (pair.Second - pair.First).Length).ToArray();
                        return Math.Abs(Vector.AngleBetween(axis, row[0]) + Vector.AngleBetween(axis, row[^1])) < 1e-6 &&
                            gaps.All(gap => Math.Abs(gap - gaps[0]) < 1e-6);
                    });
            }), "");
        var work = layout.Sectors[0].Slots.Select(point => point - layout.Center).ToArray();
        var rows = work.GroupBy(vector => Math.Round(vector.Length, 5)).ToArray();
        check("开始面板-工作组从内向外连续排列，外圈可容纳更多图标",
            rows.Length >= 3 && rows.Skip(1).Any(row => row.Count() > rows[0].Count()) &&
            rows.Zip(rows.Skip(1)).All(pair => Math.Abs(pair.Second.Key - pair.First.Key - layout.Pitch) < 1e-5),
            $"各排数量 {string.Join(", ", rows.Select(row => row.Count()))}");
        check("开始面板-扇区独立留缝并为圆心搜索留位",
            layout.Sectors.All(sector => !sector.Shape.FillContains(layout.Center) &&
                !sector.Shape.FillContains(layout.Center + new Vector(0, -layout.Radius / 2)) &&
                sector.Slots.All(point => sector.Shape.FillContains(point))), "");
        check("开始面板-组名位于分区内侧，不遮挡搜索按钮或图标",
            layout.Sectors.All(sector =>
            {
                var title = new RectangleGeometry(sector.Title);
                var firstRing = (sector.Slots[0] - layout.Center).Length;
                return (sector.Title.TopLeft + new Vector(sector.Title.Width / 2, sector.Title.Height / 2) - layout.Center).Length < firstRing &&
                    sector.Shape.FillContainsWithDetail(title) == IntersectionDetail.FullyContains &&
                    sector.Slots.All(point => title.FillContainsWithDetail(PlaceFootprint(point)) == IntersectionDetail.Empty);
            }), "");

        bool UsesSharedRings(LauncherWheelLayout.Layout wheel, int[] expectedCounts)
        {
            if (!wheel.Sectors.Select(sector => sector.Slots.Count).SequenceEqual(expectedCounts)) return false;
            var firstRing = (wheel.Sectors[0].Slots[0] - wheel.Center).Length;
            return wheel.Sectors.All(sector => sector.Slots.Select(point => (point - wheel.Center).Length)
                .GroupBy(radius => Math.Round(radius, 5)).Select((ring, index) =>
                    Math.Abs(ring.First() - firstRing - index * wheel.Pitch) < 1e-6).All(matches => matches));
        }

        var narrowCounts = new[] { 25, 1, 1, 1, 8, 1, 1, 1, 4, 1, 1, 1 };
        var narrow = LauncherWheelLayout.Arrange(new Size(1600, 1600), narrowCounts, 32, 4);
        var sixCounts = new[] { 13, 8, 9, 16, 8, 11 };
        var six = LauncherWheelLayout.Arrange(new Size(1260, 1180), sixCounts,
            AppConfig.DefaultLauncherIconSize, AppConfig.DefaultLauncherLayerGap);
        check("开始面板-六组和窄分区均从同一内圈连续向外排布",
            UsesSharedRings(six, sixCounts) && UsesSharedRings(narrow, narrowCounts), "");
        var titleRadii = six.Sectors.Select(sector =>
            (sector.Title.TopLeft + new Vector(sector.Title.Width / 2, sector.Title.Height / 2) - six.Center).Length).ToArray();
        check("开始面板-六组名称围绕圆心同圈排列，仅左右两侧竖排",
            titleRadii.All(radius => Math.Abs(radius - titleRadii[0]) < 1e-6) &&
            six.Sectors.Select(sector => sector.Title.Height > sector.Title.Width)
                .SequenceEqual(new[] { false, true, false, false, true, false }) &&
            six.Sectors.All(sector => sector.Shape.FillContainsWithDetail(new RectangleGeometry(sector.Title)) ==
                IntersectionDetail.FullyContains), "");
        var crowdedCounts = new[] { 36, 9, 22, 48, 8, 13 };
        var crowdedViewport = new Size(720, 480);
        var crowdedLabels = crowdedCounts.Select(count => Enumerable.Repeat(new Size(74, 28), count).ToArray()).ToArray();
        var crowded = LauncherWheelLayout.Arrange(crowdedViewport, crowdedCounts,
            AppConfig.DefaultLauncherIconSize, AppConfig.DefaultLauncherLayerGap, labels: crowdedLabels);
        var canvasBounds = new Rect(crowded.CanvasSize);
        check("开始面板-小视口按需增加层数并扩展画布，保留全部图标",
            UsesSharedRings(crowded, crowdedCounts) && crowded.CanvasSize.Width > crowdedViewport.Width &&
            crowded.CanvasSize.Height > crowdedViewport.Height && crowded.Sectors.All(sector =>
                canvasBounds.Contains(sector.Title) && canvasBounds.Contains(sector.Shape.Bounds) &&
                sector.Slots.All(point => sector.Shape.FillContains(point))) &&
            crowded.Sectors.Max(sector => sector.Slots.Select(point => Math.Round((point - crowded.Center).Length, 5))
                .Distinct().Count()) > 5, "");
        var manyCounts = Enumerable.Repeat(2, 60).ToArray();
        var manyLabels = manyCounts.Select(_ => new[] { new Size(16, 14), new Size(74, 28) }).ToArray();
        var many = LauncherWheelLayout.Arrange(crowdedViewport, manyCounts, 32, 4, labels: manyLabels);
        check("开始面板- 窄扇区后续的长标签也有连续的可用位置", UsesSharedRings(many, manyCounts), "");
        var two = LauncherWheelLayout.Arrange(new Size(1100, 1100), new[] { 3, 28 }, 32, 4);
        check("开始面板-两组按图标数量分配角度，超过半圆的扇区正确容纳图标",
            two.Sectors[0].Span < Math.PI && two.Sectors[1].Span > Math.PI && UsesSharedRings(two, new[] { 3, 28 }) &&
            two.Sectors.All(sector => sector.Slots.All(point => sector.Shape.FillContains(point))), "");

        var sparse = LauncherWheelLayout.Arrange(new Size(1480, 1480), new[] { 1, 1, 1, 1, 1 }, 32, 4);
        check("开始面板-最外图标层决定渐隐范围，图标标签不进入渐隐区",
            sparse.Radius < layout.Radius && sparse.FadeStart < layout.FadeStart &&
            layout.Sectors.SelectMany(sector => sector.Slots).All(point =>
            {
                var offset = (Vector)point - new Vector(layout.TileSize.Width / 2, layout.IconAnchor);
                return footprint.Children.Select(child => child.Bounds).All(bounds =>
                    new[] { bounds.TopLeft, bounds.TopRight, bounds.BottomLeft, bounds.BottomRight }
                        .All(corner => (corner + offset - layout.Center).Length < layout.FadeStart));
            }) && layout.FadeStart < layout.Radius, "");

        var single = LauncherWheelLayout.Arrange(new Size(880, 880), new[] { 4 }, 32, 4);
        var singleSlots = single.Sectors[0].Slots;
        var circleGaps = singleSlots.Select((point, index) =>
            (singleSlots[(index + 1) % singleSlots.Count] - point).Length).ToArray();
        check("开始面板-单分组整圆相邻图标首尾等距",
            circleGaps.Length == 4 && circleGaps.Min() >= Math.Min(single.TileSize.Width, single.TileSize.Height) - 1e-6 &&
            circleGaps.Max() - circleGaps.Min() < 1e-6, "");
        var widerGap = LauncherWheelLayout.Arrange(new Size(1260, 1180), sixCounts, AppConfig.DefaultLauncherIconSize, 24);
        var largerIcons = LauncherWheelLayout.Arrange(new Size(1260, 1180), sixCounts, 60, AppConfig.DefaultLauncherLayerGap);
        check("开始面板-图标大小和层间距按设置独立生效",
            widerGap.TileSize == six.TileSize && widerGap.IconAnchor == six.IconAnchor &&
            widerGap.Pitch - six.Pitch == 24 - AppConfig.DefaultLauncherLayerGap &&
            largerIcons.IconAnchor - six.IconAnchor == (60 - AppConfig.DefaultLauncherIconSize) / 2.0, "");
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
