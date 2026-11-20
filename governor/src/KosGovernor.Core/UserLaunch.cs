using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KosGovernor.Core;

/// <summary>
/// Opens gamemode programs with Explorer's token (standard user), not the elevated engine's: a game or
/// Discord opened this way is the same as one opened from the Start menu.
/// </summary>
public static class UserLaunch
{
    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, IntPtr attributes, int impersonation, int type, out SafeAccessTokenHandle duplicate);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessWithTokenW(SafeAccessTokenHandle token, uint logonFlags, string? application, string commandLine, uint flags,
        IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInformation info);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    public static void Start(StartEntry entry)
    {
        if (Validation.StartProblem(entry) is { } problem) throw new InvalidOperationException(problem);
        if (!File.Exists(entry.Path)) throw new FileNotFoundException($"{entry.Path} doesn't exist.");
        var commandLine = $"\"{entry.Path}\"{(string.IsNullOrWhiteSpace(entry.Arguments) ? "" : " " + entry.Arguments)}";
        var pi = Create(entry.Path, commandLine);
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        Log.Info($"Gamemode: started {commandLine} (pid {pi.dwProcessId})");
    }

    /// <summary>Runs a program as the normal user and waits for it: its exit code, or null when it didn't end in time.</summary>
    internal static uint? Run(string path, string commandLine, int timeoutMs)
    {
        var pi = Create(path, commandLine);
        CloseHandle(pi.hThread);
        try { return WaitForSingleObject(pi.hProcess, (uint)timeoutMs) == 0 && GetExitCodeProcess(pi.hProcess, out var code) ? code : null; }
        finally { CloseHandle(pi.hProcess); }
    }

    static ProcessInformation Create(string path, string commandLine)
    {
        if (GetWindowThreadProcessId(GetShellWindow(), out var shellPid) == 0 || shellPid == 0)
            throw new InvalidOperationException("Explorer isn't running, so the program can't be opened as the normal user.");
        using var shell = ProcessControl.Open(shellPid, Native.ProcessQueryLimited) ?? throw Error("opening Explorer");
        if (!OpenProcessToken(shell, 0x0002, out var token)) throw Error("reading Explorer's token"); // TOKEN_DUPLICATE
        using (token)
        {
            // QUERY | ASSIGN_PRIMARY | DUPLICATE | ADJUST_DEFAULT | ADJUST_SESSIONID; SecurityImpersonation, TokenPrimary.
            if (!DuplicateTokenEx(token, 0x0008 | 0x0001 | 0x0002 | 0x0080 | 0x0100, IntPtr.Zero, 2, 1, out var primary)) throw Error("copying Explorer's token");
            using (primary)
            {
                var si = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
                if (!CreateProcessWithTokenW(primary, 0, path, commandLine, 0, IntPtr.Zero, Path.GetDirectoryName(path), ref si, out var pi))
                    throw Error($"starting {path}");
                return pi;
            }
        }
    }

    static Exception Error(string what) => new InvalidOperationException($"{what} failed: {Native.Win32Message(Marshal.GetLastWin32Error())}");
}
