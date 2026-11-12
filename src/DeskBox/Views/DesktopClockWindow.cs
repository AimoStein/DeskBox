using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using DeskBox.Interop;
using WinForms = System.Windows.Forms;

namespace DeskBox.Views;

internal sealed class DesktopClockWindow : Window
{
    private const int DesignWidth = 980;
    private const int DesignHeight = 320;
    private static readonly CultureInfo DateCulture = CultureInfo.GetCultureInfo("zh-CN");
    private readonly TextBlock _time = CreateText("Segoe UI", 188, FontWeights.SemiBold);
    private readonly TextBlock _date = CreateText("Microsoft YaHei UI", 38, FontWeights.Medium);
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background);

    internal DesktopClockWindow()
    {
        Title = "简桌 · 桌面时钟";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        IsHitTestVisible = false;
        Width = DesignWidth;
        Height = DesignHeight;

        Typography.SetNumeralAlignment(_time, FontNumeralAlignment.Tabular);
        var face = new Grid { Width = DesignWidth, Height = DesignHeight };
        face.RowDefinitions.Add(new RowDefinition { Height = new GridLength(242) });
        face.RowDefinitions.Add(new RowDefinition { Height = new GridLength(78) });
        face.Children.Add(_time);
        Grid.SetRow(_date, 1);
        face.Children.Add(_date);
        Content = new Viewbox { Child = face, Stretch = Stretch.Uniform };

        SourceInitialized += (_, _) => NativeMethods.MakeOverlay(Handle);
        _timer.Tick += (_, _) => UpdateTime();
        Closed += (_, _) => _timer.Stop();
        UpdateTime();
        _timer.Start();
    }

    internal IntPtr Handle => new WindowInteropHelper(this).Handle;

    internal void ShowOnDesktop(IntPtr owner)
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        var ownerChanged = NativeMethods.GetWindowOwner(handle) != owner;
        if (ownerChanged) NativeMethods.SetWindowOwner(handle, owner);
        Position(handle);
        if (!IsVisible)
        {
            Show();
            Position(handle);
            NativeMethods.PushToBottom(handle);
        }
        else if (ownerChanged)
        {
            NativeMethods.PushToBottom(handle);
        }
    }

    private static void Position(IntPtr handle)
    {
        if (WinForms.Screen.PrimaryScreen is not { } screen) return;
        var bounds = screen.Bounds;
        var scale = Math.Min(bounds.Width / 2560.0, bounds.Height / 1440.0);
        var width = (int)Math.Round(DesignWidth * scale);
        var height = (int)Math.Round(DesignHeight * scale);
        var x = bounds.Left + (bounds.Width - width) / 2;
        var y = bounds.Top + (int)Math.Round(bounds.Height * 0.075);
        if (NativeMethods.GetWindowRect(handle, out var rect) && rect.Left == x && rect.Top == y &&
            rect.Right == x + width && rect.Bottom == y + height) return;
        NativeMethods.SetWindowPos(handle, IntPtr.Zero, x, y, width, height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER);
    }

    private void UpdateTime()
    {
        var now = DateTime.Now;
        _time.Text = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var date = now.ToString("yyyy年M月d日  dddd", DateCulture);
        if (_date.Text != date) _date.Text = date;
        _timer.Interval = TimeSpan.FromMilliseconds(1000 - now.Millisecond);
    }

    private static TextBlock CreateText(string font, double size, FontWeight weight) => new()
    {
        FontFamily = new FontFamily(font),
        FontSize = size,
        FontWeight = weight,
        Foreground = Brushes.White,
        TextAlignment = TextAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Effect = new DropShadowEffect { Color = Colors.Black, ShadowDepth = 2, BlurRadius = 10, Opacity = 0.7 },
    };
}
