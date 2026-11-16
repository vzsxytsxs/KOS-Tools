using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.Network;
using KosToolbox.Core.Persistence;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace KosToolbox.Core.SystemInfo;

public sealed record CoreReading(int Index, double Usage, double? Mhz, bool? Parked);
public sealed record CpuReading(double Usage, double? Mhz, IReadOnlyList<CoreReading> Cores, uint Processes, uint Threads, uint Handles, TimeSpan Uptime);
public sealed record MemoryReading(ulong Total, ulong Used, ulong Committed, ulong CommitLimit, ulong Cached, ulong PagedPool, ulong NonpagedPool);
public sealed record DiskReading(int Number, string Model, string Kind, IReadOnlyList<string> Volumes, double? Active, double? LatencyMs, double? ReadBps, double? WriteBps);
public sealed record NetReading(string Name, bool Up, long LinkBps, double SendBps, double ReceiveBps);
public sealed record LiveReading(CpuReading Cpu, MemoryReading Memory, IReadOnlyList<DiskReading> Disks, IReadOnlyList<NetReading> Networks, GpuDevice? Gpu, GpuSensors? GpuSensors);

/// <summary>Fixed machine data for Home (read once): CPU, cores, installed memory and speed.</summary>
public sealed record MachineInfo(string Cpu, int Cores, int Logical, double? BaseMhz, int MemoryModules, uint? MemorySpeedMts, string? MemoryType);

/// <summary>
/// Live readings for Home, like Task Manager: usage of each logical processor (kernel times), MHz and
/// parked core ("Processor Information" counters), memory, disks ("PhysicalDisk" counters + type and model
/// via IOCTL), physical network (interface statistics) and the NVIDIA GPU (NVML). Call <see cref="Read"/> every second;
/// rates and usage compare with the previous reading. Counters disabled on the system just leave their fields empty.
/// shortcut: only processor group 0 (up to 64 logical); with more, read per group.
/// </summary>
public sealed class LiveMonitor : IDisposable
{
    readonly Pdh? _pdh;
    readonly IntPtr _perf, _freq, _park, _idle, _latency, _read, _write;
    long[] _idleTimes = Array.Empty<long>(), _busyTimes = Array.Empty<long>();
    readonly Dictionary<string, (long Sent, long Received, long Ticks)> _net = new();
    readonly Dictionary<int, (string Model, string Kind)> _diskInfo = new();
    List<NetAdapter>? _adapters;
    (GpuDevice Device, bool Found)? _gpu;

    public LiveMonitor()
    {
        try
        {
            _pdh = new Pdh();
            _perf = _pdh.Add(@"\Processor Information(*)\% Processor Performance");
            _freq = _pdh.Add(@"\Processor Information(*)\Processor Frequency");
            _park = _pdh.Add(@"\Processor Information(*)\Parking Status");
            _idle = _pdh.Add(@"\PhysicalDisk(*)\% Idle Time");
            _latency = _pdh.Add(@"\PhysicalDisk(*)\Avg. Disk sec/Transfer");
            _read = _pdh.Add(@"\PhysicalDisk(*)\Disk Read Bytes/sec");
            _write = _pdh.Add(@"\PhysicalDisk(*)\Disk Write Bytes/sec");
            _pdh.Collect();
        }
        catch (Exception ex) { Log.Warn($"Performance counters aren't available: {ex.Message}"); _pdh = null; }
    }

    public LiveReading Read()
    {
        _pdh?.Collect();
        return new LiveReading(Cpu(), Memory(), Disks(), Networks(), Gpu(out var sensors), sensors);
    }

    // ---------- CPU ----------

    CpuReading Cpu()
    {
        var (idle, busy) = ProcessorTimes();
        var usage = new double[idle.Length];
        if (_idleTimes.Length == idle.Length)
            for (var i = 0; i < idle.Length; i++)
            {
                var b = busy[i] - _busyTimes[i];
                usage[i] = b <= 0 ? 0 : Math.Clamp(100.0 * (b - (idle[i] - _idleTimes[i])) / b, 0, 100);
            }
        (_idleTimes, _busyTimes) = (idle, busy);

        var perf = Values(_perf);
        var freq = Values(_freq);
        var park = Values(_park);
        double? Mhz(string instance) => perf.TryGetValue(instance, out var p) && freq.TryGetValue(instance, out var f) ? f * p / 100 : null;
        var cores = Enumerable.Range(0, usage.Length).Select(i => new CoreReading(i, usage[i], Mhz($"0,{i}"),
            park.TryGetValue($"0,{i}", out var parked) ? parked >= 1 : null)).ToList();

        var info = new PerformanceInformation { cb = Marshal.SizeOf<PerformanceInformation>() };
        GetPerformanceInfo(ref info, info.cb);
        return new CpuReading(cores.Count == 0 ? 0 : cores.Average(c => c.Usage), Mhz("_Total") ?? Mhz("0,_Total"), cores,
            info.ProcessCount, info.ThreadCount, info.HandleCount, TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    /// <summary>Idle and total times (kernel + user; kernel already includes idle) of each logical processor.</summary>
    static (long[] Idle, long[] Busy) ProcessorTimes()
    {
        const int size = 48; // SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION x64
        var count = Math.Min(Environment.ProcessorCount, 64);
        var buffer = Marshal.AllocHGlobal(size * count);
        try
        {
            if (NtQuerySystemInformation(8, buffer, size * count, out _) < 0) return (Array.Empty<long>(), Array.Empty<long>());
            var idle = new long[count];
            var busy = new long[count];
            for (var i = 0; i < count; i++)
            {
                var p = buffer + i * size;
                idle[i] = Marshal.ReadInt64(p);
                busy[i] = Marshal.ReadInt64(p, 8) + Marshal.ReadInt64(p, 16);
            }
            return (idle, busy);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    Dictionary<string, double> Values(IntPtr counter) => _pdh is null || counter == IntPtr.Zero ? new() : _pdh.Values(counter);

    // ---------- memory ----------

    static MemoryReading Memory()
    {
        var m = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        GlobalMemoryStatusEx(ref m);
        var info = new PerformanceInformation { cb = Marshal.SizeOf<PerformanceInformation>() };
        GetPerformanceInfo(ref info, info.cb);
        var page = (ulong)info.PageSize;
        return new MemoryReading(m.TotalPhys, m.TotalPhys - m.AvailPhys, (ulong)info.CommitTotal * page, (ulong)info.CommitLimit * page, (ulong)info.SystemCache * page,
            (ulong)info.KernelPaged * page, (ulong)info.KernelNonpaged * page);
    }

    // ---------- disks ----------

    List<DiskReading> Disks()
    {
        var idle = Values(_idle);
        var latency = Values(_latency);
        var read = Values(_read);
        var write = Values(_write);
        var list = new List<DiskReading>();
        // Instances "0 C: D:" (disk number and partition letters). Without the counters, the list stays empty.
        foreach (var instance in idle.Keys.Where(k => k != "_Total").OrderBy(k => k))
        {
            var parts = instance.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!int.TryParse(parts[0], out var number)) continue;
            if (!_diskInfo.TryGetValue(number, out var info)) _diskInfo[number] = info = DiskInfo(number);
            var volumes = parts.Skip(1).Select(Volume).ToList();
            list.Add(new DiskReading(number, info.Model, info.Kind, volumes,
                idle.TryGetValue(instance, out var i) ? Math.Clamp(100 - i, 0, 100) : null,
                latency.TryGetValue(instance, out var l) ? l * 1000 : null,
                read.TryGetValue(instance, out var r) ? r : null, write.TryGetValue(instance, out var w) ? w : null));
        }
        return list;
    }

    static string Volume(string letter)
    {
        try
        {
            var d = new DriveInfo(letter);
            var label = d.IsReady && d.VolumeLabel.Length > 0 ? $" {d.VolumeLabel}" : "";
            return d.IsReady ? $"{letter}{label} ({d.TotalSize / 1e9:0} GB)" : letter;
        }
        catch { return letter; }
    }

    /// <summary>Model and type (NVMe SSD, SSD, HDD) via IOCTL_STORAGE_QUERY_PROPERTY, without opening the disk for reading.</summary>
    static (string Model, string Kind) DiskInfo(int number)
    {
        using var h = CreateFile($@"\\.\PhysicalDrive{number}", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero); // FILE_SHARE_READ|WRITE, OPEN_EXISTING
        if (h.IsInvalid) return ($"Disk {number}", "Disk");
        var buffer = new byte[1024];
        string model = $"Disk {number}";
        var bus = -1;
        if (Query(h, 0, buffer)) // StorageDeviceProperty → STORAGE_DEVICE_DESCRIPTOR
        {
            string Text(int offsetField)
            {
                var offset = BitConverter.ToInt32(buffer, offsetField);
                if (offset <= 0 || offset >= buffer.Length) return "";
                var end = Array.IndexOf(buffer, (byte)0, offset);
                return System.Text.Encoding.ASCII.GetString(buffer, offset, (end < 0 ? buffer.Length : end) - offset).Trim();
            }
            model = $"{Text(12)} {Text(16)}".Trim() is { Length: > 0 } m ? m : model;
            bus = BitConverter.ToInt32(buffer, 28);
        }
        bool? seekPenalty = Query(h, 7, buffer) ? buffer[8] != 0 : null; // StorageDeviceSeekPenaltyProperty
        var kind = bus == 17 ? "SSD (NVMe)" : seekPenalty switch { false => "SSD", true => "HDD", null => "Disk" };
        if (bus == 7) kind += " · USB";
        return (model, kind);
    }

    static bool Query(SafeFileHandle h, int property, byte[] output)
    {
        var query = new byte[12];
        BitConverter.GetBytes(property).CopyTo(query, 0); // PropertyId; QueryType 0 = PropertyStandardQuery
        Array.Clear(output);
        return DeviceIoControl(h, 0x2D1400, query, query.Length, output, output.Length, out _, IntPtr.Zero);
    }

    // ---------- network ----------

    List<NetReading> Networks()
    {
        try { _adapters ??= NetAdapters.List(includeVirtual: false); }
        catch (Exception ex) { Log.Warn($"Listing network adapters failed: {ex.Message}"); _adapters = new(); }
        var nics = NetworkInterface.GetAllNetworkInterfaces();
        var now = Environment.TickCount64;
        var list = new List<NetReading>();
        foreach (var a in _adapters)
        {
            if (nics.FirstOrDefault(n => n.Id.Equals(a.InterfaceId, StringComparison.OrdinalIgnoreCase)) is not { } nic) { list.Add(new NetReading(a.Name, false, 0, 0, 0)); continue; }
            var stats = nic.GetIPStatistics();
            double send = 0, receive = 0;
            if (_net.TryGetValue(nic.Id, out var last) && now > last.Ticks)
            {
                var seconds = (now - last.Ticks) / 1000.0;
                send = Math.Max(0, (stats.BytesSent - last.Sent) * 8 / seconds);
                receive = Math.Max(0, (stats.BytesReceived - last.Received) * 8 / seconds);
            }
            _net[nic.Id] = (stats.BytesSent, stats.BytesReceived, now);
            list.Add(new NetReading(a.Name, nic.OperationalStatus == OperationalStatus.Up, nic.Speed, send, receive));
        }
        return list;
    }

    // ---------- GPU ----------

    GpuDevice? Gpu(out GpuSensors? sensors)
    {
        sensors = null;
        try
        {
            _gpu ??= GpuTuner.List().FirstOrDefault() is { } d ? (d, true) : (new GpuDevice("", ""), false);
            if (!_gpu.Value.Found) return null;
            sensors = GpuTuner.ReadSensors(_gpu.Value.Device.BusId);
            return _gpu.Value.Device;
        }
        catch (Exception) { return _gpu is { Found: true } g ? g.Device : null; } // without NVML/NVIDIA: Home shows only the name, if any
    }

    // ---------- fixed data ----------

    public static MachineInfo Machine()
    {
        using var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        var name = (cpu?.GetValue("ProcessorNameString") as string)?.Trim() ?? "CPU";
        var baseMhz = cpu?.GetValue("~MHz") as int?;
        var (modules, speed, type) = MemoryModules();
        return new MachineInfo(name, PhysicalCores(), Environment.ProcessorCount, baseMhz, modules, speed, type);
    }

    static int PhysicalCores()
    {
        GetLogicalProcessorInformationEx(0, IntPtr.Zero, out var length); // RelationProcessorCore
        if (length <= 0) return Environment.ProcessorCount;
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetLogicalProcessorInformationEx(0, buffer, out length)) return Environment.ProcessorCount;
            var count = 0;
            for (var offset = 0; offset < length; count++) offset += Marshal.ReadInt32(buffer, offset + 4); // Size
            return count;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Memory modules from the SMBIOS table (type 17): count, configured speed (MT/s) and type (DDR4/DDR5).</summary>
    internal static (int Modules, uint? SpeedMts, string? Type) MemoryModules() => Summary(HardwareInfo.Modules(Smbios.Read()));

    /// <summary>The same from a RawSMBIOSData (8-byte header). Separate for testing.</summary>
    internal static (int Modules, uint? SpeedMts, string? Type) ParseSmbiosMemory(byte[] data) => Summary(HardwareInfo.Modules(Smbios.Parse(data)));

    static (int, uint?, string?) Summary(IReadOnlyList<MemoryModule> m) => (m.Count, m.Max(x => x.SpeedMts), m.Select(x => x.Type).FirstOrDefault(t => t is not null));

    public void Dispose() => _pdh?.Dispose();

    // ---------- Win32 ----------

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus { public uint Length, MemoryLoad; public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual; }

    [StructLayout(LayoutKind.Sequential)]
    struct PerformanceInformation
    {
        public int cb;
        public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("psapi.dll")] static extern bool GetPerformanceInfo(ref PerformanceInformation info, int size);
    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int size, out int returned);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetLogicalProcessorInformationEx(int relation, IntPtr buffer, out int length);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
}

/// <summary>Performance counter (PDH) queries using English names, which work on any Windows language. Also used by KOS Governor.</summary>
public sealed class Pdh : IDisposable
{
    const uint FormatDouble = 0x200, NoCap100 = 0x8000;
    const int MoreData = unchecked((int)0x800007D2);
    readonly IntPtr _query;

    public Pdh()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) throw new InvalidOperationException("PdhOpenQuery failed.");
    }

    /// <summary>Counter with a wildcard; IntPtr.Zero if the counter doesn't exist (e.g. disabled counters).</summary>
    public IntPtr Add(string path) => PdhAddEnglishCounter(_query, path, IntPtr.Zero, out var c) == 0 ? c : IntPtr.Zero;

    public void Collect() => PdhCollectQueryData(_query);

    /// <summary>Value of each instance; instances without a valid value (first reading of a rate) are left out.</summary>
    public Dictionary<string, double> Values(IntPtr counter)
    {
        var result = new Dictionary<string, double>();
        uint size = 0;
        if (PdhGetFormattedCounterArray(counter, FormatDouble | NoCap100, ref size, out _, IntPtr.Zero) != MoreData || size == 0) return result;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, FormatDouble | NoCap100, ref size, out var count, buffer) != 0) return result;
            for (var i = 0; i < count; i++)
            {
                var item = buffer + i * 24; // PDH_FMT_COUNTERVALUE_ITEM_W x64: name, status, value
                if (Marshal.ReadInt32(item, 8) is not (0 or 1)) continue; // PDH_CSTATUS_VALID_DATA / NEW_DATA
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                result[name] = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return result;
    }

    public void Dispose() => PdhCloseQuery(_query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, out uint count, IntPtr buffer);
    [DllImport("pdh.dll")] static extern int PdhCloseQuery(IntPtr query);
}
