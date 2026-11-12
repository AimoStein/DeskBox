using DeskBox.Rendering;

namespace DeskBox.Views;

internal partial class LauncherWindow
{
    private void ApplyBackgroundColor()
    {
        var brightness = Math.Clamp(_config.LauncherBackgroundBrightness, 25, 180);
        var hue = Math.Clamp(_config.LauncherBackgroundHue, -180, 180);
        var saturation = Math.Clamp(_config.LauncherBackgroundSaturation, 0, 200);
        if (brightness == 100 && hue == 0 && saturation == 100)
        {
            Scene.Effect = null;
            return;
        }

        _backgroundColor ??= LauncherColor.Create();
        if (_backgroundColor is null) return;
        _backgroundColor.SetColor(brightness / 100.0, hue, saturation / 100.0);
        Scene.Effect = _backgroundColor;
    }
}
