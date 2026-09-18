using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using JianZhuo.Interop;
using JianZhuo.Services;

namespace JianZhuo.Views;

/// <summary>
/// 拖动盒子时铺在桌面上的对齐参考线。
/// 整块屏幕大小、鼠标穿透、不抢焦点，只在拖动期间出现。
/// </summary>
internal sealed class AlignGuideWindow : Window
{
    private readonly Canvas _canvas = new();

    public AlignGuideWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SnapsToDevicePixels = true;

        Content = _canvas;
        SyncToVirtualScreen();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.MakeOverlay(new WindowInteropHelper(this).Handle);
    }

    public void ShowGuides(IReadOnlyList<AlignGuide> guides)
    {
        if (guides.Count == 0)
        {
            HideGuides();
            return;
        }

        // 显示器布局可能变过，每次出现前重新贴合虚拟桌面
        SyncToVirtualScreen();

        var brush = AccentBrush();
        _canvas.Children.Clear();

        foreach (var guide in guides)
        {
            var line = new Line
            {
                Stroke = brush,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                StrokeDashCap = PenLineCap.Round,
                SnapsToDevicePixels = true,
            };

            if (guide.Vertical)
            {
                line.X1 = guide.Position - Left;
                line.X2 = line.X1;
                line.Y1 = guide.From - Top - 3;
                line.Y2 = guide.To - Top + 3;
            }
            else
            {
                line.Y1 = guide.Position - Top;
                line.Y2 = line.Y1;
                line.X1 = guide.From - Left - 3;
                line.X2 = guide.To - Left + 3;
            }

            _canvas.Children.Add(line);
        }

        if (!IsVisible)
        {
            Show();
        }
    }

    public void HideGuides()
    {
        if (IsVisible)
        {
            Hide();
        }

        if (_canvas.Children.Count > 0)
        {
            _canvas.Children.Clear();
        }
    }

    private void SyncToVirtualScreen()
    {
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = Math.Max(1, SystemParameters.VirtualScreenWidth);
        Height = Math.Max(1, SystemParameters.VirtualScreenHeight);
    }

    private static Brush AccentBrush()
    {
        if (Application.Current?.TryFindResource("AccentBrush") is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
    }
}
