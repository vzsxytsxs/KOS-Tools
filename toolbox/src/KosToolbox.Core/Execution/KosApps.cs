using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using KosToolbox.Core.Text;
using KosToolbox.Core.Tools;

namespace KosToolbox.Core.Execution;

/// <summary>
/// The other KOS apps (KOS Governor, KOS Firewall, KOS Equalizer), which ask for administrator when they start. Installed in
/// %ProgramFiles%\KOS\&lt;folder&gt;; a copy of the toolbox run from the repository also finds them in their publish folder.
/// </summary>
public static class KosApps
{
    public static string Installed(string folder, string exe) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "KOS", folder, exe);

    /// <summary>Where the app is: installed, beside this app's folder, or in the repository's publish folder. Null when nowhere.</summary>
    public static string? Find(string folder, string exe) => new[]
    {
        Installed(folder, exe),
        Path.Combine(AppContext.BaseDirectory, "..", folder, exe),
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", folder, "publish", "win-x64", exe),
    }.Select(Path.GetFullPath).FirstOrDefault(File.Exists);

    /// <summary>
    /// Starts the app. Elevated, only the installed copy and only when SYSTEM or Administrators own it (an elevated start of a
    /// file a user can replace would run that file as administrator). Not elevated, through the shell, so Windows asks (UAC).
    /// </summary>
    public static void Start(string folder, string exe)
    {
        if (Focus(exe, Installed(folder, exe), Find(folder, exe))) return;
        if (Elevation.IsAdmin)
        {
            var path = Installed(folder, exe);
            if (!File.Exists(path)) throw new InvalidOperationException(Txt.AppNotInstalled(Path.GetFileNameWithoutExtension(exe), path));
            if (ToolDiscovery.OwnerProblem(path) is { } problem) throw new InvalidOperationException(problem);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = false })?.Dispose();
            return;
        }
        var found = Find(folder, exe) ?? throw new InvalidOperationException(Txt.AppNotInstalled(Path.GetFileNameWithoutExtension(exe), Installed(folder, exe)));
        try { Process.Start(new ProcessStartInfo(found) { UseShellExecute = true })?.Dispose(); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { } // UAC cancelled
    }

    /// <summary>
    /// Brings the app's window to the front when it already runs from one of <paramref name="paths"/> (no second copy, and no UAC
    /// prompt just to show it); false when it isn't running, has no window, or its minimized window didn't come back (then starting
    /// it again makes the running copy show itself). The caller is in front (the user just clicked it), so Windows lets it hand the
    /// focus over.
    /// </summary>
    public static bool Focus(string exe, params string?[] paths)
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
            using (p)
            {
                var hwnd = p.MainWindowHandle; // a visible top-level window; the engine's hidden one doesn't count
                if (hwnd == IntPtr.Zero) continue;
                if (paths.Length > 0 && !paths.Any(x => x is not null && string.Equals(ImagePath(p.Id), Path.GetFullPath(x), StringComparison.OrdinalIgnoreCase)))
                    continue; // another program with the same name
                if (IsIconic(hwnd))
                {
                    ShowWindow(hwnd, 9); // SW_RESTORE
                    if (IsIconic(hwnd)) continue;
                }
                SetForegroundWindow(hwnd);
                return true;
            }
        return false;
    }

    /// <summary>The process's executable (also for a process running as administrator); null when it can't be read.</summary>
    static string? ImagePath(int pid)
    {
        var h = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) return null;
        try
        {
            var path = new char[1024];
            var size = path.Length;
            return QueryFullProcessImageName(h, 0, path, ref size) ? new string(path, 0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, char[] path, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
