using System.Buffers.Binary;
using System.Runtime.InteropServices;
using KosToolbox.Core.SystemInfo;
using Microsoft.Win32.SafeHandles;

namespace KosToolbox.Core.Storage;

/// <summary>A drive's health; <see cref="Attributes"/> = the ATA SMART table (empty on NVMe).</summary>
public sealed record DiskHealthReport(string Summary, bool Warning, IReadOnlyList<InfoRow> Rows, IReadOnlyList<InfoRow> Attributes);

/// <summary>
/// Drive health like CrystalDiskInfo, without a driver and without administrator (a handle with no access rights is enough):
/// NVMe through its SMART/health log page (IOCTL_STORAGE_QUERY_PROPERTY, protocol-specific), SATA through the SMART
/// attribute table the storage driver returns with IOCTL_STORAGE_PREDICT_FAILURE.
/// </summary>
public static class DiskHealth
{
    const uint QueryProperty = 0x2D1400, PredictFailure = 0x2D1100;

    /// <summary>Null when the drive answers neither (USB enclosures, RAID, virtual disks).</summary>
    public static DiskHealthReport? Read(int disk)
    {
        using var h = CreateFile($@"\\.\PhysicalDrive{disk}", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero); // no access, FILE_SHARE_READ|WRITE, OPEN_EXISTING
        if (h.IsInvalid) return null;

        // STORAGE_PROPERTY_QUERY (StorageDeviceProtocolSpecificProperty, standard query) + STORAGE_PROTOCOL_SPECIFIC_DATA + room for the log.
        var buffer = new byte[8 + 40 + 512];
        BitConverter.GetBytes(50).CopyTo(buffer, 0);
        BitConverter.GetBytes(3).CopyTo(buffer, 8);    // ProtocolTypeNvme
        BitConverter.GetBytes(2).CopyTo(buffer, 12);   // NVMeDataTypeLogPage
        BitConverter.GetBytes(2).CopyTo(buffer, 16);   // log page 2: SMART / health information
        BitConverter.GetBytes(40).CopyTo(buffer, 24);  // ProtocolDataOffset (from the protocol data)
        BitConverter.GetBytes(512).CopyTo(buffer, 28); // ProtocolDataLength
        if (DeviceIoControl(h, QueryProperty, buffer, buffer.Length, buffer, buffer.Length, out _, IntPtr.Zero))
        {
            // STORAGE_PROTOCOL_DATA_DESCRIPTOR: Version, Size (48 each), then the protocol data with where the log landed.
            int version = BitConverter.ToInt32(buffer, 0), size = BitConverter.ToInt32(buffer, 4);
            int offset = BitConverter.ToInt32(buffer, 8 + 16), length = BitConverter.ToInt32(buffer, 8 + 20);
            if (version == 48 && size == 48 && offset >= 40 && length >= 512 && 8 + offset + 512 <= buffer.Length)
                return Nvme(buffer.AsSpan(8 + offset, 512));
        }

        var smart = new byte[4 + 512]; // STORAGE_PREDICT_FAILURE: PredictFailure, then the drive's SMART data
        return DeviceIoControl(h, PredictFailure, null, 0, smart, smart.Length, out var returned, IntPtr.Zero) && returned == smart.Length
            ? Ata(BitConverter.ToUInt32(smart, 0) != 0, smart.AsSpan(4))
            : null;
    }

    /// <summary>NVMe SMART / health information log (NVMe base specification, log page 02h).</summary>
    internal static DiskHealthReport Nvme(ReadOnlySpan<byte> log)
    {
        var warning = log[0];
        var kelvin = BinaryPrimitives.ReadUInt16LittleEndian(log[1..]);
        int spare = log[3], threshold = log[4], used = log[5];
        // 128-bit counters: the low 64 bits are plenty.
        var counters = log.ToArray();
        ulong U(int at) => BinaryPrimitives.ReadUInt64LittleEndian(counters.AsSpan(at));
        var media = U(160);
        var problems = new List<string>();
        string[] bits = { "spare below its threshold", "temperature past its limit", "reliability degraded", "read-only", "backup power failed", "persistent memory read-only" };
        for (var i = 0; i < bits.Length; i++) if ((warning & (1 << i)) != 0) problems.Add(bits[i]);
        if (media > 0) problems.Add($"{media:N0} media errors");
        if (used >= 90) problems.Add($"{used} % of its rated endurance used");
        var rows = new List<InfoRow>
        {
            new("Health", problems.Count == 0 ? "Good" : string.Join(", ", problems)),
            new("Temperature", kelvin > 0 ? $"{kelvin - 273} °C" : "—"),
            new("Wear", $"{used} % of the rated endurance used"),
            new("Available spare", $"{spare} % (warns below {threshold} %)"),
            new("Data written", Bytes(U(48) * 512_000)), // data units are 1000 sectors of 512 bytes
            new("Data read", Bytes(U(32) * 512_000)),
            new("Power-on hours", $"{U(128):N0} h"),
            new("Power cycles", $"{U(112):N0}"),
            new("Unsafe shutdowns", $"{U(144):N0}"),
            new("Media errors", $"{media:N0}"),
            new("Error log entries", $"{U(176):N0}"),
        };
        return new DiskHealthReport(rows[0].Value, problems.Count > 0, rows, Array.Empty<InfoRow>());
    }

    /// <summary>ATA SMART data: a revision word, then 30 attributes of 12 bytes (id, flags, value, worst, 6-byte raw, reserved).</summary>
    internal static DiskHealthReport Ata(bool failing, ReadOnlySpan<byte> data)
    {
        var attributes = new Dictionary<byte, (byte Value, byte Worst, ulong Raw)>();
        var list = new List<InfoRow>();
        for (var i = 0; i < 30; i++)
        {
            var a = data.Slice(2 + i * 12, 12);
            if (a[0] == 0 || attributes.ContainsKey(a[0])) continue;
            ulong raw = 0;
            for (var b = 0; b < 6; b++) raw |= (ulong)a[5 + b] << (8 * b);
            attributes[a[0]] = (a[3], a[4], raw);
            list.Add(new InfoRow($"{a[0]:X2} {Names.GetValueOrDefault(a[0], "Vendor specific")}", $"{raw:N0} · value {a[3]}, worst {a[4]}"));
        }
        ulong? Raw(params byte[] ids) => ids.Select(id => attributes.TryGetValue(id, out var x) ? x.Raw : (ulong?)null).FirstOrDefault(r => r is not null);
        // Normalized value of the first life attribute the drive has (100 = new).
        byte? life = new byte[] { 0xE7, 0xCA, 0xB1, 0xE9, 0xA9 }.Select(id => attributes.TryGetValue(id, out var x) ? x.Value : (byte?)null).FirstOrDefault(v => v is not null);
        ulong reallocated = Raw(0x05) ?? 0, pending = Raw(0xC5) ?? 0, uncorrectable = Raw(0xC6) ?? 0;
        var problems = new List<string>();
        if (failing) problems.Add("the drive reports it is failing");
        if (reallocated > 0) problems.Add($"{reallocated:N0} reallocated sectors");
        if (pending > 0) problems.Add($"{pending:N0} pending sectors");
        if (uncorrectable > 0) problems.Add($"{uncorrectable:N0} uncorrectable sectors");
        if (life <= 10) problems.Add($"{life} % life left");
        var rows = new List<InfoRow> { new("Health", problems.Count == 0 ? "Good" : string.Join(", ", problems)) };
        if (Raw(0xC2, 0xBE) is { } temp) rows.Add(new("Temperature", $"{temp & 0xFF} °C")); // the other raw bytes hold min/max on some drives
        if (life is { } l) rows.Add(new("Life left", $"{l} % (the drive's estimate)"));
        // shortcut: a few old drives count minutes or half-hours here; CrystalDiskInfo keeps a per-model table for them.
        if (Raw(0x09) is { } hours) rows.Add(new("Power-on hours", $"{hours & 0xFFFFFFFF:N0} h"));
        if (Raw(0x0C) is { } cycles) rows.Add(new("Power cycles", $"{cycles:N0}"));
        if (Raw(0xC0, 0xAE) is { } unsafeOff) rows.Add(new("Unsafe shutdowns", $"{unsafeOff:N0}"));
        rows.Add(new("Reallocated sectors", $"{reallocated:N0}"));
        if (Raw(0xC5) is not null) rows.Add(new("Pending sectors", $"{pending:N0}"));
        if (Raw(0xC6) is not null) rows.Add(new("Uncorrectable sectors", $"{uncorrectable:N0}"));
        if (Raw(0xC7) is { } crc) rows.Add(new("Cable (CRC) errors", $"{crc:N0}"));
        return new DiskHealthReport(rows[0].Value, problems.Count > 0, rows, list);
    }

    static string Bytes(ulong b) => b >= 1_000_000_000_000 ? $"{b / 1e12:0.00} TB" : $"{b / 1e9:0.0} GB";

    static readonly Dictionary<byte, string> Names = new()
    {
        [0x01] = "Read error rate", [0x02] = "Throughput performance", [0x03] = "Spin-up time", [0x04] = "Start/stop count",
        [0x05] = "Reallocated sectors", [0x07] = "Seek error rate", [0x08] = "Seek time performance", [0x09] = "Power-on hours",
        [0x0A] = "Spin retry count", [0x0C] = "Power cycles", [0xA9] = "Remaining life", [0xAB] = "Program fail count",
        [0xAC] = "Erase fail count", [0xAD] = "Average erase count", [0xAE] = "Unexpected power loss", [0xB1] = "Wear leveling count",
        [0xB3] = "Used reserved blocks", [0xB5] = "Program fail count", [0xB6] = "Erase fail count", [0xB7] = "SATA downshift errors",
        [0xB8] = "End-to-end errors", [0xBB] = "Reported uncorrectable errors", [0xBC] = "Command timeouts", [0xBD] = "High fly writes",
        [0xBE] = "Airflow temperature", [0xC0] = "Power-off retracts", [0xC1] = "Load cycles", [0xC2] = "Temperature",
        [0xC3] = "Hardware ECC recovered", [0xC4] = "Reallocation events", [0xC5] = "Pending sectors", [0xC6] = "Uncorrectable sectors",
        [0xC7] = "Cable (CRC) errors", [0xCA] = "Lifetime (vendor)", [0xE7] = "SSD life left", [0xE8] = "Available reserved space",
        [0xE9] = "Media wearout", [0xF0] = "Head flying hours", [0xF1] = "Total writes", [0xF2] = "Total reads",
    };

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[]? input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
}
