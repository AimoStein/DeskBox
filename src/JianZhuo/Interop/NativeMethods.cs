using System.Runtime.InteropServices;
using System.Text;

namespace JianZhuo.Interop;

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

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    public const int SW_SHOWNOACTIVATE = 4;

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;

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

    /// <summary>把窗口压到最底层（紧贴桌面之上，低于所有普通窗口）。</summary>
    public static void PushToBottom(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
        {
            SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    /// <summary>
    /// 把桌面窗口压回 z 序最底层。
    ///
    /// 按 Win+D（显示桌面）时系统会把桌面窗口提到盒子上面，盒子这时已经贴着 z 序底部、
    /// 受 z 序带次限制，光把自己抬高没有用（实测抬不动）。把桌面压回底层才会重新露出盒子，
    /// 而且盒子依旧在普通窗口之下。
    /// </summary>
    public static void PushDesktopToBottom()
    {
        var progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
        {
            SetWindowPos(progman, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        // 图标视图挂在某个 WorkerW 里时，被提上去的是那个 WorkerW
        var defView = FindDesktopDefView();
        var root = defView == IntPtr.Zero ? IntPtr.Zero : GetAncestor(defView, GA_ROOT);

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

    /// <summary>定位承载桌面图标的 SHELLDLL_DefView。</summary>
    public static IntPtr FindDesktopDefView()
    {
        var progman = FindWindow("Progman", null);

        var defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView != IntPtr.Zero)
        {
            return defView;
        }

        // Windows 10/11：图标视图挂在 Progman 下的某个 WorkerW 里
        var worker = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        while (worker != IntPtr.Zero)
        {
            defView = FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
            {
                return defView;
            }

            worker = FindWindowEx(progman, worker, "WorkerW", null);
        }

        // 兜底：直接扫描顶层窗口
        IntPtr found = IntPtr.Zero;
        EnumWindows((top, _) =>
        {
            var view = FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view != IntPtr.Zero)
            {
                found = view;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return found;
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
