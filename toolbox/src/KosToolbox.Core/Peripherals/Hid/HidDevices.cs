using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using KosToolbox.Core.Persistence;

namespace KosToolbox.Core.Peripherals.Hid;

/// <summary>
/// One top-level HID collection. Windows gives each its own device path (<c>&amp;colNN</c> when an interface has several).
/// Read through a handle opened with access 0: attributes, caps and the product string, nothing else.
/// </summary>
/// <param name="Path">Device interface path, to open it. A Bluetooth path carries the device's address: never show or copy it.</param>
/// <param name="Version">bcdDevice (the REV_ of the hardware id).</param>
/// <param name="InputLength">Report byte lengths from HidP_GetCaps, report id included; 0 = the collection has no report of that kind.</param>
/// <param name="ContainerId">DEVPKEY_Device_ContainerId of the device node: the same for every collection of one physical device.</param>
/// <param name="Bluetooth">The HID node's parent is on the Bluetooth bus.</param>
public sealed record HidCollection(string Path, ushort VendorId, ushort ProductId, ushort Version, ushort UsagePage, ushort Usage,
    int InputLength, int OutputLength, int FeatureLength, string? Product, Guid? ContainerId, bool Bluetooth)
{
    /// <summary>USB interface number (<c>&amp;mi_xx</c>); -1 when the path has none.</summary>
    public int Interface => HidDevices.InterfaceOf(Path);
    /// <summary>Top-level collection number (<c>&amp;colNN</c>); -1 when the interface has a single collection.</summary>
    public int Collection => HidDevices.CollectionOf(Path);
}

/// <summary>A physical device: its collections in interface and collection order.</summary>
public sealed record HidDevice(string Key, IReadOnlyList<HidCollection> Collections)
{
    public ushort VendorId => Collections[0].VendorId;
    public ushort ProductId => Collections[0].ProductId;
    public ushort Version => Collections[0].Version;
    public bool Bluetooth => Collections[0].Bluetooth;
    public string? Product => Collections.Select(c => c.Product).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
    public PeripheralKind Kind => HidDevices.GuessKind(Collections);
}

/// <summary>
/// HID enumeration through SetupAPI and hid.dll (no WinRT, COM or reflection: the governor engine is AOT). Every collection is opened with
/// access 0 only to read what Windows already knows; nothing is sent to a device. FIDO security keys are skipped without being opened.
/// </summary>
public static class HidDevices
{
    public const ushort FidoUsagePage = 0xF1D0;
    // The container of devices built into the PC: every one of them has it, so it doesn't tell one device from another.
    static readonly Guid ComputerContainer = new("00000000-0000-0000-ffff-ffffffffffff");

    /// <summary>The HID devices connected now, one per physical device.</summary>
    public static List<HidDevice> List() => Group(Collections());

    /// <summary>Collections of the same physical device together (<see cref="GroupKey"/>), each device in interface and collection order.</summary>
    public static List<HidDevice> Group(IEnumerable<HidCollection> collections) => collections
        .GroupBy(GroupKey, StringComparer.OrdinalIgnoreCase)
        .Select(g => new HidDevice(g.Key, g.OrderBy(c => c.Interface).ThenBy(c => c.Collection).ToList()))
        .ToList();

    /// <summary>
    /// VID/PID and the container id; without a container id that identifies a device, the parent instance from the path instead (the
    /// collections of one interface: a device's interfaces then stay apart).
    /// </summary>
    public static string GroupKey(HidCollection c) => $"{c.VendorId:X4}:{c.ProductId:X4}#"
        + (c.ContainerId is { } id && id != Guid.Empty && id != ComputerContainer ? id.ToString("D") : ParentInstance(c.Path) ?? c.Path);

    // \\?\hid#vid_1532&pid_007b&mi_01&col02#8&317e7b51&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}: hardware part, instance, interface class.
    static string HardwarePart(string path) => path.Split('#') is { Length: >= 3 } p ? p[1] : "";

    public static int InterfaceOf(string path) => TwoHexDigits(HardwarePart(path), "&mi_");
    public static int CollectionOf(string path) => TwoHexDigits(HardwarePart(path), "&col");

    // Read as hex (as hidapi reads mi_) and shown back as two hex digits, so the path's digits come back the same either way.
    static int TwoHexDigits(string part, string tag)
    {
        var at = part.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
        return at >= 0 && part.Length >= at + tag.Length + 2
            && int.TryParse(part.AsSpan(at + tag.Length, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var n) ? n : -1;
    }

    /// <summary>The path's instance without its last field (<c>8&amp;317e7b51&amp;0</c>): shared by the collections of one interface.</summary>
    public static string? ParentInstance(string path) =>
        path.Split('#') is { Length: >= 3 } p && p[2].LastIndexOf('&') is var at and > 0 ? p[2][..at].ToLowerInvariant() : null;

    /// <summary>A FIDO security key collection, from the ids Windows gives the node (<c>HID_DEVICE_UP:F1D0_U:0001</c>).</summary>
    public static bool IsFido(IEnumerable<string> ids) => ids.Any(i => i.Contains("UP:F1D0_", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The first mouse, keyboard, headset or gamepad usage in interface and collection order: receivers and keyboards often carry both a
    /// mouse and a keyboard collection, and the first interface is the device's own.
    /// </summary>
    public static PeripheralKind GuessKind(IEnumerable<HidCollection> collections)
    {
        foreach (var c in collections.OrderBy(c => c.Interface).ThenBy(c => c.Collection))
            switch (c.UsagePage, c.Usage)
            {
                case (0x01, 0x02): return PeripheralKind.Mouse;
                case (0x01, 0x06): return PeripheralKind.Keyboard;
                case (0x01, 0x04) or (0x01, 0x05): return PeripheralKind.Controller; // joystick, gamepad
                case (0x0B, 0x05): return PeripheralKind.Headset; // telephony › headset
            }
        return PeripheralKind.Other;
    }

    /// <summary>Every present HID collection except FIDO ones; a path that can't be opened or read is left out.</summary>
    public static List<HidCollection> Collections()
    {
        HidNative.HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new IntPtr(-1)) throw new Win32Exception();
        var list = new List<HidCollection>();
        var unreadable = 0;
        try
        {
            var data = new InterfaceData { Size = (uint)Marshal.SizeOf<InterfaceData>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
            {
                var node = new DevInfoData { Size = (uint)Marshal.SizeOf<DevInfoData>() };
                if (InterfacePath(set, ref data, ref node) is not { } path) continue;
                if (IsFido(Strings(set, ref node, HardwareIds).Concat(Strings(set, ref node, CompatibleIds)))) continue; // never opened
                var container = Bytes(set, ref node, ContainerId) is { Length: 16 } g ? new Guid(g) : (Guid?)null;
                var bluetooth = Text(set, ref node, Parent)?.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) == true; // BTHENUM\, BTHLEDEVICE\
                try { if (Read(path, container, bluetooth) is { } c) list.Add(c); }
                catch (Win32Exception) { unreadable++; }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        if (unreadable > 0) Log.Info($"Peripherals: {unreadable} HID collection(s) couldn't be opened to read their attributes.");
        return list;
    }

    static HidCollection? Read(string path, Guid? container, bool bluetooth)
    {
        using var handle = HidNative.Open(path, 0, 0);
        var caps = HidNative.ReadCaps(handle);
        if (caps.UsagePage == FidoUsagePage) return null; // its ids didn't say so: closed before anything else is asked
        var a = HidNative.ReadAttributes(handle);
        return new HidCollection(path, a.VendorId, a.ProductId, a.Version, caps.UsagePage, caps.Usage,
            caps.InputLength, caps.OutputLength, caps.FeatureLength, HidNative.Product(handle), container, bluetooth);
    }

    // ---------- SetupAPI ----------
    const uint DigcfPresent = 0x2, DigcfDeviceInterface = 0x10;
    const int ErrorInsufficientBuffer = 122;

    [StructLayout(LayoutKind.Sequential)]
    struct DevInfoData { public uint Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    struct InterfaceData { public uint Size; public Guid ClassGuid; public uint Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    struct DevPropKey { public Guid Fmtid; public uint Pid; public DevPropKey(string g, uint p) { Fmtid = new Guid(g); Pid = p; } }

    static readonly DevPropKey HardwareIds = new("a45c254e-df1c-4efd-8020-67d146a850e0", 3);
    static readonly DevPropKey CompatibleIds = new("a45c254e-df1c-4efd-8020-67d146a850e0", 4);
    static readonly DevPropKey ContainerId = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c", 2);
    static readonly DevPropKey Parent = new("4340a6c5-93fa-4706-972c-7b648008a5a7", 8);

    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")]
    static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid interfaceGuid, uint index, ref InterfaceData data);

    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, int size, out int required, ref DevInfoData node);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiGetDevicePropertyW")]
    static extern bool SetupDiGetDeviceProperty(IntPtr set, ref DevInfoData data, ref DevPropKey key, out uint type,
        byte[]? buffer, int size, out int required, uint flags);

    static string? InterfacePath(IntPtr set, ref InterfaceData data, ref DevInfoData node)
    {
        SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var size, ref node);
        if (size <= 4) return null;
        var detail = Marshal.AllocHGlobal(size);
        try
        {
            // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W is its fixed part (8 on x64, 6 on x86), not the buffer; the path follows the DWORD.
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            return SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out _, ref node) ? Marshal.PtrToStringUni(detail + 4) : null;
        }
        finally { Marshal.FreeHGlobal(detail); }
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

    static string[] Strings(IntPtr set, ref DevInfoData data, DevPropKey key) =>
        Bytes(set, ref data, key) is { } b ? Encoding.Unicode.GetString(b).Split('\0', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
}
