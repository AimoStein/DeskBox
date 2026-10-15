using System.IO;
using System.Runtime.InteropServices;
using DeskBox.Interop;

namespace DeskBox.Services;

/// <summary>
/// 给 shell 命名空间项（回收站 / 此电脑 / 控制面板 / 网络这类系统图标）建快捷方式。
/// 盒子目录必须保持是真实文件夹，所以系统图标入盒 = 在盒子目录里放一个指向它的 .lnk。
/// </summary>
public static class ShortcutFactory
{
    /// <summary>
    /// 在 targetFolder 里为命名空间项建 .lnk，重名用 FsUtil.UniquePath 加 " (2)"；返回落点路径；失败抛异常（调用方 catch）。
    /// </summary>
    /// <param name="targetFolder">盒子目录（不存在会自动创建）。</param>
    /// <param name="parsingName">解析名，形如 ::{645FF040-5081-101B-9F08-00AA002F954E}。</param>
    /// <param name="displayName">快捷方式显示名，例如 "回收站"。</param>
    public static string CreateInFolder(string targetFolder, string parsingName, string displayName)
    {
        if (string.IsNullOrWhiteSpace(targetFolder))
        {
            throw new ArgumentException("盒子目录为空，无法创建快捷方式。", nameof(targetFolder));
        }

        if (string.IsNullOrWhiteSpace(parsingName))
        {
            throw new ArgumentException("解析名为空，无法创建快捷方式。", nameof(parsingName));
        }

        Directory.CreateDirectory(targetFolder);

        var name = BuildFileName(displayName, parsingName);
        var target = FsUtil.UniquePath(targetFolder, name);

        // 拖放与 IShellLink 都要求 STA；自己初始化过的话用完回退
        using (ShellItemInterop.EnsureComInitialized())
        {
            var link = ShellItemInterop.CreateShellLink();
            try
            {
                // SetPath 直接吃解析名：shell 会把 ::{GUID} 解析成 IDList 存进 .lnk，
                // 这样图标、双击打开都由系统按原来的命名空间项处理。
                var hr = link.SetPath(parsingName);
                if (hr != ShellItemInterop.S_OK)
                {
                    throw new COMException($"IShellLink.SetPath 失败（0x{hr:X8}）：{parsingName}", hr);
                }

                var persist = (ShellItemInterop.IPersistFile)link;
                hr = persist.Save(target, true);
                if (hr != ShellItemInterop.S_OK)
                {
                    throw new COMException($"IPersistFile.Save 失败（0x{hr:X8}）：{target}", hr);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(link);
            }
        }

        Log.Info($"系统图标已入盒：{target} ← {parsingName}");
        return target;
    }

    /// <summary>
    /// 只读：读回 .lnk 的目标解析名。盒子里的 .lnk 如果指向系统命名空间项
    /// （回收站 / 控制面板 / 网络），这里会拿到 <c>::{CLSID}</c>，用来恢复桌面图标。
    /// 先试 GetPath（普通文件），取不到再 GetIDList + SHGetNameFromIDList（命名空间项）。
    /// 任何失败只返回 false，不抛异常。
    /// </summary>
    public static bool TryGetTargetParsingName(string lnkPath, out string parsingName)
    {
        parsingName = string.Empty;

        if (string.IsNullOrWhiteSpace(lnkPath) || !File.Exists(lnkPath))
        {
            return false;
        }

        try
        {
            using (ShellItemInterop.EnsureComInitialized())
            {
                var link = ShellItemInterop.CreateShellLink();
                try
                {
                    var persist = (ShellItemInterop.IPersistFile)link;
                    if (persist.Load(lnkPath, 0) != ShellItemInterop.S_OK)
                    {
                        return false;
                    }

                    // 普通文件目标：GetPath 能取到真实路径
                    var buffer = new System.Text.StringBuilder(1024);
                    if (link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0) == ShellItemInterop.S_OK)
                    {
                        var path = buffer.ToString();
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            parsingName = path;
                            return true;
                        }
                    }

                    // 命名空间项目标：只能靠 IDList → 解析名
                    if (link.GetIDList(out var pidl) != ShellItemInterop.S_OK || pidl == IntPtr.Zero)
                    {
                        return false;
                    }

                    try
                    {
                        var name = ShellItemInterop.GetName(pidl, ShellItemInterop.SIGDN.DESKTOPABSOLUTEPARSING);
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            return false;
                        }

                        parsingName = name;
                        return true;
                    }
                    finally
                    {
                        ShellItemInterop.FreePidl(pidl);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(link);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读快捷方式目标失败 {lnkPath}：{ex.Message}");
            return false;
        }
    }

    /// <summary>显示名清洗后补上 .lnk；显示名不可用时退回解析名。</summary>
    private static string BuildFileName(string? displayName, string parsingName)
    {
        var baseName = FsUtil.Sanitize(string.IsNullOrWhiteSpace(displayName) ? parsingName : displayName);

        if (baseName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return baseName;
        }

        return baseName + ".lnk";
    }
}
