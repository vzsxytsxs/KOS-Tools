using System.Runtime.InteropServices;
using System.Text;
using KosToolbox.Core.Text;

namespace KosToolbox.Core.Gpu;

public sealed class NvmlException(string function, int code, string message) : Exception($"{function} failed: {message} ({code})")
{
    public int Code { get; } = code;
}

/// <summary>
/// Interop with the public NVML (nvml.h). The DLL is loaded by full path and each function is resolved with
/// TryGetExport: a function the installed driver does not export only disables the control that depends on it.
/// </summary>
internal static class Nvml
{
    public const int Success = 0, NotSupported = 3, NoPermission = 4, FunctionNotFound = 13;
    public const uint ClockGraphics = 0, ClockSm = 1, ClockMem = 2, TemperatureGpu = 0, FanPolicyAuto = 0, PState0 = 0;

    // ---------- types from nvml.h ----------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct PciInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string BusIdLegacy;
        public uint Domain, Bus, Device, PciDeviceId, PciSubSystemId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string BusId;
    }

    [StructLayout(LayoutKind.Sequential)] public struct Utilization { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)] public struct Memory { public ulong Total, Free, Used; }

    /// <summary>nvmlClockOffset_v1_t.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ClockOffset { public uint Version, Type, PState; public int Offset, Min, Max; }
    public static readonly uint ClockOffsetV1 = (uint)Marshal.SizeOf<ClockOffset>() | (1u << 24); // NVML_STRUCT_VERSION(ClockOffset, 1)

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int NoArgs();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr ErrorString(int code);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int OutUInt(out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int SysText(byte[] buffer, uint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int ByIndex(uint index, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] public delegate int ByBusId([MarshalAs(UnmanagedType.LPStr)] string busId, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DevText(IntPtr device, byte[] buffer, uint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DevPci(IntPtr device, out PciInfo pci);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DevUtil(IntPtr device, out Utilization util);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DevMemory(IntPtr device, out Memory memory);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DevUInt(IntPtr device, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DevULong(IntPtr device, out ulong value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DevArgUInt(IntPtr device, uint arg, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DevTwoUInt(IntPtr device, out uint a, out uint b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DevInt(IntPtr device, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DevTwoInt(IntPtr device, out int a, out int b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int Dev(IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DevSetUInt(IntPtr device, uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DevSetTwoUInt(IntPtr device, uint a, uint b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DevSetInt(IntPtr device, int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DevOffset(IntPtr device, ref ClockOffset info);

    static readonly object Gate = new();
    static IntPtr _lib;
    static string? _loadError;

    public static string[] Candidates => new[]
    {
        Path.Combine(Environment.SystemDirectory, "nvml.dll"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll"),
    };

    /// <summary>Loads and initializes once per process. Null = ready; otherwise, the reason (only the missing DLL is cached).</summary>
    public static string? Initialize()
    {
        lock (Gate)
        {
            if (_lib != IntPtr.Zero) return null;
            if (_loadError is not null) return _loadError;
            // Full paths only: the default DLL search could find an nvml.dll in the current folder.
            var path = Candidates.FirstOrDefault(File.Exists);
            if (path is null || !NativeLibrary.TryLoad(path, out var lib)) return _loadError = Txt.NvmlMissing;
            _lib = lib;
            var init = Fn<NoArgs>("nvmlInit_v2");
            var code = init?.Invoke() ?? FunctionNotFound;
            if (code != Success)
            {
                // No cache: a transient failure (driver being updated) goes away on the next Refresh.
                var message = Txt.GpuInitFailed(Describe(code), code);
                _lib = IntPtr.Zero;
                NativeLibrary.Free(lib);
                return message;
            }
            return null;
        }
    }

    /// <summary>
    /// Closes and reopens NVML. After the video driver restarts (Restart Driver, update), the old session only
    /// returns errors ("GPU is lost"/uninitialized); the next call to Initialize opens a new one.
    /// </summary>
    /// <summary>How many times the NVML session was reopened (driver restarted).</summary>
    public static int Generation { get; private set; }

    public static void Reset()
    {
        lock (Gate)
        {
            if (_lib == IntPtr.Zero) return;
            try { Fn<NoArgs>("nvmlShutdown")?.Invoke(); } catch { /* session already lost */ }
            NativeLibrary.Free(_lib);
            _lib = IntPtr.Zero;
            Generation++;
            Persistence.Log.Info("NVML reset (driver restarted?).");
        }
    }

    public static T? Fn<T>(string name) where T : Delegate =>
        _lib != IntPtr.Zero && NativeLibrary.TryGetExport(_lib, name, out var p) ? Marshal.GetDelegateForFunctionPointer<T>(p) : null;

    public static bool Has(string name) => _lib != IntPtr.Zero && NativeLibrary.TryGetExport(_lib, name, out _);

    public static string Describe(int code)
    {
        try { return Fn<ErrorString>("nvmlErrorString") is { } f ? Marshal.PtrToStringAnsi(f(code)) ?? $"error {code}" : $"error {code}"; }
        catch { return $"error {code}"; }
    }

    /// <summary>Throws with the function name and the NVML message when the code is not NVML_SUCCESS.</summary>
    public static void Check(string function, int code)
    {
        if (code != Success) throw new NvmlException(function, code, Describe(code));
    }

    /// <summary>Calls an optional function; null if not exported or if it fails (the reason goes in <paramref name="error"/>).</summary>
    public static int Call<T>(string name, Func<T, int> call, out string? error) where T : Delegate
    {
        if (Fn<T>(name) is not { } f) { error = Txt.GpuNotExported(name); return FunctionNotFound; }
        var code = call(f);
        error = code == Success ? null : $"{name}: {Describe(code)}";
        return code;
    }

    public static string Ansi(byte[] buffer)
    {
        var end = Array.IndexOf(buffer, (byte)0);
        return Encoding.ASCII.GetString(buffer, 0, end < 0 ? buffer.Length : end).Trim();
    }
}
