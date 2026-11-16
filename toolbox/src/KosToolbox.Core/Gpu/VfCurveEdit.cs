namespace KosToolbox.Core.Gpu;

/// <summary>V/F curve editing like MSI Afterburner (no I/O, testable).</summary>
public static class VfCurveEdit
{
    /// <summary>
    /// Moves the <paramref name="selected"/> points together by <paramref name="diff"/> kHz from
    /// <paramref name="baseDeltas"/> (index → offset). The step stops when the first point reaches the end of the driver's range,
    /// so the group keeps its shape. Then the curve is flattened (<see cref="Flatten"/>). Point 0 never changes
    /// (the driver does not allow it). Returns the new offsets.
    /// </summary>
    public static Dictionary<int, int> Move(IReadOnlyList<GpuVfPoint> points, IReadOnlyDictionary<int, int> baseDeltas,
        IReadOnlySet<int> selected, long diff, int minDelta, int maxDelta)
    {
        var result = new Dictionary<int, int>(baseDeltas);
        var moving = points.Where(p => p.Index > 0 && selected.Contains(p.Index)).ToList();
        if (moving.Count == 0) return result;
        var lo = moving.Max(p => (long)minDelta - baseDeltas[p.Index]);
        var hi = moving.Min(p => (long)maxDelta - baseDeltas[p.Index]);
        diff = lo <= hi ? Math.Clamp(diff, lo, hi) : 0;
        if (diff == 0) return result;
        foreach (var p in moving) result[p.Index] = (int)(baseDeltas[p.Index] + diff);
        return Flatten(points, result, minDelta, maxDelta);
    }

    /// <summary>
    /// Like Afterburner: no point stays below a point to its left; those that would rise to the highest one before
    /// them (within the driver's range). Point 0 never changes.
    /// </summary>
    public static Dictionary<int, int> Flatten(IReadOnlyList<GpuVfPoint> points, IReadOnlyDictionary<int, int> deltas, int minDelta, int maxDelta)
    {
        var result = new Dictionary<int, int>(deltas);
        var top = long.MinValue;
        foreach (var p in points.Where(p => p.Index > 0))
        {
            var stock = (long)p.FrequencyKhz - p.DeltaKhz;
            var d = (long)result[p.Index];
            if (stock + d < top) d = Math.Clamp(top - stock, minDelta, maxDelta);
            top = Math.Max(top, stock + d);
            result[p.Index] = (int)d;
        }
        return result;
    }
}
