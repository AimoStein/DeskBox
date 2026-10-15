using System.Runtime.InteropServices;
using System.Text;

namespace DeskBox.Interop;

/// <summary>
/// 拖放解析系统图标要用到的 P/Invoke 与 COM 接口，统一放这里，不污染 NativeMethods.cs。
/// 涉及 shell 命名空间项（回收站 / 此电脑 / 控制面板 / 网络）——这类项没有文件系统路径，
/// 只能拿到 PIDL 与解析名（形如 ::{645FF040-5081-101B-9F08-00AA002F954E}）。
/// </summary>
public static class ShellItemInterop
{
    /// <summary>SIGDN：SHGetNameFromIDList 要哪种名字。</summary>
    public enum SIGDN : uint
    {
        NORMALDISPLAY = 0x00000000,
        PARENTRELATIVEPARSING = 0x80018001,
        DESKTOPABSOLUTEPARSING = 0x80028000,
        PARENTRELATIVEEDITING = 0x80031001,
        DESKTOPABSOLUTEEDITING = 0x8004c000,
        FILESYSPATH = 0x80058000,
        URL = 0x80068000,
    }

    public const int S_OK = 0;
    public const int E_PENDING = unchecked((int)0x8000000A);

    /// <summary>
    /// shell 用来在拖放里传「一组 shell 项」的剪贴板格式名。它对应 CFSTR_SHELLIDLIST，
    /// 内容是 CIDA 结构，不是文件路径。
    /// </summary>
    public const string ShellIdListArrayFormat = "Shell IDList Array";

    // 桌面常见系统图标的解析名（自检与排查时用得上）
    public const string RecycleBinParsingName = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    public const string ControlPanelParsingName = "::{21EC2020-3AEA-1069-A2DD-08002B30309D}";

    public const string NetworkParsingName = "::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}";

    // ---------------------------------------------------------------- P/Invoke

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetPathFromIDListW")]
    private static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder pszPath);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetNameFromIDList")]
    private static extern int SHGetNameFromIDList(IntPtr pidl, SIGDN sigdnName, out IntPtr ppszName);

    [DllImport("shell32.dll", EntryPoint = "ILClone")]
    private static extern IntPtr ILClone(IntPtr pidl);

    [DllImport("shell32.dll", EntryPoint = "ILFree")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll", EntryPoint = "ILFindLastID")]
    private static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("shell32.dll", EntryPoint = "ILGetSize")]
    private static extern uint ILGetSize(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern int SHGetDesktopFolder(out IShellFolder ppshf);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    /// <summary>系统自己解析拖放数据（Vista+）。比手工拆 CIDA 可靠。</summary>
    [DllImport("shell32.dll")]
    private static extern int SHCreateShellItemArrayFromDataObject(
        [MarshalAs(UnmanagedType.Interface)] object pdo,
        [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        [In] ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    private const uint CLSCTX_INPROC_SERVER = 0x1;
    private const uint COINIT_APARTMENTTHREADED = 0x2;

    // ---------------------------------------------------------------- COM 接口

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellLinkW
    {
        [PreserveSig]
        int GetPath(
            [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
            int cch,
            IntPtr pfd,
            uint fFlags);

        [PreserveSig]
        int GetIDList(out IntPtr ppidl);

        [PreserveSig]
        int SetIDList(IntPtr pidl);

        [PreserveSig]
        int GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);

        [PreserveSig]
        int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        [PreserveSig]
        int GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);

        [PreserveSig]
        int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        [PreserveSig]
        int GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);

        [PreserveSig]
        int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        [PreserveSig]
        int GetHotkey(out short pwHotkey);

        [PreserveSig]
        int SetHotkey(short wHotkey);

        [PreserveSig]
        int GetShowCmd(out int piShowCmd);

        [PreserveSig]
        int SetShowCmd(int iShowCmd);

        [PreserveSig]
        int GetIconLocation(
            [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath,
            int cch,
            out int piIcon);

        [PreserveSig]
        int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

        [PreserveSig]
        int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

        [PreserveSig]
        int Resolve(IntPtr hwnd, uint fFlags);

        [PreserveSig]
        int SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPersistFile
    {
        [PreserveSig]
        int GetClassID(out Guid pClassID);

        [PreserveSig]
        int IsDirty();

        [PreserveSig]
        int Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);

        [PreserveSig]
        int Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);

        [PreserveSig]
        int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);

        [PreserveSig]
        int GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    /// <summary>ShellLink COM 组件（CLSID_ShellLink）。</summary>
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkObject
    {
    }

    /// <summary>IShellFolder：只为拿桌面文件夹，用它的 GetUIObjectOf 生成真实 IDataObject。</summary>
    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellFolder
    {
        [PreserveSig]
        int ParseDisplayName(
            IntPtr hwnd,
            IntPtr pbc,
            [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
            out uint pchEaten,
            out IntPtr ppidl,
            ref uint pdwAttributes);

        [PreserveSig]
        int EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);

        [PreserveSig]
        int BindToObject(IntPtr pidl, IntPtr pbc, [In] ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int BindToStorage(IntPtr pidl, IntPtr pbc, [In] ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);

        [PreserveSig]
        int CreateViewObject(IntPtr hwndOwner, [In] ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int GetAttributesOf(uint cidl, IntPtr apidl, ref uint rgfInOut);

        [PreserveSig]
        int GetUIObjectOf(
            IntPtr hwndOwner,
            uint cidl,
            [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
            [In] ref Guid riid,
            IntPtr rgfReserved,
            out IntPtr ppv);

        [PreserveSig]
        int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr lpName);

        [PreserveSig]
        int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    /// <summary>IShellItem（Vista+）：取路径 / 解析名 / 显示名。</summary>
    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int GetParent(out IShellItem ppsi);

        [PreserveSig]
        int GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);

        [PreserveSig]
        int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

        [PreserveSig]
        int Compare(IShellItem psi, uint hint, out int piOrder);
    }

    /// <summary>IShellItemArray（Vista+）：系统解析拖放数据后的结果。</summary>
    [ComImport]
    [Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemArray
    {
        [PreserveSig]
        int BindToHandler(IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int GetPropertyStore(uint flags, [In] ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int GetPropertyDescriptionList(IntPtr keyType, [In] ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int GetAttributes(uint attribFlags, uint sfgaoMask, out uint psfgaoAttribs);

        [PreserveSig]
        int GetCount(out uint pdwNumItems);

        [PreserveSig]
        int GetItemAt(uint dwIndex, out IShellItem ppsi);

        [PreserveSig]
        int EnumItems(out IntPtr ppenumShellItems);
    }

    /// <summary>FORMATETC / STGMEDIUM 用 BCL 自带的 ComTypes 版本，不再自己定义一份。</summary>
    private const int DvaspectContent = 1;
    private const int TymedHglobal = 1;

    private static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid IidShellLinkW = new("000214F9-0000-0000-C000-000000000046");

    public static readonly Guid IidDataObject = new("0000010e-0000-0000-C000-000000000046");
    public static readonly Guid IidShellItemArray = new("B63EA76D-1F85-456F-A19C-48159EFA858B");
    public static readonly Guid IidShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    public static readonly Guid BhidDataObject = new("b8c0bd9f-ed24-455c-83e6-d5390c4fe8c4");

    // ---------------------------------------------------------------- COM 初始化

    /// <summary>
    /// 确保当前线程 COM 已初始化（拖放与 IShellLink 都要求 STA）。
    /// Dispose 时只回退「自己初始化的那一次」，别人先初始化过的不会误伤。
    /// </summary>
    public readonly struct ComScope : IDisposable
    {
        private readonly bool _owned;

        internal ComScope(bool owned) => _owned = owned;

        public void Dispose()
        {
            if (_owned)
            {
                CoUninitialize();
            }
        }
    }

    public static ComScope EnsureComInitialized()
    {
        // 0 = S_OK / 1 = S_FALSE：都表示线程已经初始化过，不用（也不能）再 Uninitialize
        var hr = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
        var owned = hr == S_OK;
        return new ComScope(owned);
    }

    // ---------------------------------------------------------------- 解析名 / PATH

    /// <summary>把解析名（含 ::{GUID} 形式）解析成 PIDL；失败返回 IntPtr.Zero。</summary>
    public static IntPtr ParseDisplayName(string parsingName)
    {
        if (string.IsNullOrWhiteSpace(parsingName))
        {
            return IntPtr.Zero;
        }

        try
        {
            var hr = SHParseDisplayName(parsingName, IntPtr.Zero, out var pidl, 0, out _);
            return hr == S_OK ? pidl : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>PIDL → 文件系统路径；不是真实文件（系统图标）时返回 null。</summary>
    public static string? GetFileSystemPath(IntPtr pidl)
    {
        if (pidl == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(32768);
            if (SHGetPathFromIDListW(pidl, buffer))
            {
                var path = buffer.ToString();
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }
        }
        catch
        {
            // 视为没有路径
        }

        return null;
    }

    /// <summary>PIDL → 指定 SIGDN 的名字（NORMALDISPLAY / DESKTOPABSOLUTEPARSING）；失败返回 null。</summary>
    public static string? GetName(IntPtr pidl, SIGDN sigdn)
    {
        if (pidl == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (SHGetNameFromIDList(pidl, sigdn, out var ptr) != S_OK || ptr == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var name = Marshal.PtrToStringUni(ptr);
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            finally
            {
                CoTaskMemFree(ptr);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 这个字符串是不是 shell 解析名（::{GUID} / ::{GUID}\子项）。
    /// 有些来源会把命名空间项混在 FileDrop 里，得按虚拟项处理。
    /// </summary>
    public static bool LooksLikeParsingName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.StartsWith("::", StringComparison.Ordinal);

    // ---------------------------------------------------------------- 真实拖放数据源

    /// <summary>桌面文件夹（SHGetDesktopFolder）；失败抛 COMException。</summary>
    public static IShellFolder GetDesktopFolder()
    {
        var hr = SHGetDesktopFolder(out var desktop);
        if (hr != S_OK || desktop is null)
        {
            throw new COMException($"SHGetDesktopFolder 失败（0x{hr:X8}）", hr);
        }

        return desktop;
    }

    /// <summary>PIDL 里的最后一个 item id（不加锁、不复制，指向原 PIDL 内部）。</summary>
    public static IntPtr FindLastId(IntPtr pidl)
    {
        if (pidl == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        try
        {
            return ILFindLastID(pidl);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// 用桌面文件夹给一个绝对 PIDL 生成真实的 shell IDataObject
    /// （和资源管理器拖动时给的那个是同一类对象）。失败返回 null。
    /// </summary>
    public static object? CreateDataObject(IShellFolder parent, IntPtr absolutePidl)
    {
        if (parent is null || absolutePidl == IntPtr.Zero)
        {
            return null;
        }

        var child = FindLastId(absolutePidl);
        if (child == IntPtr.Zero)
        {
            return null;
        }

        var iid = IidDataObject;
        var hr = parent.GetUIObjectOf(IntPtr.Zero, 1, new[] { child }, ref iid, IntPtr.Zero, out var ppv);
        if (hr != S_OK || ppv == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.GetObjectForIUnknown(ppv);
        }
        finally
        {
            Marshal.Release(ppv);
        }
    }

    /// <summary>
    /// 给一个真实文件/文件夹路径生成 shell IDataObject（和拖动它时系统给的同类对象）；失败返回 null。
    /// </summary>
    public static object? CreateDataObjectFromParsingName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var iid = IidShellItem;
            var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item);
            if (hr != S_OK || item is not IShellItem shellItem)
            {
                Log.Warn($"SHCreateItemFromParsingName 失败（0x{hr:X8}）：{path}");
                return null;
            }

            try
            {
                var bhid = BhidDataObject;
                var dataObjectIid = IidDataObject;
                if (shellItem.BindToHandler(IntPtr.Zero, ref bhid, ref dataObjectIid, out var ppv) != S_OK ||
                    ppv == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    return Marshal.GetObjectForIUnknown(ppv);
                }
                finally
                {
                    Marshal.Release(ppv);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(shellItem);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"取文件 IDataObject 失败 {path}：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 从 WPF 的 IDataObject 里翻出底层 COM IDataObject。
    /// 拖放时 WPF 把 OLE 的 IDataObject 一层层包在 DataObject / OleConverter 里，
    /// 但没有公开访问器，只能顺着 _innerData 往下找，直到拿到真正的 COM 对象（RCW）。
    /// 翻不到就返回 null，调用方退化成自己拆 CIDA。
    /// </summary>
    public static object? TryGetComDataObject(System.Windows.IDataObject? data)
    {
        if (data is null)
        {
            return null;
        }

        try
        {
            object? current = data;

            // 最多往下钻 4 层：DataObject → OleConverter → __ComObject
            for (var depth = 0; depth < 4 && current is not null; depth++)
            {
                // 真正的 COM 对象（RCW）才能交给 shell 的 API
                if (current.GetType().FullName == "System.__ComObject")
                {
                    return current;
                }

                // WPF 自己的包装对象（不是 COM）继续往下钻
                if (current is not System.Windows.DataObject &&
                    current is System.Runtime.InteropServices.ComTypes.IDataObject)
                {
                    return current;
                }

                current = GetPrivateField(current, "_innerData");
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"取底层 COM IDataObject 失败：{ex.Message}");
            return null;
        }
    }

    private static object? GetPrivateField(object target, string name)
    {
        var type = target.GetType();
        while (type is not null)
        {
            var field = type.GetField(
                name,
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public);

            if (field is not null)
            {
                return field.GetValue(target);
            }

            type = type.BaseType;
        }

        return null;
    }

    /// <summary>让系统自己把拖放数据拆成 IShellItemArray（Vista+）；失败返回 null。</summary>
    public static IShellItemArray? CreateShellItemArray(object? comDataObject)
    {
        if (comDataObject is null)
        {
            return null;
        }

        try
        {
            var iid = IidShellItemArray;
            var hr = SHCreateShellItemArrayFromDataObject(comDataObject, ref iid, out var result);
            if (hr != S_OK || result is not IShellItemArray array)
            {
                Log.Warn($"SHCreateShellItemArrayFromDataObject 失败（0x{hr:X8}）");
                return null;
            }

            return array;
        }
        catch (Exception ex)
        {
            Log.Warn($"SHCreateShellItemArrayFromDataObject 异常：{ex.Message}");
            return null;
        }
    }

    /// <summary>IShellItem → 指定 SIGDN 的名字；失败返回 null。</summary>
    public static string? GetItemName(IShellItem item, SIGDN sigdn)
    {
        if (item is null)
        {
            return null;
        }

        try
        {
            if (item.GetDisplayName(sigdn, out var ptr) != S_OK || ptr == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var name = Marshal.PtrToStringUni(ptr);
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            finally
            {
                CoTaskMemFree(ptr);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从 COM IDataObject 直接取某个剪贴板格式的 HGLOBAL 字节（诊断用）。</summary>
    public static byte[]? ReadGlobalBytes(System.Runtime.InteropServices.ComTypes.IDataObject? data, string format)
    {
        if (data is null || string.IsNullOrWhiteSpace(format))
        {
            return null;
        }

        try
        {
            var formatId = (short)System.Windows.DataFormats.GetDataFormat(format).Id;
            var etc = new System.Runtime.InteropServices.ComTypes.FORMATETC
            {
                cfFormat = formatId,
                ptd = IntPtr.Zero,
                dwAspect = System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT,
                lindex = -1,
                tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL,
            };

            var hr = data.QueryGetData(ref etc);
            if (hr != S_OK)
            {
                Log.Warn($"拖放数据里没有 {format} 格式（QueryGetData 0x{hr:X8}）");
                return null;
            }

            data.GetData(ref etc, out var medium);
            var owned = medium.pUnkForRelease is null;
            try
            {
                if (medium.unionmember == IntPtr.Zero)
                {
                    return null;
                }

                var locked = GlobalLock(medium.unionmember);
                if (locked == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    var size = (int)GlobalSize(medium.unionmember).ToUInt64();
                    if (size <= 0)
                    {
                        return null;
                    }

                    var bytes = new byte[size];
                    Marshal.Copy(locked, bytes, 0, size);
                    return bytes;
                }
                finally
                {
                    GlobalUnlock(medium.unionmember);
                }
            }
            finally
            {
                // pUnkForRelease 为空时由我们自己释放这个 HGLOBAL，否则交给它
                if (owned && medium.unionmember != IntPtr.Zero)
                {
                    GlobalFree(medium.unionmember);
                }
                else if (medium.pUnkForRelease is not null)
                {
                    Marshal.ReleaseComObject(medium.pUnkForRelease);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"直接读 {format} 失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>复制一份 PIDL（调用方负责 ILFree）。失败返回 IntPtr.Zero。</summary>
    public static IntPtr ClonePidl(IntPtr pidl)
    {
        if (pidl == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        try
        {
            return ILClone(pidl);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>释放 PIDL。</summary>
    public static void FreePidl(IntPtr pidl)
    {
        if (pidl != IntPtr.Zero)
        {
            try
            {
                ILFree(pidl);
            }
            catch
            {
                // 释放失败无所谓
            }
        }
    }

    /// <summary>
    /// 单个 PIDL 占多少字节（含结尾的 2 字节 0）。用来把 PIDL 拼进 CIDA 结构。
    /// 注意 SHITEMID 的 cb 是「这一个 item id 的总字节数，含 cb 自己」，
    /// 所以下一个 item id 在 pidl + cb 处（shell 的 _ILNext 就是这么走的），
    /// 而不是 pidl + 2 + cb。cb == 0 就是整个 PIDL 的结尾。
    /// 遇到不合法的数据（负数 cb、走不到结尾、长得离谱）一律返回 0，绝不越界乱读。
    /// </summary>
    public static int PidlSize(IntPtr pidl)
    {
        if (pidl == IntPtr.Zero)
        {
            return 0;
        }

        // 单个 PIDL 不可能超过这个长度，超过就说明拿到的不是 PIDL
        const int MaxPidlSize = 64 * 1024;

        var offset = 0;
        while (offset < MaxPidlSize)
        {
            var cb = Marshal.ReadInt16(pidl, offset);

            if (cb == 0)
            {
                return offset + 2;
            }

            if (cb < 0)
            {
                break;
            }

            offset += cb;
        }

        return 0;
    }

    /// <summary>shell 自己算出来的 PIDL 大小（含结尾 2 字节 0），诊断/自检用。</summary>
    public static int GetPidlSizeByShell(IntPtr pidl)
    {
        if (pidl == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            return (int)ILGetSize(pidl);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>创建一个 IShellLinkW（CLSID_ShellLink）。失败抛 COMException。</summary>
    public static IShellLinkW CreateShellLink()
    {
        var clsid = ClsidShellLink;
        var iid = IidShellLinkW;
        var hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out var obj);

        if (hr != S_OK || obj is not IShellLinkW link)
        {
            // 兜底：走 COM 激活器（两者等价，通常走不到这里）
            var type = Type.GetTypeFromCLSID(ClsidShellLink, throwOnError: true)!;
            link = (IShellLinkW)Activator.CreateInstance(type)!;
        }

        return link;
    }
}
