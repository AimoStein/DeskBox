using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskBox.Models;
using DeskBox.Services;
using Vortice.Direct3D11;

namespace DeskBox.Rendering;

/// <summary>Isolated preview/probe: no instance lock, configuration writes, or file operations.</summary>
internal static class GlassPreview
{
    internal static void Run(string? output)
    {
        ThemeManager.Apply(ThemeMode.Light);
        var config = new AppConfig();
        var window = new Window
        {
            Title = "Liquid Glass 预览", Width = 440, Height = 340,
            Left = SystemParameters.WorkArea.Right - 480, Top = SystemParameters.WorkArea.Bottom - 390,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = true, Topmost = true
        };
        var grid = new Grid { Margin = new Thickness(14) };
        grid.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)), CornerRadius = new CornerRadius(22),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Opacity = 0.3 }
        });
        var glass = new GlassBackdrop { Config = config };
        grid.Children.Add(glass);
        var content = new StackPanel { Margin = new Thickness(25, 20, 25, 20) };
        content.Children.Add(new TextBlock { Text = "Liquid Glass", FontSize = 21, FontWeight = FontWeights.SemiBold, Foreground = Brushes.Black });
        content.Children.Add(new TextBlock { Text = "Wallpaper Engine · 实时玻璃", Margin = new Thickness(0, 6, 0, 22), Foreground = Brushes.Black, Opacity = 0.75 });
        foreach (var text in new[] { "📁  项目文件", "📄  工作文档", "🖼  图片素材" })
            content.Children.Add(new TextBlock { Text = text, FontSize = 17, Margin = new Thickness(0, 0, 0, 22), Foreground = Brushes.Black });
        var status = new TextBlock { Text = "正在连接动态壁纸…", FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Black, Opacity = 0.75 };
        content.Children.Add(status);
        grid.Children.Add(content);
        window.Content = grid;
        window.MouseLeftButtonDown += (_, _) => window.DragMove();
        window.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) window.Close(); };
        window.Closed += (_, _) => Application.Current.Shutdown();
        window.Show();
        var samples = new List<string>();
        var count = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            count++;
            var engine = LiquidGlassEngine.Current;
            var frames = engine?.Captures.Sum(c => c.Frames) ?? 0;
            status.Text = frames > 0 ? $"实时捕获 {frames} 帧 · 按 Esc 关闭" : "未取得视频画面，使用普通背景";
            samples.Add($"second={count}, frames={frames}, tickMs={engine?.LastTickMilliseconds:F2}, captures={engine?.Captures.Count}, {engine?.SurfaceStatus}");
            if (output is not null && (count == 3 || count == 6) && engine is not null)
            {
                Directory.CreateDirectory(output);
                var capture = engine.Captures.FirstOrDefault(c => c.Texture is not null);
                if (capture?.Texture is not null) SaveTexture(engine, capture.Texture, Path.Combine(output, $"wallpaper-{count}.png"));
                engine.SaveSurface(Path.Combine(output, $"glass-{count}.png"));
            }
            if (output is not null && count >= 8)
            {
                timer.Stop();
                Directory.CreateDirectory(output);
                File.WriteAllLines(Path.Combine(output, "probe.txt"), samples);
                window.Close();
            }
        };
        timer.Start();
    }

    internal static void SaveVisual(FrameworkElement visual, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth),
            (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }

    internal static bool HasVisibleGlass(FrameworkElement visual)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth),
            (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(bitmap.PixelWidth / 2, bitmap.PixelHeight / 2, 1, 1), pixel, 4, 0);
        return pixel[3] > 200;
    }

    internal static void SaveTexture(LiquidGlassEngine engine, ID3D11Texture2D texture, string path)
    {
        var bitmap = CopyTexture(engine, texture);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }

    internal static BitmapSource CopyTexture(LiquidGlassEngine engine, ID3D11Texture2D texture)
    {
        var description = texture.Description;
        description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        description.MiscFlags = ResourceOptionFlags.None;
        using var staging = engine.Device.CreateTexture2D(description);
        engine.Context.CopyResource(staging, texture);
        var mapped = engine.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var bitmap = BitmapSource.Create((int)description.Width, (int)description.Height, 96, 96,
                PixelFormats.Bgra32, null, mapped.DataPointer, (int)(mapped.RowPitch * description.Height), (int)mapped.RowPitch);
            bitmap.Freeze();
            return bitmap;
        }
        finally { engine.Context.Unmap(staging, 0); }
    }
}
