using System.Windows.Threading;
using DeskBox.Interop;

namespace DeskBox.Services;

/// <summary>临时隐藏任务栏；只恢复本次由程序隐藏的窗口，不修改系统的自动隐藏设置。</summary>
public sealed class DesktopTaskbar : IDisposable
{
    private readonly HashSet<IntPtr> _hiddenWindows = new();
    private readonly DispatcherTimer _timer;

    public DesktopTaskbar()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _timer.Tick += (_, _) => HideVisibleTaskbars();
    }

    public void SetHidden(bool hidden)
    {
        if (hidden)
        {
            HideVisibleTaskbars();
            _timer.Start();
        }
        else
        {
            Restore();
        }
    }

    private void HideVisibleTaskbars()
    {
        // Explorer 重启或显示器变化后句柄可能失效；新出现的任务栏也要跟着隐藏。
        _hiddenWindows.RemoveWhere(hwnd => !NativeMethods.IsWindow(hwnd) || !NativeMethods.IsTaskbarWindow(hwnd));
        foreach (var hwnd in NativeMethods.FindTaskbarWindows())
        {
            if (NativeMethods.IsWindowVisible(hwnd) && NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE))
            {
                _hiddenWindows.Add(hwnd);
            }
        }
    }

    private void Restore()
    {
        _timer.Stop();
        foreach (var hwnd in _hiddenWindows)
        {
            if (NativeMethods.IsWindow(hwnd) && NativeMethods.IsTaskbarWindow(hwnd))
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);
            }
        }
        _hiddenWindows.Clear();
    }

    public void Dispose() => Restore();
}
