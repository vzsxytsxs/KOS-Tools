using System.Diagnostics;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;
using KosToolbox.Core.Tools;
using Microsoft.Win32;

namespace KosToolbox.Core.Drivers;

/// <summary>One DDU option: its command-line switch, the vendor it applies to (null = any) and whether it starts checked.</summary>
public sealed record DduOption(string Switch, string? Vendor, string Name, string? Description, bool Default);

public sealed class DduJob
{
    /// <summary>NVIDIA, AMD or INTEL (<see cref="DisplayDrivers.Vendor"/>).</summary>
    public string Vendor { get; set; } = "";
    /// <summary>Switches from <see cref="Ddu.Options"/> (only those of this vendor or of any vendor).</summary>
    public List<string> Options { get; set; } = new();
    /// <summary>Restart into Safe Mode, clean there and restart back (otherwise: clean now and restart).</summary>
    public bool SafeMode { get; set; }
}

/// <summary>
/// Advanced display driver removal through Display Driver Uninstaller (in the KOS tools folder), driven by its command line:
/// it removes the driver, its services, folders, registry leftovers and devices, and restarts. Safe Mode: a one-time boot
/// (bcdedit /bootsequence) into a copy of the current boot entry with safeboot, and a RunOnce entry (the * makes it run in
/// Safe Mode) that deletes the copy and runs DDU. The boot after that is normal whatever happens in Safe Mode (no
/// administrator signs in, the PIN doesn't work there).
/// </summary>
public static class Ddu
{
    public static string Exe => Path.Combine(ToolDiscovery.Root, "Display Driver Uninstaller", "Display Driver Uninstaller.exe");
    public static bool Available => File.Exists(Exe);
    const string RunOnceKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", RunOnceName = "*KosDdu";
    static string Bcdedit => Path.Combine(Environment.SystemDirectory, "bcdedit.exe");

    static readonly Dictionary<string, string> CleanSwitch = new(StringComparer.Ordinal)
    {
        ["NVIDIA"] = "-CleanNvidia", ["AMD"] = "-CleanAmd", ["INTEL"] = "-CleanIntel",
    };
    public static IReadOnlyCollection<string> Vendors => CleanSwitch.Keys;

    /// <summary>"-CreateRestorePoint" isn't a DDU switch: without it, the arguments get -NoRestorePoint.</summary>
    public const string RestorePoint = "-CreateRestorePoint";

    public static readonly IReadOnlyList<DduOption> Options = new DduOption[]
    {
        new("-RemovePhysx", "NVIDIA", "PhysX", null, true),
        new("-RemoveGFE", "NVIDIA", "GeForce Experience / NVIDIA app", null, true),
        new("-RemoveNvidiaCache", "NVIDIA", "NVIDIA shader cache", "Leave it if you use the NVIDIA app's automatic shader compilation.", true),
        new("-RemoveNvidiaDirs", "NVIDIA", @"C:\NVIDIA driver folders", "Where the NVIDIA installer extracts itself.", true),
        new("-Remove3DTVPlay", "NVIDIA", "3DTV Play", null, true),
        new("-RemoveNVBROADCAST", "NVIDIA", "NVIDIA Broadcast", null, false),
        new("-RemoveNVCP", "NVIDIA", "NVIDIA Control Panel (Microsoft Store)", "The DCH control panel app. The KOS setup installs it separately (NVCP.exe); after removing it, install it again.", false),
        new("-RemoveAMDCP", "AMD", "AMD control panel (Microsoft Store)", null, true),
        new("-RemoveAMDDirs", "AMD", @"C:\AMD driver folders", null, true),
        new("-RemoveCrimsonCache", "AMD", "AMD shader cache", "Recommended for graphical glitches from a damaged cache, or when switching to another brand.", true),
        new("-RemoveAudioBus", "AMD", "AMD Audio Bus", null, false),
        new("-RemoveAMDKMPFD", "AMD", "AMDKMPFD filter driver", "Part of the AMD chipset driver: reinstall the chipset driver after removing it.", false),
        new("-RemoveINTELCP", "INTEL", "Intel control panel (Microsoft Store)", null, true),
        new("-RemoveINTELIGS", "INTEL", "Intel Arc Control / Intel Graphics Software", null, true),
        new("-RemoveOneAPI", "INTEL", "Intel oneAPI Level Zero", null, false),
        new("-RemoveEnduranceGaming", "INTEL", "Intel Endurance Gaming", null, false),
        new("-RemoveIntelNpu", "INTEL", "Intel NPU", null, false),
        new("-RemoveVulkan", null, "Vulkan runtime", "Shared by every GPU and game that uses Vulkan: leave it with two GPUs.", false),
        new("-removemonitors", null, "Monitors (present and not present)", "Windows detects them again at the next start; EDID overrides saved in GPU › Display are lost.", false),
        new(RestorePoint, null, "Create a restore point first", "Only in normal mode, when System Restore is on.", false),
    };

    /// <summary>DDU's command line for the job (validated).</summary>
    internal static List<string> Arguments(DduJob job)
    {
        Validate(job);
        var args = new List<string> { "-Silent", "-NoSafeModeMsg", CleanSwitch[job.Vendor] };
        args.AddRange(Options.Where(o => o.Switch != RestorePoint && job.Options.Contains(o.Switch)).Select(o => o.Switch));
        if (!job.Options.Contains(RestorePoint) || job.SafeMode) args.Add("-NoRestorePoint");
        args.Add("-Restart");
        return args;
    }

    /// <summary>Only a known vendor and that vendor's (or any vendor's) known switches.</summary>
    internal static void Validate(DduJob job)
    {
        if (!CleanSwitch.ContainsKey(job.Vendor)) throw new InvalidOperationException($"Unknown display driver vendor: {job.Vendor}");
        if (job.Options.Count != job.Options.Distinct(StringComparer.Ordinal).Count()) throw new InvalidOperationException("Repeated DDU option.");
        foreach (var o in job.Options)
            if (!Options.Any(x => x.Switch == o && (x.Vendor is null || x.Vendor == job.Vendor)))
                throw new InvalidOperationException($"Invalid DDU option for {job.Vendor}: {o}");
    }

    /// <summary>Starts DDU now, or schedules it for Safe Mode and restarts. Elevated if needed.</summary>
    public static async Task RunAsync(DduJob job, CancellationToken ct = default)
    {
        Validate(job);
        if (Elevation.IsAdmin) await Task.Run(() => Execute(job), ct).ConfigureAwait(false);
        else await ElevatedRunner.RunAsync(new ElevatedJob { Ddu = job }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Elevated: DDU must exist and be owned by SYSTEM/Administrators (it runs as administrator). Now: starts it and returns (it
    /// restarts Windows when it finishes). Safe Mode: Safe Mode copy of the boot entry, RunOnce entry, one-time boot, restart;
    /// any failure undoes all of it.
    /// </summary>
    internal static void Execute(DduJob job)
    {
        var args = Arguments(job);
        var exe = Exe;
        if (!File.Exists(exe)) throw new FileNotFoundException(Txt.DduMissing(exe), exe);
        if (ToolDiscovery.OwnerProblem(exe) is { } problem) throw new InvalidOperationException(problem);
        var line = string.Join(" ", args);
        if (!job.SafeMode)
        {
            Log.Info($"DDU: starting {exe} {line}");
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi)?.Dispose();
            return;
        }
        // The copy's id is the only GUID in bcdedit's (localized) message.
        var copy = ProcessRunner.Run(Bcdedit, "/copy {current} /d \"KOS DDU Safe Mode\"");
        var id = BootEntryId(copy.Output);
        if (copy.ExitCode != 0 || id is null) throw new InvalidOperationException(Txt.BcdeditFailed(copy.ExitCode, copy.Output));
        try
        {
            var menu = ProcessRunner.Run(Bcdedit, $"/displayorder {id} /remove"); // not offered in the boot menu
            Log.Info($"DDU: boot entry {id} created (removing it from the boot menu: exit {menu.ExitCode}).");
            Bcd($"/set {id} safeboot minimal");
            // cmd /c "A & B": with the first character a quote, cmd drops the first and the last quote and runs the rest as is.
            var command = $"\"{Path.Combine(Environment.SystemDirectory, "cmd.exe")}\" /d /c \"\"{Bcdedit}\" /delete {id} & \"{exe}\" {line}\"";
            using (var key = Registry.LocalMachine.CreateSubKey(RunOnceKey, writable: true)) key.SetValue(RunOnceName, command, RegistryValueKind.String);
            Log.Info($"DDU: RunOnce {RunOnceName} = {command}");
            Bcd($"/bootsequence {id}");
            Log.Info("DDU: next boot in Safe Mode (once); restarting.");
            var restart = ProcessRunner.Run(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/r /t 3 /c \"KOS Toolbox: restarting into Safe Mode to remove the display driver\"");
            if (restart.ExitCode != 0) throw new InvalidOperationException(Txt.RestartFailed(restart.ExitCode, restart.Output));
        }
        catch
        {
            Cancel(id);
            throw;
        }
    }

    static void Bcd(string args)
    {
        var r = ProcessRunner.Run(Bcdedit, args);
        if (r.ExitCode != 0) throw new InvalidOperationException(Txt.BcdeditFailed(r.ExitCode, r.Output));
    }

    /// <summary>The {GUID} in bcdedit /copy's message; null if there isn't exactly one.</summary>
    internal static string? BootEntryId(string output)
    {
        var ids = System.Text.RegularExpressions.Regex.Matches(output, @"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}");
        return ids.Count == 1 ? ids[0].Value : null;
    }

    /// <summary>Undoes a half-done Safe Mode run: the RunOnce entry and the boot entry (deleting it also drops it from the one-time boot).</summary>
    static void Cancel(string id)
    {
        using (var key = Registry.LocalMachine.OpenSubKey(RunOnceKey, writable: true)) key?.DeleteValue(RunOnceName, throwOnMissingValue: false);
        var r = ProcessRunner.Run(Bcdedit, $"/delete {id}");
        Log.Info($"DDU: Safe Mode run cancelled (deleting {id}: bcdedit exit {r.ExitCode}).");
    }
}
