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
    public AppConfig? Config { get; set; }
    public bool IsAvailable => _available;
    public event Action<bool>? AvailabilityChanged;

    public GlassBackdrop()
    {
        Children.Add(_image);
        IsHitTestVisible = false;
        Opacity = 0;
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    public void Refresh()
    {
        if (Config?.LiquidGlassEnabled == true) Attach();
        else Detach();
    }

    private void Attach()
    {
        if (_surface is not null || !IsLoaded || Config?.LiquidGlassEnabled != true) return;
        _window = Window.GetWindow(this);
        if (_window is null) return;
        try
        {
            _surface = LiquidGlassEngine.Attach(new WindowInteropHelper(_window).Handle,
                GetScreenBounds, () => IsVisible && _window.IsVisible && Config.LiquidGlassEnabled,
                GetParameters, SetAvailable);
            _image.Source = _surface.Image;
        }
        catch (Exception ex) { Log.Warn("Liquid Glass 初始化失败: " + ex); SetAvailable(false); }
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
            Shape = new Vector4(0, 0, (float)(config.CornerRadius * dpi), (float)(RefractionBandWidth * dpi)),
            Material = new Vector4((float)(config.GlassBlur * dpi), (float)(config.GlassRefraction * dpi),
                (float)config.BoxOpacity, ThemeManager.IsDark ? 1 : 0),
            Pointer = new Vector4((float)(mouse.X / Math.Max(ActualWidth, 1)),
                (float)(mouse.Y / Math.Max(ActualHeight, 1)), _hover, 0)
        };
    }

    private void SetAvailable(bool available)
    {
        Opacity = available ? 1 : 0;
        if (_available == available) return;
        _available = available;
        AvailabilityChanged?.Invoke(available);
    }

    private void Detach()
    {
        _image.Source = null;
        _surface?.Dispose(); _surface = null;
        SetAvailable(false);
        _window = null;
    }

    public void Dispose() => Detach();
}
