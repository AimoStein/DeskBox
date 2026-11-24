using System.Windows;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Views;

internal readonly record struct LauncherGridLayout(int IconSize, int Gap)
{
    internal const int CardPadding = 12;
    internal const int HeadingHeight = 32;
    internal const int HoverInset = 4;
    internal const int EmptyUngroupedHeight = 44;
    private const double WindowHorizontalSpace = 48;
    private const double WindowVerticalSpace = 92;
    private const int PreferredMinimumWidth = 640;

    internal double TileWidth => Math.Max(76, IconSize + 20);
    internal double TileHeight => IconSize + 40 + 2 * HoverInset;

    internal int TileColumns(double cardWidth) => Math.Max(1, (int)((cardWidth - 2 * CardPadding) / TileWidth));

    internal static int MaxGroupColumns(int items) => Math.Clamp(items, 1, BoxConfig.MaxLauncherGridDimension);

    internal static int GroupColumns(LaunchSection section) =>
        Math.Clamp(section.Group!.LauncherColumns, 1, MaxGroupColumns(section.Items.Count));

    internal Size GroupSize(LaunchSection section) => GroupSize(section.Items.Count, GroupColumns(section));

    internal Size GroupSize(int items, int columns)
    {
        var rows = RowsFor(items, columns);
        return new Size(Math.Max(120, 2 * CardPadding + columns * TileWidth),
            2 * CardPadding + HeadingHeight + (rows > 0 ? rows * TileHeight : 24));
    }

    internal int ResizeGroup(LaunchSection section, Vector delta)
    {
        var items = section.Items.Count;
        var columns = GroupColumns(section);
        var dx = delta.X / TileWidth;
        var dy = delta.Y / TileHeight;
        var desiredColumns = Math.Clamp(columns + dx, 1, MaxGroupColumns(items));
        var desiredRows = Math.Clamp(RowsFor(items, columns) + dy, 0, items);

        // Along an axis, match that dimension first. On a diagonal, choose the nearest valid shape.
        (double Primary, double Secondary) Distance(int candidate)
        {
            var x = Math.Abs(candidate - desiredColumns);
            var y = Math.Abs(RowsFor(items, candidate) - desiredRows);
            if (Math.Abs(dx) < 0.5) return (y, x);
            if (Math.Abs(dy) < 0.5) return (x, y);
            return (x * x + y * y, x);
        }

        var best = columns;
        var bestDistance = Distance(best);
        for (var candidate = 1; candidate <= MaxGroupColumns(items); candidate++)
        {
            var distance = Distance(candidate);
            if (distance.CompareTo(bestDistance) >= 0) continue;
            best = candidate;
            bestDistance = distance;
        }
        return best;
    }

    internal Size MeasureWindow(Size available, LauncherGroupBoard board, LaunchSection? ungrouped)
    {
        var width = Math.Min(available.Width, Math.Max(PreferredMinimumWidth, board.Width + WindowHorizontalSpace));
        var viewportWidth = Math.Max(1, width - WindowHorizontalSpace);
        var overview = OverviewSize(viewportWidth, board, ungrouped);
        var height = WindowVerticalSpace + overview.Height * (viewportWidth / overview.Width);
        return new Size(width, Math.Min(available.Height, Math.Ceiling(height)));
    }

    internal Size OverviewSize(double viewportWidth, LauncherGroupBoard board, LaunchSection? ungrouped)
    {
        var width = Math.Max(viewportWidth, board.Width);
        var height = board.Height;
        if (ungrouped is not null)
        {
            if (board.Cards.Count > 0) height += Gap;
            height += ungrouped.Items.Count == 0 ? EmptyUngroupedHeight :
                2 * CardPadding + HeadingHeight + RowsFor(ungrouped.Items.Count, TileColumns(width)) * TileHeight;
        }
        return new Size(width, height);
    }

    internal static int RowsFor(int items, int columns) => (int)Math.Ceiling(items / (double)columns);
}

internal sealed class LauncherGroupBoard
{
    internal readonly record struct Card(LaunchSection Section, Rect Bounds);
    private readonly int _gap;
    private readonly int _firstRowCount;

    internal LauncherGroupBoard(IEnumerable<LaunchSection> groups, LauncherGridLayout layout)
        : this(groups.Where(section => section.Group is not null).OrderBy(section => Order(section.Group!))
            .Select(section => new Card(section, new Rect(new Point(), layout.GroupSize(section)))).ToArray(), layout.Gap)
    {
    }

    private LauncherGroupBoard(Card[] cards, int gap)
    {
        _gap = gap;
        _firstRowCount = (cards.Length + 1) / 2;
        Pack(cards);
        Cards = cards;
        Width = cards.Length == 0 ? 0 : cards.Max(card => card.Bounds.Right);
        Height = cards.Length == 0 ? 0 : cards.Max(card => card.Bounds.Bottom);
    }

    internal IReadOnlyList<Card> Cards { get; }
    internal double Width { get; }
    internal double Height { get; }

    internal LauncherGroupBoard ResizeCard(int slot, Size size)
    {
        var cards = Cards.ToArray();
        cards[slot] = cards[slot] with { Bounds = new Rect(new Point(), size) };
        return new LauncherGroupBoard(cards, _gap);
    }

    internal (int Slot, Rect Bounds)? Snap(Point point, int sourceSlot)
    {
        if (sourceSlot < 0 || sourceSlot >= Cards.Count ||
            point.X < 0 || point.X > Width || point.Y < 0 || point.Y > Height) return null;
        var secondRow = _firstRowCount < Cards.Count && point.Y >= Cards[_firstRowCount].Bounds.Top - _gap / 2.0;
        var target = secondRow ? _firstRowCount : 0;
        var last = (secondRow ? Cards.Count : _firstRowCount) - 1;
        while (target < last && point.X > Cards[target].Bounds.Right + _gap / 2.0) target++;
        if (target == sourceSlot) return (target, Cards[target].Bounds);

        var preview = Cards.ToArray();
        (preview[sourceSlot], preview[target]) = (preview[target], preview[sourceSlot]);
        Pack(preview);
        return (target, preview[target].Bounds);
    }

    internal int? Neighbor(int slot, int dx, int dy)
    {
        var row = slot < _firstRowCount ? 0 : 1;
        var column = slot - row * _firstRowCount + dx;
        row += dy;
        var target = row * _firstRowCount + column;
        return row is 0 or 1 && column >= 0 && column < _firstRowCount && target < Cards.Count ? target : null;
    }

    private void Pack(Card[] cards)
    {
        var left = 0.0;
        var top = 0.0;
        var rowHeight = 0.0;
        for (var slot = 0; slot < cards.Length; slot++)
        {
            if (slot == _firstRowCount)
            {
                left = 0;
                top = rowHeight + _gap;
            }
            var size = cards[slot].Bounds.Size;
            cards[slot] = cards[slot] with { Bounds = new Rect(new Point(left, top), size) };
            left += size.Width + _gap;
            rowHeight = Math.Max(rowHeight, size.Height);
        }
    }

    internal static bool AssignSlots(IEnumerable<BoxConfig> groups)
    {
        // Preserve the reading order from earlier layouts while compacting slots.
        var ordered = groups.OrderBy(Order).ToArray();
        var changed = false;
        for (var slot = 0; slot < ordered.Length; slot++)
        {
            if (ordered[slot].LauncherSlot == slot) continue;
            ordered[slot].LauncherSlot = slot;
            changed = true;
        }
        return changed;
    }

    private static long Order(BoxConfig group) => group.LauncherSlot is >= 0 ? group.LauncherSlot.Value : long.MaxValue;

    internal static void MoveTo(IEnumerable<BoxConfig> groups, BoxConfig group, int slot)
    {
        if (groups.FirstOrDefault(other => other.LauncherSlot == slot) is not { } occupant) return;
        occupant.LauncherSlot = group.LauncherSlot;
        group.LauncherSlot = slot;
    }
}
