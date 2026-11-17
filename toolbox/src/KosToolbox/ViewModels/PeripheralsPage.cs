using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using KosToolbox.Core.Peripherals;
using KosToolbox.Core.Peripherals.Hid;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.SystemInfo;
using KosToolbox.Core.Text;

namespace KosToolbox.ViewModels;

/// <summary>A collection of the selected device, as the HID collections table shows it (usage page:usage, and its name when known).</summary>
public sealed record HidCollectionRow(string Location, string Usage, string? UsageName, string Input, string Output, string Feature);

/// <summary>
/// A device of the list; selected, its settings and Info show beside the list. A device with a driver is read on the page's refresh
/// (getters only) and changed only by a click: the value is validated, written once, read back, and the toast says what the device has.
/// </summary>
public sealed class PeripheralRowViewModel : ObservableObject
{
    PeripheralState? _state;
    string? _error, _dpiText, _sleepText, _lowText, _brightText, _colorText;
    bool _selected, _busy;

    public PeripheralRowViewModel(HidDevice device, PeripheralDriver? driver)
    {
        Device = device;
        Driver = driver;
        Name = driver?.Model ?? device.Product ?? Txt.PeripheralUnnamed(device.VendorId, device.ProductId);
        CopyReportCommand = new RelayCommand(CopyReport);
        Info = new InfoSection(Txt.PeripheralInfo, columns: 1) { Rows = InfoRows() };
        static string Length(int n) => n == 0 ? "—" : n.ToString();
        Collections = device.Collections.Select(c => new HidCollectionRow(Txt.HidLocation(c.Interface, c.Collection), $"{c.UsagePage:X4}:{c.Usage:X4}",
            Txt.HidUsageName(c.UsagePage, c.Usage), Length(c.InputLength), Length(c.OutputLength), Length(c.FeatureLength))).ToList();
        ApplyDpiCommand = new AsyncCommand(() => Int(_dpiText) is { } v ? Write(Txt.PeripheralDpi, d => d.SetDpi(v, v)) : Invalid());
        StageCommand = new AsyncCommand(p => p is string k && int.TryParse(k, out var n) ? Write(Txt.PeripheralDpiStages, d => d.SetActiveStage(n)) : Task.CompletedTask);
        PollingCommand = new AsyncCommand(p => p is string k && int.TryParse(k, out var hz) && hz != _state?.Polling?.Hz
            ? Write(Txt.PeripheralPolling, d => d.SetPollingRate(hz), Txt.PeripheralPollingApplied(hz)) : Task.CompletedTask);
        ApplySleepCommand = new AsyncCommand(() => Int(_sleepText) is { } m ? Write(Txt.PeripheralSleep, d => d.SetSleep(m * 60)) : Invalid());
        ApplyLowCommand = new AsyncCommand(() => Int(_lowText) is { } v ? Write(Txt.PeripheralLowBattery, d => d.SetLowBattery(v)) : Invalid());
        ApplyBrightnessCommand = new AsyncCommand(() => Int(_brightText) is { } v ? Write(Txt.PeripheralBrightness, d => d.SetBrightness(v)) : Invalid());
        HostModeCommand = new AsyncCommand(() => Write(Txt.PeripheralUseHostMode, d => d.SetHostMode()));
        GamemodeCommand = new RelayCommand(() => SaveGamemode(keep: true));
        GamemodeClearCommand = new RelayCommand(() => SaveGamemode(keep: false));
        EffectCommand = new AsyncCommand(p => p is string k && Enum.TryParse<LightingEffect>(k, out var e) && e != _state?.Lighting?.Effect
            ? Write(Txt.PeripheralEffect, d => d.SetLighting(e, Hex(_colorText) ?? _state?.Lighting?.Color ?? 0x00FF00)) : Task.CompletedTask);
        // A color on an effect without one (off, spectrum) switches to static
        ApplyColorCommand = new AsyncCommand(() => Hex(_colorText) is { } c
            ? Write(Txt.PeripheralColor, d => d.SetLighting(_state?.Lighting?.Effect == LightingEffect.Breathing ? LightingEffect.Breathing : LightingEffect.Static, c))
            : Invalid(Txt.PeripheralColorInvalid));
        GmPollingCommand = new RelayCommand(p => { if (p is string k) { _gmHz = int.TryParse(k, out var hz) ? hz : null; Raise(nameof(GmRates)); } });
        SwatchCommand = new RelayCommand(p => { if (p is string hex) ColorText = hex; });
        DockBatteryOnCommand = new AsyncCommand(() => SaveDockBattery(true));
        DockBatteryOffCommand = new AsyncCommand(() => SaveDockBattery(false));
        _gamemode = Gamemode();
        _dockBattery = IsDock && Config()?.DockBattery == true;
    }

    // ---------- the dock showing the mouse battery (KOS Governor, live color only) ----------
    bool _dockBattery;
    public bool IsDock => Driver is KosToolbox.Core.Peripherals.Razer.RazerDriver { IsDock: true };
    public bool DockBattery { get => _dockBattery; private set { Set(ref _dockBattery, value); Raise(nameof(DockBatteryText)); } }
    public string DockBatteryText => _dockBattery ? Txt.PeripheralDockBatteryOn : Txt.PeripheralDockBatteryOff;
    public ICommand DockBatteryOnCommand { get; }
    public ICommand DockBatteryOffCommand { get; }

    static GamemodePeripheral? Config()
    {
        try { return GamemodePeripheral.Load(); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    async Task SaveDockBattery(bool on)
    {
        try
        {
            if (!KosToolbox.Core.Execution.Elevation.IsAdmin) throw new InvalidOperationException(Txt.GpuGamemodeNeedsAdmin);
            var config = GamemodePeripheral.Load() ?? new GamemodePeripheral();
            config.DockBattery = on;
            config.Save();
            DockBattery = on;
            Toasts.Show(on ? Txt.PeripheralDockBatterySaved : Txt.PeripheralDockBatteryRemoved);
            if (on && _state?.Brightness == 0) Toasts.Show(Txt.PeripheralDockBatteryDark, ToastKind.Warning);
            Log.Info($"Peripherals: {Name}: mouse battery on the ring {(on ? "on" : "off")}.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Toasts.Show(Txt.PeripheralWriteFailed(ex.Message), ToastKind.Error);
            return;
        }
        // Off: the engine stops coloring; the ring gets back the effect stored in the dock (written stored and live, read back)
        if (!on && _state?.Lighting is { Effect: not LightingEffect.Other } l) await Write(Txt.PeripheralLighting, d => d.SetLighting(l.Effect, l.Color ?? 0));
    }

    // ---------- gamemode profile: what KOS Governor writes live while gamemode is on, put back when it ends ----------
    GamemodePeripheralEntry? _gamemode;
    bool _gmDpiOn, _gmLightOff, _gmLoaded;
    string? _gmDpiText;
    int? _gmHz;
    public ICommand GamemodeCommand { get; }
    public ICommand GamemodeClearCommand { get; }
    public ICommand GmPollingCommand { get; }
    public bool CanGmDpi => Driver is { HasLiveDpi: true } && CanDpi && Dpi is not null;
    public bool CanGmPolling => CanPolling && HasPolling;
    public bool CanGmLight => Driver is { HasLiveBrightness: true } && HasLight;
    public bool CanGamemode => Driver is { BatteryOnly: false } && (CanGmDpi || CanGmPolling || CanGmLight);
    public string GmLightText => IsDock ? Txt.PeripheralGamemodeRingOff : Txt.PeripheralGamemodeLightOff;
    public bool GmDpiOn { get => _gmDpiOn; set => Set(ref _gmDpiOn, value); }
    public string? GmDpiText { get => _gmDpiText; set { if (Set(ref _gmDpiText, value)) Raise(nameof(GmDpiSlider)); } }
    public double GmDpiSlider { get => Int(_gmDpiText) ?? Dpi?.X ?? 0; set => GmDpiText = Snap(value).ToString(); }
    public bool GmLightOff { get => _gmLightOff; set => Set(ref _gmLightOff, value); }
    /// <summary>"Keep" and each supported rate; the choice is saved only by Save.</summary>
    public IReadOnlyList<OptionItem> GmRates => _state?.Polling is { } p
        ? new[] { new OptionItem("", Txt.PeripheralGamemodeKeep) { IsSelected = _gmHz is null } }
            .Concat(p.Supported.Select(hz => new OptionItem(hz.ToString(), $"{hz} Hz") { IsSelected = hz == _gmHz })).ToList()
        : Array.Empty<OptionItem>();
    public string GamemodeText => _gamemode is { } g ? Txt.PeripheralGamemodeValues(g.Dpi, g.PollingHz, g.Brightness == 0, IsDock) : Txt.PeripheralGamemodeNone;
    public bool HasGamemode => _gamemode is not null;

    GamemodePeripheralEntry? Gamemode()
    {
        try { return GamemodePeripheral.Load()?.Devices.FirstOrDefault(e => e.VendorId == Device.VendorId && e.ProductId == Device.ProductId); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    /// <summary>The editor starts from the saved profile, else from what the device has now (nothing chosen).</summary>
    void LoadGamemodeEditor()
    {
        if (_gmLoaded || _state is null) return;
        _gmLoaded = true;
        _gmDpiOn = _gamemode?.Dpi is not null;
        _gmDpiText = (_gamemode?.Dpi ?? _state.Dpi?.X)?.ToString();
        _gmHz = _gamemode?.PollingHz;
        _gmLightOff = _gamemode?.Brightness == 0;
    }

    /// <summary>Saves (keep) or removes this device's gamemode profile, each value checked against what the device reported.</summary>
    void SaveGamemode(bool keep)
    {
        try
        {
            if (!KosToolbox.Core.Execution.Elevation.IsAdmin) throw new InvalidOperationException(Txt.GpuGamemodeNeedsAdmin);
            int? dpi = null, hz = null;
            if (keep)
            {
                if (_gmDpiOn && CanGmDpi)
                    dpi = Int(_gmDpiText) is { } v && v >= Dpi!.Min && v <= Dpi.Max ? v : throw new InvalidOperationException(Txt.PeripheralGamemodeDpiInvalid(Dpi!.Min, Dpi.Max));
                if (_gmHz is { } h && CanGmPolling && _state!.Polling!.Supported.Contains(h)) hz = h;
                if (dpi is null && hz is null && !(_gmLightOff && CanGmLight)) throw new InvalidOperationException(Txt.PeripheralGamemodeNothing);
            }
            var config = GamemodePeripheral.Load() ?? new GamemodePeripheral();
            config.Devices.RemoveAll(e => e.VendorId == Device.VendorId && e.ProductId == Device.ProductId);
            if (keep)
                config.Devices.Add(new GamemodePeripheralEntry
                {
                    VendorId = Device.VendorId, ProductId = Device.ProductId, Name = Name, Dpi = dpi, PollingHz = hz,
                    Brightness = _gmLightOff && CanGmLight ? 0 : null,
                });
            config.Suspended = false; // choosing again lifts a pause
            config.Save();
            _gamemode = Gamemode();
            Raise(nameof(GamemodeText));
            Raise(nameof(HasGamemode));
            Toasts.Show(keep ? Txt.PeripheralGamemodeSaved : Txt.PeripheralGamemodeRemoved);
            Log.Info($"Peripherals: gamemode profile for {Name}: {(_gamemode is null ? "none" : GamemodeText)}.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            Toasts.Show(Txt.PeripheralWriteFailed(ex.Message), ToastKind.Error);
        }
    }

    // ---------- the picture: a drawing per model family, its light in the color and brightness the controls show ----------
    /// <summary>Which drawing: Viper, DeathAdder, Naga, Dock, Mouse, Headset, Keyboard, Controller or Other.</summary>
    public string Art => (Driver?.Model ?? Name) switch
    {
        var m when m.Contains("Dock", StringComparison.OrdinalIgnoreCase) => "Dock",
        var m when Kind == PeripheralKind.Mouse && m.Contains("Viper", StringComparison.OrdinalIgnoreCase) => "Viper",
        var m when Kind == PeripheralKind.Mouse && m.Contains("DeathAdder", StringComparison.OrdinalIgnoreCase) => "DeathAdder",
        var m when Kind == PeripheralKind.Mouse && m.Contains("Naga", StringComparison.OrdinalIgnoreCase) => "Naga",
        _ => Kind is PeripheralKind.Mouse or PeripheralKind.Headset or PeripheralKind.Keyboard or PeripheralKind.Controller ? Kind.ToString() : "Other",
    };
    /// <summary>The device has a light the toolbox reads: the drawing shows it.</summary>
    public bool HasLight => HasBrightness || HasLighting;
    static readonly Brush Rainbow = Frozen(new LinearGradientBrush(new GradientStopCollection(new[] { 0xFF0000, 0xFFFF00, 0x00FF00, 0x00FFFF, 0x0000FF, 0xFF00FF, 0xFF0000 }
        .Select((c, i) => new GradientStop(Rgb(c), i / 6.0))), 45));
    /// <summary>The light as the controls show it (a typed brightness or color included, before Apply); null while it is off.</summary>
    public Brush? LedBrush
    {
        get
        {
            if (!HasLight) return null;
            var level = Int(_brightText) ?? _state?.Brightness ?? 100;
            var effect = _state?.Lighting?.Effect ?? LightingEffect.Static;
            if (level <= 0 || effect == LightingEffect.Off) return null;
            var brush = effect == LightingEffect.Spectrum ? Rainbow.Clone() : new SolidColorBrush(Rgb(Hex(_colorText) ?? _state?.Lighting?.Color ?? 0xFFFFFF));
            brush.Opacity = 0.3 + 0.7 * Math.Min(level, 100) / 100;
            return Frozen(brush);
        }
    }
    public string LedText => !HasLight ? "" : LedBrush is null ? Txt.PeripheralLightOff
        : _state?.Lighting is { } l ? Txt.LightingEffectName(l.Effect) + (l.Effect is LightingEffect.Static or LightingEffect.Breathing && l.Color is { } c ? $" · #{c:X6}" : "")
        : Txt.PeripheralLightOn;
    static Color Rgb(int c) => Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c);
    static Brush Frozen(Brush b) { b.Freeze(); return b; }
    /// <summary>Color presets: they fill the color box (and the picture); Apply writes.</summary>
    public static IReadOnlyList<string> Swatches { get; } = new[] { "#FF0000", "#FF6A00", "#FFD000", "#00FF40", "#00E5FF", "#0050FF", "#9000FF", "#FFFFFF" };
    public ICommand SwatchCommand { get; }

    public HidDevice Device { get; }
    public PeripheralDriver? Driver { get; }
    public string Name { get; private set; }
    public bool IsSupported => Driver is not null;
    public bool IsUntested => Driver is { Tested: false };
    public bool IsBatteryOnly => Driver is { BatteryOnly: true };
    public PeripheralKind Kind => Driver?.Kind ?? Device.Kind;
    /// <summary>Connection, VID:PID and the battery, for the list.</summary>
    public string Sub => string.Join(" · ", new[] { ConnectionText, Txt.HidIds(Device.VendorId, Device.ProductId), BatteryText }.Where(s => !string.IsNullOrEmpty(s)));
    string? ConnectionText => (Driver?.Connection ?? (Device.Bluetooth ? PeripheralConnection.Bluetooth : null)) is { } c ? Txt.PeripheralConnectionName(c) : null;
    public string BatteryText => Txt.PeripheralBatteryText(_state?.Battery) + (_state?.Battery?.Charging == true ? Txt.PeripheralChargingSuffix : "");
    public string KindText => Txt.PeripheralKindName(Kind);
    public string Glyph => Kind switch
    {
        PeripheralKind.Mouse => "", PeripheralKind.Keyboard => "", PeripheralKind.Headset => "",
        PeripheralKind.Controller => "", _ => "", // mouse, keyboard, headphones, gamepad, a USB plug (escaped: the glyphs are private-use characters)
    };
    public string StatusText => Driver is null ? Txt.PeripheralNotSupported : Driver.Tested ? Driver.Family : Txt.PeripheralUntested;
    public string? Note => Driver is null ? Txt.PeripheralNotSupportedNote
        : _state?.Battery?.Asleep == true ? Txt.PeripheralAsleepNote
        : IsBatteryOnly ? Txt.PeripheralBatteryOnlyNote
        : IsUntested ? Txt.PeripheralUntestedNote : null;
    /// <summary>A vendor app for this brand is running (it may overwrite what the toolbox writes).</summary>
    public string? VendorNote { get; private set; }
    public string? Error { get => _error; private set => Set(ref _error, value); }
    public bool IsBusy { get => _busy; private set => Set(ref _busy, value); }
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }

    public InfoSection Info { get; }
    public IReadOnlyList<HidCollectionRow> Collections { get; }
    public ICommand CopyReportCommand { get; }
    public ICommand ApplyDpiCommand { get; }
    public ICommand StageCommand { get; }
    public ICommand PollingCommand { get; }
    public ICommand ApplySleepCommand { get; }
    public ICommand ApplyLowCommand { get; }
    public ICommand ApplyBrightnessCommand { get; }
    public ICommand HostModeCommand { get; }

    // ---------- what the device has ----------
    IReadOnlySet<PeripheralSetting> Writable => Driver?.Writable ?? new HashSet<PeripheralSetting>();
    public PeripheralDpi? Dpi => _state?.Dpi;
    public string DpiRange => Dpi is { } d ? Txt.PeripheralDpiRange(d) : "";
    public bool CanDpi => Writable.Contains(PeripheralSetting.Dpi);
    public IReadOnlyList<OptionItem> Stages => Dpi?.Stages is { } s
        ? s.Select((st, i) => new OptionItem((i + 1).ToString(), Txt.PeripheralStage(i + 1, st)) { IsSelected = Dpi.ActiveStage == i + 1 }).ToList()
        : Array.Empty<OptionItem>();
    public bool HasStages => Stages.Count > 0;
    public bool CanStage => Writable.Contains(PeripheralSetting.ActiveStage);
    public IReadOnlyList<OptionItem> Rates => _state?.Polling is { } p
        ? p.Supported.Select(hz => new OptionItem(hz.ToString(), $"{hz} Hz") { IsSelected = hz == p.Hz }).ToList()
        : Array.Empty<OptionItem>();
    public bool HasPolling => _state?.Polling is not null;
    public bool CanPolling => Writable.Contains(PeripheralSetting.PollingRate);
    public bool HasPerformance => Dpi is not null || HasPolling;
    public bool OnboardMode => _state?.OnboardMode == true && Writable.Contains(PeripheralSetting.HostMode);

    public bool HasBattery => _state?.Battery is not null;
    public bool HasSleep => _state?.SleepSeconds is not null;
    public bool HasLow => _state?.LowBatteryPercent is not null;
    public bool HasPower => HasBattery || HasSleep || HasLow;
    public bool CanSleep => Writable.Contains(PeripheralSetting.Sleep);
    public bool CanLow => Writable.Contains(PeripheralSetting.LowBattery);
    public bool HasBrightness => _state?.Brightness is not null;
    public bool CanBrightness => Writable.Contains(PeripheralSetting.Brightness);
    public bool HasLighting => _state?.Lighting is not null;
    public bool CanLighting => Writable.Contains(PeripheralSetting.Lighting);
    public bool HasLightingCard => HasBrightness || HasLighting || IsDock;
    /// <summary>The effects offered; none is selected when the device has one the toolbox doesn't offer.</summary>
    public IReadOnlyList<OptionItem> Effects => _state?.Lighting is { } l
        ? new[] { LightingEffect.Off, LightingEffect.Static, LightingEffect.Breathing, LightingEffect.Spectrum }
            .Select(e => new OptionItem(e.ToString(), Txt.LightingEffectName(e)) { IsSelected = e == l.Effect }).ToList()
        : Array.Empty<OptionItem>();
    public ICommand EffectCommand { get; }
    public ICommand ApplyColorCommand { get; }

    public string? DpiText { get => _dpiText; set { if (Set(ref _dpiText, value)) Raise(nameof(DpiSlider)); } }
    /// <summary>The sliders only move the text (and the picture); Apply is the one write.</summary>
    public double DpiSlider { get => Int(_dpiText) ?? Dpi?.X ?? 0; set => DpiText = Snap(value).ToString(); }
    public double DpiStep => Dpi?.Step is > 0 and var step ? step : 50;
    int Snap(double v) => (int)(Math.Round(v / DpiStep) * DpiStep);
    public string? SleepText { get => _sleepText; set => Set(ref _sleepText, value); }
    public string? LowText { get => _lowText; set => Set(ref _lowText, value); }
    public string? BrightnessText { get => _brightText; set { if (Set(ref _brightText, value)) { Raise(nameof(BrightnessSlider)); Raise(nameof(LedBrush)); Raise(nameof(LedText)); } } }
    public double BrightnessSlider { get => Int(_brightText) ?? _state?.Brightness ?? 0; set => BrightnessText = ((int)Math.Round(value)).ToString(); }
    public string? ColorText { get => _colorText; set { if (Set(ref _colorText, value)) Raise(nameof(LedBrush)); } }

    /// <summary>Reads everything the driver knows (getters only). Runs off the UI thread; the result is applied on it.</summary>
    public async Task ReadAsync()
    {
        if (Driver is not { } driver || _busy) return;
        IsBusy = true;
        try
        {
            var (state, vendor) = await Task.Run(() => (driver.Read(), PeripheralRegistry.RunningVendorApp(driver.Family)));
            VendorNote = vendor is null ? null : Txt.PeripheralVendorApp(vendor);
            Show(state);
            Error = null;
        }
        catch (Exception ex) when (ex is PeripheralException or System.ComponentModel.Win32Exception or System.IO.IOException or TimeoutException or InvalidOperationException)
        {
            Log.Warn($"Peripherals: reading {Name} failed: {ex.Message}");
            Error = Txt.PeripheralReadFailed(ex.Message);
        }
        finally { IsBusy = false; }
    }

    /// <summary>The battery only (the 60 s refresh while the page is shown).</summary>
    public async Task ReadBatteryAsync()
    {
        if (Driver is not { } driver || _busy || _state is null) return;
        try
        {
            var battery = await Task.Run(driver.ReadBattery);
            Show(_state with { Battery = battery });
        }
        catch (Exception ex) when (ex is PeripheralException or System.ComponentModel.Win32Exception or System.IO.IOException or TimeoutException) { }
    }

    Task Invalid(string problem = "enter a whole number.")
    {
        Toasts.Show(Txt.PeripheralWriteFailed(problem), ToastKind.Error);
        return Task.CompletedTask;
    }

    static int? Int(string? s) => int.TryParse(s?.Trim(), out var v) ? v : null;

    /// <summary>#RRGGBB (the # is optional) as 0xRRGGBB.</summary>
    static int? Hex(string? s) => s?.Trim().TrimStart('#') is { Length: 6 } h
        && int.TryParse(h, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    async Task Write(string what, Func<PeripheralDriver, PeripheralState> set, string? success = null)
    {
        if (Driver is not { } driver || _busy) return;
        IsBusy = true;
        try
        {
            Show(await Task.Run(() => set(driver)));
            Toasts.Show(success ?? Txt.PeripheralApplied(what));
            if (driver is { Tested: false }) Toasts.Show(Txt.PeripheralUntestedApplied(what), ToastKind.Warning);
            Log.Info($"Peripherals: {Name}: {what} written and read back.");
        }
        catch (ReadBackException ex)
        {
            Show(ex.State);
            Toasts.Show(Txt.PeripheralWriteFailed(ex.Message), ToastKind.Error);
            Log.Warn($"Peripherals: {Name}: {what}: {ex.Message}");
        }
        catch (Exception ex) when (ex is PeripheralException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception or System.IO.IOException or TimeoutException)
        {
            Toasts.Show(Txt.PeripheralWriteFailed(ex.Message), ToastKind.Error);
            Log.Warn($"Peripherals: {Name}: {what} failed: {ex.Message}");
        }
        finally { IsBusy = false; }
    }

    void Show(PeripheralState state)
    {
        _state = state;
        if (state.Name is { Length: > 0 } n) Name = n;
        DpiText = state.Dpi?.X.ToString();
        SleepText = state.SleepSeconds is { } s ? (s / 60).ToString() : null;
        LowText = state.LowBatteryPercent?.ToString();
        BrightnessText = state.Brightness?.ToString();
        if (state.Lighting?.Color is { } color) ColorText = $"#{color:X6}"; // an effect without a color keeps what was typed
        Info.Rows = InfoRows();
        LoadGamemodeEditor();
        Raise(string.Empty); // every derived property
    }

    List<InfoRow> InfoRows()
    {
        var ids = Txt.HidIds(Device.VendorId, Device.ProductId);
        var rows = new List<InfoRow> { new(Txt.PeripheralVidPid, ids), new(Txt.PeripheralProduct, Device.Product ?? "—") };
        if (Driver is not null) rows.Add(new(Txt.PeripheralFamily, Driver.Family));
        else rows.Add(new(Txt.PeripheralKindLabel, KindText));
        if (ConnectionText is { } c) rows.Add(new(Txt.PeripheralConnection, c));
        rows.Add(new(Txt.PeripheralRevision, Txt.HidRevision(Device.Version)));
        if (_state?.Firmware is { } fw) rows.Add(new(Txt.PeripheralFirmware, fw));
        if (_state?.Serial is { } sn) rows.Add(new(Txt.PeripheralSerial, sn));
        if (_state?.Protocol is { } pr) rows.Add(new(Txt.PeripheralFamily, pr));
        if (_state?.Layers is { } l) rows.Add(new(Txt.PeripheralLayers, l.ToString()));
        return rows;
    }

    void CopyReport()
    {
        try
        {
            Clipboard.SetText(Txt.PeripheralReport(Device));
            Toasts.Show(Txt.DeviceReportCopied, ToastKind.Success);
        }
        catch (ExternalException) { Toasts.Show(Txt.ClipboardBusy, ToastKind.Error); }
    }
}

/// <summary>
/// Peripherals: the HID devices connected now, one row per physical device, supported ones first. A device a driver claims (exact VID,
/// PID and collection) is read with getters when the page is shown and on Refresh, its battery again every 60 s while the page is
/// visible; nothing is written except by a click. Unclaimed devices are only enumerated (access-0 opens) and dumped. Device changes
/// re-read the list 2.5 s after Windows reports them.
/// </summary>
public sealed class PeripheralsPageViewModel : PageViewModel
{
    const int WmDeviceChange = 0x0219, DbtDevNodesChanged = 0x0007;
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromSeconds(2.5) };
    readonly DispatcherTimer _battery = new() { Interval = TimeSpan.FromSeconds(60) };
    IReadOnlyList<PeripheralRowViewModel> _devices = Array.Empty<PeripheralRowViewModel>();
    PeripheralRowViewModel? _selected;
    HwndSource? _window;
    string? _loadError;
    bool _loading, _loaded;

    /// <summary>Where the devices come from; the ui-shot probe swaps in a sample list.</summary>
    public static Func<List<HidDevice>> Source { get; set; } = HidDevices.List;
    /// <summary>Which driver claims a device; the ui-shot probe swaps in sample drivers (records only, nothing opened).</summary>
    public static Func<HidDevice, PeripheralDriver?> Claim { get; set; } = PeripheralRegistry.Claim;

    public PeripheralsPageViewModel() : base("peripherals", Txt.PagePeripherals, "", Txt.PagePeripheralsSubtitle)
    {
        SelectCommand = new RelayCommand(p => { if (p is PeripheralRowViewModel row) Selected = row; });
        // A read still running when the timer fires: try again later rather than miss the change.
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            if (_loading) _debounce.Start();
            else MainViewModel.Fire(RefreshAsync());
        };
        _battery.Tick += (_, _) => MainViewModel.Fire(Batteries());
    }

    /// <summary>The list and the selected device scroll on their own, side by side.</summary>
    public override bool ScrollsItself => true;
    public ICommand SelectCommand { get; }
    public IReadOnlyList<PeripheralRowViewModel> Devices
    {
        get => _devices;
        private set { Set(ref _devices, value); Raise(nameof(HasDevices)); Raise(nameof(Supported)); Raise(nameof(Unsupported)); }
    }
    public IReadOnlyList<PeripheralRowViewModel> Supported => _devices.Where(d => d.IsSupported).ToList();
    public IReadOnlyList<PeripheralRowViewModel> Unsupported => _devices.Where(d => !d.IsSupported).ToList();
    public bool HasSupported => Supported.Count > 0;
    public bool HasUnsupported => Unsupported.Count > 0;
    public bool HasDevices => _devices.Count > 0;
    public bool IsEmpty => _loaded && _devices.Count == 0 && _loadError is null;
    public bool IsLoading { get => _loading; private set => Set(ref _loading, value); }
    public string? LoadError { get => _loadError; private set => Set(ref _loadError, value); }

    public PeripheralRowViewModel? Selected
    {
        get => _selected;
        private set
        {
            if (!Set(ref _selected, value)) return;
            foreach (var d in _devices) d.IsSelected = ReferenceEquals(d, value);
        }
    }

    public override async Task RefreshAsync()
    {
        if (_loading) return;
        IsLoading = true;
        var keep = _selected?.Device.Key;
        try
        {
            var devices = await Task.Run(() => Source().Select(d => (Device: d, Driver: SafeClaim(d))).ToList());
            Devices = devices.Select(d => new PeripheralRowViewModel(d.Device, d.Driver))
                .OrderBy(r => r.IsSupported ? 0 : 1).ThenBy(r => r.Kind).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            Raise(nameof(HasSupported));
            Raise(nameof(HasUnsupported));
            LoadError = null;
        }
        catch (Exception ex)
        {
            Log.Error("Reading HID devices failed", ex);
            Devices = Array.Empty<PeripheralRowViewModel>();
            LoadError = Txt.PeripheralsReadFailed(ex.Message);
        }
        finally
        {
            _loaded = true;
            IsLoading = false;
            Raise(nameof(IsEmpty));
        }
        Selected = _devices.FirstOrDefault(r => r.Device.Key == keep) ?? _devices.FirstOrDefault();
        // One device at a time: drivers on different devices are independent, but this keeps the bus quiet.
        foreach (var row in _devices.Where(r => r.IsSupported).ToList()) await row.ReadAsync();
    }

    static PeripheralDriver? SafeClaim(HidDevice d)
    {
        try { return Claim(d); }
        catch (ArgumentException) { return null; }
    }

    async Task Batteries()
    {
        foreach (var row in _devices.Where(r => r.IsSupported && r.HasBattery).ToList()) await row.ReadBatteryAsync();
    }

    /// <summary>While shown, device changes re-read the list (DBT_DEVNODES_CHANGED reaches top-level windows without registering).</summary>
    public override void OnShown(bool shown)
    {
        if (shown)
        {
            _battery.Start();
            if (_window is null && Application.Current?.MainWindow is { } w && PresentationSource.FromVisual(w) is HwndSource source)
            {
                _window = source;
                source.AddHook(OnMessage);
            }
        }
        else
        {
            _battery.Stop();
            _window?.RemoveHook(OnMessage);
            _window = null;
            _debounce.Stop();
        }
    }

    IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmDeviceChange && wParam == DbtDevNodesChanged)
        {
            _debounce.Stop(); // a burst of changes (a receiver's interfaces arriving one by one) gives one read
            _debounce.Start();
        }
        return IntPtr.Zero;
    }
}
