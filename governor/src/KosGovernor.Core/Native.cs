using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace KosGovernor.Core;

/// <summary>Windows calls used to read and change processes and threads.</summary>
internal static class Native
{
    // ---------- access ----------
    public const uint ProcessTerminate = 0x0001, ProcessVmRead = 0x0010, ProcessSetInformation = 0x0200, ProcessQueryInformation = 0x0400,
        ProcessSuspendResume = 0x0800, ProcessQueryLimited = 0x1000, ProcessSetLimited = 0x2000;
    public const uint ThreadTerminate = 0x0001, ThreadSuspendResume = 0x0002, ThreadSetInformation = 0x0020, ThreadQueryInformation = 0x0040,
        ThreadSetLimited = 0x0400, ThreadQueryLimited = 0x0800;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeAccessTokenHandle OpenThread(uint access, bool inherit, int tid);

    /// <summary>Times as FILETIME (100 ns since 1601), the same units as the snapshot's CreateTime.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessTimes(SafeProcessHandle h, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetThreadTimes(SafeAccessTokenHandle h, out long creation, out long exit, out long kernel, out long user);

    public const int RelationCache = 2;

    /// <summary>DWM in the MMCSS class while this process lives (1) or not (0). HRESULT.</summary>
    [DllImport("dwmapi.dll")] public static extern int DwmEnableMMCSS(int enable);

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);

    /// <summary>Process of the window in front; 0 when there is none (lock screen, desktop switch).</summary>
    public static int ForegroundPid() => GetWindowThreadProcessId(GetForegroundWindow(), out var pid) == 0 ? 0 : pid;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref int length);

    // ---------- processes ----------
    [DllImport("kernel32.dll", SetLastError = true)] public static extern uint GetPriorityClass(SafeProcessHandle h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetPriorityClass(SafeProcessHandle h, uint priorityClass);
    /// <summary>The process's GPU scheduling priority class. NTSTATUS.</summary>
    [DllImport("gdi32.dll")] public static extern int D3DKMTSetProcessSchedulingPriorityClass(SafeProcessHandle h, int priority);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetProcessPriorityBoost(SafeProcessHandle h, out bool disabled);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetProcessPriorityBoost(SafeProcessHandle h, bool disable);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetProcessAffinityMask(SafeProcessHandle h, out UIntPtr process, out UIntPtr system);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetProcessAffinityMask(SafeProcessHandle h, UIntPtr mask);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetProcessDefaultCpuSets(SafeProcessHandle h, [Out] uint[]? ids, uint count, out uint required);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetProcessDefaultCpuSets(SafeProcessHandle h, uint[]? ids, uint count);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetProcessInformation(SafeProcessHandle h, int infoClass, ref ProcessPowerThrottling info, int size);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetProcessInformation(SafeProcessHandle h, int infoClass, ref ProcessPowerThrottling info, int size);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetProcessInformation(SafeProcessHandle h, int infoClass, ref uint info, int size);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetProcessInformation(SafeProcessHandle h, int infoClass, ref uint info, int size);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool TerminateProcess(SafeProcessHandle h, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool IsProcessCritical(SafeProcessHandle h, out bool critical);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageName(SafeProcessHandle h, int flags, StringBuilder name, ref int size);

    public const int ProcessMemoryPriority = 0, ProcessPowerThrottlingClass = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessPowerThrottling { public uint Version, ControlMask, StateMask; }

    [DllImport("ntdll.dll")] public static extern int NtQueryInformationProcess(SafeProcessHandle h, int infoClass, ref int info, int size, out int returned);
    [DllImport("ntdll.dll")] public static extern int NtSetInformationProcess(SafeProcessHandle h, int infoClass, ref int info, int size);
    [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(SafeProcessHandle h);
    [DllImport("ntdll.dll")] public static extern int NtResumeProcess(SafeProcessHandle h);
    public const int ProcessIoPriority = 33;

    [DllImport("ntdll.dll")] public static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int size, out int returned);
    public const int SystemProcessInformation = 5;
    public const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    // ---------- threads ----------
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int GetThreadPriority(SafeAccessTokenHandle h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetThreadPriority(SafeAccessTokenHandle h, int priority);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetThreadPriorityBoost(SafeAccessTokenHandle h, out bool disabled);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetThreadPriorityBoost(SafeAccessTokenHandle h, bool disable);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern UIntPtr SetThreadAffinityMask(SafeAccessTokenHandle h, UIntPtr mask);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool QueryThreadCycleTime(SafeAccessTokenHandle h, out ulong cycles);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetThreadIdealProcessorEx(SafeAccessTokenHandle h, out ProcessorNumber number);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetThreadIdealProcessorEx(SafeAccessTokenHandle h, ref ProcessorNumber number, out ProcessorNumber previous);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetThreadSelectedCpuSets(SafeAccessTokenHandle h, [Out] uint[]? ids, uint count, out uint required);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetThreadSelectedCpuSets(SafeAccessTokenHandle h, uint[]? ids, uint count);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetThreadInformation(SafeAccessTokenHandle h, int infoClass, ref uint info, int size);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetThreadInformation(SafeAccessTokenHandle h, int infoClass, ref uint info, int size);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern uint SuspendThread(SafeAccessTokenHandle h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern uint ResumeThread(SafeAccessTokenHandle h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool TerminateThread(SafeAccessTokenHandle h, uint exitCode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern int GetThreadDescription(SafeAccessTokenHandle h, out IntPtr description);
    [DllImport("kernel32.dll")] public static extern IntPtr LocalFree(IntPtr p);
    public const int ThreadMemoryPriority = 0;

    [DllImport("ntdll.dll")] public static extern int NtQueryInformationThread(SafeAccessTokenHandle h, int infoClass, ref int info, int size, out int returned);
    [DllImport("ntdll.dll")] public static extern int NtQueryInformationThread(SafeAccessTokenHandle h, int infoClass, ref IntPtr info, int size, out int returned);
    [DllImport("ntdll.dll")] public static extern int NtQueryInformationThread(SafeAccessTokenHandle h, int infoClass, ref ThreadBasicInformation info, int size, out int returned);
    [DllImport("ntdll.dll")] public static extern int NtSetInformationThread(SafeAccessTokenHandle h, int infoClass, ref int info, int size);
    public const int ThreadBasicInformationClass = 0, ThreadQuerySetWin32StartAddress = 9, ThreadIoPriority = 22;

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessorNumber { public ushort Group; public byte Number; public byte Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    public struct ThreadBasicInformation
    {
        public int ExitStatus;
        public IntPtr TebBaseAddress, UniqueProcess, UniqueThread;
        public UIntPtr AffinityMask;
        public int Priority, BasePriority;
    }

    // ---------- modules ----------
    [DllImport("psapi.dll", SetLastError = true)]
    public static extern bool EnumProcessModulesEx(SafeProcessHandle h, [Out] IntPtr[]? modules, int size, out int needed, uint filter);
    [DllImport("psapi.dll", SetLastError = true)]
    public static extern bool GetModuleInformation(SafeProcessHandle h, IntPtr module, out ModuleInfo info, int size);
    [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetModuleBaseName(SafeProcessHandle h, IntPtr module, StringBuilder name, int size);

    [StructLayout(LayoutKind.Sequential)]
    public struct ModuleInfo { public IntPtr BaseOfDll; public uint SizeOfImage; public IntPtr EntryPoint; }

    // ---------- system ----------
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetSystemCpuSetInformation(IntPtr info, int length, out int returned, IntPtr process, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    // ---------- privileges ----------
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool LookupPrivilegeValue(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disableAll, ref TokenPrivilege state, int length, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct TokenPrivilege { public int Count; public long Luid; public int Attributes; }

    /// <summary>Enables privileges the elevated token already has but disabled (SeDebug opens more processes; SeIncreaseBasePriority allows high I/O and Realtime).</summary>
    public static void EnablePrivileges(params string[] names)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out var token)) return; // ADJUST_PRIVILEGES | QUERY
        using (token)
            foreach (var name in names)
            {
                if (!LookupPrivilegeValue(null, name, out var luid)) continue;
                var tp = new TokenPrivilege { Count = 1, Luid = luid, Attributes = 2 }; // SE_PRIVILEGE_ENABLED
                AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
    }

    /// <summary>Win32 error message (or converted NTSTATUS) for the log and the interface.</summary>
    public static string Win32Message(int error) => new System.ComponentModel.Win32Exception(error).Message;

    [DllImport("ntdll.dll")] static extern int RtlNtStatusToDosError(int status);
    public static string NtMessage(int status) => Win32Message(RtlNtStatusToDosError(status));

    // ---------- notices to windows ----------
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);

    /// <summary>WM_SETTINGCHANGE to all windows (e.g. "ImmersiveColorSet" makes the taskbar and menus reread transparency).</summary>
    public static void BroadcastSettingChange(string area) =>
        SendMessageTimeout((IntPtr)0xFFFF, 0x001A, IntPtr.Zero, area, 0x0002, 1000, out _); // HWND_BROADCAST, SMTO_ABORTIFHUNG
}
