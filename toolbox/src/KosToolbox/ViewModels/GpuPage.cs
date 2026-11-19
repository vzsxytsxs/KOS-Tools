using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.SystemInfo;
using KosToolbox.Core.Text;
using KosToolbox.Views;

namespace KosToolbox.ViewModels;

/// <summary>
/// GPU page (NVIDIA, through the public NVML): live reads every second while the page is visible, and pending
/// adjustments (power limit, clock offsets, fan) applied only with Apply and verified by re-reading the card.
/// </summary>
public sealed class GpuTab(string key, string label, string icon, bool nvidiaOnly = false) : ObservableObject
{
    bool _isSelected, _isAvailable = !nvidiaOnly;
    /// <summary>Main and Driver show for any GPU; the others need an NVIDIA card.</summary>
    public bool NvidiaOnly { get; } = nvidiaOnly;
    public bool IsAvailable { get => _isAvailable; set => Set(ref _isAvailable, value); }
    public string Key { get; } = key;
    public string Label { get; } = label;
    public string Icon { get; } = icon;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

public sealed partial class GpuPageViewModel : PageViewModel
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    IReadOnlyList<GpuDevice> _devices = Array.Empty<GpuDevice>();
    IReadOnlyList<InfoRow> _readings = Array.Empty<InfoRow>();
    GpuDevice? _device;
    GpuState? _state;
    string _power = "", _thermal = "", _core = "", _mem = "", _fan = "";
    string? _powerError, _thermalError, _coreError, _memError, _fanError, _notice, _driver, _readError, _message;
    uint? _pPower, _pFan;
    int? _pThermal, _pCore, _pMem;
    bool? _pSlowdown;
    string _fanMode = "auto";
    bool _busy, _isError, _reading, _signIn;

    public GpuPageViewModel(IEnumerable<ICatalogItem> tweaks, TweakViewModel? windowsUpdateDrivers = null) : base("gpu", Txt.PageGpu, "", Txt.PageGpuSubtitle)
    {
        Tweaks = new CatalogPageViewModel(Core.Catalog.Pages.Gpu, Txt.PageGpu, "", "", tweaks, singleTab: true);
        Driver = new GpuDriverViewModel(windowsUpdateDrivers);
        FanModes = new[] { new OptionItem("auto", "Auto") { IsSelected = true }, new OptionItem("fixed", "Static"), new OptionItem("curve", "Curve") };
        SelectFanModeCommand = new RelayCommand(p => SelectFanMode((string)p!), _ => FanEnabled);
        ApplyCommand = new AsyncCommand(ApplyAsync, () => !_busy && PendingCount > 0 && !HasErrors);
        DiscardCommand = new RelayCommand(Discard, () => !_busy && (PendingCount > 0 || HasErrors));
        ResetCommand = new RelayCommand(Reset, () => !_busy && _state is not null);
        _timer.Tick += (_, _) => MainViewModel.Fire(ReadSensorsAsync());
        OpenInspectorCommand = new RelayCommand(() => OpenInspector(ProfileInspector.Exe, null), () => ProfileInspector.Available);
        OpenHiddenCommand = new RelayCommand(() => OpenInspector(ProfileInspector.UnlockedExe, null), () => ProfileInspector.UnlockedAvailable);
        ImportNipCommand = new RelayCommand(ImportNip, () => ProfileInspector.Available);
        InitCurve();
        InitVf();
        InitProfiles();
        SelectTabCommand = new RelayCommand(p => SelectTab((string)p!));
        Tabs[0].IsSelected = true;
    }

    /// <summary>Graphics card (outline): I/O bracket, body with fan and the PCIe connector.</summary>
    public override string? IconPath =>
        "M2,4 L2,20 M2,6 L20,6 Q21,6 21,7 L21,16 Q21,17 20,17 L2,17 M6,17 L6,19.5 L16,19.5 L16,17 " +
        "M16.5,11.5 A3.5,3.5 0 1 1 9.5,11.5 A3.5,3.5 0 1 1 16.5,11.5 M14,11.5 A1,1 0 1 1 12,11.5 A1,1 0 1 1 14,11.5";

    // ---------- tabs (Main, Overclock, Settings, Display, DRS) ----------
    string _tab = "main";
    public IReadOnlyList<GpuTab> Tabs { get; } = new[]
    {
        new GpuTab("main", "Main", ""), new GpuTab("overclock", "Overclock", "", nvidiaOnly: true), new GpuTab("settings", "Settings", "", nvidiaOnly: true),
        new GpuTab("display", "Display", "", nvidiaOnly: true), new GpuTab("drs", "DRS", "", nvidiaOnly: true),
        new GpuTab("driver", Txt.GpuTabDriver, ""),
    };
    public ICommand SelectTabCommand { get; private set; } = null!;
    /// <summary>Functions of the KOS NVIDIA tool (nvidia_tool.cmd), as catalog tweaks; they live in the Main tab.</summary>
    public CatalogPageViewModel Tweaks { get; }

    /// <summary>
    /// There is an NVIDIA card (with or without a driver). Without one (AMD, Intel) the page shows only the notice (and, with AMD, the AMD Radeon tweaks);
    /// the monitoring and tuning are NVIDIA only.
    /// </summary>
    public bool IsNvidia
    {
        get => _isNvidia;
        private set
        {
            if (!Set(ref _isNvidia, value)) return;
            Raise(nameof(ShowMainTweaks));
            foreach (var t in Tabs) t.IsAvailable = !t.NvidiaOnly || value;
        }
    }
    /// <summary>An AMD adapter: the catalog then has the AMD Radeon section, shown on Main even without an NVIDIA card.</summary>
    public bool HasAmd { get; } = Core.Gpu.AmdGpus.ClassKeys().Count > 0;
    public bool ShowMainTweaks => IsMainTab && (IsNvidia || HasAmd);
    bool _isNvidia;
    public GpuSettingsViewModel Settings { get; } = new();
    /// <summary>The former Display page (EDID, resolutions, VRR, HDMI audio), now a tab here.</summary>
    public DisplayPageViewModel Display { get; } = new();
    public bool IsMainTab => _tab == "main";
    public bool IsOverclockTab => _tab == "overclock";
    public bool IsSettingsTab => _tab == "settings";
    public bool IsDisplayTab => _tab == "display";
    public bool IsDrsTab => _tab == "drs";
    public bool IsDriverTab => _tab == "driver";
    /// <summary>Driver tab: installed driver, NVIDIA driver install, Display Driver Uninstaller (any GPU).</summary>
    public GpuDriverViewModel Driver { get; }
    /// <summary>Driver settings (DRS profiles), like NVIDIA Profile Inspector.</summary>
    public DriverSettingsViewModel Drs { get; } = new();
    /// <summary>Overclock profiles/apply bar; Settings, Display and DRS have their own bars.</summary>
    public bool ShowOcToolbar => IsOverclockTab;
    /// <summary>The DRS tab has its own (virtualized) list: it receives the window height.</summary>
    public override bool ScrollsItself => IsDrsTab;

    /// <summary>Also used by Drivers › "Open GPU › Driver".</summary>
    internal void SelectTab(string key)
    {
        if (Tabs.All(t => t.Key != key)) return;
        _tab = key;
        foreach (var t in Tabs) t.IsSelected = t.Key == key;
        Raise(nameof(IsMainTab));
        Raise(nameof(ShowMainTweaks));
        Raise(nameof(IsOverclockTab));
        Raise(nameof(IsSettingsTab));
        Raise(nameof(IsDisplayTab));
        Raise(nameof(IsDrsTab));
        Raise(nameof(IsDriverTab));
        Raise(nameof(ShowOcToolbar));
        Raise(nameof(ScrollsItself));
        if (key == "main") MainViewModel.Fire(Tweaks.RefreshAsync());
        if (key == "drs") MainViewModel.Fire(Drs.LoadAsync());
        if (key == "settings") MainViewModel.Fire(Settings.LoadAsync());
        if (key == "display") MainViewModel.Fire(Display.RefreshAsync());
        if (key == "driver") MainViewModel.Fire(Driver.RefreshAsync());
    }

    public IReadOnlyList<OptionItem> FanModes { get; }
    public ICommand SelectFanModeCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand DiscardCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand OpenInspectorCommand { get; }
    public ICommand OpenHiddenCommand { get; }
    public ICommand ImportNipCommand { get; }
    public string? InspectorMissing => ProfileInspector.Available ? null : Txt.ProfileInspectorMissing(ProfileInspector.Exe);

    // ---------- driver settings (Profile Inspector) ----------
    void ImportNip()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = Txt.NipFilter, Title = Txt.ImportNipTitle };
        if (dialog.ShowDialog() != true) return;
        OpenInspector(ProfileInspector.Exe, dialog.FileName);
    }

    static void OpenInspector(string exe, string? nip)
    {
        // When elevated, the toolbox would open the program as administrator: a file that a standard user may have swapped is not opened.
        if (ProfileInspector.OwnerProblem(exe) is { } problem) { Toasts.Show(Txt.NotOpened(problem), ToastKind.Warning); return; }
        try { ProfileInspector.Open(exe, nip); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { /* UAC cancelled */ }
        catch (Exception ex)
        {
            Log.Error($"Opening {exe} failed", ex);
            Toasts.Show(ex.Message, ToastKind.Error);
        }
    }

    public string? Notice { get => _notice; private set => Set(ref _notice, value); }
    public bool HasDevice => _device is not null;
    public bool HasManyDevices => _devices.Count > 1;
    public IReadOnlyList<GpuDevice> Devices { get => _devices; private set { Set(ref _devices, value); Raise(nameof(HasManyDevices)); } }
    public string DeviceDetail => _device is null ? "" : string.Join(" · ", new[] { Txt.GpuDriver(_driver), $"PCI {_device.BusId}" }.Where(s => s.Length > 0));
    public IReadOnlyList<InfoRow> Readings { get => _readings; private set => Set(ref _readings, value); }
    public string? ReadError { get => _readError; private set => Set(ref _readError, value); }
    public string? Message { get => _message; private set => Set(ref _message, value); }
    public bool IsError { get => _isError; private set => Set(ref _isError, value); }

    public GpuDevice? SelectedDevice
    {
        get => _device;
        set
        {
            if (value is null || value == _device) return;
            if (_busy || PendingCount > 0)
            {
                // The list goes back to the current card: the pending edits belong to that card.
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() => Raise(nameof(SelectedDevice)));
                if (PendingCount > 0) Report(Txt.GpuPendingFirst, true);
                return;
            }
            Select(value);
            Readings = Array.Empty<InfoRow>();
            MainViewModel.Fire(LoadStateAsync());
        }
    }

    void Select(GpuDevice? device)
    {
        _device = device;
        Raise(nameof(SelectedDevice));
        Raise(nameof(HasDevice));
        Raise(nameof(DeviceDetail));
    }

    public override void OnShown(bool shown)
    {
        if (shown) _timer.Start(); else _timer.Stop();
    }

    // ---------- reading ----------
    public override async Task RefreshAsync()
    {
        if (_busy) return;
        var (devices, problem, driver, others, nvidia) = await Task.Run(() =>
        {
            List<GpuDevice> list;
            string? error = null;
            try { list = GpuTuner.List(); }
            catch (Exception ex) { list = new(); error = ex.Message; Log.Warn($"GPU: {ex.Message}"); }
            List<string> other;
            var anyNvidia = list.Count > 0;
            try { other = GpuTuner.OtherAdapters(); anyNvidia |= GpuTuner.NvidiaAdapters().Count > 0; }
            catch (Exception ex) { other = new(); Log.Warn($"GPU: listing display adapters failed: {ex.Message}"); }
            return (list, error, GpuTuner.DriverVersion(), other, anyNvidia);
        });
        _driver = driver;
        IsNvidia = nvidia;
        Devices = devices;
        Notice = string.Join("\n", new[] { problem ?? (devices.Count == 0 ? Txt.GpuNoNvidia : null), others.Count > 0 ? Txt.GpuOtherAdapters(others) : null }.Where(s => s is not null));
        if (Notice.Length == 0) Notice = null;
        var keep = _device?.BusId;
        Select(devices.FirstOrDefault(d => d.BusId == keep) ?? devices.FirstOrDefault());
        // Pending edits are not discarded when returning to the page (as on the Power page).
        if (PendingCount == 0) await LoadStateAsync();
        // When the toolbox opens, the last chosen profile comes back loaded (pending; the card only changes with Apply).
        if (!_profileLoadedOnOpen && _state is not null) { _profileLoadedOnOpen = true; LoadProfile(_selectedProfile); }
        _signIn =await Task.Run(GpuTuner.SignInEnabled);
        Raise(nameof(SignIn));
        await ReadSensorsAsync();
        if (IsSettingsTab) await Settings.LoadAsync(force: true);
        if (IsDisplayTab) await Display.RefreshAsync();
        if (IsMainTab) await Tweaks.RefreshAsync();
        if (IsDriverTab) await Driver.RefreshAsync();
    }

    async Task LoadStateAsync()
    {
        if (_device is not { } device) { _state = null; LoadStaged(); return; }
        try
        {
            var state = await Task.Run(() => GpuTuner.ReadState(device.BusId));
            if (_device != device) return;
            _state = state;
        }
        catch (Exception ex)
        {
            Log.Error("Reading the GPU settings failed", ex);
            _state = null;
            Report(Txt.GpuReadFailed(ex.Message), true);
        }
        LoadStaged();
    }

    readonly GpuResetWatch _resetWatch = new();

    /// <summary>
    /// The driver restarted or recovered from a hang: everything went back to default. Re-reads the card; the overclock saved for
    /// logon is reapplied by KOS Engine, or here when it is not running (requires administrator).
    /// </summary>
    async Task AfterDriverResetAsync(GpuDevice device)
    {
        Log.Warn("GPU: the display driver restarted or recovered; reloading the card's settings.");
        var reapplied = false;
        if (!GovernorFanCurve.EngineRunning && Elevation.IsAdmin && File.Exists(GpuTuner.SignInFile))
            reapplied = await Task.Run(GpuTuner.RunAtSignIn) == ElevatedJob.ExitOk;
        await LoadStateAsync();
        // The open tab read the monitor while the driver was down (empty boxes, NVAPI errors): read it again.
        if (IsSettingsTab) await Settings.LoadAsync(force: true);
        if (IsDisplayTab) await Display.RefreshAsync();
        Report(reapplied ? Txt.GpuDriverResetReapplied
            : GovernorFanCurve.EngineRunning && File.Exists(GpuTuner.SignInFile) ? Txt.GpuDriverResetGovernor
            : Txt.GpuDriverReset, false);
    }

    async Task ReadSensorsAsync()
    {
        if (_reading || _device is not { } device) return;
        _reading = true;
        try
        {
            var (r, reset) = await Task.Run(() => (GpuTuner.ReadSensors(device.BusId), _resetWatch.Poll()));
            if (_device == device) { Readings = Format(r); RefreshCurveState(r); UpdateVfLive(r); }
            if (reset && _device == device) await AfterDriverResetAsync(device);
            ReadError = null;
        }
        catch (Exception ex)
        {
            // Once per message: the read repeats every second and must not flood the log.
            if (ReadError != Txt.GpuReadFailed(ex.Message)) Log.Warn($"GPU: reading sensors failed: {ex.Message}");
            ReadError = Txt.GpuReadFailed(ex.Message);
        }
        finally { _reading = false; }
    }

    internal static IReadOnlyList<InfoRow> Format(GpuSensors r)
    {
        var list = new List<InfoRow>();
        void Add(string label, string? value) => list.Add(new InfoRow(label, value ?? "—"));
        static string? Mhz(uint? v) => v is { } x ? $"{x} MHz" : null;
        Add(Txt.GpuCoreClock, Mhz(r.GraphicsMhz));
        Add(Txt.GpuMemoryClock, Mhz(r.MemoryMhz));
        Add(Txt.GpuSmClock, Mhz(r.SmMhz));
        Add(Txt.GpuTemperature, r.TemperatureC is { } t ? $"{t} °C" : null);
        Add(Txt.GpuHotspot, r.HotspotC is { } h ? $"{h:0} °C" : null);
        if (r.MemoryJunctionC is { } mj) Add(Txt.GpuMemoryJunction, $"{mj:0} °C");
        Add(Txt.GpuPowerDraw, r.PowerMw is { } p ? $"{p / 1000.0:0.0} W" : null);
        Add(Txt.GpuFanSpeed, r.Fans.FirstOrDefault() is { } fan ? $"{fan} %" : null);
        Add(Txt.GpuVoltage, r.VoltageUv is { } uv ? $"{uv / 1000.0:0} mV" : null);
        Add(Txt.GpuLoad, r.GpuLoad is { } g ? $"{g} %" : null);
        Add(Txt.GpuVram, r.VramUsed is { } u ? $"{u / 1048576:N0} MB" : null);
        Add(Txt.GpuPcie, r.PcieGen is { } gen && r.PcieWidth is { } width ? $"{gen}.0 x{width}" : null);
        Add(Txt.GpuBandwidth, r.PcieTxKb is { } tx && r.PcieRxKb is { } rx ? $"↑{tx / 1024.0:0.0} ↓{rx / 1024.0:0.0} MB/s" : null);
        Add(Txt.GpuThermalSlowdown, r.ThrottleReasons is { } reasons ? ((reasons & 0x60) != 0 ? "Active" : "Inactive") : null);
        Add(Txt.GpuPState, r.PState is { } ps and < 16 ? $"P{ps}" : null);
        return list;
    }

    // ---------- pending values ----------
    public bool PowerEnabled => _state is { PowerProblem: null };
    public string? PowerProblem => _state?.PowerProblem;
    public string PowerHint => _state is { PowerMinMw: { } min, PowerMaxMw: { } max, PowerDefaultMw: { } def } ? Txt.GpuWattsRange(min, max, def) : "";
    public string? PowerError { get => _powerError; private set => Set(ref _powerError, value); }
    public string PowerText
    {
        get => _power;
        set
        {
            _power = value ?? "";
            _pPower = null;
            PowerError = null;
            if (_state is { PowerProblem: null, PowerMinMw: { } min, PowerMaxMw: { } max })
            {
                if (GpuTuner.ParseWatts(_power) is not { } mw) PowerError = Txt.GpuEnterWatts;
                else if (mw < min || mw > max) PowerError = Txt.GpuPowerOutOfRange(min, max);
                else _pPower = mw;
            }
            Changed();
            Raise(nameof(PowerText)); // the slider also writes here
            Raise(nameof(PowerSlider));
        }
    }
    public double PowerMinimum => (_state?.PowerMinMw ?? 0) / 1000.0;
    public double PowerMaximum => (_state?.PowerMaxMw ?? 1000) / 1000.0;
    public double PowerSlider
    {
        get => (_pPower ?? _state?.PowerLimitMw ?? 0) / 1000.0;
        set
        {
            var watts = value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            if (_power != watts) PowerText = watts;
        }
    }

    public bool ThermalEnabled => _state?.Thermal is not null;
    public string? ThermalProblem => _state?.ThermalProblem;
    public string ThermalHint => _state?.Thermal is { } t ? Txt.GpuThermalRange(t.Minimum, t.Maximum, t.Default) : "";
    public string? ThermalError { get => _thermalError; private set => Set(ref _thermalError, value); }
    public string ThermalText
    {
        get => _thermal;
        set
        {
            _thermal = value ?? "";
            _pThermal = null;
            ThermalError = null;
            if (_state?.Thermal is { } t)
            {
                if (!int.TryParse(_thermal.Trim(), out var c)) ThermalError = Txt.GpuEnterTemperature;
                else if (c < t.Minimum || c > t.Maximum) ThermalError = Txt.GpuThermalOutOfRange(t.Minimum, t.Maximum);
                else _pThermal = c;
            }
            Changed();
            Raise(nameof(ThermalText));
            Raise(nameof(ThermalSlider));
        }
    }
    public double ThermalMinimum => _state?.Thermal?.Minimum ?? 0;
    public double ThermalMaximum => _state?.Thermal?.Maximum ?? 100;
    public double ThermalSlider
    {
        get => _pThermal ?? _state?.Thermal?.Current ?? ThermalMinimum;
        set { var v = ((int)Math.Round(value)).ToString(); if (_thermal != v) ThermalText = v; }
    }

    public bool SlowdownEnabled => _state?.ThermalSlowdown is not null;
    public string? SlowdownProblem => _state?.ThermalSlowdownProblem;
    public bool ThermalSlowdownOn
    {
        get => _pSlowdown ?? true;
        set
        {
            if (!SlowdownEnabled || _pSlowdown == value) return;
            _pSlowdown = value;
            Changed();
            Raise(nameof(ThermalSlowdownOn));
        }
    }

    public bool CoreEnabled => _state?.Core is not null;
    public string? CoreProblem => _state is null ? null : _state.CoreProblem;
    public string CoreHint => _state?.Core is { } r ? Txt.GpuMhzRange(r.Min, r.Max) : "";
    public string? CoreError { get => _coreError; private set => Set(ref _coreError, value); }
    public string CoreText { get => _core; set { _core = value ?? ""; (_pCore, CoreError) = Offset(_core, _state?.Core); Changed(); Raise(nameof(CoreText)); Raise(nameof(CoreSlider)); CoreShiftChanged(); } }
    public double CoreMinimum => _state?.Core?.Min ?? 0;
    public double CoreMaximum => _state?.Core?.Max ?? 1;
    public double CoreSlider
    {
        get => _pCore ?? _state?.Core?.Current ?? CoreMinimum;
        set { var v = ((int)Math.Round(value)).ToString(); if (_core != v) CoreText = v; }
    }

    public bool MemEnabled => _state?.Memory is not null;
    public string? MemProblem => _state is null ? null : _state.MemoryProblem;
    public string MemHint => _state?.Memory is { } r ? Txt.GpuMhzRange(r.Min, r.Max) : "";
    public string? MemError { get => _memError; private set => Set(ref _memError, value); }
    public string MemText { get => _mem; set { _mem = value ?? ""; (_pMem, MemError) = Offset(_mem, _state?.Memory); Changed(); Raise(nameof(MemText)); Raise(nameof(MemSlider)); } }
    public double MemMinimum => _state?.Memory?.Min ?? 0;
    public double MemMaximum => _state?.Memory?.Max ?? 1;
    public double MemSlider
    {
        get => _pMem ?? _state?.Memory?.Current ?? MemMinimum;
        set { var v = ((int)Math.Round(value)).ToString(); if (_mem != v) MemText = v; }
    }

    static (int?, string?) Offset(string text, GpuRange? range)
    {
        if (range is null) return (null, null);
        if (GpuTuner.ParseMhz(text) is not { } v) return (null, Txt.GpuEnterMhz);
        return v < range.Min || v > range.Max ? (null, Txt.GpuOffsetOutOfRange(range.Min, range.Max)) : (v, null);
    }

    public bool FanEnabled => _state is { FanProblem: null };
    public string? FanProblem => _state?.FanProblem;
    public string FanHint => _state is { FanProblem: null } s ? Txt.GpuPercentRange(s.FanMin, s.FanMax) : "";
    public bool FanPercentEnabled => FanEnabled && _fanMode == "fixed";
    public bool FanCurveSelected => _fanMode == "curve";
    public bool ShowFanCurve => FanCurveSelected || CurveRunning;
    public string? FanError { get => _fanError; private set => Set(ref _fanError, value); }
    void SelectFanMode(string mode)
    {
        if (mode is not ("auto" or "fixed" or "curve")) return;
        if (mode != "curve") TurnCurveOff();
        SetFanMode(mode);
        if (mode == "curve") StartCurve();
    }
    void SetFanMode(string mode)
    {
        if (mode is not ("auto" or "fixed" or "curve")) return;
        _fanMode = mode;
        foreach (var m in FanModes) m.IsSelected = m.Key == mode;
        Raise(nameof(FanPercentEnabled));
        Raise(nameof(FanCurveSelected));
        Raise(nameof(ShowFanCurve));
        FanText = _fan;
    }
    public string FanText
    {
        get => _fan;
        set
        {
            _fan = value ?? "";
            _pFan = null;
            FanError = null;
            if (_fanMode == "fixed" && _state is { FanProblem: null } s)
            {
                if (GpuTuner.ParsePercent(_fan) is not { } pct) FanError = Txt.GpuEnterPercent;
                else if (pct < s.FanMin || pct > s.FanMax) FanError = Txt.GpuFanOutOfRange(s.FanMin, s.FanMax);
                else _pFan = pct;
            }
            Changed();
            Raise(nameof(FanText)); // the slider also writes here
            Raise(nameof(FanSlider));
        }
    }

    // The slider and the % field are the same pending value: both go through FanText validation.
    public double FanMinimum => _state is { FanProblem: null } s ? s.FanMin : 0;
    public double FanMaximum => _state is { FanProblem: null } s ? s.FanMax : 100;
    public double FanSlider
    {
        get => GpuTuner.ParsePercent(_fan) is { } p ? p : FanMinimum;
        set
        {
            var v = (uint)Math.Round(value);
            if (GpuTuner.ParsePercent(_fan) != v) FanText = v.ToString();
        }
    }

    bool HasErrors => _powerError is not null || _thermalError is not null || _coreError is not null || _memError is not null || _fanError is not null || _vfError is not null;
    public int PendingCount => _state is null ? 0 : Count(Build(full: false));
    public bool HasPending => PendingCount > 0;
    public string PendingText => Txt.GpuPending(PendingCount);
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); } }

    static int Count(GpuTuning t) => (t.PowerLimitMw is null ? 0 : 1) + (t.TemperatureLimitC is null ? 0 : 1) + (t.ThermalSlowdown is null ? 0 : 1)
        + (t.CoreOffsetMhz is null ? 0 : 1) + (t.MemOffsetMhz is null ? 0 : 1) + (t.CurveChanges?.Count ?? 0)
        + (t.LockVoltageUv is null ? 0 : 1) + (t.ClockLockMhz is null ? 0 : 1)
        + (t.FanAuto || t.FanPercent is not null ? 1 : 0);

    void Changed()
    {
        Raise(nameof(PendingCount));
        Raise(nameof(HasPending));
        Raise(nameof(PendingText));
        CommandManager.InvalidateRequerySuggested();
        TrackUndo();
    }

    /// <summary>Pending values; with <paramref name="full"/>, all controllable ones (what is saved for logon).</summary>
    GpuTuning Build(bool full)
    {
        var s = _state!;
        var t = new GpuTuning { PciBusId = s.BusId };
        if (_pPower is { } p && (full || p != s.PowerLimitMw)) t.PowerLimitMw = p;
        if (_pThermal is { } temp && (full || temp != s.Thermal?.Current)) t.TemperatureLimitC = temp;
        // Saved for sign-in only when off: on is what the driver starts with.
        if (_pSlowdown is { } on && (on != s.ThermalSlowdown || full && !on)) t.ThermalSlowdown = on;
        if (_pCore is { } c && (full || c != s.Core?.Current)) t.CoreOffsetMhz = c;
        if (_pMem is { } m && (full || m != s.Memory?.Current)) t.MemOffsetMhz = m;
        t.CurveChanges = CurveChanges(full);
        t.LockVoltageUv = CurveLock(full);
        t.ClockLockMhz = ClockLock(full);
        if (s.FanProblem is null)
        {
            if (_fanMode == "auto") t.FanAuto = full || s.FanAuto == false;
            else if (_fanMode == "fixed" && _pFan is { } f && (full || s.FanAuto != false || f != s.FanTarget)) t.FanPercent = f;
        }
        return t;
    }

    /// <summary>Shows what the card has now (discards what was pending).</summary>
    void LoadStaged()
    {
        var s = _state;
        _restoring = true;
        _power = s?.PowerLimitMw is { } p ? Txt.W(p) : "";
        _thermal = s?.Thermal?.Current.ToString() ?? "";
        _core = s?.Core?.Current.ToString() ?? "";
        _mem = s?.Memory?.Current.ToString() ?? "";
        _fan = s?.FanTarget?.ToString() ?? "";
        _pPower = s?.PowerProblem is null ? s?.PowerLimitMw : null;
        _pThermal = s?.Thermal?.Current;
        _pSlowdown = s?.ThermalSlowdown;
        _pCore = s?.Core?.Current;
        _pMem = s?.Memory?.Current;
        _powerError = _thermalError = _coreError = _memError = _fanError = null;
        LoadVf();
        SetFanMode(CurveEnabledHere ? "curve" : s?.FanAuto == false ? "fixed" : "auto");
        _restoring = false;
        AfterReload();
        Raise(""); // all properties of the page
        CommandManager.InvalidateRequerySuggested();
    }

    void Discard()
    {
        LoadStaged();
        Message = null;
    }

    void Reset()
    {
        if (_state is not { } s) return;
        if (s.PowerDefaultMw is { } def) PowerText = Txt.W(def);
        if (s.Thermal is { } thermal) ThermalText = thermal.Default.ToString();
        ThermalSlowdownOn = true;
        if (s.Core is not null) CoreText = "0";
        if (s.Memory is not null) MemText = "0";
        ResetVf();
        if (s.FanProblem is null) SetFanMode("auto");
        Message = null;
    }

    // ---------- writing ----------
    async Task<bool> ApplyAsync()
    {
        if (_state is null || _device is null) return false;
        var changes = Build(full: false);
        if (Count(changes) == 0) { Report(Txt.GpuNoChanges, false); return false; }
        StopCurveFor(changes);
        // Writes only what changed; with "Apply at sign-in" on, the saved file gets the complete set.
        var ok = await RunAsync(_signIn ? new GpuJob { Tuning = changes, Saved = Build(full: true), SignIn = true } : new GpuJob { Tuning = changes }, Txt.GpuApplied(Count(changes)));
        if (ok && changes.ThermalSlowdown == false) Toasts.Show(Txt.GpuThermalSlowdownWarning, ToastKind.Warning);
        return ok;
    }

    /// <summary>
    /// When the toolbox opens: applies the last chosen profile, without asking and without going through the page. Only writes what differs
    /// from the card (if already equal, nothing happens, not even UAC); the result goes to a notification.
    /// </summary>
    public async Task ApplyProfileOnStartAsync()
    {
        if (_selectedProfile is not { } name) return;
        // The previous apply did not finish (Windows or the toolbox closed midway): the profile may have crashed the
        // card. It stays off until an Apply through the page completes.
        if (_profiles.StartApplyUnfinished) { Toasts.Show(Txt.GpuStartSkipped(name), ToastKind.Warning); return; }
        await RefreshAsync(); // reads the card and marks the profile as edits (LoadProfile)
        if (_state is null || _busy || PendingCount == 0) return;
        if (HasErrors) { Toasts.Show(Txt.GpuStartInvalid(name), ToastKind.Warning); return; }
        // Off removes a limit and the profile file is the user's to write: opening leaves it on, only Apply on the page turns it off.
        if (_pSlowdown == false && _state.ThermalSlowdown == true)
        {
            ThermalSlowdownOn = true;
            Toasts.Show(Txt.GpuStartSlowdownKept(name), ToastKind.Warning);
            if (PendingCount == 0) return;
        }
        MarkStartApply(true);
        bool ok;
        try { ok = await ApplyAsync(); }
        finally { MarkStartApply(false); } // only a Windows or toolbox crash leaves the mark behind
        Toasts.Show(ok ? Txt.GpuStartApplied(name) : Txt.GpuStartFailed(name, Message), ok ? ToastKind.Success : IsError ? ToastKind.Error : ToastKind.Warning);
    }

    void MarkStartApply(bool running)
    {
        if (_profiles.StartApplyUnfinished == running) return;
        _profiles.StartApplyUnfinished = running;
        SaveProfiles();
    }

    public bool SignIn
    {
        get => _signIn;
        set
        {
            if (value == _signIn) return;
            // The checkbox goes back to the real state; it only changes after confirming and re-reading the task.
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() => Raise(nameof(SignIn)));
            if (!_busy) MainViewModel.Fire(SetSignInAsync(value));
        }
    }

    async Task SetSignInAsync(bool on)
    {
        if (_state is null) return;
        if (on)
        {
            if (PendingCount > 0 || HasErrors) { Report(Txt.GpuPendingFirst, true); return; }
            if (!Elevation.InProtectedFolder) { Report(Txt.GpuSignInNeedsProgramFiles, true); return; }
            await RunAsync(new GpuJob { Saved = Build(full: true), SignIn = true }, Txt.GpuSignInOn);
        }
        else await RunAsync(new GpuJob { SignIn = false }, Txt.GpuSignInOff);
    }

    /// <summary>Runs (elevated if needed), re-reads the card and the task and reports only what was confirmed. True = everything confirmed.</summary>
    async Task<bool> RunAsync(GpuJob job, string success)
    {
        var busId = _device!.BusId;
        IsBusy = true;
        Report(Txt.Applying, false);
        string? failure = null;
        var cancelled = false;
        var ok = false;
        try { await GpuTuner.RunAsync(job); }
        catch (ElevationCancelledException ex) { failure = ex.Message; cancelled = true; }
        catch (Exception ex)
        {
            Log.Error("GPU change failed", ex);
            failure = ex.Message;
        }
        try
        {
            var state = await Task.Run(() => GpuTuner.ReadState(busId));
            _signIn = await Task.Run(GpuTuner.SignInEnabled);
            Raise(nameof(SignIn));
            if (_device?.BusId == busId) _state = state;
            LoadStaged();
            var unconfirmed = job.Tuning is { } t ? GpuTuner.Unconfirmed(t, state) : new List<string>();
            if (failure is not null) Report(failure, !cancelled);
            else if (unconfirmed.Count > 0) Report(Txt.GpuNotConfirmed(unconfirmed.Distinct()), true);
            else if (job.SignIn is { } on && on != _signIn) Report(on ? Txt.GpuSignInNotConfirmed : Txt.GpuSignInStillOn, true);
            else { Report(success, false); ok = true; }
        }
        catch (Exception ex)
        {
            Log.Error("Reading the GPU after the change failed", ex);
            Report(failure ?? Txt.GpuReadFailed(ex.Message), true);
        }
        finally
        {
            IsBusy = false;
            MarkStartApply(false); // finished (successfully or not): apply-on-open is valid again
        }
        return ok;
    }

    void Report(string? message, bool error) { Message = message; IsError = error; }
}
