using KosToolbox.Core.Text;

namespace KosToolbox.Core.SystemInfo;

/// <summary>Lists of logical processors ("0-3,6") and the corresponding affinity masks.</summary>
public static class Cpus
{
    // shortcut: only the first processor group (up to 64 logical), like KAFFINITY and Process.ProcessorAffinity; with more than 64, use CPU sets.
    public static int Count { get; set; } = Math.Clamp(Environment.ProcessorCount, 1, 64);
    public static ulong AllMask => Count == 64 ? ulong.MaxValue : (1UL << Count) - 1;

    /// <summary>Converts "0-3,6" into a mask. Null with <paramref name="error"/> when the text isn't valid.</summary>
    public static ulong? Parse(string text, out string? error)
    {
        error = null;
        ulong mask = 0;
        foreach (var raw in text.Split(',', StringSplitOptions.TrimEntries))
        {
            var range = raw.Split('-', StringSplitOptions.TrimEntries);
            if (range.Length is 0 or > 2 || !int.TryParse(range[0], out var a) || !int.TryParse(range[^1], out var b) || a < 0 || a > b)
            {
                error = Txt.CpuListInvalid;
                return null;
            }
            if (b >= Count)
            {
                error = Txt.CpuOutOfRange(Count);
                return null;
            }
            for (var i = a; i <= b; i++) mask |= 1UL << i;
        }
        return mask;
    }

    public static string Format(ulong mask)
    {
        var parts = new List<string>();
        for (var i = 0; i < 64; i++)
        {
            if ((mask >> i & 1) == 0) continue;
            var j = i;
            while (j < 63 && (mask >> (j + 1) & 1) == 1) j++;
            parts.Add(i == j ? $"{i}" : $"{i}-{j}");
            i = j;
        }
        return string.Join(",", parts);
    }
}
