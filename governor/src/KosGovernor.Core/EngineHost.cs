using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace KosGovernor.Core;

/// <summary>
/// The engine process (KosGovernorEngine.exe, Native AOT; or the interface's exe with "--engine" when the engine exe is
/// missing): tray icon, global gamemode hotkey and the <see cref="Engine"/> loop on its own thread. One instance per
/// session; ends through the Exit menu or the stop event.
/// </summary>
public static class EngineHost
{
    public static int Run()
    {
        using var mutex = new Mutex(true, Names.EngineMutex, out var created);
        if (!created) return 0;
        // SeProfileSingleProcess: clear the standby list (gamemode); SeIncreaseQuota: empty the working set of other processes.
        Native.EnablePrivileges("SeDebugPrivilege", "SeIncreaseBasePriorityPrivilege", "SeProfileSingleProcessPrivilege", "SeIncreaseQuotaPrivilege");
        GamemodeSystem.RecoverAtStart();
        EtwDriverTrace.StopSession(); // a thread director trace left by a crash
        RuleExtras.RecoverAtStart();
        Log.Prune();
        Log.Info($"Engine {typeof(EngineHost).Assembly.GetName().Version} started.");
        using var stop = new EventWaitHandle(false, EventResetMode.AutoReset, Names.Stop);
        using var reload = new EventWaitHandle(false, EventResetMode.AutoReset, Names.Reload);
        using var toggle = new EventWaitHandle(false, EventResetMode.AutoReset, Names.ToggleGamemode);
        // A signal left from an engine that already exited (it sets Stop again on its way out, and the event outlives it
        // while another process holds a handle) isn't meant for this one.
        stop.Reset();
        toggle.Reset();
        var engine = new Engine();
        var fans = new FanCurveHost();
        var gpu = new GamemodeGpuHost(fans);
        gpu.RecoverAtStart();
        engine.GamemodeSwitched += gpu.Switch;
        var mice = new GamemodePeripheralHost(); // peripherals: gamemode mouse settings
        mice.RecoverAtStart();
        engine.GamemodeSwitched += mice.Switch;
        var recovery = new GpuRecovery();
        using var tray = new Tray(engine, stop);
        engine.ConfigChanged += tray.Refresh;
        engine.SessionEnded += report => tray.Balloon("Gamemode session", $"{report.Summary}. Report on the toolbox's Stability page.",
            report.Sections.Any(s => s.Lines.Any(l => l.Warning)));
        var batteries = new PeripheralBatteryMonitor((title, text) => tray.Balloon(title, text, warning: true)); // peripherals: tray batteries
        batteries.Start();

        var worker = new Thread(() =>
        {
            var handles = new WaitHandle[] { stop, reload, toggle, engine.Wake };
            var trimmed = false;
            while (true)
            {
                try { engine.Tick(); }
                catch (Exception ex) { Log.Error("Engine tick failed", ex); }
                try { fans.Sync(); }
                catch (Exception ex) { Log.Error("Fan curve check failed", ex); }
                try { recovery.Tick(fans); }
                catch (Exception ex) { Log.Error("Checking for a display driver reset failed", ex); }
                tray.Refresh();
                // Once, after the first cycle: the pages used only at startup (config, game list, the ~12 MB that
                // NVML zeroes on init) leave the working set; the ones used again come back on their own.
                if (!trimmed) trimmed = EmptyWorkingSet((IntPtr)(-1));
                var i = WaitHandle.WaitAny(handles, Math.Clamp(engine.Config.General.EngineIntervalMs, Validation.MinEngineMs, Validation.MaxEngineMs));
                if (i == 0) break;
                if (i == 2) engine.RequestToggle();
            }
            engine.Shutdown();
            if (engine.Gamemode) gpu.Switch(false);
            if (engine.Gamemode) mice.Switch(false);
            batteries.Stop();
            fans.Shutdown();
            tray.Close();
        }) { IsBackground = true, Name = "Governor engine" };
        worker.Start();
        tray.Run();
        stop.Set();
        worker.Join(5000);
        Log.Info("Engine stopped.");
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr process); // -1 = the process itself

    /// <summary>The engine's own Native AOT executable, next to the interface's.</summary>
    public static string EnginePath => Path.Combine(AppContext.BaseDirectory, "KosGovernorEngine.exe");
    public static string UiPath => Path.Combine(AppContext.BaseDirectory, "KosGovernor.exe");

    /// <summary>
    /// Starts the engine (already elevated) if it isn't running. Without the engine executable next to it (the interface run
    /// from its own build folder), the interface's executable runs the engine itself with "--engine".
    /// </summary>
    public static void EnsureRunning()
    {
        if (Names.EngineRunning) return;
        var start = File.Exists(EnginePath) ? new ProcessStartInfo(EnginePath) : new ProcessStartInfo(Environment.ProcessPath!, "--engine");
        start.UseShellExecute = false;
        Process.Start(start)?.Dispose();
    }
}

/// <summary>Invisible window with the tray icon and the global hotkey (Windows API, no WPF: keeps the engine light).</summary>
internal sealed class Tray : IDisposable
{
    const uint WmNull = 0, WmDestroy = 0x2, WmClose = 0x10, WmContextMenu = 0x7B, WmHotkey = 0x312, WmApp = 0x8000;
    const uint WmTray = WmApp + 1, WmRefresh = WmApp + 2;
    const uint WmLButtonUp = 0x202, WmLButtonDblClk = 0x203, WmRButtonUp = 0x205;
    const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NifMessage = 1, NifIcon = 2, NifTip = 4, NifInfo = 0x10, NiifInfo = 1, NiifWarning = 2;
    const uint MfString = 0, MfChecked = 8, MfPopup = 0x10, MfSeparator = 0x800;
    const int CmdOpen = 1, CmdGamemode = 2, CmdExit = 3, CmdProfile = 100;

    readonly Engine _engine;
    readonly EventWaitHandle _stop;
    readonly WndProc _proc;
    readonly uint _taskbarCreated;
    IntPtr _hwnd, _icon;
    bool _hotkey;

    public Tray(Engine engine, EventWaitHandle stop)
    {
        _engine = engine;
        _stop = stop;
        _proc = Proc; // reference kept: Windows calls the delegate while the window exists
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        var hInstance = GetModuleHandle(null);
        var cls = new WndClassEx { cbSize = Marshal.SizeOf<WndClassEx>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), hInstance = hInstance, lpszClassName = "KosGovernorEngine" };
        RegisterClassEx(ref cls);
        _hwnd = CreateWindowEx(0, cls.lpszClassName, "KOS Governor", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        // The engine is elevated and Explorer isn't: without this, UIPI blocks clicks on the icon and the taskbar-recreated notice.
        ChangeWindowMessageFilterEx(_hwnd, WmTray, 1, IntPtr.Zero);
        ChangeWindowMessageFilterEx(_hwnd, _taskbarCreated, 1, IntPtr.Zero);
        DarkMenus(_hwnd);
        var size = GetSystemMetrics(49); // SM_CXSMICON, already at the monitor's scale
        _icon = LoadImage(hInstance, (IntPtr)32512, 1, size, size, 0); // the .exe's icon (IDI_APPLICATION), at tray size
        if (_icon == IntPtr.Zero)
            ExtractIconEx(Environment.ProcessPath!, 0, IntPtr.Zero, out _icon, 1);
        AddIcon();
        RegisterHotkey();
    }

    public void Run()
    {
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    public void Refresh() => PostMessage(_hwnd, WmRefresh, IntPtr.Zero, IntPtr.Zero);
    public void Close() => PostMessage(_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);

    IntPtr Proc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        if (msg == _taskbarCreated && msg != 0) { AddIcon(); return IntPtr.Zero; }
        switch (msg)
        {
            case WmTray:
                var e = (uint)(l.ToInt64() & 0xFFFF);
                if (e is WmLButtonDblClk or WmLButtonUp) OpenUi();
                else if (e is WmRButtonUp or WmContextMenu) ShowMenu();
                return IntPtr.Zero;
            case WmHotkey:
                _engine.RequestToggle();
                return IntPtr.Zero;
            case WmRefresh:
                RegisterHotkey();
                Notify(NimModify);
                return IntPtr.Zero;
            case WmClose:
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WmDestroy:
                Notify(NimDelete);
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
        AppendMenu(menu, MfString, CmdOpen, "Open KOS Governor");
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
            case CmdOpen: OpenUi(); break;
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

    static void OpenUi()
    {
        try { Process.Start(new ProcessStartInfo(EngineHost.UiPath) { UseShellExecute = false })?.Dispose(); }
        catch (Exception ex) { Log.Error("Opening the interface failed", ex); }
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
