using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DeskBox.Views;

/// <summary>极简输入对话框（重命名等场景）。</summary>
public static class PromptDialog
{
    public static string? Show(Window? owner, string title, string label, string initial)
    {
        var box = new TextBox
        {
            Text = initial,
            Style = Application.Current.TryFindResource("FlatTextBox") as Style,
            Margin = new Thickness(0, 10, 0, 0),
        };

        var okButton = new Button
        {
            Content = "确定",
            Style = Application.Current.TryFindResource("PrimaryButton") as Style,
            Margin = new Thickness(8, 0, 0, 0),
        };

        var cancelButton = new Button
        {
            Content = "取消",
            Style = Application.Current.TryFindResource("GhostButton") as Style,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(okButton);

        var content = new StackPanel { Margin = new Thickness(20) };
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource("TextPrimaryBrush"),
        });
        content.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = (Brush)Application.Current.FindResource("TextSecondaryBrush"),
        });
        content.Children.Add(box);
        content.Children.Add(buttons);

        var frame = new Border
        {
            Background = (Brush)Application.Current.FindResource("BoxBgBrush"),
            BorderBrush = (Brush)Application.Current.FindResource("BoxBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = content,
            Effect = new DropShadowEffect
            {
                BlurRadius = 22,
                ShadowDepth = 3,
                Opacity = 0.28,
                Color = Colors.Black,
            },
        };

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is { IsVisible: true }
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
            Content = new Border { Margin = new Thickness(16), Child = frame },
            Width = 360,
        };

        if (owner is { IsVisible: true })
        {
            window.Owner = owner;
        }

        string? result = null;

        okButton.Click += (_, _) =>
        {
            result = box.Text;
            window.DialogResult = true;
        };

        cancelButton.Click += (_, _) => window.DialogResult = false;

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                result = box.Text;
                window.DialogResult = true;
            }
            else if (e.Key == Key.Escape)
            {
                window.DialogResult = false;
            }
        };

        window.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };

        window.ShowDialog();
        return window.DialogResult == true ? result : null;
    }
}
