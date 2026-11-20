using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace KosGovernor.Core;

/// <summary>
/// KOS Shell's dark mode for classic Windows tools, one switch per group on the toolbox's Windows › Visuals page
/// (HKCU\Software\TheKOS\Shell: DarkRegedit, DarkConsoles, DarkNotepad, DarkDialogs = 1).
/// <list type="bullet">
/// <item>While a switch is on, an out-of-context window event hook (it loads nothing anywhere) tells the tray thread when a top-level
/// window is shown. A window of an app whose switch is on, matched by its full path in the Windows folder, gets KosShell.dll through a
/// hook on that window's thread only (DarkHook), removed after one message; the DLL checks its exe again and turns on dark mode in that
/// process (shell\KosDark.cpp).</item>
/// <item>DarkDialogs: File Explorer only once it shows a dialog (#32770; the DLL then darkens only dialogs there), rundll32 only while it
/// runs a Control Panel applet (shell32.dll,Control_RunDLL), and System Properties (System32\SystemProperties*.exe).</item>
/// <item>No other process gets the DLL, games included. Off applies to apps started afterwards: an open window stays dark until it's closed.</item>
/// </list>
/// Runs on the tray thread (the window events arrive there).
/// </summary>
internal sealed class DarkApps
{
    /// <summary>Each switch's registry value and its apps' paths under the Windows folder (the DLL has the same list).</summary>
    internal static readonly (string Value, string[] Paths)[] Apps =
    [
        ("DarkRegedit", ["regedit.exe"]),
        ("DarkConsoles", [@"System32\mmc.exe"]),
        ("DarkNotepad", [@"System32\notepad.exe"]),
        ("DarkDialogs", ["explorer.exe", @"System32\rundll32.exe", @"System32\SystemProperties*.exe"]),
    ];

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
        Apps.Where(a => value(a.Value) is int v && v == 1).SelectMany(a => a.Paths).Select(p => Path.Combine(windows, p)).ToArray();

    /// <summary>A wanted path matches the exe: the same file, or for "Name*.exe" one in that folder whose name starts so.</summary>
    internal static bool Matches(string wanted, string exe)
    {
        if (!wanted.EndsWith("*.exe", StringComparison.Ordinal)) return string.Equals(wanted, exe, StringComparison.OrdinalIgnoreCase);
        var prefix = wanted[..^5];
        return exe.Length > prefix.Length + 4 && exe.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && exe.IndexOf('\\', prefix.Length) < 0;
    }

    /// <summary>
    /// Whether this window's process gets the DLL: true yes, false no; null not yet (File Explorer before a dialog: asked again at its
    /// next window). The command line is read only for rundll32.
    /// </summary>
    internal static bool? Wants(string[] wanted, string exe, string windowClass, Func<string?> commandLine, string windows)
    {
        if (!wanted.Any(w => Matches(w, exe))) return false;
        if (string.Equals(exe, Path.Combine(windows, "explorer.exe"), StringComparison.OrdinalIgnoreCase))
            return windowClass == "#32770" ? true : null;
        if (string.Equals(exe, Path.Combine(windows, @"System32\rundll32.exe"), StringComparison.OrdinalIgnoreCase))
            return commandLine()?.Contains("shell32.dll,Control_RunDLL", StringComparison.OrdinalIgnoreCase) == true;
        return true;
    }

    readonly WinEventProc _callback;
    /// <summary>Processes (id and start time) already decided; true: File Explorer waiting for a dialog.</summary>
    readonly Dictionary<(int Pid, long Created), bool> _seen = new();
    readonly HashSet<(int Pid, long Created)> _retried = new();
    readonly string _windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
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
        _seen.Clear();
        _retried.Clear();
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
        var cls = new StringBuilder(16);
        GetClassName(hwnd, cls, cls.Capacity);
        var windowClass = cls.ToString();
        using var process = ProcessControl.Open((int)pid, Native.ProcessQueryLimited);
        if (process is null || !Native.GetProcessTimes(process, out var created, out _, out _, out _)) return;
        var key = ((int)pid, created);
        if (_seen.TryGetValue(key, out var waiting) && (!waiting || windowClass != "#32770")) return;
        if (_seen.Count > 4096) { _seen.Clear(); _retried.Clear(); } // shortcut: forgets exited processes in bulk; open apps are only checked again
        var name = new StringBuilder(1024);
        var size = name.Capacity;
        if (!Native.QueryFullProcessImageName(process, 0, name, ref size)) return;
        var exe = name.ToString();
        var verdict = Wants(_wanted, exe, windowClass, () => CommandLine(process), _windows);
        _seen[key] = verdict is null;
        if (verdict != true) return;
        var app = Path.GetFileName(exe);
        // Already there: the same build gets the hook again (the DLL starts once); an older one renamed by an update would run this
        // build's offset in its old code, so that process waits for its restart.
        if (LoadedBuild((int)pid) is false) { Log.Warn($"Dark mode: {app} (pid {pid}) has another KOS Shell build loaded (or its modules can't be read); dark from its next start."); return; }
        // The thread is held open and checked to be that process's: a thread id reused by another process never gets the hook.
        using var owner = ThreadControl.Open((int)thread, Native.ThreadQueryLimited);
        if (owner is null || GetProcessIdOfThread(owner) != pid) return;
        var hook = SetWindowsHookEx(4, _proc, _module, thread); // WH_CALLWNDPROC on that one thread
        if (hook == IntPtr.Zero) { Log.Warn($"Dark mode: {app} (pid {pid}) couldn't be hooked (error {Marshal.GetLastWin32Error()})."); return; }
        bool answered;
        // WM_NULL, SMTO_ABORTIFHUNG: the hook runs in the app. Short: the tray thread waits (the killswitch hotkey arrives there too).
        try { answered = SendMessageTimeout(hwnd, 0, IntPtr.Zero, IntPtr.Zero, 2, 1000, out _) != IntPtr.Zero; }
        finally { UnhookWindowsHookEx(hook); }
        if (answered && LoadedBuild((int)pid) is true) Log.Info($"Dark mode: KOS Shell in {app} (pid {pid}).");
        // Busy: tried again at its next window, once (each try holds the tray thread up to a second).
        else if (!answered) { if (_retried.Add(key)) _seen.Remove(key); else Log.Warn($"Dark mode: {app} (pid {pid}) didn't answer twice; left as it is."); }
        else Log.Warn($"Dark mode: KOS Shell didn't load into {app} (pid {pid}).");
    }

    /// <summary>KosShell.dll in that process: null when it isn't, true when it's the build the engine has (the same link time and size
    /// in its header), false for another one.</summary>
    bool? LoadedBuild(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            foreach (System.Diagnostics.ProcessModule m in p.Modules)
                using (m)
                    if (string.Equals(m.ModuleName, "KosShell.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        using var process = ProcessControl.Open(pid, Native.ProcessQueryLimited | Native.ProcessVmRead);
                        var header = new byte[0x200];
                        if (process is null || !ReadProcessMemory(process, m.BaseAddress, header, header.Length, out var read) || read != header.Length) return false;
                        // shortcut: header bytes compared, not the whole image; a rebuild always gets a new link time (no /Brepro)
                        var own = new byte[header.Length];
                        Marshal.Copy(_module, own, 0, own.Length);
                        return Stamp(header) is { } theirs && theirs == Stamp(own);
                    }
        }
        catch (Exception ex) { Log.Warn($"Dark mode: the modules of pid {pid} can't be read: {ex.Message}"); return false; }
        return null;
    }

    /// <summary>A PE header's link time and image size; null when it isn't one.</summary>
    internal static (uint Time, uint Size)? Stamp(byte[] header)
    {
        if (header.Length < 0x40 || header[0] != 'M' || header[1] != 'Z') return null;
        var pe = BitConverter.ToInt32(header, 0x3C);
        if (pe < 0x40 || pe > header.Length - 0x54 || BitConverter.ToUInt32(header, pe) != 0x4550) return null; // "PE\0\0"
        return (BitConverter.ToUInt32(header, pe + 8), BitConverter.ToUInt32(header, pe + 0x50)); // TimeDateStamp, SizeOfImage
    }

    /// <summary>The process's command line (ProcessCommandLineInformation); null when it can't be read.</summary>
    static string? CommandLine(SafeProcessHandle process)
    {
        var buffer = new byte[2048];
        var status = NtQueryInformationProcess(process, 60, buffer, buffer.Length, out var needed);
        if (status == unchecked((int)0xC0000004) && needed > buffer.Length && needed < 0x20000) // STATUS_INFO_LENGTH_MISMATCH
        {
            buffer = new byte[needed];
            status = NtQueryInformationProcess(process, 60, buffer, buffer.Length, out _);
        }
        if (status != 0) return null;
        // UNICODE_STRING (Length, MaximumLength, Buffer) whose text follows it in the same buffer.
        var length = BitConverter.ToUInt16(buffer, 0);
        var start = IntPtr.Size == 8 ? 16 : 8;
        return start + length <= buffer.Length ? Encoding.Unicode.GetString(buffer, start, length) : null;
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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int size);
    [DllImport("kernel32.dll")] static extern uint GetProcessIdOfThread(SafeAccessTokenHandle thread);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(SafeProcessHandle process, IntPtr address, byte[] buffer, nint size, out nint read);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(SafeProcessHandle process, int infoClass, byte[] info, int size, out int returned);
}
