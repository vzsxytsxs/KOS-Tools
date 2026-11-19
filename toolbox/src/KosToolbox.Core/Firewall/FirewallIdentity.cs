using System.Runtime.InteropServices;

namespace KosToolbox.Core.Firewall;

/// <summary>
/// Names for what a rule matches: a service's SID (what ALE_USER_ID sees in its token), a Store app's package SID, and the
/// drive-letter path of the "\device\harddiskvolumeN\..." path a net event carries. Plain P/Invoke (KOS Engine uses it).
/// </summary>
public static class FirewallIdentity
{
    /// <summary>The S-1-5-80-... SID of a service (NT SERVICE\name); null if there is no such service.</summary>
    public static string? ServiceSid(string service)
    {
        if (string.IsNullOrWhiteSpace(service) || service.IndexOfAny(['\\', '/', '"']) >= 0) return null;
        int sidSize = 0, domainSize = 0;
        LookupAccountNameW(null, @"NT SERVICE\" + service, IntPtr.Zero, ref sidSize, null, ref domainSize, out _);
        if (sidSize == 0) return null;
        var sid = Marshal.AllocHGlobal(sidSize);
        try
        {
            var domain = new char[Math.Max(1, domainSize)];
            if (!LookupAccountNameW(null, @"NT SERVICE\" + service, sid, ref sidSize, domain, ref domainSize, out _)) return null;
            return SidString(sid) is { } s && FirewallPolicy.IsSid(s, "S-1-5-80-") ? s : null;
        }
        finally { Marshal.FreeHGlobal(sid); }
    }

    /// <summary>The S-1-15-2-... SID of a Store app, from its package family name; null if it can't be derived.</summary>
    public static string? PackageSid(string familyName)
    {
        if (string.IsNullOrWhiteSpace(familyName) || DeriveAppContainerSidFromAppContainerName(familyName.ToLowerInvariant(), out var sid) != 0) return null;
        try { return SidString(sid) is { } s && FirewallPolicy.IsSid(s, "S-1-15-2-") ? s : null; }
        finally { FreeSid(sid); }
    }

    static string? SidString(IntPtr sid)
    {
        if (!ConvertSidToStringSidW(sid, out var text)) return null;
        try { return Marshal.PtrToStringUni(text); }
        finally { Wfp.LocalFree(text); }
    }

    static readonly object Gate = new();
    static Dictionary<string, string>? _drives;
    static DateTime _drivesRead;

    /// <summary>
    /// "\device\harddiskvolume5\users\x\a.exe" as "C:\users\x\a.exe" (the case of the device path is kept: Windows lowercases
    /// app ids). Null for anything that isn't on a lettered volume ("System", a network path, an unknown device).
    /// </summary>
    public static string? DosPath(string? devicePath)
    {
        if (devicePath is null || !devicePath.StartsWith(@"\device\", StringComparison.OrdinalIgnoreCase)) return null;
        lock (Gate)
        {
            if (_drives is null || DateTime.UtcNow - _drivesRead > TimeSpan.FromMinutes(1))
            {
                _drives = new(StringComparer.OrdinalIgnoreCase);
                var buffer = new char[1024];
                foreach (var drive in Environment.GetLogicalDrives())
                {
                    var letter = drive.TrimEnd('\\');
                    if (QueryDosDeviceW(letter, buffer, buffer.Length) > 0) _drives[new string(buffer, 0, Math.Max(0, Array.IndexOf(buffer, '\0')))] = letter;
                }
                _drivesRead = DateTime.UtcNow;
            }
            foreach (var (device, letter) in _drives)
                if (devicePath.Length > device.Length && devicePath[device.Length] == '\\' && devicePath.StartsWith(device, StringComparison.OrdinalIgnoreCase))
                    return letter + devicePath[device.Length..];
        }
        return null;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool LookupAccountNameW(string? system, string account, IntPtr sid, ref int sidSize, [Out] char[]? domain, ref int domainSize, out int use);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr text);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
    [DllImport("advapi32.dll")] static extern IntPtr FreeSid(IntPtr sid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern int QueryDosDeviceW(string device, [Out] char[] target, int max);
}
