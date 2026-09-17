using System.IO;
using System.Text;
using JianZhuo.Models;

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
