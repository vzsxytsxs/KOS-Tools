using System.Collections.ObjectModel;
using System.Windows.Threading;
using KosToolbox.Core.Catalog;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Storage;
using KosToolbox.Core.Text;
using KosToolbox.Core.Power;
using KosToolbox.Core.SystemInfo;
using KosToolbox.Core.Tweaks;

namespace KosToolbox.ViewModels;

/// <summary>A resource on Home (like in Task Manager): its row in the list, and its graph and figures in the inspector when selected.</summary>
public sealed class MonitorCard(string key, string title, string graph) : ObservableObject
{
    public const int Points = 60;
    string _title = title, _subtitle = "", _value = "—", _line = "", _short = "—", _maxText = "100%";
    IReadOnlyList<InfoRow> _details = Array.Empty<InfoRow>();
    double[] _history = new double[Points];
    double _max = 100;
    bool _selected;

    public string Key { get; } = key;
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Subtitle { get => _subtitle; set => Set(ref _subtitle, value); }
    public string Value { get => _value; set => Set(ref _value, value); }
    /// <summary>Second line of its row (model, speed, drive letters).</summary>
    public string Line { get => _line; set => Set(ref _line, value); }
    /// <summary>The figure at the right of its row, short enough for the list.</summary>
    public string Short { get => _short; set => Set(ref _short, value); }
    /// <summary>The figures in one line over the inspector.</summary>
    public IReadOnlyList<InfoRow> Details { get => _details; set => Set(ref _details, value); }
    /// <summary>What the inspector's graph shows, and its top value.</summary>
    public string Graph { get; } = graph + Txt.HomeSixtySeconds;
    public string MaxText { get => _maxText; set => Set(ref _maxText, value); }
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    /// <summary>Last 60 s (newest on the right).</summary>
    public double[] History { get => _history; private set => Set(ref _history, value); }
    /// <summary>Top of the chart: 100 for percentages; network adjusts to the highest recent rate.</summary>
    public double Max { get => _max; set => Set(ref _max, value); }
    /// <summary>Reads the details for the inspector (CPU, memory, GPU, disks); null on rows without details (network).</summary>
    public Func<Task<HardwareDetail?>>? LoadDetail { get; init; }

    public void Push(double v) => History = Shift(_history, v);

    internal static double[] Shift(double[] old, double v)
    {
        var h = new double[old.Length];
        Array.Copy(old, 1, h, 0, old.Length - 1);
        h[^1] = v;
        return h;
    }
}

/// <summary>A logical processor: usage, MHz, parked state and a small chart.</summary>
public sealed class CoreTile(int index) : ObservableObject
{
    string _text = "";
    string? _detail;
    bool _parked;
    double[] _history = new double[30];

    public string Label { get; } = $"CPU {index}";
    /// <summary>Physical core of the logical processor (e.g. "Core 3"; "P"/"E" on hybrid processors).</summary>
    public string? Detail { get => _detail; set => Set(ref _detail, value); }
    public string Text { get => _text; set => Set(ref _text, value); }
    public bool Parked { get => _parked; set => Set(ref _parked, value); }
    public double[] History { get => _history; private set => Set(ref _history, value); }
    public void Push(double v) => History = MonitorCard.Shift(_history, v);
}

public sealed record StatusRow(string Name, string Value);

/// <summary>Live Home dashboard: CPU (and each core), memory, GPU, disks and network, read every second while Home is open.</summary>
public sealed partial class HomeViewModel
{
    readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Dictionary<string, MonitorCard> _cards = new();
    LiveMonitor? _monitor;
    MachineInfo? _machine;
    bool _sampling, _liveFailed;
    IReadOnlyList<StatusRow> _status = Array.Empty<StatusRow>();

    public MonitorCard Cpu { get; private set; } = null!;
    public ObservableCollection<CoreTile> Cores { get; } = new();
    /// <summary>CPU, memory and GPU, in that order at the top of the list.</summary>
    public ObservableCollection<MonitorCard> Hardware { get; } = new();
    /// <summary>Each disk and each physical adapter, in that order.</summary>
    public ObservableCollection<MonitorCard> Resources { get; } = new();
    /// <summary>State of system settings (from the toolbox catalog), updated when Home opens.</summary>
    public IReadOnlyList<StatusRow> SystemStatus { get => _status; private set => Set(ref _status, value); }

    void InitLive()
    {
        Cpu = new MonitorCard("cpu", "CPU", Txt.HomeGraphUsage) { LoadDetail = CpuDetailAsync };
        Hardware.Add(Cpu);
        _cards["cpu"] = Cpu;
        _liveTimer.Tick += async (_, _) => await SampleAsync();
        ReadMemoryAtStart();
    }

    public override void OnShown(bool shown)
    {
        if (!shown) { _liveTimer.Stop(); return; }
        _liveTimer.Start();
        MainViewModel.Fire(SampleAsync());
    }

    async Task SampleAsync()
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            var (machine, reading) = await Task.Run(() =>
            {
                _machine ??= LiveMonitor.Machine();
                _monitor ??= new LiveMonitor();
                return (_machine, _monitor.Read());
            });
            Apply(machine, reading);
            _liveFailed = false;
        }
        catch (Exception ex)
        {
            if (!_liveFailed) Log.Warn($"Home: reading the system status failed: {ex.Message}"); // once, not every second
            _liveFailed = true;
        }
        finally { _sampling = false; }
    }

    void Apply(MachineInfo m, LiveReading r)
    {
        // CPU: the card shows the overall figure; the details (all readings and each core) open on top.
        var parked = r.Cpu.Cores.Count(c => c.Parked == true);
        var upTime = $"{(int)r.Cpu.Uptime.TotalDays}:{r.Cpu.Uptime:hh\\:mm\\:ss}";
        Cpu.Subtitle = m.Cpu;
        Cpu.Value = Cpu.Short = $"{r.Cpu.Usage:0}%";
        Cpu.Line = $"{m.Cpu} · {Ghz(r.Cpu.Mhz)}";
        Cpu.Push(r.Cpu.Usage);
        Cpu.Details = new InfoRow[] { new("Speed", Ghz(r.Cpu.Mhz)), new("Cores", $"{m.Cores} / {m.Logical} threads"), new("Up time", upTime) };
        if (_cpuLive is not null)
            _cpuLive.Rows = new InfoRow[]
            {
                new("Usage", $"{r.Cpu.Usage:0}%"), new("Speed", Ghz(r.Cpu.Mhz)),
                new("Parked", r.Cpu.Cores.Any(c => c.Parked is not null) ? $"{parked} of {r.Cpu.Cores.Count}" : "—"),
                new("Processes", r.Cpu.Processes.ToString()), new("Threads", r.Cpu.Threads.ToString()), new("Handles", r.Cpu.Handles.ToString()),
                new("Up time", upTime),
            };
        while (Cores.Count < r.Cpu.Cores.Count) Cores.Add(new CoreTile(Cores.Count) { Detail = _coreNames.GetValueOrDefault(Cores.Count) });
        foreach (var c in r.Cpu.Cores)
        {
            var tile = Cores[c.Index];
            tile.Push(c.Usage);
            tile.Text = c.Mhz is { } mhz ? $"{c.Usage:0}% · {mhz / 1000:0.00} GHz" : $"{c.Usage:0}%";
            tile.Parked = c.Parked == true;
        }

        // Memory
        var mem = Card("mem", "Memory", Txt.HomeGraphMemory, MemoryDetailAsync);
        var used = 100.0 * r.Memory.Used / Math.Max(1, r.Memory.Total);
        mem.Subtitle = $"{Gb(r.Memory.Total)}{(m.MemoryModules > 0 ? $" · {m.MemoryModules} × {m.MemoryType ?? "RAM"}" : "")}{(m.MemorySpeedMts is { } s ? $" · {s} MT/s" : "")}";
        mem.Value = $"{Gb(r.Memory.Used)} ({used:0}%)";
        mem.Short = $"{used:0}%";
        mem.Line = $"{Gb(r.Memory.Used, false)} / {Gb(r.Memory.Total)}{(m.MemoryType is { } type ? $" · {type}" : "")}{(m.MemorySpeedMts is { } mts ? $" · {mts} MT/s" : "")}";
        mem.Push(used);
        mem.Details = new InfoRow[] { new("In use", Gb(r.Memory.Used)), new("Available", Gb(r.Memory.Total - r.Memory.Used)), new("Cached", Gb(r.Memory.Cached)) };
        if (_memLive is not null)
            _memLive.Rows = new InfoRow[]
            {
                new("In use", $"{Gb(r.Memory.Used)} ({used:0}%)"), new("Available", Gb(r.Memory.Total - r.Memory.Used)),
                new("Committed", $"{Gb(r.Memory.Committed, false)} / {Gb(r.Memory.CommitLimit)}"), new("Cached", Gb(r.Memory.Cached)),
                new("Paged pool", Mb(r.Memory.PagedPool)), new("Non-paged pool", Mb(r.Memory.NonpagedPool)),
            };

        // GPU (NVIDIA through NVML; other cards have no reading yet)
        if (r.Gpu is { } gpu && r.GpuSensors is { } g)
        {
            _gpu = (gpu, g);
            var card = Card("gpu", "GPU", Txt.HomeGraphUsage, GpuDetailAsync);
            card.Subtitle = gpu.Name;
            card.Value = card.Short = g.GpuLoad is { } load ? $"{load}%" : "—";
            card.Line = g.TemperatureC is { } temp ? $"{gpu.Name} · {temp} °C" : gpu.Name;
            card.Push(g.GpuLoad ?? 0);
            card.Details = new InfoRow[]
            {
                new("Temperature", g.TemperatureC is { } t ? $"{t} °C" : "—"), new("Core clock", g.GraphicsMhz is { } core ? $"{core} MHz" : "—"),
                new("VRAM", g.VramUsed is { } vu && g.VramTotal is { } vt ? $"{vu / 1048576} / {vt / 1048576} MB" : "—"),
            };
            if (_gpuLive is not null) _gpuLive.Rows = GpuPageViewModel.Format(g);
        }

        // Disks
        foreach (var d in r.Disks)
        {
            var number = d.Number;
            var card = Card($"disk{number}", $"Disk {number}", Txt.HomeGraphDisk, () => DiskDetailAsync(number), resource: true);
            card.Subtitle = $"{d.Model} · {d.Kind}";
            card.Value = card.Short = d.Active is { } a ? $"{a:0}%" : "—";
            card.Line = string.Join(" · ", new[] { d.Model, d.Kind }.Concat(d.Volumes.Select(v => v.Split(' ')[0])));
            card.Push(d.Active ?? 0);
            card.Details = new InfoRow[]
            {
                new("Partitions", d.Volumes.Count > 0 ? string.Join(", ", d.Volumes) : "No drive letter"),
                new("Response time", d.LatencyMs is { } l ? $"{l:0.00} ms" : "—"),
                new("Read", Rate(d.ReadBps)), new("Write", Rate(d.WriteBps)),
            };
        }

        // Network (physical adapters)
        foreach (var n in r.Networks)
        {
            var card = Card($"net{n.Name}", "Network", Txt.HomeGraphNetwork);
            card.Subtitle = n.Name;
            card.Value = n.Up ? $"↑ {Bits(n.SendBps)}   ↓ {Bits(n.ReceiveBps)}" : "Disconnected";
            card.Short = n.Up ? Bits(n.SendBps + n.ReceiveBps) : "—";
            card.Line = $"{n.Name} · {(n.Up ? n.LinkBps > 0 ? Bits(n.LinkBps) : "Connected" : "Disconnected")}";
            card.Push((n.SendBps + n.ReceiveBps) / 1000);
            card.Max = Math.Max(100, card.History.Max() * 1.25); // kbps, automatic scale like in Task Manager
            card.MaxText = Bits(card.Max * 1000);
            card.Details = new InfoRow[]
            {
                new("Status", n.Up ? "Connected" : "Disconnected"), new("Link speed", n.LinkBps > 0 ? Bits(n.LinkBps) : "—"),
                new("Send", Bits(n.SendBps)), new("Receive", Bits(n.ReceiveBps)),
            };
        }
    }

    /// <summary>Card for a resource; with <paramref name="load"/>, it has details and goes among CPU/memory/GPU unless it's a <paramref name="resource"/> (disks).</summary>
    MonitorCard Card(string key, string title, string graph, Func<Task<HardwareDetail?>>? load = null, bool resource = false)
    {
        if (_cards.TryGetValue(key, out var card)) return card;
        _cards[key] = card = new MonitorCard(key, title, graph) { LoadDetail = load };
        (load is null || resource ? Resources : Hardware).Add(card);
        return card;
    }

    static string Ghz(double? mhz) => mhz is { } v ? $"{v / 1000:0.00} GHz" : "—";
    static string Gb(ulong bytes, bool unit = true) => $"{bytes / 1073741824.0:0.0}{(unit ? " GB" : "")}";
    static string Mb(ulong bytes) => $"{bytes / 1048576.0:N0} MB";
    static string Rate(double? bytes) => bytes switch
    {
        null => "—",
        >= 1048576 => $"{bytes / 1048576:0.0} MB/s",
        _ => $"{bytes / 1024:0} KB/s",
    };
    static string Bits(double bps) => bps switch
    {
        >= 1e9 => $"{bps / 1e9:0.##} Gbps",
        >= 1e6 => $"{bps / 1e6:0.0} Mbps",
        _ => $"{bps / 1e3:0} Kbps",
    };

    // ---------- settings status ----------

    /// <summary>Catalog tweaks shown on Home (the value is the state read now; unavailable ones do not appear).</summary>
    static readonly (string Id, string? Name)[] StatusTweaks =
    {
        ("game-bar", null), ("fullscreen-mode", null), ("hags", "Hardware-accelerated GPU scheduling (HAGS)"),
        ("nv-pstate", "NVIDIA dynamic P-states"), ("nv-msi", "NVIDIA MSI mode"), ("mpo", null), ("vbs", null), ("hvci", null), ("hypervisor", null),
        ("power-throttling", null), ("global-timer-resolution", null), ("use-platform-tick", null), ("processor-idle", null),
        ("spectre-meltdown", null), ("windows-update", null), ("uac", null), ("firewall", null),
    };

    static List<StatusRow> ReadStatus()
    {
        var rows = new List<StatusRow>();
        try
        {
            var plan = PowerPlans.List().FirstOrDefault(p => p.IsActive);
            if (plan is not null) rows.Add(new StatusRow("Power plan", plan.Name));
        }
        catch (Exception ex) { Log.Warn($"Home: reading the power plan failed: {ex.Message}"); }
        if (CpuPowerControl.Read() is { } cpu) rows.Add(new StatusRow(Txt.StatusCpuFrequency, cpu.Describe()));
        Checks(rows);
        foreach (var (id, name) in StatusTweaks)
        {
            if (KosCatalog.Find(id) is not { } t) continue;
            TweakState s;
            try { s = TweakEvaluator.Evaluate(t); }
            catch (Exception ex) { Log.Warn($"Home: {id}: {ex.Message}"); continue; }
            if (s.Status == TweakStatus.Unavailable) continue;
            var value = s.Status switch
            {
                TweakStatus.Matched => t.Options.FirstOrDefault(o => o.Key == s.Option)?.Label ?? s.Option ?? "—",
                TweakStatus.Partial => $"Partly {t.Options.FirstOrDefault(o => o.Key == s.ClosestOption)?.Label ?? "set"}",
                TweakStatus.NotConfigured => "Windows default",
                TweakStatus.ReadError => "Couldn't read",
                _ => "Custom",
            };
            rows.Add(new StatusRow(name ?? t.Name, value));
        }
        return rows;
    }

    /// <summary>Hardware checks: a monitor below its highest refresh rate, Resizable BAR and the GPU link, BypassIO per fixed drive.</summary>
    static void Checks(List<StatusRow> rows)
    {
        try
        {
            var displays = WindowsDisplays.List();
            foreach (var d in displays)
            {
                // Modes come in landscape; a rotated screen reports its size swapped.
                var (w, h) = d.Orientation is 1 or 3 ? (d.Height, d.Width) : (d.Width, d.Height);
                var best = WindowsDisplays.Modes(d.GdiName).Where(m => m.Width == w && m.Height == h).Select(m => m.Hz).DefaultIfEmpty(d.Hz).Max();
                rows.Add(new StatusRow(Txt.StatusRefresh(displays.Count > 1 ? d.Name : null), Txt.StatusRefreshValue(d.Hz, best, w, h)));
            }
        }
        catch (Exception ex) { Log.Warn($"Home: reading the refresh rates failed: {ex.Message}"); }
        try
        {
            foreach (var g in GpuTuner.List())
            {
                var bus = GpuTuner.ReadBus(g.BusId);
                if (bus is { Bar1Bytes: { } bar1, VramBytes: { } vram }) rows.Add(new StatusRow(Txt.StatusRebar, Txt.StatusRebarValue(bar1, vram)));
                if (bus.LinkGen is not null) rows.Add(new StatusRow(Txt.StatusGpuLink, Txt.StatusGpuLinkValue(bus.LinkGen, bus.LinkWidth, bus.CardGen)));
            }
        }
        catch (Exception ex) { Log.Info($"Home: no NVIDIA bus reading ({ex.Message})."); }
        try
        {
            var counts = Txt.StabilityCounts(StabilityPageViewModel.Recent(Stability.Events(200)));
            rows.Add(new StatusRow(Txt.StatusStability, counts.Length > 0 ? counts : "None"));
        }
        catch (Exception ex) { Log.Warn($"Home: reading the crash history failed: {ex.Message}"); }
        foreach (var drive in System.IO.DriveInfo.GetDrives().Where(d => d.DriveType == System.IO.DriveType.Fixed && d.IsReady))
        {
            try
            {
                if (BypassIo.Query(drive.RootDirectory.FullName) is { } s)
                    rows.Add(new StatusRow(Txt.StatusBypassIo(drive.Name.TrimEnd('\\')), Txt.StatusBypassIoValue(s.Supported, s.Driver, s.Reason)));
            }
            catch (Exception ex) { Log.Warn($"Home: BypassIO on {drive.Name}: {ex.Message}"); }
        }
    }
}
