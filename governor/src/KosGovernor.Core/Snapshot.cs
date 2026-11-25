using System.Runtime.InteropServices;

namespace KosGovernor.Core;

public sealed record ThreadEntry(int Tid, long CreateTime, int Priority, int BasePriority, int State, int WaitReason, uint ContextSwitches = 0)
{
    /// <summary>State 5 (Waiting) with reason 5 (Suspended).</summary>
    public bool Suspended => State == 5 && WaitReason == 5;
}

/// <param name="CpuTime">User plus kernel time so far (100 ns units): the CPU use is the change between two snapshots.</param>
/// <param name="PrivateWorkingSet">Private working set in bytes (Task Manager's "Memory" column).</param>
public sealed record ProcessEntry(int Pid, int ParentPid, string Name, long CreateTime, int BasePriority, int HandleCount, int SessionId,
    long WorkingSet, IReadOnlyList<ThreadEntry> Threads, long CpuTime = 0, long PrivateWorkingSet = 0)
{
    public bool Suspended => Threads.Count > 0 && Threads.All(t => t.Suspended);
}

/// <summary>
/// List of processes and threads in a single call (NtQuerySystemInformation), without opening each process: it's what the engine
/// and the interface read every interval.
/// </summary>
public static class Snapshot
{
    // x64 layout of SYSTEM_PROCESS_INFORMATION (256 bytes) and SYSTEM_THREAD_INFORMATION (80 bytes).
    const int ProcThreads = 4, ProcPrivateWs = 8, ProcCreate = 32, ProcUser = 40, ProcKernel = 48, ProcName = 56, ProcBasePriority = 72, ProcPid = 80, ProcParent = 88, ProcHandles = 96,
        ProcSession = 100, ProcWorkingSet = 144, ProcSize = 256;
    const int ThrCreate = 16, ThrTid = 48, ThrPriority = 56, ThrBase = 60, ThrSwitches = 64, ThrState = 68, ThrWait = 72, ThrSize = 80;

    static readonly object Gate = new();
    static IntPtr _buffer;
    static int _size;

    public static List<ProcessEntry> Take()
    {
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("KOS Governor needs 64-bit Windows.");
        lock (Gate)
        {
            if (_size == 0) Grow(1 << 20);
            int status;
            while ((status = Native.NtQuerySystemInformation(Native.SystemProcessInformation, _buffer, _size, out var needed)) == Native.StatusInfoLengthMismatch)
                Grow(Math.Max(needed + (64 << 10), _size * 2));
            if (status < 0) throw new InvalidOperationException($"Reading the process list failed: {Native.NtMessage(status)}");
            return Parse(_buffer);
        }
    }

    static void Grow(int size)
    {
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
        _buffer = Marshal.AllocHGlobal(size);
        _size = size;
    }

    static List<ProcessEntry> Parse(IntPtr p)
    {
        var list = new List<ProcessEntry>(256);
        while (true)
        {
            var count = Marshal.ReadInt32(p, ProcThreads);
            var pid = (int)Marshal.ReadInt64(p, ProcPid);
            var nameLength = (ushort)Marshal.ReadInt16(p, ProcName);
            var namePtr = Marshal.ReadIntPtr(p, ProcName + 8);
            var name = pid == 0 ? "System Idle Process" : namePtr == IntPtr.Zero ? "" : Marshal.PtrToStringUni(namePtr, nameLength / 2);
            var threads = new ThreadEntry[count];
            for (var i = 0; i < count; i++)
            {
                var t = p + ProcSize + i * ThrSize;
                threads[i] = new ThreadEntry((int)Marshal.ReadInt64(t, ThrTid), Marshal.ReadInt64(t, ThrCreate), Marshal.ReadInt32(t, ThrPriority),
                    Marshal.ReadInt32(t, ThrBase), Marshal.ReadInt32(t, ThrState), Marshal.ReadInt32(t, ThrWait), (uint)Marshal.ReadInt32(t, ThrSwitches));
            }
            list.Add(new ProcessEntry(pid, (int)Marshal.ReadInt64(p, ProcParent), name, Marshal.ReadInt64(p, ProcCreate), Marshal.ReadInt32(p, ProcBasePriority),
                Marshal.ReadInt32(p, ProcHandles), Marshal.ReadInt32(p, ProcSession), Marshal.ReadInt64(p, ProcWorkingSet), threads,
                Marshal.ReadInt64(p, ProcUser) + Marshal.ReadInt64(p, ProcKernel), Marshal.ReadInt64(p, ProcPrivateWs)));
            var next = Marshal.ReadInt32(p);
            if (next == 0) return list;
            p += next;
        }
    }
}

/// <summary>Total CPU use between two reads and memory (status bar and tray tooltip).</summary>
public sealed class SystemLoad
{
    long _idle, _total;

    /// <summary>Percentage since the previous call (the first returns 0).</summary>
    public double CpuPercent()
    {
        if (!Native.GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var total = kernel + user; // kernel time already includes idle
        var (di, dt) = (idle - _idle, total - _total);
        var first = _total == 0;
        (_idle, _total) = (idle, total);
        return first || dt <= 0 ? 0 : Math.Clamp(100.0 * (dt - di) / dt, 0, 100);
    }

    long[]? _coreIdle, _coreTotal;

    /// <summary>
    /// Each logical processor's use since the previous call (null on the first, and when Windows doesn't answer): SystemProcessorPerformanceInformation,
    /// one 48-byte record per processor (idle, kernel including idle, user, DPC, interrupt times).
    /// </summary>
    public float[]? CoreLoads()
    {
        const int InfoClass = 8, Record = 48;
        var count = Environment.ProcessorCount;
        var buffer = Marshal.AllocHGlobal(count * Record);
        try
        {
            if (Native.NtQuerySystemInformation(InfoClass, buffer, count * Record, out var returned) < 0) return null;
            var n = Math.Min(count, returned / Record);
            var idle = new long[n];
            var total = new long[n];
            for (var i = 0; i < n; i++)
            {
                idle[i] = Marshal.ReadInt64(buffer, i * Record);
                total[i] = Marshal.ReadInt64(buffer, i * Record + 8) + Marshal.ReadInt64(buffer, i * Record + 16);
            }
            var (pi, pt) = (_coreIdle, _coreTotal);
            (_coreIdle, _coreTotal) = (idle, total);
            if (pi is null || pt is null || pi.Length != n) return null;
            var loads = new float[n];
            for (var i = 0; i < n; i++)
            {
                var (di, dt) = (idle[i] - pi[i], total[i] - pt[i]);
                loads[i] = dt <= 0 ? 0 : (float)Math.Clamp(100.0 * (dt - di) / dt, 0, 100);
            }
            return loads;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static (uint LoadPercent, ulong Used, ulong Total) Memory()
    {
        var m = new Native.MemoryStatus { Length = (uint)Marshal.SizeOf<Native.MemoryStatus>() };
        return Native.GlobalMemoryStatusEx(ref m) ? (m.MemoryLoad, m.TotalPhys - m.AvailPhys, m.TotalPhys) : (0, 0, 0);
    }
}
