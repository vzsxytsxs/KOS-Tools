using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace KosToolbox.Core.Peripherals.Hid;

/// <summary>
/// The only way device drivers talk to a HID collection, so tests can drive them with scripted replies. Every report buffer starts with
/// the report id and has exactly the collection's length for that kind (<see cref="HidBuffer.Build"/>).
/// </summary>
public interface IHidTransport
{
    HidCollection Collection { get; }
    void SetFeature(byte reportId, ReadOnlySpan<byte> payload);
    /// <summary>The whole feature report, its id at [0].</summary>
    byte[] GetFeature(byte reportId);
    /// <summary>Sends an output report; <see cref="TimeoutException"/> when the device doesn't take it in time.</summary>
    void Write(byte reportId, ReadOnlySpan<byte> payload, int timeoutMs = HidHandle.DefaultTimeout);
    /// <summary>The next input report (its id at [0]), or null when none arrives in time.</summary>
    byte[]? Read(int timeoutMs = HidHandle.DefaultTimeout);
    /// <summary>An input report on demand (HidD_GetInputReport), its id at [0].</summary>
    byte[] GetInputReport(byte reportId);
    /// <summary>Drops the input reports Windows has queued.</summary>
    void FlushQueue();
}

public static class HidBuffer
{
    /// <summary>
    /// The Win32 buffer of a report: the report id (0 when the device doesn't number its reports), the payload, then zeros up to
    /// <paramref name="length"/>, the caps length with the id included. A payload that doesn't fit, or a report kind the collection
    /// doesn't have (length 0), is refused.
    /// </summary>
    public static byte[] Build(byte reportId, ReadOnlySpan<byte> payload, int length)
    {
        if (length < 1) throw new ArgumentException("This collection has no report of that kind.", nameof(length));
        if (payload.Length > length - 1)
            throw new ArgumentException($"The payload has {payload.Length} bytes; this report takes at most {length - 1}.", nameof(payload));
        var buffer = new byte[length];
        buffer[0] = reportId;
        payload.CopyTo(buffer.AsSpan(1));
        return buffer;
    }
}

/// <summary>
/// An open HID collection. <see cref="OpenFeatureOnly"/> (access 0, not overlapped) is enough for feature reports, even on the mouse and
/// keyboard collections Windows holds; <see cref="OpenReadWrite"/> (read/write, overlapped) is for output and input reports on vendor
/// collections. One request at a time per physical device: every call holds that device's lock.
/// </summary>
public sealed class HidHandle : IHidTransport, IDisposable
{
    public const int DefaultTimeout = 500;
    static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);

    readonly SafeFileHandle _handle;
    readonly bool _overlapped;
    readonly object _lock;

    HidHandle(HidCollection collection, SafeFileHandle handle, bool overlapped)
    {
        Collection = collection;
        _handle = handle;
        _overlapped = overlapped;
        _lock = Locks.GetOrAdd(HidDevices.GroupKey(collection), _ => new object());
    }

    public HidCollection Collection { get; }

    public static HidHandle OpenFeatureOnly(HidCollection collection) => new(collection, Open(collection, 0, 0), false);

    public static HidHandle OpenReadWrite(HidCollection collection) =>
        new(collection, Open(collection, HidNative.GenericRead | HidNative.GenericWrite, HidNative.FlagOverlapped), true);

    static SafeFileHandle Open(HidCollection c, uint access, uint flags) => c.UsagePage == HidDevices.FidoUsagePage
        ? throw new InvalidOperationException("Security keys (FIDO) are never opened.")
        : HidNative.Open(c.Path, access, flags);

    public void SetFeature(byte reportId, ReadOnlySpan<byte> payload)
    {
        var buffer = HidBuffer.Build(reportId, payload, Collection.FeatureLength);
        lock (_lock) Check(HidNative.HidD_SetFeature(_handle, buffer, buffer.Length));
    }

    public byte[] GetFeature(byte reportId)
    {
        var buffer = HidBuffer.Build(reportId, default, Collection.FeatureLength);
        lock (_lock) Check(HidNative.HidD_GetFeature(_handle, buffer, buffer.Length));
        return buffer;
    }

    public byte[] GetInputReport(byte reportId)
    {
        var buffer = HidBuffer.Build(reportId, default, Collection.InputLength);
        lock (_lock) Check(HidNative.HidD_GetInputReport(_handle, buffer, buffer.Length));
        return buffer;
    }

    public void FlushQueue()
    {
        lock (_lock) Check(HidNative.HidD_FlushQueue(_handle));
    }

    /// <summary>How many input reports Windows queues for this handle (32 by default) before it drops the oldest.</summary>
    public void SetInputBuffers(int count)
    {
        lock (_lock) Check(HidNative.HidD_SetNumInputBuffers(_handle, count));
    }

    public void Write(byte reportId, ReadOnlySpan<byte> payload, int timeoutMs = DefaultTimeout)
    {
        var buffer = HidBuffer.Build(reportId, payload, Collection.OutputLength);
        RequireOverlapped();
        lock (_lock)
            if (Transfer(_handle, buffer, write: true, timeoutMs) < 0) throw new TimeoutException($"The device didn't take output report {reportId} within {timeoutMs} ms.");
    }

    public byte[]? Read(int timeoutMs = DefaultTimeout)
    {
        var buffer = HidBuffer.Build(0, default, Collection.InputLength);
        RequireOverlapped();
        lock (_lock) return Transfer(_handle, buffer, write: false, timeoutMs) < 0 ? null : buffer;
    }

    public void Dispose() => _handle.Dispose();

    void RequireOverlapped()
    {
        if (!_overlapped) throw new InvalidOperationException("This handle was opened for feature reports only; output and input reports need OpenReadWrite.");
    }

    static void Check(bool ok)
    {
        if (!ok) throw new Win32Exception();
    }

    /// <summary>
    /// Overlapped ReadFile/WriteFile with a timeout: the bytes moved, or -1 when the time ran out. On timeout the request is cancelled
    /// (CancelIoEx) and waited for (GetOverlappedResult), so the OVERLAPPED and the buffer, in unmanaged memory, are freed only once
    /// Windows is done with them.
    /// </summary>
    internal static int Transfer(SafeFileHandle handle, byte[] buffer, bool write, int timeoutMs)
    {
        using var done = new ManualResetEvent(false);
        var overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        var data = Marshal.AllocHGlobal(buffer.Length);
        try
        {
            Marshal.StructureToPtr(new NativeOverlapped { EventHandle = done.SafeWaitHandle.DangerousGetHandle() }, overlapped, false);
            if (write) Marshal.Copy(buffer, 0, data, buffer.Length);
            var started = write ? HidNative.WriteFile(handle, data, buffer.Length, IntPtr.Zero, overlapped)
                : HidNative.ReadFile(handle, data, buffer.Length, IntPtr.Zero, overlapped);
            if (!started && Marshal.GetLastWin32Error() is var startError && startError != HidNative.ErrorIoPending) throw new Win32Exception(startError);
            if (!done.WaitOne(timeoutMs)) HidNative.CancelIoEx(handle, overlapped);
            if (!HidNative.GetOverlappedResult(handle, overlapped, out var count, wait: true))
            {
                var error = Marshal.GetLastWin32Error();
                return error == HidNative.ErrorOperationAborted ? -1 : throw new Win32Exception(error);
            }
            if (!write) Marshal.Copy(data, buffer, 0, count);
            return count;
        }
        finally
        {
            Marshal.FreeHGlobal(data);
            Marshal.FreeHGlobal(overlapped);
        }
    }
}

/// <summary>hid.dll and the kernel32 file calls, blittable signatures only (AOT).</summary>
internal static class HidNative
{
    public const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, FlagOverlapped = 0x40000000;
    public const int ErrorIoPending = 997, ErrorOperationAborted = 995;
    const uint ShareReadWrite = 0x3, OpenExisting = 3;
    const int HidpStatusSuccess = 0x00110000, ErrorInvalidData = 13;

    [StructLayout(LayoutKind.Sequential)]
    public struct Attributes { public uint Size; public ushort VendorId, ProductId, Version; }

    // HIDP_CAPS is 64 bytes: these five, then reserved words and capability counts the toolbox doesn't read.
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    public struct Caps { public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength; }

    /// <summary>CreateFile with read and write sharing; access 0 reads attributes, caps and strings without sending reports.</summary>
    public static SafeFileHandle Open(string path, uint access, uint flags)
    {
        var handle = CreateFile(path, access, ShareReadWrite, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error);
    }

    public static Attributes ReadAttributes(SafeFileHandle handle)
    {
        var a = new Attributes { Size = (uint)Marshal.SizeOf<Attributes>() };
        return HidD_GetAttributes(handle, ref a) ? a : throw new Win32Exception();
    }

    public static Caps ReadCaps(SafeFileHandle handle)
    {
        if (!HidD_GetPreparsedData(handle, out var data)) throw new Win32Exception();
        try { return HidP_GetCaps(data, out var caps) == HidpStatusSuccess ? caps : throw new Win32Exception(ErrorInvalidData); }
        finally { HidD_FreePreparsedData(data); }
    }

    public static string? Product(SafeFileHandle handle)
    {
        var buffer = new byte[256]; // a USB string is at most 126 UTF-16 characters
        if (!HidD_GetProductString(handle, buffer, buffer.Length)) return null;
        var text = Encoding.Unicode.GetString(buffer);
        var end = text.IndexOf('\0');
        return (end >= 0 ? text[..end] : text).Trim() is { Length: > 0 } s ? s : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(SafeFileHandle handle, IntPtr buffer, int count, IntPtr read, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteFile(SafeFileHandle handle, IntPtr buffer, int count, IntPtr written, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetOverlappedResult(SafeFileHandle handle, IntPtr overlapped, out int transferred, bool wait);

    [DllImport("hid.dll")]
    public static extern void HidD_GetHidGuid(out Guid guid);

    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);

    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);

    [DllImport("hid.dll")]
    static extern bool HidD_FreePreparsedData(IntPtr data);

    [DllImport("hid.dll")]
    static extern int HidP_GetCaps(IntPtr data, out Caps caps);

    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_GetProductString(SafeFileHandle handle, [Out] byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetFeature(SafeFileHandle handle, [In, Out] byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetInputReport(SafeFileHandle handle, [In, Out] byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_FlushQueue(SafeFileHandle handle);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_SetNumInputBuffers(SafeFileHandle handle, int count);
}
