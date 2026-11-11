using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using DeskBox.Interop;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DeskBox.Rendering;

/// <summary>Reads the wallpaper configured for each monitor and prepares pixels off the UI thread.</summary>
internal static class StaticWallpaper
{
    internal enum Position { Center, Tile, Stretch, Fit, Fill, Span }

    internal sealed record Monitor(Rectangle Bounds, Rectangle Desktop, string Path, DateTime Modified,
        Position Position, uint Background);

    internal sealed record Pixels(byte[] Data, int Width, int Height)
    {
        internal unsafe WallpaperFrame Upload(ID3D11Device device, Rectangle bounds)
        {
            var description = new Texture2DDescription
            {
                Width = (uint)Width,
                Height = (uint)Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                BindFlags = BindFlags.ShaderResource,
                Usage = ResourceUsage.Immutable,
            };
            fixed (byte* data = Data)
                return new WallpaperFrame(new System.Windows.Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height),
                    device.CreateTexture2D(description, new SubresourceData(data, (uint)(Width * 4))));
        }
    }

    internal static Dictionary<Monitor, Pixels?> Read(HashSet<Monitor> uploaded, CancellationToken token)
    {
        var monitors = ReadMonitors();
        var result = new Dictionary<Monitor, Pixels?>();
        foreach (var monitor in monitors)
        {
            token.ThrowIfCancellationRequested();
            if (uploaded.Contains(monitor))
            {
                result.Add(monitor, null);
                continue;
            }
            try { result.Add(monitor, Render(monitor)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                ExternalException or OutOfMemoryException or FormatException or NotSupportedException)
            {
                Log.Warn("读取静态壁纸失败: " + ex.Message);
            }
        }
        return result;
    }

    private static IReadOnlyList<Monitor> ReadMonitors()
    {
        using var com = ShellItemInterop.EnsureComInitialized();
        var service = (IDesktopWallpaper)new DesktopWallpaperCom();
        try
        {
            Marshal.ThrowExceptionForHR(service.GetMonitorDevicePathCount(out var count));
            Marshal.ThrowExceptionForHR(service.GetPosition(out var position));
            Marshal.ThrowExceptionForHR(service.GetBackgroundColor(out var color));
            var monitors = new List<(Rectangle Bounds, string Path)>();
            for (uint index = 0; index < count; index++)
            {
                var id = TakeString(service.GetMonitorDevicePathAt(index, out var idPtr), idPtr);
                if (id.Length == 0 || service.GetMonitorRECT(id, out var rect) != 0) continue;
                var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
                if (bounds.Width <= 0 || bounds.Height <= 0) continue;
                var path = TakeString(service.GetWallpaper(id, out var pathPtr), pathPtr);
                monitors.Add((bounds, path));
            }
            if (monitors.Count == 0) return Array.Empty<Monitor>();
            var desktop = monitors.Select(monitor => monitor.Bounds).Aggregate(Rectangle.Union);
            return monitors.Select(monitor => new Monitor(monitor.Bounds, desktop, monitor.Path,
                monitor.Path.Length == 0 ? DateTime.MinValue : File.GetLastWriteTimeUtc(monitor.Path), position, color)).ToList();
        }
        finally { Marshal.ReleaseComObject(service); }
    }

    private static string TakeString(int hr, nint value)
    {
        try
        {
            Marshal.ThrowExceptionForHR(hr);
            return Marshal.PtrToStringUni(value) ?? string.Empty;
        }
        finally { Marshal.FreeCoTaskMem(value); }
    }

    internal static Pixels Render(Monitor monitor)
    {
        var color = Color.FromArgb((byte)monitor.Background, (byte)(monitor.Background >> 8), (byte)(monitor.Background >> 16));
        if (monitor.Path.Length == 0) return new Pixels([color.B, color.G, color.R, 255], 1, 1);

        using var stream = File.Open(monitor.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var image = Decode(stream);
        Orient(image);
        var bounds = monitor.Bounds;
        // Keep per-monitor textures within 4K on the longest side, using the full monitor for placement.
        var scale = Math.Min(1f, 4096f / Math.Max(bounds.Width, bounds.Height));
        var width = Math.Max(1, (int)Math.Round(bounds.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bounds.Height * scale));
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(color);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.ScaleTransform(width / (float)bounds.Width, height / (float)bounds.Height);
            if (monitor.Position == Position.Tile)
            {
                using var tiles = new TextureBrush(image, WrapMode.Tile);
                tiles.TranslateTransform(monitor.Desktop.Left - bounds.Left, monitor.Desktop.Top - bounds.Top);
                graphics.FillRectangle(tiles, 0, 0, bounds.Width, bounds.Height);
            }
            else
            {
                var layout = monitor.Position == Position.Span ? monitor.Desktop : bounds;
                var w = layout.Width / (float)image.Width;
                var h = layout.Height / (float)image.Height;
                var ratio = monitor.Position switch
                {
                    Position.Center => 1,
                    Position.Fit => Math.Min(w, h),
                    _ => Math.Max(w, h),
                };
                var imageWidth = monitor.Position == Position.Stretch ? layout.Width : image.Width * ratio;
                var imageHeight = monitor.Position == Position.Stretch ? layout.Height : image.Height * ratio;
                var destination = new RectangleF(layout.Left - bounds.Left + (layout.Width - imageWidth) / 2,
                    layout.Top - bounds.Top + (layout.Height - imageHeight) / 2, imageWidth, imageHeight);
                graphics.DrawImage(image, destination, new RectangleF(0, 0, image.Width, image.Height), GraphicsUnit.Pixel);
            }
        }

        var data = new byte[width * height * 4];
        var pixels = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, bitmap.PixelFormat);
        try
        {
            for (var row = 0; row < height; row++)
                Marshal.Copy(pixels.Scan0 + row * pixels.Stride, data, row * width * 4, width * 4);
        }
        finally { bitmap.UnlockBits(pixels); }
        return new Pixels(data, width, height);
    }

    private static Image Decode(Stream stream)
    {
        try { return Image.FromStream(stream, useEmbeddedColorManagement: true); }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException)
        {
            // WIC also recognizes formats supplied by Windows image extensions.
            stream.Position = 0;
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            var source = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var bitmap = new Bitmap(source.PixelWidth, source.PixelHeight, PixelFormat.Format32bppArgb);
            try
            {
                var bits = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, bitmap.PixelFormat);
                try
                {
                    source.CopyPixels(System.Windows.Int32Rect.Empty, bits.Scan0, bits.Stride * bitmap.Height, bits.Stride);
                }
                finally { bitmap.UnlockBits(bits); }
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }
    }

    private static void Orient(Image image)
    {
        const int orientationId = 0x0112;
        if (!image.PropertyIdList.Contains(orientationId) || image.GetPropertyItem(orientationId)?.Value is not { Length: > 0 } value)
            return;
        var rotation = value[0] switch
        {
            2 => RotateFlipType.RotateNoneFlipX,
            3 => RotateFlipType.Rotate180FlipNone,
            4 => RotateFlipType.Rotate180FlipX,
            5 => RotateFlipType.Rotate90FlipX,
            6 => RotateFlipType.Rotate90FlipNone,
            7 => RotateFlipType.Rotate270FlipX,
            8 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone,
        };
        if (rotation != RotateFlipType.RotateNoneFlipNone) image.RotateFlip(rotation);
    }

    [ComImport, Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD")]
    private class DesktopWallpaperCom;

    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        [PreserveSig] int SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.LPWStr)] string path);
        [PreserveSig] int GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string id, out nint path);
        [PreserveSig] int GetMonitorDevicePathAt(uint index, out nint id);
        [PreserveSig] int GetMonitorDevicePathCount(out uint count);
        [PreserveSig] int GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string id, out RECT rect);
        [PreserveSig] int SetBackgroundColor(uint color);
        [PreserveSig] int GetBackgroundColor(out uint color);
        [PreserveSig] int SetPosition(Position position);
        [PreserveSig] int GetPosition(out Position position);
    }
}
