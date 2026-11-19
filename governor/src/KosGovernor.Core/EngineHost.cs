using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace KosGovernor.Core;

/// <summary>
/// The engine process (KOS Engine: KosEngine.exe, Native AOT; or the governor's exe with "--engine" when the engine exe is
/// missing): tray icon, status window, global gamemode hotkey and the <see cref="Engine"/> loop on its own thread. One
/// instance per session; ends through the Exit menu or the stop event.
/// </summary>
public static class EngineHost
{
    public static int Run()
    {
        using var mutex = new Mutex(true, Names.EngineMutex, out var created);
        if (!created)
        {
            // Already running: opening the engine again shows its status window.
            try { using var show = EventWaitHandle.OpenExisting(Names.ShowStatus); show.Set(); }
            catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException) { }
            return 0;
        }
        // SeProfileSingleProcess: clear the standby list (gamemode); SeIncreaseQuota: empty the working set of other processes.
        TaskMeter.MarkBase();
        Native.EnablePrivileges("SeDebugPrivilege", "SeIncreaseBasePriorityPrivilege", "SeProfileSingleProcessPrivilege", "SeIncreaseQuotaPrivilege");
        GamemodeSystem.RecoverAtStart();
        EtwDriverTrace.StopSession(); // a thread director trace left by a crash
        RuleExtras.RecoverAtStart();
        Log.Prune();
        Log.Info($"Engine {typeof(EngineHost).Assembly.GetName().Version} started.");
        SignIn.MigrateOld();
        using var stop = new EventWaitHandle(false, EventResetMode.AutoReset, Names.Stop);
        using var reload = new EventWaitHandle(false, EventResetMode.AutoReset, Names.Reload);
        using var toggle = new EventWaitHandle(false, EventResetMode.AutoReset, Names.ToggleGamemode);
        using var showStatus = new EventWaitHandle(false, EventResetMode.AutoReset, Names.ShowStatus);
        // A signal left from an engine that already exited (it sets Stop again on its way out, and the event outlives it
        // while another process holds a handle) isn't meant for this one.
        stop.Reset();
        toggle.Reset();
        var engine = new Engine();
        var fans = new FanCurveHost();
        var gpu = new GamemodeGpuHost(fans);
        gpu.RecoverAtStart();
        engine.GamemodeSwitched += on => { using (TaskMeter.Measure(EngineTask.GpuProfile)) gpu.Switch(on); };
        var mice = new GamemodePeripheralHost(); // peripherals: gamemode mouse settings
        mice.RecoverAtStart();
        engine.GamemodeSwitched += on => { using (TaskMeter.Measure(EngineTask.Peripherals)) mice.Switch(on); };
        var recovery = new GpuRecovery();
        Tray? shown = null;
        using var firewall = new FirewallWatch(a => shown?.Ask(a), (title, text) => shown?.Balloon(title, text, warning: true));
        using var tray = new Tray(engine, stop, sample => EngineStatus.Build(Facts(engine, fans, firewall), sample), firewall.Answer);
        shown = tray;
        engine.ConfigChanged += tray.Refresh;
        engine.SessionEnded += report => tray.Balloon("Gamemode session", $"{report.Summary}. Report on the toolbox's Stability page.",
            report.Sections.Any(s => s.Lines.Any(l => l.Warning)));
        var batteries = new PeripheralBatteryMonitor((title, text) => tray.Balloon(title, text, warning: true), tray.Refresh); // peripherals: tray batteries
        batteries.Start();
        engine.GamemodeSwitched += on => { batteries.Gamemode = on; batteries.Poke(); }; // after mice.Switch: the dock's gamemode battery choice

        var worker = new Thread(() =>
        {
            var handles = new WaitHandle[] { stop, reload, toggle, engine.Wake, showStatus, firewall.Wake };
            var trimmed = false;
            while (true)
            {
                try { using (TaskMeter.Measure(EngineTask.Rules)) engine.Tick(); }
                catch (Exception ex) { Log.Error("Engine tick failed", ex); }
                try { using (TaskMeter.Measure(EngineTask.FanCurve)) fans.Sync(); }
                catch (Exception ex) { Log.Error("Fan curve check failed", ex); }
                try { using (TaskMeter.Measure(EngineTask.GpuProfile)) recovery.Tick(fans); }
                catch (Exception ex) { Log.Error("Checking for a display driver reset failed", ex); }
                SyncFirewall();
                tray.Refresh();
                // Once, after the first cycle: the pages used only at startup (config, game list, the ~12 MB that
                // NVML zeroes on init) leave the working set; the ones used again come back on their own.
                if (!trimmed) trimmed = EmptyWorkingSet((IntPtr)(-1));
                // A blocked connection wakes only the firewall's part; the rest keeps its interval.
                var due = Environment.TickCount64 + Math.Clamp(engine.Config.General.EngineIntervalMs, Validation.MinEngineMs, Validation.MaxEngineMs);
                int i;
                while ((i = WaitHandle.WaitAny(handles, (int)Math.Max(0, due - Environment.TickCount64))) == 5) SyncFirewall();
                if (i == 0) break;
                if (i == 1) batteries.Poke(); // the toolbox saved: the dock battery option may have changed
                if (i == 2) engine.RequestToggle();
                if (i == 4) tray.ShowStatus();
            }
            engine.Shutdown();
            if (engine.Gamemode) gpu.Switch(false);
            if (engine.Gamemode) mice.Switch(false);
            batteries.Stop();
            fans.Shutdown();
            firewall.Dispose();
            tray.Close();

            void SyncFirewall()
            {
                try { using (TaskMeter.Measure(EngineTask.Firewall)) firewall.Sync(); }
                catch (Exception ex) { Log.Error("KOS Firewall check failed", ex); }
            }
        }) { IsBackground = true, Name = "KOS engine" };
        worker.Start();
        tray.Run();
        stop.Set();
        worker.Join(5000);
        Log.Info("Engine stopped.");
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr process); // -1 = the process itself

    /// <summary>
    /// "KosEngine.exe --firewall-off", as administrator: removes every KOS Firewall filter and turns back on the Windows Firewall
    /// profiles it turned off, without the toolbox (the way back when the network is gone). Says what it did in a message box.
    /// </summary>
    public static int FirewallOff()
    {
        const string Caption = "KOS Engine";
        try
        {
            var removed = KosToolbox.Core.Firewall.KosFirewall.TurnOff(restoreWindows: true);
            Log.Info($"KOS Firewall turned off from the command line: {removed} filter(s) removed.");
            NativeUi.MessageBox(IntPtr.Zero, $"KOS Firewall is off: {removed} filter{(removed == 1 ? "" : "s")} removed. Windows Firewall is back as it was before KOS Firewall was turned on.", Caption, 0x40); // MB_ICONINFORMATION
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Turning KOS Firewall off from the command line failed", ex);
            NativeUi.MessageBox(IntPtr.Zero, $"KOS Firewall couldn't be turned off: {ex.Message}", Caption, 0x10); // MB_ICONERROR
            return 1;
        }
    }

    /// <summary>What the status window's lines are built from; the files are read only while it is open.</summary>
    static EngineFacts Facts(Engine engine, FanCurveHost fans, FirewallWatch firewall)
    {
        static T? Try<T>(Func<T?> read) where T : class
        {
            try { return read(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
        }
        var peripherals = Try(KosToolbox.Core.Peripherals.GamemodePeripheral.Load);
        return new EngineFacts
        {
            Profile = engine.Config.Active.Name, Rules = engine.RuleCount, Gamemode = engine.Gamemode, GamemodeGame = engine.GamemodeGame,
            AutoGamemode = engine.Config.AutoGamemode.Enabled, FanCurveRunning = fans.Running,
            GpuProfileChosen = Try(KosToolbox.Core.Gpu.GamemodeGpu.Load)?.On is not null,
            GpuProfileApplied = Try(KosToolbox.Core.Gpu.GamemodeGpu.LoadState) is not null,
            Batteries = PeripheralBatteryMonitor.Devices.Count,
            PeripheralProfiles = peripherals is { Suspended: false } ? peripherals.Devices.Count : 0,
            PeripheralProfilesApplied = Try(KosToolbox.Core.Peripherals.GamemodePeripheral.LoadState) is not null,
            TimerHeld = TimerResolution.Held, Overlay = engine.OverlayOn,
            FirewallOn = firewall.On, FirewallFilters = firewall.Filters, FirewallBlocked = firewall.Blocked, FirewallPopups = firewall.Popups,
        };
    }

    /// <summary>
    /// A KOS executable: next to this one, in its own folder beside this one's (installed: KOS\engine, KOS\governor, KOS\toolbox), or
    /// in that app's publish folder of the repository. Null when it isn't there.
    /// </summary>
    static string? Find(string folder, string exe) => new[]
    {
        Path.Combine(AppContext.BaseDirectory, exe),
        Path.Combine(AppContext.BaseDirectory, "..", folder, exe),
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", folder, "publish", "win-x64", exe),
    }.Select(Path.GetFullPath).FirstOrDefault(File.Exists);

    /// <summary>The engine's own Native AOT executable.</summary>
    public static string? EnginePath => Find("engine", "KosEngine.exe");
    public static string? UiPath => Find("governor", "KosGovernor.exe");
    public static string? ToolboxPath => Find("toolbox", "KosToolbox.exe");

    /// <summary>
    /// Starts the engine (already elevated) if it isn't running. Without the engine executable next to it (the interface run
    /// from its own build folder), the interface's executable runs the engine itself with "--engine".
    /// </summary>
    public static void EnsureRunning()
    {
        if (Names.EngineRunning) return;
        var start = EnginePath is { } engine ? new ProcessStartInfo(engine) : new ProcessStartInfo(Environment.ProcessPath!, "--engine");
        start.UseShellExecute = false;
        Process.Start(start)?.Dispose();
    }
}

/// <summary>
/// Invisible window with the tray icon and the global hotkey (Windows API, no WPF: keeps the engine light). A click on the icon opens
/// the <see cref="StatusWindow"/>; the menu opens KOS Toolbox and KOS Governor.
/// </summary>
internal sealed class Tray : IDisposable
{
    const uint WmNull = 0, WmDestroy = 0x2, WmClose = 0x10, WmContextMenu = 0x7B, WmHotkey = 0x312, WmApp = 0x8000;
    const uint WmTray = WmApp + 1, WmRefresh = WmApp + 2, WmStatus = WmApp + 3, WmFirewall = WmApp + 4;
    const uint WmLButtonUp = 0x202, WmLButtonDblClk = 0x203, WmRButtonUp = 0x205;
    const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NifMessage = 1, NifIcon = 2, NifTip = 4, NifInfo = 0x10, NiifInfo = 1, NiifWarning = 2;
    const uint MfString = 0, MfChecked = 8, MfPopup = 0x10, MfSeparator = 0x800;
    const int CmdOpen = 1, CmdGamemode = 2, CmdExit = 3, CmdToolbox = 4, CmdStatus = 5, CmdProfile = 100;

    readonly Engine _engine;
    readonly EventWaitHandle _stop;
    readonly WndProc _proc;
    readonly uint _taskbarCreated;
    readonly StatusWindow _status;
    readonly FirewallPopup _popup;
    readonly System.Collections.Concurrent.ConcurrentQueue<FirewallAsk> _asks = new();
    IntPtr _hwnd, _icon;
    bool _hotkey;

    public Tray(Engine engine, EventWaitHandle stop, Func<TaskMeter.Sample, StatusView> status, Action<FirewallAsk, bool?> firewallAnswer)
    {
        _popup = new FirewallPopup(firewallAnswer);
        _engine = engine;
        _stop = stop;
        _proc = Proc; // reference kept: Windows calls the delegate while the window exists
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        var hInstance = GetModuleHandle(null);
        var cls = new WndClassEx { cbSize = Marshal.SizeOf<WndClassEx>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), hInstance = hInstance, lpszClassName = "KosEngine" };
        RegisterClassEx(ref cls);
        _hwnd = CreateWindowEx(0, cls.lpszClassName, "KOS Engine", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        // The engine is elevated and Explorer isn't: without this, UIPI blocks clicks on the icon and the taskbar-recreated notice.
        ChangeWindowMessageFilterEx(_hwnd, WmTray, 1, IntPtr.Zero);
        ChangeWindowMessageFilterEx(_hwnd, _taskbarCreated, 1, IntPtr.Zero);
        DarkMenus(_hwnd);
        var size = GetSystemMetrics(49); // SM_CXSMICON, already at the monitor's scale
        _icon = LoadImage(hInstance, (IntPtr)32512, 1, size, size, 0); // the .exe's icon (IDI_APPLICATION), at tray size
        if (_icon == IntPtr.Zero)
            ExtractIconEx(Environment.ProcessPath!, 0, IntPtr.Zero, out _icon, 1);
        _status = new StatusWindow(status, _icon);
        AddIcon();
        RegisterHotkey();
    }

    public void Run()
    {
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            using var measured = TaskMeter.Measure(EngineTask.Tray); // the wait above isn't work
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    public void Refresh() => PostMessage(_hwnd, WmRefresh, IntPtr.Zero, IntPtr.Zero);
    /// <summary>Opens the status window (any thread).</summary>
    public void ShowStatus() => PostMessage(_hwnd, WmStatus, IntPtr.Zero, IntPtr.Zero);

    /// <summary>A KOS Firewall question for the popup (any thread).</summary>
    public void Ask(FirewallAsk ask)
    {
        _asks.Enqueue(ask);
        PostMessage(_hwnd, WmFirewall, IntPtr.Zero, IntPtr.Zero);
    }

    // ---------- one icon per device battery (TrayBatteryIcon), its id from the VID and PID ----------
    readonly Dictionary<string, (uint Id, IntPtr Icon, PeripheralBatteryMonitor.TrayDevice Shown)> _devices = new();

    void SyncDevices()
    {
        var now = PeripheralBatteryMonitor.Devices;
        var size = GetSystemMetrics(49); // SM_CXSMICON
        foreach (var key in _devices.Keys.Except(now.Select(d => d.Key)).ToList())
        {
            DeviceIcon(NimDelete, _devices[key].Id, IntPtr.Zero, "");
            DestroyIcon(_devices[key].Icon);
            _devices.Remove(key);
        }
        foreach (var d in now)
        {
            var known = _devices.TryGetValue(d.Key, out var had);
            if (known && had.Shown == d) continue;
            var icon = TrayBatteryIcon.ToIcon(TrayBatteryIcon.Render(size, d.Percent, d.Glyph, d.Charging), size);
            if (icon == IntPtr.Zero) continue;
            var id = known ? had.Id : d.Id;
            while (!known && _devices.Values.Any(v => v.Id == id)) id++; // two of the same model
            DeviceIcon(known ? NimModify : NimAdd, id, icon, $"{d.Name} · {d.Percent} %{(d.Charging ? ", charging" : "")}");
            if (known) DestroyIcon(had.Icon);
            _devices[d.Key] = (id, icon, d);
        }
    }

    void DeviceIcon(uint message, uint id, IntPtr icon, string tip)
    {
        var data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(), hWnd = _hwnd, uID = id, uFlags = message == NimDelete ? 0 : NifMessage | NifIcon | NifTip,
            uCallbackMessage = WmTray, hIcon = icon, szTip = tip.Length > 127 ? tip[..127] : tip, szInfo = "", szInfoTitle = "",
        };
        Shell_NotifyIcon(message, ref data);
    }

    /// <summary>Explorer restarted: every device icon is added again.</summary>
    void ReAddDevices()
    {
        foreach (var d in _devices.Values) DestroyIcon(d.Icon);
        _devices.Clear();
        SyncDevices();
    }
    public void Close() => PostMessage(_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);

    IntPtr Proc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        if (msg == _taskbarCreated && msg != 0) { AddIcon(); ReAddDevices(); return IntPtr.Zero; }
        switch (msg)
        {
            case WmTray:
                var e = (uint)(l.ToInt64() & 0xFFFF);
                if (e is WmLButtonDblClk or WmLButtonUp) _status.Show();
                else if (e is WmRButtonUp or WmContextMenu) ShowMenu();
                return IntPtr.Zero;
            case WmHotkey:
                _engine.RequestToggle();
                return IntPtr.Zero;
            case WmStatus:
                _status.Show();
                return IntPtr.Zero;
            case WmFirewall:
                while (_asks.TryDequeue(out var ask)) _popup.Add(ask);
                return IntPtr.Zero;
            case WmRefresh:
                RegisterHotkey();
                Notify(NimModify);
                SyncDevices();
                return IntPtr.Zero;
            case WmClose:
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WmDestroy:
                _status.Close();
                _popup.Close();
                Notify(NimDelete);
                foreach (var d in _devices.Values) { DeviceIcon(NimDelete, d.Id, IntPtr.Zero, ""); DestroyIcon(d.Icon); }
                _devices.Clear();
                if (_hotkey) UnregisterHotKey(hwnd, 1);
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, w, l);
    }

    uint _hotkeyMods, _hotkeyKey;

    void RegisterHotkey()
    {
        var hk = _engine.Config.Hotkey;
        if (_hotkey && hk.Modifiers == _hotkeyMods && hk.Key == _hotkeyKey) return;
        if (_hotkey) UnregisterHotKey(_hwnd, 1);
        (_hotkeyMods, _hotkeyKey) = (hk.Modifiers, hk.Key);
        _hotkey = RegisterHotKey(_hwnd, 1, hk.Modifiers | 0x4000, hk.Key); // MOD_NOREPEAT
        if (!_hotkey) Log.Warn($"The gamemode hotkey couldn't be registered (another program may use it): {Native.Win32Message(Marshal.GetLastWin32Error())}");
    }

    /// <summary>A Windows notification from the tray icon (any thread).</summary>
    public void Balloon(string title, string text, bool warning)
    {
        var data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(), hWnd = _hwnd, uID = 1, uFlags = NifInfo, szTip = "",
            szInfo = text.Length > 255 ? text[..254] + "…" : text, szInfoTitle = title, dwInfoFlags = warning ? NiifWarning : NiifInfo,
        };
        Shell_NotifyIcon(NimModify, ref data);
    }

    void AddIcon()
    {
        Notify(NimAdd);
    }

    void Notify(uint message)
    {
        var data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(), hWnd = _hwnd, uID = 1, uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = WmTray, hIcon = _icon, szTip = Tip(), szInfo = "", szInfoTitle = "",
        };
        Shell_NotifyIcon(message, ref data);
    }

    /// <summary>The engine's tooltip, then the device batteries when there are any (a tooltip holds 127 characters).</summary>
    string Tip() => (PeripheralBatteryMonitor.Line is { Length: > 0 } line ? _engine.Tooltip + "\n" + line : _engine.Tooltip) is var t && t.Length > 127 ? t[..127] : t;

    void ShowMenu()
    {
        var menu = CreatePopupMenu();
        var profiles = CreatePopupMenu();
        var config = _engine.Config;
        AppendMenu(menu, MfString, CmdStatus, "Status");
        AppendMenu(menu, MfSeparator, 0, null);
        if (EngineHost.ToolboxPath is not null) AppendMenu(menu, MfString, CmdToolbox, "Open KOS Toolbox");
        if (EngineHost.UiPath is not null) AppendMenu(menu, MfString, CmdOpen, "Open KOS Governor");
        AppendMenu(menu, MfSeparator, 0, null);
        // The text after the \t is shown right-aligned (the hotkey).
        AppendMenu(menu, MfString | (_engine.Gamemode ? MfChecked : 0), CmdGamemode, $"Gamemode\t{HotkeyText(config.Hotkey)}");
        for (var i = 0; i < config.Profiles.Count; i++)
            AppendMenu(profiles, MfString | (i == config.ActiveProfile ? MfChecked : 0), CmdProfile + i, config.Profiles[i].Name);
        AppendMenu(menu, MfPopup, profiles, "Profile");
        AppendMenu(menu, MfSeparator, 0, null);
        AppendMenu(menu, MfString, CmdExit, "Exit");
        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd);
        var cmd = TrackPopupMenuEx(menu, 0x0100 | 0x0002, pt.X, pt.Y, _hwnd, IntPtr.Zero); // TPM_RETURNCMD | TPM_RIGHTBUTTON
        PostMessage(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);
        switch (cmd)
        {
            case CmdOpen: Open(EngineHost.UiPath); break;
            case CmdToolbox: Open(EngineHost.ToolboxPath); break;
            case CmdStatus: _status.Show(); break;
            case CmdGamemode: _engine.RequestToggle(); break;
            case CmdExit: _stop.Set(); break;
            case >= CmdProfile: SwitchProfile(cmd - CmdProfile); break;
        }
    }

    static string HotkeyText(Hotkey h)
    {
        var parts = new List<string>();
        if ((h.Modifiers & Hotkey.Ctrl) != 0) parts.Add("Ctrl");
        if ((h.Modifiers & Hotkey.Alt) != 0) parts.Add("Alt");
        if ((h.Modifiers & Hotkey.Shift) != 0) parts.Add("Shift");
        if ((h.Modifiers & Hotkey.Win) != 0) parts.Add("Win");
        var name = new StringBuilder(64);
        parts.Add(GetKeyNameText((int)(MapVirtualKey(h.Key, 0) << 16), name, name.Capacity) > 0 ? name.ToString() : $"0x{h.Key:X2}");
        return string.Join("+", parts);
    }

    /// <summary>
    /// Dark menu (like Explorer's in dark mode), always, even with Windows on the light theme. The uxtheme API
    /// has no public name, only an ordinal (Windows 10 1903+); if it's missing, the default menu stays.
    /// </summary>
    static void DarkMenus(IntPtr hwnd)
    {
        try
        {
            SetPreferredAppMode(2); // ForceDark
            AllowDarkModeForWindow(hwnd, true);
            FlushMenuThemes();
        }
        catch (EntryPointNotFoundException) { }
    }

    static void SwitchProfile(int index)
    {
        try
        {
            var config = ConfigStore.Load();
            if (index < 0 || index >= config.Profiles.Count) return;
            config.ActiveProfile = index;
            ConfigStore.Save(config);
        }
        catch (Exception ex) { Log.Error("Switching the profile from the tray failed", ex); }
    }

    static void Open(string? exe)
    {
        if (exe is null) return;
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false })?.Dispose(); }
        catch (Exception ex) { Log.Error($"Opening {Path.GetFileName(exe)} failed", ex); }
    }

    public void Dispose()
    {
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        _icon = IntPtr.Zero;
    }

    // ---------- Win32 ----------
    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WndClassEx
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Msg { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }

    [StructLayout(LayoutKind.Sequential)]
    struct Point { public int X, Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WndClassEx cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l); // W: the class is Unicode (the A version cut the title to "K")
    [DllImport("user32.dll")] static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref Msg msg);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint msg, uint action, IntPtr changeInfo);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr menu, uint flags, IntPtr id, string? text);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point pt);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetKeyNameText(int lParam, StringBuilder name, int size);
    [DllImport("uxtheme.dll", EntryPoint = "#135")] static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#133")] static extern bool AllowDarkModeForWindow(IntPtr hwnd, bool allow);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] static extern void FlushMenuThemes();
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] static extern IntPtr LoadImage(IntPtr instance, IntPtr name, uint type, int cx, int cy, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint ExtractIconEx(string file, int index, IntPtr large, out IntPtr small, uint count);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
}
