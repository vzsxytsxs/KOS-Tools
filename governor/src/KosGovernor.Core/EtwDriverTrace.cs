using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using KosToolbox.Core.Latency;

namespace KosGovernor.Core;

/// <summary>Interrupt work seen by a short trace: ISR + DPC ms per processor (logical index) and the drivers with the most.</summary>
public sealed record InterruptLoad(IReadOnlyDictionary<int, double> CpuMs, IReadOnlyList<(string Driver, double Ms)> Drivers, TimeSpan Length)
{
    /// <summary>"0 (dxgkrnl.sys, ndis.sys)": the processors with the most, for the log.</summary>
    public string Busiest(int count = 2) => string.Join(", ", CpuMs.OrderByDescending(c => c.Value).Take(count).Select(c => $"CPU {c.Key} {c.Value / Length.TotalSeconds:0.0} ms/s"))
        + (Drivers.Count > 0 ? $" ({string.Join(", ", Drivers.Take(3).Select(d => d.Driver))})" : "");
}

/// <summary>
/// A short kernel trace of DPC and ISR routines: its own real-time session (SystemTraceProvider in system logger mode, so it doesn't take
/// the NT Kernel Logger), the same event decoding as the toolbox's latency measurement. Reads only; needs administrator. The thread
/// director uses it to keep the game's busiest threads off the processors that handle the most interrupts.
/// </summary>
public static class EtwDriverTrace
{
    public const string SessionName = "KOS Governor Interrupts";

    static readonly object Gate = new();
    static readonly EventRecordCallback Callback = OnEvent; // kept alive for ETW (a static: Native AOT, no closure)
    static LatencyCollector? _collector;
    static readonly byte[] Payload = new byte[64];

    /// <summary>Traces for <paramref name="length"/> and returns what each processor spent in interrupts. Blocks; run it off the engine loop.</summary>
    public static InterruptLoad Sample(TimeSpan length)
    {
        lock (Gate)
        {
            StopSession(); // one left by an engine that crashed
            var collector = new LatencyCollector(KernelModules.Read(keepDebug: true), Stopwatch.Frequency); // the engine keeps SeDebugPrivilege on
            Start();
            ulong trace;
            var logfile = new EventTraceLogfile
            {
                LoggerName = Marshal.StringToHGlobalUni(SessionName),
                ProcessTraceMode = ProcessRealTime | ProcessEventRecord | ProcessRawTimestamp,
                EventRecordCallback = Marshal.GetFunctionPointerForDelegate(Callback),
            };
            try { trace = OpenTraceW(ref logfile); }
            finally { Marshal.FreeHGlobal(logfile.LoggerName); }
            if (trace == InvalidTrace) { var error = Marshal.GetLastWin32Error(); StopSession(); throw new Win32Exception(error); }
            _collector = collector;
            var consumer = new Thread(() => ProcessTrace(new[] { trace }, 1, IntPtr.Zero, IntPtr.Zero)) { IsBackground = true, Name = "Interrupt trace" };
            try
            {
                consumer.Start();
                Thread.Sleep(length);
            }
            finally
            {
                StopSession(); // ProcessTrace returns once the session ends
                consumer.Join(5000);
                CloseTrace(trace);
                _collector = null;
            }
            var drivers = collector.Report(length, _ => "").Drivers
                .Select(d => (d.Name, Ms: d.Isr.TotalMs + d.Dpc.TotalMs)).Where(d => d.Ms > 0).OrderByDescending(d => d.Ms).ToList();
            return new InterruptLoad(collector.CpuMs(), drivers, length);
        }
    }

    static void Start()
    {
        var props = Properties();
        try
        {
            var p = Marshal.PtrToStructure<TraceProperties>(props);
            p.Guid = Guid.NewGuid(); // a session GUID of its own, never SystemTraceControlGuid
            p.ClientContext = 1;     // QPC timestamps, the clock the routines' start times use
            p.LogFileMode = RealTimeMode | SystemLoggerMode;
            p.EnableFlags = FlagDpc | FlagInterrupt | FlagNoSysConfig;
            p.BufferSizeKb = 64;
            // Logged at high IRQL, where ETW can't allocate buffers on demand: reserve them up front.
            p.MinimumBuffers = (uint)Math.Min(2 * Environment.ProcessorCount + 8, 256);
            p.MaximumBuffers = p.MinimumBuffers * 2;
            p.FlushTimer = 1;
            Marshal.StructureToPtr(p, props, false);
            var error = StartTraceW(out _, SessionName, props);
            if (error != 0) throw new Win32Exception(error);
        }
        finally { Marshal.FreeHGlobal(props); }
    }

    static void OnEvent(IntPtr record)
    {
        try
        {
            if (_collector is not { } collector) return;
            // EVENT_RECORD (x64): TimeStamp at 16, ProviderId at 24, Opcode at 45, ProcessorNumber at 80, UserDataLength at 86, UserData at 96.
            var provider = Marshal.PtrToStructure<Guid>(record + 24);
            var opcode = Marshal.ReadByte(record, 45);
            if (!LatencyCollector.Wanted(provider, opcode)) return;
            var length = Math.Min((int)(ushort)Marshal.ReadInt16(record, 86), Payload.Length);
            Marshal.Copy(Marshal.ReadIntPtr(record, 96), Payload, 0, length);
            collector.Add(provider, opcode, Marshal.ReadByte(record, 80), Marshal.ReadInt64(record, 16), Payload.AsSpan(0, length), _ => -1);
        }
        catch { } // never let an exception reach ETW's native caller
    }

    /// <summary>Stops the session if it runs (engine exit, or one left by a crash).</summary>
    public static void StopSession()
    {
        var props = Properties();
        try { ControlTraceW(0, SessionName, props, 1); } // EVENT_TRACE_CONTROL_STOP
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

    const ulong InvalidTrace = ulong.MaxValue;
    const uint RealTimeMode = 0x100, SystemLoggerMode = 0x02000000, TracedGuid = 0x00020000;
    const uint FlagDpc = 0x20, FlagInterrupt = 0x40, FlagNoSysConfig = 0x10000000;
    const uint ProcessRealTime = 0x100, ProcessRawTimestamp = 0x1000, ProcessEventRecord = 0x10000000;

    /// <summary>EVENT_TRACE_PROPERTIES (120 bytes on x64), starting with its WNODE_HEADER.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct TraceProperties
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
    struct EventTraceLogfile
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
}
