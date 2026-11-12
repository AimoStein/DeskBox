using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskBox.Rendering;

namespace DeskBox.Views;

internal partial class LauncherWindow
{
    private void RestoreGradientBackground()
    {
        SetBackground("");
        _config.LauncherBackgroundBrightness = 100;
        _config.LauncherBackgroundHue = 0;
        _config.LauncherBackgroundSaturation = 100;
        ApplyBackgroundColor(100, 0, 100);
        SaveChange();
    }

    private void ApplyBackgroundColor(int brightness, int hue, int saturation)
    {
        brightness = Math.Clamp(brightness, 25, 180);
        hue = Math.Clamp(hue, -180, 180);
        saturation = Math.Clamp(saturation, 0, 200);
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

    private void EditBackgroundColor() => Interact(() =>
    {
        var initial = new[]
        {
            _config.LauncherBackgroundBrightness,
            _config.LauncherBackgroundHue,
            _config.LauncherBackgroundSaturation,
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        var dialog = new Window
        {
            Title = "背景颜色", Owner = this, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Width = 390, SizeToContent = SizeToContent.Height,
            Background = PlateBrush, Foreground = TextBrush, Content = panel,
        };

        var sliders = new[]
        {
            AddSlider("亮度", 25, 180, initial[0], "%"),
            AddSlider("色调", -180, 180, initial[1], "°"),
            AddSlider("饱和度", 0, 200, initial[2], "%"),
        };
        foreach (var slider in sliders) slider.ValueChanged += (_, _) =>
            ApplyBackgroundColor((int)sliders[0].Value, (int)sliders[1].Value, (int)sliders[2].Value);

        var buttons = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        panel.Children.Add(buttons);
        AddButton("重置", Dock.Left, () =>
        {
            sliders[0].Value = 100;
            sliders[1].Value = 0;
            sliders[2].Value = 100;
        });
        AddButton("确定", Dock.Right, () => dialog.DialogResult = true).IsDefault = true;
        AddButton("取消", Dock.Right, () => dialog.DialogResult = false).IsCancel = true;
        if (dialog.ShowDialog() == true)
        {
            _config.LauncherBackgroundBrightness = (int)sliders[0].Value;
            _config.LauncherBackgroundHue = (int)sliders[1].Value;
            _config.LauncherBackgroundSaturation = (int)sliders[2].Value;
            SaveChange();
        }
        else
        {
            ApplyBackgroundColor(initial[0], initial[1], initial[2]);
        }

        Slider AddSlider(string name, int min, int max, int value, string unit)
        {
            var label = new TextBlock { Foreground = TextBrush, Margin = new Thickness(0, 0, 0, 4) };
            var slider = new Slider
            {
                Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), IsSnapToTickEnabled = true,
                TickFrequency = 1, LargeChange = 10, Margin = new Thickness(0, 0, 0, 12),
            };
            void UpdateLabel() => label.Text = $"{name}   {(int)slider.Value}{unit}";
            UpdateLabel();
            slider.ValueChanged += (_, _) => UpdateLabel();
            System.Windows.Automation.AutomationProperties.SetName(slider, name);
            panel.Children.Add(label);
            panel.Children.Add(slider);
            return slider;
        }

        Button AddButton(string label, Dock dock, Action action)
        {
            var button = PanelButton(label);
            DockPanel.SetDock(button, dock);
            button.Click += (_, _) => action();
            buttons.Children.Add(button);
            return button;
        }
    });
}
