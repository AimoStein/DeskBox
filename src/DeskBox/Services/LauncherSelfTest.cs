using DeskBox.Models;
using DeskBox.Views;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

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

        var blender = LaunchEntry.FromPath(Path.Combine(sandbox, "Blender 5.3.lnk"));
        var spacedName = LaunchEntry.FromPath(Path.Combine(sandbox, "b-l"));
        var photoshop = LaunchEntry.FromPath(Path.Combine(sandbox, "Photoshop.lnk"));
        var editor = LaunchEntry.FromPath(Path.Combine(sandbox, "Visual Studio Code.lnk"));
        var cpp = LaunchEntry.FromPath(Path.Combine(sandbox, "C++.lnk"));
        var searchConfig = new AppConfig();
        searchConfig.RecordLauncherOpen(blender.Path);
        var searchable = new[] { blender, spacedName, photoshop, editor, cpp, blender };
        LaunchEntry[] Search(string query) => LauncherCatalog.Search(searchable, query, searchConfig);
        check("开始面板-输入法分隔符和全角字母仍匹配应用，完整名称优先",
            new[] { "b'l", "b’l", "bʼl", "ｂ＇ｌ", "b l", "bl" }.All(query =>
                Search(query).SequenceEqual(new[] { spacedName, blender })) &&
            Search("p'h").SequenceEqual(new[] { photoshop }), "");
        check("开始面板-支持多词名称首字母、普通子串和带标点的名称",
            Search("v's'c").SequenceEqual(new[] { editor }) &&
            Search("ender").SequenceEqual(new[] { blender }) &&
            Search("C++").SequenceEqual(new[] { cpp }) && Search("'").Length == 0, "");

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
        var allAppsCatalog = new LauncherCatalog(new[] { personal, shared }, new[] { app, personal, shared });
        var allAppSections = allAppsCatalog.Sections(config);
        check("开始面板-所有应用包含已分组和未分组的安装入口",
            allAppSections[2].Items.ToHashSet().SetEquals(new[] { app, personal, shared }), "");
        config.LauncherDisplayNames[personal.Path.ToUpperInvariant()] = "我的编辑器";
        config.LauncherDisplayNames[app.Path] = "我的应用";
        var renamedSections = allAppsCatalog.Sections(config);
        check("开始面板-自定义名称在分组及所有应用生效，同名入口保持原名",
            renamedSections[0].Items.Single() == personal with { Name = "我的编辑器" } &&
            renamedSections[1].Items.Single() == shared &&
            renamedSections[2].Items.Contains(app with { Name = "我的应用" }) &&
            renamedSections[2].Items.Contains(personal with { Name = "我的编辑器" }), "");
        config.LauncherDisplayNames.Clear();
        var office = new LaunchEntry(@"shell:AppsFolder\Example.Office", "Excel") with { Name = "自定义名称" };
        var browser = new LaunchEntry(@"shell:AppsFolder\Example.Browser", "Chrome");
        var game = LaunchEntry.FromPath(Path.Combine(sandbox, "Steam", "SteamVR.lnk"));
        var categories = LauncherAppCategories.Group(new[] { office, browser, game, app }, new[] { sandbox });
        check("开始面板-所有应用按原始名称和开始菜单目录分类，自定义名称不影响分类",
            categories.Select(category => category.Name).SequenceEqual(new[] { "办公与效率", "网络与沟通", "游戏与娱乐", "其他应用" }) &&
            categories.SelectMany(category => category.Items).ToHashSet().SetEquals(new[] { office, browser, game, app }), "");
        var otherApps = Enumerable.Range(1, 25)
            .Select(number => new LaunchEntry($@"shell:AppsFolder\Example.Sample{number:D2}", $"Example {number:D2}"))
            .ToArray();
        var categoryConfig = new AppConfig();
        categoryConfig.RecordLauncherOpen(otherApps[^1].Path);
        categoryConfig.RecordLauncherOpen(otherApps[1].Path);
        categoryConfig.RecordLauncherOpen(otherApps[^1].Path);
        var installedSection = new LauncherCatalog(Array.Empty<LaunchEntry>(), otherApps).Sections(categoryConfig)[^1];
        var otherCategory = LauncherAppCategories.Group(installedSection.Items, Array.Empty<string>()).Single();
        check("开始面板-其他应用按字母分组后仍按打开次数排列图标",
            otherCategory.Name == "其他应用 · A–F" && otherCategory.Items.SequenceEqual(
                new[] { otherApps[^1], otherApps[1], otherApps[0] }.Concat(otherApps.Skip(2).SkipLast(1))), "");
        var shortcutFolder = Path.Combine(sandbox, "launcher-filter");
        Directory.CreateDirectory(shortcutFolder);
        var groupedUrl = Path.Combine(shortcutFolder, "原入口.url");
        var installedUrl = Path.Combine(shortcutFolder, "安装入口.url");
        File.WriteAllText(groupedUrl, "[InternetShortcut]\nURL=https://example.test/app\n");
        File.Copy(groupedUrl, installedUrl, overwrite: true);
        var filterGroup = new LaunchSection(group, group.Title, new[] { personal, LaunchEntry.FromPath(groupedUrl) });
        var filtered = LauncherCatalog.ExcludeGroupedApps(new[] { personal, shared, app, LaunchEntry.FromPath(installedUrl) }, new[] { filterGroup });
        check("开始面板-过滤同路径及同目标的快捷方式，保留同名不同入口",
            filtered.SequenceEqual(new[] { shared, app }), "");
        var launchTarget = Path.Combine(shortcutFolder, "App.exe");
        var firstLink = WriteLink("Desktop.lnk", "--launch-from=desktopicon --brand=example", sandbox);
        var secondLink = WriteLink("Installed.lnk", "--launch-from=startmenu --brand=example", shortcutFolder);
        var featureLink = WriteLink("Feature.lnk", "--launch-from=startmenu --brand=example --feature", shortcutFolder);
        var controlPanel = new LaunchEntry(@"shell:AppsFolder\Microsoft.Windows.ControlPanel", "控制面板");
        var duplicateGroup = new LaunchSection(group, group.Title,
            new[] { firstLink, new LaunchEntry("::{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}", "控制面板") });
        check("开始面板-过滤工作目录、启动来源和 Shell 入口不同的重复应用",
            LauncherCatalog.ExcludeGroupedApps(new[] { secondLink, featureLink, controlPanel }, new[] { duplicateGroup })
                .SequenceEqual(new[] { featureLink }), "");

        LaunchEntry WriteLink(string name, string args, string directory)
        {
            var path = ShortcutFactory.CreateInFolder(shortcutFolder, launchTarget, name);
            using var com = DeskBox.Interop.ShellItemInterop.EnsureComInitialized();
            var link = DeskBox.Interop.ShellItemInterop.CreateShellLink();
            try
            {
                var file = (DeskBox.Interop.ShellItemInterop.IPersistFile)link;
                System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(file.Load(path, 0));
                System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(link.SetArguments(args));
                System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(link.SetWorkingDirectory(directory));
                System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(file.Save(path, true));
            }
            finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(link); }
            return LaunchEntry.FromPath(path);
        }

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
        VirtualGroups.MoveToUngrouped(config, usageItems.Select(item => new GroupEntry { Path = item.Path }));
        check("开始面板-移出分组保留打开次数，未分组按同一规则排序",
            usageCatalog.Sections(config)[1].Items.SequenceEqual(expected), "");
        var renamed = Path.Combine(sandbox, "Renamed.lnk");
        config.LauncherDisplayNames[zulu.Path] = "我的 Zulu";
        check("开始面板-快捷方式改名保留打开次数",
            VirtualGroups.RenameReference(config, zulu.Path, renamed) &&
            config.LauncherOpenCounts.GetValueOrDefault(renamed) == 2 &&
            !config.LauncherOpenCounts.ContainsKey(zulu.Path), "");
        check("开始面板-原文件更名后保留自定义名称",
            config.LauncherDisplayNames.GetValueOrDefault(renamed) == "我的 Zulu" &&
            !config.LauncherDisplayNames.ContainsKey(zulu.Path), "");

        var outside = LaunchEntry.FromPath(Path.Combine(sandbox, "outside", "外部快捷方式.lnk"));
        var moveItems = new[] { app, outside };
        var references = moveItems.Select(item => new GroupEntry { Path = item.Path, DisplayName = item == app ? item.Name : "" }).ToArray();
        VirtualGroups.Add(config, group, references);
        VirtualGroups.MoveToUngrouped(config, references);
        VirtualGroups.MoveToUngrouped(config, references);
        var restoredConfig = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(System.Text.Json.JsonSerializer.Serialize(config))!;
        var ungrouped = catalog.Sections(restoredConfig)[1].Items;
        check("开始面板-Shell 应用和外部快捷方式移出后持久显示在未分组，重复拖放不重复",
            group.Entries.Count == 0 && moveItems.All(item => ungrouped.Count(entry => entry == item) == 1), "");

        VirtualGroups.Add(config, group, references);
        check("开始面板-重新归组后从未分组移除",
            moveItems.All(item => !catalog.Sections(config)[1].Items.Contains(item)) &&
            moveItems.All(item => catalog.Sections(config)[0].Items.Contains(item)) &&
            !config.LauncherUngroupedEntries.Any(entry => moveItems.Any(item => item.Path == entry.Path)), "");

        var counts = new[] { 14, 8, 10, 14, 6, 12, 31 };
        var boardGroups = counts.Select((_, index) => new BoxConfig { IsVirtual = true, Title = $"分组 {index + 1}" }).ToArray();
        int?[] oldSlots = [0, 2, 3, 4, 5, 8, null];
        for (var index = 0; index < boardGroups.Length; index++) boardGroups[index].LauncherSlot = oldSlots[index];
        check("开始面板-升级布局保留分组阅读顺序",
            LauncherGroupBoard.AssignSlots(Enumerable.Reverse(boardGroups)) &&
            boardGroups.Select(box => box.LauncherSlot).SequenceEqual(Enumerable.Range(0, boardGroups.Length).Select(slot => (int?)slot)) &&
            !LauncherGroupBoard.AssignSlots(boardGroups), "");
        var groups = boardGroups.Select((box, index) => new LaunchSection(box, box.Title,
            Enumerable.Repeat(alpha, counts[index]).ToArray())).ToArray();
        var layout = new LauncherGridLayout(49, 18);
        var board = new LauncherGroupBoard(groups, layout);
        var firstBounds = board.Cards[0].Bounds;
        var nearSecond = board.Snap(new Point(firstBounds.Right + layout.Gap * 0.75, 8), 0);
        var lowerRow = board.Cards[4].Bounds;
        var acrossRows = board.Snap(new Point(lowerRow.Left + 8, lowerRow.Top + 8), 0);
        check("开始面板-两行分别顶对齐且等间距，支持跨行移动",
            board.Cards.Count == boardGroups.Length && HasEqualGaps(board) &&
            lowerRow.Top == board.Cards.Take(4).Max(card => card.Bounds.Bottom) + layout.Gap &&
            nearSecond?.Slot == boardGroups[1].LauncherSlot && nearSecond?.Bounds.Left == firstBounds.Right + layout.Gap &&
            acrossRows?.Slot == 4 && board.Neighbor(0, 0, 1) == 4 && board.Neighbor(3, 1, 0) is null &&
            board.Snap(new Point(board.Width + layout.Gap, 8), 0) is null &&
            board.Snap(new Point(8, board.Height + layout.Gap), 0) is null, "");
        var dx = layout.TileWidth;
        var dy = layout.TileHeight;
        var dragOffsets = new[] { new Vector(-2 * dx, 0), new Vector(0, -dy), new Vector(-5000, 5000),
            new Vector(5000, -5000), new Vector(-5000, -5000), new Vector(5000, 5000) };
        check("开始面板-横向和纵向拖动按合法格位吸附，全部图标可见且没有整行整列空位",
            layout.ResizeGroup(groups[0], dragOffsets[0]) == 4 &&
            layout.ResizeGroup(groups[0], dragOffsets[1]) == 7 &&
            groups.All(group => dragOffsets.All(offset =>
            {
                var columns = layout.ResizeGroup(group, offset);
                var rows = LauncherGridLayout.RowsFor(group.Items.Count, columns);
                return columns <= group.Items.Count && columns * rows >= group.Items.Count &&
                    columns * (rows - 1) < group.Items.Count;
            })), "");
        var grownGroups = groups.ToArray();
        grownGroups[0] = grownGroups[0] with { Items = Enumerable.Repeat(alpha, 50).ToArray() };
        grownGroups[1] = grownGroups[1] with { Items = Array.Empty<LaunchEntry>() };
        var grown = new LauncherGroupBoard(grownGroups, layout);
        var fitted = layout.MeasureWindow(new Size(3200, 2000), new LauncherGroupBoard(groups.Take(2), layout), ungrouped: null);
        var grownSize = layout.MeasureWindow(new Size(3200, 2000), new LauncherGroupBoard(grownGroups.Take(2), layout), ungrouped: null);
        check("开始面板-拖入和移出图标后自动收紧卡片并保持顺序",
            !LauncherGroupBoard.AssignSlots(boardGroups) &&
            board.Cards.Select(card => card.Section.Group).SequenceEqual(grown.Cards.Select(card => card.Section.Group)) &&
            HasEqualGaps(grown) && grown.Cards[1].Bounds.Width < board.Cards[1].Bounds.Width &&
            grown.Height > board.Height && grown.Width < board.Width, "");

        boardGroups[0].LauncherColumns = 4;
        boardGroups[0].LauncherRows = 12;
        var fixedSize = layout.GroupSize(groups[0]);
        var resized = board.ResizeCard(0, fixedSize);
        check("开始面板-旧配置的多余行列自动收紧，图标变多时按需扩展",
            fixedSize.Height == 2 * LauncherGridLayout.CardPadding + LauncherGridLayout.HeadingHeight + 4 * dy &&
            LauncherGridLayout.GroupColumns(groups[0] with { Items = new[] { alpha } }) == 1 &&
            layout.GroupSize(grownGroups[0]).Height > fixedSize.Height && fixedSize.Width < firstBounds.Width, "");
        check("开始面板-调整一组行列后其他组保持左右顺序、尺寸和间距",
            HasEqualGaps(resized) && resized.Cards[0].Bounds.Size == fixedSize &&
            resized.Cards.Skip(1).Zip(board.Cards.Skip(1)).All(pair =>
                pair.First.Section == pair.Second.Section && pair.First.Bounds.Size == pair.Second.Bounds.Size &&
                Math.Abs((pair.First.Bounds.Left - pair.Second.Bounds.Left) -
                    (pair.Second.Bounds.Top == 0 ? fixedSize.Width - firstBounds.Width : 0)) < 0.01), "");

        var initialSlots = boardGroups.Select(box => box.LauncherSlot).ToArray();
        LauncherGroupBoard.MoveTo(boardGroups, boardGroups[0], 1);
        var restoredBoardGroups = System.Text.Json.JsonSerializer.Deserialize<BoxConfig[]>(System.Text.Json.JsonSerializer.Serialize(boardGroups))!;
        check("开始面板-手动移动可交换格位，保存重启后保留位置和图标行列",
            !LauncherGroupBoard.AssignSlots(restoredBoardGroups) &&
            restoredBoardGroups[0].LauncherSlot == initialSlots[1] && restoredBoardGroups[1].LauncherSlot == initialSlots[0] &&
            restoredBoardGroups[0].LauncherColumns == 4 &&
            restoredBoardGroups.Skip(2).Select(box => box.LauncherSlot).SequenceEqual(initialSlots.Skip(2)), "");

        var viewport = new Size(1760, 1260);
        var windowSpace = new Size(3200, 2000);
        var emptySize = layout.MeasureWindow(windowSpace, new LauncherGroupBoard(Array.Empty<LaunchSection>(), layout), ungrouped: null);
        check("开始面板-窗口按内容独立调整宽高",
            fitted.Width < windowSpace.Width && fitted.Height < windowSpace.Height &&
            emptySize.Height < fitted.Height && fitted.Height < grownSize.Height &&
            fitted.Width == grownSize.Width && fitted.Width == emptySize.Width, $"{fitted.Width} × {fitted.Height}");
        var portraitSpace = new Size(420, 780);
        var portraitWindow = layout.MeasureWindow(portraitSpace, board, ungrouped: null);
        var shortSpace = new Size(1600, 420);
        var shortWindow = layout.MeasureWindow(shortSpace, board, ungrouped: null);
        check("开始面板-纵向屏幕或并排设置时窗口留在可用区内",
            portraitWindow.Width == portraitSpace.Width && portraitWindow.Height <= portraitSpace.Height &&
            shortWindow.Width <= shortSpace.Width && shortWindow.Height == shortSpace.Height, "");

        var screen = new System.Drawing.Rectangle(-1920, 0, 1920, 1080);
        check("开始面板-按任务栏所在边定位并保留 DPI 缩放间距",
            LauncherWindow.NearTaskbar(screen, new(-1920, 0, 1920, 1032), 800, 600, 1.25) == new System.Drawing.Point(-1360, 422) &&
            LauncherWindow.NearTaskbar(screen, new(-1920, 48, 1920, 1032), 800, 600, 1) == new System.Drawing.Point(-1360, 56) &&
            LauncherWindow.NearTaskbar(screen, new(-1872, 0, 1872, 1080), 800, 600, 1) == new System.Drawing.Point(-1864, 240) &&
            LauncherWindow.NearTaskbar(screen, new(-1920, 0, 1872, 1080), 800, 600, 1) == new System.Drawing.Point(-856, 240), "");

        bool HasEqualGaps(LauncherGroupBoard value)
        {
            var rows = value.Cards.GroupBy(card => card.Bounds.Top).ToArray();
            return rows.Length == 2 && rows.All(row => row.First().Bounds.Left == 0 &&
                row.Zip(row.Skip(1)).All(pair => Math.Abs(pair.Second.Bounds.Left - pair.First.Bounds.Right - layout.Gap) < 0.01));
        }

        LauncherGroupBoard.AssignSlots(config.Boxes);
        var window = new LauncherWindow(config);
        try
        {
            window.SetSections(allAppSections);
            var root = (FrameworkElement)window.Content;
            var scroll = (ScrollViewer)window.FindName("ItemsScroll");
            var cards = (StackPanel)window.FindName("Cards");
            var panelLayout = new LauncherGridLayout(config.LauncherIconSize, config.LauncherLayerGap);
            var panel = panelLayout.MeasureWindow(viewport, new LauncherGroupBoard(allAppSections, panelLayout), allAppSections[1]);
            root.Measure(panel);
            root.Arrange(new Rect(panel));
            FlushLayout();

            var appsPage = (FrameworkElement)cards.Children[1];
            var content = (FrameworkElement)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
            double AppsTop() => appsPage.TranslatePoint(new Point(), content).Y;
            var overviewFrame = (Viewbox)((StackPanel)cards.Children[0]).Children[0];
            check("开始面板-首屏贴合实际高度，所有应用在滚动时才创建",
                scroll.ViewportHeight > 0 && Math.Abs(scroll.ViewportHeight - overviewFrame.ActualHeight) < 1 &&
                AppsTop() >= scroll.ViewportHeight - 0.5 && ((StackPanel)appsPage).Children.Count == 0, "");

            Wheel(-120);
            check("开始面板-向下滚动到所有应用",
                scroll.VerticalOffset > 0 && Math.Abs(AppsTop()) < 0.5, "");

            var appCard = (Border)((StackPanel)appsPage).Children[0];
            var appContent = (StackPanel)appCard.Child;
            var filter = (CheckBox)((DockPanel)appContent.Children[0]).Children[0];
            var appBody = (ContentControl)appContent.Children[1];
            var savedChanges = 0;
            window.ConfigChanged += () => savedChanges++;
            var initialCount = AppTiles().Count();
            var firstTile = (Button)AppTiles().First();
            check("开始面板-路径提示显示一秒，面板根节点无虚线焦点框",
                ToolTipService.GetShowDuration(firstTile) == 1000 && root.FocusVisualStyle is null, "");
            var icon = (Image)((StackPanel)firstTile.Content).Children[0];
            firstTile.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            var animationEnd = new DispatcherFrame();
            var animationTimer = new DispatcherTimer(DispatcherPriority.Background)
                { Interval = TimeSpan.FromMilliseconds(240) };
            animationTimer.Tick += (_, _) => animationEnd.Continue = false;
            animationTimer.Start();
            Dispatcher.PushFrame(animationEnd);
            animationTimer.Stop();
            var iconBounds = icon.TransformToAncestor(firstTile).TransformBounds(new Rect(icon.RenderSize));
            var tileBorder = (Border)firstTile.Template.FindName("Tile", firstTile);
            check("开始面板-悬浮图标放大后完整位于按钮内，按钮保持透明",
                iconBounds.Width > icon.Width && new Rect(firstTile.RenderSize).Contains(iconBounds) &&
                ((System.Windows.Media.SolidColorBrush)tileBorder.Background).Color.A == 0, "");
            ToggleFilter(true);
            var persisted = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(System.Text.Json.JsonSerializer.Serialize(config))!;
            check("开始面板-勾选后立即过滤并保存偏好，保持在所有应用页",
                initialCount == 3 && AppTiles().Count() == 2 &&
                persisted.LauncherHideGroupedApps && savedChanges == 1 && Math.Abs(AppsTop()) < 0.5, "");
            ToggleFilter(false);
            check("开始面板-取消勾选恢复所有应用",
                AppTiles().Count() == initialCount &&
                !config.LauncherHideGroupedApps && savedChanges == 2, "");

            Wheel(120);
            check("开始面板-向上滚动返回分组首屏",
                scroll.VerticalOffset == 0 && AppsTop() >= scroll.ViewportHeight - 0.5, "");

            var denseUngrouped = new LaunchSection(null, "未分组", Enumerable.Repeat(alpha, 26).ToArray());
            window.SetSections(groups.Concat(new[] { denseUngrouped, allAppSections[^1] }).ToArray());
            var narrow = new Size(800, 450);
            root.Measure(narrow);
            root.Arrange(new Rect(narrow));
            FlushLayout();
            var groupPage = (StackPanel)cards.Children[0];
            var viewbox = (Viewbox)groupPage.Children[0];
            var surface = (Canvas)((StackPanel)viewbox.Child).Children[0];
            var tiles = Descendants(groupPage).OfType<Button>().Where(button => button.Tag is LaunchEntry).ToArray();
            var firstScreen = new Rect(content.RenderSize);
            firstScreen.Inflate(0.5, 0.5);
            var tileBounds = tiles.Select(tile => tile.TransformToAncestor(content).TransformBounds(new Rect(tile.RenderSize))).ToArray();
            var topPositions = surface.Children.OfType<Border>()
                .GroupBy(card => card.TranslatePoint(new Point(), content).Y).Select(row => row.Count()).ToArray();
            check("开始面板-空间受限也严格分两行顶对齐，组内和未分组图标全部在首屏",
                topPositions.SequenceEqual(new[] { 4, 3 }) &&
                tiles.Length == counts.Sum() + denseUngrouped.Items.Count && tileBounds.All(firstScreen.Contains) &&
                !Descendants(groupPage).OfType<ScrollViewer>().Any() && scroll.HorizontalOffset == 0 &&
                scroll.VerticalOffset == 0 && ((FrameworkElement)cards.Children[1]).TranslatePoint(new Point(), content).Y
                    >= scroll.ViewportHeight - 0.5, $"{tiles.Length} 项 / {content.RenderSize}");

            static IEnumerable<DependencyObject> Descendants(DependencyObject visual)
            {
                for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(visual); index++)
                {
                    var child = System.Windows.Media.VisualTreeHelper.GetChild(visual, index);
                    yield return child;
                    foreach (var descendant in Descendants(child)) yield return descendant;
                }
            }

            void Wheel(int delta)
            {
                scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta)
                    { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                FlushLayout();
            }

            IEnumerable<UIElement> AppTiles() => ((StackPanel)appBody.Content).Children
                .OfType<System.Windows.Controls.Primitives.UniformGrid>().SelectMany(grid => grid.Children.Cast<UIElement>());

            void ToggleFilter(bool enabled)
            {
                filter.IsChecked = enabled;
                filter.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                FlushLayout();
            }

            void FlushLayout()
            {
                root.Dispatcher.Invoke(() => root.UpdateLayout(), DispatcherPriority.ApplicationIdle);
            }
        }
        finally
        {
            window.Close();
        }
    }
}
