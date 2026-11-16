using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using KosToolbox.Core.Persistence;
using Microsoft.Win32.SafeHandles;

namespace KosToolbox.Core.Latency;

/// <summary>
/// A latency measurement, like LatencyMon: a real-time kernel trace session of its own (SystemTraceProvider, system logger
/// mode, so it doesn't take the NT Kernel Logger) with DPC, ISR and hard pagefault events, and a thread that measures how
/// long it takes to run after the clock interrupt that expires its 1 ms timer. Needs administrator. Reads only: nothing on the system is changed, and the
/// session ends with <see cref="Dispose"/> (also when the toolbox closes; one left by a crash is stopped at the next start).
/// </summary>
public sealed class LatencyTrace : IDisposable
{
    public const string SessionName = "KOS Toolbox Latency";

    readonly LatencyCollector _collector;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly EventRecordCallback _callback; // kept alive while the trace runs
    readonly Thread _consumer, _timer;
    readonly Dictionary<uint, int> _threadPids = new(); // trace thread only
    readonly Dictionary<int, string> _names = new();    // Report caller only
    readonly byte[] _payload = new byte[64];
    ulong _trace = InvalidTrace;
    volatile bool _stopping;
    int _disposed;

    /// <summary>Kernel modules with a known address; 0 = Windows hid them, so drivers can't be named.</summary>
    public int Modules { get; }

    LatencyTrace(KernelModules modules)
    {
        Modules = modules.Count;
        _collector = new LatencyCollector(modules, Stopwatch.Frequency);
        _callback = OnEvent;
        _consumer = new Thread(Consume) { IsBackground = true, Name = "Latency trace" };
        _timer = new Thread(MeasureTimer) { IsBackground = true, Name = "Latency timer" };
    }

    public static LatencyTrace Start()
    {
        StopSession(); // one left running by a toolbox that crashed
        var trace = new LatencyTrace(KernelModules.Read());
        try { trace.Open(); }
        catch { trace.Dispose(); throw; }
        trace._consumer.Start();
        trace._timer.Start();
        AppDomain.CurrentDomain.ProcessExit += trace.OnExit;
        Log.Info($"Latency: measurement started ({trace.Modules} kernel modules).");
        return trace;
    }

    public LatencyReport Report() => _collector.Report(_clock.Elapsed, ProcessName);

    void Open()
    {
        var props = Properties();
        try
        {
            var p = Marshal.PtrToStructure<TraceProperties>(props);
            p.Guid = Guid.NewGuid(); // a session GUID of its own, never SystemTraceControlGuid
            p.ClientContext = 1;     // QPC timestamps, the same clock as the routines' start times
            p.LogFileMode = RealTimeMode | SystemLoggerMode;
            p.EnableFlags = FlagDpc | FlagInterrupt | FlagHardFaults | FlagNoSysConfig;
            p.BufferSizeKb = 64;
            // DPCs and ISRs are logged at high IRQL, where ETW can't allocate buffers on demand: reserve them up front.
            p.MinimumBuffers = (uint)Math.Min(2 * Environment.ProcessorCount + 8, 256);
            p.MaximumBuffers = p.MinimumBuffers * 2;
            p.FlushTimer = 1;
            Marshal.StructureToPtr(p, props, false);
            var error = StartTraceW(out _, SessionName, props);
            if (error != 0) throw new Win32Exception(error);
        }
        finally { Marshal.FreeHGlobal(props); }

        var logfile = new EventTraceLogfile
        {
            LoggerName = Marshal.StringToHGlobalUni(SessionName),
            ProcessTraceMode = ProcessRealTime | ProcessEventRecord | ProcessRawTimestamp,
            EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_callback),
        };
        try { _trace = OpenTraceW(ref logfile); }
        finally { Marshal.FreeHGlobal(logfile.LoggerName); }
        if (_trace == InvalidTrace) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    void Consume()
    {
        var error = ProcessTrace(new[] { _trace }, 1, IntPtr.Zero, IntPtr.Zero);
        if (error != 0 && !_stopping) Log.Warn($"Latency: the trace stopped ({new Win32Exception((int)error).Message}).");
    }

    bool _callbackFailed;

    void OnEvent(IntPtr record)
    {
        try
        {
            // EVENT_RECORD (x64): TimeStamp at 16, ProviderId at 24, Opcode at 45, ProcessorNumber at 80, UserDataLength at 86, UserData at 96.
            var provider = Marshal.PtrToStructure<Guid>(record + 24);
            var opcode = Marshal.ReadByte(record, 45);
            if (!LatencyCollector.Wanted(provider, opcode)) return;
            var length = Math.Min((int)(ushort)Marshal.ReadInt16(record, 86), _payload.Length);
            Marshal.Copy(Marshal.ReadIntPtr(record, 96), _payload, 0, length);
            _collector.Add(provider, opcode, Marshal.ReadByte(record, 80), Marshal.ReadInt64(record, 16), _payload.AsSpan(0, length), ProcessOfThread);
        }
        catch (Exception ex) when (!_callbackFailed) { _callbackFailed = true; Log.Error("Latency: an event couldn't be read", ex); }
        catch { } // never let an exception reach ETW's native caller
    }

    int ProcessOfThread(uint tid)
    {
        if (_threadPids.TryGetValue(tid, out var pid)) return pid;
        pid = -1;
        using (var h = OpenThread(0x0800, false, tid)) // THREAD_QUERY_LIMITED_INFORMATION
            if (!h.IsInvalid) pid = (int)GetProcessIdOfThread(h);
        return _threadPids[tid] = pid;
    }

    string ProcessName(int pid)
    {
        if (_names.TryGetValue(pid, out var name)) return name;
        try { name = pid < 0 ? "(exited)" : Process.GetProcessById(pid).ProcessName; }
        catch { name = "(exited)"; }
        return _names[pid] = name;
    }

    /// <summary>
    /// Interrupt to thread: a time-critical thread waits on a 1 ms timer, and on waking measures how long since the clock
    /// interrupt that expired it. The timer fires on the first clock tick from its due time (measured: up to one timer
    /// resolution late), so that tick is the reference, not the due time; when more ticks passed before the thread ran, the
    /// expiring one was at most due + resolution.
    /// </summary>
    void MeasureTimer()
    {
        using var timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2, 0x1F0003); // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS
        if (timer.IsInvalid) { Log.Warn("Latency: no high-resolution timer; interrupt to thread isn't measured."); return; }
        SetThreadPriority(GetCurrentThread(), 15); // THREAD_PRIORITY_TIME_CRITICAL
        while (!_stopping)
        {
            var relative = -10_000L; // 1 ms, in 100 ns
            var gcs = GC.CollectionCount(0);
            QueryInterruptTimePrecise(out var start);
            if (!SetWaitableTimer(timer, ref relative, 0, IntPtr.Zero, IntPtr.Zero, false)) return;
            WaitForSingleObject(timer, 1000);
            QueryInterruptTimePrecise(out var now);
            QueryInterruptTime(out var tick); // when the last clock interrupt updated it
            NtQueryTimerResolution(out _, out _, out var resolution); // a game can change it while measuring
            var due = start + 10_000;
            if (GC.CollectionCount(0) == gcs) // a pause of the toolbox's own garbage collector isn't the system's latency
                _collector.Timer(now - Math.Clamp(tick, due, due + resolution));
        }
    }

    void OnExit(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        AppDomain.CurrentDomain.ProcessExit -= OnExit;
        _stopping = true;
        StopSession(); // ProcessTrace returns once the session ends
        if (_consumer.IsAlive) _consumer.Join(5000);
        if (_trace != InvalidTrace) CloseTrace(_trace);
        if (_timer.IsAlive) _timer.Join(2000);
        Log.Info($"Latency: measurement stopped after {_clock.Elapsed:hh\\:mm\\:ss}.");
    }

    /// <summary>Stops the session if it runs; true when it was running.</summary>
    public static bool StopSession()
    {
        var props = Properties();
        try { return ControlTraceW(0, SessionName, props, 1) == 0; } // EVENT_TRACE_CONTROL_STOP
        finally { Marshal.FreeHGlobal(props); }
    }

    /// <summary>Zeroed EVENT_TRACE_PROPERTIES with room for the session name after it (StartTrace and ControlTrace copy it there).</summary>
    static IntPtr Properties()
    {
        var header = Marshal.SizeOf<TraceProperties>();
        var size = header + 1024 * sizeof(char);
        var buffer = Marshal.AllocHGlobal(size);
        Marshal.Copy(new byte[size], 0, buffer, size);
        Marshal.StructureToPtr(new TraceProperties { BufferSize = (uint)size, Flags = TracedGuid, LoggerNameOffset = (uint)header }, buffer, false);
        return buffer;
    }

    // ---------- Win32 ----------

    const ulong InvalidTrace = ulong.MaxValue;
    const uint RealTimeMode = 0x100, SystemLoggerMode = 0x02000000, TracedGuid = 0x00020000;
    const uint FlagDpc = 0x20, FlagInterrupt = 0x40, FlagHardFaults = 0x2000, FlagNoSysConfig = 0x10000000;
    const uint ProcessRealTime = 0x100, ProcessRawTimestamp = 0x1000, ProcessEventRecord = 0x10000000;

    /// <summary>EVENT_TRACE_PROPERTIES (120 bytes on x64), starting with its WNODE_HEADER.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct TraceProperties
    {
        public uint BufferSize, ProviderId;
        public ulong HistoricalContext;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext, Flags;
        public uint BufferSizeKb, MinimumBuffers, MaximumBuffers, MaximumFileSize, LogFileMode, FlushTimer, EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten, LogBuffersLost, RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset, LoggerNameOffset;
    }

    /// <summary>EVENT_TRACE_LOGFILEW (448 bytes on x64); only the fields used here.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 448)]
    internal struct EventTraceLogfile
    {
        [FieldOffset(8)] public IntPtr LoggerName;
        [FieldOffset(28)] public uint ProcessTraceMode;
        [FieldOffset(424)] public IntPtr EventRecordCallback;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void EventRecordCallback(IntPtr record);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int StartTraceW(out ulong handle, string name, IntPtr properties);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int ControlTraceW(ulong handle, string name, IntPtr properties, uint code);
    [DllImport("advapi32.dll", SetLastError = true)] static extern ulong OpenTraceW(ref EventTraceLogfile logfile);
    [DllImport("advapi32.dll")] static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
    [DllImport("advapi32.dll")] static extern uint CloseTrace(ulong handle);
    [DllImport("kernel32.dll", SetLastError = true)] static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period, IntPtr completion, IntPtr arg, bool resume);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
    [DllImport("kernelbase.dll")] static extern void QueryInterruptTimePrecise(out long time);
    [DllImport("kernelbase.dll")] static extern void QueryInterruptTime(out long time);
    [DllImport("ntdll.dll")] static extern int NtQueryTimerResolution(out uint minimum, out uint maximum, out uint current);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] static extern bool SetThreadPriority(IntPtr thread, int priority);
    [DllImport("kernel32.dll", SetLastError = true)] static extern SafeWaitHandle OpenThread(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll")] static extern uint GetProcessIdOfThread(SafeWaitHandle thread);
}
