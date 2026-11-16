using System.Runtime.InteropServices;
using KosToolbox.Core.Text;
using Microsoft.Win32;

namespace KosToolbox.Core.Devices;

/// <summary>
/// RW (RwDrv.sys) is on Microsoft's vulnerable driver blocklist: Windows refuses to load it while the blocklist is on (value absent =
/// on), and while VBS or memory integrity runs. The same checks are in bin\3\XHCI-IMOD-Interval.ps1, which doesn't start RW then.
/// </summary>
public static class RwDriver
{
    /// <summary>Why the driver can't load now; null when it can.</summary>
    public static string? Blocked()
    {
        using var ci = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CI\Config");
        using var policy = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CI\Policy");
        return Reason(ci?.GetValue("VulnerableDriverBlocklistEnable") as int?, policy?.GetValue("VerifiedAndReputablePolicyState") as int?, IsolationFlags());
    }

    /// <summary>
    /// <paramref name="smartAppControl"/>: VerifiedAndReputablePolicyState; Smart App Control (and S mode) force the blocklist on.
    /// <paramref name="isolation"/>: first byte of SYSTEM_ISOLATED_USER_MODE_INFORMATION (bit 0 secure kernel running = VBS, bit 1 HVCI);
    /// null when it couldn't be read, which counts as blocked.
    /// </summary>
    internal static string? Reason(int? blocklist, int? smartAppControl, byte? isolation) =>
        blocklist != 0 || smartAppControl is not (null or 0) ? Txt.RwBlocked(Txt.RwBlocklistOn)
        : isolation is not { } f ? Txt.RwBlocked(Txt.RwVbsRunning)
        : (f & 2) != 0 ? Txt.RwBlocked(Txt.RwHvciRunning)
        : (f & 1) != 0 ? Txt.RwBlocked(Txt.RwVbsRunning)
        : null;

    const int SystemIsolatedUserModeInformation = 165;

    static byte? IsolationFlags()
    {
        var buffer = new byte[16];
        return NtQuerySystemInformation(SystemIsolatedUserModeInformation, buffer, buffer.Length, out _) == 0 ? buffer[0] : null;
    }

    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int infoClass, byte[] buffer, int size, out int returned);
}
