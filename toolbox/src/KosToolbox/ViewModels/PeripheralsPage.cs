using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using KosToolbox.Core.Peripherals;
using KosToolbox.Core.Peripherals.Royuan;
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
    string? _error, _dpiText, _sleepText, _lowText, _brightText, _colorText, _actText, _rtPressText, _rtReleaseText, _deadZoneText;
    bool _selected, _busy, _rapidTrigger;

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
        ApplySwitchesCommand = new AsyncCommand(() => Mm(_actText) is { } a && Mm(_deadZoneText) is { } dz
            && (!_rapidTrigger || Mm(_rtPressText) is not null && Mm(_rtReleaseText) is not null)
            ? Write(Txt.PeripheralSwitches, d => new PeripheralMagnetic(a, _rapidTrigger, Mm(_rtPressText) ?? 0, Mm(_rtReleaseText) ?? 0, dz) is var travel
                    && d is RoyuanDriver r && _chosen.Count > 0 ? r.SetKeyTravel(_chosen.ToList(), travel) : d.SetMagnetic(travel),
                warning: Txt.PeripheralSwitchesStored(_state?.Profile))
            : Invalid(Txt.PeripheralSwitchesInvalid));
        TravelCommand = new AsyncCommand(ToggleTravel);
        KeyCommand = new RelayCommand(p => { if (p is int slot) Choose(new[] { slot }); });
        ModeKindCommand = new RelayCommand(p => { if (p is string k && byte.TryParse(k, out var kind)) { _modeKind = kind; RaiseMode(); } });
        ApplyModeCommand = new AsyncCommand(ApplyMode);
        ApplyRemapCommand = new AsyncCommand(ApplyRemap);
        RemapDefaultCommand = new AsyncCommand(RemapDefault);
        MacroLoadCommand = new AsyncCommand(MacroLoad);
        PaintCommand = new AsyncCommand(() => Paint(false));
        FillCommand = new AsyncCommand(() => Paint(true));
        SendPictureCommand = new AsyncCommand(SendPicture);
        MacroSaveCommand = new AsyncCommand(MacroSave);
        MacroClearCommand = new AsyncCommand(MacroClear);
        MacroBindCommand = new AsyncCommand(MacroBind);
        SelectAllKeysCommand = new RelayCommand(_ => Choose(_caps.Select(c => c.Slot), true));
        ClearKeysCommand = new RelayCommand(_ => Choose(_caps.Select(c => c.Slot), false));
        ProfileCommand = new AsyncCommand(p => p is string k && int.TryParse(k, out var n) && n != _state?.Profile
            ? Write(Txt.PeripheralProfile, d => d.SetProfile(n), Txt.PeripheralProfileApplied(n)) : Task.CompletedTask);
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
        GmLightCommand = new RelayCommand(p => { if (p is string k) { _gmLight = k; Raise(nameof(GmLights)); Raise(nameof(GmHasColor)); } });
        GmSwatchCommand = new RelayCommand(p => { if (p is string hex) GmColorText = hex; });
        GmRtCommand = new RelayCommand(p => { if (p is string k) { _gmRt = k; Raise(nameof(GmRts)); Raise(nameof(GmRtOn)); } });
        GmRingCommand = new RelayCommand(p => { if (p is string k) { _gmRing = k; Raise(nameof(GmRings)); } });
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

    // ---------- the battery's tray icon (KOS Governor), on unless turned off here ----------
    bool? _inTray;
    uint TrayId => (uint)Device.VendorId << 16 | Device.ProductId;
    public bool ShowInTray
    {
        get => _inTray ??= Config()?.TrayHidden.Contains(TrayId) != true;
        set
        {
            if (value == ShowInTray) return;
            try
            {
                if (!KosToolbox.Core.Execution.Elevation.IsAdmin) throw new InvalidOperationException(Txt.GpuGamemodeNeedsAdmin);
                var config = GamemodePeripheral.Load() ?? new GamemodePeripheral();
                config.TrayHidden.Remove(TrayId);
                if (!value) config.TrayHidden.Add(TrayId);
                config.Save(); // wakes the engine: the icon comes or goes in seconds
                _inTray = value;
                Toasts.Show(value ? Txt.PeripheralTrayShown : Txt.PeripheralTrayHidden);
                Log.Info($"Peripherals: {Name}: battery in the tray {(value ? "on" : "off")}.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                Toasts.Show(Txt.PeripheralWriteFailed(ex.Message), ToastKind.Error);
            }
            Raise(); // a failed save puts the switch back
        }
    }

    // ---------- gamemode profile: what KOS Governor writes while gamemode is on, put back when it ends ----------
    GamemodePeripheralEntry? _gamemode;
    bool _gmDpiOn, _gmLoaded;
    string? _gmDpiText, _gmColorText, _gmRtText;
    int? _gmHz;
    string _gmLight = "", _gmRt = ""; // light: "" keep, "off", or an effect name; rapid trigger: "" keep, "on", "off"
    public ICommand GamemodeCommand { get; }
    public ICommand GamemodeClearCommand { get; }
    public ICommand GmPollingCommand { get; }
    public ICommand GmLightCommand { get; }
    public ICommand GmSwatchCommand { get; }
    public ICommand GmRtCommand { get; }
    public bool CanGmDpi => Driver is { HasLiveDpi: true } && CanDpi && Dpi is not null;
    public bool CanGmPolling => CanPolling && HasPolling;
    /// <summary>A light gamemode can turn off: live on a mouse or the dock; stored on a keyboard that stores everything.</summary>
    public bool CanGmLight => Driver is { HasLiveBrightness: true } or { StoredOnly: true } && HasLight;
    /// <summary>Effects in gamemode: a Razer device's live effect, or a stored-only keyboard's.</summary>
    public bool CanGmEffect => CanLighting && Driver is KosToolbox.Core.Peripherals.Razer.RazerDriver { IsDock: false } or { StoredOnly: true };
    public bool CanGmRt => Writable.Contains(PeripheralSetting.Magnetic) && Driver is KosToolbox.Core.Peripherals.Royuan.RoyuanDriver;
    public bool CanGamemode => Driver is { BatteryOnly: false } && (CanGmDpi || CanGmPolling || CanGmLight || CanGmRt);
    /// <summary>The device stores every write: gamemode writes its flash on each start and stop.</summary>
    public bool GmStoredOnly => Driver is { StoredOnly: true };
    public bool GmDpiOn { get => _gmDpiOn; set => Set(ref _gmDpiOn, value); }
    public string? GmDpiText { get => _gmDpiText; set { if (Set(ref _gmDpiText, value)) Raise(nameof(GmDpiSlider)); } }
    public double GmDpiSlider { get => LogDpi(Int(_gmDpiText) ?? Dpi?.X); set => GmDpiText = Snap(Math.Exp(value)).ToString(); }
    /// <summary>A light that isn't a dock's ring: Keep, Off and the effects. The dock's ring has <see cref="GmRings"/> instead.</summary>
    public bool CanGmLightChoice => CanGmLight && !IsDock;
    public IReadOnlyList<OptionItem> GmLights => new[] { ("", Txt.PeripheralGamemodeKeep), ("off", Txt.PeripheralGamemodeRingOffShort) }
        .Concat(CanGmEffect ? Driver!.Effects.Where(e => e != LightingEffect.Off).Select(e => (e.ToString(), Txt.LightingEffectName(e))) : Array.Empty<(string, string)>())
        .Select(o => new OptionItem(o.Item1, o.Item2) { IsSelected = o.Item1 == _gmLight }).ToList();
    public bool GmHasColor => _gmLight is nameof(LightingEffect.Static) or nameof(LightingEffect.Breathing);
    public string? GmColorText { get => _gmColorText; set => Set(ref _gmColorText, value); }
    public bool CanGmRing => CanGmLight && IsDock;
    string _gmRing = ""; // "" keep, "off" ring off, "battery" / "nobattery" the mouse battery on the ring or not
    public ICommand GmRingCommand { get; }
    public IReadOnlyList<OptionItem> GmRings => new[]
    {
        ("", Txt.PeripheralGamemodeKeep), ("off", Txt.PeripheralGamemodeRingOffShort), ("battery", Txt.PeripheralGamemodeBattery), ("nobattery", Txt.PeripheralGamemodeNoBattery),
    }.Select(o => new OptionItem(o.Item1, o.Item2) { IsSelected = o.Item1 == _gmRing }).ToList();
    public IReadOnlyList<OptionItem> GmRts => new[] { ("", Txt.PeripheralGamemodeKeep), ("on", Txt.PeripheralTurnOn), ("off", Txt.PeripheralTurnOff) }
        .Select(o => new OptionItem(o.Item1, o.Item2) { IsSelected = o.Item1 == _gmRt }).ToList();
    public bool GmRtOn => _gmRt == "on";
    public string? GmRtText { get => _gmRtText; set => Set(ref _gmRtText, value); }
    /// <summary>"Keep" and each supported rate; the choice is saved only by Save.</summary>
    public IReadOnlyList<OptionItem> GmRates => _state?.Polling is { } p
        ? new[] { new OptionItem("", Txt.PeripheralGamemodeKeep) { IsSelected = _gmHz is null } }
            .Concat(p.Supported.Select(hz => new OptionItem(hz.ToString(), $"{hz} Hz") { IsSelected = hz == _gmHz })).ToList()
        : Array.Empty<OptionItem>();
    public string GamemodeText => _gamemode is { } g ? Txt.PeripheralGamemodeValues(g, IsDock) : Txt.PeripheralGamemodeNone;
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
        _gmLight = _gamemode?.Brightness == 0 ? "off" : _gamemode?.Effect?.ToString() ?? "";
        _gmColorText = $"#{_gamemode?.Color ?? _state.Lighting?.Color ?? 0xFFFFFF:X6}";
        _gmRing = _gamemode?.Brightness == 0 ? "off" : _gamemode?.DockBattery switch { true => "battery", false => "nobattery", _ => "" };
        _gmRt = _gamemode?.RapidTrigger switch { true => "on", false => "off", _ => "" };
        _gmRtText = (_gamemode?.RapidTriggerMm ?? 0.1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Another place changed the saved profile (the lighting sync): the editor shows it again.</summary>
    public void ReloadGamemode()
    {
        _gamemode = Gamemode();
        _gmLoaded = false;
        LoadGamemodeEditor();
        Raise(string.Empty);
    }

    /// <summary>Saves (keep) or removes this device's gamemode profile, each value checked against what the device reported.</summary>
    void SaveGamemode(bool keep)
    {
        try
        {
            if (!KosToolbox.Core.Execution.Elevation.IsAdmin) throw new InvalidOperationException(Txt.GpuGamemodeNeedsAdmin);
            var entry = new GamemodePeripheralEntry { VendorId = Device.VendorId, ProductId = Device.ProductId, Name = Name };
            if (keep)
            {
                if (_gmDpiOn && CanGmDpi)
                    entry.Dpi = Int(_gmDpiText) is { } v && v >= Dpi!.Min && v <= Dpi.Max ? v : throw new InvalidOperationException(Txt.PeripheralGamemodeDpiInvalid(Dpi!.Min, Dpi.Max));
                if (_gmHz is { } h && CanGmPolling && _state!.Polling!.Supported.Contains(h)) entry.PollingHz = h;
                if (CanGmLightChoice && _gmLight == "off") entry.Brightness = 0;
                else if (CanGmLightChoice && CanGmEffect && Enum.TryParse<LightingEffect>(_gmLight, out var fx))
                {
                    entry.Effect = fx;
                    if (GmHasColor) entry.Color = Hex(_gmColorText) ?? throw new InvalidOperationException(Txt.PeripheralColorInvalid);
                }
                if (CanGmRing && _gmRing != "")
                {
                    if (_gmRing == "off") entry.Brightness = 0;
                    else entry.DockBattery = _gmRing == "battery";
                }
                if (CanGmRt && _gmRt != "")
                {
                    entry.RapidTrigger = _gmRt == "on";
                    if (GmRtOn)
                        entry.RapidTriggerMm = double.TryParse(_gmRtText?.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var mm) && mm is >= 0.01 and <= PeripheralMagnetic.RtMaxMm
                            ? mm : throw new InvalidOperationException(Txt.PeripheralGamemodeRtInvalid(PeripheralMagnetic.RtMaxMm));
                }
                if (entry is { Dpi: null, PollingHz: null, Brightness: null, Effect: null, DockBattery: null, RapidTrigger: null })
                    throw new InvalidOperationException(Txt.PeripheralGamemodeNothing);
            }
            var config = GamemodePeripheral.Load() ?? new GamemodePeripheral();
            config.Devices.RemoveAll(e => e.VendorId == Device.VendorId && e.ProductId == Device.ProductId);
            if (keep) config.Devices.Add(entry);
            config.Suspended = false; // choosing again lifts a pause
            config.Save();
            _gamemode = Gamemode();
            Raise(nameof(GamemodeText));
            Raise(nameof(HasGamemode));
            Toasts.Show(keep ? Txt.PeripheralGamemodeSaved : Txt.PeripheralGamemodeRemoved);
            if (keep && GmStoredOnly) Toasts.Show(Txt.PeripheralGamemodeStoredNote, ToastKind.Warning);
            Log.Info($"Peripherals: gamemode profile for {Name}: {(_gamemode is null ? "none" : GamemodeText)}.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            Toasts.Show(Txt.PeripheralWriteFailed(ex.Message), ToastKind.Error);
        }
    }

    // ---------- lighting sync (the page's card): one effect and color on several devices ----------
    bool _synced = true;
    /// <summary>Part of the lighting sync: a device whose light the toolbox writes (not a dock showing the mouse battery).</summary>
    public bool CanSync => CanLighting && !(IsDock && _dockBattery);
    public bool IsSynced { get => _synced; set => Set(ref _synced, value); }

    /// <summary>The sync's choice on this device: off (brightness 0 on a keyboard, the off effect elsewhere) or an effect lit.</summary>
    public Task ApplySync(LightingEffect effect, int color) => Write(Txt.PeripheralLighting, d => d switch
    {
        KosToolbox.Core.Peripherals.Royuan.RoyuanDriver r => effect == LightingEffect.Off ? r.SetBrightness(0) : r.SetLightingLit(effect, color),
        _ => d.SetLighting(effect, color),
    });

    // ---------- the picture: a drawing per model family, its light in the color and brightness the controls show ----------
    /// <summary>Which picture: Dock, Mouse, Headset, Keyboard, Controller or Other.</summary>
    public string Art => (Driver?.Model ?? Name) switch
    {
        var m when Driver is not null && m.Contains("Dock", StringComparison.OrdinalIgnoreCase) => "Dock", // not a laptop dock's HID interface
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
    public bool IsSelected { get => _selected; set { if (Set(ref _selected, value) && !value) StopTravel(); } }

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
    /// <summary>Up to four rates per row: seven don't fit one row of the controls column.</summary>
    public int RateColumns => Math.Clamp(_state?.Polling?.Supported.Count ?? 1, 1, 4);
    public bool HasPolling => _state?.Polling is not null;
    public bool CanPolling => Writable.Contains(PeripheralSetting.PollingRate);
    public bool HasPerformance => Dpi is not null || HasPolling || HasProfile;
    public bool HasSwitches => _state?.Magnetic is not null;
    public bool CanSwitches => Writable.Contains(PeripheralSetting.Magnetic);
    public bool SwitchesMixed => SwitchTravel is { Uniform: false };
    public string SwitchesMixedText => _chosen.Count > 0 ? Txt.PeripheralKeysMixed : Txt.PeripheralSwitchesMixed;
    public string KeysChosenText => _chosen.Count > 0 ? Txt.PeripheralKeysChosen(_chosen.Count) : Txt.PeripheralKeysAll;
    /// <summary>The travel the editor shows: the chosen keys', or every key's.</summary>
    PeripheralMagnetic? SwitchTravel => _chosen.Count > 0 && Driver is RoyuanDriver r ? r.KeyTravel(_chosen) : _state?.Magnetic;

    // ---------- the keyboard: one cap per key, chosen by click; the Switches editor then applies to the chosen keys only ----------
    readonly HashSet<int> _chosen = new();
    IReadOnlyList<KeyCapViewModel> _caps = Array.Empty<KeyCapViewModel>();
    public IReadOnlyList<KeyCapViewModel> KeyCaps { get => _caps; private set => Set(ref _caps, value); }
    public bool HasKeyCaps => _caps.Count > 0;
    public ICommand KeyCommand { get; }
    public ICommand SelectAllKeysCommand { get; }
    public ICommand ClearKeysCommand { get; }

    void BuildCaps()
    {
        if (Driver is not RoyuanDriver { Layout: { } layout, Keymap: { } keymap } r) { KeyCaps = Array.Empty<KeyCapViewModel>(); return; }
        _chosen.IntersectWith(layout.Select(k => k.Slot));
        KeyCaps = layout.Select(k => new KeyCapViewModel(k, keymap[k.Slot].Label, r.KeyTravel(new[] { k.Slot }) is { } t ? MmText(t.ActuationMm) : null)
            { IsSelected = _chosen.Contains(k.Slot), Paint = k.Slot < RoyuanCodec.PictureKeys ? _picture[k.Slot] : 0 }).ToList();
    }

    void Choose(IEnumerable<int> slots, bool? on = null)
    {
        foreach (var slot in slots)
        {
            var cap = _caps.FirstOrDefault(c => c.Slot == slot);
            if (cap is null) continue;
            cap.IsSelected = on ?? !cap.IsSelected;
            if (cap.IsSelected) _chosen.Add(slot); else _chosen.Remove(slot);
        }
        LoadSwitchFields();
        LoadKeyMode();
        LoadRemap();
        Raise(nameof(SwitchesMixed));
        Raise(nameof(SwitchesMixedText));
        Raise(nameof(KeysChosenText));
    }

    // ---------- key mode (one chosen key; two for snap tap) ----------
    byte _modeKind;
    bool _modeRepeat;
    string? _holdMsText, _dksPointText;
    UsageOption? _holdKey, _tapKey;
    /// <summary>The key codes a sub-layer can take: none, then the HID keyboard usages with their names.</summary>
    public static IReadOnlyList<UsageOption> Usages { get; } = new[] { new UsageOption(0, Txt.PeripheralModeNone) }
        .Concat(Enumerable.Range(0x04, 0x70).Concat(Enumerable.Range(0xE0, 8)).Select(u => new UsageOption((byte)u, RoyuanKey.Usage((byte)u)))).ToList();
    public IReadOnlyList<DksRowViewModel> DksRows { get; } = Enumerable.Range(0, 4).Select(i => new DksRowViewModel(i)).ToList();
    public bool CanKeyMode => Driver is RoyuanDriver && CanSwitches && _chosen.Count is 1 or 2;
    public string KeyModeHint => _chosen.Count switch { 1 when _modeKind == RoyuanCodec.KindSnap => Txt.PeripheralModeSnapNeedsTwo, 1 => "", 2 when _modeKind == RoyuanCodec.KindSnap => Txt.PeripheralModeSnapNote, 2 => Txt.PeripheralModeSnapNeedsTwo, _ => Txt.PeripheralModeOneKey };
    public IReadOnlyList<OptionItem> ModeKinds => new (byte Kind, string Label)[]
    {
        (RoyuanCodec.KindPlain, Txt.PeripheralModePlain), (RoyuanCodec.KindSnap, Txt.PeripheralModeSnap), (RoyuanCodec.KindToggle, Txt.PeripheralModeToggle),
        (RoyuanCodec.KindModTap, Txt.PeripheralModeModTap), (RoyuanCodec.KindDks, Txt.PeripheralModeDks),
    }.Select(k => new OptionItem(k.Kind.ToString(), k.Label) { IsSelected = k.Kind == (_modeKind == RoyuanCodec.KindToggleRepeat ? RoyuanCodec.KindToggle : _modeKind) }).ToList();
    public ICommand ModeKindCommand { get; }
    public ICommand ApplyModeCommand { get; }
    public bool ModeIsToggle => _modeKind is RoyuanCodec.KindToggle or RoyuanCodec.KindToggleRepeat;
    public bool ModeIsModTap => _modeKind == RoyuanCodec.KindModTap;
    public bool ModeIsDks => _modeKind == RoyuanCodec.KindDks;
    public string ModeNote => _modeKind switch
    {
        RoyuanCodec.KindToggle or RoyuanCodec.KindToggleRepeat => Txt.PeripheralModeToggleNote, RoyuanCodec.KindModTap => Txt.PeripheralModeModTapNote,
        RoyuanCodec.KindDks => Txt.PeripheralModeDksNote, _ => "",
    };
    public bool ModeRepeat { get => _modeRepeat; set => Set(ref _modeRepeat, value); }
    public string? HoldMsText { get => _holdMsText; set => Set(ref _holdMsText, value); }
    public string? DksPointText { get => _dksPointText; set => Set(ref _dksPointText, value); }
    public UsageOption? HoldKey { get => _holdKey; set => Set(ref _holdKey, value); }
    public UsageOption? TapKey { get => _tapKey; set => Set(ref _tapKey, value); }

    static UsageOption? Option(RoyuanKey k) => k.Type == 0 ? Usages.FirstOrDefault(u => u.Code == k.B) : null;

    void LoadKeyMode()
    {
        if (Driver is not RoyuanDriver r || _chosen.Count == 0 || r.KeyMode(_chosen.First()) is not { } m) { RaiseMode(); return; }
        _modeKind = _chosen.Count == 2 ? RoyuanCodec.KindSnap : m.Kind;
        ModeRepeat = m.Kind == RoyuanCodec.KindToggleRepeat;
        HoldMsText = (m.HoldMs > 0 ? m.HoldMs : 200).ToString();
        DksPointText = MmText(m.DksPointMm > 0 ? m.DksPointMm : 0.5);
        HoldKey = Option(m.Layers[0]);
        TapKey = Option(m.Layers[1]);
        for (var i = 0; i < 4; i++) DksRows[i].Load(Option(m.Layers[i]), m.Actions[i]);
        RaiseMode();
    }

    void RaiseMode()
    {
        foreach (var n in new[] { nameof(CanKeyMode), nameof(KeyModeHint), nameof(ModeKinds), nameof(ModeIsToggle), nameof(ModeIsModTap), nameof(ModeIsDks), nameof(ModeNote) }) Raise(n);
    }

    Task ApplyMode()
    {
        if (Driver is not RoyuanDriver r || _chosen.Count == 0 || r.KeyMode(_chosen.First()) is not { } now) return Task.CompletedTask;
        var slot = _chosen.First();
        RoyuanKeyMode? want = _modeKind switch
        {
            RoyuanCodec.KindSnap when _chosen.Count == 2 => now with { Kind = RoyuanCodec.KindSnap, Partner = _chosen.Last() },
            RoyuanCodec.KindPlain when _chosen.Count == 1 => now with { Kind = RoyuanCodec.KindPlain, Partner = null },
            RoyuanCodec.KindToggle or RoyuanCodec.KindToggleRepeat when _chosen.Count == 1 =>
                now with { Kind = _modeRepeat ? RoyuanCodec.KindToggleRepeat : RoyuanCodec.KindToggle, Partner = null },
            RoyuanCodec.KindModTap when _chosen.Count == 1 && Int(_holdMsText) is { } ms && _holdKey is { Code: > 0 } hold && _tapKey is { Code: > 0 } tap =>
                now with { Kind = RoyuanCodec.KindModTap, Partner = null, HoldMs = ms, Layers = new[] { hold.Key, tap.Key, now.Layers[2], now.Layers[3] } },
            RoyuanCodec.KindDks when _chosen.Count == 1 && Mm(_dksPointText) is { } mm && DksRows[0].Key is { Code: > 0 } =>
                now with { Kind = RoyuanCodec.KindDks, Partner = null, DksPointMm = mm, Actions = DksRows.Select(d => d.Actions).ToArray(),
                    Layers = DksRows.Select(d => d.Key?.Key ?? default).ToArray() },
            _ => null,
        };
        return want is null ? Invalid(Txt.PeripheralModeInvalid)
            : Write(Txt.PeripheralKeyMode, d => ((RoyuanDriver)d).SetKeyMode(slot, want), warning: Txt.PeripheralSwitchesStored(_state?.Profile));
    }

    // ---------- remap (one chosen key): base layer, Fn layer, factory default ----------
    UsageOption? _remapKey, _remapFn;
    public bool CanRemap => Driver is RoyuanDriver && CanSwitches && _chosen.Count == 1;
    public UsageOption? RemapKey { get => _remapKey; set => Set(ref _remapKey, value); }
    public UsageOption? RemapFn { get => _remapFn; set => Set(ref _remapFn, value); }
    /// <summary>What the chosen key has now, whatever its kind (a consumer key, Fn, a macro): a list choice left empty keeps it.</summary>
    public string RemapKeyLabel => $"{Txt.PeripheralRemapKey}: {KeyNow(r => r.Keymap)}";
    public string RemapFnLabel => $"{Txt.PeripheralRemapFn}: {KeyNow(r => r.FnLayer)}";
    string KeyNow(Func<RoyuanDriver, IReadOnlyList<RoyuanKey>?> layer) =>
        Driver is RoyuanDriver r && _chosen.Count == 1 && layer(r) is { } l && l[_chosen.First()] is var k ? (k.IsEmpty ? Txt.PeripheralModeNone : k.Label) : "";
    public ICommand ApplyRemapCommand { get; }
    public ICommand RemapDefaultCommand { get; }

    void LoadRemap()
    {
        if (Driver is RoyuanDriver r && _chosen.Count == 1)
        {
            var slot = _chosen.First();
            RemapKey = r.Keymap is { } k ? Option(k[slot]) : null;
            RemapFn = r.FnLayer is { } f ? Option(f[slot]) : null;
        }
        Raise(nameof(CanRemap));
        Raise(nameof(RemapKeyLabel));
        Raise(nameof(RemapFnLabel));
    }

    Task ApplyRemap()
    {
        if (Driver is not RoyuanDriver || _chosen.Count != 1) return Task.CompletedTask;
        var slot = _chosen.First();
        var key = _remapKey?.Key; // an empty choice keeps what the key has (it can be a kind the list doesn't offer)
        var fn = _remapFn?.Key;
        return Write(Txt.PeripheralRemap, d =>
        {
            var driver = (RoyuanDriver)d;
            var state = key is { } k && driver.Keymap?[slot] != k ? driver.SetKey(slot, 0, k) : d.Read();
            return fn is { } f && driver.FnLayer?[slot] != f ? driver.SetFnKey(slot, f) : state;
        }, warning: Txt.PeripheralSwitchesStored(_state?.Profile));
    }

    Task RemapDefault()
    {
        if (Driver is not RoyuanDriver { Identified: { } model } || _chosen.Count != 1 || model.Default(_chosen.First()) is not { } key) return Task.CompletedTask;
        var slot = _chosen.First();
        return Write(Txt.PeripheralRemapDefault, d => ((RoyuanDriver)d).SetKey(slot, 0, key));
    }

    // ---------- per-key colors: painted here (the keyboard can't report them), sent whole, never confirmed by a read ----------
    readonly int[] _picture = Enumerable.Repeat(0xFFFFFF, RoyuanCodec.PictureKeys).ToArray();
    string? _paintText = "#FFFFFF";
    public string? PaintText { get => _paintText; set => Set(ref _paintText, value); }
    public ICommand PaintCommand { get; }
    public ICommand FillCommand { get; }
    public ICommand SendPictureCommand { get; }

    Task Paint(bool all)
    {
        if (Hex(_paintText) is not { } color) return Invalid(Txt.PeripheralColorInvalid);
        foreach (var cap in _caps.Where(c => all || _chosen.Contains(c.Slot)))
            if (cap.Slot < RoyuanCodec.PictureKeys) { _picture[cap.Slot] = color; cap.Paint = color; }
        return Task.CompletedTask;
    }

    async Task SendPicture()
    {
        await Write(Txt.PeripheralPictureSend, d => ((RoyuanDriver)d).SendPicture(_picture.ToArray()), Txt.PeripheralPictureSent);
    }

    // ---------- macros ----------
    int _macroSlot, _macroMode;
    string? _macroRepeat = "1", _macroSteps;
    public bool HasMacros => Driver is RoyuanDriver { MacroWrites: true } && CanSwitches;
    public bool HasPicture => Driver is RoyuanDriver && CanSwitches;
    public IReadOnlyList<int> MacroSlots { get; } = Enumerable.Range(1, RoyuanMacro.Slots).ToList();
    public int MacroSlot { get => _macroSlot + 1; set => Set(ref _macroSlot, Math.Clamp(value - 1, 0, RoyuanMacro.Slots - 1)); }
    public string? MacroRepeat { get => _macroRepeat; set => Set(ref _macroRepeat, value); }
    public string? MacroSteps { get => _macroSteps; set => Set(ref _macroSteps, value); }
    public IReadOnlyList<string> MacroModes => Txt.PeripheralMacroModes;
    public int MacroMode { get => _macroMode; set => Set(ref _macroMode, value); }
    public ICommand MacroLoadCommand { get; }
    public ICommand MacroSaveCommand { get; }
    public ICommand MacroClearCommand { get; }
    public ICommand MacroBindCommand { get; }

    async Task MacroLoad()
    {
        if (Driver is not RoyuanDriver r || _busy) return;
        StopTravel();
        IsBusy = true;
        try
        {
            var m = await Task.Run(() => r.ReadMacro(_macroSlot));
            MacroRepeat = m?.Repeat.ToString();
            MacroSteps = m is null ? null : string.Join(Environment.NewLine, m.Steps.Select(StepText));
        }
        catch (Exception ex) when (ex is PeripheralException or ArgumentException or System.ComponentModel.Win32Exception or System.IO.IOException)
        {
            Toasts.Show(Txt.PeripheralWriteFailed(ex.Message), ToastKind.Error);
        }
        finally { IsBusy = false; }
    }

    static string StepText(RoyuanMacroStep s) => $"{(s.MouseButton ? $"Mouse{s.Code + 1}" : RoyuanKey.Usage(s.Code))} {(s.Pressed ? "down" : "up")} {s.DelayMs}";

    /// <summary>"key down|up ms" per line; null with the bad line's number.</summary>
    static (RoyuanMacroStep[]? Steps, int BadLine) ParseSteps(string? text)
    {
        var steps = new List<RoyuanMacroStep>();
        var lines = (text ?? "").Split('\n').Select(l => l.Trim()).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Length == 0) continue;
            var p = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length != 3 || p[1] is not ("down" or "up") || !int.TryParse(p[2], out var ms)) return (null, i + 1);
            if (p[0].StartsWith("Mouse", StringComparison.OrdinalIgnoreCase) && int.TryParse(p[0][5..], out var b) && b is >= 1 and <= 5)
                steps.Add(new RoyuanMacroStep((byte)(b - 1), true, p[1] == "down", ms));
            else if (Usages.FirstOrDefault(u => u.Code > 0 && string.Equals(u.Label, p[0], StringComparison.OrdinalIgnoreCase)) is { } u)
                steps.Add(new RoyuanMacroStep(u.Code, false, p[1] == "down", ms));
            else return (null, i + 1);
        }
        return (steps.ToArray(), 0);
    }

    Task MacroSave()
    {
        var (steps, bad) = ParseSteps(_macroSteps);
        if (steps is null) return Invalid(Txt.PeripheralMacroInvalid(bad));
        if (Int(_macroRepeat) is not { } repeat) return Invalid();
        var slot = _macroSlot;
        return Write(Txt.PeripheralMacros, d => ((RoyuanDriver)d).SetMacro(slot, new RoyuanMacro(repeat, steps)));
    }

    Task MacroClear()
    {
        var slot = _macroSlot;
        MacroSteps = null;
        return Write(Txt.PeripheralMacroClear, d => ((RoyuanDriver)d).SetMacro(slot, new RoyuanMacro(0, Array.Empty<RoyuanMacroStep>())));
    }

    Task MacroBind()
    {
        if (_chosen.Count != 1) return Invalid(Txt.PeripheralMacroBindNote);
        var slot = _chosen.First();
        var key = new RoyuanKey(9, (byte)_macroMode, (byte)_macroSlot, 0);
        return Write(Txt.PeripheralMacroBind, d => ((RoyuanDriver)d).SetKey(slot, 0, key), warning: Txt.PeripheralSwitchesStored(_state?.Profile));
    }

    void LoadSwitchFields()
    {
        if (SwitchTravel is not { } m) return;
        ActuationText = MmText(m.ActuationMm);
        RapidTrigger = m.RapidTrigger;
        RtPressText = MmText(m.RtPressMm);
        RtReleaseText = MmText(m.RtReleaseMm);
        DeadZoneText = MmText(m.BottomDeadZoneMm);
    }
    public string SwitchesRange => Txt.PeripheralSwitchesRange(_state?.Magnetic?.StepMm ?? 0.01);
    public ICommand ApplySwitchesCommand { get; }

    // ---------- live key travel (ROYUAN magnetic boards; a RAM flag on the keyboard, off when the page or the device is left) ----------
    RoyuanTravelStream? _travel;
    CancellationTokenSource? _travelStop;
    string? _travelText;
    double _travelMm;
    public bool CanTravel => Driver is RoyuanDriver && CanSwitches;
    public bool TravelOn => _travel is not null;
    public string TravelButtonText => TravelOn ? Txt.PeripheralTravelStop : Txt.PeripheralTravelStart;
    public string? TravelText { get => _travelText; private set => Set(ref _travelText, value); }
    public double TravelMm { get => _travelMm; private set => Set(ref _travelMm, value); }
    public ICommand TravelCommand { get; }

    async Task ToggleTravel()
    {
        if (TravelOn) { StopTravel(); return; }
        if (Driver is not RoyuanDriver driver || _busy) return;
        try { _travel = await Task.Run(driver.StartTravelStream); }
        catch (Exception ex) when (ex is PeripheralException or System.ComponentModel.Win32Exception or System.IO.IOException or TimeoutException)
        {
            Toasts.Show(Txt.PeripheralTravelFailed(ex.Message), ToastKind.Error);
            return;
        }
        TravelText = Txt.PeripheralTravelHint;
        TravelMm = 0;
        Raise(nameof(TravelOn));
        Raise(nameof(TravelButtonText));
        var stream = _travel;
        var stop = _travelStop = new CancellationTokenSource();
        var ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _ = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
                if (stream.Next(200) is { } k)
                    ui.BeginInvoke(() =>
                    {
                        if (stop.IsCancellationRequested) return;
                        TravelText = Txt.PeripheralTravelKey(k.Slot, k.Mm);
                        TravelMm = k.Mm;
                        if (_caps.FirstOrDefault(c => c.Slot == k.Slot) is { } cap) cap.LiveMm = k.Mm;
                    });
            stream.Dispose(); // turns the stream off on the keyboard
        });
    }

    /// <summary>Stops the live travel (the reading loop turns it off on the keyboard and closes the collection).</summary>
    public void StopTravel()
    {
        if (_travel is null) return;
        _travelStop?.Cancel();
        _travel = null;
        TravelText = null;
        TravelMm = 0;
        foreach (var cap in _caps) cap.LiveMm = 0;
        Raise(nameof(TravelOn));
        Raise(nameof(TravelButtonText));
    }
    public string? ActuationText { get => _actText; set { if (Set(ref _actText, value)) Raise(nameof(ActuationSlider)); } }
    public double ActuationSlider { get => Mm(_actText) ?? _state?.Magnetic?.ActuationMm ?? 2; set => ActuationText = MmText(SnapMm(value)); }
    public bool RapidTrigger { get => _rapidTrigger; set => Set(ref _rapidTrigger, value); }
    public string? RtPressText { get => _rtPressText; set => Set(ref _rtPressText, value); }
    public string? RtReleaseText { get => _rtReleaseText; set => Set(ref _rtReleaseText, value); }
    public string? DeadZoneText { get => _deadZoneText; set => Set(ref _deadZoneText, value); }
    double SnapMm(double mm) => _state?.Magnetic?.StepMm is { } s and > 0 ? Math.Round(mm / s) * s : Math.Round(mm, 2);
    static string MmText(double mm) => mm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    /// <summary>Millimetres typed with a dot or a comma.</summary>
    static double? Mm(string? s) => double.TryParse(s?.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    public ICommand ProfileCommand { get; }
    public IReadOnlyList<OptionItem> Profiles => _state is { Profile: { } active, Profiles: { } count }
        ? Enumerable.Range(1, count).Select(n => new OptionItem(n.ToString(), Txt.PeripheralProfileItem(n)) { IsSelected = n == active }).ToList()
        : Array.Empty<OptionItem>();
    public bool HasProfile => Profiles.Count > 0;
    public bool CanProfile => Writable.Contains(PeripheralSetting.Profile);
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
    public IReadOnlyList<OptionItem> Effects => _state?.Lighting is { } l && Driver is { } d
        ? d.Effects
            .Select(e => new OptionItem(e.ToString(), Txt.LightingEffectName(e)) { IsSelected = e == l.Effect }).ToList()
        : Array.Empty<OptionItem>();
    public ICommand EffectCommand { get; }
    public ICommand ApplyColorCommand { get; }

    public string? DpiText { get => _dpiText; set { if (Set(ref _dpiText, value)) Raise(nameof(DpiSlider)); } }
    /// <summary>The sliders only move the text (and the picture); Apply is the one write.</summary>
    /// <summary>Logarithmic: 400, 800 and 1600 sit apart instead of in the first tenth of a 100–20000 track.</summary>
    public double DpiSlider { get => LogDpi(Int(_dpiText) ?? Dpi?.X); set => DpiText = Snap(Math.Exp(value)).ToString(); }
    public double DpiLogMin => LogDpi(Dpi?.Min);
    public double DpiLogMax => LogDpi(Dpi?.Max);
    static double LogDpi(int? v) => Math.Log(Math.Max(1, v ?? 1));
    double DpiStep => Dpi?.Step is > 0 and var step ? step : 50;
    int Snap(double v) => Math.Clamp((int)(Math.Round(v / DpiStep) * DpiStep), Dpi?.Min ?? 1, Dpi?.Max ?? int.MaxValue);
    public string? SleepText { get => _sleepText; set => Set(ref _sleepText, value); }
    public string? LowText { get => _lowText; set => Set(ref _lowText, value); }
    public string? BrightnessText { get => _brightText; set { if (Set(ref _brightText, value)) { Raise(nameof(BrightnessSlider)); Raise(nameof(LedBrush)); Raise(nameof(LedText)); } } }
    public double BrightnessSlider { get => Int(_brightText) ?? _state?.Brightness ?? 0; set => BrightnessText = ((int)Math.Round(value)).ToString(); }
    public string? ColorText { get => _colorText; set { if (Set(ref _colorText, value)) Raise(nameof(LedBrush)); } }

    /// <summary>Reads everything the driver knows (getters only). Runs off the UI thread; the result is applied on it.</summary>
    public async Task ReadAsync()
    {
        if (Driver is not { } driver || _busy) return;
        StopTravel(); // the travel reads hold the device's HID lock while they wait
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
    internal static int? Hex(string? s) => s?.Trim().TrimStart('#') is { Length: 6 } h
        && int.TryParse(h, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    async Task Write(string what, Func<PeripheralDriver, PeripheralState> set, string? success = null, string? warning = null)
    {
        if (Driver is not { } driver || _busy) return;
        StopTravel(); // the travel reads hold the device's HID lock while they wait
        IsBusy = true;
        try
        {
            Show(await Task.Run(() => set(driver)));
            Toasts.Show(success ?? Txt.PeripheralApplied(what));
            if (warning is not null) Toasts.Show(warning, ToastKind.Warning);
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
        BuildCaps();
        LoadSwitchFields();
        LoadKeyMode();
        LoadRemap();
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
        SyncEffectCommand = new RelayCommand(p => { if (p is string k && Enum.TryParse<LightingEffect>(k, out var e)) { _syncEffect = e; Raise(nameof(SyncEffects)); Raise(nameof(SyncHasColor)); } });
        SyncSwatchCommand = new RelayCommand(p => { if (p is string hex) SyncColorText = hex; });
        SyncApplyCommand = new AsyncCommand(ApplySync);
        SyncGamemodeCommand = new RelayCommand(SyncGamemode);
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
        Raise(nameof(SyncRows));
        Raise(nameof(HasSync));
    }

    static PeripheralDriver? SafeClaim(HidDevice d)
    {
        try { return Claim(d); }
        catch (ArgumentException) { return null; }
    }

    // ---------- lighting sync: one effect and color on the checked devices, applied now or saved for gamemode ----------
    LightingEffect _syncEffect = LightingEffect.Static;
    string? _syncColor = "#FF0000";
    public IReadOnlyList<PeripheralRowViewModel> SyncRows => _devices.Where(d => d.CanSync).ToList();
    public bool HasSync => SyncRows.Count >= 2;
    public IReadOnlyList<OptionItem> SyncEffects => new[] { LightingEffect.Off, LightingEffect.Static, LightingEffect.Breathing, LightingEffect.Spectrum }
        .Select(e => new OptionItem(e.ToString(), Txt.LightingEffectName(e)) { IsSelected = e == _syncEffect }).ToList();
    public bool SyncHasColor => _syncEffect is LightingEffect.Static or LightingEffect.Breathing;
    public string? SyncColorText { get => _syncColor; set => Set(ref _syncColor, value); }
    public static IReadOnlyList<string> Swatches => PeripheralRowViewModel.Swatches;
    public ICommand SyncEffectCommand { get; }
    public ICommand SyncSwatchCommand { get; }
    public ICommand SyncApplyCommand { get; }
    public ICommand SyncGamemodeCommand { get; }

    /// <summary>The checked devices and the color, or null after an error toast.</summary>
    (List<PeripheralRowViewModel> Rows, int Color)? SyncChoice()
    {
        var rows = SyncRows.Where(r => r.IsSynced).ToList();
        if (rows.Count == 0) { Toasts.Show(Txt.PeripheralWriteFailed(Txt.PeripheralSyncNone), ToastKind.Error); return null; }
        if (!SyncHasColor) return (rows, 0);
        if (PeripheralRowViewModel.Hex(_syncColor) is { } c) return (rows, c);
        Toasts.Show(Txt.PeripheralWriteFailed(Txt.PeripheralColorInvalid), ToastKind.Error);
        return null;
    }

    /// <summary>Each checked device in turn, each write read back (a toast per device).</summary>
    async Task ApplySync()
    {
        if (SyncChoice() is not { } choice) return;
        foreach (var row in choice.Rows) await row.ApplySync(_syncEffect, choice.Color);
    }

    /// <summary>The same light in each checked device's gamemode profile (off, or the effect and color); the rest of each profile kept.</summary>
    void SyncGamemode()
    {
        if (SyncChoice() is not { } choice) return;
        var rows = choice.Rows.Where(r => r.CanGmLightChoice && (_syncEffect == LightingEffect.Off || r.CanGmEffect)).ToList();
        try
        {
            if (!KosToolbox.Core.Execution.Elevation.IsAdmin) throw new InvalidOperationException(Txt.GpuGamemodeNeedsAdmin);
            if (rows.Count == 0) throw new InvalidOperationException(Txt.PeripheralSyncNone);
            var config = GamemodePeripheral.Load() ?? new GamemodePeripheral();
            foreach (var row in rows)
            {
                var e = config.Devices.FirstOrDefault(x => x.VendorId == row.Device.VendorId && x.ProductId == row.Device.ProductId);
                if (e is null) config.Devices.Add(e = new GamemodePeripheralEntry { VendorId = row.Device.VendorId, ProductId = row.Device.ProductId, Name = row.Name });
                var off = _syncEffect == LightingEffect.Off;
                (e.Brightness, e.Effect, e.Color) = (off ? 0 : null, off ? null : _syncEffect, SyncHasColor ? choice.Color : null);
            }
            config.Suspended = false;
            config.Save();
            foreach (var row in rows) row.ReloadGamemode();
            Toasts.Show(Txt.PeripheralSyncGamemodeSaved);
            Log.Info($"Peripherals: lighting sync saved for gamemode on {string.Join(", ", rows.Select(r => r.Name))}: {_syncEffect}.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            Toasts.Show(Txt.PeripheralWriteFailed(ex.Message), ToastKind.Error);
        }
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
            foreach (var d in _devices) d.StopTravel();
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

/// <summary>One key of the keyboard picture: where it sits (in pixels of <see cref="Unit"/> per key), what it does, its actuation, and
/// how far it is pressed while the live travel runs.</summary>
public sealed class KeyCapViewModel(RoyuanKeyPosition position, string label, string? actuation) : ObservableObject
{
    public const double Unit = 40, MaxTravelMm = 4;
    bool _selected;
    double _liveMm;
    public int Slot => position.Slot;
    public string Label => label;
    public string? Actuation => actuation;
    public double Left => position.X * Unit;
    public double Top => position.Row * Unit;
    public double Width => position.Width * Unit - 4;
    public double Height => Unit - 4;
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    int _paint;
    /// <summary>The key's color in the page's per-key picture (0xRRGGBB).</summary>
    public int Paint { get => _paint; set { if (Set(ref _paint, value)) Raise(nameof(PaintBrush)); } }
    public System.Windows.Media.Brush PaintBrush => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)(_paint >> 16), (byte)(_paint >> 8), (byte)_paint));
    /// <summary>The live travel in mm (0 when released or the stream is off); the cap fills from the bottom with it.</summary>
    public double LiveMm { get => _liveMm; set { if (Set(ref _liveMm, value)) Raise(nameof(LiveHeight)); } }
    public double LiveHeight => Math.Clamp(_liveMm / MaxTravelMm, 0, 1) * Height;
}

/// <summary>A key code the key-mode editor offers.</summary>
public sealed record UsageOption(byte Code, string Label)
{
    public RoyuanKey Key => Code == 0 ? default : new RoyuanKey(0, 0, Code, 0);
    public override string ToString() => Label;
}

/// <summary>One dynamic-keystroke row: a key (sub-layer <see cref="Layer"/>) and what it does at each of the four events.</summary>
public sealed class DksRowViewModel(int layer) : ObservableObject
{
    UsageOption? _key;
    readonly int[] _cells = new int[4];
    public int Layer => layer;
    public UsageOption? Key { get => _key; set => Set(ref _key, value); }
    public IReadOnlyList<string> CellChoices => Txt.PeripheralDksCells;
    public int Cell0 { get => _cells[0]; set => SetCell(0, value); }
    public int Cell1 { get => _cells[1]; set => SetCell(1, value); }
    public int Cell2 { get => _cells[2]; set => SetCell(2, value); }
    public int Cell3 { get => _cells[3]; set => SetCell(3, value); }
    /// <summary>The action byte: event A in bits 0..1, then B, C, D; byte n is sub-layer n (pressed on an R82HE, 2026-11-18).</summary>
    public byte Actions => (byte)(_cells[0] | _cells[1] << 2 | _cells[2] << 4 | _cells[3] << 6);

    void SetCell(int i, int v) { if (_cells[i] == v) return; _cells[i] = Math.Clamp(v, 0, 3); Raise($"Cell{i}"); }

    public void Load(UsageOption? key, byte actions)
    {
        Key = key;
        for (var i = 0; i < 4; i++) SetCell(i, actions >> 2 * i & 3);
    }
}
