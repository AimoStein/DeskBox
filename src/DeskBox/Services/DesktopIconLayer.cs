using DeskBox.Interop;

namespace DeskBox.Services;

/// <summary>运行期间接管 Explorer 的图标列表，退出时交还给 Explorer。</summary>
internal sealed class DesktopIconLayer : IDisposable
{
    private IntPtr _hidden;
    private uint _explorerPid;
    private bool _disposed;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    internal bool Suppress()
    {
        if (_disposed) return false;
        var view = NativeMethods.FindDesktopDefView();
        if (view == IntPtr.Zero) return true; // Explorer 尚未创建图标层。
        var list = NativeMethods.FindWindowEx(view, IntPtr.Zero, "SysListView32", null);
        if (list == IntPtr.Zero) return false;
        if (!NativeMethods.IsWindowVisible(list)) return true;

        NativeMethods.GetWindowThreadProcessId(list, out var pid);
        if (NativeMethods.ShowWindowAsync(list, NativeMethods.SW_HIDE))
        {
            _hidden = list;
            _explorerPid = pid;
        }
        return !NativeMethods.IsWindowVisible(list);
    }

    internal void RestoreDesktop(DesktopShell shell)
    {
        Dispose();
        shell.SetIconsVisible(true);

        // Explorer may have recreated the list, or it may already have been hidden at startup.
        var view = NativeMethods.FindDesktopDefView();
        if (view == IntPtr.Zero) return;
        var list = NativeMethods.FindWindowEx(view, IntPtr.Zero, "SysListView32", null);
        if (list != IntPtr.Zero && !NativeMethods.IsWindowVisible(list))
            NativeMethods.ShowWindowAsync(list, NativeMethods.SW_SHOWNOACTIVATE);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public void Dispose()
    {
        _disposed = true;
        if (_hidden != IntPtr.Zero && NativeMethods.IsWindow(_hidden) &&
            NativeMethods.GetWindowThreadProcessId(_hidden, out var pid) != 0 && pid == _explorerPid)
            NativeMethods.ShowWindowAsync(_hidden, NativeMethods.SW_SHOWNOACTIVATE);
        _hidden = IntPtr.Zero;
    }
}
