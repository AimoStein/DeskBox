using System.IO;
using System.Runtime.InteropServices;
using DeskBox.Interop;
using DeskBox.Models;

namespace DeskBox.Services;

internal static class DesktopCatalogSelfTest
{
    internal static void Run(Action<string, bool, string> check, string sandbox)
    {
        var root = Path.Combine(sandbox, "desktop-catalog");
        var desktop = Directory.CreateDirectory(Path.Combine(root, "desktop")).FullName;
        var shared = Directory.CreateDirectory(Path.Combine(root, "public")).FullName;
        var boxRoot = Directory.CreateDirectory(Path.Combine(root, "boxes")).FullName;
        var install = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
        var oldTarget = Path.Combine(Directory.CreateDirectory(Path.Combine(install, "app-1.0.0")).FullName, "Editor.exe");
        var newTarget = Path.Combine(Directory.CreateDirectory(Path.Combine(install, "app-2.0.0")).FullName, "Editor.exe");
        File.WriteAllText(oldTarget, "");
        File.WriteAllText(newTarget, "");

        var original = Path.Combine(boxRoot, "编辑器.lnk");
        var duplicate = Path.Combine(shared, "Editor.lnk");
        var anotherGroup = Path.Combine(boxRoot, "备用名称.lnk");
        var profile = Path.Combine(desktop, "工作配置.lnk");
        Link(original, oldTarget, "");
        Link(duplicate, oldTarget, "");
        Link(anotherGroup, oldTarget, "");
        Link(profile, oldTarget, "--profile=work");
        var first = new BoxConfig { IsVirtual = true, Entries = new() { new() { Path = original } }, ItemOrder = new() { original } };
        var second = new BoxConfig { IsVirtual = true, Entries = new() { new() { Path = anotherGroup } } };
        var cfg = new AppConfig { Boxes = new() { first, second } };
        var shell = new DesktopShell(desktop, shared);
        DesktopCatalog Scan() => DesktopCatalog.Scan(shell, boxRoot, false,
            cfg.Boxes.SelectMany(box => box.Entries).Select(entry => entry.Path).ToList(), null);

        var catalog = Scan().Reconcile(cfg);
        check("快捷方式去重跨个人桌面、公共桌面与盒子",
            catalog.Hidden.Contains((second.Id, anotherGroup)) && catalog.Desktop.All(item => item.Path != duplicate), "");
        check("同一程序的不同配置入口继续可见", catalog.Desktop.Any(item => item.Path == profile), "");
        check("合并仅影响显示，原快捷方式仍存在", File.Exists(duplicate) && File.Exists(anotherGroup), "");

        File.Delete(oldTarget);
        File.Delete(original);
        var replacement = Path.Combine(desktop, "编辑器新版.lnk");
        Link(replacement, newTarget, "");
        catalog = Scan().Reconcile(cfg);
        check("升级替换失效入口时保留盒子和顺序",
            first.Entries[0].Path == replacement && first.ItemOrder.SequenceEqual(new[] { replacement }) &&
            first.Entries[0].DisplayName == "编辑器", "");
        check("升级期间桌面入口仍只出现一次", catalog.Desktop.All(item => item.Path != replacement), "");
        check("重复扫描不会重复迁移", !Scan().Reconcile(cfg).ConfigChanged && first.Entries.Count == 1, "");

        var firstUrl = Path.Combine(desktop, "Steam A.url");
        var secondUrl = Path.Combine(shared, "Steam A renamed.url");
        var thirdUrl = Path.Combine(desktop, "Steam B.url");
        File.WriteAllText(firstUrl, "[InternetShortcut]\nURL=steam://rungameid/123");
        File.Copy(firstUrl, secondUrl);
        File.WriteAllText(thirdUrl, "[InternetShortcut]\nURL=steam://rungameid/456");
        var urls = Scan().Reconcile(cfg).Desktop.Count(item => item.Path.EndsWith(".url", StringComparison.OrdinalIgnoreCase));
        check("相同 URL 合并，不同游戏保留", urls == 2, $"显示 {urls} 个链接");
    }

    private static void Link(string path, string target, string arguments)
    {
        using var com = ShellItemInterop.EnsureComInitialized();
        var link = ShellItemInterop.CreateShellLink();
        try
        {
            Marshal.ThrowExceptionForHR(link.SetPath(target));
            Marshal.ThrowExceptionForHR(link.SetArguments(arguments));
            Marshal.ThrowExceptionForHR(link.SetWorkingDirectory(Path.GetDirectoryName(target)!));
            Marshal.ThrowExceptionForHR(((ShellItemInterop.IPersistFile)link).Save(path, true));
        }
        finally { Marshal.ReleaseComObject(link); }
    }
}
