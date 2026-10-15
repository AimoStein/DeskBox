using System.Windows;
using System.Windows.Controls;
using DeskBox.Services;

namespace DeskBox.Views;

/// <summary>Uses fixed outer insets and spreads spare width between columns; partial rows stay aligned.</summary>
public sealed class IconGridPanel : Panel
{
    private double _cellWidth, _cellHeight;

    protected override Size MeasureOverride(Size availableSize)
    {
        _cellWidth = _cellHeight = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _cellWidth = Math.Max(_cellWidth, child.DesiredSize.Width);
            _cellHeight = Math.Max(_cellHeight, child.DesiredSize.Height);
        }
        if (InternalChildren.Count == 0) return new Size(0, 0);
        var columns = double.IsInfinity(availableSize.Width) ? InternalChildren.Count
            : CellGrid.Count(availableSize.Width, _cellWidth);
        return new Size(double.IsInfinity(availableSize.Width) ? columns * _cellWidth : availableSize.Width,
            CellGrid.CellsFor(InternalChildren.Count, columns) * _cellHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count == 0 || _cellWidth <= 0) return finalSize;
        // Use the box's column capacity even when there are fewer items, so adding/removing
        // an item cannot shift the entire group away from the edge.
        var columns = CellGrid.Count(finalSize.Width, _cellWidth);
        var gap = columns > 1 ? Math.Max(0, (finalSize.Width - columns * _cellWidth) / (columns - 1)) : 0;
        for (var i = 0; i < InternalChildren.Count; i++)
            InternalChildren[i].Arrange(new Rect(i % columns * (_cellWidth + gap),
                i / columns * _cellHeight, _cellWidth, _cellHeight));
        return finalSize;
    }
}
