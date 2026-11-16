namespace KosGovernor.Core;

/// <summary>
/// KOS default rules: Windows priority boost off for shell, system, launcher and overlay processes, and the DWM input
/// threads at time-critical priority. New configurations start with them; older ones get the missing ones (ConfigStore.Migrate).
/// </summary>
public static class DefaultRules
{
    static readonly string[] NoBoost =
    {
        "ApplicationFrameHost.exe", "audiodg.exe", "ctfmon.exe", "Discord.exe", "dllhost.exe", "dwm.exe",
        "EABackgroundService.exe", "EACefSubProcess.exe", "EADesktop.exe", "EALocalHostSvc.exe", "explorer.exe",
        "fontdrvhost.exe", "GameInputRedistService.exe", "GameInputSvc.exe", "gamingservices.exe", "gamingservicesnet.exe",
        "lsass.exe", "msedgewebview2.exe", "MSIAfterburner.exe", "NVDisplay.Container.exe", "NVIDIA Overlay.exe",
        "PerfWatson2.exe", "rundll32.exe", "RuntimeBroker.exe", "SearchHost.exe", "sihost.exe", "ShellExperienceHost.exe",
        "ShellHost.exe", "StartMenu.exe", "steam.exe", "steamservice.exe", "steamwebhelper.exe", "svchost.exe",
        "taskhostw.exe", "winlogon.exe", "winws.exe", "WMIADAP.exe", "WmiPrvSE.exe",
    };

    static readonly string[] DwmInputThreads = { "DWM Kernel Sensor Thread", "DWM Master Input Thread" };

    public static List<ProcessRule> Create() => NoBoost.Select(exe => new ProcessRule
    {
        Exe = exe,
        Boost = false,
        Threads = exe == "dwm.exe"
            ? DwmInputThreads.Select(d => new ThreadRule { Description = d, Priority = ThreadPriority.TimeCritical }).ToList()
            : new(),
    }).ToList();

    /// <summary>Adds the default rules whose program has no rule in the profile yet; existing rules are never changed.</summary>
    public static void AddMissing(Profile profile) =>
        profile.Rules.AddRange(Create().Where(d => !profile.Rules.Any(r => r.Exe.Equals(d.Exe, StringComparison.OrdinalIgnoreCase))));
}
