using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KosToolbox.Core.Storage;

/// <summary>Whether a volume can use BypassIO (the DirectStorage read path) and, if not, the driver that blocks it.</summary>
public sealed record BypassIoState(bool Supported, string? Driver, string? Reason);

/// <summary>
/// FSCTL_MANAGE_BYPASS_IO with FS_BPIO_OP_QUERY on the volume's root folder (allowed from user mode, no administrator). Every
/// filter and the storage driver answer; the first one that can't names itself and says why, as <c>fsutil bypassIo state</c> does.
/// </summary>
public static class BypassIo
{
    const uint ManageBypassIo = 0x90448; // CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 274, METHOD_BUFFERED, FILE_ANY_ACCESS)
    const int OpQuery = 3;

    /// <summary>Null when Windows doesn't know BypassIO (before Windows 11) or the folder can't be opened.</summary>
    public static BypassIoState? Query(string root)
    {
        // No access rights; backup semantics opens a folder, and no buffering, because a cached handle is refused by NTFS itself.
        using var h = CreateFile(root, 0, 7, IntPtr.Zero, 3, 0x02000000 | 0x20000000, IntPtr.Zero);
        if (h.IsInvalid) return null;
        var input = new byte[24]; // FS_BPIO_INPUT: Operation, InFlags, two reserved ULONGLONGs
        BitConverter.GetBytes(OpQuery).CopyTo(input, 0);
        var output = new byte[352]; // FS_BPIO_OUTPUT with FS_BPIO_RESULTS
        return DeviceIoControl(h, ManageBypassIo, input, input.Length, output, output.Length, out var returned, IntPtr.Zero) && returned >= 24 + 328
            ? Parse(output)
            : null;
    }

    /// <summary>FS_BPIO_OUTPUT: Operation, OutFlags, 2 × ULONGLONG, then FS_BPIO_RESULTS (OpStatus, name length + WCHAR[32], reason length + WCHAR[128]).</summary>
    internal static BypassIoState Parse(byte[] o)
    {
        var status = BitConverter.ToUInt32(o, 24);
        string? Text(int lengthAt, int max)
        {
            var length = Math.Min((int)BitConverter.ToUInt16(o, lengthAt), max);
            return length == 0 ? null : System.Text.Encoding.Unicode.GetString(o, lengthAt + 2, length * 2).TrimEnd('\0');
        }
        return new BypassIoState(status == 0, Text(28, 32), Text(94, 128));
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
}
