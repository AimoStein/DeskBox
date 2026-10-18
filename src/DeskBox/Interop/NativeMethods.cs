using System.Runtime.InteropServices;
using System.Text;

namespace DeskBox.Interop;

[StructLayout(LayoutKind.Sequential)]
public struct POINT
{
    public int X;
    public int Y;

    public POINT(int x, int y)
    {
        X = x;
        Y = y;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct SIZE
{
    public int cx;
    public int cy;

    public SIZE(int cx, int cy)
    {
        this.cx = cx;
        this.cy = cy;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
public struct MSLLHOOKSTRUCT
{
    public POINT pt;
    public uint mouseData;
    public uint flags;
    public uint time;
    public IntPtr dwExtraInfo;
}

public enum SIIGBF
{
    RESIZETOFIT = 0x00,
    BIGGERSIZEOK = 0x01,
    MEMORYONLY = 0x02,
    ICONONLY = 0x04,
    THUMBNAILONLY = 0x08,
    INCACHEONLY = 0x10,
}

[ComImport]
[Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItemImageFactory
{
    [PreserveSig]
    int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct SHFILEOPSTRUCT
{
    public IntPtr hwnd;
    public uint wFunc;

    [MarshalAs(UnmanagedType.LPWStr)]
    public string pFrom;

    [MarshalAs(UnmanagedType.LPWStr)]
    public string? pTo;

    public ushort fFlags;

    [MarshalAs(UnmanagedType.Bool)]
    public bool fAnyOperationsAborted;

    public IntPtr hNameMappings;

    [MarshalAs(UnmanagedType.LPWStr)]
    public string? lpszProgressTitle;
}

public static class NativeMethods
{
    public const int WM_COMMAND = 0x0111;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WH_MOUSE_LL = 14;

    /// <summary>资源管理器「显示桌面图标」菜单命令 ID。</summary>
    public const int CMD_TOGGLE_DESKTOP_ICONS = 0x7402;

    public static readonly IntPtr HWND_BOTTOM = new(1);
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>GetWindow 命令：z 序里紧挨在上面的那个窗口。</summary>
    public const uint GW_HWNDPREV = 3;

    /// <summary>GetWindow 命令：z 序里紧挨在下面的那个窗口。</summary>
    public const uint GW_HWNDNEXT = 2;

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;

    /// <summary>对象显示事件：窗口第一次显示 / 重新显示时触发。开机时 Explorer 创建桌面窗口用的就是它。</summary>
    public const uint EVENT_OBJECT_SHOW = 0x8002;

    /// <summary>对象创建事件。</summary>
    public const uint EVENT_OBJECT_CREATE = 0x8000;

    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public const int SW_SHOWNOACTIVATE = 4;

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>SHChangeNotify：目录内容变了（配合 SHCNF_PATHW 时 dwItem1 是宽字符路径）。</summary>
    public const uint SHCNE_UPDATEDIR = 0x00001000;

    public const uint SHCNF_PATHW = 0x0005;
    public const uint SHCNF_FLUSHNOWAIT = 0x3000;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    internal static bool IsLeftMouseButtonDown() => (GetAsyncKeyState(0x01) & 0x8000) != 0;

    [DllImport("shell32.dll", EntryPoint = "SHChangeNotify")]
    internal static extern void SHChangeNotifyIdList(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    public const uint FO_DELETE = 0x0003;
    public const ushort FOF_SILENT = 0x0004;
    public const ushort FOF_NOCONFIRMATION = 0x0010;
    public const ushort FOF_ALLOWUNDO = 0x0040;
    public const ushort FOF_NOERRORUI = 0x0400;

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetTopWindow(IntPtr hWnd);

    [DllImport("shell32.dll", EntryPoint = "SHChangeNotify", ExactSpelling = true)]
    public static extern void SHChangeNotify(
        uint wEventId,
        uint uFlags,
        [MarshalAs(UnmanagedType.LPWStr)] string? dwItem1,
        [MarshalAs(UnmanagedType.LPWStr)] string? dwItem2);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    public const uint GA_ROOT = 2;

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    public delegate void WinEventProc(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventProc lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    public static string ClassNameOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>GetWindowLongPtr 的宿主：64 位进程用 Ptr 版本，32 位退回普通版本。</summary>
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    /// <summary>GWLP_HWNDPARENT 在顶层窗口上就是「owner」，和 SetParent 不是一回事。</summary>
    public const int GWLP_HWNDPARENT = -8;

    /// <summary>读取窗口的 owner（顶层窗口的 GWLP_HWNDPARENT）。</summary>
    public static IntPtr GetWindowOwner(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hwnd, GWLP_HWNDPARENT)
            : new IntPtr(GetWindowLong32(hwnd, GWLP_HWNDPARENT));
    }

    /// <summary>
    /// 盒子窗口的 owner 应该挂到哪个窗口上。
    ///
    /// 实测：直接挂 Progman 时，只要某个盒子成为前台窗口，Windows 会把「桌面已显示」当成当前状态，
    /// 再按 Win+D 就什么都不做（任务栏「显示桌面」亦然）。挂到承载桌面图标的 SHELLDLL_DefView
    /// 上，owner 约束同样能保证 Win+D 时盒子被一起带上去，而且不会让外壳误判桌面状态。
    /// </summary>
    public static IntPtr DesktopOwnerWindow()
    {
        var defView = FindDesktopDefView();
        return defView != IntPtr.Zero ? defView : DesktopRoot();
    }

    /// <summary>
    /// 把窗口的 owner 设成指定窗口（这里是承载桌面图标的 SHELLDLL_DefView），返回原来的 owner。
    /// owner 关系由窗口管理器维护：owned 窗口永远在 owner 之上，所以 Win+D 把桌面提起来时，
    /// 盒子会被一起带上、不会出现「桌面盖住盒子」的那一帧。
    /// </summary>
    public static IntPtr SetWindowOwner(IntPtr hwnd, IntPtr owner)
    {
        if (hwnd == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hwnd, GWLP_HWNDPARENT, owner)
            : new IntPtr(SetWindowLong32(hwnd, GWLP_HWNDPARENT, owner.ToInt32()));
    }

    /// <summary>
    /// 一组盒子窗口是不是真的紧贴在桌面根窗口上方（z 序里从桌面根往上依次都是这些盒子）。
    ///
    /// 用来在显示桌面结束后自校验：Win+D 恢复时，如果前台的还是某个盒子，系统降下桌面根窗口的
    /// 时序比前台事件晚几十毫秒，这时按 HWND_BOTTOM 压底会把盒子压到「当时还高高的桌面根」上面，
    /// 等桌面降下去盒子就停在普通窗口层高处了。所以要过一小段时间再确认一次，必要时重新压底。
    /// </summary>
    public static bool AreBoxesAtDesktopLayer(IReadOnlyCollection<IntPtr> handles, IntPtr desktopRoot)
    {
        if (desktopRoot == IntPtr.Zero || handles.Count == 0)
        {
            return false;
        }

        var remaining = new HashSet<IntPtr>();
        foreach (var hwnd in handles)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            {
                return false;
            }

            remaining.Add(hwnd);
        }

        var current = GetWindow(desktopRoot, GW_HWNDPREV);
        var guard = remaining.Count + 8;
        while (current != IntPtr.Zero && guard-- > 0)
        {
            if (remaining.Remove(current))
            {
                if (remaining.Count == 0)
                {
                    return true;
                }
            }
            else
            {
                // 紧挨着桌面根上方的窗口不是盒子：盒子已经飘到普通窗口层里了
                return false;
            }

            current = GetWindow(current, GW_HWNDPREV);
        }

        return remaining.Count == 0;
    }

    /// <summary>把窗口压到最底层（紧贴桌面之上，低于所有普通窗口）。</summary>
    /// <remarks>
    /// 窗口有 owner（见 <see cref="SetWindowOwner"/>）时，HWND_BOTTOM 会被窗口管理器约束回
    /// 「紧贴 owner 上方」——正好还是桌面之上、普通窗口之下，不需要另写一套插入逻辑。
    /// </remarks>
    public static void PushToBottom(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
        {
            SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    /// <summary>缓存桌面相关窗口句柄：压底不再是"每次调用都全屏扫描一遍"。</summary>
    private static IntPtr _cachedProgman;
    private static IntPtr _cachedDesktopDefView;

    /// <summary>
    /// 取 Progman 句柄；缓存失效（IsWindow 为假，例如 explorer 重启过）时重找。
    /// 注意：本机实测 FindWindow("Progman") 会返回 0，所以再沿 z 序扫描类名兜底。
    /// </summary>
    private static IntPtr CachedProgman()
    {
        if (!IsWindow(_cachedProgman))
        {
            _cachedProgman = FindWindow("Progman", null);

            if (_cachedProgman == IntPtr.Zero)
            {
                var hwnd = GetTopWindow(IntPtr.Zero);
                while (hwnd != IntPtr.Zero)
                {
                    if (ClassNameOf(hwnd).Equals("Progman", StringComparison.OrdinalIgnoreCase))
                    {
                        _cachedProgman = hwnd;
                        break;
                    }

                    hwnd = GetWindow(hwnd, GW_HWNDNEXT);
                }
            }
        }

        return _cachedProgman;
    }

    /// <summary>
    /// 桌面（壁纸 + 图标）所在的顶层窗口：优先用承载 SHELLDLL_DefView 的那个顶层窗口，
    /// 找不到再退回 Progman。判断"盒子有没有被桌面盖住 / 该插到哪一层"都以它为基准。
    /// </summary>
    public static IntPtr DesktopRoot()
    {
        var defView = FindDesktopDefView();
        var root = defView == IntPtr.Zero ? IntPtr.Zero : GetAncestor(defView, GA_ROOT);

        return root != IntPtr.Zero ? root : CachedProgman();
    }

    /// <summary>
    /// 把桌面窗口压回 z 序最底层。
    ///
    /// 按 Win+D（显示桌面）时系统会把桌面窗口提到盒子上面，盒子这时已经贴着 z 序底部、
    /// 受 z 序带次限制，光把自己抬高没有用（实测抬不动）。把桌面压回底层才会重新露出盒子，
    /// 而且盒子依旧在普通窗口之下。
    ///
    /// 注意：这个方法本身会重排窗口，调用方必须确认"盒子真的被桌面盖住"再调，
    /// 否则分层玻璃窗口被反复重排，看起来就是格子闪烁。
    /// </summary>
    public static void PushDesktopToBottom()
    {
        var progman = CachedProgman();
        var defView = FindDesktopDefView();
        var root = defView == IntPtr.Zero ? IntPtr.Zero : GetAncestor(defView, GA_ROOT);

        if (progman != IntPtr.Zero)
        {
            SetWindowPos(progman, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        // 图标视图挂在某个 WorkerW 里时，被提上去的是那个 WorkerW
        if (root != IntPtr.Zero && root != progman)
        {
            SetWindowPos(root, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    /// <summary>
    /// 把窗口抬到最前面但不激活。
    /// 用在「显示桌面（Win+D）把桌面提到最前」的时候，让盒子重新盖在桌面之上。
    /// </summary>
    public static void RaiseToTop(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
        {
            SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    /// <summary>这个窗口是不是桌面本身（Progman / WorkerW / 图标视图）。</summary>
    public static bool IsDesktopWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var cls = ClassNameOf(hwnd);
        return cls.Equals("Progman", StringComparison.OrdinalIgnoreCase) ||
               cls.Equals("WorkerW", StringComparison.OrdinalIgnoreCase) ||
               cls.Equals("SHELLDLL_DefView", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>桌面当前是否在前台（按 Win+D 之后就是这样）。</summary>
    public static bool DesktopIsForeground() => IsDesktopWindow(GetForegroundWindow());

    /// <summary>
    /// 任务栏相关的窗口类：点任务栏 / 开始菜单 / 任务栏缩略图只会改变前台，
    /// 不会盖住盒子，这时不该产生任何 z 序动作（否则盒子会闪）。
    /// </summary>
    public static bool IsShellTrayClass(string className)
    {
        return className.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
               className.Equals("Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase) ||
               className.Equals("TaskListThumbnailWnd", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>这个窗口是不是任务栏相关窗口。</summary>
    public static bool IsShellTrayWindow(IntPtr hwnd) => IsShellTrayClass(ClassNameOf(hwnd));

    /// <summary>
    /// 窗口中心点当前是不是被桌面层盖住了（Win+D 之后就是这个状态）。
    /// 被普通窗口挡住不算——那种情况本来就该让普通窗口在上面。
    /// </summary>
    public static bool IsCoveredByDesktop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        var x = rect.Left + (rect.Right - rect.Left) / 2;
        var y = rect.Top + (rect.Bottom - rect.Top) / 2;

        var hit = WindowFromPoint(new POINT(x, y));
        if (hit == IntPtr.Zero)
        {
            return false;
        }

        var root = GetAncestor(hit, GA_ROOT);
        if (root == hwnd || hit == hwnd)
        {
            return false; // 命中的就是自己（或自己的子窗口）
        }

        return IsDesktopWindow(root) || IsDesktopWindow(hit);
    }

    /// <summary>
    /// 多个采样点是不是全都被桌面窗口盖住。
    /// 单点容易被瞬态窗口骗到，所以要求每个点命中的都是桌面类窗口；
    /// 这条同时覆盖了"桌面在前台或命中窗口确实是桌面类"——命中窗口全是桌面类已经足够，
    /// 而普通窗口挡住盒子属于正常层叠（本来就该让普通窗口在上面），不算被桌面盖住。
    /// </summary>
    public static bool IsCoveredByDesktop(IReadOnlyList<POINT> points)
    {
        if (points.Count == 0)
        {
            return false;
        }

        foreach (var point in points)
        {
            var hit = WindowFromPoint(point);
            if (hit == IntPtr.Zero)
            {
                return false;
            }

            if (!IsDesktopWindow(hit) && !IsDesktopWindow(GetAncestor(hit, GA_ROOT)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>盒子"保持可见"的 z 序动作走了哪条路径（写进日志，用来核对 z 序语义）。</summary>
    public enum ZOrderAction
    {
        /// <summary>句柄无效，什么都没做。</summary>
        NoTarget,

        /// <summary>盒子本来就露在桌面之上（自校验通过，没有动过任何窗口）。</summary>
        AlreadyAbove,

        /// <summary>把盒子插回桌面之上，自校验通过。</summary>
        PlacedAboveDesktop,

        /// <summary>桌面根窗口在整个 z 序最上面：没有"桌面之上"可插，退化为把桌面压回底层。</summary>
        FellBackNoInsertAfter,

        /// <summary>插入之后自校验没过，退化为把桌面压回底层。</summary>
        FellBackVerifyFailed,

        /// <summary>本该退化压桌面，但同一批里已经压过一次，这次不动（避免重复重排）。</summary>
        FallbackSuppressed,
    }

    /// <summary>
    /// 把被桌面盖住的盒子插回"桌面之上"——不动桌面窗口，只挪我们自己的盒子。
    /// 改桌面（Progman）的 z 序会触发整屏重绘（用户看到所有格子闪一下），所以只在退化时才做。
    ///
    /// insertAfter 取 z 序里紧挨在桌面根窗口上面的那个窗口，把盒子插在它下面，
    /// 结果就是"紧贴桌面上方、仍在所有普通窗口之下"。
    /// 插完必须自校验：盒子内容区中心要重新命中盒子自己；而且盒子不能被搞成置顶窗口
    /// （insertAfter 落在置顶带里时系统会把被插窗口一起变成置顶）。
    /// </summary>
    public static ZOrderAction PlaceBoxAboveDesktop(
        IntPtr hwnd,
        IntPtr desktopRoot,
        POINT contentCenter,
        bool allowDesktopFallback)
    {
        if (hwnd == IntPtr.Zero)
        {
            return ZOrderAction.NoTarget;
        }

        if (HitsSelf(hwnd, contentCenter))
        {
            return ZOrderAction.AlreadyAbove;
        }

        if (desktopRoot == IntPtr.Zero)
        {
            return Fallback(allowDesktopFallback) ? ZOrderAction.FellBackNoInsertAfter : ZOrderAction.FallbackSuppressed;
        }

        var insertAfter = GetWindow(desktopRoot, GW_HWNDPREV);
        if (insertAfter == IntPtr.Zero)
        {
            // 桌面根窗口已经压在整个 z 序最上面：没有"桌面之上"的位置可插
            return Fallback(allowDesktopFallback) ? ZOrderAction.FellBackNoInsertAfter : ZOrderAction.FallbackSuppressed;
        }

        // 桌面根窗口位于非置顶带最上面时（典型是 Win+D），它上面就是置顶带：
        // 直接拿置顶窗口当 insertAfter 会把盒子也变成置顶（以后会盖住所有普通窗口），
        // 这里改用 HWND_TOP —— 非置顶窗口会停在非置顶带顶部，正好就是"紧贴桌面上方"。
        var target = IsTopmostWindow(insertAfter) ? HWND_TOP : insertAfter;

        SetWindowPos(hwnd, target, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        if (HitsSelf(hwnd, contentCenter) && !IsTopmostWindow(hwnd))
        {
            return ZOrderAction.PlacedAboveDesktop;
        }

        // 兜底：万一还是被顶成置顶窗口，先改回来再走退化路径
        if (IsTopmostWindow(hwnd))
        {
            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        return Fallback(allowDesktopFallback) ? ZOrderAction.FellBackVerifyFailed : ZOrderAction.FallbackSuppressed;
    }

    /// <summary>退化路径：把桌面压回最底层（会触发整屏重绘，所以只能兜底用）。</summary>
    private static bool Fallback(bool allowDesktopFallback)
    {
        if (!allowDesktopFallback)
        {
            return false;
        }

        PushDesktopToBottom();
        return true;
    }

    /// <summary>这个点命中的是不是 hwnd 自己（或它的子窗口）。</summary>
    private static bool HitsSelf(IntPtr hwnd, POINT point)
    {
        var hit = WindowFromPoint(point);
        if (hit == IntPtr.Zero)
        {
            return false;
        }

        return hit == hwnd || GetAncestor(hit, GA_ROOT) == hwnd;
    }

    /// <summary>窗口是不是置顶窗口（WS_EX_TOPMOST）。</summary>
    public static bool IsTopmostWindow(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && (GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;

    /// <summary>
    /// 让窗口鼠标穿透、不抢焦点、不出现在 Alt+Tab —— 拖动盒子时的对齐参考线用这个。
    /// </summary>
    public static void MakeOverlay(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(
            hwnd,
            GWL_EXSTYLE,
            style | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED);
    }

    /// <summary>
    /// 定位承载桌面图标的 SHELLDLL_DefView。
    /// 结果带缓存（IsWindow 校验失效后重找），避免每次调用都做一遍 EnumWindows 全屏扫描。
    /// </summary>
    public static IntPtr FindDesktopDefView()
    {
        if (IsWindow(_cachedDesktopDefView))
        {
            return _cachedDesktopDefView;
        }

        var progman = CachedProgman();

        var defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero)
        {
            // Windows 10/11：图标视图挂在 Progman 下的某个 WorkerW 里
            var worker = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
            while (worker != IntPtr.Zero)
            {
                defView = FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (defView != IntPtr.Zero)
                {
                    break;
                }

                worker = FindWindowEx(progman, worker, "WorkerW", null);
            }
        }

        if (defView == IntPtr.Zero)
        {
            // 兜底：直接扫描顶层窗口（只在缓存失效后才会走到这里）
            EnumWindows((top, _) =>
            {
                var view = FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (view != IntPtr.Zero)
                {
                    defView = view;
                    return false;
                }

                return true;
            }, IntPtr.Zero);
        }

        _cachedDesktopDefView = defView;
        return defView;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>删除到回收站。</summary>
    public static bool DeleteToRecycleBin(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0",
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT),
        };

        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }
}
