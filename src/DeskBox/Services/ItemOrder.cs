namespace DeskBox.Services;

/// <summary>
/// 盒子里条目的显示顺序：用户拖动条目调整位置时用它算出新顺序。
/// 只做纯计算，不碰窗口，方便被自检直接调用。
/// </summary>
public static class ItemOrder
{
    /// <summary>
    /// 把 <paramref name="moving"/> 这些条目挪到 <paramref name="insertIndex"/> 处，
    /// 返回新的顺序。下标基于「挪动之前」的列表，因此调用方可以直接把
    /// 拖拽落点对应的下标传进来。
    /// </summary>
    public static List<T> Apply<T>(IReadOnlyList<T> current, IReadOnlyCollection<T> moving, int insertIndex)
        where T : notnull
    {
        var set = new HashSet<T>(moving);
        var result = current.Where(item => !set.Contains(item)).ToList();

        // 落点在挪动前列表里的位置，扣掉它前面那些「同样要被挪走的条目」才是插入点
        var removedBefore = 0;
        for (var i = 0; i < Math.Min(insertIndex, current.Count); i++)
        {
            if (set.Contains(current[i]))
            {
                removedBefore++;
            }
        }

        var target = Math.Clamp(insertIndex - removedBefore, 0, result.Count);
        result.InsertRange(target, current.Where(item => set.Contains(item)));

        return result;
    }

    /// <summary>
    /// 按记录的显示顺序排序：记录里的条目按记录顺序排在前面，
    /// 没记录过的（新放进盒子的）保持原有的先后顺序排在后面。
    /// </summary>
    public static List<T> Sort<T>(IReadOnlyList<T> items, IReadOnlyList<string>? order, Func<T, string> key)
    {
        if (order is null || order.Count == 0)
        {
            return items.ToList();
        }

        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, index) in order.Select((name, index) => (name, index)))
        {
            rank.TryAdd(name, index);
        }

        return items
            .OrderBy(item => rank.TryGetValue(key(item), out var index) ? index : int.MaxValue)
            .ToList();
    }
}
