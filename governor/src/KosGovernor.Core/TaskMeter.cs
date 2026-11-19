using System.Runtime.InteropServices;

namespace KosGovernor.Core;

/// <summary>The engine's tasks, as the status window lists them.</summary>
public enum EngineTask { Rules, FanCurve, GpuProfile, Peripherals, Rtss, Tray }

/// <summary>
/// CPU and memory per engine task, for the status window. Each task's work runs inside <see cref="Measure"/>: the scope reads the
/// thread's cycle counter and the process's private bytes before and after, and books the difference to the task (a scope inside
/// another one is taken out of the outer one). With the window closed (<see cref="On"/> false) a scope costs one flag read, except
/// a task's first run, which is always measured: that is the memory its start-up took.
/// The CPU figure is exact (cycles). The memory figure is an estimate: private bytes are per process, so what other threads
/// allocate during a scope lands in it too, and memory a task took and gave back still counts; the status lines scale the
/// estimates down to what the process really holds above its base.
/// </summary>
public sealed class TaskMeter
{
    static readonly int Count = Enum.GetValues<EngineTask>().Length;
    static readonly long[] Cycles = new long[Count], Bytes = new long[Count];
    static readonly bool[] Seen = new bool[Count];
    [ThreadStatic] static long t_childCycles, t_childBytes;

    /// <summary>The status window is open.</summary>
    public static volatile bool On;

    /// <summary>The current thread's cycle count and the process's private bytes; tests replace them.</summary>
    internal static Func<long> ThreadCycles = () => QueryThreadCycleTime(GetCurrentThread(), out var c) ? (long)c : 0;
    internal static Func<long> PrivateBytes = ProcessPrivateBytes;

    public static Scope Measure(EngineTask task)
    {
        var i = (int)task;
        if (!On && Seen[i]) return default;
        Seen[i] = true;
        var scope = new Scope(i, ThreadCycles(), PrivateBytes(), t_childCycles, t_childBytes);
        (t_childCycles, t_childBytes) = (0, 0);
        return scope;
    }

    public readonly struct Scope : IDisposable
    {
        readonly int _task;
        readonly long _cycles, _bytes, _outerCycles, _outerBytes;
        readonly bool _active;

        internal Scope(int task, long cycles, long bytes, long outerCycles, long outerBytes) =>
            (_task, _cycles, _bytes, _outerCycles, _outerBytes, _active) = (task, cycles, bytes, outerCycles, outerBytes, true);

        public void Dispose()
        {
            if (!_active) return;
            long cycles = ThreadCycles() - _cycles, bytes = PrivateBytes() - _bytes;
            Interlocked.Add(ref Cycles[_task], cycles - t_childCycles);
            Interlocked.Add(ref Bytes[_task], bytes - t_childBytes);
            // What this scope took counts as a child of the scope around it
            (t_childCycles, t_childBytes) = (_outerCycles + cycles, _outerBytes + bytes);
        }
    }

    /// <summary>One task's share: percent of all processors since the previous sample, and its estimated memory (never negative).</summary>
    public readonly record struct Usage(double CpuPercent, long Bytes);

    /// <summary>
    /// The whole engine since the previous sample, then each task in <see cref="EngineTask"/> order. <paramref name="BaseBytes"/>: the
    /// private bytes before any task started (the runtime), which no task's estimate may eat into.
    /// </summary>
    public sealed record Sample(double CpuPercent, long PrivateBytes, Usage[] Tasks, long BaseBytes = 0);

    static long _base;
    /// <summary>Engine start, before the tasks: what the process holds with nothing running yet.</summary>
    public static void MarkBase() => _base = ProcessPrivateBytes();

    long[] _lastCycles = new long[Count];
    long _lastProcessCycles, _lastProcessTime, _lastWall;

    // Sampling state lives in an instance so the window owns it; the counters above are the engine's.
    TaskMeter() { }
    public static TaskMeter Start()
    {
        var m = new TaskMeter();
        m.Take(); // the first real sample measures from here
        return m;
    }

    /// <summary>The usage since the previous call.</summary>
    public Sample Take()
    {
        var cycles = new long[Count];
        var bytes = new long[Count];
        for (var i = 0; i < Count; i++) (cycles[i], bytes[i]) = (Interlocked.Read(ref Cycles[i]), Interlocked.Read(ref Bytes[i]));
        QueryProcessCycleTime(GetCurrentProcess(), out var processCycles);
        GetProcessTimes(GetCurrentProcess(), out _, out _, out var kernel, out var user);
        long processTime = kernel + user, wall = System.Diagnostics.Stopwatch.GetTimestamp();
        var wallTicks = (wall - _lastWall) * (double)TimeSpan.TicksPerSecond / System.Diagnostics.Stopwatch.Frequency;
        var cpu = wallTicks <= 0 ? 0 : (processTime - _lastProcessTime) / wallTicks / Environment.ProcessorCount * 100;
        var deltas = cycles.Zip(_lastCycles, (now, before) => now - before).ToArray();
        var shares = Shares(deltas, (long)processCycles - _lastProcessCycles, cpu);
        (_lastCycles, _lastProcessCycles, _lastProcessTime, _lastWall) = (cycles, (long)processCycles, processTime, wall);
        return new Sample(cpu, ProcessPrivateBytes(), shares.Select((s, i) => new Usage(s, Math.Max(0, bytes[i]))).ToArray(), _base);
    }

    /// <summary>Splits the process's CPU percent by each task's part of the process's cycles.</summary>
    internal static double[] Shares(long[] taskCycles, long processCycles, double processPercent) =>
        taskCycles.Select(c => processCycles <= 0 ? 0 : Math.Clamp(processPercent * c / processCycles, 0, processPercent)).ToArray();

    /// <summary>Tests: the counters back to zero.</summary>
    internal static void Reset()
    {
        Array.Clear(Cycles);
        Array.Clear(Bytes);
        Array.Clear(Seen);
        (t_childCycles, t_childBytes) = (0, 0);
    }

    internal static long TaskCycles(EngineTask t) => Interlocked.Read(ref Cycles[(int)t]);
    internal static long TaskBytes(EngineTask t) => Interlocked.Read(ref Bytes[(int)t]);

    static long ProcessPrivateBytes()
    {
        var c = new MemoryCounters { cb = Marshal.SizeOf<MemoryCounters>() };
        return GetProcessMemoryInfo(GetCurrentProcess(), ref c, c.cb) ? (long)c.PrivateUsage : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryCounters
    {
        public int cb, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage,
            PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }

    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);
    [DllImport("kernel32.dll")] static extern bool QueryProcessCycleTime(IntPtr process, out ulong cycles);
    [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("psapi.dll")] static extern bool GetProcessMemoryInfo(IntPtr process, ref MemoryCounters counters, int size);
}
