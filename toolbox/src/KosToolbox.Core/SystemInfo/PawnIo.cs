using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace KosToolbox.Core.SystemInfo;

/// <summary>
/// PawnIO, an optional signed driver (installed from Setup) that runs small signed modules in the kernel; only an elevated
/// process can open it. PawnIOLib.dll is loaded from PawnIO's own installation folder. The two modules used (IntelMSR and
/// AMDFamily17 from PawnIO.Modules 0.2.11, LGPL-2.1, see PawnIO\COPYING) are embedded, as PawnIO's documentation asks;
/// the driver refuses a module without its author's signature.
/// </summary>
public sealed class PawnIo : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int OpenFn(out IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int LoadFn(IntPtr handle, byte[] blob, nuint size);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int ExecuteFn(IntPtr handle, byte[] name, ulong[] input, nuint inCount, [Out] ulong[] output, nuint outCount, out nuint written);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CloseFn(IntPtr handle);

    readonly IntPtr _lib, _handle;
    readonly ExecuteFn _execute;
    readonly CloseFn _close;
    readonly object _gate = new();
    bool _closed;

    PawnIo(IntPtr lib, IntPtr handle, ExecuteFn execute, CloseFn close) => (_lib, _handle, _execute, _close) = (lib, handle, execute, close);

    /// <summary>PawnIO's installation folder (from its uninstall entry); null = not installed.</summary>
    public static string? InstallFolder
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            return key?.GetValue("InstallLocation") is string folder && File.Exists(Path.Combine(folder, "PawnIOLib.dll")) ? folder : null;
        }
    }

    /// <summary>Opens the driver with one embedded module (e.g. "IntelMSR"). Throws with the reason.</summary>
    public static PawnIo Open(string module)
    {
        var folder = InstallFolder ?? throw new InvalidOperationException("PawnIO isn't installed.");
        var dll = Path.Combine(folder, "PawnIOLib.dll");
        if (Tools.ToolDiscovery.OwnerProblem(dll) is { } problem) throw new InvalidOperationException(problem);
        var blob = Module(module);
        var lib = NativeLibrary.Load(dll);
        try
        {
            T Fn<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));
            var close = Fn<CloseFn>("pawnio_close");
            var hr = Fn<OpenFn>("pawnio_open")(out var handle);
            if (hr < 0) throw new InvalidOperationException($"PawnIO couldn't be opened ({Describe(hr)}).");
            hr = Fn<LoadFn>("pawnio_load")(handle, blob, (nuint)blob.Length);
            if (hr < 0)
            {
                close(handle);
                throw new InvalidOperationException($"PawnIO refused its {module} module ({Describe(hr)}).");
            }
            return new PawnIo(lib, handle, Fn<ExecuteFn>("pawnio_execute"), close);
        }
        catch
        {
            NativeLibrary.Free(lib);
            throw;
        }
    }

    internal static byte[] Module(string name)
    {
        using var stream = typeof(PawnIo).Assembly.GetManifestResourceStream($"PawnIO.{name}.bin")
            ?? throw new InvalidOperationException($"The PawnIO module {name} isn't in this build.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>A module function with one value in and one out (e.g. "ioctl_read_msr"). Throws if the module refuses.</summary>
    public ulong Execute(string function, ulong input)
    {
        var name = Encoding.ASCII.GetBytes(function + "\0");
        var output = new ulong[1];
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var hr = _execute(_handle, name, new[] { input }, 1, output, 1, out var written);
            if (hr < 0 || written < 1) throw new InvalidOperationException($"PawnIO {function}(0x{input:X}) failed ({Describe(hr)}).");
        }
        return output[0];
    }

    static string Describe(int hr) => $"0x{hr:X8}: {Marshal.GetExceptionForHR(hr)?.Message ?? "unknown error"}";

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _close(_handle);
            NativeLibrary.Free(_lib);
        }
    }
}
