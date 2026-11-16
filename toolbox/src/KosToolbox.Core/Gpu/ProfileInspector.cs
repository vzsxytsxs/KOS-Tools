using System.Diagnostics;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Tools;

namespace KosToolbox.Core.Gpu;

/// <summary>
/// NVIDIA driver settings (global and per-game profiles) through the NVIDIA Profile Inspector that KOS installs in
/// tools; the toolbox does not edit the driver profile database on its own. Profile Inspector asks for UAC and, when
/// opening a .nip, asks before importing.
/// </summary>
public static class ProfileInspector
{
    public static string Exe => Path.Combine(ToolDiscovery.Root, "nvidiaProfileInspector", "nvidiaProfileInspector.exe");
    /// <summary>KOS variant that also shows the driver's hidden settings.</summary>
    public static string UnlockedExe => Path.Combine(ToolDiscovery.Root, "nvidiaProfileInspector_UNLOCKED.exe");

    public static bool Available => File.Exists(Exe);
    public static bool UnlockedAvailable => File.Exists(UnlockedExe);

    /// <summary>Executable owner problem when the toolbox is elevated (it would open as administrator); null if ok.</summary>
    public static string? OwnerProblem(string exe) => Elevation.IsAdmin ? ToolDiscovery.OwnerProblem(exe) : null;

    /// <summary>
    /// Opens Profile Inspector; with <paramref name="nipFile"/>, it offers to import the file. When elevated, refuses an
    /// executable that a regular user may have placed or swapped (applies to every button that opens the program).
    /// </summary>
    public static void Open(string exe, string? nipFile = null)
    {
        if (!File.Exists(exe)) throw new FileNotFoundException(Text.Txt.ToolMissing(exe), exe);
        if (OwnerProblem(exe) is { } problem) throw new InvalidOperationException(Text.Txt.NotOpened(problem));
        if (nipFile is not null && (!File.Exists(nipFile) || !nipFile.EndsWith(".nip", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(Text.Txt.NipInvalid);
        var args = nipFile is null ? "" : ProcessRunner.Quote(nipFile);
        Log.Info($"Opening {exe} {args}");
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose();
    }
}
