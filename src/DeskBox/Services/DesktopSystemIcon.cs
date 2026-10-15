using Microsoft.Win32;
using DeskBox.Interop;

namespace DeskBox.Services;

/// <summary>
/// 桌面系统图标（回收站 / 此电脑 / 控制面板 / 网络）的显示与隐藏。
/// 这些是 shell 命名空间项，没法像文件那样"移动"，Windows 唯一支持的"从桌面拿走"方式
/// 就是写 HideDesktopIcons 注册表，等价于「设置 → 个性化 → 主题 → 桌面图标设置」里取消勾选。
/// </summary>
public static class DesktopSystemIcon
{
    /// <summary>桌面重读设置用哪一级刷新（日志里会写，方便排查）。</summary>
    public enum RefreshLevel
    {
        /// <summary>没刷新成功。</summary>
        None = 0,

        /// <summary>只通知对应命名空间项从桌面移出或恢复。</summary>
        ItemChanged = 1,

    }

    private const string DefaultNewStartPanelKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";

    private const string DefaultClassicStartMenuKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\ClassicStartMenu";

    /// <summary>自检用：把注册表写到临时子键，别动用户真实设置。</summary>
    internal static string NewStartPanelKey { get; set; } = DefaultNewStartPanelKey;

    internal static string ClassicStartMenuKey { get; set; } = DefaultClassicStartMenuKey;

    /// <summary>自检用：只验注册表读写，不真去刷新桌面。</summary>
    internal static bool SuppressRefresh { get; set; }

    /// <summary>上一次用的刷新策略（日志/自检用）。</summary>
    public static RefreshLevel LastRefreshLevel { get; private set; } = RefreshLevel.None;

    /// <summary>
    /// 从解析名里取出 CLSID，归一化成 <c>{645FF040-5081-101B-9F08-00AA002F954E}</c> 这种大写形式。
    /// 支持 <c>::{CLSID}</c> 以及后面还带子路径的形式；取不到返回 false。
    /// </summary>
    public static bool TryGetClsid(string? parsingName, out string clsid)
    {
        clsid = string.Empty;

        if (string.IsNullOrWhiteSpace(parsingName))
        {
            return false;
        }

        var text = parsingName.Trim();
        if (text.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) text = text[6..];
        if (text.StartsWith("::", StringComparison.Ordinal)) text = text[2..];
        // 普通磁盘路径即使包含 {GUID} 目录，也不是桌面系统图标。
        var start = 0;
        if (!text.StartsWith('{'))
        {
            return false;
        }

        var end = text.IndexOf('}', start + 1);
        if (end <= start + 1)
        {
            return false;
        }

        var raw = text.Substring(start, end - start + 1);
        if (!Guid.TryParse(raw, out var guid))
        {
            return false;
        }

        // 注册表值名统一用大写（比较本身不区分大小写，但日志和排查时看着一致）
        clsid = guid.ToString("B").ToUpperInvariant();
        return true;
    }

    /// <summary>
    /// 隐藏 / 恢复桌面上的这个系统图标。
    /// 两个子键都写：NewStartPanel 是新式开始菜单，ClassicStartMenu 是经典菜单，写全更稳。
    /// 任何失败只记日志，不抛异常。
    /// </summary>
    public static bool SetHidden(string parsingName, bool hidden) => SetHidden(new[] { parsingName }, hidden);

    /// <summary>只更新这一批系统项，不刷新整个桌面或重建全局图标缓存。</summary>
    internal static bool SetHidden(IEnumerable<string> parsingNames, bool hidden)
    {
        var clsids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parsingName in parsingNames)
        {
            if (TryGetClsid(parsingName, out var clsid)) clsids.Add(clsid);
        }
        if (clsids.Count == 0) return false;
        var written = true;
        foreach (var clsid in clsids)
        {
            if (!TryWrite(clsid, hidden)) written = false;
        }

        var level = SuppressRefresh ? RefreshLevel.None : NotifyVisibility(clsids, hidden);
        LastRefreshLevel = level;

        var action = hidden ? "隐藏" : "恢复";
        Log.Info($"系统图标桌面{action}：{string.Join(", ", clsids)}（刷新策略={Describe(level)}）");
        return written && (SuppressRefresh || level != RefreshLevel.None);
    }

    /// <summary>读回注册表里的隐藏状态（只证明设置写进去了，不证明桌面已经刷新）。</summary>
    public static bool IsHidden(string parsingName)
    {
        if (!TryGetClsid(parsingName, out var clsid))
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(NewStartPanelKey);
            var value = key?.GetValue(clsid);

            return value switch
            {
                int number => number != 0,
                uint number => number != 0,
                _ => false,
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"读桌面图标隐藏状态失败 {clsid}：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 向 Shell 通知这些虚拟条目移出/恢复。这里只更新视图成员，不删除文件或系统对象。
    /// 不能用 UPDATEDIR/F5：部分 Explorer 会按缓存重新枚举，导致设置已隐藏而图标仍常驻。
    /// </summary>
    private static RefreshLevel NotifyVisibility(IEnumerable<string> clsids, bool hidden)
    {
        var ok = true;
        using var com = ShellItemInterop.EnsureComInitialized();
        foreach (var clsid in clsids)
        {
            try
            {
                var pidl = ShellItemInterop.ParseDisplayName("::" + clsid);
                if (pidl == IntPtr.Zero) { ok = false; continue; }
                try
                {
                    NativeMethods.SHChangeNotifyIdList(hidden ? 0x4u : 0x2u,
                        0x1000, pidl, IntPtr.Zero); // SHCNE_DELETE/CREATE + SHCNF_IDLIST | SHCNF_FLUSH
                }
                finally { ShellItemInterop.FreePidl(pidl); }
            }
            catch (Exception ex)
            {
                ok = false;
                Log.Warn($"更新桌面系统图标失败 {clsid}：{ex.Message}");
            }
        }
        return ok ? RefreshLevel.ItemChanged : RefreshLevel.None;
    }

    private static bool TryWrite(string clsid, bool hidden)
    {
        var ok = true;

        foreach (var path in new[] { NewStartPanelKey, ClassicStartMenuKey })
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
                if (key is null)
                {
                    ok = false;
                    Log.Warn($"打不开注册表子键 HKCU\\{path}");
                    continue;
                }

                if (hidden)
                {
                    key.SetValue(clsid, 1, RegistryValueKind.DWord);
                }
                else
                {
                    // 恢复显示必须写显式的 DWORD 0。
                    // 实机实测：把这个值删掉再刷新，桌面图标不会回来（Explorer 不认"缺值=显示"），
                    // 写 0 才会回来，所以这里不删值。
                    key.SetValue(clsid, 0, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                ok = false;
                Log.Warn($"写注册表失败 HKCU\\{path}\\{clsid}：{ex.Message}");
            }
        }

        return ok;
    }

    private static string Describe(RefreshLevel level) => level switch
    {
        RefreshLevel.ItemChanged => "逐项 Shell 命名空间通知",
        _ => "未刷新",
    };
}
