using System.Runtime.InteropServices;
using System.Windows.Automation;
using DeskBox.Interop;

namespace DeskBox.Services;

/// <summary>监听桌面空白处双击（全局低级鼠标钩子 + UI Automation 命中检测）。</summary>
public sealed class DesktopMouseWatcher : IDisposable
{
    private const int DoubleClickMilliseconds = 420;
    private const int DoubleClickTolerance = 5;

    private readonly NativeMethods.LowLevelMouseProc _callback;
    private IntPtr _hook = IntPtr.Zero;
    private uint _lastClickTime;
    private POINT _lastClickPoint;

    public DesktopMouseWatcher()
    {
        _callback = HookProc;
    }

    public event EventHandler? EmptyDesktopDoubleClicked;

    public bool Enabled { get; set; } = true;

    public void Start()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        _hook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL,
            _callback,
            NativeMethods.GetModuleHandle(null),
            0);

        if (_hook == IntPtr.Zero)
        {
            Log.Warn("安装鼠标钩子失败，双击隐藏桌面图标将不可用。");
        }
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && Enabled && wParam.ToInt32() == NativeMethods.WM_LBUTTONDOWN)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var isDouble = _lastClickTime != 0
                               && data.time - _lastClickTime <= DoubleClickMilliseconds
                               && Math.Abs(data.pt.X - _lastClickPoint.X) <= DoubleClickTolerance
                               && Math.Abs(data.pt.Y - _lastClickPoint.Y) <= DoubleClickTolerance;

                if (isDouble)
                {
                    _lastClickTime = 0;
                    if (IsEmptyDesktopPoint(data.pt))
                    {
                        EmptyDesktopDoubleClicked?.Invoke(this, EventArgs.Empty);
                    }
                }
                else
                {
                    _lastClickTime = data.time;
                    _lastClickPoint = data.pt;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("鼠标钩子异常: " + ex.Message);
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>判断这个点是否落在桌面空白处（而不是图标、窗口或我们的盒子上）。</summary>
    private static bool IsEmptyDesktopPoint(POINT point)
    {
        var hwnd = NativeMethods.WindowFromPoint(point);
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var className = NativeMethods.ClassNameOf(hwnd);
        var onDesktop = className is "SysListView32" or "SHELLDLL_DefView" or "Progman" or "WorkerW";
        if (!onDesktop)
        {
            return false;
        }

        try
        {
            var element = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
            if (element is null)
            {
                return false;
            }

            // 命中的是桌面列表项（也就是图标）就不算空白
            return element.Current.ControlType != ControlType.ListItem
                   && element.Current.ControlType != ControlType.DataItem;
        }
        catch (Exception ex)
        {
            Log.Warn("桌面命中检测失败: " + ex.Message);
            return false;
        }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
