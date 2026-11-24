using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskBox.Interop;
using DeskBox.Services;

namespace DeskBox.Views;

internal partial class LauncherWindow
{
    private static string TaskManagerPath => Path.Combine(Environment.SystemDirectory, "Taskmgr.exe");
    private static string RegistryEditorPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "regedit.exe");
    private static string ExplorerPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    private void InitializeToolbarIcons()
    {
        var pen = new Pen(TextBrush, 1.65)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();
        foreach (var button in Toolbar.Children.OfType<Button>())
        {
            if (button.Content is Geometry geometry)
                button.Content = new DrawingImage(new GeometryDrawing(null, pen, geometry));
        }
    }

    private async void LoadWindowsToolbarIcons(object sender, RoutedEventArgs e)
    {
        Loaded -= LoadWindowsToolbarIcons;
        await ApplyWindowsToolbarIconsAsync();
    }

    internal async Task ApplyWindowsToolbarIconsAsync()
    {
        var systemGlyphs = new[]
        {
            (InstalledAppsButton, 0xE71D), (SleepButton, 0xE708), (RestartButton, 0xE72C), (ShutDownButton, 0xE7E8),
        };
        foreach (var family in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
            if (!new Typeface(family).TryGetGlyphTypeface(out var font) ||
                !systemGlyphs.All(glyph => font.CharacterToGlyphMap.ContainsKey(glyph.Item2))) continue;
            foreach (var (button, character) in systemGlyphs)
            {
                var outline = font.GetGlyphOutline(font.CharacterToGlyphMap[character], 20, 20);
                if (!outline.Bounds.IsEmpty) button.Content = new DrawingImage(new GeometryDrawing(TextBrush, null, outline));
            }
            break;
        }

        var folders = Task.Run(() => (
            Documents: Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Downloads: ShellItemInterop.GetKnownFolderPath(ShellItemInterop.DownloadsFolderId)));
        var apps = Task.WhenAll(
            LoadIconAsync(TaskManagerButton, TaskManagerPath),
            LoadIconAsync(RegistryEditorButton, RegistryEditorPath),
            LoadIconAsync(ExplorerButton, ExplorerPath),
            LoadIconAsync(WindowsSettingsButton,
                @"shell:AppsFolder\windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ImmersiveControlPanel", "SystemSettings.exe")));
        var (documents, downloads) = await folders;
        await Task.WhenAll(apps, LoadIconAsync(DocumentsButton, documents), LoadIconAsync(DownloadsButton, downloads));

        async Task LoadIconAsync(Button button, string? path, string? alternate = null)
        {
            var icon = string.IsNullOrEmpty(path) ? null : await IconCache.GetAsync(path, 40);
            if (icon is null && alternate is not null) icon = await IconCache.GetAsync(alternate, 40);
            if (icon is not null && TrimToolbarIcon(icon) is { } visible) button.Content = visible;
        }
    }

    internal static BitmapSource? TrimToolbarIcon(BitmapSource icon)
    {
        // Ignore nearly transparent shadows when sizing the visible artwork.
        const byte minimumVisibleAlpha = 24;
        var source = icon.Format == PixelFormats.Bgra32 || icon.Format == PixelFormats.Pbgra32
            ? icon : new FormatConvertedBitmap(icon, PixelFormats.Bgra32, null, 0);
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);
        var left = width;
        var top = height;
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (pixels[y * stride + x * 4 + 3] < minimumVisibleAlpha) continue;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }
        if (right < left) return null;
        var bounds = new Int32Rect(left, top, right - left + 1, bottom - top + 1);
        if (bounds.Width == width && bounds.Height == height) return icon;
        var cropped = new CroppedBitmap(icon, bounds);
        cropped.Freeze();
        return cropped;
    }
}
