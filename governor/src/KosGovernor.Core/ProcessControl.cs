using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace KosGovernor.Core;

/// <summary>What can be read from a process right now. Null = couldn't read (protected process, access denied).</summary>
public sealed class ProcessDetails
{
    public string? ImagePath { get; init; }
    public CpuPriority? Priority { get; init; }
    public IoPriority? Io { get; init; }
    public MemoryPriority? Memory { get; init; }
    public bool? Boost { get; init; }
    public Efficiency? Efficiency { get; init; }
    public ulong? Affinity { get; init; }
    /// <summary>Mask of the default CPU sets; 0 = none set (all).</summary>
    public ulong? CpuSets { get; init; }
}

/// <summary>Reading and changing processes (priority, I/O, memory, boost, efficiency, affinity, CPU sets, suspend, terminate).</summary>
public static class ProcessControl
{
    static readonly uint[] PriorityClasses = { 0x40, 0x4000, 0x20, 0x8000, 0x80, 0x100 }; // CpuPriority order

    /// <summary>SeDebug opens processes of other users and services; SeIncreaseBasePriority allows high I/O and Realtime.</summary>
    public static void EnablePrivileges() => Native.EnablePrivileges("SeDebugPrivilege", "SeIncreaseBasePriorityPrivilege");

    public static ProcessDetails Read(int pid, bool withPath, CpuTopology topo)
    {
        using var h = Open(pid, Native.ProcessQueryLimited | Native.ProcessQueryInformation) ?? Open(pid, Native.ProcessQueryLimited);
        if (h is null) return new ProcessDetails();
        string? path = null;
        if (withPath)
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            if (Native.QueryFullProcessImageName(h, 0, sb, ref size)) path = sb.ToString();
        }
        var cls = Native.GetPriorityClass(h);
        var io = 0;
        uint memory = 0;
        var throttling = new Native.ProcessPowerThrottling { Version = 1 };
        return new ProcessDetails
        {
            ImagePath = path,
            Priority = Array.IndexOf(PriorityClasses, cls) is >= 0 and var i ? (CpuPriority)i : null,
            Io = Native.NtQueryInformationProcess(h, Native.ProcessIoPriority, ref io, 4, out _) >= 0 && io is >= 0 and <= 3 ? (IoPriority)io : null,
            Memory = Native.GetProcessInformation(h, Native.ProcessMemoryPriority, ref memory, 4) && memory is >= 1 and <= 5 ? (MemoryPriority)(memory - 1) : null,
            Boost = Native.GetProcessPriorityBoost(h, out var disabled) ? !disabled : null,
            Efficiency = Native.GetProcessInformation(h, Native.ProcessPowerThrottlingClass, ref throttling, Marshal.SizeOf<Native.ProcessPowerThrottling>())
                ? Decode(throttling) : null,
            Affinity = Native.GetProcessAffinityMask(h, out var mask, out _) ? (ulong)mask : null,
            CpuSets = ReadCpuSets(h, topo),
        };
    }

    internal static ulong? ReadCpuSets(SafeProcessHandle h, CpuTopology topo)
    {
        // With no CPU sets set the call succeeds with 0; with some, it fails asking for space (122) and says how much.
        if (Native.GetProcessDefaultCpuSets(h, null, 0, out var required)) return 0;
        if (Marshal.GetLastWin32Error() != 122 || required == 0) return null;
        var ids = new uint[required];
        return Native.GetProcessDefaultCpuSets(h, ids, required, out _) ? topo.MaskFromCpuSetIds(ids) : null;
    }

    /// <summary>Executable path (for icon, folder and properties).</summary>
    public static string? ImagePath(int pid)
    {
        using var h = Open(pid, Native.ProcessQueryLimited);
        if (h is null) return null;
        var sb = new StringBuilder(1024);
        var size = sb.Capacity;
        return Native.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
    }

    /// <summary>
    /// Applies the action and the rule's adjustments; returns what failed (empty = all fine). Continues after a failure.
    /// <paramref name="created"/> (the start time from the snapshot) makes sure the pid wasn't reused by another process since.
    /// </summary>
    public static List<string> Apply(int pid, ProcessLevelRule r, IReadOnlyList<CpuMask> masks, CpuTopology topo, long? created = null)
    {
        var errors = new List<string>();
        var access = Native.ProcessQueryLimited | Native.ProcessSetInformation
                     | (r.Action == RuleAction.Suspend ? Native.ProcessSuspendResume : 0) | (r.Action == RuleAction.Terminate ? Native.ProcessTerminate : 0);
        using var h = Open(pid, access);
        if (h is null) { errors.Add($"can't open the process: {Native.Win32Message(Marshal.GetLastWin32Error())}"); return errors; }
        if (created is { } start && !(Native.GetProcessTimes(h, out var creation, out _, out _, out _) && creation == start))
        {
            errors.Add("the process closed (its pid now belongs to another one)");
            return errors;
        }

        if (r.Action is RuleAction.Terminate or RuleAction.Suspend)
        {
            if (Native.IsProcessCritical(h, out var critical) && critical) { errors.Add("it's a critical Windows process"); return errors; }
            if (r.Action == RuleAction.Terminate) Check(Native.TerminateProcess(h, 1), "terminate");
            else Nt(Native.NtSuspendProcess(h), "suspend");
            return errors;
        }
        if (r.Priority is { } p) Check(Native.SetPriorityClass(h, PriorityClasses[(int)p]), "CPU priority");
        if (r.Io is { } io) { var v = (int)io; Nt(Native.NtSetInformationProcess(h, Native.ProcessIoPriority, ref v, 4), "I/O priority"); }
        if (r.Memory is { } m) { var v = (uint)m + 1; Check(Native.SetProcessInformation(h, Native.ProcessMemoryPriority, ref v, 4), "memory priority"); }
        if (r.Boost is { } b) Check(Native.SetProcessPriorityBoost(h, !b), "priority boost");
        if (r.Efficiency is { } e)
        {
            var s = Encode(e);
            Check(Native.SetProcessInformation(h, Native.ProcessPowerThrottlingClass, ref s, Marshal.SizeOf<Native.ProcessPowerThrottling>()), "efficiency mode");
        }
        if (r.GpuPriority is { } gpu) Nt(Native.D3DKMTSetProcessSchedulingPriorityClass(h, (int)gpu), "GPU priority");
        if (r.Affinity is { } a)
        {
            if (CpuSpec.Resolve(a, topo, masks, out var error) is { } mask) Check(Native.SetProcessAffinityMask(h, (UIntPtr)mask), "affinity");
            else errors.Add($"affinity: {error}");
        }
        if (r.CpuSets is { } c)
        {
            if (CpuSpec.Resolve(c, topo, masks, out var error) is { } mask)
            {
                // All = no CPU sets set (the process goes back to the system default).
                var ids = mask == topo.AllMask ? null : topo.CpuSetIds(mask);
                Check(Native.SetProcessDefaultCpuSets(h, ids, (uint)(ids?.Length ?? 0)), "CPU sets");
            }
            else errors.Add($"CPU sets: {error}");
        }
        return errors;

        void Check(bool ok, string what) { if (!ok) errors.Add($"{what}: {Native.Win32Message(Marshal.GetLastWin32Error())}"); }
        void Nt(int status, string what) { if (status < 0) errors.Add($"{what}: {Native.NtMessage(status)}"); }
    }

    static Efficiency Decode(Native.ProcessPowerThrottling s) =>
        (s.ControlMask & 1) == 0 ? Efficiency.Auto : (s.StateMask & 1) == 1 ? Efficiency.On : Efficiency.Off;

    static Native.ProcessPowerThrottling Encode(Efficiency e) =>
        new() { Version = 1, ControlMask = e == Efficiency.Auto ? 0u : 1u, StateMask = e == Efficiency.On ? 1u : 0u };

    /// <summary>
    /// Efficiency mode from <paramref name="from"/> to <paramref name="to"/>. False (nothing changed) when the pid no longer belongs to the
    /// process that started at <paramref name="created"/>, it can't be opened, or it isn't in <paramref name="from"/> (the program or the user
    /// chose another mode).
    /// </summary>
    public static bool SwitchEfficiency(int pid, long created, Efficiency from, Efficiency to)
    {
        using var h = Open(pid, Native.ProcessQueryLimited | Native.ProcessSetInformation);
        if (h is null || !(Native.GetProcessTimes(h, out var creation, out _, out _, out _) && creation == created)) return false;
        var s = new Native.ProcessPowerThrottling { Version = 1 };
        var size = Marshal.SizeOf<Native.ProcessPowerThrottling>();
        if (!Native.GetProcessInformation(h, Native.ProcessPowerThrottlingClass, ref s, size) || Decode(s) != from) return false;
        s = Encode(to);
        return Native.SetProcessInformation(h, Native.ProcessPowerThrottlingClass, ref s, size);
    }

    public static void Resume(int pid)
    {
        using var h = Open(pid, Native.ProcessSuspendResume) ?? throw LastError("can't open the process");
        if (Native.NtResumeProcess(h) is var status and < 0) throw new InvalidOperationException(Native.NtMessage(status));
    }

    internal static SafeProcessHandle? Open(int pid, uint access)
    {
        var h = Native.OpenProcess(access, false, pid);
        if (!h.IsInvalid) return h;
        h.Dispose();
        return null;
    }

    static Exception LastError(string what) => new InvalidOperationException($"{what}: {Native.Win32Message(Marshal.GetLastWin32Error())}");
}

/// <summary>A thread read now. Relative priority as GetThreadPriority returns it (-15 to 15).</summary>
public sealed class ThreadDetails
{
    public string? Description { get; init; }
    public int? Priority { get; init; }
    public IoPriority? Io { get; init; }
    public MemoryPriority? Memory { get; init; }
    public bool? Boost { get; init; }
    public (int Group, int Number)? Ideal { get; init; }
    public ulong? Affinity { get; init; }
    /// <summary>Mask of the chosen CPU sets; 0 = none (uses the process's).</summary>
    public ulong? CpuSets { get; init; }
    public IntPtr StartAddress { get; init; }
}

public static class ThreadControl
{
    public static readonly int[] Priorities = { -15, -2, -1, 0, 1, 2, 15 }; // ThreadPriority order

    public static ThreadDetails Read(int tid, CpuTopology topo)
    {
        using var h = Open(tid, Native.ThreadQueryLimited | Native.ThreadQueryInformation) ?? Open(tid, Native.ThreadQueryLimited);
        if (h is null) return new ThreadDetails();
        var io = 0;
        uint memory = 0;
        var basic = new Native.ThreadBasicInformation();
        var start = IntPtr.Zero;
        var priority = Native.GetThreadPriority(h);
        return new ThreadDetails
        {
            Description = Description(h),
            Priority = priority == 0x7fffffff ? null : priority,
            Io = Native.NtQueryInformationThread(h, Native.ThreadIoPriority, ref io, 4, out _) >= 0 && io is >= 0 and <= 3 ? (IoPriority)io : null,
            Memory = Native.GetThreadInformation(h, Native.ThreadMemoryPriority, ref memory, 4) && memory is >= 1 and <= 5 ? (MemoryPriority)(memory - 1) : null,
            Boost = Native.GetThreadPriorityBoost(h, out var disabled) ? !disabled : null,
            Ideal = Native.GetThreadIdealProcessorEx(h, out var n) ? (n.Group, n.Number) : null,
            Affinity = Native.NtQueryInformationThread(h, Native.ThreadBasicInformationClass, ref basic, Marshal.SizeOf<Native.ThreadBasicInformation>(), out _) >= 0
                ? (ulong)basic.AffinityMask : null,
            CpuSets = ReadCpuSets(h, topo),
            StartAddress = Native.NtQueryInformationThread(h, Native.ThreadQuerySetWin32StartAddress, ref start, IntPtr.Size, out _) >= 0 ? start : IntPtr.Zero,
        };
    }

    internal static ulong? ReadCpuSets(SafeAccessTokenHandle h, CpuTopology topo)
    {
        if (Native.GetThreadSelectedCpuSets(h, null, 0, out var required)) return 0;
        if (Marshal.GetLastWin32Error() != 122 || required == 0) return null;
        var ids = new uint[required];
        return Native.GetThreadSelectedCpuSets(h, ids, required, out _) ? topo.MaskFromCpuSetIds(ids) : null;
    }

    /// <summary>Description only (the engine checks it every interval on threads that still have no rule).</summary>
    public static string? Description(int tid)
    {
        using var h = Open(tid, Native.ThreadQueryLimited);
        return h is null ? null : Description(h);
    }

    static string? Description(SafeAccessTokenHandle h)
    {
        if (Native.GetThreadDescription(h, out var p) < 0 || p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p) is { Length: > 0 } s ? s : null; }
        finally { Native.LocalFree(p); }
    }

    public static IntPtr StartAddress(int tid)
    {
        using var h = Open(tid, Native.ThreadQueryInformation);
        var start = IntPtr.Zero;
        return h is not null && Native.NtQueryInformationThread(h, Native.ThreadQuerySetWin32StartAddress, ref start, IntPtr.Size, out _) >= 0 ? start : IntPtr.Zero;
    }

    /// <summary>
    /// Applies the action and adjustments; <paramref name="processAffinity"/> limits the affinity (outside it Windows refuses). Nothing
    /// is done when the thread id now belongs to a thread other than the one that started at <paramref name="created"/>.
    /// </summary>
    public static List<string> Apply(int tid, long created, ThreadRule r, ulong processAffinity, IReadOnlyList<CpuMask> masks, CpuTopology topo)
    {
        var errors = new List<string>();
        var access = Native.ThreadQueryLimited | Native.ThreadSetInformation | Native.ThreadSetLimited
                     | (r.Action == RuleAction.Suspend ? Native.ThreadSuspendResume : 0) | (r.Action == RuleAction.Terminate ? Native.ThreadTerminate : 0);
        using var h = Open(tid, access);
        if (h is null) { errors.Add($"can't open the thread: {Native.Win32Message(Marshal.GetLastWin32Error())}"); return errors; }
        if (!(Native.GetThreadTimes(h, out var creation, out _, out _, out _) && creation == created)) return errors; // ended, id reused
        if (r.Action == RuleAction.Terminate) { Check(Native.TerminateThread(h, 1), "terminate"); return errors; }
        if (r.Action == RuleAction.Suspend) { Check(Native.SuspendThread(h) != uint.MaxValue, "suspend"); return errors; }
        if (r.Priority is { } p) Check(Native.SetThreadPriority(h, Priorities[(int)p]), "priority");
        if (r.Io is { } io) { var v = (int)io; Nt(Native.NtSetInformationThread(h, Native.ThreadIoPriority, ref v, 4), "I/O priority"); }
        if (r.Memory is { } m) { var v = (uint)m + 1; Check(Native.SetThreadInformation(h, Native.ThreadMemoryPriority, ref v, 4), "memory priority"); }
        if (r.Boost is { } b) Check(Native.SetThreadPriorityBoost(h, !b), "priority boost");
        if (r.IdealCpu is { } cpu)
        {
            var n = new Native.ProcessorNumber { Group = 0, Number = (byte)cpu };
            Check(Native.SetThreadIdealProcessorEx(h, ref n, out _), "ideal processor");
        }
        if (r.Affinity is { } a)
        {
            if (CpuSpec.Resolve(a, topo, masks, out var error) is { } mask)
            {
                mask &= processAffinity == 0 ? topo.AllMask : processAffinity;
                if (mask == 0) errors.Add("affinity: none of these processors are allowed for the process");
                else Check(Native.SetThreadAffinityMask(h, (UIntPtr)mask) != UIntPtr.Zero, "affinity");
            }
            else errors.Add($"affinity: {error}");
        }
        if (r.CpuSets is { } c)
        {
            if (CpuSpec.Resolve(c, topo, masks, out var error) is { } mask)
            {
                var ids = mask == 0 ? null : topo.CpuSetIds(mask); // "none" = back to the process's CPU sets
                Check(Native.SetThreadSelectedCpuSets(h, ids, (uint)(ids?.Length ?? 0)), "CPU sets");
            }
            else errors.Add($"CPU sets: {error}");
        }
        return errors;

        void Check(bool ok, string what) { if (!ok) errors.Add($"{what}: {Native.Win32Message(Marshal.GetLastWin32Error())}"); }
        void Nt(int status, string what) { if (status < 0) errors.Add($"{what}: {Native.NtMessage(status)}"); }
    }

    public static void Resume(int tid)
    {
        using var h = Open(tid, Native.ThreadSuspendResume) ?? throw new InvalidOperationException($"can't open the thread: {Native.Win32Message(Marshal.GetLastWin32Error())}");
        if (Native.ResumeThread(h) == uint.MaxValue) throw new InvalidOperationException(Native.Win32Message(Marshal.GetLastWin32Error()));
    }

    internal static SafeAccessTokenHandle? Open(int tid, uint access)
    {
        var h = Native.OpenThread(access, false, tid);
        if (!h.IsInvalid) return h;
        h.Dispose();
        return null;
    }
}

/// <summary>Modules loaded in a process, to say which one each thread starts in.</summary>
public sealed class ModuleMap
{
    readonly List<(ulong Base, ulong End, string Name)> _modules = new();

    public static ModuleMap Read(int pid)
    {
        var map = new ModuleMap();
        using var h = ProcessControl.Open(pid, Native.ProcessQueryInformation | Native.ProcessVmRead);
        if (h is null) return map;
        Native.EnumProcessModulesEx(h, null, 0, out var needed, 3); // LIST_MODULES_ALL
        if (needed <= 0) return map;
        var modules = new IntPtr[needed / IntPtr.Size + 16];
        if (!Native.EnumProcessModulesEx(h, modules, modules.Length * IntPtr.Size, out needed, 3)) return map;
        var name = new StringBuilder(260);
        foreach (var m in modules.Take(Math.Min(modules.Length, needed / IntPtr.Size)))
        {
            if (m == IntPtr.Zero || !Native.GetModuleInformation(h, m, out var info, Marshal.SizeOf<Native.ModuleInfo>())) continue;
            name.Clear();
            if (Native.GetModuleBaseName(h, m, name, name.Capacity) == 0) continue;
            map._modules.Add(((ulong)info.BaseOfDll, (ulong)info.BaseOfDll + info.SizeOfImage, name.ToString()));
        }
        return map;
    }

    /// <summary>Whether a module with this file name is loaded; null when the process couldn't be read (every process has some).</summary>
    public bool? Has(string name) => _modules.Count == 0 ? null : _modules.Exists(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Name of the module that contains the address; null if none (module loaded after the read).</summary>
    public string? Find(IntPtr address)
    {
        var a = (ulong)address;
        foreach (var (b, e, n) in _modules) if (a >= b && a < e) return n;
        return null;
    }
}
