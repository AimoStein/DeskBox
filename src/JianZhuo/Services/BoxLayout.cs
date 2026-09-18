using System.Windows;

namespace JianZhuo.Services;

/// <summary>盒子在桌面坐标系（DIP）里的位置与大小，用于排布计算。</summary>
public readonly struct BoxRect
{
    public BoxRect(string id, double x, double y, double width, double height)
    {
        Id = id;
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public string Id { get; }

    public double X { get; }

    public double Y { get; }

    public double Width { get; }

    public double Height { get; }

    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double CenterX => X + Width / 2;

    public double CenterY => Y + Height / 2;

    public BoxRect WithPosition(double x, double y) => new(Id, x, y, Width, Height);
}

/// <summary>拖动时显示的一条对齐参考线。</summary>
public readonly struct AlignGuide
{
    public AlignGuide(bool vertical, double position, double from, double to)
    {
        Vertical = vertical;
        Position = position;
        From = from;
        To = to;
    }

    /// <summary>true = 竖线（x 对齐），false = 横线（y 对齐）。</summary>
    public bool Vertical { get; }

    /// <summary>参考线所在的 x（竖线）或 y（横线）。</summary>
    public double Position { get; }

    public double From { get; }

    public double To { get; }
}

/// <summary>吸附结果：修正后的位置 + 需要画的参考线。</summary>
public sealed class SnapResult
{
    public double X { get; set; }

    public double Y { get; set; }

    public AlignGuide? Vertical { get; set; }

    public AlignGuide? Horizontal { get; set; }

    public bool Snapped => Vertical.HasValue || Horizontal.HasValue;
}

/// <summary>
/// 盒子排布的纯计算部分：拖动时吸附对齐其他盒子，落位时找最近的空位。
/// 不碰窗口，方便被自检直接调用。
/// </summary>
public static class BoxLayout
{
    /// <summary>吸附距离阈值（DIP）：离对齐位置这么近就算对齐。</summary>
    public const double AlignThreshold = 8;

    /// <summary>两个盒子并排 / 叠放时保留的间距（DIP），避免完全贴合。</summary>
    public const double SnapGap = 12;

    /// <summary>判定重叠时的容差：仅仅贴边不算重叠。</summary>
    public const double OverlapTolerance = 0.5;

    /// <summary>参与避让计算的盒子数量上限，避免极端情况下组合爆炸。</summary>
    private const int MaxPeers = 48;

    public static bool Overlaps(BoxRect a, BoxRect b, double tolerance = OverlapTolerance) =>
        a.X < b.Right - tolerance &&
        b.X < a.Right - tolerance &&
        a.Y < b.Bottom - tolerance &&
        b.Y < a.Bottom - tolerance;

    public static bool AnyOverlap(BoxRect moving, IReadOnlyList<BoxRect> peers, double tolerance = OverlapTolerance)
    {
        foreach (var peer in peers)
        {
            if (Overlaps(moving, peer, tolerance))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 两个盒子是否靠得太近（小于 gap 就算太近，所以"贴边"也算）。
    /// 用于"并排时留缝"的判定。
    /// </summary>
    public static bool TooClose(BoxRect a, BoxRect b, double gap = SnapGap, double tolerance = OverlapTolerance) =>
        a.X < b.Right + gap - tolerance &&
        b.X - gap < a.Right - tolerance &&
        a.Y < b.Bottom + gap - tolerance &&
        b.Y - gap < a.Bottom - tolerance;

    public static bool AnyTooClose(
        BoxRect moving,
        IReadOnlyList<BoxRect> peers,
        double gap = SnapGap,
        double tolerance = OverlapTolerance)
    {
        foreach (var peer in peers)
        {
            if (TooClose(moving, peer, gap, tolerance))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 把盒子约束在可视区域内：四周至少留出 gap 的间距（和两个盒子之间的间距一致）。
    /// 拖到屏幕外时用它把盒子拉回边缘内侧，而不是停在看不见的地方。
    /// 盒子比区域还大时贴住左上角，保证左上边缘始终可见。
    /// </summary>
    public static BoxRect ClampToArea(BoxRect box, Rect area, double gap = SnapGap)
    {
        if (area.Width <= 0 || area.Height <= 0 || double.IsNaN(area.Width) || double.IsNaN(area.Height))
        {
            return box;
        }

        var minX = area.Left + gap;
        var minY = area.Top + gap;
        var maxX = area.Right - gap - box.Width;
        var maxY = area.Bottom - gap - box.Height;

        var x = maxX <= minX ? minX : Math.Clamp(box.X, minX, maxX);
        var y = maxY <= minY ? minY : Math.Clamp(box.Y, minY, maxY);

        if (Math.Abs(x - box.X) < 0.01 && Math.Abs(y - box.Y) < 0.01)
        {
            return box;
        }

        return box.WithPosition(x, y);
    }

    /// <summary>
    /// 把正在拖动的盒子吸附到其他盒子的边或中线上；阈值内没有可对齐的目标时位置不变。
    /// </summary>
    public static SnapResult Snap(BoxRect moving, IReadOnlyList<BoxRect> peers, double threshold = AlignThreshold)
    {
        var result = new SnapResult { X = moving.X, Y = moving.Y };

        if (threshold <= 0 || peers is null || peers.Count == 0)
        {
            return result;
        }

        var bestX = double.MaxValue;
        var bestY = double.MaxValue;

        double lineX = 0;
        double lineY = 0;
        var peerX = default(BoxRect);
        var peerY = default(BoxRect);
        var hasX = false;
        var hasY = false;

        foreach (var peer in peers)
        {
            // 竖直方向（x 轴）的对齐：左左 / 左贴右 / 右贴左 / 右右 / 中线。
            // 两种"贴住"的情况额外留出 SnapGap，避免两个盒子完全贴合。
            ConsiderX(peer.X - moving.X, peer.X, peer);
            ConsiderX(peer.Right + SnapGap - moving.X, peer.Right, peer);
            ConsiderX(peer.X - SnapGap - moving.Right, peer.X, peer);
            ConsiderX(peer.Right - moving.Right, peer.Right, peer);
            ConsiderX(peer.CenterX - moving.CenterX, peer.CenterX, peer);

            // 水平方向（y 轴）的对齐：上上 / 上贴下 / 下贴上 / 下下 / 中线
            ConsiderY(peer.Y - moving.Y, peer.Y, peer);
            ConsiderY(peer.Bottom + SnapGap - moving.Y, peer.Bottom, peer);
            ConsiderY(peer.Y - SnapGap - moving.Bottom, peer.Y, peer);
            ConsiderY(peer.Bottom - moving.Bottom, peer.Bottom, peer);
            ConsiderY(peer.CenterY - moving.CenterY, peer.CenterY, peer);
        }

        if (!hasX && !hasY)
        {
            return result;
        }

        // 上面记录的是「对齐到哪条线」，这里换算成盒子最终位置
        if (hasX)
        {
            result.X = moving.X + bestX;
        }

        if (hasY)
        {
            result.Y = moving.Y + bestY;
        }

        var snapped = moving.WithPosition(result.X, result.Y);

        if (hasX)
        {
            result.Vertical = new AlignGuide(
                vertical: true,
                position: lineX,
                from: Math.Min(snapped.Y, peerX.Y),
                to: Math.Max(snapped.Bottom, peerX.Bottom));
        }

        if (hasY)
        {
            result.Horizontal = new AlignGuide(
                vertical: false,
                position: lineY,
                from: Math.Min(snapped.X, peerY.X),
                to: Math.Max(snapped.Right, peerY.Right));
        }

        return result;

        void ConsiderX(double delta, double line, BoxRect peer)
        {
            var distance = Math.Abs(delta);
            if (distance > threshold || distance >= Math.Abs(bestX))
            {
                return;
            }

            bestX = delta;
            lineX = line;
            peerX = peer;
            hasX = true;
        }

        void ConsiderY(double delta, double line, BoxRect peer)
        {
            var distance = Math.Abs(delta);
            if (distance > threshold || distance >= Math.Abs(bestY))
            {
                return;
            }

            bestY = delta;
            lineY = line;
            peerY = peer;
            hasY = true;
        }
    }

    /// <summary>
    /// 找离目标位置最近、且不与其他盒子重叠的落位。
    /// 候选位置由其他盒子的边和当前目标位置推导，因此结果总是「贴着某个盒子」而不是随机漂移。
    /// </summary>
    public static BoxRect FindFreeSpot(
        BoxRect desired,
        IReadOnlyList<BoxRect> peers,
        Rect? bounds = null,
        double gap = SnapGap,
        double tolerance = OverlapTolerance)
    {
        var others = Nearest(desired, peers, MaxPeers);

        if (others.Count == 0 || !AnyTooClose(desired, others, gap, tolerance))
        {
            return desired;
        }

        var xs = new List<double> { desired.X };
        var ys = new List<double> { desired.Y };

        foreach (var peer in others)
        {
            xs.Add(peer.X);
            xs.Add(peer.Right);
            xs.Add(peer.X - desired.Width);
            xs.Add(peer.Right - desired.Width);
            xs.Add(peer.CenterX - desired.Width / 2);
            xs.Add(peer.Right + gap);
            xs.Add(peer.X - gap - desired.Width);

            ys.Add(peer.Y);
            ys.Add(peer.Bottom);
            ys.Add(peer.Y - desired.Height);
            ys.Add(peer.Bottom - desired.Height);
            ys.Add(peer.CenterY - desired.Height / 2);
            ys.Add(peer.Bottom + gap);
            ys.Add(peer.Y - gap - desired.Height);
        }

        if (bounds is { } area && area.Width > 0 && area.Height > 0)
        {
            var minX = area.Left;
            var maxX = Math.Max(minX, area.Right - desired.Width);
            var minY = area.Top;
            var maxY = Math.Max(minY, area.Bottom - desired.Height);

            for (var i = 0; i < xs.Count; i++)
            {
                xs[i] = Math.Clamp(xs[i], minX, maxX);
            }

            for (var i = 0; i < ys.Count; i++)
            {
                ys[i] = Math.Clamp(ys[i], minY, maxY);
            }
        }

        xs.Sort((a, b) => Math.Abs(a - desired.X).CompareTo(Math.Abs(b - desired.X)));
        ys.Sort((a, b) => Math.Abs(a - desired.Y).CompareTo(Math.Abs(b - desired.Y)));

        BoxRect? best = null;
        var bestCost = double.MaxValue;
        BoxRect? crowded = null;
        var crowdedArea = double.MaxValue;

        foreach (var x in xs)
        {
            foreach (var y in ys)
            {
                var candidate = desired.WithPosition(x, y);
                var overlap = TooCloseArea(candidate, others, gap, tolerance);

                if (overlap <= 0)
                {
                    var cost = (x - desired.X) * (x - desired.X) + (y - desired.Y) * (y - desired.Y);
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        best = candidate;
                    }
                }
                else if (overlap < crowdedArea)
                {
                    crowdedArea = overlap;
                    crowded = candidate;
                }
            }
        }

        // 正常情况一定有完全空闲的位置；桌面挤到极满时退而求其次，选压得最少的位置。
        return best ?? crowded ?? desired;
    }

    /// <summary>和各个盒子"距离不足"的累计面积；0 表示所有间距都够。</summary>
    private static double TooCloseArea(BoxRect candidate, IReadOnlyList<BoxRect> peers, double gap, double tolerance)
    {
        var total = 0.0;

        foreach (var peer in peers)
        {
            // 把对方按 gap 外扩，再算交叠面积
            var width = Math.Min(candidate.Right, peer.Right + gap) - Math.Max(candidate.X, peer.X - gap);
            var height = Math.Min(candidate.Bottom, peer.Bottom + gap) - Math.Max(candidate.Y, peer.Y - gap);

            if (width > tolerance && height > tolerance)
            {
                total += width * height;
            }
        }

        return total;
    }

    private static List<BoxRect> Nearest(BoxRect desired, IReadOnlyList<BoxRect> peers, int limit)
    {
        var list = new List<BoxRect>();

        if (peers is null)
        {
            return list;
        }

        foreach (var peer in peers)
        {
            if (peer.Id == desired.Id)
            {
                continue;
            }

            list.Add(peer);
        }

        if (list.Count <= limit)
        {
            return list;
        }

        list.Sort((a, b) => Distance(a).CompareTo(Distance(b)));
        list.RemoveRange(limit, list.Count - limit);
        return list;

        double Distance(BoxRect peer)
        {
            var dx = peer.CenterX - desired.CenterX;
            var dy = peer.CenterY - desired.CenterY;
            return dx * dx + dy * dy;
        }
    }
}
