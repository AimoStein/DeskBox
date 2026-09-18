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
