using System.Runtime.InteropServices;

namespace KosToolbox.Core.Devices;

public enum IrqRole { Other, Gpu, Audio, Network, Usb, InputUsb }

/// <summary>A PCI device as the layout and the advice see it: its role, settings and the interrupts in effect.</summary>
public sealed record IrqDevice(string InstanceId, string Name, IrqRole Role, uint? Support, InterruptConfig Config, IrqState? Effective)
{
    /// <summary>Processors its interrupts go to: what is in effect, else the pinned mask.</summary>
    public ulong? Cpus(ulong allMask) => Effective is { Count: > 0, Affinity: var a } && (a & allMask) != 0 ? a & allMask : Config.Mask;
    public bool Pinned => Config.Policy == Interrupts.PolicySpecified && Config.Mask is not null;
}

/// <summary>A physical core (group 0): its first logical processor, all of its logical processors, and its class and L3.</summary>
public sealed record CpuCore(int FirstCpu, ulong Mask, int EfficiencyClass, int SchedulingClass, int Cache, long L3Bytes);

/// <param name="Kept">Already pinned inside the pool: left as it is.</param>
public sealed record IrqPick(IrqDevice Device, ulong Mask, bool Kept);

/// <summary>
/// Suggested interrupt layout: devices that matter for latency get their own P-cores, away from CPU 0's core (where Windows
/// handles most other interrupts and DPCs) and, on CPUs with several L3 domains, away from the CCD games run on.
/// </summary>
public static class IrqLayout
{
    /// <summary>Role from the Windows class, the PCI class code and whether a mouse or keyboard hangs below it.</summary>
    public static IrqRole RoleOf(PnpDevice d, bool input) => d.Class.ToUpperInvariant() switch
    {
        "DISPLAY" => IrqRole.Gpu,
        "NET" => IrqRole.Network,
        "USB" => input ? IrqRole.InputUsb : IrqRole.Usb,
        "MEDIA" => IrqRole.Audio,
        _ => d.PciBaseClass == 4 ? IrqRole.Audio : IrqRole.Other, // HD Audio controllers are class System on Windows 11
    };

    /// <summary>
    /// The L3 domain games should run on, when the P-cores span several (AMD CCDs): the largest L3 (3D V-Cache), or with equal
    /// sizes the one holding the core with the highest SchedulingClass (the first such domain on a tie). Null with a single L3.
    /// </summary>
    public static int? GameCache(IReadOnlyList<CpuCore> cores)
    {
        var p = PCores(cores);
        if (p.Select(c => c.Cache).Distinct().Count() < 2) return null;
        var largest = p.Max(c => c.L3Bytes);
        if (p.Any(c => c.L3Bytes != largest)) return p.First(c => c.L3Bytes == largest).Cache;
        var top = p.Max(c => c.SchedulingClass);
        return p.First(c => c.SchedulingClass == top).Cache;
    }

    static List<CpuCore> PCores(IReadOnlyList<CpuCore> cores)
    {
        if (cores.Count == 0) return new();
        var top = cores.Max(c => c.EfficiencyClass);
        return cores.Where(c => c.EfficiencyClass == top).OrderBy(c => c.FirstCpu).ToList();
    }

    /// <summary>Cores interrupts may go to: P-cores, without CPU 0's core, outside the game CCD when there is one (else inside it).</summary>
    public static List<CpuCore> Pool(IReadOnlyList<CpuCore> cores)
    {
        var p = PCores(cores).Where(c => (c.Mask & 1) == 0).ToList();
        if (GameCache(cores) is not { } game) return p;
        var other = p.Where(c => c.Cache != game).ToList();
        return other.Count > 0 ? other : p;
    }

    public static List<IrqPick> Suggest(IReadOnlyList<IrqDevice> devices, IReadOnlyList<CpuCore> cores)
    {
        var pool = Pool(cores);
        var picks = new List<IrqPick>();
        if (pool.Count == 0) return picks;
        var uses = pool.ToDictionary(c => c, _ => 0);
        var poolMask = pool.Aggregate(0UL, (m, c) => m | 1UL << c.FirstCpu);
        var left = devices.Where(d => d.Role != IrqRole.Other).ToList();

        foreach (var d in left.Where(d => d.Pinned && (d.Config.Mask!.Value & ~poolMask) == 0).ToList())
        {
            picks.Add(new IrqPick(d, d.Config.Mask!.Value, true));
            foreach (var c in pool.Where(c => (d.Config.Mask!.Value >> c.FirstCpu & 1) == 1)) uses[c]++;
            left.Remove(d);
        }

        CpuCore Pick(ICollection<CpuCore>? skip = null)
        {
            var core = pool.Where(c => skip?.Contains(c) != true).OrderBy(c => uses[c]).First(); // OrderBy is stable: pool order on ties
            uses[core]++;
            return core;
        }
        void Add(IrqDevice d, ulong mask) => picks.Add(new IrqPick(d, mask, false));

        foreach (var d in left.Where(d => d.Role == IrqRole.InputUsb)) Add(d, 1UL << Pick().FirstCpu);
        foreach (var d in left.Where(d => d.Role == IrqRole.Network)) Add(d, 1UL << Pick().FirstCpu);
        CpuCore? shared = null;
        foreach (var d in left.Where(d => d.Role is IrqRole.Audio or IrqRole.Usb)) Add(d, 1UL << (shared ??= Pick()).FirstCpu);
        foreach (var d in left.Where(d => d.Role == IrqRole.Gpu))
        {
            var n = Math.Min(Math.Max(2, d.Effective?.Count ?? 1), Math.Max(1, pool.Count / 2));
            var chosen = new List<CpuCore>();
            for (var i = 0; i < n; i++) chosen.Add(Pick(chosen));
            Add(d, chosen.Aggregate(0UL, (m, c) => m | 1UL << c.FirstCpu));
        }
        return picks;
    }

    // ---------- topology ----------
    static readonly Lazy<IReadOnlyList<CpuCore>> Lazy = new(ReadCores);
    public static IReadOnlyList<CpuCore> Cores => Lazy.Value;

    /// <summary>
    /// Physical cores of group 0 from GetSystemCpuSetInformation (Id @8, Group @12, LogicalProcessorIndex @14, CoreIndex @15,
    /// LastLevelCacheIndex @16, EfficiencyClass @18, SchedulingClass @20), with the L3 size of each cache domain.
    /// </summary>
    static IReadOnlyList<CpuCore> ReadCores()
    {
        var cpus = new List<(int Index, int Core, int Cache, int Eff, int Sched)>();
        GetSystemCpuSetInformation(IntPtr.Zero, 0, out var length, IntPtr.Zero, 0);
        if (length > 0)
        {
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (GetSystemCpuSetInformation(buffer, length, out length, IntPtr.Zero, 0))
                    for (var offset = 0; offset + 24 <= length;)
                    {
                        var p = buffer + offset;
                        var size = Marshal.ReadInt32(p);
                        if (Marshal.ReadInt32(p, 4) == 0 && Marshal.ReadInt16(p, 12) == 0 && Marshal.ReadByte(p, 14) < 64)
                            cpus.Add((Marshal.ReadByte(p, 14), Marshal.ReadByte(p, 15), Marshal.ReadByte(p, 16), Marshal.ReadByte(p, 18), Marshal.ReadByte(p, 20)));
                        if (size <= 0) break;
                        offset += size;
                    }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        if (cpus.Count == 0) cpus.AddRange(Enumerable.Range(0, Math.Min(Environment.ProcessorCount, 64)).Select(i => (i, i, 0, 0, 0)));
        var l3 = L3Sizes();
        return cpus.GroupBy(c => c.Core)
            .Select(g => new CpuCore(g.Min(c => c.Index), g.Aggregate(0UL, (m, c) => m | 1UL << c.Index), g.Max(c => c.Eff), g.Max(c => c.Sched),
                g.First().Cache, l3.FirstOrDefault(x => (x.Mask >> g.Min(c => c.Index) & 1) == 1).Size))
            .OrderBy(c => c.FirstCpu).ToList();
    }

    /// <summary>Level-3 caches of group 0 from GetLogicalProcessorInformationEx(RelationCache): CacheSize @12, Mask @40, Group @48.</summary>
    static List<(ulong Mask, long Size)> L3Sizes()
    {
        const int RelationCache = 2;
        var list = new List<(ulong, long)>();
        var length = 0;
        GetLogicalProcessorInformationEx(RelationCache, IntPtr.Zero, ref length);
        if (length <= 0) return list;
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationCache, buffer, ref length)) return list;
            for (var offset = 0; offset + 50 <= length;)
            {
                var p = buffer + offset;
                if (Marshal.ReadInt32(p) == RelationCache && Marshal.ReadByte(p, 8) == 3 && Marshal.ReadInt16(p, 48) == 0)
                    list.Add(((ulong)Marshal.ReadInt64(p, 40), (uint)Marshal.ReadInt32(p, 12)));
                var size = Marshal.ReadInt32(p, 4);
                if (size <= 0) break;
                offset += size;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return list;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetSystemCpuSetInformation(IntPtr information, int bufferLength, out int returnedLength, IntPtr process, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref int returnedLength);
}
