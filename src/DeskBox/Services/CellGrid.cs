using System.Windows;

namespace DeskBox.Services;

/// <summary>
/// 盒子缩放的「顿挫」计算：宽高只能是图标格子的整数倍。
/// 纯计算，不碰窗口，方便被自检直接调用。
/// </summary>
public static class CellGrid
{
    /// <summary>一段长度里能放下几个格子（容差半格，至少 min 个）。</summary>
    public static int Count(double available, double cell, int min = 1)
    {
        if (cell <= 1 || double.IsNaN(available) || available <= 0)
        {
            return Math.Max(1, min);
        }

        return Math.Max(Math.Max(1, min), (int)Math.Floor((available + 0.5) / cell));
    }

    /// <summary>每格放 perCell 个时，装下 count 个条目要几格（按行理解）。</summary>
    public static int CellsFor(int count, int perCell) =>
        Math.Max(1, (int)Math.Ceiling(Math.Max(1, count) / (double)Math.Max(1, perCell)));

    /// <summary>
    /// 把自由拖动出来的框吸附成格子整数倍。
    /// 拖左右：列数按格子跳，同时按条目总数把高度收成刚好装下的行数（3×4 → 4×3）。
    /// 拖上下：行数按格子跳，宽度不变。拖左上角时另一条边固定。
    /// </summary>
    public static Rect SnapResize(
        Rect origin,
        double left,
        double top,
        double width,
        double height,
        string direction,
        double cellWidth,
        double cellHeight,
        double chromeWidth,
        double chromeHeight,
        int columnsNow,
        int rowsNow,
        int count,
        bool snapColumns = true)
    {
        if (cellWidth <= 1 || cellHeight <= 1)
        {
            return new Rect(left, top, width, height);
        }

        var total = Math.Max(1, count);
        var columns = Math.Max(1, columnsNow);
        var rows = Math.Max(1, rowsNow);

        var horizontal = snapColumns && (direction.Contains('L') || direction.Contains('R'));
        var vertical = direction.Contains('T') || direction.Contains('B');

        if (horizontal)
        {
            columns = Math.Max(1, (int)Math.Round((width - chromeWidth) / cellWidth));
            width = chromeWidth + columns * cellWidth;

            // 列数一变，内容跟着重排：高度自动收放成刚好装下所有格子的行数
            rows = Math.Max(1, (int)Math.Ceiling(total / (double)columns));
            height = chromeHeight + rows * cellHeight;
        }
        else if (vertical)
        {
            rows = Math.Max(1, (int)Math.Round((height - chromeHeight) / cellHeight));
            height = chromeHeight + rows * cellHeight;
        }

        if (direction.Contains('L'))
        {
            left = origin.Right - width;
        }

        if (direction.Contains('T'))
        {
            top = origin.Bottom - height;
        }

        return new Rect(left, top, width, height);
    }
}
