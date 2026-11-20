using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace KosGovernor.Core;

/// <summary>
/// KOS Shell's transparent taskbar, switched on the toolbox's Windows › Visuals page (HKCU\Software\TheKOS\Shell, TransparentTaskbar = 1).
/// <list type="bullet">
/// <item>Every taskbar window gets a transparent accent (SetWindowCompositionAttribute) at each engine tick and shortly after a color,
/// theme or display change, when Explorer puts its own look back. That is all the Windows 10 taskbar needs.</item>
/// <item>The Windows 11 taskbar draws its background in XAML: KosShell.dll, beside the engine, is loaded into Explorer through XAML
/// diagnostics ("rundll32 KosShell.dll,Inject pid", run with Explorer's own token) and keeps the background rectangles transparent.
/// It creates Local\KosShell.&lt;pid&gt;.On and .Changed, through which it's turned off and on again.</item>
/// <item>Turning it off asks each taskbar to redo its own look (WM_SETTINGCHANGE "ImmersiveColorSet") and the DLL to put the
/// rectangles' own brushes back.</item>
/// <item>Crash guard: before loading the DLL, the Explorer it goes into is written to shell-guard.txt. If that Explorer ends within 30 seconds
/// (the DLL works right when it loads), the DLL isn't loaded again until the switch is turned off and on.</item>
/// </list>
/// </summary>
internal sealed class ShellHost
{
    const string KeyPath = @"Software\TheKOS\Shell";
    static readonly TimeSpan GuardWindow = TimeSpan.FromSeconds(30);
    static string GuardPath => Path.Combine(ConfigStore.Directory, "shell-guard.txt");
    static string DllPath => Path.Combine(AppContext.BaseDirectory, "KosShell.dll");
    /// <summary>The accent is on the taskbars (read by the tray's re-apply).</summary>
    static volatile bool _applied;
    /// <summary>The status window's line: null when off.</summary>
    public static volatile string? State;

    readonly Action<string> _warn;
    int _loadedInto, _failedInto; // the Explorer that has KosShell.dll (its switch as last set), and one it didn't load into
    bool _dllOn, _refusalLogged;

    /// <param name="warn">A balloon for the user (the crash guard tripped).</param>
    public ShellHost(Action<string> warn) => _warn = warn;

    public static bool Wanted()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(KeyPath); return key?.GetValue("TransparentTaskbar") is int v && v == 1; }
        catch { return false; }
    }

    /// <summary>Every engine tick.</summary>
    public void Sync()
    {
        if (!Wanted())
        {
            TurnOff();
            State = null;
            if (File.Exists(GuardPath) && Guard() is { Crashed: true }) { File.Delete(GuardPath); Log.Info("Transparent taskbar turned off: KOS Shell may load into Explorer again."); }
            (_refusalLogged, _failedInto) = (false, 0);
            return;
        }
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) { State = "On · no taskbar yet"; return; }
        Reapply(force: true);
        if (!IsXaml(tray)) { State = "On · Windows 10 taskbar"; return; }
        if (GetWindowThreadProcessId(tray, out var pid) != 0) Load(pid);
        State = _dllOn && _loadedInto == pid ? "On · Windows 11 taskbar (KOS Shell in Explorer)" : "On · Windows 11 taskbar: KOS Shell not loaded (see the log)";
    }

    /// <summary>The transparent accent on every taskbar (any thread); after Explorer may have put its own back.</summary>
    public static void Reapply(bool force = false)
    {
        if (!force && !_applied) return;
        var any = false;
        foreach (var bar in Taskbars()) any |= SetAccent(bar, 2, 0); // ACCENT_ENABLE_TRANSPARENTGRADIENT, fully transparent
        _applied = any;
    }

    /// <summary>Every tick while off: the DLL is switched off even when an earlier engine left it on.</summary>
    void TurnOff()
    {
        if (_applied)
        {
            _applied = false;
            foreach (var bar in Taskbars()) SendMessageTimeout(bar, 0x1A, IntPtr.Zero, "ImmersiveColorSet", 2, 2000, out _); // SMTO_ABORTIFHUNG
            Log.Info("Transparent taskbar off: the taskbar's own look is back.");
        }
        if (FindWindow("Shell_TrayWnd", null) is var tray && tray != IntPtr.Zero && GetWindowThreadProcessId(tray, out var pid) != 0 && Switch((int)pid, false)) _dllOn = false;
    }

    /// <summary>KosShell.dll in this Explorer, switched on (loaded once per Explorer; not tried again in one it didn't load into).</summary>
    void Load(uint pid)
    {
        if (Switch((int)pid, true)) // its events are there
        {
            if (_loadedInto != pid)
            {
                // Anyone can create the names: the status believes the module list
                _dllOn = HasModule((int)pid, "KosShell.dll");
                if (_dllOn) Log.Info($"KOS Shell in Explorer (pid {pid}): taskbar transparent.");
                else Log.Warn($"Transparent taskbar: KOS Shell's switch exists in Explorer (pid {pid}) but the DLL isn't loaded there.");
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
        if (!File.Exists(DllPath)) { if (!_refusalLogged) { Log.Warn($"Transparent taskbar: {DllPath} is missing."); _refusalLogged = true; } return; }
        // Explorer runs it from here: only a file a standard user can't have replaced.
        if (KosToolbox.Core.Tools.ToolDiscovery.OwnerProblem(DllPath) is { } owner) { if (!_refusalLogged) { Log.Warn($"Transparent taskbar: {owner}"); _refusalLogged = true; } return; }
        try
        {
            ConfigStore.PrepareDirectory();
            File.WriteAllText(GuardPath, $"{pid} {DateTime.UtcNow.Ticks}");
            var rundll = Path.Combine(Environment.SystemDirectory, "rundll32.exe");
            var code = UserLaunch.Run(rundll, $"\"{rundll}\" \"{DllPath}\",Inject {pid}", 15000);
            if (code == 0 && Switch((int)pid, true) && HasModule((int)pid, "KosShell.dll")) { (_loadedInto, _dllOn) = ((int)pid, true); Log.Info($"KOS Shell loaded into Explorer (pid {pid}): taskbar transparent."); }
            else { Log.Warn($"Transparent taskbar: KOS Shell didn't load into Explorer (pid {pid}, {(code is { } c ? $"0x{c:X8}" : "no answer")}); not tried again in this Explorer."); _failedInto = (int)pid; }
        }
        catch (Exception ex) { Log.Warn($"Transparent taskbar: loading KOS Shell failed: {ex.Message}; not tried again in this Explorer."); _failedInto = (int)pid; }
    }

    static bool HasModule(int pid, string name)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            foreach (System.Diagnostics.ProcessModule m in p.Modules)
                using (m) if (string.Equals(m.ModuleName, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch (Exception ex) { Log.Warn($"Transparent taskbar: Explorer's modules can't be read: {ex.Message}"); }
        return false;
    }

    /// <summary>Sets the DLL's switch in that Explorer; false when the DLL isn't there. Opened as Explorer's user (see <see cref="UserLaunch.AsShellUser"/>).</summary>
    static bool Switch(int pid, bool on)
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
                        if (onEvent.WaitOne(0) == on) return true;
                        if (on) onEvent.Set(); else onEvent.Reset();
                        changed.Set();
                        return true;
                    }
                }
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or IOException) { return false; }
    }

    internal sealed record GuardState(int Pid, DateTime Loaded, bool Crashed);
    internal enum GuardVerdict { Wait, Clear, Trip, Tripped }

    /// <summary>Its Explorer outlived the window: cleared; ended inside it: tripped (a later end isn't KOS Shell's doing).</summary>
    internal static GuardVerdict Judge(GuardState g, bool alive, DateTime now) =>
        g.Crashed ? GuardVerdict.Tripped : now - g.Loaded > GuardWindow ? GuardVerdict.Clear : alive ? GuardVerdict.Wait : GuardVerdict.Trip;

    static GuardState? Guard()
    {
        // The elevated engine reads, rewrites and deletes it: the same folder and owner checks as the other state files here.
        if ((ConfigStore.FolderProblem() ?? ConfigStore.OwnerProblem(GuardPath)) is { } problem) { Log.Warn($"Transparent taskbar: {problem}"); return null; }
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
            case GuardVerdict.Tripped: return "Transparent taskbar: Explorer closed right after KOS Shell loaded, so it isn't loaded again until the switch is turned off and on in the toolbox.";
            case GuardVerdict.Clear: File.Delete(GuardPath); return null;
            case GuardVerdict.Wait: return null;
        }
        File.WriteAllText(GuardPath, $"{g.Pid} {g.Loaded.Ticks} crashed");
        Log.Warn($"Transparent taskbar: Explorer (pid {g.Pid}) ended within {GuardWindow.TotalSeconds:0} s of loading KOS Shell; it won't be loaded again until the switch is turned off and on.");
        _warn("Explorer closed right after the transparent taskbar loaded. It stays off until you turn it off and on again in the toolbox (Windows › Visuals).");
        return "Transparent taskbar: refused after Explorer closed.";
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

    static bool SetAccent(IntPtr hwnd, int state, uint color)
    {
        var accent = new Accent { State = state, Flags = 2, Color = color }; // flag 2: the color applies
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
