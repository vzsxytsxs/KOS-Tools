using System.Runtime.InteropServices;

namespace KosGovernor.Core;

public sealed record HostedService(string Name, string DisplayName);

/// <summary>Services running per process (EnumServicesStatusEx): shows what each svchost hosts and matches the +S rules.</summary>
public static class Services
{
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool CloseServiceHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool EnumServicesStatusEx(IntPtr scm, int infoLevel, uint type, uint state, IntPtr buffer, int size, out int needed, out int count, ref int resume, string? group);

    // ENUM_SERVICE_STATUS_PROCESSW x64: two pointers + SERVICE_STATUS_PROCESS (36 bytes), aligned to 56; PID at 16 + 28.
    const int Stride = 56, PidOffset = 44;

    public static Dictionary<int, List<HostedService>> ByProcess()
    {
        var result = new Dictionary<int, List<HostedService>>();
        var scm = OpenSCManager(null, null, 0x0004); // SC_MANAGER_ENUMERATE_SERVICE
        if (scm == IntPtr.Zero) return result;
        try
        {
            var resume = 0;
            EnumServicesStatusEx(scm, 0, 0x30, 1, IntPtr.Zero, 0, out var needed, out _, ref resume, null); // Win32, ativos
            if (needed <= 0) return result;
            var buffer = Marshal.AllocHGlobal(needed + 4096);
            try
            {
                resume = 0;
                if (!EnumServicesStatusEx(scm, 0, 0x30, 1, buffer, needed + 4096, out _, out var count, ref resume, null)) return result;
                for (var i = 0; i < count; i++)
                {
                    var e = buffer + i * Stride;
                    var pid = Marshal.ReadInt32(e, PidOffset);
                    if (pid == 0) continue;
                    var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(e)) ?? "";
                    var display = Marshal.PtrToStringUni(Marshal.ReadIntPtr(e, 8)) ?? name;
                    if (!result.TryGetValue(pid, out var list)) result[pid] = list = new();
                    list.Add(new HostedService(name, display));
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseServiceHandle(scm); }
        foreach (var list in result.Values) list.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }
}
