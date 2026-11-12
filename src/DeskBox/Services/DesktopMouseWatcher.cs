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
    private readonly bool _iconsSuppressed;
    private IntPtr _hook = IntPtr.Zero;
    private uint _lastClickTime;
    private POINT _lastClickPoint;

    public DesktopMouseWatcher(bool iconsSuppressed = false)
    {
        _iconsSuppressed = iconsSuppressed;
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
            Log.Warn("安装鼠标钩子失败，桌面双击操作将不可用。");
        }
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && Enabled && wParam.ToInt32() == NativeMethods.WM_LBUTTONDOWN)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var window = NativeMethods.WindowFromPoint(data.pt);
                if (!IsDesktopSurface(window))
                {
                    _lastClickTime = 0;
                    return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
                }
                var isDouble = _lastClickTime != 0
                               && data.time - _lastClickTime <= DoubleClickMilliseconds
                               && Math.Abs(data.pt.X - _lastClickPoint.X) <= DoubleClickTolerance
                               && Math.Abs(data.pt.Y - _lastClickPoint.Y) <= DoubleClickTolerance;

                if (isDouble)
                {
                    _lastClickTime = 0;
                    if ((_iconsSuppressed && NativeMethods.ClassNameOf(window) != "SysListView32") ||
                        IsEmptyDesktopPoint(data.pt))
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

    private static bool IsDesktopSurface(IntPtr window) => window != IntPtr.Zero &&
        (NativeMethods.IsDesktopWindow(window) ||
         NativeMethods.IsDesktopWindow(NativeMethods.GetAncestor(window, NativeMethods.GA_ROOT)));

    /// <summary>When Explorer icons are visible, double-clicks on items keep their normal action.</summary>
    private static bool IsEmptyDesktopPoint(POINT point)
    {
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
