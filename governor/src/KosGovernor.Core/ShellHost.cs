using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace KosGovernor.Core;

/// <summary>
/// KOS Shell's taskbar style, chosen on the toolbox's Windows › Visuals page (HKCU\Software\TheKOS\Shell: TransparentTaskbar 1
/// transparent, 2 blur, 3 acrylic; TaskbarColor "#AARRGGBB" or "accent", a tint).
/// <list type="bullet">
/// <item>Every taskbar window gets that accent (SetWindowCompositionAttribute) at each engine tick and shortly after a color,
/// theme or display change, when Explorer puts its own look back. That is all the Windows 10 taskbar needs. Blur and acrylic need
/// Windows' Transparency effects (DWM draws them black without it): while that's off the taskbar is transparent with the tint.</item>
/// <item>The Windows 11 taskbar draws its background in XAML: KosShell.dll, beside the engine, is loaded into Explorer through XAML
/// diagnostics ("rundll32 KosShell.dll,Inject pid", run with Explorer's own token) and keeps the background rectangles transparent, so the accent shows through.
/// It creates Local\KosShell.&lt;pid&gt;.On and .Changed, through which it's turned off and on again.</item>
/// <item>Turning it off asks each taskbar to redo its own look (WM_SETTINGCHANGE "ImmersiveColorSet") and the DLL to put the
/// rectangles' own brushes back.</item>
/// <item>Crash guard: before loading the DLL, the Explorer it goes into is written to shell-guard.txt. If that Explorer ends within 30 seconds
/// (the DLL works right when it loads), the DLL isn't loaded again until the taskbar style is set to Windows and back.</item>
/// </list>
/// </summary>
internal sealed class ShellHost
{
    const string KeyPath = @"Software\TheKOS\Shell";
    static readonly TimeSpan GuardWindow = TimeSpan.FromSeconds(30);
    static string GuardPath => Path.Combine(ConfigStore.Directory, "shell-guard.txt");
    internal static string DllPath => Path.Combine(AppContext.BaseDirectory, "KosShell.dll");
    /// <summary>The accent is on the taskbars (read by the tray's re-apply).</summary>
    static volatile bool _applied;
    /// <summary>The accent wanted, as last read (by the tray's re-apply too).</summary>
    static volatile Look _look = new(2, 2, 0, "Transparent");
    bool? _transparency; // Windows' Transparency effects, as last seen while on
    uint[]? _parts; // the DLL's Look, as last read
    /// <summary>The status window's line: null when off.</summary>
    public static volatile string? State;

    readonly Action<string> _warn;
    int _loadedInto, _failedInto; // the Explorer that has KosShell.dll (its switch as last set), and one it didn't load into
    bool _dllOn, _refusalLogged;

    /// <param name="warn">A balloon for the user (the crash guard tripped).</param>
    public ShellHost(Action<string> warn) => _warn = warn;

    /// <summary>An accent: its state (2 transparent gradient, 4 acrylic blur), flags and ABGR tint.</summary>
    internal sealed record Look(int State, int Flags, uint Color, string Name);

    /// <summary>
    /// The accent for the user's values (registry data: anything unexpected is off, or no tint). Measured on the Windows 10 taskbar:
    /// state 4 with flag 1 is a blur under the tint at its alpha (a clear tint: plain blur), with flag 2 the tint at the strength of
    /// Windows' own acrylic whatever its alpha; state 2 honors the alpha too.
    /// </summary>
    /// <param name="accent">Windows' accent color (DWM AccentColor, ABGR).</param>
    internal static Look? LookFor(object? style, object? color, uint accent, bool transparency)
    {
        if (style is not int s || s is < 1 or > 3) return null;
        var tint = color is "accent" ? 0x80000000 | (accent & 0xFFFFFF) : color is string c ? Argb(c) is { } argb ? Abgr(argb) : 0 : 0;
        if (s == 1) return new(2, 2, tint, "Transparent");
        if (!transparency) return new(2, 2, tint, (s == 2 ? "Blur" : "Acrylic") + " needs Transparency effects (Windows › Visuals): transparent meanwhile");
        return s == 2 ? new(4, 1, tint, "Blur") : new(4, 2, tint == 0 ? 0xFF202020 : tint, "Acrylic");
    }

    /// <summary>"#AARRGGBB", or null.</summary>
    static uint? Argb(string text) =>
        text.Length == 9 && text[0] == '#' && uint.TryParse(text.AsSpan(1), System.Globalization.NumberStyles.AllowHexSpecifier, null, out var v) ? v : null;

    /// <summary>Swaps red and blue: ARGB to ABGR and back.</summary>
    static uint Abgr(uint argb) => (argb & 0xFF00FF00) | ((argb >> 16) & 0xFF) | ((argb & 0xFF) << 16);

    /// <summary>
    /// What KosShell.dll is told besides on/off (its Look mapping, 4 x 32 bits): version 1; the color of the Windows 11 taskbar's
    /// button backgrounds and running-app indicators, 0xFFRRGGBB (TaskbarButtons 1 white, 2 Windows' accent; else 0: Windows' own);
    /// with the transparent accent, 1 and the tint (ARGB) for the hidden icons flyout's background, else 0 0 (Windows' own acrylic,
    /// which goes with blur and acrylic).
    /// </summary>
    internal static uint[] PartsFor(Look look, object? buttons, uint accent) =>
    [
        1,
        buttons is 1 ? 0xFFFFFFFF : buttons is 2 ? 0xFF000000 | (Abgr(accent) & 0xFFFFFF) : 0,
        look.State == 2 ? 1u : 0,
        look.State == 2 ? Abgr(look.Color) : 0,
    ];

    static (Look? Look, bool Transparency, uint[]? Parts) Wanted()
    {
        try
        {
            using var personalize = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var transparency = personalize?.GetValue("EnableTransparency") is int t && t == 1;
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key?.GetValue("TransparentTaskbar") is not { } style) return (null, transparency, null);
            using var dwm = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            var accent = dwm?.GetValue("AccentColor") is int a ? unchecked((uint)a) : 0xFFD47800;
            var look = LookFor(style, key.GetValue("TaskbarColor"), accent, transparency);
            return (look, transparency, look is null ? null : PartsFor(look, key.GetValue("TaskbarButtons"), accent));
        }
        catch { return (null, false, null); }
    }

    /// <summary>Every engine tick.</summary>
    public void Sync()
    {
        var (look, transparency, parts) = Wanted();
        _parts = parts;
        if (look is null)
        {
            TurnOff();
            (State, _transparency) = (null, transparency); // so Windows → Blur, which turns it on in the same apply, is announced
            if (File.Exists(GuardPath) && Guard() is { Crashed: true }) { File.Delete(GuardPath); Log.Info("Taskbar style turned off: KOS Shell may load into Explorer again."); }
            (_refusalLogged, _failedInto) = (false, 0);
            return;
        }
        _look = look;
        // The toolbox turns Transparency effects on with blur and acrylic, but writes no notice: DWM and Explorer read it on this one.
        if (_transparency is { } was && was != transparency) Announce();
        _transparency = transparency;
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) { State = $"{look.Name} · no taskbar yet"; return; }
        Reapply(force: true);
        if (!IsXaml(tray)) { State = $"{look.Name} · Windows 10 taskbar"; return; }
        if (GetWindowThreadProcessId(tray, out var pid) != 0) Load(pid);
        State = _dllOn && _loadedInto == pid ? $"{look.Name} · Windows 11 taskbar (KOS Shell in Explorer)" : $"{look.Name} · Windows 11 taskbar: KOS Shell not loaded (see the log)";
    }

    static int _announcing;

    /// <summary>
    /// WM_SETTINGCHANGE "ImmersiveColorSet" to every window, off the engine tick: a broadcast waits on each window, and anyone can
    /// flip the value and own windows that take their time. One at a time.
    /// </summary>
    static void Announce()
    {
        if (Interlocked.Exchange(ref _announcing, 1) == 1) return; // shortcut: a flip during a broadcast in flight isn't announced again
        new Thread(() => { try { Native.BroadcastSettingChange("ImmersiveColorSet"); } finally { Volatile.Write(ref _announcing, 0); } }) { IsBackground = true, Name = "Taskbar style notice" }.Start();
    }

    /// <summary>The wanted accent on every taskbar (any thread); after Explorer may have put its own back.</summary>
    public static void Reapply(bool force = false)
    {
        if (!force && !_applied) return;
        var any = false;
        var look = _look;
        foreach (var bar in Taskbars()) any |= SetAccent(bar, look.State, look.Flags, look.Color);
        _applied = any;
    }

    /// <summary>Every tick while off: the DLL is switched off even when an earlier engine left it on.</summary>
    void TurnOff()
    {
        if (_applied)
        {
            _applied = false;
            foreach (var bar in Taskbars()) SendMessageTimeout(bar, 0x1A, IntPtr.Zero, "ImmersiveColorSet", 2, 2000, out _); // SMTO_ABORTIFHUNG
            Log.Info("Taskbar style off: the taskbar's own look is back.");
        }
        if (FindWindow("Shell_TrayWnd", null) is var tray && tray != IntPtr.Zero && GetWindowThreadProcessId(tray, out var pid) != 0 && Switch((int)pid, false)) _dllOn = false;
    }

    /// <summary>KosShell.dll in this Explorer, switched on (loaded once per Explorer; not tried again in one it didn't load into).</summary>
    void Load(uint pid)
    {
        if (Switch((int)pid, true, _parts)) // its events are there
        {
            if (_loadedInto != pid)
            {
                // Anyone can create the names: the status believes the module list
                _dllOn = HasModule((int)pid, "KosShell.dll");
                if (_dllOn) Log.Info($"KOS Shell in Explorer (pid {pid}): taskbar background clear.");
                else Log.Warn($"Taskbar style: KOS Shell's switch exists in Explorer (pid {pid}) but the DLL isn't loaded there.");
                _loadedInto = (int)pid;
            }
            CheckGuard();
            return;
        }
        if (pid == _failedInto) return;
        if (CheckGuard() is { } refused)
        {
            if (!_refusalLogged) { Log.Warn(refused); _refusalLogged = true; }
            return;
        }
        if (!File.Exists(DllPath)) { if (!_refusalLogged) { Log.Warn($"Taskbar style: {DllPath} is missing."); _refusalLogged = true; } return; }
        // Explorer runs it from here: only a file a standard user can't have replaced.
        if (KosToolbox.Core.Tools.ToolDiscovery.OwnerProblem(DllPath) is { } owner) { if (!_refusalLogged) { Log.Warn($"Taskbar style: {owner}"); _refusalLogged = true; } return; }
        try
        {
            ConfigStore.PrepareDirectory();
            File.WriteAllText(GuardPath, $"{pid} {DateTime.UtcNow.Ticks}");
            var rundll = Path.Combine(Environment.SystemDirectory, "rundll32.exe");
            var code = UserLaunch.Run(rundll, $"\"{rundll}\" \"{DllPath}\",Inject {pid}", 15000);
            if (code == 0 && Switch((int)pid, true, _parts) && HasModule((int)pid, "KosShell.dll")) { (_loadedInto, _dllOn) = ((int)pid, true); Log.Info($"KOS Shell loaded into Explorer (pid {pid}): taskbar background clear."); }
            else { Log.Warn($"Taskbar style: KOS Shell didn't load into Explorer (pid {pid}, {(code is { } c ? $"0x{c:X8}" : "no answer")}); not tried again in this Explorer."); _failedInto = (int)pid; }
        }
        catch (Exception ex) { Log.Warn($"Taskbar style: loading KOS Shell failed: {ex.Message}; not tried again in this Explorer."); _failedInto = (int)pid; }
    }

    internal static bool HasModule(int pid, string name)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            foreach (System.Diagnostics.ProcessModule m in p.Modules)
                using (m) if (string.Equals(m.ModuleName, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch (Exception ex) { Log.Warn($"KOS Shell: the modules of pid {pid} can't be read: {ex.Message}"); }
        return false;
    }

    /// <summary>
    /// Sets the DLL's switch in that Explorer, and its Look when given and different (Changed is set for either); false when the
    /// DLL isn't there. Opened as Explorer's user (see <see cref="UserLaunch.AsShellUser"/>).
    /// </summary>
    static bool Switch(int pid, bool on, uint[]? parts = null)
    {
        try
        {
            return UserLaunch.AsShellUser(() =>
            {
                if (!EventWaitHandle.TryOpenExisting($@"Local\KosShell.{pid}.On", out var onEvent)) return false;
                using (onEvent)
                {
                    if (!EventWaitHandle.TryOpenExisting($@"Local\KosShell.{pid}.Changed", out var changed)) return false;
                    using (changed)
                    {
                        var wrote = parts is not null && WriteParts(pid, parts);
                        if (onEvent.WaitOne(0) == on && !wrote) return true;
                        if (on) onEvent.Set(); else onEvent.Reset();
                        changed.Set();
                        return true;
                    }
                }
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or IOException) { return false; }
    }

    /// <summary>The DLL's Look, written when it differs (true). A DLL without one (an older build in this Explorer) keeps only the clear background.</summary>
    static bool WriteParts(int pid, uint[] parts)
    {
        try
        {
            using var map = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting($@"Local\KosShell.{pid}.Look", System.IO.MemoryMappedFiles.MemoryMappedFileRights.ReadWrite);
            using var view = map.CreateViewAccessor(0, parts.Length * 4);
            var same = true;
            for (var i = 0; i < parts.Length; i++) same &= view.ReadUInt32(i * 4) == parts[i];
            if (same) return false;
            for (var i = 0; i < parts.Length; i++) view.Write(i * 4, parts[i]);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    internal sealed record GuardState(int Pid, DateTime Loaded, bool Crashed);
    internal enum GuardVerdict { Wait, Clear, Trip, Tripped }

    /// <summary>Its Explorer outlived the window: cleared; ended inside it: tripped (a later end isn't KOS Shell's doing).</summary>
    internal static GuardVerdict Judge(GuardState g, bool alive, DateTime now) =>
        g.Crashed ? GuardVerdict.Tripped : now - g.Loaded > GuardWindow ? GuardVerdict.Clear : alive ? GuardVerdict.Wait : GuardVerdict.Trip;

    static GuardState? Guard()
    {
        // The elevated engine reads, rewrites and deletes it: the same folder and owner checks as the other state files here.
        if ((ConfigStore.FolderProblem() ?? ConfigStore.OwnerProblem(GuardPath)) is { } problem) { Log.Warn($"Taskbar style: {problem}"); return null; }
        try
        {
            var parts = File.ReadAllText(GuardPath).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && int.TryParse(parts[0], out var pid) && long.TryParse(parts[1], out var ticks)
                ? new GuardState(pid, new DateTime(ticks, DateTimeKind.Utc), parts.Length > 2 && parts[2] == "crashed") : null;
        }
        catch { return null; }
    }

    /// <summary>Clears the guard once its Explorer outlived the window; trips it when that Explorer ended inside it. The refusal, or null.</summary>
    string? CheckGuard()
    {
        if (!File.Exists(GuardPath) || Guard() is not { } g) return null;
        switch (Judge(g, ProcessControl.Open(g.Pid, Native.ProcessQueryLimited) is { } h && Alive(h), DateTime.UtcNow))
        {
            case GuardVerdict.Tripped: return "Taskbar style: Explorer closed right after KOS Shell loaded, so it isn't loaded again until the taskbar style is set to Windows and back in the toolbox.";
            case GuardVerdict.Clear: File.Delete(GuardPath); return null;
            case GuardVerdict.Wait: return null;
        }
        File.WriteAllText(GuardPath, $"{g.Pid} {g.Loaded.Ticks} crashed");
        Log.Warn($"Taskbar style: Explorer (pid {g.Pid}) ended within {GuardWindow.TotalSeconds:0} s of loading KOS Shell; it won't be loaded again until the taskbar style is set to Windows and back.");
        _warn("Explorer closed right after the taskbar style loaded. It stays off until you set the taskbar style to Windows and back in the toolbox (Windows › Visuals).");
        return "Taskbar style: refused after Explorer closed.";
    }

    static bool Alive(Microsoft.Win32.SafeHandles.SafeProcessHandle h)
    {
        using (h) return GetExitCodeProcess(h, out var code) && code == 259; // STILL_ACTIVE
    }

    /// <summary>Explorer's taskbars: the main one and the secondary ones of the same process (anyone can create a window of those classes).</summary>
    static List<IntPtr> Taskbars()
    {
        var list = new List<IntPtr>();
        var main = FindWindow("Shell_TrayWnd", null);
        if (main == IntPtr.Zero || GetWindowThreadProcessId(main, out var shell) == 0) return list;
        list.Add(main);
        for (var s = IntPtr.Zero; (s = FindWindowEx(IntPtr.Zero, s, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero;)
            if (GetWindowThreadProcessId(s, out var pid) != 0 && pid == shell) list.Add(s);
        return list;
    }

    /// <summary>The Windows 11 taskbar: XAML content inside the taskbar window (the Windows 10 one is plain Win32 controls).</summary>
    static bool IsXaml(IntPtr tray)
    {
        var found = false;
        var name = new StringBuilder(64);
        EnumChildWindows(tray, (h, _) =>
        {
            name.Clear();
            GetClassName(h, name, name.Capacity);
            found = name.ToString() == "Windows.UI.Composition.DesktopWindowContentBridge";
            return !found;
        }, IntPtr.Zero);
        return found;
    }

    static bool SetAccent(IntPtr hwnd, int state, int flags, uint color)
    {
        var accent = new Accent { State = state, Flags = flags, Color = color };
        var size = Marshal.SizeOf<Accent>();
        var p = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, p, false);
            var data = new CompositionData { Attribute = 19, Data = p, Size = size }; // WCA_ACCENT_POLICY
            return SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    [StructLayout(LayoutKind.Sequential)] struct Accent { public int State, Flags; public uint Color; public int Animation; }
    [StructLayout(LayoutKind.Sequential)] struct CompositionData { public int Attribute; public IntPtr Data; public int Size; }
    delegate bool EnumProc(IntPtr hwnd, IntPtr l);

    [DllImport("user32.dll")] static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int size);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(Microsoft.Win32.SafeHandles.SafeProcessHandle h, out uint code);
}
