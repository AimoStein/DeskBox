using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JianZhuo.Interop;

namespace JianZhuo.Services;

/// <summary>用系统的 IShellItemImageFactory 取高清图标，.lnk 会自动解析成目标图标。</summary>
public static class IconCache
{
    private static readonly ConcurrentDictionary<string, BitmapSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<int, BitmapSource> Placeholders = new();

    private static readonly Guid IidShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    public static void Clear() => Cache.Clear();

    public static void Invalidate(string path)
    {
        foreach (var key in Cache.Keys)
        {
            if (key.StartsWith(path + "|", StringComparison.OrdinalIgnoreCase))
            {
                Cache.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// 放到 STA 线程上取图标：Shell 的 COM 接口在 STA 下最稳，BitmapSource 冻结后可跨线程使用。
    /// </summary>
    public static Task<BitmapSource?> GetAsync(string path, int size)
    {
        var completion = new TaskCompletionSource<BitmapSource?>();

        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(Get(path, size));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "JianZhuo.Icon",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return completion.Task;
    }

    public static BitmapSource? Get(string path, int size)
    {
        var key = BuildKey(path, size);
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var image = Load(path, size);
        if (image is not null)
        {
            Cache[key] = image;
        }

        return image;
    }

    public static BitmapSource Placeholder(int size) =>
        Placeholders.GetOrAdd(size, CreatePlaceholder);

    private static string BuildKey(string path, int size)
    {
        long ticks = 0;
        try
        {
            if (File.Exists(path))
            {
                ticks = File.GetLastWriteTimeUtc(path).Ticks;
            }
            else if (Directory.Exists(path))
            {
                ticks = Directory.GetLastWriteTimeUtc(path).Ticks;
            }
        }
        catch
        {
            // 拿不到时间戳就用 0
        }

        return $"{path}|{size}|{ticks}";
    }

    private static BitmapSource? Load(string path, int size)
    {
        // Shell 生成图标/缩略图有时会返回 E_PENDING，等一会儿再试
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var image = TryLoadOnce(path, size, out var pending);
            if (image is not null)
            {
                return image;
            }

            if (!pending)
            {
                return null;
            }

            Thread.Sleep(150 * attempt);
        }

        Log.Warn($"图标始终未就绪 {path}");
        return null;
    }

    private static BitmapSource? TryLoadOnce(string path, int size, out bool pending)
    {
        pending = false;

        try
        {
            if (!FsUtil.PathExists(path))
            {
                return null;
            }

            NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, IidShellItemImageFactory, out var factory);
            try
            {
                var hr = factory.GetImage(
                    new SIZE(size, size),
                    SIIGBF.ICONONLY | SIIGBF.BIGGERSIZEOK,
                    out var hBitmap);

                // 0x8000000A = E_PENDING：Shell 还在准备图形资源
                pending = hr == unchecked((int)0x8000000A);

                if (hr != 0 || hBitmap == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap,
                        IntPtr.Zero,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }
                finally
                {
                    NativeMethods.DeleteObject(hBitmap);
                }
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"取图标失败 {path}: {ex.Message}");
            return null;
        }
    }

    private static BitmapSource CreatePlaceholder(int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var rect = new Rect(1, 1, size - 2, size - 2);
            var brush = new SolidColorBrush(Color.FromArgb(38, 128, 128, 138));
            dc.DrawRoundedRectangle(brush, null, rect, size * 0.18, size * 0.18);

            var glyph = new StreamGeometry();
            using (var ctx = glyph.Open())
            {
                var left = size * 0.32;
                var right = size * 0.68;
                var top = size * 0.24;
                var bottom = size * 0.76;
                ctx.BeginFigure(new Point(left, top), true, true);
                ctx.LineTo(new Point(right, top), true, false);
                ctx.LineTo(new Point(right, bottom), true, false);
                ctx.LineTo(new Point(left, bottom), true, false);
            }

            glyph.Freeze();
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(120, 128, 128, 138)), Math.Max(1.2, size * 0.035));
            dc.DrawGeometry(null, pen, glyph);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
