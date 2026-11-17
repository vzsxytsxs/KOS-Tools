using System.Runtime.InteropServices;

namespace KosToolbox.Core.Peripherals.Batteries;

/// <summary>
/// The battery Windows already knows for Bluetooth devices (what Settings › Bluetooth shows): property
/// {104EA319-6EE2-4701-BD47-8DDBF425BBE5},2 (BYTE) on the BTHENUM\, BTHLE\ and BTHLEDEVICE\ nodes, keyed by container id so it can be
/// matched to the HID device. Nothing is sent to any device. Property ids from Wireless Battery (src/drivers/generic.cpp).
/// </summary>
public static class BluetoothBattery
{
    const uint DigcfPresent = 0x2, DigcfAllClasses = 0x4;
    static readonly DevPropKey Battery = new("104ea319-6ee2-4701-bd47-8ddbf425bbe5", 2);
    static readonly DevPropKey ContainerId = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c", 2);
    static readonly DevPropKey ProblemCode = new("4340a6c5-93fa-4706-972c-7b648008a5a7", 3);

    /// <summary>Container id → percent, for every present Bluetooth node without a problem that reports one.</summary>
    public static Dictionary<Guid, int> ByContainer()
    {
        var result = new Dictionary<Guid, int>();
        foreach (var enumerator in new[] { "BTHENUM", "BTHLE", "BTHLEDEVICE" })
        {
            var set = SetupDiGetClassDevs(IntPtr.Zero, enumerator, IntPtr.Zero, DigcfPresent | DigcfAllClasses);
            if (set == new IntPtr(-1)) continue;
            try
            {
                var node = new DevInfoData { Size = (uint)Marshal.SizeOf<DevInfoData>() };
                for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref node); i++)
                {
                    if (Get(set, ref node, ProblemCode, 4) is { } problem && BitConverter.ToUInt32(problem) != 0) continue;
                    if (Get(set, ref node, Battery, 1) is not [var percent and <= 100]) continue;
                    if (Get(set, ref node, ContainerId, 16) is { } id) result[new Guid(id)] = percent;
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }
        return result;
    }

    static byte[]? Get(IntPtr set, ref DevInfoData node, DevPropKey key, int size)
    {
        var buffer = new byte[size];
        return SetupDiGetDeviceProperty(set, ref node, ref key, out _, buffer, size, out var used, 0) && used == size ? buffer : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DevInfoData { public uint Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    struct DevPropKey { public Guid Fmtid; public uint Pid; public DevPropKey(string g, uint p) { Fmtid = new Guid(g); Pid = p; } }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetClassDevsW")]
    static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DevInfoData data);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiGetDevicePropertyW")]
    static extern bool SetupDiGetDeviceProperty(IntPtr set, ref DevInfoData data, ref DevPropKey key, out uint type, byte[] buffer, int size, out int required, uint flags);
}
