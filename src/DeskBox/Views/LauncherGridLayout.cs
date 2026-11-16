using System.Windows;
using DeskBox.Services;

namespace DeskBox.Views;

internal readonly record struct LauncherGridLayout(int IconSize, int GroupColumns, int Gap)
{
    internal const int CardPadding = 12;
    internal const int HeadingHeight = 32;
    internal const int HoverInset = 4;
    private const int MinimumIconSize = 32;
    private const int MaximumGroupColumns = 4;

    internal double TileWidth => Math.Max(76, IconSize + 20);
    internal double TileHeight => IconSize + 40 + 2 * HoverInset;

    internal int TileColumns(double cardWidth) =>
        Math.Max(1, (int)((cardWidth - 2 * CardPadding) / TileWidth));

    internal double CardWidth(double width, int columns) => (width - (columns - 1) * Gap) / columns;

    internal static Size MeasureViewport(Size available, IReadOnlyList<LaunchSection> groups, int preferredIconSize, int gap)
    {
        var minWidth = Math.Min(380, available.Width);
        var minHeight = Math.Min(72, available.Height);
        var widestLayout = Fit(available, groups, preferredIconSize, gap);
        if (widestLayout.TotalHeight(available.Width, groups) > available.Height) return available;
        var best = new Size(available.Width, minHeight);
        var bestScore = double.PositiveInfinity;
        var bestWidthDeficit = int.MaxValue;

        // Rows change height only when a card gains another tile column.
        var widths = new SortedSet<double> { minWidth, available.Width };
        var cardCount = groups.Count(section => !IsEmptyUngrouped(section));
        for (var cards = 1; cards <= Math.Min(MaximumGroupColumns, cardCount); cards++)
        {
            for (var tiles = 1; ; tiles++)
            {
                var cardWidth = Math.Max(180, 2 * CardPadding + tiles * widestLayout.TileWidth) + 1;
                var width = cards * cardWidth + (cards - 1) * gap;
                if (width > available.Width) break;
                if (width >= minWidth) widths.Add(width);
            }
        }

        foreach (var width in widths)
        {
            var layout = Fit(new Size(width, available.Height), groups, widestLayout.IconSize, gap);
            var (contentHeight, unusedHeight, widthDeficit) = layout.MeasureGroups(width, groups);
            if (layout.IconSize != widestLayout.IconSize || contentHeight > available.Height) continue;
            var height = Math.Max(contentHeight, minHeight);
            var score = width * (height + unusedHeight) + 0.25 * Math.Pow(width - 1.55 * height, 2);
            if (widthDeficit > bestWidthDeficit || (widthDeficit == bestWidthDeficit && score >= bestScore)) continue;
            best = new Size(width, height);
            bestScore = score;
            bestWidthDeficit = widthDeficit;
        }
        return best;
    }

    internal static LauncherGridLayout Fit(Size viewport, IReadOnlyList<LaunchSection> groups, int preferredIconSize, int gap)
    {
        var best = new LauncherGridLayout(preferredIconSize, 1, gap);
        var bestScore = (Overflow: double.PositiveInfinity, WidthDeficit: int.MaxValue,
            Height: double.PositiveInfinity, UnusedHeight: double.PositiveInfinity);
        var mostColumns = Math.Clamp((int)((viewport.Width + gap) / (180 + gap)), 1, MaximumGroupColumns);
        mostColumns = Math.Min(mostColumns, Math.Max(1, groups.Count(section => !IsEmptyUngrouped(section))));
        for (var icon = preferredIconSize; icon >= MinimumIconSize; icon--)
        {
            for (var columns = mostColumns; columns >= 1; columns--)
            {
                var layout = new LauncherGridLayout(icon, columns, gap);
                var (height, unusedHeight, widthDeficit) = layout.MeasureGroups(viewport.Width, groups);
                var score = (Math.Max(0, height - viewport.Height), widthDeficit, height, unusedHeight);
                if (score.CompareTo(bestScore) >= 0) continue;
                best = layout;
                bestScore = score;
            }
            if (bestScore.Overflow == 0) break;
        }
        return best;
    }

    internal double TotalHeight(double width, IReadOnlyList<LaunchSection> groups) => MeasureGroups(width, groups).Height;

    private (double Height, double UnusedHeight, int WidthDeficit) MeasureGroups(double width, IReadOnlyList<LaunchSection> groups)
    {
        Span<double> heights = stackalloc double[GroupColumns];
        heights.Clear();
        var tileColumns = TileColumns(CardWidth(width, GroupColumns));
        var index = 0;
        var widthDeficit = 0;
        var emptyUngrouped = false;
        foreach (var group in groups)
        {
            if (IsEmptyUngrouped(group))
            {
                emptyUngrouped = true;
                continue;
            }
            var column = index++ % GroupColumns;
            if (heights[column] > 0) heights[column] += Gap;
            var count = group.Items.Count;
            heights[column] += CardHeight(count, tileColumns);
            // To have fewer rows than columns, c columns must hold all items in c - 1 rows.
            var wideColumns = Math.Min(count, (int)Math.Ceiling((1 + Math.Sqrt(1 + 4.0 * count)) / 2));
            widthDeficit += Math.Max(0, wideColumns - tileColumns);
        }
        var height = 0.0;
        var total = 0.0;
        foreach (var column in heights)
        {
            height = Math.Max(height, column);
            total += column;
        }
        var unusedHeight = height - total / GroupColumns;
        return (height + (emptyUngrouped ? EmptyUngroupedHeight + (index > 0 ? Gap : 0) : 0), unusedHeight, widthDeficit);
    }

    internal const int EmptyUngroupedHeight = 44;

    internal static bool IsEmptyUngrouped(LaunchSection section) =>
        section.Group is null && section.Name == "未分组" && section.Items.Count == 0;

    private double CardHeight(int items, int columns) => 2 * CardPadding + HeadingHeight +
        (items == 0 ? 24 : Math.Ceiling(items / (double)columns) * TileHeight);
}
