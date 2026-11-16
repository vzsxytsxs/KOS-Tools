using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using KosToolbox.Core.Persistence;

namespace KosToolbox.Core.Devices;

/// <summary>Plug and Play device as SetupAPI enumerates it (the same as Device Manager shows).</summary>
public sealed record PnpDevice(string InstanceId, string Name, string Class, bool Present)
{
    public DateTime? LastArrival { get; init; }
    /// <summary>Bits of DEVPKEY_PciDevice_InterruptSupport: 1 = line (INTx), 2 = MSI, 4 = MSI-X. Null outside PCI or without data.</summary>
    public uint? InterruptSupport { get; init; }
    public uint? MessageMaximum { get; init; }
    /// <summary>Ghost devices only: an interrupt affinity was set for it (Check all leaves it unchecked).</summary>
    public bool HasAffinity { get; init; }
}

/// <summary>Enumeration and removal of devices through SetupAPI/newdev (no pnputil or PowerShell).</summary>
public static class PnpDevices
{
    const uint DigcfPresent = 0x2, DigcfAllClasses = 0x4;
    const int ErrorNoMoreItems = 259, ErrorInsufficientBuffer = 122;

    [StructLayout(LayoutKind.Sequential)]
    struct DevInfoData { public uint Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    struct DevPropKey { public Guid Fmtid; public uint Pid; public DevPropKey(string g, uint p) { Fmtid = new Guid(g); Pid = p; } }

    static readonly DevPropKey FriendlyName = new("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
    static readonly DevPropKey DeviceDesc = new("a45c254e-df1c-4efd-8020-67d146a850e0", 2);
    static readonly DevPropKey ClassName = new("a45c254e-df1c-4efd-8020-67d146a850e0", 9);
    static readonly DevPropKey LastArrivalDate = new("83da6326-97a6-4088-9453-a1923f573b29", 102);
    static readonly DevPropKey PciInterruptSupport = new("3ab22e31-8264-4b4e-9af5-a8d2d8e33e62", 14);
    static readonly DevPropKey PciMessageMaximum = new("3ab22e31-8264-4b4e-9af5-a8d2d8e33e62", 15);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")]
    static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string? enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DevInfoData data);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceInstanceIdW")]
    static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DevInfoData data, char[] buffer, int size, out int required);

    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiGetDevicePropertyW")]
    static extern bool SetupDiGetDeviceProperty(IntPtr set, ref DevInfoData data, ref DevPropKey key, out uint type,
        byte[]? buffer, int size, out int required, uint flags);

    [DllImport("newdev.dll", SetLastError = true)]
    static extern bool DiUninstallDevice(IntPtr parent, IntPtr set, ref DevInfoData data, uint flags, out bool needReboot);

    [StructLayout(LayoutKind.Sequential)]
    struct PropChangeParams { public uint HeaderSize, InstallFunction, StateChange, Scope, HwProfile; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DevInstallParams
    {
        public uint Size, Flags, FlagsEx;
        public IntPtr Parent, MsgHandler, MsgContext, FileQueue;
        public UIntPtr ClassInstallReserved;
        public uint Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DriverPath;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiSetClassInstallParams(IntPtr set, ref DevInfoData data, ref PropChangeParams parameters, int size);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiCallClassInstaller(uint function, IntPtr set, ref DevInfoData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceInstallParamsW")]
    static extern bool SetupDiGetDeviceInstallParams(IntPtr set, ref DevInfoData data, ref DevInstallParams parameters);

    /// <summary>
    /// Restarts a connected device so it re-reads its configuration (DIF_PROPERTYCHANGE/DICS_PROPCHANGE, like
    /// "devcon restart"). Returns true when Windows asks for a restart to finish. Requires administrator.
    /// </summary>
    public static bool Restart(string instanceId) => ChangeState(instanceId, 3);

    /// <summary>Disables (DICS_DISABLE) or enables (DICS_ENABLE) a device, as Device Manager does. Requires administrator.</summary>
    public static bool SetEnabled(string instanceId, bool enabled) => ChangeState(instanceId, enabled ? 1u : 2u);

    static bool ChangeState(string instanceId, uint stateChange)
    {
        const uint DifPropertyChange = 0x12, DicsFlagGlobal = 1, DiNeedRestart = 0x80, DiNeedReboot = 0x100;
        bool? reboot = null;
        ForEach(null, DigcfPresent, (set, data, id) =>
        {
            if (!id.Equals(instanceId, StringComparison.OrdinalIgnoreCase)) return true;
            var d = data;
            var p = new PropChangeParams { HeaderSize = 8, InstallFunction = DifPropertyChange, StateChange = stateChange, Scope = DicsFlagGlobal };
            if (!SetupDiSetClassInstallParams(set, ref d, ref p, Marshal.SizeOf<PropChangeParams>()) || !SetupDiCallClassInstaller(DifPropertyChange, set, ref d))
                throw new Win32Exception();
            var install = new DevInstallParams { Size = (uint)Marshal.SizeOf<DevInstallParams>(), DriverPath = "" };
            reboot = SetupDiGetDeviceInstallParams(set, ref d, ref install) && (install.Flags & (DiNeedRestart | DiNeedReboot)) != 0;
            return false;
        });
        Log.Info($"Device {(stateChange switch { 1 => "enabled", 2 => "disabled", _ => "restarted" })}: {instanceId} (reboot needed: {reboot})");
        return reboot ?? throw new InvalidOperationException($"No connected device has the id {instanceId}.");
    }

    /// <summary>Lists devices. <paramref name="enumerator"/> filters by bus (e.g. "PCI"); null = all.</summary>
    public static List<PnpDevice> Enumerate(string? enumerator = null, bool presentOnly = false)
    {
        var present = presentOnly ? null : InstanceIds(enumerator, DigcfPresent);
        var list = new List<PnpDevice>();
        ForEach(enumerator, presentOnly ? DigcfPresent : 0, (set, data, id) =>
        {
            var d = data;
            var name = Text(set, ref d, FriendlyName) ?? Text(set, ref d, DeviceDesc) ?? id;
            list.Add(new PnpDevice(id, name, Text(set, ref d, ClassName) ?? "", present?.Contains(id) ?? true)
            {
                LastArrival = Bytes(set, ref d, LastArrivalDate) is { Length: 8 } ft ? DateTime.FromFileTime(BitConverter.ToInt64(ft)) : null,
                InterruptSupport = UInt(set, ref d, PciInterruptSupport),
                MessageMaximum = UInt(set, ref d, PciMessageMaximum),
            });
            return true;
        });
        return list;
    }

    /// <summary>
    /// Removes devices that are not connected now (like "Uninstall device" in Device Manager,
    /// without deleting the driver). A present device is never removed. Requires administrator.
    /// </summary>
    public static List<(string Id, string? Error)> RemoveNotPresent(IReadOnlyCollection<string> ids, out bool rebootNeeded)
    {
        var wanted = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        var present = InstanceIds(null, DigcfPresent);
        var results = new List<(string, string?)>();
        var reboot = false;
        ForEach(null, 0, (set, data, id) =>
        {
            if (!wanted.Remove(id)) return true;
            if (present.Contains(id)) { results.Add((id, "The device is connected now; it wasn't removed.")); return true; }
            var d = data;
            if (DiUninstallDevice(IntPtr.Zero, set, ref d, 0, out var r))
            {
                reboot |= r;
                results.Add((id, null));
                Log.Info($"Device removed: {id}");
            }
            else
            {
                var err = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                results.Add((id, err));
                Log.Warn($"Device {id} wasn't removed: {err}");
            }
            return true;
        });
        results.AddRange(wanted.Select(id => (id, (string?)"The device no longer exists.")));
        rebootNeeded = reboot;
        return results;
    }

    /// <summary>Instance ids of the devices present now, in one cheap call (cfgmgr32); no names. Throws if the list can't be read.</summary>
    public static HashSet<string> PresentIds()
    {
        const uint FilterPresent = 0x100;
        const int BufferSmall = 0x1A; // CR_BUFFER_SMALL: a device arrived between the two calls
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_Size(out var length, null, FilterPresent) != 0) break;
            var buffer = new char[length];
            var cr = CM_Get_Device_ID_List(null, buffer, length, FilterPresent);
            if (cr == BufferSmall) continue;
            if (cr != 0) break;
            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        throw new InvalidOperationException("The list of present devices couldn't be read.");
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_ID_List_SizeW")]
    static extern int CM_Get_Device_ID_List_Size(out int length, string? filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_ID_ListW")]
    static extern int CM_Get_Device_ID_List(string? filter, [Out] char[] buffer, int length, uint flags);

    static HashSet<string> InstanceIds(string? enumerator, uint flags)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ForEach(enumerator, flags, (_, _, id) => { set.Add(id); return true; });
        return set;
    }

    static void ForEach(string? enumerator, uint flags, Func<IntPtr, DevInfoData, string, bool> visit)
    {
        var set = SetupDiGetClassDevs(IntPtr.Zero, enumerator, IntPtr.Zero, flags | DigcfAllClasses);
        if (set == new IntPtr(-1)) throw new Win32Exception();
        try
        {
            var data = new DevInfoData { Size = (uint)Marshal.SizeOf<DevInfoData>() };
            var buffer = new char[512];
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                if (!SetupDiGetDeviceInstanceId(set, ref data, buffer, buffer.Length, out var len)) continue;
                if (!visit(set, data, new string(buffer, 0, Math.Max(0, len - 1)))) return; // stop requested, not an error
            }
            var err = Marshal.GetLastWin32Error();
            if (err is not (0 or ErrorNoMoreItems)) Log.Warn($"Device enumeration stopped early: {new Win32Exception(err).Message}");
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    static byte[]? Bytes(IntPtr set, ref DevInfoData data, DevPropKey key)
    {
        if (SetupDiGetDeviceProperty(set, ref data, ref key, out _, null, 0, out var size, 0) || size == 0
            || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer) return null;
        var buffer = new byte[size];
        return SetupDiGetDeviceProperty(set, ref data, ref key, out _, buffer, size, out _, 0) ? buffer : null;
    }

    static string? Text(IntPtr set, ref DevInfoData data, DevPropKey key) =>
        Bytes(set, ref data, key) is { } b && Encoding.Unicode.GetString(b).TrimEnd('\0') is { Length: > 0 } s ? s : null;

    static uint? UInt(IntPtr set, ref DevInfoData data, DevPropKey key) =>
        Bytes(set, ref data, key) is { Length: 4 } b ? BitConverter.ToUInt32(b) : null;
}
