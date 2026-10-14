using System.Windows;
using System.Windows.Controls;
using DeskBox.Services;

namespace DeskBox.Views;

/// <summary>Centers complete columns, with equal outer padding and aligned partial rows.</summary>
public sealed class CenteredIconPanel : Panel
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
            : Math.Min(InternalChildren.Count, CellGrid.Count(availableSize.Width, _cellWidth));
        return new Size(double.IsInfinity(availableSize.Width) ? columns * _cellWidth : availableSize.Width,
            CellGrid.CellsFor(InternalChildren.Count, columns) * _cellHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count == 0 || _cellWidth <= 0) return finalSize;
        var columns = Math.Min(InternalChildren.Count, CellGrid.Count(finalSize.Width, _cellWidth));
        var left = Math.Max(0, (finalSize.Width - columns * _cellWidth) / 2);
        for (var i = 0; i < InternalChildren.Count; i++)
            InternalChildren[i].Arrange(new Rect(left + i % columns * _cellWidth,
                i / columns * _cellHeight, _cellWidth, _cellHeight));
        return finalSize;
    }
}
