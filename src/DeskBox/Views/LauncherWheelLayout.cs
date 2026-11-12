using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace DeskBox.Views;

internal static class LauncherWheelLayout
{
    private const double HalfGap = 4;
    private const double TileWidthRatio = 0.76;
    private const double TileHeightRatio = 0.80;
    internal const double CompactIconInset = 2; // Button border and vertical padding.
    internal const double CompactLabelGap = 1;
    internal const double CompactLabelHeight = 16;
    private const double CompactVerticalReserve = CompactIconInset * 2 + CompactLabelGap + CompactLabelHeight + 1;
    private const double ExtraTileHeight = 6;

    internal sealed record Sector(double Span, Geometry Shape, Geometry Outline, Point3D[] Edges, Rect Title, IReadOnlyList<Point> Slots);
    internal sealed record Layout(Point Center, double Radius, double FadeStart, double HubRadius, double Pitch,
        Size TileSize, double IconAnchor, IReadOnlyList<Sector> Sectors);

    internal static int CompactIconSize(Size bounds, double iconSize) =>
        Math.Clamp((int)Math.Min(bounds.Height - CompactVerticalReserve, bounds.Width - 4),
            16, Math.Min(78, (int)iconSize + 17));

    internal static Layout Arrange(Size size, IReadOnlyList<int> counts, double preferredPitch,
        IReadOnlyList<Size>? titles = null, double iconSize = 37)
    {
        var center = new Point(size.Width / 2, size.Height / 2);
        var radius = Math.Min(center.X, center.Y) - 2;
        if (counts.Count == 0) return Empty(center, radius);
        var spans = Angles(counts);
        var angle = -Math.PI / 2;
        var divisions = spans.Select((span, index) =>
        {
            var start = angle;
            angle += span;
            var middle = start + span / 2;
            var title = titles?[index] ?? new Size(56, 24);
            return (Start: start, Span: span, Direction: new Vector(Math.Cos(middle), Math.Sin(middle)), Title: title);
        }).ToArray();
        // Leave exactly as much space as the horizontal names need beyond the rim.
        foreach (var division in divisions)
        {
            var dx = Math.Abs(division.Direction.X);
            var dy = Math.Abs(division.Direction.Y);
            var support = dx * division.Title.Width / 2 + dy * division.Title.Height / 2;
            if (dx > 0.001) radius = Math.Min(radius, (center.X - division.Title.Width / 2 - 2) / dx - support - 8);
            if (dy > 0.001) radius = Math.Min(radius, (center.Y - division.Title.Height / 2 - 2) / dy - support - 8);
        }
        var hub = HubRadius(radius);
        if (radius < 45) return Empty(center, radius);

        var inner = hub + 9;
        const double minimum = 46;
        for (var pitch = Math.Max(minimum, preferredPitch); ; pitch = Math.Max(minimum, pitch - 2))
        {
            var tile = new Size(pitch * TileWidthRatio, pitch * TileHeightRatio + ExtraTileHeight);
            var iconAnchor = CompactIconInset + CompactIconSize(tile, iconSize) / 2.0;
            var padding = new Vector(tile.Width / 2, Math.Max(iconAnchor, tile.Height - iconAnchor)).Length + 2;
            var minFade = Math.Clamp(pitch * 0.52, 24, 44);
            var rings = new List<double>();
            for (var ring = inner + padding; ring <= radius - padding - minFade; ring += pitch) rings.Add(ring);
            var slots = divisions.Select((division, i) =>
                SpokeSlots(center, rings, inner, division.Start, division.Span, pitch, counts[i], tile, iconAnchor)).ToArray();
            if (pitch > minimum && slots.Where((row, i) => row.Count < counts[i]).Any()) continue;

            var outermostIcon = slots.Where(row => row.Count > 0)
                .Select(row => (row[^1] - center).Length).DefaultIfEmpty(inner).Max();
            var fadeStart = Math.Min(radius - minFade, Math.Max(inner + pitch, outermostIcon + padding));
            var outer = Math.Min(radius, fadeStart + Math.Clamp(pitch * 1.1, 54, 94));
            var sectors = divisions.Select((division, i) =>
            {
                var (start, span, direction, titleSize) = division;
                var (shape, outline) = BuildSector(center, outer, inner, start, span);
                var gap = SectorHalfGap(inner, span);
                var edges = span >= Math.Tau - 0.001
                    ? new[] { new Point3D(0, 0, 10000), new Point3D(0, 0, 10000) }
                    : new[] { Edge(center, start, 1, gap), Edge(center, start + span, -1, gap) };
                var support = Math.Abs(direction.X) * titleSize.Width / 2 + Math.Abs(direction.Y) * titleSize.Height / 2;
                var titleCenter = center + direction * (outer + support + 8);
                var title = new Rect(titleCenter - new Vector(titleSize.Width / 2, titleSize.Height / 2), titleSize);
                return new Sector(span, shape, outline, edges, title, slots[i]);
            }).ToArray();
            return new Layout(center, outer, fadeStart, hub, pitch, tile, iconAnchor, sectors);
        }
    }

    private static Layout Empty(Point center, double radius) =>
        new(center, radius, 0, HubRadius(radius), 0, Size.Empty, 0, Array.Empty<Sector>());

    private static double HubRadius(double radius) => Math.Clamp(radius * 0.083, 22, 39);

    private static double[] Angles(IReadOnlyList<int> counts)
    {
        if (counts.Count == 1) return new[] { Math.Tau };
        if (counts.Count == 2) return new[] { Math.PI, Math.PI };
        var min = Math.Min(Math.PI / 5, Math.Tau / counts.Count * 0.46);
        const double max = Math.PI * 0.92;
        var weights = counts.Select(count => count + 2.0).ToArray();
        var low = 0.0;
        var high = Math.Tau;
        for (var step = 0; step < 32; step++)
        {
            var mid = (low + high) / 2;
            if (weights.Sum(weight => Math.Min(max, min + mid * weight)) < Math.Tau) low = mid;
            else high = mid;
        }
        var angles = weights.Select(weight => Math.Min(max, min + high * weight)).ToArray();
        var total = angles.Sum();
        return angles.Select(span => span * (Math.Tau / total)).ToArray();
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
                false, SweepDirection.Counterclockwise, true, true);
            path.QuadraticBezierTo(At(center, inner, start + innerInset), OnSide(inner + corner, start, 1), true, true);
            path.LineTo(outerStart, true, true);
        }

        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(outerStart, true, true);
            path.ArcTo(outerEnd, new Size(outer, outer), 0, false, SweepDirection.Clockwise, false, false);
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

    private static Point3D Edge(Point center, double angle, int side, double halfGap)
    {
        var nx = -Math.Sin(angle) * side;
        var ny = Math.Cos(angle) * side;
        return new Point3D(nx, ny, -nx * center.X - ny * center.Y - halfGap);
    }

    private static IReadOnlyList<Point> SpokeSlots(Point center, IReadOnlyList<double> rings, double inner, double start,
        double span, double pitch, int count, Size tile, double iconAnchor)
    {
        if (count == 0 || rings.Count == 0) return Array.Empty<Point>();
        var fullCircle = span >= Math.Tau - 0.001;
        var middle = start + span / 2;
        var gap = SectorHalfGap(inner, span);
        double SideClearance(double angle, int side)
        {
            var ny = Math.Cos(angle) * side;
            var height = ny >= 0 ? iconAnchor : tile.Height - iconAnchor;
            return gap + 2 + Math.Abs(Math.Sin(angle)) * tile.Width / 2 + Math.Abs(ny) * height;
        }
        var leftClearance = SideClearance(start, 1);
        var rightClearance = SideClearance(start + span, -1);

        // Fix the directions once for this sector. Near the hub, skip spokes
        // until there is a full tile's worth of space; outer rows use more of them.
        var outerStep = 2 * Math.Asin(pitch / (2 * rings[^1]));
        var circleSpokes = Math.Max(2, (int)Math.Floor(Math.Tau / outerStep));
        var spoke = fullCircle ? Math.Tau / circleSpokes : outerStep / 2;
        var rows = new List<(double Radius, int Stride, int Low, int High, int Capacity)>();
        var capacity = 0;
        foreach (var radius in rings)
        {
            if (capacity >= count) break;
            var ideal = 2 * Math.Asin(pitch / (2 * radius));
            var stride = Math.Max(1, (int)Math.Ceiling(ideal / spoke - 1e-9));
            var low = 0;
            var high = 0;
            if (!fullCircle)
            {
                if (Math.Max(leftClearance, rightClearance) >= radius) continue;
                low = (int)Math.Ceiling((Math.Asin(leftClearance / radius) - span / 2) / spoke - 1e-9);
                high = (int)Math.Floor((span / 2 - Math.Asin(rightClearance / radius)) / spoke + 1e-9);
                if (low > high) continue;
            }
            var rowCapacity = fullCircle ? circleSpokes / stride : 1 + (high - low) / stride;
            rows.Add((radius, stride, low, high, rowCapacity));
            capacity += rowCapacity;
        }

        var remaining = Math.Min(count, capacity);
        var result = new List<Point>();
        var earlierSpokes = new HashSet<int>();
        var previousRow = new HashSet<int>();
        for (var row = 0; row < rows.Count; row++)
        {
            var (radius, stride, low, high, rowCapacity) = rows[row];
            // Share a partial outer row with the preceding ones instead of
            // leaving a lone icon outside a densely filled row.
            var slots = Math.Min(rowCapacity, remaining / (rows.Count - row));
            remaining -= slots;
            if (fullCircle)
            {
                // The circle has no side edges, but must also keep the last
                // icon one pitch away from the first across the wraparound.
                for (var i = 0; i < slots; i++)
                    result.Add(At(center, radius, start + (i * circleSpokes / slots) * spoke));
            }
            else
            {
                var width = (slots - 1) * stride;
                var centered = (low + high - width) / 2.0;
                var firstSpoke = (int)Math.Ceiling(centered);
                var best = double.NegativeInfinity;
                // Keep rows near the bisector, but prefer reusing a direction
                // from the inner rows so shortcuts form visible straight lines.
                for (var first = Math.Max(low, (int)Math.Ceiling(centered - 2));
                    first <= Math.Min(high - width, (int)Math.Floor(centered + 2)); first++)
                {
                    var score = -Math.Abs(first - centered);
                    for (var i = 0; i < slots; i++)
                    {
                        var index = first + i * stride;
                        score += previousRow.Contains(index) ? 4 : earlierSpokes.Contains(index) ? 2 : 0;
                    }
                    if (score <= best) continue;
                    best = score;
                    firstSpoke = first;
                }
                previousRow.Clear();
                for (var i = 0; i < slots; i++)
                {
                    var index = firstSpoke + i * stride;
                    result.Add(At(center, radius, middle + index * spoke));
                    previousRow.Add(index);
                    earlierSpokes.Add(index);
                }
            }
        }
        return result;
    }

    private static Point At(Point center, double radius, double angle) =>
        center + new Vector(Math.Cos(angle) * radius, Math.Sin(angle) * radius);
}
