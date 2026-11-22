namespace KosToolbox.Core.Latency;

/// <summary>
/// Statistics of one capture's frame times (ms), the ones CapFrameX shows. Two kinds of "low": <see cref="P1LowFps"/> is the
/// 99th percentile frame time as FPS (what the result card always showed), <see cref="Worst1Fps"/> the average FPS of the worst
/// 1 % of frames (CapFrameX's "1 % low average"), which counts how bad the spikes are, not only that they exist.
/// </summary>
public sealed record FrameStats(int Frames, double Seconds, double AverageFps, double MedianMs, double P90Ms, double P95Ms, double P99Ms, double P999Ms,
    double MaxMs, double P1LowFps, double P01LowFps, double Worst1Fps, double Worst01Fps, int Stutters, double StdDevMs, double Over16Share, double Over8Share)
{
    /// <summary>Null with fewer than 2 frames.</summary>
    public static FrameStats? Of(IReadOnlyList<double> ms)
    {
        if (ms.Count < 2) return null;
        var sorted = ms.Order().ToArray();
        var total = sorted.Sum();
        var mean = total / sorted.Length;
        var median = Percentile(sorted, 0.5);
        double WorstFps(double share)
        {
            var n = Math.Max(1, (int)Math.Ceiling(share * sorted.Length));
            return 1000 / (sorted.Skip(sorted.Length - n).Sum() / n);
        }
        return new FrameStats(sorted.Length, total / 1000, 1000 * sorted.Length / total, median, Percentile(sorted, 0.9), Percentile(sorted, 0.95),
            Percentile(sorted, 0.99), Percentile(sorted, 0.999), sorted[^1], 1000 / Percentile(sorted, 0.99), 1000 / Percentile(sorted, 0.999),
            WorstFps(0.01), WorstFps(0.001), ms.Count(t => t > 2 * median), Math.Sqrt(sorted.Sum(t => (t - mean) * (t - mean)) / sorted.Length),
            (double)sorted.Count(t => t > 1000 / 60.0) / sorted.Length, (double)sorted.Count(t => t > 1000 / 120.0) / sorted.Length);
    }

    /// <summary>Nearest rank, the same as <see cref="FrameCapture"/> has always used.</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];

    /// <summary>The time (s) at which each frame ended, from the start of the capture.</summary>
    public static double[] Cumulative(IReadOnlyList<double> ms)
    {
        var t = new double[ms.Count];
        var sum = 0.0;
        for (var i = 0; i < ms.Count; i++) t[i] = (sum += ms[i]) / 1000;
        return t;
    }

    /// <summary>
    /// A line of at most 2 × <paramref name="slices"/> points: the lowest and highest point of each slice, in order, so no spike is lost
    /// and four overlaid captures of tens of thousands of frames still draw quickly.
    /// </summary>
    public static List<(double X, double Y)> Thin(IReadOnlyList<double> x, IReadOnlyList<double> y, int slices = 800)
    {
        if (x.Count <= 2 * slices) return x.Select((v, i) => (v, y[i])).ToList();
        var result = new List<(double, double)>(2 * slices);
        for (var s = 0; s < slices; s++)
        {
            int from = (int)((long)s * x.Count / slices), to = (int)((long)(s + 1) * x.Count / slices);
            int lo = from, hi = from;
            for (var i = from; i < to; i++) { if (y[i] < y[lo]) lo = i; if (y[i] > y[hi]) hi = i; }
            result.Add((x[Math.Min(lo, hi)], y[Math.Min(lo, hi)]));
            if (lo != hi) result.Add((x[Math.Max(lo, hi)], y[Math.Max(lo, hi)]));
        }
        return result;
    }

    /// <summary>Average FPS in consecutive windows (frames grouped by the window they end in), at the middle of each window (s).</summary>
    public static List<(double Seconds, double Fps)> FpsOverTime(IReadOnlyList<double> ms, double window = 1)
    {
        var result = new List<(double, double)>();
        double end = 0, sum = 0;
        int count = 0, index = 0;
        foreach (var t in ms)
        {
            end += t / 1000;
            var w = (int)Math.Floor(end / window - 1e-9);
            if (w != index && count > 0)
            {
                result.Add(((index + 0.5) * window, 1000 * count / sum));
                (count, sum) = (0, 0);
            }
            index = w;
            count++;
            sum += t;
        }
        if (count > 0) result.Add(((index + 0.5) * window, 1000 * count / sum));
        return result;
    }

    /// <summary>
    /// A round bin width (0.1, 0.2, 0.25, 0.5, 1, 2, 2.5, 5 × 10^n ms) giving about 40 bins between the 1st and 99th percentile of all the
    /// captures: a few long spikes don't squeeze the usual frames into one bin.
    /// </summary>
    public static double BinWidth(IEnumerable<IReadOnlyList<double>> captures)
    {
        var all = captures.Where(c => c.Count > 0).Select(c => c.Order().ToArray()).ToList();
        if (all.Count == 0) return 1;
        var low = all.Min(s => Percentile(s, 0.01));
        var high = all.Max(s => Percentile(s, 0.99));
        var raw = Math.Max(high - low, 0.1) / 40;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        return new[] { 1, 2, 2.5, 5, 10 }.Select(m => m * magnitude).First(s => s >= raw - 1e-12);
    }

    /// <summary>
    /// Share of frames per bin of <paramref name="width"/> ms (start of each bin), from the bin of the shortest frame to the one of the
    /// 99.9th percentile of <paramref name="range"/> (all the captures compared, so their bins line up); longer frames count in the last bin.
    /// </summary>
    public static List<(double Ms, double Share)> Histogram(IReadOnlyList<double> ms, double width, IEnumerable<IReadOnlyList<double>>? range = null)
    {
        var sets = (range ?? new[] { ms }).Where(c => c.Count > 0).Select(c => c.Order().ToArray()).ToList();
        if (ms.Count == 0 || sets.Count == 0) return new();
        var first = (int)Math.Floor(sets.Min(s => s[0]) / width + 1e-9);
        var last = (int)Math.Floor(sets.Max(s => Percentile(s, 0.999)) / width + 1e-9);
        var bins = new double[Math.Min(last - first + 1, 400)];
        foreach (var t in ms) bins[Math.Clamp((int)Math.Floor(t / width + 1e-9) - first, 0, bins.Length - 1)]++;
        return bins.Select((n, i) => ((first + i) * width, n / ms.Count)).ToList();
    }
}

/// <summary>A row of the comparison table: one value per capture and its difference from the first (the reference).</summary>
public sealed record FrameCompareRow(string Label, string Unit, IReadOnlyList<FrameCompareCell> Cells);

/// <summary><see cref="DiffPercent"/> is null for the reference; <see cref="Worse"/> when it is more than 5 % worse than the reference.</summary>
public sealed record FrameCompareCell(double Value, double? DiffPercent, bool Worse);

public static class FrameCompare
{
    /// <summary>Beyond this, a difference is worse, not noise between two runs.</summary>
    public const double WorseThreshold = 5;

    static readonly (string Label, string Unit, bool HigherIsBetter, Func<FrameStats, double> Value)[] Rows =
    {
        ("Average", "FPS", true, s => s.AverageFps),
        ("1 % low (99th percentile)", "FPS", true, s => s.P1LowFps),
        ("0.1 % low (99.9th percentile)", "FPS", true, s => s.P01LowFps),
        ("Worst 1 % average", "FPS", true, s => s.Worst1Fps),
        ("Worst 0.1 % average", "FPS", true, s => s.Worst01Fps),
        ("Median frame time", "ms", false, s => s.MedianMs),
        ("99th percentile frame time", "ms", false, s => s.P99Ms),
        ("99.9th percentile frame time", "ms", false, s => s.P999Ms),
        ("Longest frame", "ms", false, s => s.MaxMs),
        ("Frame time deviation", "ms", false, s => s.StdDevMs),
        ("Stutters (over 2× median)", "", false, s => s.Stutters),
        ("Frames over 16.7 ms", "%", false, s => 100 * s.Over16Share),
        ("Frames over 8.3 ms", "%", false, s => 100 * s.Over8Share),
    };

    /// <summary>The comparison of <paramref name="stats"/>, the first being the reference.</summary>
    public static List<FrameCompareRow> Table(IReadOnlyList<FrameStats> stats) => Rows.Select(r =>
    {
        var reference = r.Value(stats[0]);
        return new FrameCompareRow(r.Label, r.Unit, stats.Select((s, i) =>
        {
            var v = r.Value(s);
            if (i == 0) return new FrameCompareCell(v, null, false);
            double? diff = Math.Abs(reference) < 1e-12 ? (Math.Abs(v) < 1e-12 ? 0 : null) : 100 * (v - reference) / reference;
            var worse = r.HigherIsBetter ? diff < -WorseThreshold : diff > WorseThreshold || (diff is null && v > reference);
            return new FrameCompareCell(v, diff, worse);
        }).ToList());
    }).ToList();
}
