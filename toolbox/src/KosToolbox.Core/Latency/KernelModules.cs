using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using KosToolbox.Core.Persistence;

namespace KosToolbox.Core.Latency;

/// <summary>Loaded kernel modules (drivers) by address, to name the driver a DPC or ISR routine belongs to.</summary>
public sealed class KernelModules
{
    public const string Unknown = "(unknown driver)";
    readonly (ulong Base, ulong End, string Name)[] _modules;

    public KernelModules(IEnumerable<(ulong Base, uint Size, string Name)> modules) =>
        _modules = modules.Where(m => m.Base != 0).Select(m => (m.Base, m.Base + m.Size, m.Name)).OrderBy(m => m.Base).ToArray();

    public int Count => _modules.Length;

    public string Find(ulong address)
    {
        int lo = 0, hi = _modules.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var m = _modules[mid];
            if (address < m.Base) hi = mid - 1;
            else if (address >= m.End) lo = mid + 1;
            else return m.Name;
        }
        return Unknown;
    }

    /// <summary>
    /// SystemModuleInformation. Windows only gives kernel addresses to a process with SeDebugPrivilege enabled (24H2 and
    /// later), so it is enabled for the read and disabled again (<paramref name="keepDebug"/>: left on, for a process that needs it
    /// anyway, like the governor's engine); without it every routine is <see cref="Unknown"/>.
    /// </summary>
    public static KernelModules Read(bool keepDebug = false)
    {
        var debug = false;
        try { Process.EnterDebugMode(); debug = true; }
        catch (Exception ex) { Log.Warn($"Latency: SeDebugPrivilege couldn't be enabled ({ex.Message}); driver names may be missing."); }
        try
        {
            var size = 256 * 1024;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var buffer = new byte[size];
                var status = NtQuerySystemInformation(11, buffer, buffer.Length, out var needed);
                if (status == 0) return new KernelModules(Parse(buffer));
                if (status != InfoLengthMismatch) throw new InvalidOperationException($"NtQuerySystemInformation: 0x{status:X8}");
                size = Math.Max(size * 2, needed + 4096);
            }
            throw new InvalidOperationException("The module list kept growing.");
        }
        finally { if (debug && !keepDebug) Process.LeaveDebugMode(); }
    }

    const int InfoLengthMismatch = unchecked((int)0xC0000004);
    const int EntrySize = 296; // RTL_PROCESS_MODULE_INFORMATION x64

    /// <summary>RTL_PROCESS_MODULES (x64): count, then entries from offset 8 with ImageBase at 16, ImageSize at 24, the file name offset at 38 and the path at 40.</summary>
    internal static List<(ulong Base, uint Size, string Name)> Parse(ReadOnlySpan<byte> data)
    {
        var list = new List<(ulong, uint, string)>();
        if (data.Length < 8) return list;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data);
        for (var i = 0; i < count && 8 + (i + 1) * EntrySize <= data.Length; i++)
        {
            var e = data.Slice(8 + i * EntrySize, EntrySize);
            var path = e.Slice(40, 256);
            var nameAt = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(e[38..]), (ushort)255);
            var name = path[nameAt..];
            var end = name.IndexOf((byte)0);
            list.Add((BinaryPrimitives.ReadUInt64LittleEndian(e[16..]), BinaryPrimitives.ReadUInt32LittleEndian(e[24..]),
                Encoding.ASCII.GetString(end < 0 ? name : name[..end])));
        }
        return list;
    }

    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int infoClass, byte[] buffer, int size, out int returned);
}
