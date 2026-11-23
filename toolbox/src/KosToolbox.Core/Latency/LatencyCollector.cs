using System.Buffers.Binary;

namespace KosToolbox.Core.Latency;

/// <summary>Count, longest and total run time of a routine kind, in QPC ticks.</summary>
public struct RunStats
{
    public long Count, Max, Total;
    public void Add(long ticks) { Count++; Total += ticks; if (ticks > Max) Max = ticks; }
}

/// <summary>Count, longest run (µs) and total time (ms).</summary>
public sealed record RunTimes(long Count, double MaxUs, double TotalMs);

/// <summary>How many ISRs and DPCs of a driver ran on one logical processor.</summary>
public sealed record CpuShare(int Cpu, long Count);

/// <summary>
/// ISR and DPC times of one driver or one logical processor. <paramref name="OnCpus"/> (drivers only): the processors its routines
/// ran on, most routines first.
/// </summary>
public sealed record LatencyRow(string Name, RunTimes Isr, RunTimes Dpc, IReadOnlyList<CpuShare>? OnCpus = null)
{
    public double MaxUs => Math.Max(Isr.MaxUs, Dpc.MaxUs);
}

public sealed record FaultRow(string Process, long Count);

public sealed record LatencyReport(TimeSpan Elapsed, long TimerSamples, double TimerMaxUs, double TimerAverageUs,
    IReadOnlyList<LatencyRow> Drivers, IReadOnlyList<LatencyRow> Cpus, long HardFaults, IReadOnlyList<FaultRow> Faults);

/// <summary>
/// Adds up the kernel's DPC, ISR and hard pagefault events and the timer wake-up delays of a measurement. Events come from
/// the trace thread, <see cref="Report"/> from the interface: both go through one lock.
/// </summary>
public sealed class LatencyCollector(KernelModules modules, long frequency)
{
    // SystemTraceProvider classes: PerfInfo (DPC 66 threaded, 67 ISR, 68 DPC, 69 timer DPC) and PageFault_V2 (32 hard fault).
    public static readonly Guid PerfInfo = new("ce1dbfb4-137e-4da6-87b0-3f59aa102cbc"), PageFault = new("3d6fa8d3-fe05-11d0-9dda-00c04fd7ba7c");
    public const byte ThreadedDpc = 66, Isr = 67, Dpc = 68, TimerDpc = 69, HardFault = 32;

    sealed class Pair { public RunStats Isr, Dpc; }

    readonly object _gate = new();
    readonly Dictionary<string, Pair> _drivers = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, Pair> _cpus = new();
    readonly Dictionary<string, HashSet<int>> _driverCpus = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Dictionary<int, long>> _driverCpuCounts = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, long> _faults = new();
    RunStats _timer;

    /// <summary>Whether <see cref="Add"/> uses an event; lets the trace skip copying the others' payload.</summary>
    public static bool Wanted(Guid provider, byte opcode) =>
        provider == PerfInfo ? opcode is ThreadedDpc or Isr or Dpc or TimerDpc : provider == PageFault && opcode == HardFault;

    /// <summary>
    /// One kernel event: <paramref name="timestamp"/> is when the routine ended (raw QPC) and the payload starts with
    /// when it began, then the routine's address (x64). A hard fault carries the faulting thread at offset 32.
    /// </summary>
    public void Add(Guid provider, byte opcode, int cpu, long timestamp, ReadOnlySpan<byte> data, Func<uint, int> processOfThread)
    {
        if (provider == PerfInfo && opcode is ThreadedDpc or Isr or Dpc or TimerDpc && data.Length >= 16)
        {
            var ticks = timestamp - BinaryPrimitives.ReadInt64LittleEndian(data);
            if (ticks < 0) return;
            var driver = modules.Find(BinaryPrimitives.ReadUInt64LittleEndian(data[8..]));
            lock (_gate)
            {
                if (!_drivers.TryGetValue(driver, out var d)) _drivers[driver] = d = new();
                if (!_cpus.TryGetValue(cpu, out var c)) _cpus[cpu] = c = new();
                if (!_driverCpus.TryGetValue(driver, out var on)) _driverCpus[driver] = on = new();
                on.Add(cpu);
                if (!_driverCpuCounts.TryGetValue(driver, out var counts)) _driverCpuCounts[driver] = counts = new();
                counts[cpu] = counts.GetValueOrDefault(cpu) + 1;
                if (opcode == Isr) { d.Isr.Add(ticks); c.Isr.Add(ticks); }
                else { d.Dpc.Add(ticks); c.Dpc.Add(ticks); }
            }
        }
        else if (provider == PageFault && opcode == HardFault && data.Length >= 36)
        {
            var pid = processOfThread(BinaryPrimitives.ReadUInt32LittleEndian(data[32..]));
            lock (_gate) _faults[pid] = _faults.GetValueOrDefault(pid) + 1;
        }
    }

    /// <summary>How long a thread took to run after its timer's interrupt, in 100 ns (interrupt time).</summary>
    public void Timer(long late) { lock (_gate) _timer.Add(Math.Max(0, late)); }

    /// <summary>Totals so far: drivers by longest run, processors in order, processes by hard faults (same name added up).</summary>
    public LatencyReport Report(TimeSpan elapsed, Func<int, string> processName)
    {
        List<LatencyRow> drivers, cpus;
        List<KeyValuePair<int, long>> faults;
        RunStats timer;
        lock (_gate)
        {
            drivers = _drivers.Select(d => Row(d.Key, d.Value) with
            {
                OnCpus = _driverCpuCounts.TryGetValue(d.Key, out var counts)
                    ? counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key).Select(c => new CpuShare(c.Key, c.Value)).ToList() : null,
            }).OrderByDescending(r => r.MaxUs).ToList();
            cpus = _cpus.OrderBy(c => c.Key).Select(c => Row($"CPU {c.Key}", c.Value)).ToList();
            faults = _faults.ToList();
            timer = _timer;
        }
        var byName = faults.GroupBy(f => processName(f.Key), StringComparer.OrdinalIgnoreCase)
            .Select(g => new FaultRow(g.Key, g.Sum(f => f.Value))).OrderByDescending(f => f.Count).ToList();
        return new LatencyReport(elapsed, timer.Count, timer.Max / 10.0, timer.Count == 0 ? 0 : timer.Total / 10.0 / timer.Count,
            drivers, cpus, faults.Sum(f => f.Value), byName);
    }

    /// <summary>The processors any of <paramref name="drivers"/> ran an ISR or DPC on (names with or without ".sys").</summary>
    public HashSet<int> CpusOf(IEnumerable<string> drivers)
    {
        var names = new HashSet<string>(drivers.Select(Bare), StringComparer.OrdinalIgnoreCase);
        lock (_gate) return _driverCpus.Where(d => names.Contains(Bare(d.Key))).SelectMany(d => d.Value).ToHashSet();
    }

    static string Bare(string driver) => driver.EndsWith(".sys", StringComparison.OrdinalIgnoreCase) ? driver[..^4] : driver;

    /// <summary>ISR and DPC time per processor so far, in ms (the governor's thread director avoids the busiest ones).</summary>
    public Dictionary<int, double> CpuMs()
    {
        lock (_gate) return _cpus.ToDictionary(c => c.Key, c => Us(c.Value.Isr.Total + c.Value.Dpc.Total) / 1000);
    }

    LatencyRow Row(string name, Pair p) => new(name, Times(p.Isr), Times(p.Dpc));
    RunTimes Times(RunStats s) => new(s.Count, Us(s.Max), Us(s.Total) / 1000);
    double Us(long ticks) => ticks * 1e6 / frequency;
}
