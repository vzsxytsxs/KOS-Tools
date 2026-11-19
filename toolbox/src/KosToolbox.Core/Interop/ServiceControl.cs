using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KosToolbox.Core.Interop;

/// <summary>A service or driver as the Service Control Manager enumerates it.</summary>
public sealed record ScmService(string Name, string DisplayName, uint Type, uint State);

/// <summary>Service Control Manager calls (advapi32), without PowerShell or sc.exe.</summary>
public static class ServiceControl
{
    public const uint TypeWin32 = 0x30, TypeDriver = 0x0B;
    const uint ScManagerConnect = 0x0001, ScManagerEnumerate = 0x0004;
    const uint ServiceQueryConfig = 0x0001, ServiceChangeConfig = 0x0002, ServiceQueryStatus = 0x0004;
    public const uint StateStopped = 1, StateRunning = 4;
    const uint ServiceNoChange = 0xFFFFFFFF;
    const int ErrorMoreData = 234;

    [StructLayout(LayoutKind.Sequential)]
    struct ServiceStatusProcess
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct EnumServiceStatusProcess
    {
        public IntPtr ServiceName, DisplayName;
        public ServiceStatusProcess Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DelayedAutoStartInfo { public int Delayed; }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenSCManagerW")]
    static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenServiceW")]
    static extern IntPtr OpenService(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "EnumServicesStatusExW")]
    static extern bool EnumServicesStatusEx(IntPtr scm, int infoLevel, uint serviceType, uint serviceState, IntPtr buffer, uint bufferSize,
        out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string? groupName);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ChangeServiceConfigW")]
    static extern bool ChangeServiceConfig(IntPtr service, uint serviceType, uint startType, uint errorControl, string? binaryPath,
        string? loadOrderGroup, IntPtr tagId, string? dependencies, string? startName, string? password, string? displayName);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "ChangeServiceConfig2W")]
    static extern bool ChangeServiceConfig2(IntPtr service, uint infoLevel, ref DelayedAutoStartInfo info);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, out ServiceStatusProcess status, int size, out int needed);

    /// <summary>The service's current state (<see cref="StateRunning"/>, ...); null if there is no such service or it can't be queried.</summary>
    public static uint? State(string name)
    {
        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) return null;
        try
        {
            var svc = OpenService(scm, name, ServiceQueryStatus);
            if (svc == IntPtr.Zero) return null;
            try { return QueryServiceStatusEx(svc, 0, out var st, Marshal.SizeOf<ServiceStatusProcess>(), out _) ? st.CurrentState : null; }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    /// <summary>Lists services (<see cref="TypeWin32"/>) or drivers (<see cref="TypeDriver"/>) in any state.</summary>
    public static List<ScmService> Enumerate(uint type)
    {
        var scm = OpenSCManager(null, null, ScManagerConnect | ScManagerEnumerate);
        if (scm == IntPtr.Zero) throw new Win32Exception();
        try
        {
            var list = new List<ScmService>();
            uint resume = 0;
            var size = Marshal.SizeOf<EnumServiceStatusProcess>();
            while (true)
            {
                if (EnumServicesStatusEx(scm, 0, type, 3, IntPtr.Zero, 0, out var needed, out _, ref resume, null)) break; // nothing to list
                var err = Marshal.GetLastWin32Error();
                if (err != ErrorMoreData) throw new Win32Exception(err);
                var buffer = Marshal.AllocHGlobal((int)needed);
                try
                {
                    var ok = EnumServicesStatusEx(scm, 0, type, 3, buffer, needed, out _, out var count, ref resume, null);
                    var more = !ok && Marshal.GetLastWin32Error() == ErrorMoreData;
                    if (!ok && !more) throw new Win32Exception();
                    for (var i = 0; i < count; i++)
                    {
                        var e = Marshal.PtrToStructure<EnumServiceStatusProcess>(buffer + i * size);
                        list.Add(new ScmService(Marshal.PtrToStringUni(e.ServiceName) ?? "", Marshal.PtrToStringUni(e.DisplayName) ?? "",
                            e.Status.ServiceType, e.Status.CurrentState));
                    }
                    if (!more) break;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return list;
        }
        finally { CloseServiceHandle(scm); }
    }

    /// <summary>
    /// Changes the startup type through the SCM (like services.msc). <paramref name="delayed"/> only applies to
    /// automatic start; null keeps the current value. Throws <see cref="Win32Exception"/> with the Windows error code.
    /// </summary>
    public static void SetStartType(string name, int start, bool? delayed)
    {
        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) throw new Win32Exception();
        try
        {
            var svc = OpenService(scm, name, ServiceChangeConfig | ServiceQueryConfig);
            if (svc == IntPtr.Zero) throw new Win32Exception();
            try
            {
                if (!ChangeServiceConfig(svc, ServiceNoChange, (uint)start, ServiceNoChange, null, null, IntPtr.Zero, null, null, null, null))
                    throw new Win32Exception();
                if (delayed is { } d)
                {
                    var info = new DelayedAutoStartInfo { Delayed = d ? 1 : 0 };
                    if (!ChangeServiceConfig2(svc, 3 /*SERVICE_CONFIG_DELAYED_AUTO_START_INFO*/, ref info)) throw new Win32Exception();
                }
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }
}
