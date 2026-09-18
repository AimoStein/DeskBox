using System.Windows;

namespace JianZhuo.Services;

/// <summary>
/// 「一键整理盒子」的纯计算：把桌面上的盒子排整齐。
/// 不碰窗口，方便被自检直接调用。
/// </summary>
public static class BoxArranger
{
    /// <summary>
    /// 按盒子现在的大致位置（先上后左）排成整齐的行：
    /// 贴着可视区顶部、整体水平居中（屏幕上方正中），一行摆不下就换行；
    /// 行内顶部对齐、左右间距一致，盒子自身的尺寸不变。
    /// </summary>
    public static List<BoxRect> Arrange(IReadOnlyList<BoxRect> boxes, Rect area, double gap = BoxLayout.SnapGap)
    {
        var result = new List<BoxRect>(boxes.Count);

        if (boxes.Count == 0)
        {
            return result;
        }

        var ordered = SortInReadingOrder(boxes);

        // 先按宽度分行：左右各留 gap 之内放得下就继续加，放不下就另起一行
        var usable = Math.Max(0, area.Width - gap * 2);
        var rows = new List<List<BoxRect>>();

        foreach (var box in ordered)
        {
            var row = rows.Count > 0 ? rows[^1] : null;

            if (row is null)
            {
                rows.Add(new List<BoxRect> { box });
                continue;
            }

            var width = row.Sum(b => b.Width) + gap * row.Count;
            if (width + box.Width <= usable)
            {
                row.Add(box);
            }
            else
            {
                rows.Add(new List<BoxRect> { box });
            }
        }

        // 每一行水平居中摆放，行与行之间留 gap，行内顶部对齐
        var y = area.Top + gap;

        foreach (var row in rows)
        {
            var total = row.Sum(b => b.Width) + gap * (row.Count - 1);
            var x = total >= area.Width ? area.Left + gap : area.Left + (area.Width - total) / 2;
            var rowHeight = 0.0;

            foreach (var box in row)
            {
                result.Add(box.WithPosition(x, y));
                x += box.Width + gap;
                rowHeight = Math.Max(rowHeight, box.Height);
            }

            y += rowHeight + gap;
        }

        return result;
    }

    /// <summary>
    /// 阅读顺序：纵向中线接近的算同一行，行内按左边缘排，行与行按上边缘排。
    /// 这样「一键整理」尊重用户原来把哪些盒子摆在一排的想法，只是把它们对齐。
    /// </summary>
    private static List<BoxRect> SortInReadingOrder(IReadOnlyList<BoxRect> boxes)
    {
        var band = boxes.Average(b => b.Height) / 2;

        var rows = new List<List<BoxRect>>();
        foreach (var box in boxes.OrderBy(b => b.CenterY))
        {
            var row = rows.Count > 0 ? rows[^1] : null;

            if (row is not null && Math.Abs(box.CenterY - row[0].CenterY) <= band)
            {
                row.Add(box);
            }
            else
            {
                rows.Add(new List<BoxRect> { box });
            }
        }

        var ordered = new List<BoxRect>(boxes.Count);
        foreach (var row in rows)
        {
            ordered.AddRange(row.OrderBy(b => b.X));
        }

        return ordered;
    }
}
