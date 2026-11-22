using System.Runtime.InteropServices;

namespace KosToolbox.Core.Devices;

/// <summary>
/// Interrupts a device has now (its allocated resources, as Device Manager › Resources shows them).
/// </summary>
/// <param name="Count">Interrupt resources: one per MSI/MSI-X message, or the line IRQs.</param>
/// <param name="Lines">IRQ numbers of a line-based device; empty with MSI.</param>
/// <param name="Affinity">Union of the processors the interrupts may go to (group 0); 0 = not known (raw MSI without a translated list).</param>
public sealed record IrqState(int Count, bool MessageSignaled, IReadOnlyList<uint> Lines, ulong Affinity);

/// <summary>Reads the allocated IRQ resources of a device through cfgmgr32 (no administrator needed).</summary>
public static class IrqResources
{
    const uint AllocLogConf = 2, ResTypeIrq = 4;
    const int CrSuccess = 0;

    /// <summary>The interrupts in effect; null when the device isn't found. A device without interrupts has Count 0.</summary>
    public static IrqState? Read(string instanceId)
    {
        if (CM_Locate_DevNodeW(out var devInst, instanceId, 0) != CrSuccess) return null;
        var descriptors = new List<byte[]>();
        if (CM_Get_First_Log_Conf(out var logConf, devInst, AllocLogConf) != CrSuccess) return Parse(descriptors); // no resources assigned
        try
        {
            var current = logConf;
            while (CM_Get_Next_Res_Des(out var resDes, current, ResTypeIrq, out _, 0) == CrSuccess)
            {
                if (current != logConf) CM_Free_Res_Des_Handle(current);
                current = resDes;
                if (CM_Get_Res_Des_Data_Size(out var size, resDes, 0) != CrSuccess || size == 0) continue;
                var data = new byte[size];
                if (CM_Get_Res_Des_Data(resDes, data, size, 0) == CrSuccess) descriptors.Add(data);
            }
            if (current != logConf) CM_Free_Res_Des_Handle(current);
        }
        finally { CM_Free_Log_Conf_Handle(logConf); }
        var state = Parse(descriptors);
        // cfgmgr returns the raw resources, where an MSI message's affinity can be 0; the translated list has the processors used.
        return state.Count > 0 && Translated(devInst) is { } list && TranslatedAffinity(list) is { } translated ? state with { Affinity = translated } : state;
    }

    /// <summary>
    /// IRQ_DES_64 (x64): Count @0, Type @4, Flags @8, Alloc_Num @12, Affinity (ULONG64) @16 (raw resources).
    /// MSI messages have Alloc_Num above 0x7FFFFFFF (Device Manager's 4294967xxx), one descriptor per message.
    /// </summary>
    internal static IrqState Parse(IEnumerable<byte[]> descriptors)
    {
        int count = 0, msi = 0;
        var lines = new List<uint>();
        ulong affinity = 0;
        foreach (var d in descriptors)
        {
            if (d.Length < 24) continue;
            var number = BitConverter.ToUInt32(d, 12);
            count++;
            affinity |= BitConverter.ToUInt64(d, 16);
            if ((int)number < 0) msi++;
            else if (!lines.Contains(number)) lines.Add(number);
        }
        return new IrqState(count, count > 0 && msi == count, lines, affinity);
    }

    /// <summary>
    /// Union of the group-0 interrupt affinities in a CM_RESOURCE_LIST (REG_RESOURCE_LIST): Count @0, then full descriptors
    /// (InterfaceType, BusNumber, Version, Revision, Count = 16 bytes) of 20-byte partial descriptors: Type @0 (2 = interrupt),
    /// Level @4, Group @6, Vector @8, Affinity @12. Null when the list has no interrupt or doesn't parse.
    /// </summary>
    internal static ulong? TranslatedAffinity(byte[] list)
    {
        const int Interrupt = 2, DeviceSpecific = 5;
        if (list.Length < 4) return null;
        ulong affinity = 0;
        var any = false;
        var offset = 4;
        for (var full = BitConverter.ToUInt32(list, 0); full > 0; full--)
        {
            if (offset + 16 > list.Length) return null;
            var partials = BitConverter.ToUInt32(list, offset + 12);
            offset += 16;
            for (; partials > 0; partials--)
            {
                if (offset + 20 > list.Length) return null;
                if (list[offset] == Interrupt && BitConverter.ToUInt16(list, offset + 6) == 0)
                {
                    affinity |= BitConverter.ToUInt64(list, offset + 12);
                    any = true;
                }
                var extra = list[offset] == DeviceSpecific ? BitConverter.ToUInt32(list, offset + 4) : 0u;
                if (extra > (uint)(list.Length - offset - 20)) return null;
                offset += 20 + (int)extra;
            }
        }
        return any ? affinity : null;
    }

    /// <summary>The device's translated resources from HARDWARE\RESOURCEMAP (by its PDO name); null when absent.</summary>
    static byte[]? Translated(uint devInst)
    {
        var key = new DevPropKey { Fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Pid = 16 }; // DEVPKEY_Device_PDOName
        var size = 512;
        var name = new byte[size];
        if (CM_Get_DevNode_PropertyW(devInst, ref key, out _, name, ref size, 0) != CrSuccess) return null;
        var pdo = System.Text.Encoding.Unicode.GetString(name, 0, size).TrimEnd('\0');
        var hklm = new IntPtr(unchecked((int)0x80000002));
        const string Map = @"HARDWARE\RESOURCEMAP\PnP Manager\PnpManager";
        var length = 0;
        if (RegGetValueW(hklm, Map, pdo + ".Translated", 0xFFFF, out _, null, ref length) != 0 || length == 0) return null;
        var data = new byte[length];
        return RegGetValueW(hklm, Map, pdo + ".Translated", 0xFFFF, out _, data, ref length) == 0 ? data[..length] : null;
    }

    [StructLayout(LayoutKind.Sequential)] struct DevPropKey { public Guid Fmtid; public uint Pid; }

    /// <summary>
    /// True when the registry asks for something the allocated interrupts don't show yet: MSISupported against the mode
    /// (only for devices that report MSI support), or "Specified processors" against the processors in use.
    /// </summary>
    public static bool Pending(InterruptConfig config, IrqState? state, uint? support, ulong allMask)
    {
        if (state is not { Count: > 0 }) return false;
        if (config.Msi is { } msi && (support & 6) is > 0 && (msi == 1) != state.MessageSignaled) return true;
        if (config.Policy == Interrupts.PolicySpecified && config.Mask is { } mask)
        {
            var used = state.Affinity & allMask;
            return used != 0 && (used & ~mask) != 0; // 0: the processors aren't known, so nothing is claimed
        }
        return false;
    }

    /// <summary>Instance id of the parent device; null at the root or when it can't be read.</summary>
    public static string? Parent(string instanceId)
    {
        if (CM_Locate_DevNodeW(out var devInst, instanceId, 0) != CrSuccess || CM_Get_Parent(out var parent, devInst, 0) != CrSuccess) return null;
        var buffer = new char[512];
        return CM_Get_Device_IDW(parent, buffer, buffer.Length, 0) == CrSuccess ? new string(buffer).TrimEnd('\0') : null;
    }

    /// <summary>
    /// PCI USB host controllers with a present mouse or keyboard below them (walking up the device tree from each one).
    /// </summary>
    public static HashSet<string> InputControllers()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in PnpDevices.Enumerate(presentOnly: true).Where(d => d.Class is "Mouse" or "Keyboard"))
        {
            for (var (id, depth) = (Parent(d.InstanceId), 0); id is not null && depth < 16; id = Parent(id), depth++)
                if (id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)) { result.Add(id); break; }
        }
        return result;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_First_Log_Conf(out IntPtr logConf, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_Next_Res_Des(out IntPtr resDes, IntPtr current, uint forResource, out uint resourceId, uint flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_Res_Des_Data_Size(out int size, IntPtr resDes, uint flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_Res_Des_Data(IntPtr resDes, [Out] byte[] buffer, int size, uint flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Free_Res_Des_Handle(IntPtr resDes);
    [DllImport("cfgmgr32.dll")] static extern int CM_Free_Log_Conf_Handle(IntPtr logConf);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey key, out uint type, [Out] byte[] buffer, ref int size, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int RegGetValueW(IntPtr hkey, string subKey, string value, uint flags, out uint type, [Out] byte[]? data, ref int size);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_IDW(uint devInst, [Out] char[] buffer, int length, uint flags);
}
