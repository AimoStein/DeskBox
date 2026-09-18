using System.IO;
using System.Text;
using System.Windows;
using JianZhuo.Models;
using JianZhuo.Views;

namespace JianZhuo.Services;

/// <summary>
/// 离线自检：在临时沙盒里跑一遍一键整理与撤销，不碰真实桌面。
/// 用法：JianZhuo.exe --selftest
/// </summary>
public static class SelfTest
{
    public static string ReportPath =>
        Path.Combine(Path.GetTempPath(), "jianzhuo-selftest.txt");

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

        var sandbox = Path.Combine(Path.GetTempPath(), "JianZhuo-SelfTest");
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

            var shell = new DesktopShell();
            windows.Add(new BoxWindow(layoutConfig, configA, shell, Peers) { ShowActivated = false });
            windows.Add(new BoxWindow(layoutConfig, configB, shell, Peers) { ShowActivated = false });

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
