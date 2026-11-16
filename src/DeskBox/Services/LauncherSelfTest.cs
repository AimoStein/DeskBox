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
        var groups = counts.Select(count => new LaunchSection(group, group.Title, Enumerable.Repeat(alpha, count).ToArray())).ToArray();
        var viewport = new Size(1760, 1260);
        var layout = LauncherGridLayout.Fit(viewport, groups, 49, 18);
        check("开始面板-首屏优先完整展示分组和未分组快捷方式",
            layout.GroupColumns > 1 && layout.IconSize == 49 && layout.TotalHeight(viewport.Width, groups) <= viewport.Height, "");
        var fitted = LauncherGridLayout.MeasureViewport(viewport, groups, 49, 18);
        var fittedLayout = LauncherGridLayout.Fit(fitted, groups, 49, 18);
        check("开始面板-按分组实际面积确定窗口尺寸",
            fitted.Width < viewport.Width && fitted.Height < viewport.Height && fittedLayout.IconSize == 49 &&
            fittedLayout.TotalHeight(fitted.Width, groups) == fitted.Height, $"{fitted.Width} × {fitted.Height}");
        var fittedColumns = fittedLayout.TileColumns(fittedLayout.CardWidth(fitted.Width, fittedLayout.GroupColumns));
        check("开始面板-有足够空间时每组横向图标多于竖向",
            counts.All(count => Math.Min(count, fittedColumns) > Math.Ceiling(count / (double)fittedColumns)), "");

        var narrowViewport = new Size(420, 320);
        var narrow = LauncherGridLayout.Fit(narrowViewport, groups, 49, 18);
        check("开始面板-窄窗口自动压缩并保留全部可滚动内容",
            narrow.IconSize == 32 && narrow.TotalHeight(narrowViewport.Width, groups) > narrowViewport.Height &&
            narrow.TileColumns(narrow.CardWidth(narrowViewport.Width, narrow.GroupColumns)) > 0, "");
        check("开始面板-尺寸受限时使用可用空间并允许滚动",
            LauncherGridLayout.MeasureViewport(narrowViewport, groups, 49, 18) == narrowViewport, "");

        var screen = new System.Drawing.Rectangle(-1920, 0, 1920, 1080);
        check("开始面板-按任务栏所在边定位并保留 DPI 缩放间距",
            LauncherWindow.NearTaskbar(screen, new(-1920, 0, 1920, 1032), 800, 600, 1.25) == new System.Drawing.Point(-1360, 422) &&
            LauncherWindow.NearTaskbar(screen, new(-1920, 48, 1920, 1032), 800, 600, 1) == new System.Drawing.Point(-1360, 56) &&
            LauncherWindow.NearTaskbar(screen, new(-1872, 0, 1872, 1080), 800, 600, 1) == new System.Drawing.Point(-1864, 240) &&
            LauncherWindow.NearTaskbar(screen, new(-1920, 0, 1872, 1080), 800, 600, 1) == new System.Drawing.Point(-856, 240), "");

        var empty = new LaunchSection(group, group.Title, Array.Empty<LaunchEntry>());
        var withEmpty = new[] { empty, empty };
        var roomy = LauncherGridLayout.Fit(viewport, withEmpty, 64, 21);
        check("开始面板-空分组保留空间，内容较少时沿用设置中的图标尺寸和间距",
            roomy.IconSize == 64 && roomy.GroupColumns == 2 && roomy.Gap == 21 &&
            roomy.TotalHeight(viewport.Width, withEmpty) > LauncherGridLayout.HeadingHeight, "");
        var emptySize = LauncherGridLayout.MeasureViewport(viewport, withEmpty, 49, 18);
        check("开始面板-内容变少时窗口自动缩小",
            emptySize.Width * emptySize.Height < fitted.Width * fitted.Height, "");

        var window = new LauncherWindow(config);
        try
        {
            window.SetSections(allAppSections);
            var root = (FrameworkElement)window.Content;
            var scroll = (ScrollViewer)window.FindName("ItemsScroll");
            var cards = (StackPanel)window.FindName("Cards");
            var compact = LauncherGridLayout.MeasureViewport(viewport, sections.Take(2).ToArray(), config.LauncherIconSize, config.LauncherLayerGap);
            var panel = new Size(compact.Width + LauncherWindow.PanelHorizontalSpace, compact.Height + LauncherWindow.PanelVerticalSpace);
            root.Measure(panel);
            root.Arrange(new Rect(panel));
            FlushLayout();

            var appsPage = (FrameworkElement)cards.Children[1];
            var content = (FrameworkElement)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
            double AppsTop() => appsPage.TranslatePoint(new Point(), content).Y;
            check("开始面板-所有应用位于首屏可视范围之外",
                scroll.ViewportHeight > 0 && AppsTop() >= scroll.ViewportHeight - 0.5, "");

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
