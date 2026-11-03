using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using DeskBox.Models;
using DeskBox.Views;

namespace DeskBox.Services;

internal static class VirtualGroupSelfTest
{
    internal static void Run(Action<string, bool, string> check, string sandbox)
    {
        void Test(string name, bool ok) => check("虚拟分组-" + name, ok, "");
        var root = Path.Combine(sandbox, "virtual-groups");
        Directory.CreateDirectory(root);
        var windows = new List<BoxWindow>();
        try
        {
            var oldFolder = Path.Combine(root, "旧盒子");
            Directory.CreateDirectory(oldFolder);
            var first = Path.Combine(oldFolder, "A.txt");
            var second = Path.Combine(oldFolder, "B.url");
            File.WriteAllText(first, "source A");
            File.WriteAllText(second, "[InternetShortcut]\nURL=https://example.com\n");
            File.WriteAllText(Path.Combine(oldFolder, "desktop.ini"), "metadata");
            var old = new BoxConfig
            {
                Title = "保留标题", Folder = oldFolder, X = 251, Y = 182, Width = 420, Height = 310,
                Locked = true, ItemOrder = new() { "B.url", "A.txt" },
            };
            var config = new AppConfig { BoxRoot = Path.Combine(root, "不应创建的文件夹"), Boxes = new() { old } };
            var id = old.Id;
            Test("旧盒子迁移成功", VirtualGroups.Migrate(config));
            Test("迁移不改变名称布局和ID", old.Title == "保留标题" && old.Id == id && old.X == 251 && old.Y == 182 &&
                old.Width == 420 && old.Height == 310 && old.Locked);
            Test("迁移保留原文件和文件夹", File.ReadAllText(first) == "source A" && File.Exists(second) && Directory.Exists(oldFolder));
            Test("迁移保留顺序且忽略desktop.ini", old.IsVirtual && old.Entries.Select(entry => entry.Path).SequenceEqual(new[] { second, first }) &&
                old.ItemOrder.SequenceEqual(new[] { second, first }));
            Test("迁移幂等", !VirtualGroups.Migrate(config));
            var unavailable = new BoxConfig { Folder = Path.Combine(root, "离线目录") };
            Test("目录离线不丢失旧记录", !VirtualGroups.Convert(unavailable, root) && !unavailable.IsVirtual);

            var otherFolder = Path.Combine(root, "另一来源");
            Directory.CreateDirectory(otherFolder);
            var sameName = Path.Combine(otherFolder, "A.txt");
            File.WriteAllText(sameName, "different A");
            VirtualGroups.Add(config, old, new[] { new GroupEntry { Path = sameName }, new GroupEntry { Path = first.ToUpperInvariant() } });
            Test("同名文件按完整路径区分且大小写去重", old.Entries.Count == 3);
            var target = new BoxConfig { Title = "分组B", IsVirtual = true };
            config.Boxes.Add(target);
            VirtualGroups.Add(config, target, new[] { new GroupEntry { Path = first } });
            Test("跨组只移动归属", !old.Entries.Any(entry => entry.Path == first) && target.Entries.Single().Path == first && File.ReadAllText(first) == "source A");
            Test("移出只删除引用", VirtualGroups.Remove(target, new[] { first }) == 1 && File.Exists(first));

            // 经过真实 WPF 拖放入口，不能只测存储层。
            old.Locked = false;
            var shell = new DesktopShell(Path.Combine(root, "模拟桌面"));
            var a = new BoxWindow(config, old, shell);
            var b = new BoxWindow(config, target, shell);
            windows.Add(a);
            windows.Add(b);
            foreach (var window in windows)
                window.MembershipChanged += (_, _) => { foreach (var peer in windows) peer.RefreshFromConfig(reloadItems: true); };
            var drop = new DataObject();
            drop.SetData(DataFormats.FileDrop, new[] { first, sameName });
            b.HandleDrop(drop, 0);
            Test("窗口拖入保留两个同名原文件", b.ItemsHost.Items.Count == 2 && File.ReadAllText(first) == "source A" && File.ReadAllText(sameName) == "different A");
            Test("新组不创建存储目录", !Directory.Exists(config.EffectiveBoxRoot) && string.IsNullOrEmpty(target.Folder));
            var reorder = new DataObject();
            reorder.SetData(DataFormats.FileDrop, new[] { sameName });
            reorder.SetData(BoxWindow.VirtualDragFormat, target.Id);
            b.HandleDrop(reorder, 0);
            Test("同名项目独立排序", target.ItemOrder[0] == sameName && b.ItemsHost.Items.Cast<BoxItem>().First().Path == sameName);
            var cross = new DataObject();
            cross.SetData(DataFormats.FileDrop, new[] { sameName });
            cross.SetData(BoxWindow.VirtualDragFormat, target.Id);
            a.HandleDrop(cross, 0);
            Test("跨组拖放即时刷新来源", b.ItemsHost.Items.Count == 1 && a.ItemsHost.Items.Cast<BoxItem>().First().Path == sameName && File.Exists(sameName));

            const string systemPath = "::{645FF040-5081-101B-9F08-00AA002F954E}";
            var systemDrop = new DataObject();
            systemDrop.SetData(DataFormats.FileDrop, new[] { systemPath });
            var filesBefore = Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories).Length;
            b.HandleDrop(systemDrop, 0);
            Test("系统图标直接保存解析名不创建快捷方式", target.Entries.Any(entry => entry.Path == systemPath) &&
                Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories).Length == filesBefore);
            Test("系统图标有显示名和图标", b.ItemsHost.Items.Cast<BoxItem>().First().Name != systemPath && IconCache.Get(systemPath, 37) is not null);

            Pump(250); // 安装延后到首帧之后的目录监听。
            var renamed = Path.Combine(oldFolder, "外部重命名.txt");
            File.Move(first, renamed);
            Pump(750);
            Test("外部重命名自动更新路径和排序", target.Entries.Any(entry => entry.Path == renamed) &&
                target.ItemOrder.Contains(renamed) && !target.Entries.Any(entry => entry.Path == first));
            File.Delete(renamed);
            Pump(500);
            var missing = b.ItemsHost.Items.Cast<BoxItem>().FirstOrDefault(item => item.Path == renamed);
            Test("原文件暂不可用保留引用并提示", target.Entries.Any(entry => entry.Path == renamed) && missing?.Meta == "原项目暂不可用");
            File.WriteAllText(renamed, "returned");
            Pump(500);
            Test("原文件恢复自动显示", b.ItemsHost.Items.Cast<BoxItem>().Any(item => item.Path == renamed && item.Meta != "原项目暂不可用"));

            var serialized = JsonSerializer.Serialize(config);
            var back = JsonSerializer.Deserialize<AppConfig>(serialized)!;
            Test("配置往返保留布局引用和完整路径顺序", back.Boxes[0].Id == old.Id && back.Boxes[0].X == 251 && back.Boxes[0].IsVirtual &&
                back.Boxes[1].Entries.Any(entry => entry.Path == systemPath && entry.DisplayName.Length > 0) &&
                back.Boxes[1].ItemOrder.SequenceEqual(target.ItemOrder));
            var removalRequested = false;
            b.RemovalRequested += (_, _) => removalRequested = true;
            b.Dissolve();
            Test("解散保留所有原文件不生成桌面副本", removalRequested && target.Entries.Count == 0 && File.ReadAllText(renamed) == "returned" &&
                !Directory.Exists(shell.DesktopPath));

            var offlinePath = Path.Combine(root, "尚未挂载", "重新出现.txt");
            VirtualGroups.Add(config, target, new[] { new GroupEntry { Path = offlinePath } });
            b.RefreshFromConfig(reloadItems: true);
            Test("离线原目录仍显示分组和引用", b.ItemsHost.Items.Cast<BoxItem>().Single().Meta == "原项目暂不可用");
            Directory.CreateDirectory(Path.GetDirectoryName(offlinePath)!);
            File.WriteAllText(offlinePath, "mounted");
            b.RecheckReferenceLocations();
            Test("原目录恢复重新连接监听和内容", b.ItemsHost.Items.Cast<BoxItem>().Single().Meta != "原项目暂不可用");

            var privateDesktop = Path.Combine(root, "用户桌面");
            var publicDesktop = Path.Combine(root, "公共桌面");
            Directory.CreateDirectory(privateDesktop);
            Directory.CreateDirectory(publicDesktop);
            var personal = Path.Combine(privateDesktop, "应用.url");
            var shared = Path.Combine(publicDesktop, "应用.url");
            File.WriteAllText(personal, "personal");
            File.WriteAllText(shared, "public");
            var desktop = new DesktopShell(privateDesktop, publicDesktop);
            var desktopItems = desktop.EnumerateItems(config.EffectiveBoxRoot, false);
            Test("个人和公共桌面同名快捷方式均可枚举", desktopItems.Contains(personal) && desktopItems.Contains(shared));
            var plan = Organizer.BuildPlan(desktopItems, true, out _);
            var organizeConfig = new AppConfig { BoxRoot = config.BoxRoot };
            var result = VirtualGroups.Organize(organizeConfig, plan);
            organizeConfig.LastOrganize = result.Moves;
            Test("一键整理只记录关系", result.Moves.Count == 2 && result.Moves.All(record => record.From == record.To && record.GroupId.Length > 0) &&
                File.ReadAllText(personal) == "personal" && File.ReadAllText(shared) == "public");
            Test("重复整理不会复制或覆盖已归组项目", VirtualGroups.Organize(organizeConfig, plan).Moves.Count == 0);
            Test("撤销分组不移动原文件", VirtualGroups.Undo(organizeConfig, organizeConfig.LastOrganize) == 2 &&
                organizeConfig.LastOrganize.Count == 0 && File.Exists(personal) && File.Exists(shared));

            result = VirtualGroups.Organize(organizeConfig, plan);
            organizeConfig.LastOrganize = result.Moves;
            var manual = new BoxConfig { IsVirtual = true, Title = "手动调整" };
            organizeConfig.Boxes.Add(manual);
            VirtualGroups.Add(organizeConfig, manual, new[] { new GroupEntry { Path = personal } });
            VirtualGroups.Undo(organizeConfig, organizeConfig.LastOrganize);
            Test("撤销不覆盖整理后的手动跨组调整", manual.Entries.Single().Path == personal);

            // 升级前物理整理的撤销仍然可用，且不覆盖桌面新出现的同名文件。
            var legacySource = Path.Combine(oldFolder, "旧版副本 (2).txt");
            var original = Path.Combine(privateDesktop, "旧版副本.txt");
            File.WriteAllText(legacySource, "legacy");
            File.WriteAllText(original, "new desktop file");
            VirtualGroups.Add(config, old, new[] { new GroupEntry { Path = legacySource } });
            config.LastOrganize = new() { new MoveRecord { From = original, To = legacySource } };
            Test("旧版撤销保留冲突文件并同步真实路径", VirtualGroups.Undo(config, config.LastOrganize) == 1 &&
                File.ReadAllText(original) == "new desktop file" && old.Entries.Any(entry => entry.Path == Path.Combine(privateDesktop, "旧版副本 (2).txt")));

            var movedParent = Path.Combine(root, "重定向来源");
            VirtualGroups.RenameReference(config, oldFolder, movedParent);
            Test("来源目录迁移同步子项引用和顺序", old.Entries.Any(entry => entry.Path == Path.Combine(movedParent, "B.url")) &&
                old.ItemOrder.Contains(Path.Combine(movedParent, "B.url")));
        }
        catch (Exception ex) { check("虚拟分组-异常", false, ex.ToString()); }
        finally { foreach (var window in windows) window.Close(); }
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
