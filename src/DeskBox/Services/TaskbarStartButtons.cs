using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using DeskBox.Interop;

namespace DeskBox.Services;

internal sealed class TaskbarStartButtons
{
    private static readonly AndCondition StartButton = new(
        new PropertyCondition(AutomationElement.AutomationIdProperty, "StartButton"),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

    private sealed record Target(IntPtr Taskbar, Rect TaskbarBounds, Rect ButtonBounds);

    private Target[] _targets = [];
    private int _refreshing;

    internal async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) != 0) return;
        try
        {
            // UI Automation can wait on Explorer. Keep it off the input hook.
            var targets = await Task.Run(FindTargets).ConfigureAwait(false);
            Volatile.Write(ref _targets, targets);
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    internal IntPtr Hit(POINT point, IntPtr window)
    {
        if (window == IntPtr.Zero) return IntPtr.Zero;
        var root = NativeMethods.GetAncestor(window, NativeMethods.GA_ROOT);
        foreach (var target in Volatile.Read(ref _targets))
        {
            if (target.Taskbar != root || !NativeMethods.GetWindowRect(root, out var rect)) continue;
            var current = ToRect(rect);
            // Auto-hide can move the taskbar as a whole. A resize invalidates
            // the cached hit region until the next UI Automation scan.
            if (current.Size != target.TaskbarBounds.Size) continue;
            var offset = current.TopLeft - target.TaskbarBounds.TopLeft;
            if (target.ButtonBounds.Contains(new Point(point.X, point.Y) - offset) &&
                NativeMethods.IsTaskbarWindow(root))
                return root;
        }
        return IntPtr.Zero;
    }

    private static Target[] FindTargets()
    {
        var targets = new List<Target>();
        foreach (var taskbar in NativeMethods.FindTaskbarWindows())
        {
            if (!NativeMethods.GetWindowRect(taskbar, out var rect)) continue;
            var taskbarBounds = ToRect(rect);
            var button = FindStartButton(taskbar);
            if (!button.IsEmpty && button.Width > 0 && button.Height > 0 && taskbarBounds.Contains(button))
                targets.Add(new Target(taskbar, taskbarBounds, button));
        }
        return targets.ToArray();
    }

    private static Rect FindStartButton(IntPtr taskbar)
    {
        // Older taskbars may expose Start as a child HWND.
        var nativeButton = NativeMethods.FindWindowEx(taskbar, IntPtr.Zero, "Start", null);
        if (nativeButton != IntPtr.Zero && NativeMethods.IsWindowVisible(nativeButton) &&
            NativeMethods.GetWindowRect(nativeButton, out var rect))
            return ToRect(rect);

        try
        {
            return AutomationElement.FromHandle(taskbar)?.FindFirst(TreeScope.Descendants, StartButton)
                ?.Current.BoundingRectangle ?? Rect.Empty;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException
            or ArgumentException or UnauthorizedAccessException)
        {
            // Explorer may disappear while its controls are being enumerated.
            return Rect.Empty;
        }
    }

    private static Rect ToRect(RECT rect) =>
        new(rect.Left, rect.Top, Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top));
}
