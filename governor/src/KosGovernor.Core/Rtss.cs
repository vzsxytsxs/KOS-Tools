using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.Tools;
using Microsoft.Win32;

namespace KosGovernor.Core;

/// <summary>
/// RivaTuner Statistics Server, when the user installed it: the governor never installs or starts it, and RTSS injects into the
/// games itself (some anti-cheats refuse that). The on-screen display text goes through RTSS's shared memory, in an OSD slot of its
/// SDK; the frame limit through the profile functions RTSSHooks64.dll exports, as the SDK's own sample does.
/// </summary>
public static class Rtss
{
    const string MapName = "RTSSSharedMemoryV2";
    static readonly byte[] Owner = Encoding.ASCII.GetBytes("KOS Governor");
    const uint Signature = 0x52545353; // 'RTSS'
    // RTSS_SHARED_MEMORY (RTSSSharedMemory.h): dwSignature, dwVersion, app entry size/offset/count, OSD entry size/offset/count, dwOSDFrame, dwBusy (v2.14+).
    const int VersionAt = 4, OsdEntrySizeAt = 20, OsdArrOffsetAt = 24, OsdArrSizeAt = 28, OsdFrameAt = 32, BusyAt = 36, HeaderSize = 40;
    // RTSS_SHARED_MEMORY_OSD_ENTRY: szOSD[256], szOSDOwner[256], szOSDEx[4096] (v2.7+).
    const int OsdSize = 256, OwnerAt = 256, OwnerSize = 256, OsdExAt = 512, OsdExSize = 4096;
    const uint ExtendedText = 0x00020007, Locking = 0x0002000E;

    internal enum OsdResult { Written, Busy, NoSlot, Invalid }

    // ---------- on-screen display ----------

    /// <summary>Shows the text in the governor's OSD slot. False when RTSS isn't running or every slot is taken.</summary>
    public static bool ShowOsd(string text) => WithView(m => Write(m, text)) is OsdResult.Written or OsdResult.Busy;

    /// <summary>Gives the governor's OSD slot back (gamemode off, engine exit, and at engine start after a crash).</summary>
    public static int ReleaseOsd() => WithView(Release) ?? 0;

    /// <summary>
    /// Writes into the first slot this program owns, else claims the first free one. Slot 0 stays for the primary client
    /// (MSI Afterburner), as the SDK asks. The header comes from a mapping any program in the session could have created
    /// before RTSS: every offset is checked against the view, and the span refuses anything outside it.
    /// </summary>
    internal static OsdResult Write(Span<byte> mem, string text)
    {
        if (!Layout(mem, out var version, out var offset, out var size, out var count)) return OsdResult.Invalid;
        var slot = -1;
        for (var pass = 0; pass < 2 && slot < 0; pass++)
            for (var i = 1; i < count; i++)
            {
                var owner = mem.Slice(offset + i * size + OwnerAt, OwnerSize);
                if (pass == 0 ? IsOurs(owner) : owner[0] == 0) { slot = i; break; }
            }
        if (slot < 0) return OsdResult.NoSlot;
        var entry = mem.Slice(offset + slot * size, size);
        if (!IsOurs(entry.Slice(OwnerAt, OwnerSize)))
        {
            entry.Slice(OwnerAt, OwnerSize).Clear();
            Owner.CopyTo(entry.Slice(OwnerAt));
        }
        var field = version >= ExtendedText && size >= OsdExAt + OsdExSize ? entry.Slice(OsdExAt, OsdExSize) : entry.Slice(0, OsdSize);
        if (version >= Locking)
        {
            // Bit 0 is set while RTSS renders the OSD: skip this update rather than wait.
            ref var busy = ref MemoryMarshal.Cast<byte, int>(mem.Slice(BusyAt, 4))[0];
            if ((Interlocked.Or(ref busy, 1) & 1) != 0) return OsdResult.Busy;
            try { Fill(field, text); }
            finally { Interlocked.And(ref busy, ~1); }
        }
        else Fill(field, text);
        Interlocked.Increment(ref MemoryMarshal.Cast<byte, int>(mem.Slice(OsdFrameAt, 4))[0]);
        return OsdResult.Written;
    }

    internal static int Release(Span<byte> mem)
    {
        if (!Layout(mem, out _, out var offset, out var size, out var count)) return 0;
        var released = 0;
        for (var i = 1; i < count; i++)
        {
            var entry = mem.Slice(offset + i * size, size);
            if (!IsOurs(entry.Slice(OwnerAt, OwnerSize))) continue;
            entry.Clear();
            released++;
        }
        if (released > 0) Interlocked.Increment(ref MemoryMarshal.Cast<byte, int>(mem.Slice(OsdFrameAt, 4))[0]);
        return released;
    }

    static bool Layout(Span<byte> mem, out uint version, out int offset, out int size, out int count)
    {
        version = 0;
        offset = size = count = 0;
        if (mem.Length < HeaderSize || U32(mem, 0) != Signature) return false;
        version = U32(mem, VersionAt);
        var (s, o, c) = (U32(mem, OsdEntrySizeAt), U32(mem, OsdArrOffsetAt), U32(mem, OsdArrSizeAt));
        if (version < 0x00020000 || s < OsdExAt || o < HeaderSize || c is < 2 or > 64 || (ulong)o + (ulong)s * c > (ulong)mem.Length) return false;
        (offset, size, count) = ((int)o, (int)s, (int)c);
        return true;
    }

    static uint U32(Span<byte> mem, int at) => BinaryPrimitives.ReadUInt32LittleEndian(mem.Slice(at, 4));

    static bool IsOurs(Span<byte> owner) => owner.StartsWith(Owner) && owner[Owner.Length] == 0;

    /// <summary>ANSI text, cut to the field and ended with a zero (RTSS draws Latin-1, so ° shows).</summary>
    static void Fill(Span<byte> field, string text)
    {
        field.Clear();
        var bytes = Encoding.Latin1.GetBytes(text);
        bytes.AsSpan(0, Math.Min(bytes.Length, field.Length - 1)).CopyTo(field);
    }

    /// <summary>
    /// The OSD text: RTSS fills in &lt;FR&gt; (frames per second) and &lt;FT&gt; (frame time) for each game; the rest comes from the
    /// governor's readings (NVIDIA cards only for the GPU line).
    /// </summary>
    internal static string OsdText(double cpuPercent, ulong memUsed, ulong memTotal, GpuSensors? gpu)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("FPS  <FR>  <FT> ms\n");
        if (gpu is { } g)
        {
            var parts = new List<string>();
            if (g.GpuLoad is { } load) parts.Add(string.Format(c, "{0:0}%", load));
            if (g.TemperatureC is { } temp) parts.Add(string.Format(c, "{0:0}°C", temp));
            if (g.GraphicsMhz is { } mhz) parts.Add(string.Format(c, "{0} MHz", mhz));
            if (g.PowerMw is { } mw) parts.Add(string.Format(c, "{0:0} W", mw / 1000.0));
            if (g.VramUsed is { } used && g.VramTotal is { } total and > 0) parts.Add(string.Format(c, "VRAM {0:0.0}/{1:0.0} GB", used / 1073741824.0, total / 1073741824.0));
            if (parts.Count > 0) sb.Append("GPU  ").Append(string.Join("  ", parts)).Append('\n');
        }
        sb.Append(string.Format(c, "CPU  {0:0}%  RAM {1:0.0}/{2:0.0} GB", cpuPercent, memUsed / 1073741824.0, memTotal / 1073741824.0));
        return sb.ToString();
    }

    delegate T ViewFn<T>(Span<byte> mem);

    static unsafe T? WithView<T>(ViewFn<T> fn) where T : struct
    {
        var map = OpenFileMapping(FileMapRead | FileMapWrite, false, MapName);
        if (map == IntPtr.Zero) return null; // RTSS isn't running
        try
        {
            var view = MapViewOfFile(map, FileMapRead | FileMapWrite, 0, 0, UIntPtr.Zero);
            if (view == IntPtr.Zero) return null;
            try
            {
                // The committed region the view starts with: the span can't reach past it. Only a section in memory, as RTSS creates:
                // one backed by a file (a share that stalls or fails a read) would hang or kill this process on a page read.
                if (VirtualQuery(view, out var info, (UIntPtr)Marshal.SizeOf<MemoryInfo>()) == UIntPtr.Zero
                    || info.State != MemCommit || info.Protect != PageReadWrite || info.Type != MemMapped) return null;
                var name = stackalloc char[260];
                if (K32GetMappedFileNameW(CurrentProcess, view, name, 260) != 0) return null;
                return fn(new Span<byte>((void*)view, (int)Math.Min((ulong)info.RegionSize, int.MaxValue)));
            }
            finally { UnmapViewOfFile(view); }
        }
        finally { CloseHandle(map); }
    }

    // ---------- frame limit (global profile) ----------

    /// <summary>The global profile's frame limit as RTSS stores it: <see cref="Limit"/> / <see cref="Denominator"/> frames per second, 0 = off.</summary>
    internal readonly record struct FrameLimit(uint Limit, uint Denominator)
    {
        public override string ToString() => Limit == 0 ? "off" : Denominator <= 1 ? $"{Limit} FPS" : string.Format(CultureInfo.InvariantCulture, "{0:0.###} FPS", (double)Limit / Denominator);
    }

    internal static FrameLimit ReadLimit()
    {
        using var hooks = new Hooks();
        hooks.Load("");
        return new FrameLimit(hooks.Get("FramerateLimit"), hooks.Get("FramerateLimitDenominator"));
    }

    /// <summary>Writes the global profile, reads it back from the file and tells the running games to reload it.</summary>
    internal static void WriteLimit(FrameLimit limit)
    {
        limit = limit with { Denominator = Math.Max(1, limit.Denominator) };
        using (var hooks = new Hooks())
        {
            hooks.Load("");
            hooks.Set("FramerateLimitDenominator", limit.Denominator);
            hooks.Set("FramerateLimit", limit.Limit);
            hooks.Save("");
        }
        if (ReadLimit() is var saved && saved != limit) throw new InvalidOperationException($"RTSS's global profile reads {saved} after writing {limit}.");
        // Not the DLL's UpdateProfiles: it walks the shared memory without checking it, inside this elevated process.
        WithView(RequestProfileUpdate);
    }

    // RTSS_SHARED_MEMORY_APP_ENTRY: dwProcessID, szName[260], dwFlags; APPFLAG_PROFILE_UPDATE_REQUESTED makes the hooked game reload its profile.
    const int AppEntrySizeAt = 8, AppArrOffsetAt = 12, AppArrSizeAt = 16, AppFlagsAt = 4 + 260;
    const int ProfileUpdateRequested = 0x10000000;

    /// <summary>Flags every hooked game to reload its profile, as UpdateProfiles does, with the header checked against the view.</summary>
    internal static int RequestProfileUpdate(Span<byte> mem)
    {
        if (mem.Length < HeaderSize || U32(mem, 0) != Signature || U32(mem, VersionAt) < 0x00020000) return 0;
        var (s, o, c) = (U32(mem, AppEntrySizeAt), U32(mem, AppArrOffsetAt), U32(mem, AppArrSizeAt));
        if (s < AppFlagsAt + 4 || s % 4 != 0 || o < HeaderSize || o % 4 != 0 || c > 1024 || (ulong)o + (ulong)s * c > (ulong)mem.Length) return 0;
        var flagged = 0;
        for (var i = 0; i < (int)c; i++)
        {
            var entry = mem.Slice((int)o + i * (int)s, (int)s);
            if (U32(entry, 0) == 0) continue; // free entry
            Interlocked.Or(ref MemoryMarshal.Cast<byte, int>(entry.Slice(AppFlagsAt, 4))[0], ProfileUpdateRequested);
            flagged++;
        }
        return flagged;
    }

    /// <summary>
    /// RTSSHooks64.dll, which the elevated engine loads: only from RTSS's install folder (HKLM) under Program Files, and owned by SYSTEM,
    /// TrustedInstaller or Administrators. Null with the reason otherwise.
    /// </summary>
    internal static string? HooksDll(out string? problem)
    {
        problem = null;
        string? dir;
        using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(@"SOFTWARE\Unwinder\RTSS"))
            dir = key?.GetValue("InstallDir") as string;
        if (string.IsNullOrWhiteSpace(dir)) { problem = "RivaTuner Statistics Server isn't installed"; return null; }
        var dll = Path.GetFullPath(Path.Combine(dir, "RTSSHooks64.dll"));
        var programFiles = new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles }
            .Select(f => Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(f)) + Path.DirectorySeparatorChar);
        if (!programFiles.Any(p => dll.StartsWith(p, StringComparison.OrdinalIgnoreCase))) { problem = $"{dll} isn't under Program Files"; return null; }
        if (!File.Exists(dll)) { problem = $"{dll} doesn't exist"; return null; }
        problem = ToolDiscovery.OwnerProblem(dll);
        return problem is null ? dll : null;
    }

    /// <summary>
    /// The profile functions of RTSSHooks64.dll ("" = the global profile), loaded for one change and freed after. Plain function
    /// pointers with ANSI bytes: no delegate marshalling in the Native AOT engine.
    /// </summary>
    sealed unsafe class Hooks : IDisposable
    {
        readonly IntPtr _lib;
        readonly delegate* unmanaged[Cdecl]<byte*, void> _load, _save;
        readonly delegate* unmanaged[Cdecl]<byte*, uint*, uint, int> _get, _set;

        public Hooks()
        {
            var dll = HooksDll(out var problem) ?? throw new InvalidOperationException(problem);
            _lib = NativeLibrary.Load(dll);
            try
            {
                _load = (delegate* unmanaged[Cdecl]<byte*, void>)NativeLibrary.GetExport(_lib, "LoadProfile");
                _save = (delegate* unmanaged[Cdecl]<byte*, void>)NativeLibrary.GetExport(_lib, "SaveProfile");
                _get = (delegate* unmanaged[Cdecl]<byte*, uint*, uint, int>)NativeLibrary.GetExport(_lib, "GetProfileProperty");
                _set = (delegate* unmanaged[Cdecl]<byte*, uint*, uint, int>)NativeLibrary.GetExport(_lib, "SetProfileProperty");
            }
            catch { NativeLibrary.Free(_lib); throw; }
        }

        static byte[] Ansi(string s) => Encoding.ASCII.GetBytes(s + "\0");

        public void Load(string profile) { fixed (byte* p = Ansi(profile)) _load(p); }
        public void Save(string profile) { fixed (byte* p = Ansi(profile)) _save(p); }

        public uint Get(string name)
        {
            uint value = 0;
            fixed (byte* n = Ansi(name))
                return _get(n, &value, 4) != 0 ? value : throw new InvalidOperationException($"RTSS has no profile property {name}.");
        }

        public void Set(string name, uint value)
        {
            fixed (byte* n = Ansi(name))
                if (_set(n, &value, 4) == 0) throw new InvalidOperationException($"RTSS refused {name} = {value}.");
        }

        public void Dispose() => NativeLibrary.Free(_lib);
    }

    const uint FileMapWrite = 0x2, FileMapRead = 0x4, MemCommit = 0x1000, PageReadWrite = 0x04, MemMapped = 0x40000;
    static readonly IntPtr CurrentProcess = -1;

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryInfo
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public UIntPtr RegionSize;
        public uint State, Protect, Type;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenFileMapping(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);
    [DllImport("kernel32.dll")] static extern bool UnmapViewOfFile(IntPtr view);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern UIntPtr VirtualQuery(IntPtr address, out MemoryInfo info, UIntPtr length);
    [DllImport("kernel32.dll")] static extern unsafe uint K32GetMappedFileNameW(IntPtr process, IntPtr address, char* name, uint size);
}

/// <summary>While gamemode is on and the profile asks: the governor's readings on RTSS's on-screen display, once a second on a timer thread.</summary>
internal sealed class RtssOsd : IDisposable
{
    readonly Timer _timer;
    readonly SystemLoad _load = new();
    readonly string? _bus;
    readonly object _gate = new();
    bool _disposed, _warned;
    bool? _shown;

    public RtssOsd()
    {
        try { _bus = GpuTuner.List().FirstOrDefault()?.BusId; }
        catch (Exception ex) { Log.Info($"RTSS on-screen display: no GPU line ({ex.Message})."); }
        _load.CpuPercent(); // baseline
        _timer = new Timer(_ => Tick(), null, 1000, 1000);
        Log.Info("RTSS on-screen display: on.");
    }

    void Tick()
    {
        if (!Monitor.TryEnter(_gate)) return; // the previous update is still running
        try
        {
            if (_disposed) return;
            GpuSensors? gpu = null;
            if (_bus is not null)
                try { gpu = GpuTuner.ReadSensors(_bus, throughput: false); }
                catch { } // a driver reset makes the card unreadable for a moment
            var (_, used, total) = SystemLoad.Memory();
            var shown = Rtss.ShowOsd(Rtss.OsdText(_load.CpuPercent(), used, total, gpu));
            if (shown == _shown) return;
            _shown = shown;
            Log.Info(shown ? "RTSS on-screen display: showing." : "RTSS on-screen display: waiting for RTSS (not running, or every OSD slot is taken).");
        }
        catch (Exception ex)
        {
            if (!_warned) Log.Warn($"RTSS on-screen display failed: {ex.Message}");
            _warned = true;
        }
        finally { Monitor.Exit(_gate); }
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { Rtss.ReleaseOsd(); }
            catch (Exception ex) { Log.Warn($"RTSS on-screen display: the slot wasn't released: {ex.Message}"); }
        }
        Log.Info("RTSS on-screen display: off.");
    }
}
