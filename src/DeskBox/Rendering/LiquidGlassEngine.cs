using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D9;
using Vortice.D3DCompiler;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using DeskBox.Services;
using Rect = System.Windows.Rect;

namespace DeskBox.Rendering;

/// <summary>Shared GPU device and capture sessions for all desktop boxes. UI-thread owned.</summary>
internal sealed class LiquidGlassEngine : IDisposable
{
    // 捕获会话重建（例如 Win+D 后 Wallpaper Engine 窗口重建）期间暂时没有新帧时，
    // 最多沿用 GlassSurface 里最后一帧的时长，超过后才回退普通背景。
    private const long FrameFreezeMs = 4000;
    private static LiquidGlassEngine? _current;
    private readonly List<GlassSurface> _surfaces = new();
    private readonly List<WallpaperCapture> _captures = new();
    private readonly Dictionary<nint, long> _failed = new();
    private readonly IDirect3D9Ex _direct9;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly ID3D11VertexShader _vertex;
    private readonly ID3D11PixelShader _pixel;
    private readonly ID3D11PixelShader _blurPixel;
    private readonly Dictionary<WallpaperCapture, BlurBuffer> _blurBuffers = new();
    private readonly ID3D11Buffer _constants;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11Query _completion;
    private readonly DispatcherTimer _timer;
    private long _nextDiscover;
    private bool _disposed;
    internal ID3D11Device Device { get; }
    internal ID3D11DeviceContext Context { get; }
    internal IDirect3DDevice9Ex Device9 { get; }
    internal IReadOnlyList<WallpaperCapture> Captures => _captures;
    internal IReadOnlyList<GlassSurface> Surfaces => _surfaces;
    internal double LastTickMilliseconds { get; private set; }
    internal long Ticks { get; private set; }
    internal string SurfaceStatus => string.Join("; ", _surfaces.Select(s => $"draws={s.Draws}, lockMisses={s.LockMisses}, front={s.Image.IsFrontBufferAvailable}, bounds={s.GetBounds()}"));
    internal void SaveSurface(string path) => _surfaces.FirstOrDefault()?.SaveFrame(path);

    internal void SaveDiagnostics(string output)
    {
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "renderer.txt"),
            $"dark={ThemeManager.IsDark}; ticks={Ticks}; tickMs={LastTickMilliseconds:F2}\n{SurfaceStatus}\n" +
            string.Join("\n", _captures.Select(c => $"window={c.Window}; host={c.CaptureWindow}; frames={c.Frames}; bounds={c.Bounds}")));
        for (var i = 0; i < _surfaces.Count; i++)
            _surfaces[i].SaveFrame(Path.Combine(output, $"surface-{i}.png"));
        for (var i = 0; i < _captures.Count; i++)
            if (_captures[i].Texture is { } texture)
                GlassPreview.SaveTexture(this, texture, Path.Combine(output, $"wallpaper-{i}.png"));
    }

    private LiquidGlassEngine(nint window)
    {
        var watch = Stopwatch.StartNew();
        try
        {
        if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("Windows 不支持实时画面捕获。");
        Vortice.Direct3D11.D3D11.D3D11CreateDevice((Vortice.DXGI.IDXGIAdapter?)null, DriverType.Hardware,
            DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_0], out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        Device = device; Context = context;
        var deviceMs = watch.ElapsedMilliseconds;
        _winrtDevice = GlassInterop.CreateWinRtDevice(device);
        _direct9 = D3D9.Direct3DCreate9Ex();
        Device9 = _direct9.CreateDeviceEx(0, DeviceType.Hardware, window,
            CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve,
            new PresentParameters
            {
                Windowed = true, SwapEffect = SwapEffect.Discard, DeviceWindowHandle = window,
                BackBufferWidth = 1, BackBufferHeight = 1, BackBufferFormat = Vortice.Direct3D9.Format.Unknown,
                PresentationInterval = PresentInterval.Immediate
            });
        var device9Ms = watch.ElapsedMilliseconds;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DeskBox.Rendering.LiquidGlass.hlsl")!;
        var shader = new StreamReader(stream).ReadToEnd();
        _vertex = device.CreateVertexShader(Compiler.Compile(shader, "VS", "LiquidGlass", "vs_5_0").Span);
        _pixel = device.CreatePixelShader(Compiler.Compile(shader, "PS", "LiquidGlass", "ps_5_0").Span);
        _blurPixel = device.CreatePixelShader(Compiler.Compile(shader, "BlurPS", "LiquidGlass", "ps_5_0").Span);
        var shaderMs = watch.ElapsedMilliseconds;
        _constants = device.CreateBuffer(80, BindFlags.ConstantBuffer);
        _completion = device.CreateQuery(new QueryDescription(Vortice.Direct3D11.QueryType.Event));
        _sampler = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxLOD = float.MaxValue
        });
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000.0 / 30) };
        _timer.Tick += Tick;
        _timer.Start();
        Log.Info($"[启动] Liquid Glass 引擎初始化 {watch.ElapsedMilliseconds}ms（D3D11 {deviceMs}ms / D3D9Ex {device9Ms - deviceMs}ms / 着色器 {shaderMs - device9Ms}ms），自进程启动 {StartupTrace.SinceProcessStartMs}ms");
        }
        catch { Dispose(); throw; }
    }

    internal static GlassSurface Attach(nint window, Func<Rect> bounds, Func<bool> active,
        Func<GlassParameters> parameters, Action<bool> availability, Action recover)
    {
        _current ??= new LiquidGlassEngine(window);
        var surface = new GlassSurface(_current, bounds, active, parameters, availability, recover);
        _current._surfaces.Add(surface);
        return surface;
    }

    internal static LiquidGlassEngine? Current => _current;

    internal void Detach(GlassSurface surface)
    {
        _surfaces.Remove(surface);
        if (_surfaces.Count == 0) Dispose();
    }

    private void Discover()
    {
        foreach (var capture in _captures.Where(c => c.Closed || !GlassInterop.IsWindow(c.Window)).ToArray())
        {
            _captures.Remove(capture); capture.Dispose();
            if (_blurBuffers.Remove(capture, out var blur)) blur.Dispose();
        }
        var windows = GlassInterop.FindWallpaperWindows();
        foreach (var stale in _failed.Keys.Where(h => !windows.Contains(h)).ToArray()) _failed.Remove(stale);
        foreach (var window in windows)
        {
            var root = GlassInterop.GetAncestor(window, 2);
            if (_captures.Any(c => c.Window == window || c.CaptureWindow == root)
                || (_failed.TryGetValue(window, out var retry) && Environment.TickCount64 < retry)) continue;
            try
            {
                var capture = new WallpaperCapture(_winrtDevice, window);
                if (_captures.Any(c => c.CaptureWindow == capture.CaptureWindow)) { capture.Dispose(); continue; }
                _captures.Add(capture);
                Log.Info($"Liquid Glass: 已开始捕获 Wallpaper Engine 窗口 {window}。");
            }
            catch (Exception ex)
            {
                _failed[window] = Environment.TickCount64 + 15000;
                Log.Warn($"Liquid Glass: 壁纸窗口 {window} 捕获失败: {ex.Message}");
            }
        }
    }

    private void Tick(object? sender, EventArgs args)
    {
        if (_disposed) return;
        var active = _surfaces.Where(s => s.IsActive()).ToArray();
        if (active.Length == 0)
        {
            // Hidden boxes should not keep capturing/processing a 4K video background.
            foreach (var capture in _captures) capture.Dispose();
            _captures.Clear();
            foreach (var blur in _blurBuffers.Values) blur.Dispose();
            _blurBuffers.Clear();
            _nextDiscover = 0;
            return;
        }
        var start = Stopwatch.GetTimestamp();
        try
        {
            if (Environment.TickCount64 >= _nextDiscover)
            { Discover(); _nextDiscover = Environment.TickCount64 + 3000; }
            foreach (var capture in _captures) capture.Poll(Device, Context);
            var blurRadius = active.Max(s => s.GetParameters().Material.X);
            foreach (var capture in _captures.Where(c => c.Texture is not null)) PrepareBlur(capture, blurRadius);
            foreach (var surface in active)
            {
                var bounds = surface.GetBounds();
                var capture = _captures.Where(c => c.Texture is not null && !c.Closed)
                    .OrderByDescending(c => IntersectionArea(c.Bounds, bounds)).FirstOrDefault();
                if (capture is not null && IntersectionArea(capture.Bounds, bounds) > 0) { surface.Render(capture); continue; }
                // 捕获会话可能正在重建（例如 Win+D 后 Wallpaper Engine 窗口重建），期间暂时没有可用纹理。
                // 此时保留 D3DImage 里已绘制的最后一帧，避免 SetAvailable(false) 让玻璃背景闪一下；
                // 超过 FrameFreezeMs 仍没有新帧才回退普通背景，防止画面永久卡在旧帧。
                if (surface.HasFrame && Environment.TickCount64 - surface.LastDrawTick <= FrameFreezeMs) continue;
                surface.SetAvailable(false);
            }
            Ticks++;
        }
        catch (Exception ex)
        {
            RecoverAfterFailure(ex);
        }
        LastTickMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    // A removed D3D device cannot render again. Release every owner's surface
    // before their delayed retries attach to a fresh shared engine.
    internal void RecoverAfterFailure(Exception error)
    {
        Log.Warn("Liquid Glass 渲染故障，回退普通背景并自动重建 GPU 服务: " + error);
        _timer.Stop();
        var surfaces = _surfaces.ToArray();
        foreach (var surface in surfaces) surface.SetAvailable(false);
        foreach (var surface in surfaces) surface.Recover();
    }

    private static double IntersectionArea(Rect first, Rect second)
    {
        first.Intersect(second);
        return first.IsEmpty ? 0 : first.Width * first.Height;
    }

    private void PrepareBlur(WallpaperCapture capture, float radius)
    {
        var source = capture.Texture!;
        var size = source.Description;
        if (!_blurBuffers.TryGetValue(capture, out var blur) || blur.Width != Math.Max(1, size.Width / 2)
            || blur.Height != Math.Max(1, size.Height / 2))
        {
            blur?.Dispose();
            blur = new BlurBuffer(Device, size.Width, size.Height);
            _blurBuffers[capture] = blur;
        }
        if (blur.Frame == capture.Frames && blur.Radius == radius) return;
        RenderBlur(source, blur, radius);
        blur.Frame = capture.Frames; blur.Radius = radius;
    }

    private void RenderBlur(ID3D11Texture2D source, BlurBuffer blur, float radius)
    {
        var size = source.Description;
        var parameters = new GlassParameters
        {
            Material = new System.Numerics.Vector4(radius, 0, 0, 0),
            TextureSize = new System.Numerics.Vector4(size.Width, size.Height, 1, 0)
        };
        DrawPass(source, null, blur.HorizontalTarget, (int)blur.Width, (int)blur.Height, parameters, _blurPixel);
        parameters.Material.X = radius / 2;
        parameters.TextureSize = new System.Numerics.Vector4(blur.Width, blur.Height, 0, 1);
        DrawPass(blur.Horizontal, null, blur.OutputTarget, (int)blur.Width, (int)blur.Height, parameters, _blurPixel);
    }

    // The appearance test uses the production shader passes on a fixed texture so
    // wallpaper motion cannot masquerade as a response to a slider.
    internal System.Windows.Media.Imaging.BitmapSource RenderSample(ID3D11Texture2D source,
        int width, int height, GlassParameters parameters)
    {
        using var blur = new BlurBuffer(Device, source.Description.Width, source.Description.Height);
        RenderBlur(source, blur, parameters.Material.X);
        using var target = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
            Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            BindFlags = BindFlags.RenderTarget
        });
        using var view = Device.CreateRenderTargetView(target);
        parameters.Region = new System.Numerics.Vector4(0, 0, 1, 1);
        parameters.Shape.X = width; parameters.Shape.Y = height;
        parameters.TextureSize = new System.Numerics.Vector4(source.Description.Width, source.Description.Height, 0, 0);
        DrawPass(source, blur.Output, view, width, height, parameters, _pixel);
        return GlassPreview.CopyTexture(this, target);
    }

    internal void Draw(WallpaperCapture capture, ID3D11RenderTargetView target, int width, int height, GlassParameters parameters)
    {
        DrawPass(capture.Texture!, _blurBuffers[capture].Output, target, width, height, parameters, _pixel);
        // D3D9 and D3D11 have separate queues. Finish writes before WPF reads this shared surface.
        Context.End(_completion);
        Context.Flush();
        var start = Stopwatch.GetTimestamp();
        while (!Context.IsDataAvailable(_completion, AsyncGetDataFlags.DoNotFlush))
        {
            if (Stopwatch.GetElapsedTime(start).TotalMilliseconds > 100)
                throw new TimeoutException("玻璃 GPU 表面同步超时。");
            Thread.SpinWait(32);
        }
    }

    private void DrawPass(ID3D11Texture2D source, ID3D11Texture2D? blurred, ID3D11RenderTargetView target,
        int width, int height, GlassParameters parameters, ID3D11PixelShader shader)
    {
        using var view = Device.CreateShaderResourceView(source);
        using var blurredView = blurred is null ? null : Device.CreateShaderResourceView(blurred);
        Context.UpdateSubresource(parameters, _constants);
        Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        Context.VSSetShader(_vertex);
        Context.PSSetShader(shader);
        Context.PSSetConstantBuffer(0, _constants);
        Context.PSSetSampler(0, _sampler);
        Context.PSSetShaderResource(0, view);
        Context.PSSetShaderResource(1, blurredView!);
        Context.OMSetRenderTargets(target);
        Context.RSSetViewport(0, 0, width, height);
        Context.Draw(3, 0);
        Context.PSSetShaderResource(0, null!);
        Context.PSSetShaderResource(1, null!);
        Context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
    }

    internal static void Shutdown() => _current?.Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_timer is not null) { _timer.Stop(); _timer.Tick -= Tick; }
        foreach (var capture in _captures) capture.Dispose();
        _captures.Clear();
        foreach (var blur in _blurBuffers.Values) blur.Dispose();
        _blurBuffers.Clear();
        _completion?.Dispose(); _sampler?.Dispose(); _constants?.Dispose(); _blurPixel?.Dispose(); _pixel?.Dispose(); _vertex?.Dispose();
        Device9?.Dispose(); _direct9?.Dispose();
        (_winrtDevice as IDisposable)?.Dispose();
        Context?.Dispose(); Device?.Dispose();
        if (ReferenceEquals(_current, this)) _current = null;
    }
}
