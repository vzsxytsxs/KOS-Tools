using KosToolbox.Core.Peripherals;
using KosToolbox.Core.Peripherals.Hid;
using KosToolbox.Core.Peripherals.Razer;
using KosToolbox.Core.Peripherals.Royuan;

namespace KosGovernor.Core;

/// <summary>
/// Mouse settings for gamemode (gamemode-peripheral.json, chosen on the toolbox's Peripherals page), modelled on
/// <see cref="GamemodeGpuHost"/>. Gamemode on: reads what each listed device has, saves it, then writes the live DPI, polling rate, light
/// brightness (0 turns a mouse logo or a dock ring off) and light effect. Gamemode off: writes the saved values back. Mice and the dock:
/// volatile settings only, never onboard flash. A keyboard that stores every write (<see cref="PeripheralDriver.StoredOnly"/>, the R82HE)
/// gets its polling rate, lighting and rapid trigger written to flash, its whole lighting and switch columns saved first and written back
/// as they were. Only what differs is written; every write is read back by the driver.
/// </summary>
internal sealed class GamemodePeripheralHost
{
    public void Switch(bool on)
    {
        try { if (on) TurnOn(); else TurnOff(); }
        catch (Exception ex) { Log.Error($"Gamemode mouse settings ({(on ? "on" : "off")}) failed", ex); }
    }

    void TurnOn()
    {
        if (GamemodePeripheral.Load() is not { Devices.Count: > 0 } config) return;
        if (config.Suspended)
        {
            Log.Warn("Gamemode mouse settings skipped: writing them didn't finish last time (the PC may have crashed). Choose them again on the toolbox's Peripherals page.");
            return;
        }
        var drivers = Drivers();
        // A state left by a gamemode that didn't end still holds what the devices had before that one.
        var state = GamemodePeripheral.LoadState() is { Before.Count: > 0 } left ? left : new GamemodePeripheralState();
        if (state.Before.Count == 0)
            foreach (var e in config.Devices)
                if (Find(drivers, e) is { } d && Read(d) is { } s) state.Before.Add(Before(d, s, e));
        state.Applying = true;
        GamemodePeripheral.SaveState(state); // a crash while writing leaves Applying behind: suspended at the next start
        // Only what was read before is written: a value that couldn't be read couldn't be put back
        foreach (var e in config.Devices)
            if (state.Before.FirstOrDefault(b => b.VendorId == e.VendorId && b.ProductId == e.ProductId) is { } b)
                Write(drivers, new GamemodePeripheralEntry
                {
                    VendorId = e.VendorId, ProductId = e.ProductId, Name = e.Name,
                    Dpi = b.Dpi is null ? null : e.Dpi, PollingHz = b.PollingHz is null ? null : e.PollingHz,
                    Brightness = b.Brightness is null && b.KeyboardLight is null ? null : e.Brightness,
                    Effect = b.Effect is null && b.KeyboardLight is null ? null : e.Effect, Color = e.Color,
                    RapidTrigger = b.KeyboardSwitches is null ? null : e.RapidTrigger, RapidTriggerMm = e.RapidTriggerMm,
                }, $"gamemode values for {e.Name}");
            else Log.Warn($"Gamemode mouse: {e.Name} wasn't read before gamemode (not connected or asleep): left as it is.");
        state.Applying = false;
        GamemodePeripheral.SaveState(state);
    }

    void TurnOff()
    {
        if (GamemodePeripheral.LoadState() is not { } state) return;
        var drivers = Drivers();
        var all = state.Before.All(e => Write(drivers, e, $"the settings from before gamemode for {e.Name}"));
        // A device not there now (unplugged, asleep): the state stays and the next gamemode off tries again.
        if (all) GamemodePeripheral.SaveState(null);
    }

    /// <summary>Engine start: a gamemode the engine didn't end is ended now.</summary>
    public void RecoverAtStart()
    {
        try
        {
            if (GamemodePeripheral.LoadState() is not { } state) return;
            if (state.Applying && GamemodePeripheral.Load() is { } config)
            {
                config.Suspended = true;
                config.Save();
                Log.Error("Writing the gamemode mouse settings didn't finish (the PC may have crashed): they stay off until chosen again on the toolbox's Peripherals page.");
            }
            Log.Info("The last gamemode didn't end before the engine closed: putting the mouse settings back.");
            TurnOff();
        }
        catch (Exception ex) { Log.Error("Recovering the mouse settings from before gamemode failed", ex); }
    }

    static List<PeripheralDriver> Drivers() => HidDevices.List().Select(PeripheralRegistry.Claim).OfType<PeripheralDriver>().ToList();

    static PeripheralDriver? Find(List<PeripheralDriver> drivers, GamemodePeripheralEntry e) =>
        drivers.FirstOrDefault(d => d.Device.VendorId == e.VendorId && d.Device.ProductId == e.ProductId && !d.BatteryOnly);

    static PeripheralState? Read(PeripheralDriver d)
    {
        try { return d.Read() is { } s && s.Battery?.Asleep != true ? s : null; } // a dock reports no battery
        catch (Exception ex) when (ex is PeripheralException or System.ComponentModel.Win32Exception or IOException or TimeoutException) { return null; }
    }

    /// <summary>What the device has of each value gamemode will change: what gamemode off writes back.</summary>
    static GamemodePeripheralEntry Before(PeripheralDriver d, PeripheralState s, GamemodePeripheralEntry e)
    {
        var b = new GamemodePeripheralEntry { VendorId = e.VendorId, ProductId = e.ProductId, Name = e.Name,
            Dpi = e.Dpi is null ? null : s.Dpi?.X, PollingHz = e.PollingHz is null ? null : s.Polling?.Hz };
        if (d is RoyuanDriver r)
        {
            if (e.Brightness is not null || e.Effect is not null) b.KeyboardLight = r.StoredLight;
            if (e.RapidTrigger is not null) b.KeyboardSwitches = r.Columns;
            return b;
        }
        b.Brightness = e.Brightness is null ? null : LiveBrightness(d);
        if (e.Effect is not null && LiveLighting(d) is { Effect: not LightingEffect.Other } l) (b.Effect, b.Color) = (l.Effect, l.Color);
        return b;
    }

    static PeripheralLighting? LiveLighting(PeripheralDriver d)
    {
        try { return d is RazerDriver r ? r.ReadLiveLighting() : null; }
        catch (Exception ex) when (ex is PeripheralException or System.ComponentModel.Win32Exception or IOException or TimeoutException) { return null; }
    }

    static int? LiveBrightness(PeripheralDriver d)
    {
        try { return d.HasLiveBrightness ? d.ReadLiveBrightness() : null; }
        catch (Exception ex) when (ex is PeripheralException or System.ComponentModel.Win32Exception or IOException or TimeoutException) { return null; }
    }

    /// <summary>
    /// The keyboard (every write goes to flash, so only what differs): the saved switch columns or lighting as they were (gamemode off),
    /// else rapid trigger, the effect (lit: a brightness of 0 goes to 100 % with it) or the brightness alone.
    /// </summary>
    static void WriteKeyboard(RoyuanDriver r, PeripheralState now, GamemodePeripheralEntry e)
    {
        if (e.KeyboardSwitches is { } columns) r.WriteColumns(columns);
        else if (e.RapidTrigger is { } rt) r.SetRapidTrigger(rt, e.RapidTriggerMm ?? 0.1);
        if (e.KeyboardLight is { } light) { if (r.StoredLight != light) r.SetLight(light); }
        else if (e.Effect is { } fx)
        {
            if (now.Brightness is not > 0 || now.Lighting is not { } l || l.Effect != fx || fx != LightingEffect.Spectrum && l.Color != e.Color)
                r.SetLightingLit(fx, e.Color ?? 0xFFFFFF);
        }
        else if (e.Brightness is { } br && now.Brightness != br) r.SetBrightness(br);
    }

    /// <summary>Reads the device (getters, which also confirm its transaction id), then writes only what differs. True when done or nothing to do.</summary>
    static bool Write(List<PeripheralDriver> drivers, GamemodePeripheralEntry e, string what)
    {
        if (Find(drivers, e) is not { } d || Read(d) is not { } now)
        {
            Log.Warn($"Gamemode mouse: {what}: the device isn't connected or didn't answer.");
            return false;
        }
        try
        {
            if (e.Dpi is { } dpi && now.Dpi?.X != dpi && d.HasLiveDpi && d.Writable.Contains(PeripheralSetting.Dpi)) d.SetDpiLive(dpi, dpi);
            if (e.PollingHz is { } hz && now.Polling?.Hz != hz && d.Writable.Contains(PeripheralSetting.PollingRate)) d.SetPollingRate(hz);
            if (d is RoyuanDriver r) WriteKeyboard(r, now, e);
            else
            {
                if (e.Effect is { } fx && d is RazerDriver rz && LiveLighting(d) is var live
                    && !(live?.Effect == fx && (fx is LightingEffect.Off or LightingEffect.Spectrum || live.Color == e.Color)))
                    rz.SetLightingLive(fx, e.Color ?? 0xFFFFFF);
                if (e.Brightness is { } br && d.HasLiveBrightness && LiveBrightness(d) != br) d.SetBrightnessLive(br);
            }
            Log.Info($"Gamemode mouse: {what} written and read back.");
            return true;
        }
        catch (Exception ex) when (ex is PeripheralException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception or IOException or TimeoutException)
        {
            Log.Warn($"Gamemode mouse: writing {what} failed: {ex.Message}");
            return false;
        }
    }
}

/// <summary>
/// Device batteries for the tray: on its own background thread (a wireless read can take seconds), every 5 minutes, every minute while a
/// device is asleep or the dock shows the battery. Getters only, except the dock: with <see cref="GamemodePeripheral.DockBattery"/> on, a
/// Razer charging dock gets the Razer mouse's battery as its live color (red to green, breathing while charging; nothing stored).
/// A balloon at or under 15 %, not charging, at most once an hour per device.
/// </summary>
internal sealed class PeripheralBatteryMonitor
{
    const int Threshold = 15;
    static readonly TimeSpan Normal = TimeSpan.FromMinutes(5), Asleep = TimeSpan.FromMinutes(1), Cooldown = TimeSpan.FromHours(1);
    readonly Dictionary<string, PeripheralDriver> _drivers = new();
    readonly Dictionary<string, DateTime> _warned = new();
    readonly Action<string, string> _balloon;
    readonly Action _changed;
    readonly Dictionary<string, TrayDevice> _shown = new();
    readonly ManualResetEvent _stop = new(false);
    readonly AutoResetEvent _poke = new(false);
    Thread? _thread;

    /// <summary>A config change (the toolbox saved): scan now rather than at the next interval.</summary>
    public void Poke() => _poke.Set();

    public PeripheralBatteryMonitor(Action<string, string> balloon, Action changed) => (_balloon, _changed) = (balloon, changed);

    /// <summary>The tray tooltip's line ("Viper Ultimate 72 %"), empty when nothing reports a battery.</summary>
    public static volatile string Line = "";

    /// <summary>One tray icon per device with a battery; a device asleep keeps the last reading it gave.</summary>
    /// <param name="Id">VID and PID: the icon's id, which Windows keys "show in the taskbar" on, so the user's choice sticks.</param>
    public sealed record TrayDevice(string Key, uint Id, string Name, TrayGlyph Glyph, int Percent, bool Charging);
    public static volatile IReadOnlyList<TrayDevice> Devices = Array.Empty<TrayDevice>();

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Peripheral batteries", Priority = System.Threading.ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _stop.Set();
        _thread?.Join(2000);
    }

    void Loop()
    {
        while (true)
        {
            var asleep = false;
            try { asleep = Scan(); }
            catch (Exception ex) { Log.Warn($"Reading device batteries failed: {ex.Message}"); }
            if (WaitHandle.WaitAny(new WaitHandle[] { _stop, _poke }, asleep ? Asleep : Normal) == 0) return;
        }
    }

    bool Scan()
    {
        var present = HidDevices.List();
        foreach (var gone in _drivers.Keys.Except(present.Select(d => d.Key)).ToList()) { _drivers.Remove(gone); _shown.Remove(gone); }
        IReadOnlyCollection<uint> hidden;
        try { hidden = GamemodePeripheral.Load()?.TrayHidden ?? new List<uint>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { hidden = Array.Empty<uint>(); }
        var parts = new List<string>();
        var asleep = false;
        RazerDriver? dock = null;
        PeripheralBattery? mouse = null;
        foreach (var device in present)
        {
            if (!_drivers.TryGetValue(device.Key, out var driver))
            {
                if (PeripheralRegistry.Claim(device) is not { } claimed) continue;
                _drivers[device.Key] = driver = claimed; // kept: a Razer driver remembers the transaction id that answered
            }
            if (driver is RazerDriver { IsDock: true } d) { dock = d; continue; }
            PeripheralBattery? b;
            try { b = driver.ReadBattery(); }
            catch (Exception ex) when (ex is PeripheralException or System.ComponentModel.Win32Exception or IOException or TimeoutException or InvalidOperationException) { continue; }
            if (b is null) continue;
            var id = (uint)device.VendorId << 16 | device.ProductId;
            var hide = hidden.Contains(id); // turned off on the toolbox: no tray icon (the tooltip line and the low-battery balloon stay)
            if (hide) _shown.Remove(device.Key);
            var name = Short(driver.Model ?? device.Product ?? "Device");
            if (b.Asleep) { asleep = true; continue; }
            if (b.Percent is not { } p) continue;
            if (driver is RazerDriver) mouse = b;
            if (!hide) _shown[device.Key] = new TrayDevice(device.Key, id, driver.Model ?? device.Product ?? "Device", Glyph(driver.Kind), p, b.Charging == true);
            parts.Add($"{name} {p} %{(b.Charging == true ? "+" : "")}");
            if (p <= Threshold && b.Charging != true && (!_warned.TryGetValue(device.Key, out var at) || DateTime.UtcNow - at > Cooldown))
            {
                _warned[device.Key] = DateTime.UtcNow;
                _balloon("Low battery", $"{name}: {p} %. Charge it soon.");
            }
        }
        Line = string.Join(", ", parts);
        var devices = _shown.Values.OrderBy(d => d.Key, StringComparer.Ordinal).ToList();
        if (!devices.SequenceEqual(Devices)) { Devices = devices; _changed(); }
        return ShowOnDock(dock, mouse) || asleep;
    }

    static TrayGlyph Glyph(PeripheralKind kind) => kind switch
    {
        PeripheralKind.Mouse => TrayGlyph.Mouse, PeripheralKind.Headset => TrayGlyph.Headset, PeripheralKind.Keyboard => TrayGlyph.Keyboard,
        PeripheralKind.Controller => TrayGlyph.Controller, _ => TrayGlyph.Other,
    };

    /// <summary>
    /// The mouse battery as the dock's live color, written only when the ring shows something else (read each time: a replugged dock or
    /// an effect applied on the toolbox is corrected too). True while the option is on and a dock is there.
    /// </summary>
    bool ShowOnDock(RazerDriver? dock, PeripheralBattery? mouse)
    {
        if (dock is null) { _dockShown = null; return false; }
        bool on;
        try
        {
            var config = GamemodePeripheral.Load();
            on = config?.DockBattery == true;
            // gamemode: the dock's profile can turn the battery colors on or off for the session
            if (Gamemode && config?.Devices.FirstOrDefault(e => e.VendorId == dock.Device.VendorId && e.ProductId == dock.Device.ProductId)?.DockBattery is { } g) on = g;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return false; }
        if (!on)
        {
            if (_dockShown != false) RestoreDock(dock);
            _dockShown = false;
            return false;
        }
        _dockShown = true;
        if (mouse?.Percent is not { } p) return true; // asleep or not there: the dock keeps the last color
        var want = new PeripheralLighting(mouse.Charging == true ? LightingEffect.Breathing : LightingEffect.Static, DockBatteryColor.For(p));
        try
        {
            if (dock.ReadLiveLighting() == want) return true;
            if (dock.TransactionId is null) dock.Read(); // the dock reports no battery, so nothing has resolved its id yet
            dock.SetLightingLive(want.Effect, want.Color!.Value);
            Log.Info($"Dock: mouse battery {p} %{(mouse.Charging == true ? ", charging" : "")} shown as #{want.Color:X6}.");
        }
        catch (Exception ex) when (ex is PeripheralException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception or IOException or TimeoutException)
        {
            Log.Warn($"Dock: showing the mouse battery failed: {ex.Message}");
        }
        return true;
    }

    /// <summary>Gamemode is on (set by the engine before it pokes the loop).</summary>
    public volatile bool Gamemode;
    /// <summary>Whether the ring shows the battery: null = not known yet (engine start, a dock plugged in).</summary>
    bool? _dockShown;

    /// <summary>The battery colors stop: the ring gets back the effect stored in the dock (live only), when it shows something else.</summary>
    static void RestoreDock(RazerDriver dock)
    {
        try
        {
            if (dock.Read().Lighting is not { Effect: not LightingEffect.Other } stored || dock.ReadLiveLighting() == stored) return;
            dock.SetLightingLive(stored.Effect, stored.Color ?? 0);
            Log.Info($"Dock: battery colors off, the ring's own effect ({stored.Effect}) is back.");
        }
        catch (Exception ex) when (ex is PeripheralException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception or IOException or TimeoutException)
        {
            Log.Warn($"Dock: putting back the ring's own effect failed: {ex.Message}");
        }
    }

    static string Short(string name) => name.StartsWith("Razer ", StringComparison.Ordinal) || name.StartsWith("Logitech ", StringComparison.Ordinal) ? name[(name.IndexOf(' ') + 1)..] : name;
}
