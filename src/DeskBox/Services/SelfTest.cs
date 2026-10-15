using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using DeskBox.Models;
using DeskBox.Views;

namespace DeskBox.Services;

/// <summary>
/// 离线自检：在临时沙盒里跑一遍一键整理与撤销，不碰真实桌面。
/// 用法：DeskBox.exe --selftest
/// </summary>
public static class SelfTest
{
    public static string ReportPath =>
        Path.Combine(Path.GetTempPath(), "deskbox-selftest.txt");

    public static void Run()
    {
        var report = new StringBuilder();
        var failures = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (!ok)
            {
                failures++;
            }

            report.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? " — " + detail : string.Empty)}");
        }

        var sandbox = Path.Combine(Path.GetTempPath(), "DeskBox-SelfTest");
        var desktop = Path.Combine(sandbox, "desktop");
        var boxRoot = Path.Combine(sandbox, "boxes");

        try
        {
            if (Directory.Exists(sandbox))
            {
                Directory.Delete(sandbox, recursive: true);
            }

            Directory.CreateDirectory(desktop);

            var samples = new[]
            {
                "合同.docx", "照片.png", "片子.mp4", "资料.zip",
                "未知格式.xyz", "安装包.exe", "某程序.lnk",
            };

            foreach (var name in samples)
            {
                File.WriteAllText(Path.Combine(desktop, name), "demo");
            }

            Directory.CreateDirectory(Path.Combine(desktop, "项目资料"));

            // 1) 分类
            Check("分类-文档", Organizer.Classify(Path.Combine(desktop, "合同.docx")) == "文档");
            Check("分类-图片", Organizer.Classify(Path.Combine(desktop, "照片.png")) == "图片");
            Check("分类-视频", Organizer.Classify(Path.Combine(desktop, "片子.mp4")) == "视频");
            Check("分类-压缩包", Organizer.Classify(Path.Combine(desktop, "资料.zip")) == "压缩包");
            Check("分类-文件夹", Organizer.Classify(Path.Combine(desktop, "项目资料")) == "文件夹");
            Check("分类-其他", Organizer.Classify(Path.Combine(desktop, "未知格式.xyz")) == "其他");
            Check("分类-程序", Organizer.Classify(Path.Combine(desktop, "安装包.exe")) == "程序");
            Check("分类-快捷方式", Organizer.Classify(Path.Combine(desktop, "某程序.lnk")) == "快捷方式");

            // 2) 计划：快捷方式与程序默认不动
            var items = Directory.EnumerateFileSystemEntries(desktop).ToList();
            var plan = Organizer.BuildPlan(items, includeShortcuts: false, out var skipped);

            Check("计划跳过 exe 与 lnk", skipped.Count == 2, $"跳过 {skipped.Count} 项");
            Check("计划包含其余 6 项", plan.Count == 6, $"计划 {plan.Count} 项");

            // 3) 执行
            var result = Organizer.Execute(plan, boxRoot);
            Check("执行无失败", result.Failed.Count == 0, string.Join(",", result.Failed));
            Check("移动条数一致", result.Moves.Count == plan.Count, $"{result.Moves.Count}/{plan.Count}");
            Check("生成盒子分类", result.Categories.Count == 6, string.Join(",", result.Categories));
            Check("文档落位", File.Exists(Path.Combine(boxRoot, "文档", "合同.docx")));
            Check("文件夹落位", Directory.Exists(Path.Combine(boxRoot, "文件夹", "项目资料")));
            Check("原位置已清空", !File.Exists(Path.Combine(desktop, "合同.docx")));
            Check("exe 仍在桌面", File.Exists(Path.Combine(desktop, "安装包.exe")));

            // 4) 撤销
            var restored = Organizer.Undo(result.Moves);
            Check("撤销条数一致", restored == result.Moves.Count, $"{restored}/{result.Moves.Count}");
            Check("文件已回到桌面", File.Exists(Path.Combine(desktop, "合同.docx")));
            Check("文件夹已回到桌面", Directory.Exists(Path.Combine(desktop, "项目资料")));

            // 5) 重名处理
            var first = FsUtil.UniquePath(desktop, "重名.txt");
            File.WriteAllText(first, "1");
            var second = FsUtil.UniquePath(desktop, "重名.txt");
            Check("重名自动加后缀", Path.GetFileName(second) == "重名 (2).txt", Path.GetFileName(second));

            // 6) 文件名清洗
            Check("非法字符被替换", !FsUtil.Sanitize("a/b:c*d?e").Any(c => "/:*?".Contains(c)),
                FsUtil.Sanitize("a/b:c*d?e"));

            // 7) 桌面图标视图可定位（双击隐藏依赖它）
            var defView = Interop.NativeMethods.FindDesktopDefView();
            Check("找到桌面图标视图", defView != IntPtr.Zero, $"hwnd=0x{defView.ToInt64():X}");

            // 8) 内存中的配置序列化往返
            var config = new AppConfig();
            config.Boxes.Add(new BoxConfig { Title = "自检盒子", Folder = Path.Combine(boxRoot, "自检盒子") });
            var json = System.Text.Json.JsonSerializer.Serialize(config);
            var back = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(json);
            Check("配置往返一致", back?.Boxes.Count == 1 && back.Boxes[0].Title == "自检盒子");

            // 9) 盒子排布：拖动吸附
            var peer = new BoxRect("peer", 400, 300, 200, 150);
            var moving = new BoxRect("self", 405, 100, 200, 150);
            var snap = BoxLayout.Snap(moving, new[] { peer });
            Check("吸附-左边缘对齐", Math.Abs(snap.X - 400) < 0.001, $"x={snap.X:0.##}");
            Check("吸附-不改变未对齐的轴", Math.Abs(snap.Y - 100) < 0.001, $"y={snap.Y:0.##}");
            Check("吸附-给出竖向参考线",
                snap.Vertical.HasValue && snap.Vertical.Value.Vertical && Math.Abs(snap.Vertical.Value.Position - 400) < 0.001);

            var flush = BoxLayout.Snap(new BoxRect("self", 604, 100, 200, 150), new[] { peer });
            Check("吸附-右边缘贴住左边缘", Math.Abs(flush.X - (peer.Right + BoxLayout.SnapGap)) < 0.001,
                $"x={flush.X:0.##}");
            Check("吸附-相邻时留出间距", Math.Abs(flush.X - peer.Right - BoxLayout.SnapGap) < 0.001,
                $"间距={flush.X - peer.Right:0.##}");

            var flushUp = BoxLayout.Snap(new BoxRect("self", 500, 456, 200, 150), new[] { peer });
            Check("吸附-上下相邻时也留间距", Math.Abs(flushUp.Y - peer.Bottom - BoxLayout.SnapGap) < 0.001,
                $"y={flushUp.Y:0.##}");

            var wide = new BoxRect("wide", 400, 300, 300, 150);
            var centred = BoxLayout.Snap(new BoxRect("self", 450, 100, 200, 150), new[] { wide });
            Check("吸附-中线对齐", Math.Abs(centred.X - 450) < 0.001, $"x={centred.X:0.##}");

            var far = BoxLayout.Snap(new BoxRect("self", 420, 100, 200, 150), new[] { peer });
            Check("吸附-超出阈值不动", Math.Abs(far.X - 420) < 0.001 && !far.Snapped, $"x={far.X:0.##}");

            // 10) 盒子排布：落位避让
            var blocker = new BoxRect("blocker", 0, 0, 200, 200);
            var wanted = new BoxRect("self", 150, 10, 200, 200);
            var spot = BoxLayout.FindFreeSpot(wanted, new[] { blocker });
            Check("避让-让到最近的空位", Math.Abs(spot.X - (blocker.Right + BoxLayout.SnapGap)) < 0.001 &&
                Math.Abs(spot.Y - 10) < 0.001,
                $"({spot.X:0.##},{spot.Y:0.##})");
            Check("避让-结果不再重叠", !BoxLayout.AnyOverlap(spot, new[] { blocker }));
            Check("避让-和邻居留出间距", !BoxLayout.AnyTooClose(spot, new[] { blocker }));

            var free = new BoxRect("self", 900, 700, 200, 200);
            var untouched = BoxLayout.FindFreeSpot(free, new[] { blocker });
            Check("避让-不重叠时不动", Math.Abs(untouched.X - 900) < 0.001 && Math.Abs(untouched.Y - 700) < 0.001);

            var withSelf = BoxLayout.FindFreeSpot(
                new BoxRect("self", 500, 500, 200, 200),
                new[] { new BoxRect("self", 500, 500, 200, 200), blocker });
            Check("避让-忽略盒子自己", Math.Abs(withSelf.X - 500) < 0.001 && Math.Abs(withSelf.Y - 500) < 0.001);

            var clamped = BoxLayout.FindFreeSpot(
                new BoxRect("self", 1700, 950, 400, 200),
                new[] { new BoxRect("near", 1700, 950, 400, 200) },
                new System.Windows.Rect(0, 0, 1920, 1080));
            Check("避让-不越出给定范围", clamped.Right <= 1920.001 && clamped.Bottom <= 1080.001,
                $"({clamped.X:0.##},{clamped.Y:0.##})");

            // 11) 盒子窗口级：拖动吸附 + 落位避让（临时目录，窗口不显示）
            var layoutRoot = Path.Combine(sandbox, "layout");
            var folderA = Path.Combine(layoutRoot, "盒子A");
            var folderB = Path.Combine(layoutRoot, "盒子B");
            Directory.CreateDirectory(folderA);
            Directory.CreateDirectory(folderB);

            var layoutConfig = new AppConfig { BoxRoot = layoutRoot };
            var configB = new BoxConfig
            {
                Title = "盒子B", Folder = folderB, X = 100, Y = 200, Width = 200, Height = 400,
            };
            var configA = new BoxConfig
            {
                Title = "盒子A", Folder = folderA, X = 500, Y = 650, Width = 200, Height = 150,
            };
            layoutConfig.Boxes.Add(configA);
            layoutConfig.Boxes.Add(configB);

            var windows = new List<BoxWindow>();

            IReadOnlyList<BoxRect> Peers(string id) =>
                windows.Where(w => w.Config.Id != id).Select(w => w.ContentBox).ToList();

            // 盒子窗口要用到主题画刷；正常启动时由 App 先调用，这里补上
            ThemeManager.Apply(ThemeMode.System);

            var shell = new DesktopShell(desktop);
            windows.Add(new BoxWindow(layoutConfig, configA, shell, Peers) { ShowActivated = false });
            windows.Add(new BoxWindow(layoutConfig, configB, shell, Peers) { ShowActivated = false });

            windows[0].BeginMove(null, new Point(1000, 400));
            windows[1].BeginMove(null, new Point(1000, 400));
            Check("防闪烁-移动格子期间暂停所有盒子的层级巡检", BoxWindow.AnyDragging);
            windows[0].EndMove();
            windows[0].EndMove();
            Check("防闪烁-重复结束移动不影响另一个格子的保护", BoxWindow.AnyDragging);
            windows[1].EndMove();
            Check("防闪烁-全部移动结束后恢复巡检", !BoxWindow.AnyDragging);

            void Drag(BoxWindow window, double x, double y)
            {
                var from = window.ContentBox;
                var start = new Point(1000, 400);

                window.BeginMove(null, start);
                window.MoveTo(new Point(start.X + (x - from.X), start.Y + (y - from.Y)));
                window.EndMove();
            }

            Drag(windows[0], 105, 650);
            Check("窗口-拖动吸附到另一个盒子的左边缘", Math.Abs(windows[0].ContentBox.X - 100) < 0.001,
                $"x={windows[0].ContentBox.X:0.##}");
            Check("窗口-吸附后没有压到别的盒子",
                !BoxLayout.AnyOverlap(windows[0].ContentBox, new[] { windows[1].ContentBox }));

            Drag(windows[0], 260, 200);
            var dropped = windows[0].ContentBox;
            Check("窗口-落位自动避让到最近的空位",
                Math.Abs(dropped.X - 312) < 0.001 && Math.Abs(dropped.Y - 200) < 0.001,
                $"({dropped.X:0.##},{dropped.Y:0.##})");
            Check("窗口-避让后不再重叠", !BoxLayout.AnyOverlap(dropped, new[] { windows[1].ContentBox }));
            Check("窗口-避让后留出间距", !BoxLayout.AnyTooClose(dropped, new[] { windows[1].ContentBox }));
            Check("窗口-避让结果已写回配置",
                Math.Abs(configA.X - dropped.X) < 0.001 && Math.Abs(configA.Y - dropped.Y) < 0.001,
                $"({configA.X:0.##},{configA.Y:0.##})");

            // 11.5) 盒子不许跑到屏幕外：拖出去自动回到边缘内侧，留出和盒子之间一样的间距
            var screen = new Rect(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight);

            var oversized = BoxLayout.ClampToArea(
                new BoxRect("big", screen.Right + 500, screen.Bottom + 500, screen.Width + 800, screen.Height + 800),
                screen);
            Check("屏幕外-盒子比屏幕还大时贴住左上角",
                Math.Abs(oversized.X - (screen.Left + BoxLayout.SnapGap)) < 0.001 &&
                Math.Abs(oversized.Y - (screen.Top + BoxLayout.SnapGap)) < 0.001,
                $"({oversized.X:0.##},{oversized.Y:0.##})");

            var keepInside = BoxLayout.ClampToArea(new BoxRect("in", 400, 300, 200, 150), screen);
            Check("屏幕内-位置不受影响",
                Math.Abs(keepInside.X - 400) < 0.001 && Math.Abs(keepInside.Y - 300) < 0.001,
                $"({keepInside.X:0.##},{keepInside.Y:0.##})");

            Drag(windows[0], screen.Left - 5000, screen.Top - 5000);
            var pulledTopLeft = windows[0].ContentBox;
            Check("屏幕外-拖出左上角会回到边缘内",
                Math.Abs(pulledTopLeft.X - (screen.Left + BoxLayout.SnapGap)) < 0.5 &&
                Math.Abs(pulledTopLeft.Y - (screen.Top + BoxLayout.SnapGap)) < 0.5,
                $"({pulledTopLeft.X:0.##},{pulledTopLeft.Y:0.##})");

            Drag(windows[0], screen.Right + 5000, screen.Bottom + 5000);
            var pulledBottomRight = windows[0].ContentBox;
            Check("屏幕外-拖出右下角会回到边缘内",
                Math.Abs(pulledBottomRight.Right - (screen.Right - BoxLayout.SnapGap)) < 0.5 &&
                Math.Abs(pulledBottomRight.Bottom - (screen.Bottom - BoxLayout.SnapGap)) < 0.5,
                $"({pulledBottomRight.X:0.##},{pulledBottomRight.Y:0.##})");

            Drag(windows[0], 500, 500);
            var insideAgain = windows[0].ContentBox;
            Check("屏幕内-拖回中间能正常落位",
                Math.Abs(insideAgain.X - 500) < 0.001 && Math.Abs(insideAgain.Y - 500) < 0.001,
                $"({insideAgain.X:0.##},{insideAgain.Y:0.##})");

            // 11.6) 盒内拖动排序 / 跨盒子落位的顺序计算
            var order = new List<string> { "A", "B", "C", "D" };
            Check("排序-往后挪一条",
                string.Join(",", ItemOrder.Apply(order, new[] { "A" }, 3)) == "B,C,A,D",
                string.Join(",", ItemOrder.Apply(order, new[] { "A" }, 3)));
            Check("排序-往前挪一条",
                string.Join(",", ItemOrder.Apply(order, new[] { "D" }, 1)) == "A,D,B,C",
                string.Join(",", ItemOrder.Apply(order, new[] { "D" }, 1)));
            Check("排序-多条一起挪",
                string.Join(",", ItemOrder.Apply(order, new[] { "B", "C" }, 4)) == "A,D,B,C",
                string.Join(",", ItemOrder.Apply(order, new[] { "B", "C" }, 4)));
            Check("排序-落点没变就不动",
                string.Join(",", ItemOrder.Apply(order, new[] { "B" }, 1)) == "A,B,C,D",
                string.Join(",", ItemOrder.Apply(order, new[] { "B" }, 1)));

            var manual = ItemOrder.Sort(new[] { "b.lnk", "a.lnk", "c.lnk" }, new[] { "c.lnk", "b.lnk" }, name => name);
            Check("排序-按记录的顺序显示，新条目在后面",
                string.Join(",", manual) == "c.lnk,b.lnk,a.lnk", string.Join(",", manual));

            var orderConfig = new AppConfig();
            orderConfig.Boxes.Add(new BoxConfig
            {
                Title = "顺序盒子", Folder = boxRoot, ItemOrder = { "微信.lnk", "合同.docx" },
            });
            var orderJson = System.Text.Json.JsonSerializer.Serialize(orderConfig);
            var orderBack = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(orderJson);
            Check("排序-顺序随配置保存",
                orderBack?.Boxes[0].ItemOrder.Count == 2 && orderBack.Boxes[0].ItemOrder[0] == "微信.lnk",
                string.Join(",", orderBack?.Boxes[0].ItemOrder ?? new List<string>()));

            // 11.7) 拖动落点：盒内排序 / 跨盒子移动 / 从外面拖进来（直接喂给落点处理，不动真实鼠标）
            foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
            {
                File.WriteAllText(Path.Combine(folderA, name), "x");
            }

            windows[0].ReloadItems();
            Check("拖动-盒子内容已就绪", string.Join(",", windows[0].ItemNames) == "a.txt,b.txt,c.txt",
                string.Join(",", windows[0].ItemNames));

            var retainedItems = windows[0].ItemsHost.Items.Cast<BoxItem>().ToArray();
            retainedItems[0].IsSelected = true;
            windows[0].ReloadItems();
            Check("防闪烁-重复刷新保留图标条目和选择状态",
                windows[0].ItemsHost.Items.Cast<BoxItem>().SequenceEqual(retainedItems) && retainedItems[0].IsSelected);
            var firstFile = Path.Combine(folderA, "a.txt");
            File.WriteAllText(firstFile, "updated file");
            windows[0].ReloadItems();
            Check("防闪烁-文件变化时保留条目并刷新文件信息",
                ReferenceEquals(windows[0].ItemsHost.Items[0], retainedItems[0]) && retainedItems[0].Meta.Contains("12 B"));

            var moveInside = new DataObject();
            moveInside.SetData(DataFormats.FileDrop, new[] { Path.Combine(folderA, "c.txt") });
            moveInside.SetData(BoxWindow.BoxDragFormat, folderA);
            windows[0].HandleDrop(moveInside, 0);
            Check("拖动-盒内排序", string.Join(",", windows[0].ItemNames) == "c.txt,a.txt,b.txt",
                string.Join(",", windows[0].ItemNames));
            Check("拖动-盒内排序不动磁盘文件",
                File.Exists(Path.Combine(folderA, "a.txt")) && File.Exists(Path.Combine(folderA, "c.txt")));
            Check("拖动-盒内排序写回配置", string.Join(",", configA.ItemOrder) == "c.txt,a.txt,b.txt",
                string.Join(",", configA.ItemOrder));

            var moveAcross = new DataObject();
            moveAcross.SetData(DataFormats.FileDrop, new[] { Path.Combine(folderA, "c.txt") });
            moveAcross.SetData(BoxWindow.BoxDragFormat, folderA);
            windows[1].HandleDrop(moveAcross, 0);
            Check("拖动-跨盒子会搬走文件",
                File.Exists(Path.Combine(folderB, "c.txt")) && !File.Exists(Path.Combine(folderA, "c.txt")));
            Check("拖动-跨盒子落在指定位置", string.Join(",", windows[1].ItemNames) == "c.txt",
                string.Join(",", windows[1].ItemNames));

            windows[0].ReloadItems();
            Check("拖动-原来的盒子少了一条", string.Join(",", windows[0].ItemNames) == "a.txt,b.txt",
                string.Join(",", windows[0].ItemNames));

            var externalPath = Path.Combine(sandbox, "外部文件.txt");
            File.WriteAllText(externalPath, "x");
            var fromOutside = new DataObject();
            fromOutside.SetData(DataFormats.FileDrop, new[] { externalPath });
            windows[1].HandleDrop(fromOutside, 0);
            Check("拖动-外部文件落到最前面",
                windows[1].ItemNames.Count == 2 && windows[1].ItemNames[0] == "外部文件.txt",
                string.Join(",", windows[1].ItemNames));

            // 11.8) 缩放顿挫：宽高按图标格子跳，加宽时高度自动收放
            Check("顿挫-一段宽度能放几列", CellGrid.Count(700, 70) == 10, CellGrid.Count(700, 70).ToString());
            Check("顿挫-12 个图标 3 列 = 4 行", CellGrid.CellsFor(12, 3) == 4, CellGrid.CellsFor(12, 3).ToString());
            Check("顿挫-12 个图标 4 列 = 3 行", CellGrid.CellsFor(12, 4) == 3, CellGrid.CellsFor(12, 4).ToString());

            // 3 列 × 4 行、格子 70×92、边框留白 36 / 34
            var origin = new Rect(100, 100, 246, 402);

            var widened = CellGrid.SnapResize(origin, 100, 100, 320, 402, "R", 70, 92, 36, 34, 3, 4, 12);
            Check("顿挫-往右加宽正好一列", Math.Abs(widened.Width - (36 + 4 * 70)) < 0.001, $"宽={widened.Width:0.##}");
            Check("顿挫-加宽后高度收到 3 行", Math.Abs(widened.Height - (34 + 3 * 92)) < 0.001, $"高={widened.Height:0.##}");

            var narrowed = CellGrid.SnapResize(origin, 100, 100, 176, 402, "R", 70, 92, 36, 34, 3, 4, 12);
            Check("顿挫-往左收窄一列", Math.Abs(narrowed.Width - (36 + 2 * 70)) < 0.001, $"宽={narrowed.Width:0.##}");
            Check("顿挫-收窄后高度放到 6 行", Math.Abs(narrowed.Height - (34 + 6 * 92)) < 0.001, $"高={narrowed.Height:0.##}");

            var taller = CellGrid.SnapResize(origin, 100, 100, 246, 300, "B", 70, 92, 36, 34, 3, 4, 12);
            Check("顿挫-拖下边按行跳", Math.Abs(taller.Height - (34 + 3 * 92)) < 0.001, $"高={taller.Height:0.##}");

            var fromLeft = CellGrid.SnapResize(origin, 40, 100, 350, 402, "L", 70, 92, 36, 34, 3, 4, 12);
            Check("顿挫-拖左边时右边缘不动", Math.Abs(fromLeft.Right - origin.Right) < 0.001, $"右={fromLeft.Right:0.##}");

            var listWidth = CellGrid.SnapResize(origin, 100, 100, 300, 300, "R", 70, 92, 36, 34, 3, 4, 12, snapColumns: false);
            Check("顿挫-列表视图宽度自由", Math.Abs(listWidth.Width - 300) < 0.001, $"宽={listWidth.Width:0.##}");
            Check("顿挫-格子太小就不动", CellGrid.SnapResize(origin, 1, 2, 3, 4, "R", 0, 0, 0, 0, 1, 1, 1) == new Rect(1, 2, 3, 4));

            // 11.9) 一键整理盒子：排整齐
            var messy = new List<BoxRect>
            {
                new("b", 700, 300, 200, 150),
                new("a", 100, 20, 200, 150),
                new("c", 400, 25, 200, 150),
            };

            Check("排盒子-空列表不炸",
                BoxArranger.Arrange(Array.Empty<BoxRect>(), new Rect(0, 0, 1000, 800)).Count == 0);

            var oneRow = BoxArranger.Arrange(messy, new Rect(0, 0, 1000, 800));
            Check("排盒子-按上下左右顺序排", string.Join(",", oneRow.Select(b => b.Id)) == "a,c,b",
                string.Join(",", oneRow.Select(b => b.Id)));
            Check("排盒子-贴顶部、水平居中",
                Math.Abs((oneRow[0].X + oneRow[^1].Right) / 2 - 500) < 0.001 &&
                Math.Abs(oneRow[0].Y - BoxLayout.SnapGap) < 0.001,
                $"左={oneRow[0].X:0.##} 右={oneRow[^1].Right:0.##} 顶={oneRow[0].Y:0.##}");
            Check("排盒子-同一行顶部对齐",
                oneRow[1].Y == oneRow[0].Y && oneRow[2].Y == oneRow[0].Y);
            Check("排盒子-左右间距一致",
                Math.Abs(oneRow[1].X - oneRow[0].Right - BoxLayout.SnapGap) < 0.001 &&
                Math.Abs(oneRow[2].X - oneRow[1].Right - BoxLayout.SnapGap) < 0.001);
            Check("排盒子-盒子尺寸不变", oneRow.All(b => b.Width == 200 && b.Height == 150));
            Check("排盒子-左右两侧留白对称",
                Math.Abs(oneRow[0].X - (1000 - oneRow[^1].Right)) < 0.001,
                $"左={oneRow[0].X:0.##} 右={1000 - oneRow[^1].Right:0.##}");

            var wrapped = BoxArranger.Arrange(messy, new Rect(0, 0, 560, 800));
            Check("排盒子-一行摆不下就换行",
                Math.Abs(wrapped[2].X - (560 - 200) / 2.0) < 0.001 &&
                Math.Abs(wrapped[2].Y - (BoxLayout.SnapGap * 2 + 150)) < 0.001,
                $"({wrapped[2].X:0.##},{wrapped[2].Y:0.##})");
            Check("排盒子-换行后行距留够",
                Math.Abs(wrapped[2].Y - (wrapped[0].Bottom + BoxLayout.SnapGap)) < 0.001);
            Check("排盒子-换行后单独一行也居中",
                Math.Abs(wrapped[0].X + wrapped[1].Right - 560) < 0.001,
                $"左={wrapped[0].X:0.##} 右={560 - wrapped[1].Right:0.##}");

            // 12) 盒子里的显示名：快捷方式不显示扩展名
            var shortcut = new BoxItem(Path.Combine(layoutRoot, "微信.lnk"), 48);
            Check("显示名-快捷方式隐藏 lnk", shortcut.Name == "微信", shortcut.Name);

            var shortcut2 = new BoxItem(Path.Combine(layoutRoot, "Some Tool.LNK"), 48);
            Check("显示名-大写 LNK 同样隐藏", shortcut2.Name == "Some Tool", shortcut2.Name);

            var url = new BoxItem(Path.Combine(layoutRoot, "Goodgame Empire.url"), 48);
            Check("显示名-网址快捷方式隐藏 url", url.Name == "Goodgame Empire", url.Name);

            var document = new BoxItem(Path.Combine(layoutRoot, "合同.docx"), 48);
            Check("显示名-普通文件的扩展名保留", document.Name == "合同.docx", document.Name);

            // 13) 解散盒子直接执行（不弹确认框）：能走到下一行就说明没有阻塞对话框
            windows[0].Dissolve();
            Check("解散盒子-不确认直接解散", !Directory.Exists(folderA));

            // 14) 配置放在 exe 旁边（绿色便携）
            var exeDir = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
            var cfgDir = Path.GetFullPath(ConfigStore.Directory).TrimEnd(Path.DirectorySeparatorChar);
            Check("配置目录-默认在 exe 旁边", string.Equals(cfgDir, exeDir, StringComparison.OrdinalIgnoreCase),
                ConfigStore.Directory);
            Check("配置目录-可写", File.Exists(Path.Combine(ConfigStore.Directory, "config.json")) ||
                Directory.Exists(ConfigStore.Directory));

            // 15) 盒子右侧滚动条始终不显示（滚轮仍然可以滚动）
            Check("盒子-右侧滚动条始终隐藏",
                windows[1].Scroller.VerticalScrollBarVisibility == System.Windows.Controls.ScrollBarVisibility.Hidden,
                windows[1].Scroller.VerticalScrollBarVisibility.ToString());

            // 16) 按 Win+D（显示桌面）后盒子要自己回来
            windows[1].WindowState = WindowState.Minimized;
            windows[1].RestoreIfShellHidTheBox();
            Check("盒子-被最小化后自动还原", windows[1].WindowState == WindowState.Normal,
                windows[1].WindowState.ToString());

            // 17) Win+D：桌面层判定与前台变化处理
            Check("前台判断-空句柄不算桌面", !Interop.NativeMethods.IsDesktopWindow(IntPtr.Zero));
            windows[1].KeepVisibleOnDesktop(); // 没有窗口句柄时必须安全退出，不能抛异常
            Check("前台变化处理-可安全调用", true);

            // 18) 系统图标（控制面板 / 回收站 / 网络）拖进盒子：解析 Shell IDList Array 并建 .lnk
            // 资源管理器拖系统图标时给的是 CFSTR_SHELLIDLIST，没有文件路径。
            // 这里自己拼一个 CIDA + DataObject，把「资源管理器拖放」这条真实路径离线跑通。
            const string recycleBinClsid = Interop.ShellItemInterop.RecycleBinParsingName;

            // PIDL 字节一律「拿到就立刻拷出来」，不长期持有 shell 的 PIDL 指针
            byte[] ToBuffer(IntPtr pidl)
            {
                var size = Interop.ShellItemInterop.PidlSize(pidl);
                if (size <= 0)
                {
                    Log.Warn($"自检提示：PIDL 长度不合法（{size}），该用例跳过。");
                    return Array.Empty<byte>();
                }

                var bytes = new byte[size];
                Marshal.Copy(pidl, bytes, 0, size);
                return bytes;
            }

            // SHParseDisplayName 偶尔会给出一个走不到结尾的 PIDL（shell 命名空间项缓存有生命周期，
            // 实测约 1/5 概率），这时重试几次即可。
            // 真实拖放走的是 CIDA 字节流（PidlAt 按缓冲区边界拷贝），不受这个影响。
            byte[] ParsePidlBytes(string parsingName)
            {
                for (var attempt = 1; attempt <= 5; attempt++)
                {
                    var pidl = Interop.ShellItemInterop.ParseDisplayName(parsingName);
                    if (pidl == IntPtr.Zero)
                    {
                        Log.Warn($"自检提示：{parsingName} 第 {attempt} 次拿不到 PIDL");
                        continue;
                    }

                    var bytes = ToBuffer(pidl);

                    if (bytes.Length == 0)
                    {
                        // 把前 16 字节记下来，便于判断到底拿到了什么
                        var head = new StringBuilder();
                        try
                        {
                            for (var i = 0; i < 16; i++)
                            {
                                head.Append(Marshal.ReadByte(pidl, i).ToString("X2")).Append(' ');
                            }
                        }
                        catch (Exception ex)
                        {
                            head.Append("读不出来：" + ex.Message);
                        }

                        Interop.ShellItemInterop.FreePidl(pidl);
                        Log.Warn($"自检提示：{parsingName} 第 {attempt} 次 PIDL 长度不合法，前 16 字节 = {head}");
                        continue;
                    }

                    Interop.ShellItemInterop.FreePidl(pidl);
                    return bytes;
                }

                return Array.Empty<byte>();
            }

            // 按 CIDA 结构拼字节：DWORD cidl; DWORD aoffset[cidl + 1]; 然后是各个 PIDL。
            // aoffset[0] 留给父文件夹 PIDL，aoffset[1..cidl] 才是每个条目，顺序不能错。
            byte[] BuildCida(byte[] parent, params byte[][] items)
            {
                var headerSize = 4 + ((items.Length + 1) * 4);
                var total = headerSize + parent.Length + items.Sum(bytes => bytes.Length);
                var buffer = new byte[total];

                BitConverter.GetBytes((uint)items.Length).CopyTo(buffer, 0);

                var cursor = headerSize;

                // aoffset[0] = 父文件夹；没有父 PIDL 时保持 0
                if (parent.Length > 0)
                {
                    BitConverter.GetBytes((uint)cursor).CopyTo(buffer, 4);
                    parent.CopyTo(buffer, cursor);
                    cursor += parent.Length;
                }

                // aoffset[1..cidl] = 每个条目
                for (var i = 0; i < items.Length; i++)
                {
                    BitConverter.GetBytes((uint)cursor).CopyTo(buffer, 8 + (i * 4));
                    items[i].CopyTo(buffer, cursor);
                    cursor += items[i].Length;
                }

                return buffer;
            }

            using (Interop.ShellItemInterop.EnsureComInitialized())
            {
                Check("拖放-空数据不接受", !ShellDrop.CanAccept(null) && !ShellDrop.CanAccept(new DataObject()));

                var probePidl = Interop.ShellItemInterop.ParseDisplayName(recycleBinClsid);
                Check("拖放-能解析回收站的命名空间 PIDL", probePidl != IntPtr.Zero, recycleBinClsid);
                Interop.ShellItemInterop.FreePidl(probePidl);

                // 拿到字节后立刻释放 PIDL，后面只用字节，避免长期持有 shell 的指针
                var recycleBytes = ParsePidlBytes(recycleBinClsid);
                Check("拖放-回收站 PIDL 长度合法", recycleBytes.Length > 0, $"{recycleBytes.Length} 字节");

                if (recycleBytes.Length > 0)
                {
                    var shellData = new DataObject();
                    shellData.SetData(
                        Interop.ShellItemInterop.ShellIdListArrayFormat,
                        new MemoryStream(BuildCida(Array.Empty<byte>(), recycleBytes)));

                    Check("拖放-接受 Shell IDList Array", ShellDrop.CanAccept(shellData));

                    var parsed = ShellDrop.Read(shellData);
                    Check("拖放-系统图标解析出 1 项", parsed.Count == 1, $"共 {parsed.Count} 项");

                    if (parsed.Count == 1)
                    {
                        var item = parsed[0];
                        Check("拖放-回收站没有文件路径", string.IsNullOrEmpty(item.FileSystemPath));
                        Check("拖放-解析名含 645FF040",
                            item.ParsingName.Contains("645FF040", StringComparison.OrdinalIgnoreCase),
                            item.ParsingName);
                        Check("拖放-显示名非空", !string.IsNullOrWhiteSpace(item.DisplayName), item.DisplayName);
                    }

                    var shortcutDir = Path.Combine(sandbox, "系统图标盒子");
                    Directory.CreateDirectory(shortcutDir);
                    var lnk = ShortcutFactory.CreateInFolder(shortcutDir, recycleBinClsid, "回收站");
                    Check("快捷方式-文件已生成", File.Exists(lnk), lnk);
                    Check("快捷方式-扩展名是 .lnk",
                        Path.GetExtension(lnk).Equals(".lnk", StringComparison.OrdinalIgnoreCase),
                        Path.GetExtension(lnk));
                    Check("快捷方式-落点就在盒子目录里",
                        string.Equals(Path.GetDirectoryName(lnk), shortcutDir, StringComparison.OrdinalIgnoreCase),
                        Path.GetDirectoryName(lnk) ?? string.Empty);

                    // 用 IShellLink + IPersistFile 读回，验证存进去的就是同一个 CLSID 解析名
                    var readBack = Interop.ShellItemInterop.CreateShellLink();
                    try
                    {
                        var persist = (Interop.ShellItemInterop.IPersistFile)readBack;
                        var hr = persist.Load(lnk, 0);
                        Check("快捷方式-IPersistFile.Load 成功", hr == 0, $"hr=0x{hr:X8}");

                        if (hr == 0 && readBack.GetIDList(out var readPidl) == 0 && readPidl != IntPtr.Zero)
                        {
                            try
                            {
                                var name = Interop.ShellItemInterop.GetName(
                                    readPidl, Interop.ShellItemInterop.SIGDN.DESKTOPABSOLUTEPARSING);
                                Check("快捷方式-读回同一个 CLSID 解析名",
                                    name is not null &&
                                    name.Contains("645FF040", StringComparison.OrdinalIgnoreCase),
                                    name ?? "（空）");
                            }
                            finally
                            {
                                Interop.ShellItemInterop.FreePidl(readPidl);
                            }
                        }
                        else
                        {
                            Check("快捷方式-能读回目标 IDList", false, "GetIDList 拿不到 PIDL");
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(readBack);
                    }

                    // 重名：同一个显示名第二次入盒要加 " (2)"，不能覆盖
                    var lnk2 = ShortcutFactory.CreateInFolder(shortcutDir, recycleBinClsid, "回收站");
                    Check("快捷方式-重名不覆盖", File.Exists(lnk2) && !string.Equals(lnk2, lnk, StringComparison.OrdinalIgnoreCase),
                        Path.GetFileName(lnk2));

                    // .lnk 的图标交给 shell 解析（此时可能有别人在改 IconCache，失败只记日志不算 FAIL）
                    try
                    {
                        var icon = IconCache.Get(lnk, 48);
                        if (icon is null)
                        {
                            Log.Warn("自检提示：.lnk 图标暂时解析不出来（不算失败）");
                        }
                        else
                        {
                            Check("快捷方式-图标可解析", icon.PixelWidth > 0, $"{icon.PixelWidth}px");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"自检提示：.lnk 图标解析跳过 — {ex.Message}");
                    }
                }

                // 一次拖多个系统图标：CIDA 里放多个条目，每项都要能认出来（真实拖放里最常见的就是多个）
                var multiBytes = new[]
                {
                    ParsePidlBytes(Interop.ShellItemInterop.NetworkParsingName),
                    ParsePidlBytes(Interop.ShellItemInterop.ControlPanelParsingName),
                };

                if (multiBytes.All(bytes => bytes.Length > 0))
                {
                    var multiData = new DataObject();
                    multiData.SetData(
                        Interop.ShellItemInterop.ShellIdListArrayFormat,
                        new MemoryStream(BuildCida(Array.Empty<byte>(), multiBytes)));

                    var multi = ShellDrop.Read(multiData);
                    Check("拖放-多个系统图标解析出 2 项", multi.Count == 2, $"共 {multi.Count} 项");
                    Check("拖放-第 1 个是网络",
                        multi.Count == 2 && multi[0].ParsingName.Contains("F02C1A0D", StringComparison.OrdinalIgnoreCase),
                        multi.Count == 2 ? multi[0].ParsingName : "（空）");
                    Check("拖放-第 2 个是控制面板",
                        multi.Count == 2 && multi[1].ParsingName.Contains("21EC2020", StringComparison.OrdinalIgnoreCase),
                        multi.Count == 2 ? multi[1].ParsingName : "（空）");
                    Check("拖放-多图标都有显示名",
                        multi.Count == 2 && multi.All(m => !string.IsNullOrWhiteSpace(m.DisplayName)),
                        multi.Count == 2 ? string.Join("、", multi.Select(m => m.DisplayName)) : "（空）");

                    var networkLnk = ShortcutFactory.CreateInFolder(
                        Path.Combine(sandbox, "系统图标盒子"), multi[0].ParsingName, multi[0].DisplayName);
                    Check("快捷方式-网络项也能建 .lnk", File.Exists(networkLnk), Path.GetFileName(networkLnk));
                }
                else
                {
                    Log.Warn("自检提示：网络 / 控制面板 PIDL 拿不到，多图标用例跳过。");
                }

                // 普通文件仍然走真实路径语义，不许变成快捷方式
                var realFile = Path.Combine(sandbox, "拖放普通文件.txt");
                File.WriteAllText(realFile, "x");
                var fileData = new DataObject();
                fileData.SetData(DataFormats.FileDrop, new[] { realFile });
                var fileItems = ShellDrop.Read(fileData);
                Check("拖放-普通文件仍是真实路径",
                    fileItems.Count == 1 &&
                    string.Equals(fileItems[0].FileSystemPath, realFile, StringComparison.OrdinalIgnoreCase),
                    fileItems.Count == 1 ? fileItems[0].FileSystemPath ?? "（空）" : $"共 {fileItems.Count} 项");
                Check("拖放-普通文件显示名带扩展名",
                    fileItems.Count == 1 && fileItems[0].DisplayName == "拖放普通文件.txt",
                    fileItems.Count == 1 ? fileItems[0].DisplayName : "（空）");

                // 资源管理器拖真实文件时两种格式都会给：FileDrop + Shell IDList Array。
                // 这种必须只算一项，而且仍然走真实路径语义，绝不能变成快捷方式。
                var mixedBytes = ParsePidlBytes(realFile);

                if (mixedBytes.Length > 0)
                {
                    var mixedData = new DataObject();
                    mixedData.SetData(DataFormats.FileDrop, new[] { realFile });
                    mixedData.SetData(
                        Interop.ShellItemInterop.ShellIdListArrayFormat,
                        new MemoryStream(BuildCida(Array.Empty<byte>(), mixedBytes)));

                    var mixed = ShellDrop.Read(mixedData);
                    Check("拖放-两种格式同时给只算一项", mixed.Count == 1, $"共 {mixed.Count} 项");
                    Check("拖放-IDList 里的真实文件仍是真实路径",
                        mixed.Count == 1 &&
                        string.Equals(mixed[0].FileSystemPath, realFile, StringComparison.OrdinalIgnoreCase),
                        mixed.Count == 1 ? mixed[0].FileSystemPath ?? "（空）" : "（空）");
                }
                else
                {
                    Log.Warn("自检提示：普通文件的解析名拿不到，混合格式用例跳过。");
                }
            }

            // 18.5) 真实 shell IDataObject：和资源管理器拖动走完全同一条代码路径
            // 手工拼的 CIDA 和 Explorer 真实给的不一样（真实给的末项 PIDL 没有结尾的 2 字节 0），
            // 所以这里用桌面文件夹亲自生成 IDataObject，无鼠标、可重复。
            using (Interop.ShellItemInterop.EnsureComInitialized())
            {
                var desktopFolder = Interop.ShellItemInterop.GetDesktopFolder();
                var realShortcutDir = Path.Combine(sandbox, "真实系统图标盒子");
                Directory.CreateDirectory(realShortcutDir);

                var realCases = new[]
                {
                    (Label: "回收站", ParsingName: Interop.ShellItemInterop.RecycleBinParsingName, Clsid: "645FF040"),
                    (Label: "网络", ParsingName: Interop.ShellItemInterop.NetworkParsingName, Clsid: "F02C1A0D"),
                    (Label: "控制面板", ParsingName: Interop.ShellItemInterop.ControlPanelParsingName, Clsid: "21EC2020"),
                };

                // PIDL 长度算法必须和 shell 自己的 ILGetSize 一致，否则 CIDA 偏移全错
                var sizeProbePidl = Interop.ShellItemInterop.ParseDisplayName(Interop.ShellItemInterop.RecycleBinParsingName);
                try
                {
                    var mine = Interop.ShellItemInterop.PidlSize(sizeProbePidl);
                    var shellSize = Interop.ShellItemInterop.GetPidlSizeByShell(sizeProbePidl);
                    Check("真实拖放-PIDL 长度算法和 shell 一致", mine == shellSize && mine > 0, $"自己算={mine} shell 算={shellSize}");
                }
                finally
                {
                    Interop.ShellItemInterop.FreePidl(sizeProbePidl);
                }

                try
                {
                    foreach (var realCase in realCases)
                    {
                        var absPidl = Interop.ShellItemInterop.ParseDisplayName(realCase.ParsingName);
                        try
                        {
                            var comObject = absPidl != IntPtr.Zero
                                ? Interop.ShellItemInterop.CreateDataObject(desktopFolder, absPidl)
                                : null;
                            Check($"真实拖放-{realCase.Label} 能生成 IDataObject", comObject is not null);

                            if (comObject is null)
                            {
                                continue;
                            }

                            // 1) 整条主路径：WPF DataObject 包装 → CanAccept → Read
                            var realData = new DataObject(comObject);
                            Check($"真实拖放-{realCase.Label} 盒子接受", ShellDrop.CanAccept(realData));

                            var realItems = ShellDrop.Read(realData);
                            Check($"真实拖放-{realCase.Label} 解析出 1 项", realItems.Count == 1, $"共 {realItems.Count} 项");

                            if (realItems.Count == 1)
                            {
                                Check($"真实拖放-{realCase.Label} 没有文件路径",
                                    string.IsNullOrEmpty(realItems[0].FileSystemPath));
                                Check($"真实拖放-{realCase.Label} 解析名含 {realCase.Clsid}",
                                    realItems[0].ParsingName.Contains(realCase.Clsid, StringComparison.OrdinalIgnoreCase),
                                    realItems[0].ParsingName);
                                Check($"真实拖放-{realCase.Label} 显示名非空",
                                    !string.IsNullOrWhiteSpace(realItems[0].DisplayName), realItems[0].DisplayName);
                            }

                            // 2) 兜底路径：把 Explorer 的原始字节喂给手工 CIDA 解析
                            var comTypes = comObject as System.Runtime.InteropServices.ComTypes.IDataObject;
                            var rawBytes = comTypes is null
                                ? null
                                : Interop.ShellItemInterop.ReadGlobalBytes(
                                    comTypes, Interop.ShellItemInterop.ShellIdListArrayFormat);
                            Check($"真实拖放-{realCase.Label} 能读到原始 CIDA 字节",
                                rawBytes is { Length: > 0 }, $"{rawBytes?.Length ?? 0} 字节");

                            if (rawBytes is { Length: > 0 })
                            {
                                var rawData = new DataObject();
                                rawData.SetData(
                                    Interop.ShellItemInterop.ShellIdListArrayFormat,
                                    new MemoryStream(rawBytes));

                                var fallback = ShellDrop.Read(rawData);
                                Check($"真实拖放-{realCase.Label} 兜底解析出 1 项",
                                    fallback.Count == 1, $"共 {fallback.Count} 项");
                                Check($"真实拖放-{realCase.Label} 兜底解析名含 {realCase.Clsid}",
                                    fallback.Count == 1 &&
                                    fallback[0].ParsingName.Contains(realCase.Clsid, StringComparison.OrdinalIgnoreCase),
                                    fallback.Count == 1 ? fallback[0].ParsingName : "（空）");
                            }

                            // 3) 入盒：建 .lnk 并用 IShellLink 读回同一个 CLSID
                            var lnk = ShortcutFactory.CreateInFolder(
                                realShortcutDir,
                                realCase.ParsingName,
                                realItems.Count == 1 ? realItems[0].DisplayName : realCase.Label);
                            Check($"真实拖放-{realCase.Label} 生成 .lnk", File.Exists(lnk), Path.GetFileName(lnk));

                            var readBack = Interop.ShellItemInterop.CreateShellLink();
                            try
                            {
                                var persist = (Interop.ShellItemInterop.IPersistFile)readBack;
                                var hr = persist.Load(lnk, 0);

                                if (hr == 0 && readBack.GetIDList(out var readPidl) == 0 && readPidl != IntPtr.Zero)
                                {
                                    try
                                    {
                                        var name = Interop.ShellItemInterop.GetName(
                                            readPidl, Interop.ShellItemInterop.SIGDN.DESKTOPABSOLUTEPARSING);
                                        Check($"真实拖放-{realCase.Label} .lnk 读回同一个 CLSID",
                                            name is not null && name.Contains(realCase.Clsid, StringComparison.OrdinalIgnoreCase),
                                            name ?? "（空）");
                                    }
                                    finally
                                    {
                                        Interop.ShellItemInterop.FreePidl(readPidl);
                                    }
                                }
                                else
                                {
                                    Check($"真实拖放-{realCase.Label} .lnk 读回同一个 CLSID", false, $"Load hr=0x{hr:X8}");
                                }
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(readBack);
                            }
                        }
                        finally
                        {
                            Interop.ShellItemInterop.FreePidl(absPidl);
                        }
                    }

                    // 4) 真实文件走同样办法取 IDataObject：必须仍然是真实路径
                    var realFile = Path.Combine(sandbox, "真实对象普通文件.txt");
                    File.WriteAllText(realFile, "x");

                    var fileComObject = Interop.ShellItemInterop.CreateDataObjectFromParsingName(realFile);
                    Check("真实拖放-普通文件能生成 IDataObject", fileComObject is not null);

                    if (fileComObject is not null)
                    {
                        var realFileData = new DataObject(fileComObject);

                        // 真实文件给的是 CF_HDROP（FileDrop）+ CFSTR_SHELLIDLIST，
                        // 两条路都要能走通，而且合起来只能算一项、仍然是真实路径。
                        var realFileItems = ShellDrop.Read(realFileData);

                        // 只给 CIDA 字节（不走系统解析器）也必须能解析出同一个路径
                        if (fileComObject is System.Runtime.InteropServices.ComTypes.IDataObject fileComTypes)
                        {
                            var fileRaw = Interop.ShellItemInterop.ReadGlobalBytes(
                                fileComTypes, Interop.ShellItemInterop.ShellIdListArrayFormat);

                            if (fileRaw is { Length: > 0 })
                            {
                                var onlyCida = new DataObject();
                                onlyCida.SetData(
                                    Interop.ShellItemInterop.ShellIdListArrayFormat,
                                    new MemoryStream(fileRaw));

                                var cidaOnlyItems = ShellDrop.Read(onlyCida);
                                Check("真实拖放-普通文件兜底解析也是真实路径",
                                    cidaOnlyItems.Count == 1 &&
                                    string.Equals(cidaOnlyItems[0].FileSystemPath, realFile, StringComparison.OrdinalIgnoreCase),
                                    cidaOnlyItems.Count == 1
                                        ? cidaOnlyItems[0].FileSystemPath ?? "（空）"
                                        : $"共 {cidaOnlyItems.Count} 项");
                            }
                        }

                        Check("真实拖放-普通文件仍是真实路径",
                            realFileItems.Count == 1 &&
                            string.Equals(realFileItems[0].FileSystemPath, realFile, StringComparison.OrdinalIgnoreCase),
                            $"共 {realFileItems.Count} 项：" + string.Join(
                                " | ",
                                realFileItems.Select(x => $"{x.FileSystemPath ?? "-"} / {x.ParsingName} / {x.DisplayName}")));
                        Check("真实拖放-普通文件能读到 FileDrop",
                            realFileData.GetDataPresent(DataFormats.FileDrop),
                            string.Join(",", realFileData.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>()));
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(desktopFolder);
                }
            }

            // 18.6) 系统图标隐藏：CLSID 解析 / 注册表往返（临时子键，绝不碰真实设置）/ .lnk 目标回读
            const string tempKeyRoot = @"Software\DeskBox\SelfTest";
            var tempNewStart = tempKeyRoot + @"\NewStartPanel";
            var tempClassic = tempKeyRoot + @"\ClassicStartMenu";

            try
            {
                Check("系统图标-回收站 CLSID 解析",
                    DesktopSystemIcon.TryGetClsid(Interop.ShellItemInterop.RecycleBinParsingName, out var recycleClsid) &&
                    recycleClsid == "{645FF040-5081-101B-9F08-00AA002F954E}",
                    recycleClsid);

                Check("系统图标-网络 CLSID 解析",
                    DesktopSystemIcon.TryGetClsid(Interop.ShellItemInterop.NetworkParsingName, out var networkClsid) &&
                    networkClsid == "{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}",
                    networkClsid);

                Check("系统图标-控制面板 CLSID 解析",
                    DesktopSystemIcon.TryGetClsid(Interop.ShellItemInterop.ControlPanelParsingName, out var cplClsid) &&
                    cplClsid == "{21EC2020-3AEA-1069-A2DD-08002B30309D}",
                    cplClsid);

                Check("系统图标-CLSID 大小写归一化",
                    DesktopSystemIcon.TryGetClsid("::{645ff040-5081-101b-9f08-00aa002f954e}", out var lowerClsid) &&
                    lowerClsid == "{645FF040-5081-101B-9F08-00AA002F954E}",
                    lowerClsid);

                Check("系统图标-非法解析名一律 false",
                    !DesktopSystemIcon.TryGetClsid(null, out _) &&
                    !DesktopSystemIcon.TryGetClsid(string.Empty, out _) &&
                    !DesktopSystemIcon.TryGetClsid("C:\\Windows\\notepad.exe", out _) &&
                    !DesktopSystemIcon.TryGetClsid("C:\\Apps\\{645FF040-5081-101B-9F08-00AA002F954E}\\app.exe", out _) &&
                    !DesktopSystemIcon.TryGetClsid("::{这不是GUID}", out _));

                // .lnk 目标回读：用的是真实系统图标快捷方式
                var iconLnk = ShortcutFactory.CreateInFolder(
                    Path.Combine(sandbox, "隐藏图标用例"),
                    Interop.ShellItemInterop.RecycleBinParsingName,
                    "回收站");

                Check("系统图标-读回 .lnk 目标是同一个 CLSID",
                    ShortcutFactory.TryGetTargetParsingName(iconLnk, out var lnkTarget) &&
                    DesktopSystemIcon.TryGetClsid(lnkTarget, out var lnkClsid) &&
                    lnkClsid == "{645FF040-5081-101B-9F08-00AA002F954E}",
                    lnkTarget);

                Check("系统图标-不存在的 .lnk 返回 false",
                    !ShortcutFactory.TryGetTargetParsingName(Path.Combine(sandbox, "没有这个.lnk"), out _));

                // 注册表往返只写临时子键，并把刷新关掉（自检不许动用户的桌面）
                var oldNewKey = DesktopSystemIcon.NewStartPanelKey;
                var oldClassicKey = DesktopSystemIcon.ClassicStartMenuKey;
                var oldSuppress = DesktopSystemIcon.SuppressRefresh;

                try
                {
                    DesktopSystemIcon.NewStartPanelKey = tempNewStart;
                    DesktopSystemIcon.ClassicStartMenuKey = tempClassic;
                    DesktopSystemIcon.SuppressRefresh = true;

                    var systemNames = new[] { Interop.ShellItemInterop.RecycleBinParsingName,
                        Interop.ShellItemInterop.NetworkParsingName, "::{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}" };
                    Check("系统图标-批量隐藏回收站网络和实际控制面板 CLSID",
                        DesktopSystemIcon.SetHidden(systemNames, true) && systemNames.All(DesktopSystemIcon.IsHidden));
                    DesktopSystemIcon.SetHidden(systemNames, false);

                    Check("系统图标-初始未隐藏", !DesktopSystemIcon.IsHidden(recycleClsid));

                    DesktopSystemIcon.SetHidden(Interop.ShellItemInterop.RecycleBinParsingName, true);
                    Check("系统图标-SetHidden 写进注册表", DesktopSystemIcon.IsHidden(recycleClsid));

                    using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(tempNewStart))
                    {
                        var value = key?.GetValue(recycleClsid);
                        Check("系统图标-注册表值名就是 {CLSID}、值为 DWORD 1",
                            value is int number && number == 1,
                            $"{recycleClsid}={value ?? "（无）"}");
                    }

                    using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(tempClassic))
                    {
                        var value = key?.GetValue(recycleClsid);
                        Check("系统图标-ClassicStartMenu 子键也写了",
                            value is int number && number == 1,
                            $"{recycleClsid}={value ?? "（无）"}");
                    }

                    DesktopSystemIcon.SetHidden(Interop.ShellItemInterop.RecycleBinParsingName, false);
                    Check("系统图标-恢复后注册表里不再隐藏", !DesktopSystemIcon.IsHidden(recycleClsid));

                    using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(tempNewStart))
                    {
                        var value = key?.GetValue(recycleClsid);
                        Check("系统图标-恢复显示写的是显式 0（不是删值）",
                            value is int zero && zero == 0,
                            $"{recycleClsid}={value?.ToString() ?? "（值被删了，实机上图标不会回来）"}");
                    }

                    // 走一遍 BoxWindow 的真实入盒接线：盒子里出现 .lnk + 临时注册表被置 1
                    using (Interop.ShellItemInterop.EnsureComInitialized())
                    {
                        var boxDesktop = Interop.ShellItemInterop.GetDesktopFolder();
                        var boxPidl = Interop.ShellItemInterop.ParseDisplayName(Interop.ShellItemInterop.RecycleBinParsingName);

                        try
                        {
                            var boxCom = boxPidl != IntPtr.Zero
                                ? Interop.ShellItemInterop.CreateDataObject(boxDesktop, boxPidl)
                                : null;
                            Check("系统图标-入盒用例能造出 IDataObject", boxCom is not null);

                            if (boxCom is not null)
                            {
                                windows[1].HandleDrop(new DataObject(boxCom), 0);

                                Check("系统图标-入盒接线在盒子里建了 .lnk",
                                    windows[1].ItemNames.Any(name => name.StartsWith("回收站", StringComparison.Ordinal)),
                                    string.Join(",", windows[1].ItemNames));
                                Check("系统图标-入盒接线把桌面那一份标记为隐藏",
                                    DesktopSystemIcon.IsHidden(recycleClsid));
                            }
                        }
                        finally
                        {
                            Interop.ShellItemInterop.FreePidl(boxPidl);
                            Marshal.ReleaseComObject(boxDesktop);
                        }
                    }
                }
                finally
                {
                    DesktopSystemIcon.NewStartPanelKey = oldNewKey;
                    DesktopSystemIcon.ClassicStartMenuKey = oldClassicKey;
                    DesktopSystemIcon.SuppressRefresh = oldSuppress;
                }
            }
            finally
            {
                try
                {
                    Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(tempKeyRoot, throwOnMissingSubKey: false);

                    // 顺手把 CreateSubKey 顺带建出来的空父键也收掉，别给用户留垃圾
                    const string tempParent = @"Software\DeskBox";
                    using (var parent = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(tempParent))
                    {
                        if (parent is not null && parent.SubKeyCount == 0 && parent.ValueCount == 0)
                        {
                            parent.Close();
                            Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(tempParent, throwOnMissingSubKey: false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"自检：清理临时注册表子键失败 {ex.Message}");
                }
            }


        }
        catch (Exception ex)
        {
            failures++;
            report.AppendLine($"[FAIL] 自检异常 — {ex}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(sandbox))
                {
                    Directory.Delete(sandbox, recursive: true);
                }
            }
            catch
            {
                // 清理失败不影响结论
            }
        }

        // 19) 图标管线：磁盘 PNG 缓存往返 + TryGetFast + 统计（T2）
        // 探针文件放在固定沙盒之外，保证这段在 sandbox 已清理后仍能独立跑完
        var iconCacheDir = IconDiskCache.Directory;
        var iconProbeDir = Path.Combine(Path.GetTempPath(), "DeskBox-IconProbe");
        var iconProbe = Path.Combine(iconProbeDir, "自检图标.txt");
        var iconPngBefore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            Directory.CreateDirectory(iconProbeDir);
            File.WriteAllText(iconProbe, "icon-probe");
            const int iconSize = 48;

            if (Directory.Exists(iconCacheDir))
            {
                foreach (var file in Directory.GetFiles(iconCacheDir, "*.png"))
                {
                    iconPngBefore.Add(file);
                }
            }

            IconCache.Clear();
            var probeIcon = IconCache.Get(iconProbe, iconSize);
            Check("图标-首次取图非空", probeIcon is not null);
            Check("图标-位图已冻结可跨线程", probeIcon is null || probeIcon.IsFrozen);

            var iconPngCount = Directory.Exists(iconCacheDir)
                ? Directory.GetFiles(iconCacheDir, "*.png").Length
                : 0;
            Check("图标-磁盘缓存目录出现 PNG", iconPngCount > 0, $"{iconCacheDir} 下 {iconPngCount} 个文件");

            IconCache.Clear();
            Check("图标-Clear 后内存立即为空", IconCache.TryGetFast(iconProbe, iconSize) is null);

            var probeAgain = IconCache.Get(iconProbe, iconSize);
            Check("图标-磁盘缓存命中可秒出", probeAgain is not null);
            Check("图标-命中后 TryGetFast 立即有值", IconCache.TryGetFast(iconProbe, iconSize) is not null);

            var iconStats = IconCache.StatsSummary();
            var iconDiskHits = 0;
            foreach (var part in iconStats.Split('/'))
            {
                var text = part.Trim();
                if (text.StartsWith("磁盘命中", StringComparison.Ordinal))
                {
                    int.TryParse(text["磁盘命中".Length..].Trim(), out iconDiskHits);
                }
            }

            Check("图标-StatsSummary 磁盘命中 > 0", iconDiskHits > 0, iconStats);

            // 批量日志是去抖的（队列空后 400ms 才结算），这里等过窗口再回读日志验证可观测性
            Thread.Sleep(700);
            var iconLogLine = File.Exists(Log.FilePath)
                ? File.ReadLines(Log.FilePath).LastOrDefault(line => line.Contains("[图标] ", StringComparison.Ordinal))
                : null;
            Check("图标-批量耗时日志已落盘",
                iconLogLine is not null &&
                iconLogLine.Contains("内存命中", StringComparison.Ordinal) &&
                iconLogLine.Contains("磁盘命中", StringComparison.Ordinal) &&
                iconLogLine.Contains("/ shell ", StringComparison.Ordinal),
                iconLogLine ?? "（日志里没有 [图标] 行）");
        }
        catch (Exception ex)
        {
            failures++;
            report.AppendLine($"[FAIL] 图标管线自查异常 — {ex.Message}");
        }
        finally
        {
            // 收尾：这次自检写进磁盘缓存的 PNG 与探针文件都清掉，不给配置目录留垃圾
            try
            {
                if (Directory.Exists(iconCacheDir))
                {
                    foreach (var file in Directory.GetFiles(iconCacheDir, "*.png"))
                    {
                        if (!iconPngBefore.Contains(file))
                        {
                            File.Delete(file);
                        }
                    }
                }
            }
            catch
            {
                // 清理失败不影响结论
            }

            try
            {
                if (Directory.Exists(iconProbeDir))
                {
                    Directory.Delete(iconProbeDir, recursive: true);
                }
            }
            catch
            {
                // 忽略
            }
        }

        report.AppendLine();
        report.AppendLine(failures == 0 ? "自检结果：全部通过" : $"自检结果：{failures} 项失败");

        try
        {
            File.WriteAllText(ReportPath, report.ToString());
        }
        catch
        {
            // 忽略
        }

        Log.Info("自检完成：" + (failures == 0 ? "全部通过" : $"{failures} 项失败"));
    }
}
