using System.Windows;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D11;
using DeskBox.Services;

namespace DeskBox.Rendering;

/// <summary>One capture session per wallpaper window. Frames stay on the GPU.</summary>
internal sealed class WallpaperCapture : IDisposable
{
    private readonly IDirect3DDevice _device;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private GraphicsCaptureItem? _item;
    private Windows.Graphics.SizeInt32 _size;
    internal nint Window { get; }
    internal nint CaptureWindow { get; private set; }
    internal Rect Bounds { get; private set; }
    internal ID3D11Texture2D? Texture { get; private set; }
    internal long Frames { get; private set; }
    private volatile bool _closed;
    internal bool Closed => _closed;

    internal WallpaperCapture(IDirect3DDevice device, nint window)
    {
        Window = window;
        CaptureWindow = window;
        _device = device;
        try
        {
            try { _item = GlassInterop.CreateItem(window); }
            catch (ArgumentException)
            {
                // Wallpaper Engine video is a child HWND. WGC accepts its top-level desktop host.
                CaptureWindow = GlassInterop.GetAncestor(window, 2);
                if (CaptureWindow == 0 || CaptureWindow == window) throw;
                _item = GlassInterop.CreateItem(CaptureWindow);
                Log.Info($"Liquid Glass: 子窗口改由桌面宿主 {CaptureWindow} 捕获。");
            }
            _item.Closed += ItemClosed;
            _size = _item.Size;
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
            _session = _pool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = false;
            try { if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)) _session.IsBorderRequired = false; }
            catch (Exception ex) { Log.Warn("玻璃捕获边框: " + ex.Message); }
            _session.StartCapture();
            UpdateBounds();
        }
        catch { Dispose(); throw; }
    }

    private void ItemClosed(GraphicsCaptureItem sender, object args) => _closed = true;

    internal bool Poll(ID3D11Device device, ID3D11DeviceContext context)
    {
        if (Closed || _pool is null) return false;
        using var frame = _pool.TryGetNextFrame();
        if (frame is null) return false;
        using var source = GlassInterop.GetTexture(frame.Surface);
        var description = source.Description;
        if (Texture is null || Texture.Description.Width != description.Width || Texture.Description.Height != description.Height)
        {
            Texture?.Dispose();
            description.BindFlags = BindFlags.ShaderResource;
            description.MiscFlags = ResourceOptionFlags.None;
            description.CPUAccessFlags = CpuAccessFlags.None;
            description.Usage = ResourceUsage.Default;
            Texture = device.CreateTexture2D(description);
        }
        context.CopyResource(Texture, source);
        Frames++;
        UpdateBounds();
        if (frame.ContentSize.Width > 0 && frame.ContentSize.Height > 0
            && (frame.ContentSize.Width != _size.Width || frame.ContentSize.Height != _size.Height))
        {
            _size = frame.ContentSize;
            _pool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
        }
        return true;
    }

    private void UpdateBounds()
    {
        if (GlassInterop.GetWindowRect(CaptureWindow, out var rect))
            Bounds = new Rect(rect.Left, rect.Top, Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top));
    }

    public void Dispose()
    {
        _closed = true;
        _session?.Dispose(); _session = null;
        _pool?.Dispose(); _pool = null;
        if (_item is not null) _item.Closed -= ItemClosed;
        _item = null;
        Texture?.Dispose(); Texture = null;
    }
}
