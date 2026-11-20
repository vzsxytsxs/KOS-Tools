using System.Runtime.InteropServices;
using System.Text;

namespace KosToolbox.Core.Gpu;

/// <summary>
/// Detects that the video driver restarted (a display adapter came back under a new LUID, or NVML or NVAPI had to reopen the
/// session) or recovered from a hang (event 4101 "Display" in the System log: the driver stopped responding and came back), since
/// the last check. In both cases the driver comes back with clocks, limits and fans at their defaults.
/// </summary>
public sealed class GpuResetWatch
{
    int _generation = GpuTuner.Generation;
    uint _lastRecord = DisplayEvents.Newest();
    string _adapters = DisplayAdapters.Luids();

    /// <param name="probeBusId">Card to check the session now (callers that do not read the card themselves pass the bus id).</param>
    public bool Poll(string? probeBusId = null)
    {
        if (probeBusId is not null)
            try { GpuTuner.Probe(probeBusId); } catch { /* the reopening counts through the generation; the failure itself does not matter here */ }
        var reset = false;
        if (GpuTuner.Generation != _generation) { _generation = GpuTuner.Generation; reset = true; }
        if (DisplayEvents.RecoveredSince(ref _lastRecord)) reset = true;
        if (DisplayAdapters.Luids() is var adapters && adapters != _adapters) { _adapters = adapters; reset = true; }
        return reset;
    }
}

/// <summary>
/// The display adapters' LUIDs (D3DKMTEnumAdapters2). A restarted display driver comes back under a new LUID: with a GTX card and
/// pnputil /restart-device the LUID changed every time, NVML's session stayed broken until reopened, and NVAPI failed only for the
/// two seconds of the restart (its handle was valid again after). A new or removed adapter reads as a restart too, which only
/// re-applies settings.
/// </summary>
internal static class DisplayAdapters
{
    [StructLayout(LayoutKind.Sequential)] struct AdapterInfo { public uint Handle, LuidLow; public int LuidHigh; public uint Sources; public int PreciseRegions; }
    [StructLayout(LayoutKind.Sequential)] struct EnumAdapters2 { public uint Count; public IntPtr Adapters; }
    [StructLayout(LayoutKind.Sequential)] struct CloseAdapter { public uint Handle; }
    [DllImport("gdi32.dll")] static extern int D3DKMTEnumAdapters2(ref EnumAdapters2 e);
    [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref CloseAdapter c);

    /// <summary>"high:low" per adapter, sorted; empty when they can't be read.</summary>
    public static string Luids()
    {
        var e = new EnumAdapters2();
        if (D3DKMTEnumAdapters2(ref e) != 0 || e.Count == 0) return "";
        var size = Marshal.SizeOf<AdapterInfo>();
        e.Adapters = Marshal.AllocHGlobal(size * (int)e.Count);
        try
        {
            if (D3DKMTEnumAdapters2(ref e) != 0) return "";
            var list = new List<string>();
            for (var i = 0; i < e.Count; i++)
            {
                var a = Marshal.PtrToStructure<AdapterInfo>(e.Adapters + i * size);
                list.Add($"{a.LuidHigh:X}:{a.LuidLow:X}");
                var close = new CloseAdapter { Handle = a.Handle };
                D3DKMTCloseAdapter(ref close);
            }
            list.Sort(StringComparer.Ordinal);
            return string.Join(",", list);
        }
        finally { Marshal.FreeHGlobal(e.Adapters); }
    }
}

/// <summary>Event 4101 of the "Display" provider in the System log, read only from the last record seen.</summary>
internal static class DisplayEvents
{
    const uint SequentialRead = 0x1, SeekRead = 0x2, ForwardsRead = 0x4;
    const int RecordNumberAt = 8, EventIdAt = 20, SourceAt = 56, MaxRecordsPerPoll = 5000;

    public static uint Newest()
    {
        var h = OpenEventLog(null, "System");
        if (h == IntPtr.Zero) return 0;
        try { return GetOldestEventLogRecord(h, out var oldest) && GetNumberOfEventLogRecords(h, out var count) && count > 0 ? oldest + count - 1 : 0; }
        finally { CloseEventLog(h); }
    }

    /// <summary>Any recovered TDR after <paramref name="lastRecord"/>; advances the marker to the newest record.</summary>
    public static bool RecoveredSince(ref uint lastRecord) => Since(ref lastRecord, "Display", 4101);

    /// <summary>Any <paramref name="source"/>/<paramref name="eventId"/> event after <paramref name="lastRecord"/>.</summary>
    internal static bool Since(ref uint lastRecord, string source, uint eventId)
    {
        var newest = Newest();
        if (newest == lastRecord) return false;
        if (newest < lastRecord || newest - lastRecord > MaxRecordsPerPoll) { lastRecord = newest; return false; } // log cleared or too far behind
        var h = OpenEventLog(null, "System");
        if (h == IntPtr.Zero) return false;
        var found = false;
        try
        {
            var buffer = new byte[64 * 1024];
            var next = lastRecord + 1;
            while (next <= newest)
            {
                if (!ReadEventLog(h, SeekRead | ForwardsRead, next, buffer, (uint)buffer.Length, out var read, out var needed))
                {
                    if (needed > buffer.Length) { buffer = new byte[needed]; continue; }
                    break;
                }
                if (read == 0) break;
                for (var at = 0; at + SourceAt < read;)
                {
                    var length = BitConverter.ToInt32(buffer, at);
                    if (length <= 0) break;
                    var id = BitConverter.ToUInt32(buffer, at + EventIdAt) & 0xFFFF;
                    if (id == eventId && Source(buffer, at + SourceAt) == source) found = true;
                    next = BitConverter.ToUInt32(buffer, at + RecordNumberAt) + 1;
                    at += length;
                }
            }
        }
        finally { CloseEventLog(h); }
        lastRecord = newest;
        return found;
    }

    static string Source(byte[] data, int at)
    {
        var end = at;
        while (end + 1 < data.Length && (data[end] != 0 || data[end + 1] != 0)) end += 2;
        return Encoding.Unicode.GetString(data, at, end - at);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenEventLog(string? server, string source);
    [DllImport("advapi32.dll")] static extern bool CloseEventLog(IntPtr log);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetNumberOfEventLogRecords(IntPtr log, out uint count);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetOldestEventLogRecord(IntPtr log, out uint oldest);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] // ReadEventLogW: the source name comes in UTF-16
    static extern bool ReadEventLog(IntPtr log, uint flags, uint offset, byte[] buffer, uint size, out uint read, out uint needed);
}
