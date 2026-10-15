using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Views;
using Vortice.Direct3D11;

namespace DeskBox.Rendering;

/// <summary>Exercises settings events, real item bindings and GPU pixels against a fixed pattern.</summary>
internal static class AppearanceTest
{
    internal static unsafe void Run(LiquidGlassEngine engine, BoxWindow[] windows, AppConfig config,
        string output, Action<string, bool> check)
    {
        var original = (config.BoxOpacity, config.IconSize, config.CornerRadius, config.GlassBlur, config.GlassRefraction);
        var wasDark = ThemeManager.IsDark;
        var settings = new SettingsWindow(config);
        var defaults = new AppConfig();
        check("默认外观符合指定参数", defaults.BoxOpacity == 0.35 && defaults.IconSize == 37
            && defaults.CornerRadius == 22 && defaults.GlassBlur == 0 && defaults.GlassRefraction == 28);
        settings.RefreshState();
        check("滑块范围支持浓度 0–100%、圆角 10–100、折射 0–80",
            ((Slider)settings.FindName("SlOpacity")).Minimum == 0
            && ((Slider)settings.FindName("SlRadius")).Minimum == 10
            && ((Slider)settings.FindName("SlRadius")).Maximum == 100
            && ((Slider)settings.FindName("SlGlassRefraction")).Maximum == 80);
        settings.Applied += (_, _) =>
        {
            foreach (var window in windows) { window.RefreshFromConfig(false); window.UpdateLayout(); }
        };
        void Set(string name, double value) => ((Slider)settings.FindName(name)).Value = value;
        var item = ((ItemsControl)windows[0].FindName("ItemsHost")).Items[0];
        foreach (var size in new[] { 32, 96 })
        {
            Set("SlIcon", size);
            check($"图标滑块实时更新图标视图到 {size}px", ItemImages(windows[0]).All(i => i.ActualWidth == size));
            check("图标缩放保留原有条目", ReferenceEquals(item, ((ItemsControl)windows[0].FindName("ItemsHost")).Items[0]));
            windows[1].Config.ListView = true;
            windows[1].RefreshFromConfig(false); windows[1].UpdateLayout();
            var listImages = ItemImages(windows[1]).ToArray();
            check($"图标滑块实时更新列表视图到 {size}px", listImages.Length > 0 && listImages.All(i => i.ActualWidth == size));
        }
        windows[1].Config.ListView = false;

        const int width = 256, height = 192;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var index = (y * width + x) * 4;
                var stripe = ((x / 8 + y / 8) % 2) == 0;
                pixels[index] = stripe ? (byte)230 : (byte)45;
                pixels[index + 1] = stripe ? (byte)180 : (byte)65;
                pixels[index + 2] = stripe ? (byte)40 : (byte)210;
                pixels[index + 3] = 255;
            }
        var description = new Texture2DDescription
        {
            Width = width, Height = height, MipLevels = 1, ArraySize = 1,
            Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0), BindFlags = BindFlags.ShaderResource
        };
        ID3D11Texture2D texture;
        fixed (byte* data = pixels)
            texture = engine.Device.CreateTexture2D(description, new SubresourceData(data, width * 4));
        using (texture)
        {
            BitmapSource Sample(string name)
            {
                var image = engine.RenderSample(texture, width, height, engine.Surfaces[0].GetParameters());
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
                return image;
            }
            foreach (var dark in new[] { false, true })
            {
                ThemeManager.Apply(dark ? ThemeMode.Dark : ThemeMode.Light);
                Set("SlGlassBlur", 0); Set("SlGlassRefraction", 0); Set("SlRadius", 22);
                Set("SlOpacity", 35);
                var faint = Sample(dark ? "dark-concentration-35" : "light-concentration-35");
                Set("SlOpacity", 100);
                var dense = Sample(dark ? "dark-concentration-100" : "light-concentration-100");
                check($"{(dark ? "深色" : "浅色")}背景浓度滑块改变实际 GPU 像素", Difference(faint, dense, false) > 10);
            }
            Set("SlOpacity", 0);
            var transparent = Sample("concentration-0");
            var clearPixel = new byte[4]; transparent.CopyPixels(new Int32Rect(64, 64, 1, 1), clearPixel, 4, 0);
            check("背景浓度 0% 保留原始壁纸颜色", Math.Abs(clearPixel[0] - 230) <= 1
                && Math.Abs(clearPixel[1] - 180) <= 1 && Math.Abs(clearPixel[2] - 40) <= 1);
            Set("SlOpacity", 35);
            var sharp = Sample("blur-0");
            Set("SlGlassBlur", 16);
            var blurred = Sample("blur-16");
            check("玻璃模糊滑块改变实际 GPU 像素", Difference(sharp, blurred, false) > 10);
            Set("SlGlassBlur", 0);
            var flat = Sample("refraction-0");
            Set("SlGlassRefraction", 28);
            var refracted = Sample("refraction-28");
            check("玻璃折射滑块改变边缘实际 GPU 像素", Difference(flat, refracted, true) > 10);
            check("折射限制在窄边缘内，中心纹理不被扭曲", InteriorDifference(flat, refracted,
                (int)Math.Ceiling(engine.Surfaces[0].GetParameters().Shape.W)) == 0);
            Set("SlGlassRefraction", 80);
            var strong = Sample("refraction-80");
            check("折射可以强于旧上限 28", config.GlassRefraction == 80 && Difference(refracted, strong, true) > 2);
            Set("SlRadius", 10);
            var square = Sample("radius-10");
            Set("SlRadius", 100);
            var round = Sample("radius-100");
            check("圆角滑块改变 GPU 轮廓", Alpha(square, 8, 8) > 200 && Alpha(round, 8, 8) == 0);
            foreach (var window in windows)
            {
                var glass = (GlassBackdrop)window.FindName("GlassBackground");
                var frame = (Border)window.FindName("Frame");
                check("前景与玻璃背景使用同一圆角", glass.Clip is RectangleGeometry g && frame.Clip is RectangleGeometry f
                    && g.RadiusX == 100 && f.RadiusX == 100 && g.Bounds.Size == f.Bounds.Size);
                var clipImage = new RenderTargetBitmap((int)glass.ActualWidth, (int)glass.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                clipImage.Render(glass);
                check("圆角裁剪不留下旧玻璃方角", Alpha(clipImage, 1, 1) == 0);
            }
        }
        Set("SlRadius", 22); Set("SlIcon", 37);
        VerifyLayout(windows[0], output, check);
        Set("SlOpacity", original.BoxOpacity * 100); Set("SlIcon", original.IconSize);
        Set("SlRadius", original.CornerRadius); Set("SlGlassBlur", original.GlassBlur); Set("SlGlassRefraction", original.GlassRefraction);
        ThemeManager.Apply(wasDark ? ThemeMode.Dark : ThemeMode.Light);
        settings.Close();
    }

    private static void VerifyLayout(BoxWindow window, string output, Action<string, bool> check)
    {
        var previousWidth = window.Width;
        var fixture = window.Folder;
        var added = Enumerable.Range(1, 7).Select(i => Path.Combine(fixture, $"布局测试-{i}.txt")).ToArray();
        var extra = Path.Combine(fixture, "布局测试-额外.txt");
        foreach (var path in added) File.WriteAllText(path, "isolated layout fixture");
        try
        {
            window.Config.ListView = false; window.RefreshFromConfig(false);
            void Resize(int columns, double extraWidth = 0)
            {
                window.Width = BoxWindow.ShadowPad * 2 + BoxWindow.ContentHorizontalChrome + columns * BoxItem.CellWidth(37) + extraWidth;
                window.FitContentHeight(); window.UpdateLayout();
            }
            window.ReloadItems(); Resize(4);
            check("8 个图标、4 列自动得到 2 行高度", window.Config.Height == BoxWindow.ContentVerticalChrome + 2 * 87);
            var frame = (Border)window.FindName("Frame");
            double FirstInset() => ItemImages(window).Min(i => i.TranslatePoint(new Point(), frame).X);
            var inset = FirstInset();
            check("图标距边缘缩窄为 11 DIP", Math.Abs(inset - 11) <= 1);
            Resize(4, 31);
            var icons = ItemImages(window).Select(i => (Image: i, Point: i.TranslatePoint(new Point(0, 0), frame))).ToArray();
            var firstY = icons.Min(i => i.Point.Y);
            var firstRow = icons.Where(i => Math.Abs(i.Point.Y - firstY) < 1).OrderBy(i => i.Point.X).ToArray();
            var left = firstRow[0].Point.X;
            var right = frame.ActualWidth - firstRow[^1].Point.X - firstRow[^1].Image.ActualWidth;
            check("有宽度余量时，两侧仍保持相同窄留白", firstRow.Length == 4 && Math.Abs(left - right) <= 1 && Math.Abs(left - inset) <= 1);
            GlassPreview.SaveVisual(window, Path.Combine(output, "narrow-insets.png"));
            Resize(3);
            check("手动收窄成 3 列后高度自动变成 3 行", window.Config.Height == BoxWindow.ContentVerticalChrome + 3 * 87);
            var rows = ItemImages(window).ToArray();
            check("末行首个图标仍与首列对齐", Math.Abs(rows[6].TranslatePoint(new Point(), frame).X - inset) <= 1);
            Resize(8);
            check("手动加宽成 8 列后高度自动变成 1 行", window.Config.Height == BoxWindow.ContentVerticalChrome + 87);
            Resize(12, 19);
            check("图标未填满首行时不会整组居中增加留白", Math.Abs(FirstInset() - inset) <= 1);
            Resize(4);
            File.WriteAllText(extra, "isolated count-change fixture"); window.ReloadItems(); window.UpdateLayout();
            check("增加第 9 个图标后高度自动增加一行", window.Config.Height == BoxWindow.ContentVerticalChrome + 3 * 87);
            File.Delete(extra); window.ReloadItems(); window.UpdateLayout();
            check("移除第 9 个图标后高度自动减少一行", window.Config.Height == BoxWindow.ContentVerticalChrome + 2 * 87);
        }
        finally
        {
            foreach (var path in added) File.Delete(path);
            if (File.Exists(extra)) File.Delete(extra);
            window.Width = previousWidth; window.ReloadItems(); window.UpdateLayout();
        }
    }

    private static IEnumerable<Image> ItemImages(DependencyObject visual)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(visual); i++)
        {
            var child = VisualTreeHelper.GetChild(visual, i);
            if (child is Image image && image.DataContext is BoxItem) yield return image;
            foreach (var descendant in ItemImages(child)) yield return descendant;
        }
    }

    private static byte Alpha(BitmapSource bitmap, int x, int y)
    {
        var pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel[3];
    }

    private static double Difference(BitmapSource first, BitmapSource second, bool edgeOnly)
    {
        var a = new byte[first.PixelWidth * first.PixelHeight * 4]; var b = new byte[a.Length];
        first.CopyPixels(a, first.PixelWidth * 4, 0); second.CopyPixels(b, second.PixelWidth * 4, 0);
        double difference = 0; var samples = 0;
        for (var y = 24; y < first.PixelHeight - 24; y++)
            for (var x = 2; x < first.PixelWidth - 2; x++)
            {
                if (edgeOnly && x >= 20 && x < first.PixelWidth - 20) continue;
                var index = (y * first.PixelWidth + x) * 4;
                for (var channel = 0; channel < 3; channel++) { difference += Math.Abs(a[index + channel] - b[index + channel]); samples++; }
            }
        return difference / Math.Max(1, samples);
    }

    private static int InteriorDifference(BitmapSource first, BitmapSource second, int band)
    {
        var a = new byte[first.PixelWidth * first.PixelHeight * 4]; var b = new byte[a.Length];
        first.CopyPixels(a, first.PixelWidth * 4, 0); second.CopyPixels(b, second.PixelWidth * 4, 0);
        var maximum = 0;
        // Stay below the rounded corners and outside the curved lens band.
        for (var y = 24; y < first.PixelHeight - 24; y++)
            for (var x = band; x < first.PixelWidth - band; x++)
            {
                var index = (y * first.PixelWidth + x) * 4;
                for (var channel = 0; channel < 4; channel++)
                    maximum = Math.Max(maximum, Math.Abs(a[index + channel] - b[index + channel]));
            }
        return maximum;
    }
}
