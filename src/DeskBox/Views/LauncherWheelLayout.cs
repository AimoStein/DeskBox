using System.Windows;
using System.Windows.Media;

namespace DeskBox.Views;

internal static class LauncherWheelLayout
{
    private const double HalfGap = 4;
    internal const double CompactIconInset = 2; // Button border and vertical padding.
    internal const double CompactHorizontalInset = 3;
    internal const double CompactLabelGap = 1;
    internal const double CompactLabelHeight = 14;
    internal const double CompactLabelMaxHeight = CompactLabelHeight * 2;

    internal sealed record Sector(double Span, Geometry Shape, Geometry Outline, Rect Title, IReadOnlyList<Point> Slots);
    internal sealed record Layout(Size CanvasSize, Point Center, double Radius, double FadeStart, double HubRadius, double Pitch,
        Size TileSize, double IconAnchor, IReadOnlyList<Sector> Sectors);

    internal static Size CompactTileSize(int iconSize) => new(Math.Max(80, iconSize + 8),
        iconSize + 2 * CompactIconInset + CompactLabelGap + CompactLabelMaxHeight + 1);

    internal static double IconHoverGrowth(int iconSize) => Math.Min(iconSize * 0.08, 5);

    internal static Geometry CompactTileClip(int iconSize, Size label)
    {
        var footprint = TileFootprint.Create(iconSize, label).At(new Vector(CompactTileSize(iconSize).Width / 2,
            CompactIconInset + iconSize / 2.0));
        var clip = new GeometryGroup { FillRule = FillRule.Nonzero };
        clip.Children.Add(new RectangleGeometry(footprint.Icon));
        clip.Children.Add(new RectangleGeometry(footprint.Label));
        clip.Freeze();
        return clip;
    }

    internal static Layout Arrange(Size size, IReadOnlyList<int> counts, int iconSize, int layerGap,
        Func<int, bool, Size>? measureTitle = null, IReadOnlyList<Size[]>? labels = null)
    {
        var halfWidth = size.Width / 2;
        var halfHeight = size.Height / 2;
        var radius = Math.Min(halfWidth, halfHeight) - 2;
        if (counts.Count == 0)
            return new Layout(size, new Point(halfWidth, halfHeight), radius, 0, HubRadius(radius),
                0, Size.Empty, 0, Array.Empty<Sector>());
        var spans = Angles(counts);
        var angle = -Math.PI / 2;
        var divisions = spans.Select((span, index) =>
        {
            var start = angle;
            angle += span;
            var middle = start + span / 2;
            var direction = new Vector(Math.Cos(middle), Math.Sin(middle));
            var verticalTitle = Math.Abs(direction.Y) <= Math.Sin(Math.PI / 8);
            var title = measureTitle?.Invoke(index, verticalTitle) ?? (verticalTitle ? new Size(26, 48) : new Size(56, 24));
            return (Start: start, Span: span, Direction: direction, Title: title);
        }).ToArray();
        var hub = HubRadius(radius);
        var inner = hub + 9;
        var titleFits = divisions.Select(division => FitTitle(inner, division.Start, division.Span,
            division.Direction, division.Title)).ToArray();
        var titleRadius = titleFits.Max(fit => fit.MinimumRadius);
        var titleBounds = divisions.Select((division, i) =>
        {
            var title = titleFits[i].Size;
            var origin = division.Direction * titleRadius - new Vector(title.Width / 2, title.Height / 2);
            return new Rect((Point)origin, title);
        }).ToArray();
        var titleClearances = titleBounds.Select(title => Rect.Inflate(title, 3, 3)).ToArray();
        var tile = CompactTileSize(iconSize);
        var iconAnchor = CompactIconInset + iconSize / 2.0;
        var pitch = Math.Max(tile.Width, tile.Height) + layerGap;
        var padding = new Vector(tile.Width / 2, tile.Height - iconAnchor).Length + 2;
        var footprints = counts.Select((count, i) => Enumerable.Range(0, count).Select(j =>
            TileFootprint.Create(iconSize, labels?[i][j] ?? new Size(48, CompactLabelHeight))).ToArray()).ToArray();
        var slots = Pack(FirstRing(fitAllLabels: false), pitch);
        if (slots.Where((row, i) => row.Count < counts[i]).Any())
        {
            // Ensure any item can fit alone on a ring, clear of its neighbors and both sector edges.
            pitch = Math.Max(pitch, new Vector(tile.Width + 2, tile.Height + 2).Length + 2);
            slots = Pack(FirstRing(fitAllLabels: true), pitch);
        }

        var outermostTile = slots.SelectMany((row, i) => row.Select((offset, j) =>
            footprints[i][j].At(offset).OuterRadius)).DefaultIfEmpty(inner).Max();
        var outermostTitle = titleBounds.Max(FarthestCorner);
        var fadeStart = Math.Max(inner + pitch, Math.Max(outermostTile, outermostTitle) + 2);
        var minFade = Math.Clamp(pitch * 0.20, 16, 28);
        var outer = Math.Clamp(radius, fadeStart + minFade, fadeStart + Math.Clamp(pitch * 1.1, 54, 94));
        halfWidth = Math.Max(halfWidth, outer + 2);
        halfHeight = Math.Max(halfHeight, outer + 2);
        var canvasSize = new Size(Math.Ceiling(2 * halfWidth), Math.Ceiling(2 * halfHeight));
        var center = new Point(canvasSize.Width / 2, canvasSize.Height / 2);
        var sectors = divisions.Select((division, i) =>
        {
            var (start, span, _, _) = division;
            var (shape, outline) = BuildSector(center, outer, inner, start, span);
            var title = Rect.Offset(titleBounds[i], (Vector)center);
            return new Sector(span, shape, outline, title, slots[i].Select(offset => center + offset).ToArray());
        }).ToArray();
        return new Layout(canvasSize, center, outer, fadeStart, hub, pitch, tile, iconAnchor, sectors);

        IReadOnlyList<Vector>[] Pack(double firstRing, double spacing) => divisions.Select((division, i) =>
            RingSlots(firstRing, spacing, division.Start, division.Span, SectorHalfGap(inner, division.Span) + 2,
                footprints[i], iconSize + IconHoverGrowth(iconSize) + 2, titleClearances[i])).ToArray();

        double FirstRing(bool fitAllLabels)
        {
            var firstRing = inner + padding;
            for (var i = 0; i < counts.Count; i++)
            {
                if (counts[i] == 0) continue;
                var (start, span, direction, _) = divisions[i];
                var candidates = footprints[i].Take(fitAllLabels ? counts[i] : 1);
                firstRing = Math.Max(firstRing, candidates.Max(footprint =>
                    footprint.ClearTitle(titleClearances[i], direction)) + 1);
                if (span >= Math.Tau - 0.001) continue;
                var left = Normal(start, 1);
                var right = Normal(start + span, -1);
                var clearance = candidates.Max(footprint => Math.Max(footprint.Clearance(left), footprint.Clearance(right)));
                var gap = SectorHalfGap(inner, span) + 2;
                firstRing = Math.Max(firstRing, (clearance + gap) / Math.Sin(span / 2) + 1);
            }
            return firstRing;
        }
    }

    private static double HubRadius(double radius) => Math.Clamp(radius * 0.074, 20, 32);

    private static (Size Size, double MinimumRadius) FitTitle(double inner, double start, double span, Vector direction, Size size)
    {
        var fullCircle = span >= Math.Tau - 0.001;
        var sine = Math.Sin(span / 2);
        var gap = SectorHalfGap(inner, span) + 3;
        var sides = new[] { Normal(start, 1), Normal(start + span, -1) };
        if (!fullCircle)
        {
            // Shorten wide badges where the sector is tight near the hub.
            var available = (inner + 58) * sine - gap;
            var maxWidth = sides.Where(side => Math.Abs(side.X) > 0.001)
                .Select(side => 2 * (available - Math.Abs(side.Y) * size.Height / 2) / Math.Abs(side.X))
                .DefaultIfEmpty(size.Width).Min();
            size.Width = Math.Clamp(maxWidth, Math.Min(36, size.Width), size.Width);
        }

        double HalfExtent(Vector axis) => (Math.Abs(axis.X) * size.Width + Math.Abs(axis.Y) * size.Height) / 2;
        var radius = inner + HalfExtent(direction) + 8;
        if (!fullCircle) radius = Math.Max(radius, (gap + sides.Max(HalfExtent)) / sine + 1);
        return (size, radius);
    }

    private static double[] Angles(IReadOnlyList<int> counts)
    {
        // Reserve most of the circle for usable inner rows, then give larger groups more space.
        const double weightedShare = 0.25;
        var baseSpan = Math.Tau * (1 - weightedShare) / counts.Count;
        var total = counts.Sum(count => count + 1.0);
        return counts.Select(count => baseSpan + Math.Tau * weightedShare * (count + 1.0) / total).ToArray();
    }

    private static double SectorHalfGap(double inner, double span) =>
        Math.Min(HalfGap, inner * Math.Sin(span / 2) * 0.50);

    private static (Geometry Shape, Geometry Outline) BuildSector(Point center, double outer, double inner, double start, double span)
    {
        if (span >= Math.Tau - 0.001)
        {
            var ring = new GeometryGroup { FillRule = FillRule.EvenOdd };
            ring.Children.Add(new EllipseGeometry(center, outer, outer));
            var innerEdge = new EllipseGeometry(center, inner, inner);
            innerEdge.Freeze();
            ring.Children.Add(innerEdge);
            ring.Freeze();
            return (ring, innerEdge);
        }

        var halfGap = SectorHalfGap(inner, span);
        var corner = Math.Min(12, Math.Min((outer - inner) / 4, inner * Math.Sin(span / 2) * 0.36));
        var end = start + span;
        var innerInset = Math.Asin(halfGap / inner);
        var innerTrim = innerInset + corner / inner;
        Point OnSide(double radius, double angle, int side) =>
            At(center, radius, angle + side * Math.Asin(halfGap / radius));
        var outerStart = OnSide(outer, start, 1);
        var outerEnd = OnSide(outer, end, -1);

        void TraceOutline(StreamGeometryContext path)
        {
            path.LineTo(OnSide(inner + corner, end, -1), true, true);
            path.QuadraticBezierTo(At(center, inner, end - innerInset), At(center, inner, end - innerTrim), true, true);
            path.ArcTo(At(center, inner, start + innerTrim), new Size(inner, inner), 0,
                span - 2 * innerTrim > Math.PI, SweepDirection.Counterclockwise, true, true);
            path.QuadraticBezierTo(At(center, inner, start + innerInset), OnSide(inner + corner, start, 1), true, true);
            path.LineTo(outerStart, true, true);
        }

        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(outerStart, true, true);
            path.ArcTo(outerEnd, new Size(outer, outer), 0, span - 2 * Math.Asin(halfGap / outer) > Math.PI,
                SweepDirection.Clockwise, false, false);
            TraceOutline(path);
        }
        var outline = new StreamGeometry();
        using (var path = outline.Open())
        {
            path.BeginFigure(outerEnd, false, false);
            TraceOutline(path);
        }
        geometry.Freeze();
        outline.Freeze();
        return (geometry, outline);
    }

    private static Vector Normal(double angle, int side) => new(-Math.Sin(angle) * side, Math.Cos(angle) * side);

    private static IReadOnlyList<Vector> RingSlots(double firstRing, double pitch, double start,
        double span, double gap, IReadOnlyList<TileFootprint> footprints, double iconWidth, Rect title)
    {
        if (footprints.Count == 0) return Array.Empty<Vector>();
        var fullCircle = span >= Math.Tau - 0.001;
        var middle = start + span / 2;
        var left = Normal(start, 1);
        var right = Normal(start + span, -1);
        var result = new List<Vector>(footprints.Count);
        IReadOnlyList<TileFootprint> previous = Array.Empty<TileFootprint>();
        for (var radius = firstRing; result.Count < footprints.Count; radius += pitch)
        {
            var halfAngle = fullCircle ? Math.PI : span / 2 - Math.Asin((iconWidth / 2 + gap) / radius);
            if (halfAngle < 0) break;

            var minimumStep = 2 * Math.Asin(iconWidth / (2 * radius));
            var intervals = (int)Math.Floor(2 * halfAngle / minimumStep + 1e-9);
            var capacity = Math.Min(footprints.Count - result.Count, fullCircle ? intervals : intervals + 1);
            if (!AppendRow(radius, halfAngle, minimumStep, capacity)) break;
        }
        return result;

        bool AppendRow(double radius, double halfAngle, double minimumStep, int capacity)
        {
            for (var count = capacity; count > 0; count--)
            {
                var halfWidth = (count - 1) / 2.0;
                var maximumStep = fullCircle ? Math.Tau / count : count == 1 ? minimumStep : halfAngle / halfWidth;
                var firstStep = fullCircle ? maximumStep : minimumStep;
                const int stepSamples = 8;
                var samples = maximumStep > firstStep ? stepSamples : 1;
                var row = new Vector[count];
                var bounds = new TileFootprint[count];
                for (var sample = 0; sample < samples; sample++)
                {
                    var step = firstStep + (maximumStep - firstStep) * sample / (stepSamples - 1);
                    var i = 0;
                    for (; i < count; i++)
                    {
                        var offset = (i - halfWidth) * step;
                        var position = (Vector)At(new Point(), radius, middle + offset);
                        var footprint = footprints[result.Count + i];
                        if (!fullCircle && (!ClearsSide(left, span / 2 + offset) || !ClearsSide(right, span / 2 - offset))) break;
                        var placed = footprint.At(position);
                        if (placed.Overlaps(title) || previous.Any(placed.Overlaps) || bounds.Take(i).Any(placed.Overlaps)) break;
                        row[i] = position;
                        bounds[i] = placed;

                        bool ClearsSide(Vector normal, double angleFromSide) =>
                            angleFromSide >= Math.PI / 2 || position * normal >= gap + footprint.Clearance(normal);
                    }
                    if (i != count) continue;
                    result.AddRange(row);
                    previous = bounds;
                    return true;
                }
            }
            return false;
        }
    }

    private readonly record struct TileFootprint(Rect Icon, Rect Label)
    {
        internal static TileFootprint Create(int iconSize, Size label)
        {
            var halfIcon = iconSize / 2.0;
            var hoverGrowth = IconHoverGrowth(iconSize);
            var labelWidth = Math.Ceiling(label.Width) + 2;
            return new TileFootprint(new Rect(-halfIcon - 1 - hoverGrowth / 2, -halfIcon - 1 - hoverGrowth,
                    iconSize + 2 + hoverGrowth, iconSize + 2 + hoverGrowth),
                new Rect(-labelWidth / 2, halfIcon + CompactLabelGap - 1, labelWidth, Math.Ceiling(label.Height) + 2));
        }

        internal TileFootprint At(Vector offset) => new(new Rect(Icon.Location + offset, Icon.Size),
            new Rect(Label.Location + offset, Label.Size));

        internal bool Overlaps(TileFootprint other) => Icon.IntersectsWith(other.Icon) || Icon.IntersectsWith(other.Label) ||
            Label.IntersectsWith(other.Icon) || Label.IntersectsWith(other.Label);

        internal bool Overlaps(Rect rect) => Icon.IntersectsWith(rect) || Label.IntersectsWith(rect);

        internal double ClearTitle(Rect title, Vector direction)
        {
            return Math.Max(Clear(Icon), Clear(Label));

            double Clear(Rect rect) => Math.Min(
                AxisClearance(rect.Left, rect.Right, title.Left, title.Right, direction.X),
                AxisClearance(rect.Top, rect.Bottom, title.Top, title.Bottom, direction.Y));
        }

        private static double AxisClearance(double start, double end, double titleStart, double titleEnd, double component) =>
            component > 0 ? (titleEnd - start) / component : component < 0 ? (end - titleStart) / -component : double.PositiveInfinity;

        internal double Clearance(Vector normal) => Math.Max(Support(Icon, normal), Support(Label, normal));

        private static double Support(Rect rect, Vector normal) =>
            -(normal.X * (normal.X >= 0 ? rect.Left : rect.Right) + normal.Y * (normal.Y >= 0 ? rect.Top : rect.Bottom));

        internal double OuterRadius => Math.Max(FarthestCorner(Icon), FarthestCorner(Label));
    }

    private static double FarthestCorner(Rect rect) => new Vector(Math.Max(Math.Abs(rect.Left), Math.Abs(rect.Right)),
        Math.Max(Math.Abs(rect.Top), Math.Abs(rect.Bottom))).Length;

    private static Point At(Point center, double radius, double angle) =>
        center + new Vector(Math.Cos(angle) * radius, Math.Sin(angle) * radius);
}
