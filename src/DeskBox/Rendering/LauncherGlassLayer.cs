using System.Windows;
using System.Windows.Media;

namespace DeskBox.Rendering;

internal sealed class LauncherGlassLayer : FrameworkElement
{
    private readonly UIElement _scene;
    private readonly VisualBrush _brush;
    private readonly LauncherGlass? _glass;
    private readonly double _strength;
    private readonly UIElement? _anchor;
    private readonly Rect _region;
    private Rect _bounds = Rect.Empty;

    internal LauncherGlassLayer(UIElement scene, LauncherGlass? glass, double strength, UIElement? anchor, Rect region)
    {
        _scene = scene;
        _brush = new VisualBrush(scene) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        _glass = glass;
        _strength = strength;
        _anchor = anchor;
        _region = region;
        Effect = glass;
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext context)
    {
        // A default absolute Viewbox samples a tiny patch of the scene.
        // Set the viewbox and shader size before the first drawing exists.
        if (Align()) context.DrawRectangle(_brush, null, new Rect(RenderSize));
    }

    internal bool Align()
    {
        if (RenderSize.Width < 1 || RenderSize.Height < 1 || VisualTreeHelper.GetParent(this) is null) return false;
        // A Canvas arranges each child's content before applying Canvas.Left/Top.
        // Use the planned bounds of a sector in the already positioned canvas.
        var origin = _anchor is null ? TranslatePoint(new Point(), _scene) : _anchor.TranslatePoint(_region.TopLeft, _scene);
        var bounds = new Rect(origin, _anchor is null ? RenderSize : _region.Size);
        if (_bounds == bounds) return true;
        _bounds = bounds;
        _brush.Viewbox = bounds;
        if (_glass is not null)
        {
            _glass.SurfaceSize = new Point(bounds.Width, bounds.Height);
            _glass.Strength = _strength;
        }
        return true;
    }
}
