using System.Net.NetworkInformation;
using KosToolbox.Core.Devices;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.SystemInfo;

namespace KosGovernor.Core;

/// <summary>
/// Gamemode session report. While gamemode is on the engine samples the CPU, memory and NVIDIA card about once a second and watches what
/// interfered: busy background programs, the game losing focus, devices and network links dropping. At the end it adds the display
/// driver resets and hardware errors the System log recorded meanwhile, and the report goes to sessions.json for the toolbox's
/// Hardware › Stability page. CPU temperature, package power and (Intel) throttling come from PawnIO, when installed (Setup).
/// </summary>
internal sealed class SessionRecorder : IDisposable
{
    internal struct Stat
    {
        public int Count;
        public double Sum, Min, Max;
        public void Add(double v)
        {
            if (Count++ == 0) Min = Max = v;
            else { Min = Math.Min(Min, v); Max = Math.Max(Max, v); }
            Sum += v;
        }
        public readonly double Avg => Count == 0 ? 0 : Sum / Count;
    }

    /// <summary>NVML clock event (throttle) reasons worth reporting; "bad" ones are warnings.</summary>
    internal static readonly (ulong Bit, string Name, bool Bad)[] GpuReasons =
    {
        (0x4, "Power limit", false), (0x20, "Thermal limit (driver)", true), (0x40, "Thermal slowdown (hardware)", true),
        (0x8, "Hardware slowdown", true), (0x80, "Power brake (power supply signal)", true), (0x10, "Sync boost", false),
        (0x2, "Application clock setting", false), (0x100, "Display clock setting", false),
    };

    /// <summary>Device classes whose removal is reported (others are software devices that come and go).</summary>
    static readonly HashSet<string> DeviceClasses = new(StringComparer.OrdinalIgnoreCase)
        { "AudioEndpoint", "MEDIA", "Mouse", "Keyboard", "HIDClass", "USB", "Net", "Bluetooth", "Monitor", "Image", "Camera", "XnaComposite", "XboxComposite" };

    readonly DateTime _start = DateTime.Now;
    readonly string _profile;
    readonly int _self = Environment.ProcessId;
    string? _game;
    long _lastMs = Environment.TickCount64;
    double _seconds;

    readonly SystemLoad _load = new();
    readonly Pdh? _pdh;
    readonly IntPtr _dpc, _isr, _perf, _limit, _freq, _pagesIn;
    internal Stat Cpu, Dpc, Isr, BusyMhz, PerfLimit, CpuTemp, CpuWatts;
    readonly CpuSensors? _cpuSensors;
    internal string? CpuSensorSource, CpuSensorProblem;
    /// <summary>Samples read through PawnIO, and those with the thermal or power-limit flag set (Intel).</summary>
    internal int CpuFlagSamples, CpuThrottled, CpuPowerLimited, CpuSensorFailures;
    internal int Limited;
    internal double PagesRead, PagesPeak;
    internal ulong MinAvail = ulong.MaxValue, TotalPhys;
    internal double PeakCommit;

    readonly string? _bus;
    internal string? GpuName, GpuProblem;
    internal Stat GpuLoad, GpuTemp, Hotspot, Junction, PowerW, GpuMhz;
    internal uint? PowerLimitW;
    internal ulong VramPeak, VramTotal;
    internal int GpuBusy, GpuFailures;
    internal readonly Dictionary<ulong, int> Reasons = new();

    /// <summary>Program name → CPU % × seconds and peak CPU % (share of the whole machine).</summary>
    internal readonly Dictionary<string, (double Sum, double Peak)> Busy = new(StringComparer.OrdinalIgnoreCase);
    internal readonly Dictionary<string, int> FocusLost = new(StringComparer.OrdinalIgnoreCase);
    internal bool GameSeen;
    bool _inGame;
    HashSet<string>? _present;
    readonly Dictionary<string, PnpDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Name → first time and count (grouped: a flapping link or hub can't grow the report without bound).</summary>
    internal readonly Dictionary<string, (DateTime First, int Count)> Removed = new(StringComparer.OrdinalIgnoreCase), LinkDrops = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, bool> _links = new();

    internal SessionRecorder(string profile, bool live = true)
    {
        _profile = profile;
        if (!live) return; // tests
        _load.CpuPercent(); // baseline
        try
        {
            _pdh = new Pdh();
            _dpc = _pdh.Add(@"\Processor Information(_Total)\% DPC Time");
            _isr = _pdh.Add(@"\Processor Information(_Total)\% Interrupt Time");
            _perf = _pdh.Add(@"\Processor Information(_Total)\% Processor Performance");
            _limit = _pdh.Add(@"\Processor Information(_Total)\% Performance Limit");
            _freq = _pdh.Add(@"\Processor Information(_Total)\Processor Frequency");
            _pagesIn = _pdh.Add(@"\Memory\Pages Input/sec");
            _pdh.Collect();
        }
        catch (Exception ex) { Log.Warn($"Session report: no performance counters ({ex.Message})."); }
        if (PawnIo.InstallFolder is null) CpuSensorProblem = "install PawnIO from the toolbox's Setup page to record it";
        else
        {
            try
            {
                _cpuSensors = CpuSensors.Open();
                CpuSensorSource = _cpuSensors.Source;
                _cpuSensors.Read(); // energy baseline
            }
            catch (Exception ex)
            {
                CpuSensorProblem = ex.Message;
                Log.Warn($"Session report: CPU sensors through PawnIO unavailable ({ex.Message}).");
                _cpuSensors?.Dispose();
                _cpuSensors = null;
            }
        }
        try { if (GpuTuner.FirstLight() is { } g) (_bus, GpuName) = (g.BusId, g.Name); } // NVAPI: no NVML session for the report
        catch (Exception ex) { GpuProblem = ex.Message; }
        try
        {
            foreach (var d in PnpDevices.Enumerate(presentOnly: true)) _devices[d.InstanceId] = d;
            _present = PnpDevices.PresentIds();
        }
        catch (Exception ex) { Log.Warn($"Session report: devices aren't watched ({ex.Message})."); }
        Links();
    }

    /// <summary>Every engine tick while gamemode is on; samples at most once a second (the focus every tick).</summary>
    public void Tick(IReadOnlyList<ProcessEntry> snapshot, CpuUsage usage, Func<string, bool> isGame, Func<string, string> label)
    {
        var tree = Engine.GameTree(snapshot, isGame);
        if (_game is null && snapshot.FirstOrDefault(p => isGame(p.Name)) is { } game) _game = label(game.Name);
        Focus(snapshot, tree);
        var nowMs = Environment.TickCount64;
        if (nowMs - _lastMs < 1000) return;
        var dt = (nowMs - _lastMs) / 1000.0;
        _lastMs = nowMs;
        _seconds += dt;
        var cpu = _load.CpuPercent();
        Cpu.Add(cpu);
        Counters(cpu, dt);
        Memory();
        CpuSensorsSample();
        Gpu();
        Programs(snapshot, usage, tree, dt);
        Devices();
        Links();
    }

    void Focus(IReadOnlyList<ProcessEntry> snapshot, HashSet<int> tree)
    {
        if (tree.Count == 0) { _inGame = false; return; }
        GameSeen = true;
        var fg = Native.ForegroundPid();
        var inGame = tree.Contains(fg);
        if (_inGame && !inGame && fg != 0)
        {
            var name = snapshot.FirstOrDefault(p => p.Pid == fg)?.Name ?? $"pid {fg}";
            FocusLost[name] = FocusLost.GetValueOrDefault(name) + 1;
        }
        _inGame = inGame;
    }

    void Counters(double cpu, double dt)
    {
        if (_pdh is null) return;
        _pdh.Collect();
        double? One(IntPtr c) => c == IntPtr.Zero ? null : _pdh.Values(c).Values.Cast<double?>().FirstOrDefault();
        if (One(_dpc) is { } dpc) Dpc.Add(dpc);
        if (One(_isr) is { } isr) Isr.Add(isr);
        if (cpu >= 25 && One(_perf) is { } perf && One(_freq) is { } mhz && mhz > 0) BusyMhz.Add(mhz * perf / 100);
        if (One(_limit) is { } limit)
        {
            PerfLimit.Add(limit);
            if (limit < 99.5) Limited++;
        }
        if (One(_pagesIn) is { } pages)
        {
            PagesRead += pages * dt;
            PagesPeak = Math.Max(PagesPeak, pages);
        }
    }

    void Memory()
    {
        var m = new Native.MemoryStatus { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MemoryStatus>() };
        if (!Native.GlobalMemoryStatusEx(ref m)) return;
        TotalPhys = m.TotalPhys;
        MinAvail = Math.Min(MinAvail, m.AvailPhys);
        if (m.TotalPageFile > 0) PeakCommit = Math.Max(PeakCommit, 100.0 * (m.TotalPageFile - m.AvailPageFile) / m.TotalPageFile);
    }

    void CpuSensorsSample()
    {
        if (_cpuSensors is null) return;
        try { AddCpu(_cpuSensors.Read()); }
        catch (Exception ex)
        {
            if (CpuSensorFailures++ == 0) Log.Warn($"Session report: reading the CPU through PawnIO failed ({ex.Message}).");
        }
    }

    internal void AddCpu(CpuSensorReading r)
    {
        if (r.TemperatureC is { } t) CpuTemp.Add(t);
        if (r.PackageWatts is { } w) CpuWatts.Add(w);
        if (r.ThermalThrottling is not { } throttling) return;
        CpuFlagSamples++;
        if (throttling) CpuThrottled++;
        if (r.PowerLimited == true) CpuPowerLimited++;
    }

    void Gpu()
    {
        if (_bus is null) return;
        GpuSensors s;
        try { s = GpuTuner.ReadSensorsLight(_bus); }
        catch (Exception ex)
        {
            if (GpuFailures++ == 0) Log.Warn($"Session report: reading the GPU failed ({ex.Message}).");
            return;
        }
        AddGpu(s);
    }

    internal void AddGpu(GpuSensors s)
    {
        if (s.TemperatureC is { } t) GpuTemp.Add(t);
        if (s.HotspotC is { } h) Hotspot.Add(h);
        if (s.MemoryJunctionC is { } j) Junction.Add(j);
        if (s.PowerMw is { } p) PowerW.Add(p / 1000.0);
        if (s.PowerLimitMw is { } pl) PowerLimitW = pl / 1000;
        if (s.VramUsed is { } used) VramPeak = Math.Max(VramPeak, used);
        if (s.VramTotal is { } total) VramTotal = total;
        if (s.GpuLoad is not { } load) return;
        GpuLoad.Add(load);
        if (load < 50) return; // clocks and limits only count under load
        GpuBusy++;
        if (s.GraphicsMhz is { } mhz) GpuMhz.Add(mhz);
        foreach (var (bit, _, _) in GpuReasons)
            if ((s.ThrottleReasons & bit) != 0) Reasons[bit] = Reasons.GetValueOrDefault(bit) + 1;
    }

    /// <summary>
    /// CPU use of everything outside the game, its launcher and what they opened.
    /// shortcut: uses the CPU % of the last engine interval for the whole second; a burst between samples is missed with a long interval.
    /// </summary>
    void Programs(IReadOnlyList<ProcessEntry> snapshot, CpuUsage usage, HashSet<int> tree, double dt)
    {
        var now = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in snapshot)
            if (p.Pid != _self && !tree.Contains(p.Pid) && usage.Percent(p) is > 0 and var pct) now[p.Name] = now.GetValueOrDefault(p.Name) + pct;
        foreach (var (name, pct) in now)
        {
            var (sum, peak) = Busy.GetValueOrDefault(name);
            Busy[name] = (sum + pct * dt, Math.Max(peak, pct));
        }
    }

    /// <summary>Devices that disappeared (unplugged, or dropped by their driver); names come from the list taken at the start, or when one appears.</summary>
    void Devices()
    {
        if (_present is null) return;
        try
        {
            var now = PnpDevices.PresentIds();
            if (now.Any(id => !_present.Contains(id)))
                foreach (var d in PnpDevices.Enumerate(presentOnly: true)) _devices[d.InstanceId] = d;
            // One unplug removes several devices with the same name (HID collections): counted once per sample.
            foreach (var name in _present.Where(id => !now.Contains(id)).Select(id => _devices.TryGetValue(id, out var d) && DeviceClasses.Contains(d.Class) ? d.Name : null)
                         .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
                Count(Removed, name);
            _present = now;
        }
        catch (Exception ex)
        {
            Log.Warn($"Session report: devices are no longer watched ({ex.Message}).");
            _present = null;
        }
    }

    /// <summary>Wired and Wi-Fi links that went down.</summary>
    void Links()
    {
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.Wireless80211)) continue;
                var up = n.OperationalStatus == OperationalStatus.Up;
                if (_links.TryGetValue(n.Id, out var was) && was && !up) Count(LinkDrops, n.Name);
                _links[n.Id] = up;
            }
        }
        catch (Exception ex) { Log.Warn($"Session report: reading the network adapters failed ({ex.Message})."); }
    }

    internal static void Count(Dictionary<string, (DateTime First, int Count)> seen, string name) =>
        seen[name] = seen.TryGetValue(name, out var s) ? (s.First, s.Count + 1) : (DateTime.Now, 1);

    /// <summary>Gamemode turned off (or the engine is exiting).</summary>
    public GamemodeSession Finish()
    {
        var end = DateTime.Now;
        var events = new List<StabilityEvent>();
        try { events = Stability.Events(100, _start).Where(e => e.Time <= end.AddMinutes(1)).ToList(); }
        catch (Exception ex) { Log.Warn($"Session report: the System log couldn't be read ({ex.Message})."); }
        return Build(end, events);
    }

    internal GamemodeSession Build(DateTime end, IReadOnlyList<StabilityEvent> events)
    {
        var report = new GamemodeSession { Start = _start, End = end, Game = _game, Profile = _profile };
        var warnings = new List<string>();
        SessionSection Section(string title) { var s = new SessionSection { Title = title }; report.Sections.Add(s); return s; }
        // brief: the warning's name in the one-line summary (the label otherwise)
        void Line(SessionSection s, string label, string value, bool warning = false, string? brief = null)
        {
            s.Lines.Add(new SessionLine { Label = label, Value = value, Warning = warning });
            if (warning) warnings.Add(brief ?? label);
        }

        var duration = Duration(end - _start);
        var info = Section("Session");
        Line(info, "Game", _game ?? "No listed game was open");
        Line(info, "Profile", _profile);
        Line(info, "Time", $"{_start:g} – {end:t} ({duration})");

        var cpu = Section("CPU");
        if (Cpu.Count == 0) Line(cpu, "Load", "Not recorded (session shorter than a second)");
        else
        {
            Line(cpu, "Load", $"average {Cpu.Avg:0}%, peak {Cpu.Max:0}%");
            if (BusyMhz.Count > 0) Line(cpu, "Clock while busy", $"average {BusyMhz.Avg / 1000:0.00} GHz, lowest {BusyMhz.Min / 1000:0.00} GHz");
            if (PerfLimit.Count > 0)
            {
                var share = 100.0 * Limited / PerfLimit.Count;
                if (Limited == 0) Line(cpu, "Speed limit", "Never held below full speed");
                else Line(cpu, "Speed limit", $"Held below full speed {share:0}% of the time (down to {PerfLimit.Min:0}%): heat, a power limit or the power plan", share >= 5, "CPU speed limited");
            }
            if (Dpc.Count > 0) Line(cpu, "DPC time", $"average {Dpc.Avg:0.0}%, peak {Dpc.Max:0.0}% of the CPU");
            if (Isr.Count > 0) Line(cpu, "Interrupt time", $"average {Isr.Avg:0.0}%, peak {Isr.Max:0.0}% of the CPU");
        }
        if (CpuTemp.Count > 0)
        {
            Line(cpu, "Temperature", $"average {CpuTemp.Avg:0} °C, peak {CpuTemp.Max:0} °C ({CpuSensorSource})", CpuTemp.Max >= 95, "CPU temperature");
            if (CpuWatts.Count > 0) Line(cpu, "Package power", $"average {CpuWatts.Avg:0} W, peak {CpuWatts.Max:0} W");
            if (CpuFlagSamples > 0)
            {
                var hot = 100.0 * CpuThrottled / CpuFlagSamples;
                Line(cpu, "Thermal throttling", CpuThrottled == 0 ? "Never" : $"{hot:0.#}% of the time", CpuThrottled > 0, "CPU thermal throttling");
                Line(cpu, "Power limit", CpuPowerLimited == 0 ? "Never reached" : $"Held by the package power limit {100.0 * CpuPowerLimited / CpuFlagSamples:0.#}% of the time");
            }
        }
        else Line(cpu, "Temperature", $"Not recorded: {CpuSensorProblem ?? (CpuSensorFailures > 0 ? "every PawnIO reading failed" : "no reading yet")}");

        if (TotalPhys > 0)
        {
            var mem = Section("Memory");
            var freePct = 100.0 * MinAvail / TotalPhys;
            Line(mem, "Lowest available", $"{Gb(MinAvail)} of {Gb(TotalPhys)} ({freePct:0}%)", freePct < 5, "Low memory");
            Line(mem, "Peak commit", $"{PeakCommit:0}% of the commit limit", PeakCommit >= 90, "Commit near the limit");
            if (_pdh is not null || PagesRead > 0) Line(mem, "Read from disk (hard faults)", $"{PagesRead * 4 / 1024:N0} MB in total, peak {PagesPeak * 4 / 1024:N0} MB/s");
        }

        var gpu = Section(GpuName is null ? "GPU" : $"GPU · {GpuName}");
        if (GpuLoad.Count == 0) Line(gpu, "Not recorded", GpuProblem ?? (GpuFailures > 0 ? "every reading failed" : "only NVIDIA cards are read"));
        else
        {
            Line(gpu, "Load", $"average {GpuLoad.Avg:0}%, peak {GpuLoad.Max:0}%");
            if (GpuMhz.Count > 0) Line(gpu, "Clock under load", $"average {GpuMhz.Avg:0} MHz, lowest {GpuMhz.Min:0} MHz");
            if (GpuTemp.Count > 0)
                Line(gpu, "Temperature", $"peak {GpuTemp.Max:0} °C" + (Hotspot.Count > 0 ? $", hotspot {Hotspot.Max:0} °C" : "") + (Junction.Count > 0 ? $", memory {Junction.Max:0} °C" : ""));
            if (PowerW.Count > 0) Line(gpu, "Power", $"peak {PowerW.Max:0} W" + (PowerLimitW is { } limit ? $" of {limit} W" : ""));
            if (VramTotal > 0) Line(gpu, "VRAM", $"peak {VramPeak / 1048576:N0} of {VramTotal / 1048576:N0} MB", VramPeak >= VramTotal * 0.95, "VRAM full");
            if (GpuBusy == 0) Line(gpu, "Limits", "The card was never above 50% load");
            else
            {
                var any = false;
                foreach (var (bit, name, bad) in GpuReasons)
                    if (Reasons.TryGetValue(bit, out var n) && n > 0)
                    {
                        any = true;
                        var share = 100.0 * n / GpuBusy;
                        Line(gpu, $"Limited by: {name}", $"{share:0}% of the time under load", bad && share >= 1, name);
                    }
                if (!any) Line(gpu, "Limits", "No clock limit under load");
                if (GpuFailures > 0) Line(gpu, "Failed readings", $"{GpuFailures} (a driver reset makes the card unreadable for a moment)");
            }
        }

        var other = Section("Interference");
        var busy = Busy.Select(b => (Name: b.Key, Avg: _seconds > 0 ? b.Value.Sum / _seconds : 0, b.Value.Peak))
            .Where(b => b.Avg >= 1).OrderByDescending(b => b.Avg).Take(5).ToList();
        if (busy.Count == 0) Line(other, "Busy background programs", "None above 1% of the CPU");
        foreach (var b in busy) Line(other, b.Name, $"average {b.Avg:0.0}%, peak {b.Peak:0}% of the CPU", b.Avg >= 5, $"{b.Name} busy");
        if (!GameSeen) Line(other, "Game focus", "Not tracked: no listed game was open");
        else if (FocusLost.Count == 0) Line(other, "Game focus", "The game never lost focus");
        else Line(other, "Game lost focus", $"{FocusLost.Values.Sum()} times: " + string.Join(", ", FocusLost.OrderByDescending(f => f.Value).Take(5).Select(f => $"{f.Key} ({f.Value})")));
        if (Removed.Count == 0) Line(other, "Devices disconnected", "None");
        foreach (var (name, (first, count)) in Removed.OrderBy(r => r.Value.First))
            Line(other, "Disconnected", $"{name} · {Times(count, first)}", true, $"{name} disconnected");
        if (LinkDrops.Count == 0) Line(other, "Network link drops", "None");
        foreach (var (name, (first, count)) in LinkDrops.OrderBy(l => l.Value.First))
            Line(other, "Network link down", $"{name} · {Times(count, first)}", true, $"{name} link down");
        foreach (var e in events.OrderBy(e => e.Time)) Line(other, $"{e.Title} at {e.Time:T}", e.Detail, e.Severe || e.Kind is StabilityKind.DriverReset or StabilityKind.HardwareError, e.Title);

        var parts = new List<string> { duration };
        if (Cpu.Count > 0) parts.Add($"CPU average {Cpu.Avg:0}%");
        if (GpuTemp.Count > 0) parts.Add($"GPU peak {GpuTemp.Max:0} °C");
        parts.Add(warnings.Count == 0 ? "nothing interfered" : $"{warnings.Count} warning{(warnings.Count == 1 ? "" : "s")}: {string.Join(", ", warnings.Distinct().Take(3))}");
        report.Summary = string.Join(" · ", parts);
        return report;
    }

    static string Times(int count, DateTime first) => count == 1 ? $"at {first:T}" : $"{count} times, first at {first:T}";

    internal static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min" : $"{Math.Max(0, (int)t.TotalSeconds)} s";

    static string Gb(ulong bytes) => $"{bytes / 1073741824.0:0.0} GB";

    public void Dispose()
    {
        _pdh?.Dispose();
        _cpuSensors?.Dispose();
    }
}
