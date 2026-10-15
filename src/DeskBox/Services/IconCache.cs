using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeskBox.Services;

/// <summary>
/// 图标缓存门面：对外签名保持不变（BoxWindow / BoxItem 依赖），
/// 真正的取图管线（常驻 STA 工作线程 + 队列 + 磁盘 PNG 缓存）在 <see cref="IconLoader"/>。
/// 用系统的 IShellItemImageFactory 取高清图标，.lnk 会自动解析成目标图标。
/// </summary>
public static class IconCache
{
    private static readonly ConcurrentDictionary<int, BitmapSource> Placeholders = new();

    /// <summary>清空内存缓存（磁盘缓存保留，下次取图会走「磁盘命中」秒出）。</summary>
    public static void Clear() => IconLoader.ClearMemory();

    /// <summary>删除所有以 path + "|" 开头的内存缓存项。</summary>
    public static void Invalidate(string path) => IconLoader.InvalidatePrefix(path + "|");

    /// <summary>异步取图：内存 → 磁盘缓存 → shell；失败返回 null，不抛异常。</summary>
    public static Task<BitmapSource?> GetAsync(string path, int size) =>
        IconLoader.LoadAsync(path, size, allowShell: true);

    /// <summary>同步取图（兼容旧调用方）；shell 调用发生在 STA 工作线程上，不在调用线程。</summary>
    public static BitmapSource? Get(string path, int size) => IconLoader.LoadBlocking(path, size);

    /// <summary>预热：后台把磁盘缓存解码进内存，不碰 shell，立即返回。</summary>
    public static void Prewarm(IEnumerable<string> paths, int size) => IconLoader.Prewarm(paths, size);

    /// <summary>只查内存缓存，立即返回，绝不碰 shell。</summary>
    public static BitmapSource? TryGetFast(string path, int size) => IconLoader.TryGet(path, size);

    /// <summary>例："内存命中 12 / 磁盘命中 40 / shell 提取 8"。</summary>
    public static string StatsSummary() => IconLoader.StatsSummary();

    public static BitmapSource Placeholder(int size) =>
        Placeholders.GetOrAdd(size, CreatePlaceholder);

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
