using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Vortice.Direct3D11;
using Vortice.Direct3D9;
using Rect = System.Windows.Rect;

namespace DeskBox.Rendering;

[StructLayout(LayoutKind.Sequential)]
internal struct GlassParameters
{
    internal Vector4 Region, Shape, Material, Pointer, TextureSize;
}

/// <summary>D3D11 renders into a D3D9Ex shared surface which WPF can composite.</summary>
internal sealed class GlassSurface : IDisposable
{
    private readonly LiquidGlassEngine _engine;
    private IDirect3DTexture9? _texture9;
    private IDirect3DSurface9? _surface9;
    private ID3D11Texture2D? _texture11;
    private ID3D11RenderTargetView? _target;
    internal D3DImage Image { get; } = new();
    internal Func<Rect> GetBounds { get; }
    internal Func<bool> IsActive { get; }
    internal Func<GlassParameters> GetParameters { get; }
    internal Action<bool> AvailabilityChanged { get; }
    internal long Draws { get; private set; }
    internal long LockMisses { get; private set; }
    internal void SaveFrame(string path)
    {
        if (_texture11 is not null) GlassPreview.SaveTexture(_engine, _texture11, path);
    }
    private int _width, _height;
    private bool _available;

    internal GlassSurface(LiquidGlassEngine engine, Func<Rect> bounds, Func<bool> active,
        Func<GlassParameters> parameters, Action<bool> availability)
    {
        _engine = engine;
        GetBounds = bounds; IsActive = active; GetParameters = parameters; AvailabilityChanged = availability;
    }

    internal void SetAvailable(bool value)
    {
        if (_available == value) return;
        _available = value;
        AvailabilityChanged(value);
    }

    internal bool Render(WallpaperCapture capture)
    {
        var bounds = GetBounds();
        var width = Math.Clamp((int)Math.Ceiling(bounds.Width), 1, 8192);
        var height = Math.Clamp((int)Math.Ceiling(bounds.Height), 1, 8192);
        if (capture.Texture is null || bounds.IsEmpty) { SetAvailable(false); return false; }
        if (!Image.IsFrontBufferAvailable) { SetAvailable(false); return false; }
        var acquired = Image.TryLock(new Duration(TimeSpan.Zero));
        try
        {
            // D3DImage.TryLock increments its lock count even when it returns false.
            // Every completed call must be paired with Unlock, including skipped frames.
            if (!acquired) { LockMisses++; return false; }
            if (_width != width || _height != height || _target is null)
            {
                ReleaseTarget();
                nint shared = 0;
                _texture9 = _engine.Device9.CreateTexture((uint)width, (uint)height, 1,
                    Vortice.Direct3D9.Usage.RenderTarget, Vortice.Direct3D9.Format.A8R8G8B8, Pool.Default, ref shared);
                _surface9 = _texture9.GetSurfaceLevel(0);
                _texture11 = _engine.Device.OpenSharedResource<ID3D11Texture2D>(shared);
                _target = _engine.Device.CreateRenderTargetView(_texture11);
                _width = width; _height = height;
            }
            // WPF can drop its reference when the front buffer becomes unavailable.
            // Native targets may still be valid, so rebind even when size has not changed.
            // Reusing the same bound pointer is a no-op; software fallback also preserves it.
            Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _surface9!.NativePointer,
                enableSoftwareFallback: true);
            var parameters = GetParameters();
            parameters.Shape.X = width; parameters.Shape.Y = height;
            parameters.Region = new Vector4(
                (float)((bounds.X - capture.Bounds.X) / capture.Bounds.Width),
                (float)((bounds.Y - capture.Bounds.Y) / capture.Bounds.Height),
                (float)(bounds.Width / capture.Bounds.Width), (float)(bounds.Height / capture.Bounds.Height));
            parameters.TextureSize = new Vector4(capture.Texture.Description.Width, capture.Texture.Description.Height, 0, 0);
            _engine.Draw(capture, _target!, width, height, parameters);
            Image.AddDirtyRect(new Int32Rect(0, 0, width, height));
            Draws++;
            SetAvailable(true);
            return true;
        }
        finally { Image.Unlock(); }
    }

    private void ReleaseTarget()
    {
        Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, 0);
        _target?.Dispose(); _target = null;
        _texture11?.Dispose(); _texture11 = null;
        _surface9?.Dispose(); _surface9 = null;
        _texture9?.Dispose(); _texture9 = null;
        _width = _height = 0;
    }

    public void Dispose()
    {
        Image.Lock();
        try { ReleaseTarget(); }
        finally { Image.Unlock(); }
        _engine.Detach(this);
    }
}
