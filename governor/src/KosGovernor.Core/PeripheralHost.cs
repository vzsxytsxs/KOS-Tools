using KosToolbox.Core.Peripherals;
using KosToolbox.Core.Peripherals.Hid;
using KosToolbox.Core.Peripherals.Razer;

namespace KosGovernor.Core;

/// <summary>
/// Mouse settings for gamemode (gamemode-peripheral.json, chosen on the toolbox's Peripherals page), modelled on
/// <see cref="GamemodeGpuHost"/>. Gamemode on: reads what each listed device has, saves it, then writes the live DPI and polling rate.
/// Gamemode off: writes the saved values back. Volatile settings only, never onboard flash; every write is read back by the driver.
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
                if (Find(drivers, e) is { } d && Read(d) is { } s)
                    state.Before.Add(new GamemodePeripheralEntry { VendorId = e.VendorId, ProductId = e.ProductId, Name = e.Name,
                        Dpi = e.Dpi is null ? null : s.Dpi?.X, PollingHz = e.PollingHz is null ? null : s.Polling?.Hz });
        state.Applying = true;
        GamemodePeripheral.SaveState(state); // a crash while writing leaves Applying behind: suspended at the next start
        foreach (var e in config.Devices) Write(drivers, e, $"gamemode values for {e.Name}");
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
        try { return d.Read() is { Battery.Asleep: not true } s ? s : null; }
        catch (Exception ex) when (ex is PeripheralException or System.ComponentModel.Win32Exception or IOException or TimeoutException) { return null; }
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
    readonly ManualResetEvent _stop = new(false);
    readonly AutoResetEvent _poke = new(false);
    PeripheralLighting? _dockShown;
    Thread? _thread;

    /// <summary>A config change (the toolbox saved): scan now rather than at the next interval.</summary>
    public void Poke() => _poke.Set();

    public PeripheralBatteryMonitor(Action<string, string> balloon) => _balloon = balloon;

    /// <summary>The tray tooltip's line ("Viper Ultimate 72 %"), empty when nothing reports a battery.</summary>
    public static volatile string Line = "";

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
        foreach (var gone in _drivers.Keys.Except(present.Select(d => d.Key)).ToList()) _drivers.Remove(gone);
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
            var name = Short(driver.Model ?? device.Product ?? "Device");
            if (b.Asleep) { asleep = true; continue; }
            if (b.Percent is not { } p) continue;
            if (driver is RazerDriver) mouse = b;
            parts.Add($"{name} {p} %{(b.Charging == true ? "+" : "")}");
            if (p <= Threshold && b.Charging != true && (!_warned.TryGetValue(device.Key, out var at) || DateTime.UtcNow - at > Cooldown))
            {
                _warned[device.Key] = DateTime.UtcNow;
                _balloon("Low battery", $"{name}: {p} %. Charge it soon.");
            }
        }
        Line = string.Join(", ", parts);
        return ShowOnDock(dock, mouse) || asleep;
    }

    /// <summary>The mouse battery as the dock's live color, written only when it changes. True while the option is on and a dock is there.</summary>
    bool ShowOnDock(RazerDriver? dock, PeripheralBattery? mouse)
    {
        if (dock is null) { _dockShown = null; return false; }
        bool on;
        try { on = GamemodePeripheral.Load()?.DockBattery == true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return false; }
        if (!on) { _dockShown = null; return false; }
        if (mouse?.Percent is not { } p) return true; // asleep or not there: the dock keeps the last color
        var want = new PeripheralLighting(mouse.Charging == true ? LightingEffect.Breathing : LightingEffect.Static, DockBatteryColor.For(p));
        if (want == _dockShown) return true;
        try
        {
            if (dock.TransactionId is null) dock.Read(); // the dock reports no battery, so nothing has resolved its id yet
            dock.SetLightingLive(want.Effect, want.Color!.Value);
            _dockShown = want;
            Log.Info($"Dock: mouse battery {p} %{(mouse.Charging == true ? ", charging" : "")} shown as #{want.Color:X6}.");
        }
        catch (Exception ex) when (ex is PeripheralException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception or IOException or TimeoutException)
        {
            Log.Warn($"Dock: showing the mouse battery failed: {ex.Message}");
        }
        return true;
    }

    static string Short(string name) => name.StartsWith("Razer ", StringComparison.Ordinal) || name.StartsWith("Logitech ", StringComparison.Ordinal) ? name[(name.IndexOf(' ') + 1)..] : name;
}
