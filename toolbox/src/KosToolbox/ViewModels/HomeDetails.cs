using System.Collections.ObjectModel;
using System.Windows.Input;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Storage;
using KosToolbox.Core.SystemInfo;
using KosToolbox.Core.Tools;

namespace KosToolbox.ViewModels;

/// <summary>Label → value block in the details; live reads replace <see cref="Rows"/> every second.</summary>
public sealed class InfoSection(string title, int columns = 2) : ObservableObject
{
    IReadOnlyList<InfoRow> _rows = Array.Empty<InfoRow>();
    public string Title { get; } = title;
    public int Columns { get; } = columns;
    public IReadOnlyList<InfoRow> Rows { get => _rows; set => Set(ref _rows, value); }
    public string? Note { get; init; }
    /// <summary>Monospaced text (the SPD register dump).</summary>
    public string? Mono { get; init; }
}

/// <summary>Each logical processor (the same blocks that Home updates).</summary>
public sealed record CoresSection(ObservableCollection<CoreTile> Cores);

/// <summary>Timings in groups (Primary, Secondary, Tertiary), as in ASRock Timing Configurator.</summary>
public sealed record TimingsSection(IReadOnlyList<DramTimingGroup> Groups);

/// <summary>Block with a button and the progress of the action (e.g. reading the SPD through CPU-Z).</summary>
public sealed class ActionSection(string title, string text, ICommand command) : ObservableObject
{
    string _button = "", _status = "";
    bool _isError;
    public string Title { get; } = title;
    public string Text { get; } = text;
    public ICommand Command { get; } = command;
    public string Button { get => _button; set => Set(ref _button, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public bool IsError { get => _isError; set => Set(ref _isError, value); }
}

public sealed class DetailTab(string title, int columns = 1) : ObservableObject
{
    public string Title { get; } = title;
    /// <summary>2 = sections side by side (memory sticks).</summary>
    public int Columns { get; } = columns;
    public string? Note { get; init; }
    /// <summary>Full-width block above the sections (e.g. the CPU-Z button).</summary>
    public object? Header { get; init; }
    public ObservableCollection<object> Sections { get; } = new();
}

/// <summary>A component's tabs, under its graph in Home's inspector.</summary>
public sealed class HardwareDetail(string title, string subtitle, params DetailTab[] tabs) : ObservableObject
{
    DetailTab _selected = tabs[0];
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public IReadOnlyList<DetailTab> Tabs { get; } = tabs;
    public bool HasTabs => Tabs.Count > 1;
    public DetailTab SelectedTab { get => _selected; set => Set(ref _selected, value ?? _selected); }
}

/// <summary>The selected hardware row in Home's inspector: the row's graph and figures, and its details once read (network has none).</summary>
public sealed class HardwareInspector(MonitorCard card) : ObservableObject
{
    HardwareDetail? _detail;
    public MonitorCard Card => card;
    public HardwareDetail? Detail { get => _detail; set { if (Set(ref _detail, value)) { Raise(nameof(Title)); Raise(nameof(Chips)); } } }
    public string Title => _detail?.Title ?? card.Title;
    public IReadOnlyList<string> Chips => (_detail?.Subtitle ?? card.Subtitle).Split(" · ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>CPU, memory and GPU details, read when their row is first selected and kept for the session; disks are read again on every selection.</summary>
public sealed partial class HomeViewModel
{
    HardwareDetail? _cpuDetail, _memDetail, _gpuDetail;
    InfoSection? _cpuLive, _memLive, _gpuLive;
    (GpuDevice Device, GpuSensors Sensors)? _gpu;
    Dictionary<int, string> _coreNames = new();
    ActionSection? _cpuzAction;
    DetailTab? _spdTab, _timingsTab;
    bool _readingCpuz;
    CpuzMemory? _cpuz;
    DateTime _cpuzTime;
    string? _cpuzError;

    async Task<HardwareDetail?> CpuDetailAsync()
    {
        if (_cpuDetail is null)
        {
            CpuDetails info;
            try { info = await Task.Run(HardwareInfo.Cpu); }
            catch (Exception ex) { Log.Error("Reading the processor details failed", ex); Toasts.Show(ex.Message, ToastKind.Error); return null; }
            _cpuLive = new InfoSection("Live");
            var overview = new DetailTab("Overview");
            overview.Sections.Add(_cpuLive);
            overview.Sections.Add(new InfoSection("Processor") { Rows = info.Processor });
            overview.Sections.Add(new InfoSection("Cache", 1) { Rows = info.Cache });
            overview.Sections.Add(new InfoSection("Instructions", 1) { Rows = new[] { new InfoRow("Supported", string.Join(", ", info.Instructions)) } });
            if (info.Platform.Count > 0) overview.Sections.Add(new InfoSection("Platform", 1) { Rows = info.Platform });
            var cores = new DetailTab("Cores") { Note = "Usage and speed of each logical processor, with the physical core it belongs to. Dimmed = parked by Windows." };
            cores.Sections.Add(new CoresSection(Cores));
            NameCores(info.Topology);
            _cpuDetail = new HardwareDetail("Processor", info.Processor.FirstOrDefault()?.Value ?? "CPU", overview, cores);
        }
        return _cpuDetail;
    }

    void NameCores(CpuTopology t)
    {
        var classes = t.Cores.Select(c => c.EfficiencyClass).Distinct().ToList();
        var top = classes.DefaultIfEmpty((byte)0).Max();
        foreach (var core in t.Cores)
            foreach (var logical in core.Logical)
                _coreNames[logical] = $"Core {core.Index}" + (classes.Count > 1 ? core.EfficiencyClass == top ? " · P" : " · E" : "");
        for (var i = 0; i < Cores.Count; i++) Cores[i].Detail = _coreNames.GetValueOrDefault(i);
    }

    async Task<HardwareDetail?> MemoryDetailAsync()
    {
        if (_memDetail is null)
        {
            IReadOnlyList<MemoryModule> modules;
            MemoryBanks? banks;
            try
            {
                (modules, banks) = await Task.Run(() =>
                {
                    var table = Smbios.Read();
                    return (HardwareInfo.Modules(table), HardwareInfo.Banks(table));
                });
            }
            catch (Exception ex) { Log.Error("Reading the memory modules failed", ex); Toasts.Show(ex.Message, ToastKind.Error); return null; }

            var mem = _cards["mem"];
            _memLive = new InfoSection("Live");
            var overview = new DetailTab("Overview");
            overview.Sections.Add(_memLive);
            overview.Sections.Add(new InfoSection("Installed") { Rows = Installed(modules, banks) });

            var slots = new DetailTab("Modules", 2) { Note = "From the SMBIOS table, which the firmware fills in from each module at boot." };
            foreach (var m in modules) slots.Sections.Add(new InfoSection($"{m.Slot} · {m.SizeMb / 1024.0:0.#} GB", 1) { Rows = ModuleRows(m) });

            _cpuzAction = new ActionSection("Read with CPU-Z",
                "SPD and live timings need direct access to the SMBus and the memory controller, which Windows only gives to a kernel driver. " +
                "The toolbox runs CPU-Z from the KOS tools folder in report mode (no window, about 10 seconds); CPU-Z loads its own signed driver.",
                new AsyncCommand(ReadCpuzAsync, () => !_readingCpuz));
            _spdTab = new DetailTab("SPD", 2)
            {
                Note = "Read from each module's SPD chip: JEDEC (and XMP/EXPO, if the module has them) timing tables and the raw registers.", Header = _cpuzAction,
            };
            _timingsTab = new DetailTab("Timings") { Note = "Timings of the memory controller, in the layout of ASRock Timing Configurator.", Header = _cpuzAction };
            _memDetail = new HardwareDetail("Memory", mem.Subtitle, overview, slots, _spdTab, _timingsTab);
            ShowCpuz();
        }
        return _memDetail;
    }

    static InfoRow[] Installed(IReadOnlyList<MemoryModule> modules, MemoryBanks? banks)
    {
        var rows = new List<InfoRow>
        {
            new("Total", $"{modules.Sum(m => (double)m.SizeMb) / 1024:0.#} GB"),
            new("Modules", banks is { Slots: > 0 } b ? $"{modules.Count} of {b.Slots} slots" : modules.Count.ToString()),
            new("Type", string.Join(", ", modules.Select(m => m.Type).OfType<string>().Distinct()) is { Length: > 0 } t ? t : "—"),
            new("Speed", modules.Max(m => m.SpeedMts) is { } s ? $"{s} MT/s ({s / 2.0:0.#} MHz)" : "—"),
        };
        if (banks?.MaxBytes is { } max) rows.Add(new("Max capacity", $"{max / 1073741824.0:N0} GB"));
        if (banks?.ErrorCorrection is { } ecc) rows.Add(new("Error correction", ecc));
        return rows.ToArray();
    }

    static InfoRow[] ModuleRows(MemoryModule m)
    {
        static string Volts(ushort? mv) => mv is { } v ? $"{v / 1000.0:0.00} V" : "—";
        var rows = new List<InfoRow>
        {
            new("Slot", m.Bank is null ? m.Slot : $"{m.Slot} ({m.Bank})"),
            new("Size", $"{m.SizeMb:N0} MB"),
            new("Type", string.Join(" · ", new[] { m.Type, m.Buffering, m.FormFactor }.Where(s => s is not null)) is { Length: > 0 } t ? t : "—"),
            new("Rated speed", m.RatedMts is { } r ? $"{r} MT/s" : "—"),
            new("Configured speed", m.ConfiguredMts is { } c ? $"{c} MT/s" : "—"),
            new("Ranks", m.Ranks?.ToString() ?? "—"),
            new("Manufacturer", m.Manufacturer ?? "—"),
            new("Part number", m.PartNumber ?? "—"),
            new("Serial number", m.Serial ?? "—"),
            new("Width", m.DataWidth is { } d ? $"{d}-bit" + (m.TotalWidth is { } w && w > d ? $" + ECC ({w}-bit total)" : "") : "—"),
            new("Voltage", m.ConfiguredMv is null && m.MinMv is null ? "—" : $"{Volts(m.ConfiguredMv)} (min {Volts(m.MinMv)}, max {Volts(m.MaxMv)})"),
        };
        return rows.ToArray();
    }

    /// <summary>When the toolbox opens (preference, on by default). Without administrator rights CPU-Z would prompt for UAC: then it waits for the button.</summary>
    void ReadMemoryAtStart()
    {
        if (AppSettings.Current.ReadMemoryOnStart && Elevation.IsAdmin && Cpuz.Exe() is not null) MainViewModel.Fire(ReadCpuzAsync());
    }

    /// <summary>Reads through CPU-Z (Read button, or when the toolbox opens with the preference on); the result is kept for the session.</summary>
    async Task ReadCpuzAsync()
    {
        if (_readingCpuz) return;
        _readingCpuz = true;
        _cpuzError = null;
        ShowCpuz();
        try
        {
            _cpuz = await Cpuz.ReadMemoryAsync(CancellationToken.None);
            _cpuzTime = DateTime.Now;
            Log.Info($"Home: CPU-Z report read ({_cpuz.Dimms.Count} module(s), {_cpuz.Timings.Count} timing value(s)).");
        }
        catch (Exception ex)
        {
            Log.Warn($"Home: reading the memory with CPU-Z failed: {ex.Message}");
            _cpuzError = ex is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 } ? "Cancelled (UAC)." : ex.Message;
        }
        finally
        {
            _readingCpuz = false;
            ShowCpuz();
        }
    }

    /// <summary>Carries the last CPU-Z result to the SPD and Timings tabs (if the memory details have already been read).</summary>
    void ShowCpuz()
    {
        CommandManager.InvalidateRequerySuggested();
        if (_cpuzAction is null || _spdTab is null || _timingsTab is null) return;
        _cpuzAction.Button = _cpuz is null ? "Read" : "Read again";
        _cpuzAction.IsError = _cpuzError is not null;
        if (_readingCpuz) { _cpuzAction.Status = "Reading with CPU-Z… (about 10 seconds)"; return; }
        if (_cpuzError is not null) { _cpuzAction.Status = _cpuzError; return; }
        if (_cpuz is not { } r)
        {
            _cpuzAction.IsError = Cpuz.Exe() is null;
            _cpuzAction.Status = _cpuzAction.IsError ? $@"CPU-Z (cpuz_x64.exe) isn't in {ToolDiscovery.Root}. Put it there (for example tools\CPUZ\cpuz_x64.exe) to read the SPD and timings." : "";
            return;
        }
        _spdTab.Sections.Clear();
        _timingsTab.Sections.Clear();
        foreach (var d in r.Dimms) _spdTab.Sections.Add(new InfoSection(d.Title, 1) { Rows = d.Rows, Mono = d.Registers });
        if (r.Timings.Count > 0)
        {
            _timingsTab.Sections.Add(new InfoSection("Memory") { Rows = r.Timings.Where(t => !DramTimings.IsTiming(t.Label)).ToList() });
            _timingsTab.Sections.Add(new TimingsSection(DramTimings.Build(r.Timings, r.Dimms.Select(d => d.Spd), _machine?.MemorySpeedMts)));
        }
        var empty = r.Dimms.Count == 0 && r.Timings.Count == 0;
        _cpuzAction.IsError = empty;
        _cpuzAction.Status = empty
            ? "CPU-Z didn't report the memory. Its driver may be blocked (for example by Memory integrity); open CPU-Z from the Tools page to check."
            : $"Read at {_cpuzTime:t}.";
    }

    /// <summary>Read again on every selection: the counters only change slowly, and the drive answers in milliseconds.</summary>
    async Task<HardwareDetail?> DiskDetailAsync(int number)
    {
        if (!_cards.TryGetValue($"disk{number}", out var card)) return null;
        DiskHealthReport? health;
        try { health = await Task.Run(() => DiskHealth.Read(number)); }
        catch (Exception ex) { Log.Warn($"Home: reading the health of disk {number} failed: {ex.Message}"); health = null; }
        var overview = new DetailTab("Overview");
        if (health is null)
            overview.Sections.Add(new InfoSection("Health", 1) { Rows = new[] { new InfoRow("Health", "This drive doesn't report its health to Windows (USB enclosures and RAID often don't).") } });
        else
        {
            overview.Sections.Add(new InfoSection("Health") { Rows = health.Rows });
            if (health.Attributes.Count > 0) overview.Sections.Add(new InfoSection("SMART attributes", 1) { Rows = health.Attributes });
        }
        return new HardwareDetail(card.Title, card.Subtitle, overview);
    }

    async Task<HardwareDetail?> GpuDetailAsync()
    {
        if (_gpu is not { } gpu) return null;
        if (_gpuDetail is null)
        {
            var driver = await Task.Run(() => { try { return GpuTuner.DriverVersion(); } catch { return null; } });
            var s = gpu.Sensors;
            _gpuLive = new InfoSection("Sensors") { Rows = GpuPageViewModel.Format(s) };
            var overview = new DetailTab("Overview");
            overview.Sections.Add(_gpuLive);
            overview.Sections.Add(new InfoSection("Card")
            {
                Rows = new InfoRow[]
                {
                    new("Name", gpu.Device.Name), new("PCI bus", gpu.Device.BusId), new("Driver", driver ?? "—"),
                    new("VRAM", s.VramTotal is { } vt ? $"{vt / 1048576:N0} MB" : "—"),
                    new("PCIe link (max)", s.PcieGenMax is { } g && s.PcieWidthMax is { } w ? $"{g}.0 x{w}" : "—"),
                    new("Power limit", s.PowerLimitMw is { } p ? $"{p / 1000.0:0} W" : "—"),
                },
            });
            _gpuDetail = new HardwareDetail("Graphics card", gpu.Device.Name, overview);
        }
        return _gpuDetail;
    }
}
