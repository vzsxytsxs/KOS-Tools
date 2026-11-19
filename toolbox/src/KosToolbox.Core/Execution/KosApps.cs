using System.ComponentModel;
using System.Diagnostics;
using KosToolbox.Core.Text;
using KosToolbox.Core.Tools;

namespace KosToolbox.Core.Execution;

/// <summary>
/// The other KOS apps (KOS Firewall, KOS Equalizer), which ask for administrator when they start. Installed in
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
}
