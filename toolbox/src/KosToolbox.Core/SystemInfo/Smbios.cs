using System.Runtime.InteropServices;
using System.Text;

namespace KosToolbox.Core.SystemInfo;

/// <summary>A structure from the SMBIOS table: the formatted part and its strings (index byte 1 = first string).</summary>
public sealed class SmbiosStructure(byte type, byte[] data, IReadOnlyList<string> strings)
{
    public byte Type { get; } = type;
    public int Length => data.Length;
    public byte? Byte(int offset) => offset < data.Length ? data[offset] : null;
    public ushort? Word(int offset) => offset + 2 <= data.Length ? BitConverter.ToUInt16(data, offset) : null;
    public uint? Dword(int offset) => offset + 4 <= data.Length ? BitConverter.ToUInt32(data, offset) : null;
    public ulong? Qword(int offset) => offset + 8 <= data.Length ? BitConverter.ToUInt64(data, offset) : null;

    /// <summary>The string pointed to by the byte at <paramref name="offset"/>; null when absent, empty or outside the list.</summary>
    public string? Text(int offset) => Byte(offset) is { } i and > 0 && i <= strings.Count && strings[i - 1].Trim() is { Length: > 0 } s ? s : null;
}

/// <summary>The firmware's SMBIOS table (GetSystemFirmwareTable 'RSMB'), without a driver.</summary>
public static class Smbios
{
    public static IReadOnlyList<SmbiosStructure> Read()
    {
        var size = GetSystemFirmwareTable(0x52534D42, 0, null, 0); // 'RSMB'
        if (size == 0) return Array.Empty<SmbiosStructure>();
        var data = new byte[size];
        GetSystemFirmwareTable(0x52534D42, 0, data, size);
        return Parse(data);
    }

    /// <summary>RawSMBIOSData (8-byte header) → structures; stops at type 127 (end) or at a truncated structure.</summary>
    internal static List<SmbiosStructure> Parse(byte[] raw)
    {
        var list = new List<SmbiosStructure>();
        if (raw.Length < 8) return list;
        var end = Math.Min(raw.Length, 8 + BitConverter.ToInt32(raw, 4));
        for (var p = 8; p + 4 <= end;)
        {
            var type = raw[p];
            var length = raw[p + 1];
            if (length < 4 || p + length > end) break;
            // After the formatted part: 0-terminated strings and one extra 0 at the end (no strings: 00 00).
            var strings = new List<string>();
            var q = p + length;
            if (q < end && raw[q] == 0) q += 2;
            else
            {
                while (q < end && raw[q] != 0)
                {
                    var start = q;
                    while (q < end && raw[q] != 0) q++;
                    strings.Add(Encoding.ASCII.GetString(raw, start, q - start));
                    q++;
                }
                q++;
            }
            list.Add(new SmbiosStructure(type, raw[p..(p + length)], strings));
            if (type == 127) break;
            p = q;
        }
        return list;
    }

    [DllImport("kernel32.dll")] static extern uint GetSystemFirmwareTable(uint provider, uint id, byte[]? buffer, uint size);
}
