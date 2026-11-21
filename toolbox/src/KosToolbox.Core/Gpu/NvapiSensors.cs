using System.Runtime.InteropServices;

namespace KosToolbox.Core.Gpu;

/// <summary>
/// The readings KOS Engine needs during gamemode (session report, RTSS overlay) through public NVAPI (nvapi.h) instead of NVML:
/// NVML costs ~20 MB of private memory for as long as it's loaded. No board power here (NVAPI only has it as a share of the limit).
/// </summary>
internal static partial class Nvapi
{
    const uint GetFullName = 0xCEEE8E9F, GetDynamicPstates = 0x60DED2ED, GetClocks = 0xDCB616C3, GetPerfDecrease = 0x7F7F4600, GetMemoryInfo = 0x07F9B368;
    const int PstatesInfoSize = 72, ClocksSize = 264, MemoryInfoSize = 24;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NameFn(IntPtr gpu, byte[] name);

    /// <summary>The NVIDIA cards, with the bus id in NVML's form (00000000:bb:ss.0, what <see cref="Find"/> takes).</summary>
    public static List<GpuDevice> List()
    {
        lock (Gate)
        {
            EnsureReady();
            var handles = new IntPtr[64];
            Check(Function<EnumerateFn>(EnumerateId)(handles, out var count), "NvAPI_EnumPhysicalGPUs");
            var getBus = Function<GpuUInt>(BusId);
            var getSlot = Function<GpuUInt>(SlotId);
            var list = new List<GpuDevice>();
            for (var i = 0; i < Math.Clamp(count, 0, handles.Length); i++)
            {
                if (getBus(handles[i], out var bus) != 0 || getSlot(handles[i], out var slot) != 0) continue;
                var name = new byte[64];
                var text = Function<NameFn>(GetFullName)(handles[i], name) == 0 ? Nvml.Ansi(name) : "";
                list.Add(new GpuDevice(text.Length > 0 ? text : "NVIDIA GPU", $"00000000:{bus:X2}:{slot:X2}.0"));
            }
            return list;
        }
    }

    /// <summary>NVAPI's decrease reasons as NVML clock event bits: thermal protection → driver thermal limit, power control →
    /// power limit, insufficient power → power brake, API triggered → application clock setting.</summary>
    internal static ulong ReasonsAsNvml(uint perfDecrease) =>
        ((perfDecrease & 0x1) != 0 ? 0x20UL : 0) | ((perfDecrease & 0x2) != 0 ? 0x4UL : 0)
        | ((perfDecrease & 0x10) != 0 ? 0x80UL : 0) | ((perfDecrease & 0x8) != 0 ? 0x2UL : 0);

    /// <summary>Load, graphics clock, temperatures, clock limits and VRAM; a value NVAPI doesn't give is null.</summary>
    public static GpuSensors ReadLight(string busId)
    {
        lock (Gate)
        {
            var gpu = Open(busId);
            T? Try<T>(Func<T?> read) where T : struct
            {
                try { return read(); }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { return null; }
            }
            var load = Try<uint>(() =>
            {
                var d = Read(gpu, GetDynamicPstates, PstatesInfoSize); // NV_GPU_DYNAMIC_PSTATES_INFO_EX: utilization[0] is the GPU
                return (I(d, 8) & 1) != 0 ? (uint)I(d, 12) : null;
            });
            var mhz = Try<uint>(() =>
            {
                var d = Read(gpu, GetClocks, ClocksSize, 2); // NV_GPU_CLOCK_FREQUENCIES_V2, current clocks; domain 0 is graphics (kHz)
                return (I(d, 8) & 1) != 0 ? (uint)I(d, 12) / 1000 : null;
            });
            var reasons = Try<ulong>(() => Function<GpuUInt>(GetPerfDecrease)(gpu, out var flags) == 0 ? ReasonsAsNvml(flags) : null);
            var memory = Try<(ulong Used, ulong Total)>(() =>
            {
                var d = Read(gpu, GetMemoryInfo, MemoryInfoSize, 2); // NV_DISPLAY_DRIVER_MEMORY_INFO_V2, in KB
                var total = (ulong)(uint)I(d, 4) * 1024;
                var free = (ulong)(uint)I(d, 20) * 1024;
                return total > 0 && free <= total ? (total - free, total) : null;
            });
            var temp = Try<int>(() => ReadCoreTemperature(busId));
            var (hotspot, junction) = ReadThermalSensors(busId);
            return new GpuSensors
            {
                TemperatureC = temp is { } t ? (uint)t : null,
                GraphicsMhz = mhz,
                GpuLoad = load,
                ThrottleReasons = reasons,
                VramUsed = memory?.Used,
                VramTotal = memory?.Total,
                HotspotC = hotspot,
                MemoryJunctionC = junction,
            };
        }
    }
}
