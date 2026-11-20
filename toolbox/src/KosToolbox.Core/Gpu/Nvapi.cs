using System.Runtime.InteropServices;
using KosToolbox.Core.Text;

namespace KosToolbox.Core.Gpu;

public sealed record GpuThermalLimit(int Current, int Minimum, int Maximum, int Default);
public sealed record GpuVfPoint(int Index, int VoltageUv, int FrequencyKhz, int DeltaKhz);
public sealed record GpuVfCurve(IReadOnlyList<GpuVfPoint> Points, int MinimumDeltaKhz, int MaximumDeltaKhz);
public sealed record GpuVfChange(int Index, int VoltageUv, int DeltaKhz);
/// <summary>Monitor output color (NVAPI): format 0=RGB 1=YCbCr422 2=YCbCr444 3=YCbCr420; range 0=Full 1=Limited;
/// bpc 1=6 2=8 3=10 4=12; policy 0=user 1=driver default.</summary>
public sealed record NvColor(int Format, int Range, int Bpc, int Policy);

/// <summary>NVAPI interop. Private curve layouts are checked on every read before a write is enabled.</summary>
internal static partial class Nvapi
{
    const uint InitializeId = 0x0150E828, EnumerateId = 0xE5AC921F;
    const uint BusId = 0x1BE0B8E5, SlotId = 0x2A0A350F, ErrorId = 0x6C2D048C;
    const uint GetPstates = 0x6FF81213, SetPstates = 0x0F4DAE6B;
    const uint GetMask = 0x507B4B59, GetCurve = 0x21537AD4;
    const uint GetTable = 0x23F1B133, SetTable = 0x0733E009, GetDomains = 0x64B43A6A;
    const uint GetThermalInfo = 0x0D258BB5, GetThermalStatus = 0xE9C425A1, SetThermalStatus = 0x34C0B13D;
    const uint GetLock = 0xE440B867, SetLock = 0x39442CFB;
    const uint GetSlowdown = 0x6683EE65, SetSlowdown = 0x1B71D425; // undocumented: 0 = thermal slowdown on (the default), 0xFFFF = off
    const uint GetVoltage = 0x465F9BCF;
    const int PstatesSize = 7316, MaskSize = 6188, CurveSize = 7208, TableSize = 9248, DomainsSize = 2344;
    const int TableBase = 0x64, TableStride = 36, TableDelta = 0x18;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr QueryFn(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NoArgs();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int EnumerateFn([Out] IntPtr[] handles, out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GpuUInt(IntPtr gpu, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GpuSetUInt(IntPtr gpu, uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GpuBuffer(IntPtr gpu, IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] delegate int ErrorFn(int code, byte[] message);

    static readonly object Gate = new();
    static IntPtr _library;
    static QueryFn? _query;

    static T Function<T>(uint id) where T : Delegate
    {
        var p = _query?.Invoke(id) ?? IntPtr.Zero;
        if (p == IntPtr.Zero) throw new NotSupportedException($"NVAPI function 0x{id:X8} is unavailable.");
        return Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    static void EnsureReady()
    {
        if (_library != IntPtr.Zero) return;
        var path = Path.Combine(Environment.SystemDirectory, "nvapi64.dll");
        if (!File.Exists(path) || !NativeLibrary.TryLoad(path, out var library))
            throw new NotSupportedException("NVIDIA NVAPI is unavailable.");
        try
        {
            _query = Marshal.GetDelegateForFunctionPointer<QueryFn>(NativeLibrary.GetExport(library, "nvapi_QueryInterface"));
            Check(Function<NoArgs>(InitializeId)(), "NvAPI_Initialize");
            _library = library;
        }
        catch
        {
            _query = null;
            NativeLibrary.Free(library);
            throw;
        }
    }

    static string Describe(int code)
    {
        try
        {
            var bytes = new byte[64];
            if (Function<ErrorFn>(ErrorId)(code, bytes) == 0)
                return Nvml.Ansi(bytes);
        }
        catch { /* A numeric status is still useful. */ }
        return $"status {code}";
    }

    static void Check(int code, string action)
    {
        if (code == 0) return;
        var message = $"{action}: {Describe(code)} ({code}).";
        // API_NOT_INITIALIZED, NVIDIA_DEVICE_NOT_FOUND, HANDLE_INVALIDATED, EXPECTED_PHYSICAL_GPU_HANDLE: session lost
        // (driver restarted or recovered from a hang). Close; the next call opens a new one.
        if (code is -4 or -6 or -10 or -101) Reset();
        throw new InvalidOperationException(message);
    }

    const uint UnloadId = 0xD22BDD7E;

    /// <summary>How many times the NVAPI session was reopened (driver restarted); callers compare to notice.</summary>
    public static int Generation { get; private set; }

    /// <summary>Closes NVAPI after the driver restarts: the old session only returns errors; the next call opens another.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            if (_library == IntPtr.Zero) return;
            try { Function<NoArgs>(UnloadId)(); } catch { /* session already lost */ }
            NativeLibrary.Free(_library);
            _library = IntPtr.Zero;
            _query = null;
            ThermalMasks.Clear();
            Generation++;
            Persistence.Log.Info("NVAPI reset (driver restarted?).");
        }
    }

    /// <summary>Card handle; if the card list fails (session lost), reopens NVAPI once.</summary>
    static IntPtr Open(string busId)
    {
        try { return Find(busId); }
        catch (InvalidOperationException) when (_library != IntPtr.Zero)
        {
            Reset();
            return Find(busId);
        }
    }

    static IntPtr Find(string busId)
    {
        EnsureReady();
        var parts = busId.Split(':', '.');
        if (parts.Length != 4 || !int.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out var domain)
            || !int.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out var bus)
            || !int.TryParse(parts[2], System.Globalization.NumberStyles.HexNumber, null, out var slot)
            || !int.TryParse(parts[3], out var function) || domain != 0 || function != 0)
            throw new NotSupportedException("NVAPI tuning requires a GPU on PCI domain 0, function 0.");
        var handles = new IntPtr[64];
        Check(Function<EnumerateFn>(EnumerateId)(handles, out var count), "NvAPI_EnumPhysicalGPUs");
        var getBus = Function<GpuUInt>(BusId);
        var getSlot = Function<GpuUInt>(SlotId);
        for (var i = 0; i < Math.Clamp(count, 0, handles.Length); i++)
            if (getBus(handles[i], out var b) == 0 && getSlot(handles[i], out var s) == 0 && b == bus && s == slot)
                return handles[i];
        throw new InvalidOperationException(Txt.GpuInvalidDevice(busId));
    }

    static byte[] Read(IntPtr gpu, uint id, int size, int version = 1, byte[]? mask = null)
    {
        var data = new byte[size];
        BitConverter.GetBytes(size | (version << 16)).CopyTo(data, 0);
        mask?.CopyTo(data, 4);
        Call(gpu, id, data);
        return data;
    }

    static void Call(IntPtr gpu, uint id, byte[] data) => Call(data, $"NVAPI 0x{id:X8}", p => Function<GpuBuffer>(id)(gpu, p));

    static void Call(byte[] data, string action, Func<IntPtr, int> invoke)
    {
        var p = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, p, data.Length);
            Check(invoke(p), action);
            Marshal.Copy(p, data, 0, data.Length);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    static int I(byte[] data, int offset) => BitConverter.ToInt32(data, offset);
    static void Put(byte[] data, int offset, int value) => BitConverter.GetBytes(value).CopyTo(data, offset);

    const uint ThermalSensors = 0x65FE3AAD; // NvAPI_GPU_ThermalGetSensors (undocumented; the same one HWiNFO/LibreHardwareMonitor use)
    const int ThermalSize = 168, ThermalValuesAt = 40;
    static readonly Dictionary<string, uint> ThermalMasks = new();

    /// <summary>
    /// Hotspot (sensor 1) and memory junction (sensor 9, GDDR6X and newer only), in °C; null if the card does not report.
    /// The valid-sensor mask is discovered once per card (bit by bit, until NVAPI refuses).
    /// </summary>
    public static (double? Hotspot, double? MemoryJunction) ReadThermalSensors(string busId)
    {
        lock (Gate)
        {
            try
            {
                var gpu = Open(busId);
                if (!ThermalMasks.TryGetValue(busId, out var mask))
                {
                    var count = 0;
                    for (; count < 32; count++)
                        try { Read(gpu, ThermalSensors, ThermalSize, 2, BitConverter.GetBytes(1u << count)); }
                        catch (InvalidOperationException) { break; }
                    ThermalMasks[busId] = mask = count >= 32 ? uint.MaxValue : (1u << count) - 1;
                }
                if (mask == 0) return (null, null);
                var data = Read(gpu, ThermalSensors, ThermalSize, 2, BitConverter.GetBytes(mask));
                double? Sensor(int i) => (mask & (1u << i)) != 0 && I(data, ThermalValuesAt + i * 4) is var v and > 0 and < 150 * 256 ? v / 256.0 : null;
                return (Sensor(1), Sensor(9));
            }
            catch { return (null, null); }
        }
    }

    const uint GetThermalSettings = 0xE3640A56; // NvAPI_GPU_GetThermalSettings
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GpuIndexBuffer(IntPtr gpu, uint index, IntPtr buffer);

    /// <summary>GPU core temperature in °C (NV_GPU_THERMAL_SETTINGS_V2, the sensor whose target is the GPU); null if none reports it.</summary>
    public static int? ReadCoreTemperature(string busId)
    {
        lock (Gate)
        {
            var gpu = Open(busId);
            var data = new byte[68];
            Put(data, 0, 68 | 0x20000);
            Call(data, "NvAPI_GPU_GetThermalSettings", p => Function<GpuIndexBuffer>(GetThermalSettings)(gpu, 15, p)); // 15 = every sensor
            for (var i = 0; i < Math.Min(I(data, 4), 3); i++)
                if (I(data, 24 + i * 20) == 1 && I(data, 20 + i * 20) is var t and > 0 and < 150) return t;
            return null;
        }
    }

    // NvAPI_GPU_ClientFanCoolers* (undocumented; on an RTX card they reported the same fans, range, level and mode as NVML).
    // The size in the version field is checked by the driver, which refuses any other layout.
    const uint GetFanStatus = 0x35AED5E8, GetFanControl = 0x814B209F, SetFanControl = 0xA58971A5;
    const int FanStatusSize = 1704, FanStatusAt = 40, FanStatusStride = 52, FanControlSize = 1452, FanControlAt = 44, FanControlStride = 44;

    /// <summary>The % range every fan accepts. Throws when NVAPI has no fan coolers to control on this card.</summary>
    public static (uint Min, uint Max) ReadFanRange(string busId)
    {
        lock (Gate)
        {
            var gpu = Open(busId);
            var status = Read(gpu, GetFanStatus, FanStatusSize);
            var control = Read(gpu, GetFanControl, FanControlSize);
            var count = I(status, 4);
            if (count is < 1 or > 32 || I(control, 8) != count) throw new NotSupportedException($"NVAPI reports {count} fans for status and {I(control, 8)} for control.");
            int min = 0, max = 100;
            for (var i = 0; i < count; i++)
            {
                var at = FanStatusAt + i * FanStatusStride;
                if (I(status, at) != I(control, FanControlAt + i * FanControlStride)) throw new NotSupportedException("NVAPI's fan lists don't match.");
                (min, max) = (Math.Max(min, I(status, at + 8)), Math.Min(max, I(status, at + 12)));
            }
            if (min > max) throw new NotSupportedException($"NVAPI fan range {min}-{max}% is invalid.");
            return ((uint)min, (uint)max);
        }
    }

    /// <summary>Every fan at <paramref name="percent"/> (manual), or back to the driver's automatic mode with null.</summary>
    public static void SetFans(string busId, uint? percent)
    {
        lock (Gate)
        {
            var gpu = Open(busId);
            var control = Read(gpu, GetFanControl, FanControlSize); // fresh, so every cooler id is the driver's own
            var count = I(control, 8);
            if (count is < 1 or > 32) throw new NotSupportedException($"NVAPI reports {count} fans.");
            for (var i = 0; i < count; i++)
            {
                var at = FanControlAt + i * FanControlStride;
                Put(control, at + 4, (int)(percent ?? 0));
                Put(control, at + 8, percent is null ? 0 : 1); // 0 = automatic, 1 = manual
            }
            Call(gpu, SetFanControl, control);
        }
    }

    /// <summary>Every fan reads back as manual at <paramref name="percent"/>.</summary>
    public static bool FansAt(string busId, uint percent)
    {
        lock (Gate)
        {
            var control = Read(Open(busId), GetFanControl, FanControlSize);
            var count = I(control, 8);
            if (count is < 1 or > 32) return false;
            for (var i = 0; i < count; i++)
                if (I(control, FanControlAt + i * FanControlStride + 4) != percent || I(control, FanControlAt + i * FanControlStride + 8) != 1) return false;
            return true;
        }
    }

    public static int? ReadVoltage(string busId)
    {
        lock (Gate)
        {
            try
            {
                var voltage = I(Read(Open(busId), GetVoltage, 76), 0x28);
                return voltage is >= 300_000 and <= 1_300_000 ? voltage : null;
            }
            catch { return null; }
        }
    }

    public static (GpuRange? Core, GpuRange? Memory, string? Problem) ReadOffsets(string busId)
    {
        lock (Gate)
        {
            try
            {
                var bytes = Read(Open(busId), GetPstates, PstatesSize);
                if (I(bytes, 8) < 1 || I(bytes, 12) < 1 || I(bytes, 20) != 0 || (I(bytes, 4) & 1) == 0
                    || (I(bytes, 24) & 1) == 0)
                    throw new InvalidOperationException("NVAPI returned no P0 clock settings.");
                GpuRange? core = null, memory = null;
                for (var i = 0; i < Math.Min(I(bytes, 12), 8); i++)
                {
                    var at = 28 + i * 44;
                    var min = I(bytes, at + 16);
                    var max = I(bytes, at + 20);
                    if (I(bytes, at + 4) != 1 || (I(bytes, at + 8) & 1) == 0
                        || min >= max || min % 1000 != 0 || max % 1000 != 0) continue;
                    var range = new GpuRange(I(bytes, at + 12) / 1000, min / 1000, max / 1000, false);
                    if (I(bytes, at) == 0) core = range;
                    if (I(bytes, at) == 4) memory = range;
                }
                return (core, memory, null);
            }
            catch (Exception ex) { return (null, null, ex.Message); }
        }
    }

    public static void SetOffset(string busId, int domain, int mhz)
    {
        lock (Gate)
        {
            var gpu = Open(busId);
            var existing = Read(gpu, GetPstates, PstatesSize);
            var found = false;
            for (var i = 0; i < Math.Min(I(existing, 12), 8); i++)
            {
                var at = 28 + i * 44;
                if (I(existing, at) != domain || I(existing, at + 4) != 1 || (I(existing, at + 8) & 1) == 0) continue;
                if (mhz * 1000L < I(existing, at + 16) || mhz * 1000L > I(existing, at + 20))
                    throw new ArgumentOutOfRangeException(nameof(mhz));
                found = true;
                break;
            }
            if (!found) throw new NotSupportedException("This NVAPI clock domain is unavailable.");
            // A one-domain request leaves the other clock domain untouched.
            var request = new byte[PstatesSize];
            Put(request, 0, PstatesSize | 0x10000);
            Put(request, 8, 1); // one P-state
            Put(request, 12, 1); // one clock domain
            Put(request, 28, domain);
            Put(request, 40, checked(mhz * 1000));
            Call(gpu, SetPstates, request);
        }
    }

    public static GpuThermalLimit ReadThermal(string busId)
    {
        lock (Gate)
        {
            var gpu = Open(busId);
            var info = Read(gpu, GetThermalInfo, 104, 2);
            var status = Read(gpu, GetThermalStatus, 56, 2);
            var count = Math.Min(info[4], (byte)4);
            for (var i = 0; i < count; i++)
            {
                var at = 8 + i * 24;
                var controller = I(info, at);
                for (var j = 0; j < Math.Min(I(status, 4), 4); j++)
                {
                    var target = 8 + j * 12;
                    if (I(status, target) != controller || controller != 1) continue;
                    var min = I(info, at + 8) >> 8;
                    var def = I(info, at + 12) >> 8;
                    var max = I(info, at + 16) >> 8;
                    var current = I(status, target + 4) >> 8;
                    if (min < 40 || max > 120 || min >= max || current < min || current > max || def < min || def > max)
                        throw new InvalidOperationException("NVAPI returned an invalid thermal range.");
                    return new GpuThermalLimit(current, min, max, def);
                }
            }
            throw new NotSupportedException("The GPU has no adjustable thermal target.");
        }
    }

    public static void SetThermal(string busId, int celsius)
    {
        lock (Gate)
        {
            var limits = ReadThermal(busId);
            if (celsius < limits.Minimum || celsius > limits.Maximum) throw new ArgumentOutOfRangeException(nameof(celsius));
            var request = new byte[56];
            Put(request, 0, 56 | 0x20000);
            Put(request, 4, 1);
            Put(request, 8, 1); // GPU thermal controller
            Put(request, 12, celsius << 8);
            Call(Open(busId), SetThermalStatus, request);
        }
    }

    /// <summary>true = the driver's thermal slowdown is on (the default). Throws when the driver can't read or change it.</summary>
    public static bool ReadThermalSlowdown(string busId)
    {
        lock (Gate)
        {
            var gpu = Open(busId);
            Function<GpuSetUInt>(SetSlowdown); // read-only isn't offered: without the setter the control is unavailable
            Check(Function<GpuUInt>(GetSlowdown)(gpu, out var state), "NVAPI thermal slowdown");
            // Anything but 0 shows as off, so the switch can always write the default back.
            if (state is not (0 or 0xFFFF)) Persistence.Log.Warn($"NVAPI thermal slowdown on {busId}: unexpected state 0x{state:X} (shown as off).");
            return state == 0;
        }
    }

    public static void SetThermalSlowdown(string busId, bool on)
    {
        lock (Gate) Check(Function<GpuSetUInt>(SetSlowdown)(Open(busId), on ? 0u : 0xFFFFu), "NVAPI thermal slowdown");
    }

    public static int ReadLock(string busId)
    {
        lock (Gate)
        {
            var bytes = Read(Open(busId), GetLock, 780, 2);
            for (var i = 0; i < Math.Min(I(bytes, 8), 32); i++)
            {
                var at = 12 + i * 24;
                if (I(bytes, at) != 6) continue;
                var mode = I(bytes, at + 8);
                var voltage = I(bytes, at + 16);
                if (mode == 0) return 0;
                if (mode == 3 && voltage is >= 300_000 and <= 1_300_000) return voltage;
                throw new NotSupportedException("The GPU uses an unknown V/F lock mode.");
            }
            throw new NotSupportedException("The GPU has no V/F lock control.");
        }
    }

    public static void SetLockVoltage(string busId, int voltageUv)
    {
        lock (Gate)
        {
            if (voltageUv != 0 && !ReadCurve(busId).Points.Any(p => p.VoltageUv == voltageUv))
                throw new ArgumentOutOfRangeException(nameof(voltageUv));
            var gpu = Open(busId);
            var bytes = Read(gpu, GetLock, 780, 2);
            for (var i = 0; i < Math.Min(I(bytes, 8), 32); i++)
            {
                var at = 12 + i * 24;
                if (I(bytes, at) != 6) continue;
                Put(bytes, at + 8, voltageUv == 0 ? 0 : 3);
                Put(bytes, at + 16, voltageUv);
                Put(bytes, at + 20, 0);
                Put(bytes, 0, 780 | 0x20000);
                Call(gpu, SetLock, bytes);
                return;
            }
            throw new NotSupportedException("The GPU has no V/F lock control.");
        }
    }

    static byte[] Mask(IntPtr gpu)
    {
        var data = Read(gpu, GetMask, MaskSize);
        var mask = data[4..36];
        if (mask.All(b => b == 0)) throw new NotSupportedException("The GPU has no V/F curve points.");
        return mask;
    }

    static (int Min, int Max) DeltaRange(IntPtr gpu)
    {
        var data = Read(gpu, GetDomains, DomainsSize);
        for (var at = 0x28; at + 72 <= data.Length; at += 72)
            if (I(data, at + 4) == 0 && I(data, at + 0x28) > 0 && I(data, at + 0x2C) < 0)
                return (I(data, at + 0x2C), I(data, at + 0x28));
        throw new NotSupportedException("The driver did not report a graphics curve range.");
    }

    // ---------- monitor (color, vibrance, hue) ----------
    // Public IDs from nvapi_interface.h; monitors are found by GDI name (\\.\DISPLAYn).
    const uint DisplayIdByName = 0xAE457190, ColorControlId = 0x92F9D80D, DisplayHandleByName = 0x35C29134;
    const uint GetDvcEx = 0x0E45002D, SetDvcEx = 0x4A82C2B1, GetDvc = 0x4085DE45, SetDvc = 0x172409B4;
    const uint GetHue = 0x95B64341, SetHue = 0xF5A0F22C;
    const int ColorDataSize = 24, ColorDataVersion = 5;
    public const int ColorCmdGet = 1, ColorCmdSet = 2, ColorCmdIsSupported = 3;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] delegate int NameToUInt(string name, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] delegate int NameToHandle(string name, out IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int UIntBuffer(uint id, IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int HandleBuffer(IntPtr display, uint outputId, IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int HandleUInt(IntPtr display, uint outputId, uint value);

    static int WithBuffer(byte[] data, Func<IntPtr, int> call)
    {
        var p = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, p, data.Length);
            var code = call(p);
            Marshal.Copy(p, data, 0, data.Length);
            return code;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    static uint DisplayId(string gdiName)
    {
        EnsureReady();
        Check(Function<NameToUInt>(DisplayIdByName)(gdiName, out var id), "NvAPI_DISP_GetDisplayIdByDisplayName");
        return id;
    }

    static IntPtr DisplayHandle(string gdiName)
    {
        EnsureReady();
        Check(Function<NameToHandle>(DisplayHandleByName)(gdiName, out var handle), "NvAPI_GetAssociatedNvidiaDisplayHandle");
        return handle;
    }

    /// <summary>NV_COLOR_DATA_V5: format (byte 8), range (10), bpc (12), policy (16). Returns the NVAPI code.</summary>
    static int ColorControl(uint displayId, int cmd, ref NvColor color)
    {
        var data = new byte[ColorDataSize];
        Put(data, 0, ColorDataSize | (ColorDataVersion << 16));
        BitConverter.GetBytes((ushort)ColorDataSize).CopyTo(data, 4);
        data[6] = (byte)cmd;
        data[8] = (byte)color.Format;
        data[9] = 0xFF; // automatic colorimetry
        data[10] = (byte)color.Range;
        Put(data, 12, color.Bpc);
        Put(data, 16, color.Policy);
        Put(data, 20, 0); // desktop depth: default
        var code = WithBuffer(data, p => Function<UIntBuffer>(ColorControlId)(displayId, p));
        if (code == 0 && cmd == ColorCmdGet) color = new NvColor(data[8], data[10], I(data, 12), I(data, 16));
        return code;
    }

    public static NvColor ReadColor(string gdiName)
    {
        lock (Gate)
        {
            var color = new NvColor(0, 0, 0, 0);
            Check(ColorControl(DisplayId(gdiName), ColorCmdGet, ref color), "NvAPI_Disp_ColorControl");
            return color;
        }
    }

    public static bool ColorSupported(string gdiName, NvColor color)
    {
        lock (Gate)
        {
            try { return ColorControl(DisplayId(gdiName), ColorCmdIsSupported, ref color) == 0; }
            catch { return false; }
        }
    }

    public static void SetColor(string gdiName, NvColor color)
    {
        lock (Gate) Check(ColorControl(DisplayId(gdiName), ColorCmdSet, ref color), "NvAPI_Disp_ColorControl");
    }

    /// <summary>Digital vibrance: current level, minimum, maximum and default (the Ex version accepts levels below the default).</summary>
    public static (int Current, int Min, int Max, int Default) ReadVibrance(string gdiName)
    {
        lock (Gate)
        {
            var display = DisplayHandle(gdiName);
            try
            {
                var ex = new byte[20];
                Put(ex, 0, 20 | 0x10000);
                Check(WithBuffer(ex, p => Function<HandleBuffer>(GetDvcEx)(display, 0, p)), "NvAPI_GetDVCInfoEx");
                return (I(ex, 4), I(ex, 8), I(ex, 12), I(ex, 16));
            }
            catch (NotSupportedException)
            {
                var info = new byte[16];
                Put(info, 0, 16 | 0x10000);
                Check(WithBuffer(info, p => Function<HandleBuffer>(GetDvc)(display, 0, p)), "NvAPI_GetDVCInfo");
                return (I(info, 4), I(info, 8), I(info, 12), I(info, 8));
            }
        }
    }

    public static void SetVibrance(string gdiName, int level)
    {
        lock (Gate)
        {
            var display = DisplayHandle(gdiName);
            try
            {
                var ex = new byte[20];
                Put(ex, 0, 20 | 0x10000);
                Put(ex, 4, level);
                Check(WithBuffer(ex, p => Function<HandleBuffer>(SetDvcEx)(display, 0, p)), "NvAPI_SetDVCLevelEx");
            }
            catch (NotSupportedException)
            {
                Check(Function<HandleUInt>(SetDvc)(display, 0, (uint)Math.Max(0, level)), "NvAPI_SetDVCLevel");
            }
        }
    }

    public static (int Current, int Default) ReadHue(string gdiName)
    {
        lock (Gate)
        {
            var display = DisplayHandle(gdiName);
            var info = new byte[12];
            Put(info, 0, 12 | 0x10000);
            Check(WithBuffer(info, p => Function<HandleBuffer>(GetHue)(display, 0, p)), "NvAPI_GetHUEInfo");
            return (I(info, 4), I(info, 8));
        }
    }

    public static void SetHueAngle(string gdiName, int degrees)
    {
        lock (Gate) Check(Function<HandleUInt>(SetHue)(DisplayHandle(gdiName), 0, (uint)((degrees % 360 + 360) % 360)), "NvAPI_SetHUEAngle");
    }

    public static GpuVfCurve ReadCurve(string busId)
    {
        lock (Gate)
        {
            var gpu = Open(busId);
            var mask = Mask(gpu);
            var curve = Read(gpu, GetCurve, CurveSize, mask: mask);
            var table = Read(gpu, GetTable, TableSize, mask: mask);
            var (min, max) = DeltaRange(gpu);
            // Pascal stores half-kHz; this implementation only enables writes for plain-kHz layouts.
            var points = new List<GpuVfPoint>();
            var lastVoltage = 0;
            for (var index = 0; index < 255; index++)
            {
                var at = 0x40 + index * 28;
                var voltage = I(curve, at + 12);
                var frequency = I(curve, at + 8);
                if (I(curve, at + 4) != 0 || voltage is < 300_000 or > 1_300_000 || voltage <= lastVoltage) break;
                var deltaAt = TableBase + (index - 1) * TableStride + TableDelta;
                var delta = index == 0 ? 0 : I(table, deltaAt);
                points.Add(new GpuVfPoint(index, voltage, frequency, delta));
                lastVoltage = voltage;
            }
            if (points.Count < 20 || points[^1].FrequencyKhz < 800_000 || min < -1_500_000 || max > 1_500_000)
                throw new NotSupportedException("The NVAPI V/F layout could not be validated for this GPU.");
            return new GpuVfCurve(points, min, max);
        }
    }

    public static void SetCurve(string busId, IReadOnlyList<GpuVfChange> changes)
    {
        lock (Gate)
        {
            var current = ReadCurve(busId);
            if (changes.Count == 0 || changes.Count > current.Points.Count - 1)
                throw new ArgumentException("No editable V/F points were supplied.", nameof(changes));
            foreach (var change in changes)
                if (change.Index < 1 || change.Index >= current.Points.Count
                    || current.Points[change.Index].VoltageUv != change.VoltageUv
                    || change.DeltaKhz < current.MinimumDeltaKhz || change.DeltaKhz > current.MaximumDeltaKhz)
                    throw new ArgumentOutOfRangeException(nameof(changes), "A V/F point or delta is outside this driver's limits.");
            if (changes.Select(c => c.Index).Distinct().Count() != changes.Count)
                throw new ArgumentException("Duplicate V/F points.", nameof(changes));
            var gpu = Open(busId);
            var mask = Mask(gpu);
            var table = Read(gpu, GetTable, TableSize, mask: mask);
            foreach (var change in changes)
                Put(table, TableBase + (change.Index - 1) * TableStride + TableDelta, change.DeltaKhz);
            Put(table, 0, TableSize | 0x10000);
            mask.CopyTo(table, 4);
            Call(gpu, SetTable, table);
        }
    }
}
