using System.Runtime.InteropServices;

namespace KosGovernor.Core;

/// <param name="Cache">LastLevelCacheIndex: the same for every logical processor that shares the last-level cache.</param>
public sealed record LogicalCpu(int Index, uint CpuSetId, int Core, int EfficiencyClass, int Cache = 0);

/// <summary>
/// Logical processors with the CPU set id, the core, the efficiency class and the last-level cache (GetSystemCpuSetInformation).
/// shortcut: group 0 only (up to 64 logical), like KAFFINITY and the masks here; with more than 64, handle groups.
/// </summary>
public sealed class CpuTopology
{
    public IReadOnlyList<LogicalCpu> Cpus { get; }
    public ulong AllMask { get; }
    /// <summary>
    /// Logical processors that share a last-level cache, in order: the CCDs of a Ryzen (the CCXs on Ryzen 3000), or the
    /// compute and low-power clusters of a hybrid Intel.
    /// </summary>
    public IReadOnlyList<ulong> CacheGroups { get; }
    /// <summary>The CCD with the larger L3 (3D V-Cache) on a Ryzen whose CCDs differ; null otherwise.</summary>
    public ulong? VCacheMask { get; }
    /// <summary>E-cores outside the P-cores' cache (Intel LP E-cores); 0 when there are none.</summary>
    public ulong LpECoreMask { get; }

    /// <param name="l3Bytes">L3 size of each cache group (by <see cref="LogicalCpu.Cache"/>); without it there's no V-Cache preset.</param>
    internal CpuTopology(IEnumerable<LogicalCpu> cpus, IReadOnlyDictionary<int, long>? l3Bytes = null)
    {
        Cpus = cpus.Where(c => c.Index < 64).OrderBy(c => c.Index).ToList();
        AllMask = Cpus.Aggregate(0UL, (m, c) => m | 1UL << c.Index);
        CacheGroups = Cpus.GroupBy(c => c.Cache).Select(g => g.Aggregate(0UL, (m, c) => m | 1UL << c.Index)).ToList();
        var top = Cpus.Count == 0 ? 0 : Cpus.Max(c => c.EfficiencyClass);
        var pCaches = Cpus.Where(c => c.EfficiencyClass == top).Select(c => c.Cache).ToHashSet();
        LpECoreMask = IsHybrid ? Mask(c => c.EfficiencyClass < top && !pCaches.Contains(c.Cache)) : 0;
        var sizes = Cpus.Select(c => c.Cache).Distinct().ToDictionary(k => k, k => l3Bytes?.GetValueOrDefault(k) ?? 0);
        if (HasCcds && sizes.Values.All(s => s > 0) && sizes.Values.Distinct().Count() > 1)
            VCacheMask = Mask(c => sizes[c.Cache] == sizes.Values.Max());
    }

    /// <summary>Several CCDs (not a hybrid CPU, whose second cache group is the LP E-cores).</summary>
    public bool HasCcds => !IsHybrid && CacheGroups.Count > 1;
    /// <summary>The CCD(s) without the V-Cache: higher clocks.</summary>
    public ulong? FrequencyMask => VCacheMask is { } v ? AllMask & ~v : null;
    /// <summary>Every logical processor but the first core's, where Windows sends most interrupts and DPCs.</summary>
    public ulong NoCore0Mask => Cores is { Count: > 1 } cores ? AllMask & ~cores[0].Aggregate(0UL, (m, c) => m | 1UL << c.Index) : 0;

    /// <summary>Cores grouped for display: by type on a hybrid CPU, by CCD when there are several; otherwise one group without a title.</summary>
    public IEnumerable<(string Title, List<IReadOnlyList<LogicalCpu>> Cores)> Groups()
    {
        var top = Cpus.Count == 0 ? 0 : Cpus.Max(c => c.EfficiencyClass);
        Func<LogicalCpu, string> title =
            IsHybrid ? c => c.EfficiencyClass == top ? "P-cores" : Has(LpECoreMask, c) ? "LP E-cores" : "E-cores"
            : HasCcds ? c => $"CCD {CacheGroups.ToList().FindIndex(g => Has(g, c))}{(VCacheMask is { } v && Has(v, c) ? " · 3D V-Cache" : "")}"
            : _ => "";
        return Cores.GroupBy(core => title(core[0])).Select(g => (g.Key, g.ToList()));
    }

    static bool Has(ulong mask, LogicalCpu c) => (mask >> c.Index & 1) == 1;

    static readonly Lazy<CpuTopology> Lazy = new(Read);
    public static CpuTopology Current => Lazy.Value;

    /// <summary>Physical cores in order, each with its logical processors (SMT/HT side by side).</summary>
    public IReadOnlyList<IReadOnlyList<LogicalCpu>> Cores =>
        Cpus.GroupBy(c => c.Core).OrderBy(g => g.Min(c => c.Index)).Select(g => (IReadOnlyList<LogicalCpu>)g.OrderBy(c => c.Index).ToList()).ToList();

    public bool HasSmt => Cores.Any(c => c.Count > 1);
    public bool IsHybrid => Cpus.Select(c => c.EfficiencyClass).Distinct().Count() > 1;

    /// <summary>One logical processor per core (the first): the effect of turning SMT/HT off.</summary>
    public ulong SmtOffMask => Cores.Aggregate(0UL, (m, core) => m | 1UL << core[0].Index);
    public ulong PCoreMask => Mask(c => c.EfficiencyClass == Cpus.Max(x => x.EfficiencyClass));
    public ulong ECoreMask => Mask(c => c.EfficiencyClass == Cpus.Min(x => x.EfficiencyClass));

    ulong Mask(Func<LogicalCpu, bool> pick) => Cpus.Where(pick).Aggregate(0UL, (m, c) => m | 1UL << c.Index);

    public uint[] CpuSetIds(ulong mask) => Cpus.Where(c => (mask >> c.Index & 1) == 1).Select(c => c.CpuSetId).ToArray();
    public ulong MaskFromCpuSetIds(IEnumerable<uint> ids) => ids.Aggregate(0UL, (m, id) => Cpus.FirstOrDefault(c => c.CpuSetId == id) is { } c ? m | 1UL << c.Index : m);

    static CpuTopology Read()
    {
        Native.GetSystemCpuSetInformation(IntPtr.Zero, 0, out var length, IntPtr.Zero, 0);
        var list = new List<LogicalCpu>();
        if (length > 0)
        {
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (Native.GetSystemCpuSetInformation(buffer, length, out length, IntPtr.Zero, 0))
                    for (var offset = 0; offset < length;)
                    {
                        var p = buffer + offset;
                        var size = Marshal.ReadInt32(p);
                        // Type 0 = CpuSetInformation: Id @8, Group @12, LogicalProcessorIndex @14, CoreIndex @15, LastLevelCacheIndex @16,
                        // EfficiencyClass @18.
                        if (Marshal.ReadInt32(p, 4) == 0 && Marshal.ReadInt16(p, 12) == 0)
                            list.Add(new LogicalCpu(Marshal.ReadByte(p, 14), (uint)Marshal.ReadInt32(p, 8), Marshal.ReadByte(p, 15), Marshal.ReadByte(p, 18),
                                Marshal.ReadByte(p, 16)));
                        if (size <= 0) break;
                        offset += size;
                    }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        // No CPU sets (should not happen on Windows 10+): each logical processor becomes a core, without ids.
        if (list.Count == 0) list.AddRange(Enumerable.Range(0, Math.Min(Environment.ProcessorCount, 64)).Select(i => new LogicalCpu(i, 0, i, 0)));
        return new CpuTopology(list, L3Sizes(list));
    }

    /// <summary>L3 size of each cache group, from the level-3 entries of GetLogicalProcessorInformationEx (RelationCache).</summary>
    static Dictionary<int, long> L3Sizes(List<LogicalCpu> cpus)
    {
        var sizes = new Dictionary<int, long>();
        var length = 0;
        Native.GetLogicalProcessorInformationEx(Native.RelationCache, IntPtr.Zero, ref length);
        if (length <= 0) return sizes;
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!Native.GetLogicalProcessorInformationEx(Native.RelationCache, buffer, ref length)) return sizes;
            for (var offset = 0; offset + 50 <= length;)
            {
                var p = buffer + offset;
                // CACHE_RELATIONSHIP @8: Level @8, CacheSize @12, GroupMask.Mask @40, GroupMask.Group @48.
                if (Marshal.ReadInt32(p) == Native.RelationCache && Marshal.ReadByte(p, 8) == 3 && Marshal.ReadInt16(p, 48) == 0)
                {
                    var mask = (ulong)Marshal.ReadInt64(p, 40);
                    foreach (var c in cpus.Where(c => (mask >> c.Index & 1) == 1)) sizes[c.Cache] = (uint)Marshal.ReadInt32(p, 12);
                }
                var size = Marshal.ReadInt32(p, 4);
                if (size <= 0) break;
                offset += size;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return sizes;
    }
}

/// <summary>Named set of processors (Masks tab), used for affinity and CPU sets.</summary>
public sealed class CpuMask
{
    public string Name { get; set; } = "";
    /// <summary>List of logical processors ("0-3,8").</summary>
    public string Cpus { get; set; } = "";
}

/// <summary>
/// Text that picks processors in a rule: "all", "smt-off", "p-cores", "e-cores", "lp-e-cores", "ccd:N", "vcache",
/// "frequency-ccd", "no-core-0", "none" (thread CPU sets only: back to the process default), "mask:Name" or a list ("0-3,8").
/// Null or empty = leave unchanged.
/// </summary>
public static class CpuSpec
{
    public const string All = "all", SmtOff = "smt-off", PCores = "p-cores", ECores = "e-cores", LpECores = "lp-e-cores", VCache = "vcache",
        FrequencyCcd = "frequency-ccd", NoCore0 = "no-core-0", None = "none", MaskPrefix = "mask:", CcdPrefix = "ccd:";

    /// <summary>The presets this CPU has, in menu order ("All", "None" and the masks aren't included).</summary>
    public static IEnumerable<(string Label, string Spec)> Presets(CpuTopology topo)
    {
        if (topo.HasSmt) yield return (Label(SmtOff), SmtOff);
        if (topo.IsHybrid) { yield return (Label(PCores), PCores); yield return (Label(ECores), ECores); }
        if (topo.LpECoreMask != 0) yield return (Label(LpECores), LpECores);
        if (topo.HasCcds) for (var i = 0; i < topo.CacheGroups.Count; i++) yield return (Label(CcdPrefix + i), CcdPrefix + i);
        if (topo.VCacheMask is not null) { yield return (Label(VCache), VCache); yield return (Label(FrequencyCcd), FrequencyCcd); }
        if (topo.NoCore0Mask != 0) yield return (Label(NoCore0), NoCore0);
    }

    /// <summary>Mask to apply; 0 only for <see cref="None"/>. Null with <paramref name="error"/> when it does not resolve.</summary>
    public static ulong? Resolve(string spec, CpuTopology topo, IReadOnlyList<CpuMask> masks, out string? error)
    {
        error = null;
        switch (spec)
        {
            case All: return topo.AllMask;
            case None: return 0;
            case SmtOff: return topo.SmtOffMask;
            case PCores: return topo.IsHybrid ? topo.PCoreMask : Fail("This CPU has no E-cores.", out error);
            case ECores: return topo.IsHybrid ? topo.ECoreMask : Fail("This CPU has no E-cores.", out error);
            case LpECores: return topo.LpECoreMask != 0 ? topo.LpECoreMask : Fail("This CPU has no LP E-cores.", out error);
            case VCache: return topo.VCacheMask ?? Fail("This CPU has no CCD with 3D V-Cache.", out error);
            case FrequencyCcd: return topo.FrequencyMask ?? Fail("This CPU has no CCD with 3D V-Cache.", out error);
            case NoCore0: return topo.NoCore0Mask != 0 ? topo.NoCore0Mask : Fail("This CPU has a single core.", out error);
        }
        if (spec.StartsWith(CcdPrefix, StringComparison.Ordinal))
            return int.TryParse(spec.AsSpan(CcdPrefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ccd)
                && topo.HasCcds && ccd < topo.CacheGroups.Count
                ? topo.CacheGroups[ccd]
                : Fail(topo.HasCcds ? $"This CPU has CCDs 0 to {topo.CacheGroups.Count - 1}." : "This CPU has a single CCD.", out error);
        if (spec.StartsWith(MaskPrefix, StringComparison.Ordinal))
        {
            var name = spec[MaskPrefix.Length..];
            return masks.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } m
                ? ParseList(m.Cpus, topo, out error)
                : Fail($"Mask \"{name}\" doesn't exist.", out error);
        }
        return ParseList(spec, topo, out error);
    }

    static ulong? Fail(string message, out string? error) { error = message; return null; }

    /// <summary>"0-3,6" → mask, only with processors that exist on this machine.</summary>
    public static ulong? ParseList(string text, CpuTopology topo, out string? error)
    {
        error = null;
        ulong mask = 0;
        foreach (var raw in text.Split(',', StringSplitOptions.TrimEntries))
        {
            var range = raw.Split('-', StringSplitOptions.TrimEntries);
            if (range.Length is 0 or > 2 || !int.TryParse(range[0], out var a) || !int.TryParse(range[^1], out var b) || a < 0 || a > b || b > 63)
                return Fail("Use processor numbers like 0-3,6.", out error);
            for (var i = a; i <= b; i++) mask |= 1UL << i;
        }
        if ((mask & ~topo.AllMask) != 0) return Fail($"This PC has processors 0 to {topo.Cpus.Count - 1}.", out error);
        return mask;
    }

    public static string FormatList(ulong mask)
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

    public static string Label(string? spec) => spec switch
    {
        null or "" => "(no change)",
        All => "All",
        None => "None",
        SmtOff => "SMT / HT off",
        PCores => "P-cores",
        ECores => "E-cores",
        LpECores => "LP E-cores",
        VCache => "3D V-Cache CCD",
        FrequencyCcd => "Frequency CCD",
        NoCore0 => "All but core 0",
        _ when spec.StartsWith(CcdPrefix, StringComparison.Ordinal) => $"CCD {spec[CcdPrefix.Length..]}",
        _ when spec.StartsWith(MaskPrefix, StringComparison.Ordinal) => spec[MaskPrefix.Length..],
        _ => spec,
    };
}
