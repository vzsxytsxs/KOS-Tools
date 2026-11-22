using Microsoft.Win32;

namespace KosGovernor.Core;

/// <summary>
/// Whether MMCSS itself runs: SystemResponsiveness 100 makes the MMCSS driver refuse every task (STATUS_SERVER_DISABLED, per
/// Microsoft's MMCSS documentation), and a disabled MMCSS service has nothing to schedule. Either way gamemode's
/// "Desktop compositor in MMCSS" (DwmEnableMMCSS) has no effect, though the call itself succeeds.
/// </summary>
public static class Mmcss
{
    const string SystemProfile = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";

    /// <summary>Why MMCSS is off for <paramref name="responsiveness"/> (SystemResponsiveness) and <paramref name="serviceStart"/>
    /// (the MMCSS service's Start); null when it runs.</summary>
    public static string? Problem(int? responsiveness, int? serviceStart) =>
        serviceStart == 4 ? "the MMCSS service is disabled"
        : responsiveness >= 100 ? "SystemResponsiveness is 100, which turns MMCSS off"
        : null;

    /// <summary>The same, read from this PC; null when MMCSS runs or the values can't be read.</summary>
    public static string? Problem()
    {
        try
        {
            using var profile = Registry.LocalMachine.OpenSubKey(SystemProfile);
            using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\MMCSS");
            return Problem(profile?.GetValue("SystemResponsiveness") as int?, service?.GetValue("Start") as int?);
        }
        catch { return null; }
    }
}
