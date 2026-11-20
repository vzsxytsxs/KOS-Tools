using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace KosGovernor.Core;

/// <summary>
/// KOS Shell's dark mode for classic Windows tools, one switch per app on the toolbox's Windows › Visuals page
/// (HKCU\Software\TheKOS\Shell: DarkRegedit, DarkConsoles = 1).
/// <list type="bullet">
/// <item>While a switch is on, an out-of-context window event hook (it loads nothing anywhere) tells the tray thread when a top-level
/// window is shown. A window of an app whose switch is on, matched by its full path in the Windows folder, gets KosShell.dll through a
/// hook on that window's thread only (DarkHook), removed after one message; the DLL checks its exe again and turns on dark mode in that
/// process (shell\KosDark.cpp).</item>
/// <item>No other process gets the DLL, games included. Off applies to apps started afterwards: an open window stays dark until it's closed.</item>
/// </list>
/// Runs on the tray thread (the window events arrive there).
/// </summary>
internal sealed class DarkApps
{
    /// <summary>Each switch's registry value and its app's path under the Windows folder (the DLL has the same list).</summary>
    internal static readonly (string Value, string Path)[] Apps = [("DarkRegedit", "regedit.exe"), ("DarkConsoles", @"System32\mmc.exe")];

    /// <summary>The full paths whose switch is 1 (any thread).</summary>
    public static string[] Wanted()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\TheKOS\Shell");
            return Paths(name => key?.GetValue(name), Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        }
        catch { return []; }
    }

    internal static string[] Paths(Func<string, object?> value, string windows) =>
        Apps.Where(a => value(a.Value) is int v && v == 1).Select(a => Path.Combine(windows, a.Path)).ToArray();

    readonly WinEventProc _callback;
    readonly HashSet<(int Pid, long Created)> _decided = new();
    string[] _wanted = [];
    IntPtr _hook, _module, _proc;
    bool _refusalLogged;

    public DarkApps() => _callback = OnEvent; // reference kept: Windows calls it while the hook exists

    /// <summary>The switches changed (tray thread).</summary>
    public void Sync(string[] wanted)
    {
        _wanted = wanted;
        if (wanted.Length == 0)
        {
            if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
            return;
        }
        if (!LoadDll()) return;
        if (_hook == IntPtr.Zero)
            _hook = SetWinEventHook(EventObjectShow, EventObjectShow, IntPtr.Zero, _callback, 0, 0, WineventOutOfContext | WineventSkipOwnProcess);
        // Apps a switch now covers may be open already (a process that's dark already gets the hook again; the DLL starts once).
        _decided.Clear();
        EnumWindows((h, _) => { if (IsWindowVisible(h)) Consider(h); return true; }, IntPtr.Zero);
    }

    void OnEvent(IntPtr hook, uint e, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero || GetAncestor(hwnd, 2) != hwnd) return; // OBJID_WINDOW, CHILDID_SELF, GA_ROOT
        try { using (TaskMeter.Measure(EngineTask.Shell)) Consider(hwnd); }
        catch (Exception ex) { Log.Error("Dark mode check failed", ex); }
    }

    /// <summary>Once per process (its id and start time): KosShell.dll into it when it's one of the wanted apps.</summary>
    void Consider(IntPtr hwnd)
    {
        var thread = GetWindowThreadProcessId(hwnd, out var pid);
        if (thread == 0 || _wanted.Length == 0) return;
        using var process = ProcessControl.Open((int)pid, Native.ProcessQueryLimited);
        if (process is null || !Native.GetProcessTimes(process, out var created, out _, out _, out _)) return;
        if (_decided.Count > 4096) _decided.Clear(); // shortcut: forgets exited processes in bulk; open apps are only checked again
        if (!_decided.Add(((int)pid, created))) return;
        var name = new StringBuilder(1024);
        var size = name.Capacity;
        if (!Native.QueryFullProcessImageName(process, 0, name, ref size)) return;
        var exe = name.ToString();
        if (!_wanted.Any(w => string.Equals(w, exe, StringComparison.OrdinalIgnoreCase))) return;
        // Already there, maybe an older build renamed by an update: hooking again would run the new build's offset in the old code.
        if (ShellHost.HasModule((int)pid, "KosShell.dll")) return;
        // The thread is held open and checked to be that process's: a thread id reused by another process never gets the hook.
        using var owner = ThreadControl.Open((int)thread, Native.ThreadQueryLimited);
        if (owner is null || GetProcessIdOfThread(owner) != pid) return;
        var app = Path.GetFileName(exe);
        var hook = SetWindowsHookEx(4, _proc, _module, thread); // WH_CALLWNDPROC on that one thread
        if (hook == IntPtr.Zero) { Log.Warn($"Dark mode: {app} (pid {pid}) couldn't be hooked (error {Marshal.GetLastWin32Error()})."); return; }
        bool answered;
        // WM_NULL, SMTO_ABORTIFHUNG: the hook runs in the app. Short: the tray thread waits (the killswitch hotkey arrives there too).
        try { answered = SendMessageTimeout(hwnd, 0, IntPtr.Zero, IntPtr.Zero, 2, 1000, out _) != IntPtr.Zero; }
        finally { UnhookWindowsHookEx(hook); }
        if (ShellHost.HasModule((int)pid, "KosShell.dll")) Log.Info($"Dark mode: KOS Shell loaded into {app} (pid {pid}).");
        else if (!answered) _decided.Remove(((int)pid, created)); // busy: tried again at its next window
        else Log.Warn($"Dark mode: KOS Shell didn't load into {app} (pid {pid}).");
    }

    /// <summary>KosShell.dll in the engine, for its hook procedure: only a file a standard user can't have replaced.</summary>
    bool LoadDll()
    {
        if (_module != IntPtr.Zero) return true;
        var dll = ShellHost.DllPath;
        var problem = !File.Exists(dll) ? $"{dll} is missing." : KosToolbox.Core.Tools.ToolDiscovery.OwnerProblem(dll);
        if (problem is null)
            try
            {
                _module = NativeLibrary.Load(dll);
                _proc = NativeLibrary.GetExport(_module, "DarkHook");
                return true;
            }
            catch (Exception ex)
            {
                problem = ex.Message;
                if (_module != IntPtr.Zero) { NativeLibrary.Free(_module); _module = IntPtr.Zero; }
            }
        if (!_refusalLogged) { Log.Warn($"Dark mode: {problem}"); _refusalLogged = true; }
        return false;
    }

    const uint EventObjectShow = 0x8002, WineventOutOfContext = 0, WineventSkipOwnProcess = 2;
    delegate void WinEventProc(IntPtr hook, uint e, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    delegate bool EnumProc(IntPtr hwnd, IntPtr l);

    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, IntPtr proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll")] static extern uint GetProcessIdOfThread(Microsoft.Win32.SafeHandles.SafeAccessTokenHandle thread);
}
