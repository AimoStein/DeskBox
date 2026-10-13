using System.Runtime.InteropServices;
using System.Text;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DeskBox.Rendering;

internal static class GlassInterop
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICaptureItemInterop
    {
        void CreateForWindow(nint window, ref Guid iid, out nint item);
        void CreateForMonitor(nint monitor, ref Guid iid, out nint item);
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string value, int length, out nint hstring);
    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(nint hstring);
    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(nint name, ref Guid iid, out nint factory);
    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint device, out nint inspectable);

    internal delegate bool EnumProc(nint window, nint state);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback, nint state);
    [DllImport("user32.dll")] internal static extern bool EnumChildWindows(nint window, EnumProc callback, nint state);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(nint window, StringBuilder name, int length);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint window, out Interop.RECT rect);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] internal static extern nint GetDesktopWindow();

    internal static IReadOnlyList<nint> FindWallpaperWindows()
    {
        var result = new List<nint>();
        void Inspect(nint window)
        {
            var name = new StringBuilder(256);
            GetClassName(window, name, name.Capacity);
            if (IsWindowVisible(window) && name.ToString().StartsWith("WPE", StringComparison.Ordinal)
                && name.ToString().Contains("Wallpaper", StringComparison.Ordinal) && !result.Contains(window))
                result.Add(window);
        }
        EnumProc child = (window, _) => { Inspect(window); return true; };
        EnumProc top = (window, _) => { Inspect(window); EnumChildWindows(window, child, 0); return true; };
        EnumWindows(top, 0);
        return result;
    }

    internal static GraphicsCaptureItem CreateItem(nint window)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out var name));
        nint factoryPointer = 0;
        try
        {
            var iid = typeof(ICaptureItemInterop).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(name, ref iid, out factoryPointer));
            var factory = (ICaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPointer);
            try
            {
                var itemIid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
                factory.CreateForWindow(window, ref itemIid, out var pointer);
                try { return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(pointer); }
                finally { Marshal.Release(pointer); }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }
        finally
        {
            if (factoryPointer != 0) Marshal.Release(factoryPointer);
            WindowsDeleteString(name);
        }
    }

    internal static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var pointer));
        try { return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
    }

    internal static unsafe ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        using var reference = WinRT.MarshalInterface<IDirect3DSurface>.CreateMarshaler(surface);
        var iid = new Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(reference.ThisPtr, ref iid, out var access));
        try
        {
            var textureIid = typeof(ID3D11Texture2D).GUID;
            nint texture = 0;
            var getInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)access)[3];
            Marshal.ThrowExceptionForHR(getInterface(access, &textureIid, &texture));
            return new ID3D11Texture2D(texture);
        }
        finally { Marshal.Release(access); }
    }
}
