using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.Tools;
using Microsoft.Win32;

namespace KosGovernor.Core;

/// <summary>
/// RivaTuner Statistics Server, when the user installed it: the governor never installs it and starts it only when "Keep RTSS
/// running" is on, and RTSS injects into the games itself (some anti-cheats refuse that). The on-screen display text goes through RTSS's shared memory, in an OSD slot of its
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
    public static bool ShowOsd(string text, float[]? cores = null, float graphMaxUs = 50000) => WithView(m => Write(m, text, cores, graphMaxUs)) is OsdResult.Written or OsdResult.Busy;

    /// <summary>Gives the governor's OSD slot back (gamemode off, engine exit, and at engine start after a crash).</summary>
    public static int ReleaseOsd() => WithView(Release) ?? 0;

    /// <summary>
    /// Writes into the first slot this program owns, else claims the first free one. Slot 0 stays for the primary client
    /// (MSI Afterburner), as the SDK asks. The header comes from a mapping any program in the session could have created
    /// before RTSS: every offset is checked against the view, and the span refuses anything outside it.
    /// </summary>
    internal static OsdResult Write(Span<byte> mem, string text, float[]? cores = null, float graphMaxUs = 50000)
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
        // The frametime graph: an object in the slot's buffer (v2.12+) that the text places with <OBJ=offset>; without the buffer, no graph line.
        var graph = text.Contains(GraphTag) && version >= EmbeddedObjects && size >= BufferAt + GraphSize ? entry.Slice(BufferAt, GraphSize) : Span<byte>.Empty;
        if (graph.IsEmpty) text = text.Replace(GraphTag + "\n", "").Replace(GraphTag, "");
        // The CPU bars: a second object further into the buffer, filled with one load per core.
        var bars = text.Contains(CoresTag) && cores is { Length: > 0 and <= MaxCores } && version >= EmbeddedObjects && size >= BufferAt + CoresAt + GraphSize + cores.Length * 4
            ? entry.Slice(BufferAt + CoresAt, GraphSize + cores.Length * 4) : Span<byte>.Empty;
        if (bars.IsEmpty) text = text.Replace(CoresTag + "\n", "").Replace(CoresTag, "");
        if (version >= Locking)
        {
            // Bit 0 is set while RTSS renders the OSD: skip this update rather than wait.
            ref var busy = ref MemoryMarshal.Cast<byte, int>(mem.Slice(BusyAt, 4))[0];
            if ((Interlocked.Or(ref busy, 1) & 1) != 0) return OsdResult.Busy;
            try { Fill(field, text); FrametimeGraph(graph, graphMaxUs); CoreBars(bars, cores); }
            finally { Interlocked.And(ref busy, ~1); }
        }
        else { Fill(field, text); FrametimeGraph(graph, graphMaxUs); CoreBars(bars, cores); }
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

    // RTSS_SHARED_MEMORY_OSD_ENTRY.buffer (v2.12+) and RTSS_EMBEDDED_OBJECT_GRAPH: signature 'GR00', size, width, height, margin, flags, min, max, data count.
    const int BufferAt = OsdExAt + OsdExSize, GraphSize = 36;
    const uint EmbeddedObjects = 0x0002000C, GraphSignature = 0x47523030, GraphFrametime = 4;
    /// <summary>The graph object sits at offset 0 of the slot's buffer.</summary>
    internal const string GraphTag = "<OBJ=00000000>";
    /// <summary>The CPU bar chart object sits at offset 0x400 of the buffer (36 bytes of header, then one float per core).</summary>
    internal const string CoresTag = "<OBJ=00000400>";
    const int CoresAt = 0x400, MaxCores = 64;
    const uint GraphFilled = 1;

    /// <summary>A filled graph with one data point per core (0 to 100 %): each point is a bar one character wide, two lines high.</summary>
    static void CoreBars(Span<byte> obj, float[]? loads)
    {
        if (obj.IsEmpty || loads is null) return;
        var w = MemoryMarshal.Cast<byte, int>(obj);
        (w[0], w[1], w[2], w[3], w[4], w[5]) = ((int)GraphSignature, GraphSize + loads.Length * 4, -loads.Length, -2, 1, (int)GraphFilled);
        (w[6], w[7]) = (BitConverter.SingleToInt32Bits(0f), BitConverter.SingleToInt32Bits(100f));
        w[8] = loads.Length;
        for (var i = 0; i < loads.Length; i++) w[9 + i] = BitConverter.SingleToInt32Bits(Math.Clamp(loads[i], 0f, 100f));
    }

    /// <summary>
    /// A frametime graph RTSS fills from the game's own frames (no samples from us), 32 characters wide and 3 lines high, 0–50 ms,
    /// as the SDK's sample embeds it.
    /// </summary>
    static void FrametimeGraph(Span<byte> obj, float maxUs)
    {
        if (obj.IsEmpty) return;
        var w = MemoryMarshal.Cast<byte, int>(obj);
        (w[0], w[1], w[2], w[3], w[4], w[5]) = ((int)GraphSignature, GraphSize, -32, -3, 1, (int)GraphFrametime);
        (w[6], w[7]) = (BitConverter.SingleToInt32Bits(0f), BitConverter.SingleToInt32Bits(maxUs));
        w[8] = 0;
    }

    /// <summary>
    /// The OSD text: RTSS fills in &lt;FR&gt; (frames per second), &lt;FT&gt; (frame time) and the frametime graph for each game; the rest
    /// comes from the governor's readings (NVIDIA cards only for the GPU line). Only the chosen <paramref name="items"/>.
    /// </summary>
    internal static string OsdText(RtssOsdItems items, double cpuPercent, ulong memUsed, ulong memTotal, GpuSensors? gpu, bool compact = false, RtssOsdColor color = RtssOsdColor.None,
        RtssOsdStyle style = RtssOsdStyle.Classic)
    {
        if (style != RtssOsdStyle.Classic) return StyledText(style, items, cpuPercent, memUsed, memTotal, gpu, color);
        var c = CultureInfo.InvariantCulture;
        var lines = new List<string>();
        // The accent color is RTSS's text tag <C0=RRGGBB> (a color slot) used with <C0> ... <C> around each label.
        var accent = AccentHex(color);
        string Label(string label) => accent is null || label.Length == 0 ? label : $"<C0>{label.TrimEnd()}<C>{label[label.TrimEnd().Length..]}";
        void Line(string label, List<string> parts) { if (parts.Count > 0) lines.Add(Label(label) + string.Join("  ", parts)); }
        bool Has(RtssOsdItems item) => (items & item) != 0;

        var frames = new List<string>();
        if (Has(RtssOsdItems.Fps)) frames.Add("<FR>");
        if (Has(RtssOsdItems.FrameTime)) frames.Add("<FT> ms");
        Line(Has(RtssOsdItems.Fps) ? "FPS  " : "Frame time  ", frames);
        if (Has(RtssOsdItems.FrametimeGraph)) lines.Add(GraphTag);
        if (gpu is { } g)
        {
            var parts = new List<string>();
            if (Has(RtssOsdItems.GpuLoad) && g.GpuLoad is { } load) parts.Add(string.Format(c, "{0:0}%", load));
            if (Has(RtssOsdItems.GpuTemperature) && g.TemperatureC is { } temp) parts.Add(string.Format(c, "{0:0}°C", temp));
            if (Has(RtssOsdItems.GpuClock) && g.GraphicsMhz is { } mhz) parts.Add(string.Format(c, "{0} MHz", mhz));
            if (Has(RtssOsdItems.GpuPower) && g.PowerMw is { } mw) parts.Add(string.Format(c, "{0:0} W", mw / 1000.0));
            if (Has(RtssOsdItems.Vram) && g.VramUsed is { } used && g.VramTotal is { } total and > 0)
                parts.Add(Label("VRAM") + string.Format(c, " {0:0.0}/{1:0.0} GB", used / 1073741824.0, total / 1073741824.0));
            Line("GPU  ", parts);
        }
        var system = new List<string>();
        if (Has(RtssOsdItems.Cpu)) system.Add(Label("CPU  ") + string.Format(c, "{0:0}%", cpuPercent));
        if (Has(RtssOsdItems.Ram)) system.Add(Label("RAM ") + string.Format(c, "{0:0.0}/{1:0.0} GB", memUsed / 1073741824.0, memTotal / 1073741824.0));
        Line("", system);
        // The graph is an object on a line of its own: the compact layout keeps it there.
        var text = compact ? string.Join("  |  ", lines.Where(l => l != GraphTag)) + (lines.Contains(GraphTag) ? "\n" + GraphTag : "") : string.Join("\n", lines);
        return accent is null || text.Length == 0 ? text : $"<C0={accent}>{text}";
    }

    /// <summary>
    /// The Steam-like and benchmark looks. Two color slots: C0 for the labels (the accent, or grey / ghostly green) and C1 for the values (white).
    /// Sizes are RTSS's size tags (S1=190 is 190 %, S2=60 is 60 %); the bars object needs the per-core loads in <see cref="ShowOsd"/>.
    /// </summary>
    internal static string StyledText(RtssOsdStyle style, RtssOsdItems items, double cpuPercent, ulong memUsed, ulong memTotal, GpuSensors? gpu, RtssOsdColor color)
    {
        var c = CultureInfo.InvariantCulture;
        bool Has(RtssOsdItems item) => (items & item) != 0;
        var steam = style == RtssOsdStyle.Steam;
        var labelColor = AccentHex(color) ?? (steam ? "A8A8A8" : "A0FFA0");
        static string L(string s) => $"<C0>{s}<C>";
        static string V(string s) => $"<C1>{s}<C>";
        static string Small(string s) => $"<S2=60><S2>{s}<S>";
        var lines = new List<string>();

        if (Has(RtssOsdItems.Fps))
        {
            var head = $"<S1={(steam ? 190 : 230)}><S1>{V("<FR>")}<S> {Small(L("FPS"))}";
            if (Has(RtssOsdItems.FrameTime)) head += $"   {Small(V("<FT>") + " " + L("ms"))}";
            lines.Add(head);
        }
        else if (Has(RtssOsdItems.FrameTime)) lines.Add($"{L("FRAME TIME")} {V("<FT>")} {L("ms")}");
        if (Has(RtssOsdItems.FrametimeGraph)) lines.Add(GraphTag);

        if (gpu is { } g)
        {
            var parts = new List<string>();
            if (Has(RtssOsdItems.GpuLoad) && g.GpuLoad is { } load) parts.Add(V(string.Format(c, "{0:0}%", load)));
            if (Has(RtssOsdItems.GpuTemperature) && g.TemperatureC is { } temp) parts.Add(V(string.Format(c, "{0:0}°C", temp)));
            if (Has(RtssOsdItems.GpuClock) && g.GraphicsMhz is { } mhz) parts.Add(V(string.Format(c, "{0} MHz", mhz)));
            if (Has(RtssOsdItems.GpuPower) && g.PowerMw is { } mw) parts.Add(V(string.Format(c, "{0:0} W", mw / 1000.0)));
            if (parts.Count > 0) lines.Add($"{L("GPU")}  {string.Join("  ", parts)}");
            if (Has(RtssOsdItems.Vram) && g.VramUsed is { } used && g.VramTotal is { } total and > 0)
                lines.Add($"{L("VRAM")} {V(string.Format(c, "{0:0.0}/{1:0.0} GB", used / 1073741824.0, total / 1073741824.0))}");
        }
        var cpu = Has(RtssOsdItems.Cpu) ? $"{L("CPU")}  {V(string.Format(c, "{0:0}%", cpuPercent))}" : null;
        var ram = Has(RtssOsdItems.Ram) ? $"{L("RAM")} {V(string.Format(c, "{0:0.0}/{1:0.0} GB", memUsed / 1073741824.0, memTotal / 1073741824.0))}" : null;
        if (steam) { if (cpu is not null || ram is not null) lines.Add(string.Join("   ", new[] { cpu, ram }.Where(x => x is not null))); }
        else
        {
            if (cpu is not null) { lines.Add(cpu); lines.Add(CoresTag); } // one bar per core under the CPU line
            if (ram is not null) lines.Add(ram);
        }
        var text = string.Join("\n", lines);
        return text.Length == 0 ? text : $"<C0={labelColor}><C1=FFFFFF>{text}";
    }

    /// <summary>RRGGBB of an accent color, null for none.</summary>
    internal static string? AccentHex(RtssOsdColor color) => color switch
    {
        RtssOsdColor.GhostlyGreen => "A0FFA0", RtssOsdColor.PureGreen => "00FF00", RtssOsdColor.ElectricBlue => "00A0FF", RtssOsdColor.BrightYellow => "FFFF00",
        RtssOsdColor.HotPink => "FF69B4", RtssOsdColor.PureOrange => "FF8000", _ => null,
    };

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

    /// <summary>The limit of a profile: "" = the global one, "game.exe" = that game's own.</summary>
    internal static FrameLimit ReadLimit(string profile = "")
    {
        using var hooks = new Hooks();
        hooks.Load(profile);
        return new FrameLimit(hooks.Get("FramerateLimit"), hooks.Get("FramerateLimitDenominator"));
    }

    internal static void WriteLimit(FrameLimit limit, string profile = "")
    {
        limit = limit with { Denominator = Math.Max(1, limit.Denominator) };
        WriteProfile(profile, ("FramerateLimitDenominator", limit.Denominator), ("FramerateLimit", limit.Limit));
    }

    // ---------- per-game profiles ----------

    static readonly System.Text.RegularExpressions.Regex GameName = new(@"^[A-Za-z0-9][A-Za-z0-9 ._()\-]{0,63}\.exe$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>A program name RTSS can keep a profile for: letters, digits, spaces and "._()-", ending in .exe, no folders.</summary>
    public static bool IsGameName(string name) => GameName.IsMatch(name) && !name.Contains("..", StringComparison.Ordinal);

    /// <summary>RTSS's Profiles folder (next to the DLL the engine trusts): "Global", and each game's profile as "game.exe.cfg" (the name the DLL writes).</summary>
    static string ProfilesFolder() => Path.Combine(Path.GetDirectoryName(HooksDll(out var problem) ?? throw new InvalidOperationException(problem))!, "Profiles");

    /// <summary>
    /// Makes sure the game has a profile file, copied from the global one when there is none (RTSS keeps every setting in each profile, so a
    /// fresh one would otherwise start from RTSS's defaults, not the user's overlay). True when it was created.
    /// </summary>
    internal static bool EnsureProfile(string exe)
    {
        if (!IsGameName(exe)) throw new ArgumentException("Not a program name.", nameof(exe));
        var folder = ProfilesFolder();
        var path = Path.Combine(folder, exe + ".cfg");
        if (File.Exists(path)) return false;
        File.Copy(Path.Combine(folder, "Global"), path);
        return true;
    }

    internal static void DeleteProfile(string exe)
    {
        if (!IsGameName(exe)) throw new ArgumentException("Not a program name.", nameof(exe));
        var path = Path.Combine(ProfilesFolder(), exe + ".cfg");
        if (File.Exists(path)) File.Delete(path);
        WithView(RequestProfileUpdate);
    }

    /// <summary>The frame limiting mode: profile property SyncLimiter, 0 async, 1 front edge sync, 2 back edge sync, 3 NVIDIA Reflex (RTSS's list order).</summary>
    internal const uint MaxMode = 3;

    internal static uint ReadMode()
    {
        using var hooks = new Hooks();
        hooks.Load("");
        return hooks.Get("SyncLimiter");
    }

    internal static void WriteMode(uint mode) => WriteGlobal(("SyncLimiter", mode <= MaxMode ? mode : throw new ArgumentOutOfRangeException(nameof(mode))));

    internal static string ModeName(uint mode) => mode switch { 0 => "async", 1 => "front edge sync", 2 => "back edge sync", 3 => "NVIDIA Reflex", _ => $"mode {mode}" };

    /// <summary>Writes the global profile, reads every value back from the file and tells the running games to reload it.</summary>
    static void WriteGlobal(params (string Name, uint Value)[] values) => WriteProfile("", values);

    static void WriteProfile(string profile, params (string Name, uint Value)[] values)
    {
        using (var hooks = new Hooks())
        {
            hooks.Load(profile);
            foreach (var (name, value) in values) hooks.Set(name, value);
            hooks.Save(profile);
        }
        using (var hooks = new Hooks())
        {
            hooks.Load(profile);
            foreach (var (name, value) in values)
                if (hooks.Get(name) is var saved && saved != value) throw new InvalidOperationException($"RTSS's {(profile.Length == 0 ? "global" : profile)} profile reads {name} = {saved} after writing {value}.");
        }
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
    internal static string? HooksDll(out string? problem) => InstallFile("RTSSHooks64.dll", out problem);

    /// <summary>
    /// With "Keep RTSS running" on: starts RTSS (as the normal user, like the Start menu would; it sits in the tray) when it isn't
    /// running, because it has to be up before a game opens to hook it. Null when it runs or was started, else the reason.
    /// </summary>
    public static string? EnsureRunning()
    {
        if (System.Diagnostics.Process.GetProcessesByName("RTSS").Length > 0) return null;
        var exe = InstallFile("RTSS.exe", out var problem);
        if (exe is null) return problem;
        try { UserLaunch.StartProgram(exe); return null; }
        catch (Exception ex) { return ex.Message; }
    }

    static string? InstallFile(string file, out string? problem)
    {
        problem = null;
        string? dir;
        using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(@"SOFTWARE\Unwinder\RTSS"))
            dir = key?.GetValue("InstallDir") as string;
        if (string.IsNullOrWhiteSpace(dir)) { problem = "RivaTuner Statistics Server isn't installed"; return null; }
        var dll = Path.GetFullPath(Path.Combine(dir, file));
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
    readonly Func<RtssOsdItems> _items;
    readonly Func<(bool Compact, RtssOsdColor Color, RtssOsdStyle Style)> _style;

    public RtssOsd(Func<RtssOsdItems> items, Func<(bool Compact, RtssOsdColor Color, RtssOsdStyle Style)>? style = null)
    {
        _items = items;
        _style = style ?? (() => (false, RtssOsdColor.None, RtssOsdStyle.Classic));
        try { _bus = GpuTuner.FirstLight()?.BusId; }
        catch (Exception ex) { Log.Info($"RTSS on-screen display: no GPU line ({ex.Message})."); }
        _load.CpuPercent(); // baseline
        _timer = new Timer(_ => Tick(), null, 1000, 1000);
        Log.Info("RTSS on-screen display: on.");
    }

    void Tick()
    {
        if (!Monitor.TryEnter(_gate)) return; // the previous update is still running
        using var measured = TaskMeter.Measure(EngineTask.Rtss);
        try
        {
            if (_disposed) return;
            GpuSensors? gpu = null;
            if (_bus is not null)
                try { gpu = GpuTuner.ReadSensorsLight(_bus, power: (_items() & RtssOsdItems.GpuPower) != 0); } // NVML only for board power
                catch { } // a driver reset makes the card unreadable for a moment
            var (_, used, total) = SystemLoad.Memory();
            var (compact, color, style) = _style();
            var cores = style == RtssOsdStyle.Benchmark ? _load.CoreLoads() : null;
            var shown = Rtss.ShowOsd(Rtss.OsdText(_items(), _load.CpuPercent(), used, total, gpu, compact, color, style), cores, style == RtssOsdStyle.Classic ? 50000 : 25000);
            if (shown == _shown) return;
            _shown = shown;
            Log.Info(shown ? "RTSS on-screen display: showing." : "RTSS on-screen display: waiting for RTSS (not running, or every OSD slot is taken). Start RTSS before the game, or turn on \"Keep RTSS running\".");
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
