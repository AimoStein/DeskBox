using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Rendering;

/// <summary>Only the background is rendered with glass; WPF content stays crisp.</summary>
public sealed class GlassBackdrop : Grid, IDisposable
{
    internal const double RefractionBandWidth = 8;
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private GlassSurface? _surface;
    private Window? _window;
    private bool _available;
    private float _hover;
    private int _attachRetries;
    private System.Windows.Threading.DispatcherTimer? _retryTimer;
    public AppConfig? Config { get; set; }
    public double? CornerRadiusOverride { get; set; }
    public Func<bool>? EnabledOverride { get; set; }
    private bool GlassEnabled => EnabledOverride?.Invoke() ?? Config?.LiquidGlassEnabled == true;
    public bool IsAvailable => _available;
    public event Action<bool>? AvailabilityChanged;

    public GlassBackdrop()
    {
        Children.Add(_image);
        IsHitTestVisible = false;
        Opacity = 0;
        Loaded += (_, _) => ScheduleAttach();
        Unloaded += (_, _) => Detach();
    }

    /// <summary>
    /// 启动阶段把玻璃初始化推迟到盒子的第一帧之后：
    /// D3D 设备 / 着色器编译 / 画面捕获初始化要三四百毫秒，放在 Show() 里面会直接挡住"盒子出现"。
    /// 盒子先以边框 + 图标亮出来，玻璃背景随后再补上；运行期（设置里开关玻璃）保持同步行为。
    ///
    /// 为什么等 ContentRendered 而不是固定延时：开机冷启动时首帧可能比暖启动晚很多，
    /// 固定延时可能仍落在首帧前；以"这个窗口真的画完第一帧"为准最稳，另有超时兜底。
    /// </summary>
    private void ScheduleAttach()
    {
        if (!Services.StartupTrace.IsStartup)
        {
            Attach();
            return;
        }

        var window = Window.GetWindow(this);
        if (window is null)
        {
            _ = Task.Delay(220).ContinueWith(_ => Dispatcher.BeginInvoke(
                new Action(Attach),
                System.Windows.Threading.DispatcherPriority.Background));
            return;
        }

        void OnFirstFrame(object? sender, EventArgs e)
        {
            window.ContentRendered -= OnFirstFrame;

            // 放到下一次 Dispatcher 空闲再初始化：玻璃引擎首次创建要一两百毫秒，
            // 在 ContentRendered 里同步做会把其它盒子的首帧一起拖后。
            Dispatcher.BeginInvoke(
                new Action(Attach),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        window.ContentRendered += OnFirstFrame;

        // 兜底：窗口被隐藏 / 首帧事件没来（例如盒子一开始就是收起隐藏状态）也要把玻璃补上。
        var timeout = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1500),
        };
        timeout.Tick += (_, _) =>
        {
            timeout.Stop();
            window.ContentRendered -= OnFirstFrame;
            if (_surface is null)
            {
                Attach();
            }
        };
        timeout.Start();
    }

    public void Refresh()
    {
        if (GlassEnabled) Attach();
        else Detach();
    }

    private void Attach()
    {
        if (_surface is not null || !IsLoaded || Config is null || !GlassEnabled) return;
        _window = Window.GetWindow(this);
        if (_window is null) return;
        var watch = StartupTrace.IsStartup ? System.Diagnostics.Stopwatch.StartNew() : null;
        try
        {
            _surface = LiquidGlassEngine.Attach(new WindowInteropHelper(_window).Handle,
                GetScreenBounds, () => IsVisible && _window.IsVisible && GlassEnabled,
                GetParameters, SetAvailable, Recover);
            _image.Source = _surface.Image;
            if (watch is not null)
            {
                Log.Info($"[启动] 盒子玻璃背景接入 {watch.ElapsedMilliseconds}ms，自进程启动 {StartupTrace.SinceProcessStartMs}ms");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Liquid Glass 初始化失败: " + ex);
            SetAvailable(false);

            ScheduleRetry();
        }
    }

    private void Recover()
    {
        Detach();
        ScheduleRetry();
    }

    private void ScheduleRetry()
    {
        if (_retryTimer is not null || !IsLoaded || !GlassEnabled) return;
        // Driver recovery may take time. Retry outside the failed render stack,
        // backing off to 30 seconds while leaving normal rendering unchanged.
        _retryTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(_attachRetries++, 5))))
        };
        _retryTimer.Tick += (_, _) =>
        {
            _retryTimer.Stop();
            _retryTimer = null;
            Attach();
        };
        _retryTimer.Start();
    }

    private Rect GetScreenBounds()
    {
        if (!IsVisible || ActualWidth < 1 || ActualHeight < 1) return Rect.Empty;
        var top = PointToScreen(new Point(0, 0));
        var bottom = PointToScreen(new Point(ActualWidth, ActualHeight));
        return new Rect(top, bottom);
    }

    private GlassParameters GetParameters()
    {
        var config = Config!;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var mouse = Mouse.GetPosition(this);
        _hover += ((_window?.IsMouseOver == true ? 1f : 0f) - _hover) * 0.18f;
        return new GlassParameters
        {
            Shape = new Vector4(0, 0, (float)((CornerRadiusOverride ?? config.CornerRadius) * dpi), (float)(RefractionBandWidth * dpi)),
            Material = new Vector4((float)(config.GlassBlur * dpi), (float)(config.GlassRefraction * dpi),
                (float)config.BoxOpacity, ThemeManager.IsDark ? 1 : 0),
            Pointer = new Vector4((float)(mouse.X / Math.Max(ActualWidth, 1)),
                (float)(mouse.Y / Math.Max(ActualHeight, 1)), _hover, 0)
        };
    }

    private void SetAvailable(bool available)
    {
        if (available) _attachRetries = 0;
        Opacity = available ? 1 : 0;
        if (_available == available) return;
        _available = available;
        AvailabilityChanged?.Invoke(available);
    }

    private void Detach()
    {
        _retryTimer?.Stop();
        _retryTimer = null;
        _image.Source = null;
        var surface = _surface;
        _surface = null;
        try { surface?.Dispose(); }
        catch (Exception ex) { Log.Warn("Liquid Glass 释放故障表面: " + ex.Message); }
        SetAvailable(false);
        _window = null;
    }

    public void Dispose() => Detach();
}
