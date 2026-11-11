using System.Runtime.InteropServices;

namespace DeskBox.Interop;

/// <summary>通过 ShellWindows 获取 Explorer 正在显示的桌面视图，而不是创建另一个文件夹视图。</summary>
internal static class DesktopViewInterop
{
    internal sealed record Item(string Path, string Name, int? X = null, int? Y = null, long Revision = 0);

    internal static IReadOnlyList<string>? ReadItems() => ReadLayout()?.Select(item => item.Path).ToList();

    internal static IReadOnlyList<Item>? ReadLayout() => VisitItems(null);

    internal static void RefreshForDiagnostics() => VisitItems(null, refresh: true);

    internal static void NotifyRemovedPaths(IEnumerable<string> paths)
    {
        var removed = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = new List<IntPtr>();
        try
        {
            VisitItems((pidl, name) =>
            {
                if (removed.Contains(name)) targets.Add(ShellItemInterop.ClonePidl(pidl));
            });
            foreach (var pidl in targets)
                if (pidl != IntPtr.Zero) NativeMethods.SHChangeNotifyIdList(0x4, 0x1000, pidl, IntPtr.Zero);
        }
        finally { foreach (var pidl in targets) ShellItemInterop.FreePidl(pidl); }
    }

    private static IReadOnlyList<Item>? VisitItems(Action<IntPtr, string>? inspect, bool refresh = false)
    {
        object? windows = null;
        object? desktop = null;
        IShellBrowser? browser = null;
        IShellView? view = null;
        using var com = ShellItemInterop.EnsureComInitialized();
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), true)!;
            windows = Activator.CreateInstance(type)!;
            object location = 0, root = 0;
            desktop = ((dynamic)windows).FindWindowSW(ref location, ref root, 8, out int hwnd, 1);
            if (desktop is not IServiceProvider provider) return null;
            var service = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
            var iid = typeof(IShellBrowser).GUID;
            if (provider.QueryService(ref service, ref iid, out browser) < 0 || browser is null) return null;
            if (browser.QueryActiveShellView(out view) < 0 || view is null) return null;
            if (refresh) { view.Refresh(); return null; }
            if (view is IFolderView folder && folder.ItemCount(2, out var count) >= 0)
            {
                var items = new List<Item>();
                var list = NativeMethods.FindWindowEx(NativeMethods.FindDesktopDefView(), IntPtr.Zero, "SysListView32", null);
                for (var index = 0; index < count; index++)
                {
                    if (folder.Item(index, out var pidl) < 0 || pidl == IntPtr.Zero) continue;
                    try
                    {
                        var path = ShellItemInterop.GetName(pidl, ShellItemInterop.SIGDN.FILESYSPATH)
                            ?? ShellItemInterop.GetName(pidl, ShellItemInterop.SIGDN.DESKTOPABSOLUTEPARSING);
                        if (path is null) continue;
                        var label = ShellItemInterop.GetName(pidl, ShellItemInterop.SIGDN.NORMALDISPLAY) ?? path;
                        var positioned = folder.GetItemPosition(pidl, out var point) >= 0 &&
                            list != IntPtr.Zero && ClientToScreen(list, ref point);
                        items.Add(new Item(path, label, positioned ? point.X : null, positioned ? point.Y : null));
                        inspect?.Invoke(pidl, path);
                    }
                    finally { Marshal.FreeCoTaskMem(pidl); }
                }
                return items;
            }
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn("读取 Explorer 桌面视图失败：" + ex.Message);
            return null;
        }
        finally
        {
            if (view is not null) Marshal.ReleaseComObject(view);
            if (browser is not null) Marshal.ReleaseComObject(browser);
            if (desktop is not null) Marshal.ReleaseComObject(desktop);
            if (windows is not null) Marshal.ReleaseComObject(windows);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid iid, out IShellBrowser browser);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig] int GetWindow(out IntPtr hwnd);
        [PreserveSig] int ContextSensitiveHelp(int enter);
        [PreserveSig] int InsertMenusSB(IntPtr menu, IntPtr widths);
        [PreserveSig] int SetMenuSB(IntPtr menu, IntPtr holeMenu, IntPtr active);
        [PreserveSig] int RemoveMenusSB(IntPtr menu);
        [PreserveSig] int SetStatusTextSB(IntPtr text);
        [PreserveSig] int EnableModelessSB(int enable);
        [PreserveSig] int TranslateAcceleratorSB(IntPtr msg, ushort id);
        [PreserveSig] int BrowseObject(IntPtr pidl, uint flags);
        [PreserveSig] int GetViewStateStream(uint mode, out IntPtr stream);
        [PreserveSig] int GetControlWindow(uint id, out IntPtr hwnd);
        [PreserveSig] int SendControlMsg(uint id, uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result);
        [PreserveSig] int QueryActiveShellView(out IShellView view);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        [PreserveSig] int GetWindow(out IntPtr hwnd);
        [PreserveSig] int ContextSensitiveHelp(int enter);
        [PreserveSig] int TranslateAccelerator(IntPtr msg);
        [PreserveSig] int EnableModeless(int enable);
        [PreserveSig] int UIActivate(uint state);
        [PreserveSig] int Refresh();
    }

    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        [PreserveSig] int GetCurrentViewMode(out uint mode);
        [PreserveSig] int SetCurrentViewMode(uint mode);
        [PreserveSig] int GetFolder(ref Guid iid, out IntPtr folder);
        [PreserveSig] int Item(int index, out IntPtr pidl);
        [PreserveSig] int ItemCount(uint flags, out int count);
        [PreserveSig]
        int Items(uint flags, ref Guid iid, out IntPtr items);
        [PreserveSig]
        int GetSelectionMarkedItem(out int item);
        [PreserveSig]
        int GetFocusedItem(out int item);
        [PreserveSig]
        int GetItemPosition(IntPtr pidl, out POINT point);
    }
}
