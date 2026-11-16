using System.Runtime.InteropServices;
using System.Text;

namespace KosToolbox.Core.Display;

/// <summary>A monitor's DDC/CI answer: its capability string, the VCP codes it lists (with their allowed values, if any) and the values read now.</summary>
public sealed record DdcState(string Capabilities, IReadOnlyDictionary<byte, IReadOnlyList<uint>?> Codes, IReadOnlyDictionary<byte, (uint Current, uint Max)> Values);

/// <summary>
/// Monitor controls over DDC/CI (dxva2, no driver): brightness, contrast, input source and factory reset, like ClickMonitorDDC.
/// Microsoft warns that monitors which don't fully implement MCCS may misbehave, so only codes the capability string lists are
/// read or written, and every write is read back.
/// </summary>
public static class Ddc
{
    public const byte Restore = 0x04, Brightness = 0x10, Contrast = 0x12, Input = 0x60;
    static readonly byte[] Readable = { Brightness, Contrast, Input };

    /// <summary>Null when the monitor doesn't answer DDC/CI (off in its menu, a laptop panel, some docks and adapters). Takes up to a few seconds.</summary>
    public static DdcState? Read(string gdiName) => WithMonitor(gdiName, h =>
    {
        if (!GetCapabilitiesStringLength(h, out var length) || length == 0) return null;
        var buffer = new StringBuilder((int)length);
        if (!CapabilitiesRequestAndCapabilitiesReply(h, buffer, length)) return null;
        var caps = buffer.ToString();
        var codes = ParseVcp(caps);
        var values = new Dictionary<byte, (uint, uint)>();
        foreach (var code in Readable.Where(codes.ContainsKey))
            if (GetVCPFeatureAndVCPFeatureReply(h, code, IntPtr.Zero, out var current, out var max)) values[code] = (current, max);
        return new DdcState(caps, codes, values);
    });

    /// <summary>Writes a listed code and returns what the monitor reports afterwards (null when it can't be read back).</summary>
    public static uint? Write(string gdiName, byte code, uint value) => WithMonitor(gdiName, h =>
    {
        if (code is not (Brightness or Contrast or Input or Restore)) throw new ArgumentOutOfRangeException(nameof(code));
        if (!SetVCPFeature(h, code, value)) throw new InvalidOperationException($"The monitor refused the change ({Marshal.GetLastWin32Error()}).");
        if (code == Restore) return (uint?)value;
        Thread.Sleep(100); // MCCS asks for a pause before the next request
        return GetVCPFeatureAndVCPFeatureReply(h, code, IntPtr.Zero, out var current, out _) ? current : null;
    });

    /// <summary>The <c>vcp(...)</c> part of a capability string: each code in hex, with its allowed values in brackets when given.</summary>
    internal static Dictionary<byte, IReadOnlyList<uint>?> ParseVcp(string caps)
    {
        var result = new Dictionary<byte, IReadOnlyList<uint>?>();
        var start = caps.IndexOf("vcp(", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return result;
        var i = start + 4;
        byte? last = null;
        for (var depth = 1; i < caps.Length && depth > 0;)
        {
            var c = caps[i];
            if (c == '(')
            {
                var close = caps.IndexOf(')', i);
                if (close < 0) break;
                if (last is { } code)
                    result[code] = caps[(i + 1)..close].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Select(v => uint.TryParse(v, System.Globalization.NumberStyles.HexNumber, null, out var n) ? n : (uint?)null).OfType<uint>().ToList();
                i = close + 1;
                continue;
            }
            if (c == ')') { depth--; i++; continue; }
            if (char.IsAsciiHexDigit(c))
            {
                var end = i;
                while (end < caps.Length && char.IsAsciiHexDigit(caps[end])) end++;
                if (end - i == 2 && byte.TryParse(caps[i..end], System.Globalization.NumberStyles.HexNumber, null, out var b)) { result[b] = null; last = b; }
                i = end;
                continue;
            }
            i++;
        }
        return result;
    }

    /// <summary>Input source names from VESA MCCS (code 60h); vendors add their own numbers.</summary>
    public static string InputName(uint value) => value switch
    {
        0x01 => "VGA 1", 0x02 => "VGA 2", 0x03 => "DVI 1", 0x04 => "DVI 2", 0x05 => "Composite 1", 0x06 => "Composite 2",
        0x07 => "S-Video 1", 0x08 => "S-Video 2", 0x0C => "Component 1", 0x0D => "Component 2", 0x0E => "Component 3",
        0x0F => "DisplayPort 1", 0x10 => "DisplayPort 2", 0x11 => "HDMI 1", 0x12 => "HDMI 2", 0x1B => "USB-C",
        _ => $"Input {value:X2}h",
    };

    static T? WithMonitor<T>(string gdiName, Func<IntPtr, T?> action)
    {
        IntPtr found = IntPtr.Zero;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (m, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(m, ref info) && string.Equals(info.Device, gdiName, StringComparison.OrdinalIgnoreCase)) { found = m; return false; }
            return true;
        }, IntPtr.Zero);
        if (found == IntPtr.Zero || !GetNumberOfPhysicalMonitorsFromHMONITOR(found, out var count) || count == 0) return default;
        var monitors = new PhysicalMonitor[count];
        if (!GetPhysicalMonitorsFromHMONITOR(found, count, monitors)) return default;
        try { return action(monitors[0].Handle); } // a duplicated (cloned) desktop lists more than one; the first is the one we show
        finally { DestroyPhysicalMonitors(count, monitors); }
    }

    delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MonitorInfoEx
    {
        public int Size;
        public int L1, T1, R1, B1, L2, T2, R2, B2;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PhysicalMonitor
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
    [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
    [DllImport("dxva2.dll")] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll")] static extern bool DestroyPhysicalMonitors(uint count, PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll")] static extern bool GetCapabilitiesStringLength(IntPtr monitor, out uint length);
    [DllImport("dxva2.dll", CharSet = CharSet.Ansi)] static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr monitor, StringBuilder caps, uint length);
    [DllImport("dxva2.dll")] static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr monitor, byte code, IntPtr type, out uint current, out uint max);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool SetVCPFeature(IntPtr monitor, byte code, uint value);
}
