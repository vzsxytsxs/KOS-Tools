using System.Runtime.Intrinsics.X86;
using Microsoft.Win32;

namespace KosToolbox.Core.SystemInfo;

/// <summary>One reading; a null field isn't available on this CPU.</summary>
public sealed record CpuSensorReading(double? TemperatureC, double? PackageWatts, bool? ThermalThrottling, bool? PowerLimited);

/// <summary>
/// CPU package temperature, package power and (Intel) throttling flags through PawnIO, elevated only.
/// Intel: IA32_PACKAGE_THERM_STATUS (or the current core's IA32_THERM_STATUS) against TjMax, RAPL package energy.
/// AMD family 17h–1Ah (Zen 1–5): Tctl from SMN 0x59800 under the "Access_PCI" mutex other monitoring tools share, RAPL package energy.
/// </summary>
public sealed class CpuSensors : IDisposable
{
    const uint IntelThermStatus = 0x19C, IntelPackageThermStatus = 0x1B1, IntelTemperatureTarget = 0x1A2, IntelRaplUnit = 0x606, IntelPackageEnergy = 0x611;
    const uint AmdPowerUnit = 0xC0010299, AmdPackageEnergy = 0xC001029B, AmdThmTconCurTmp = 0x00059800;

    readonly PawnIo _io;
    readonly bool _intel, _package;
    readonly double _tjMax, _energyUnit, _amdOffset;
    readonly uint _energyMsr;
    readonly Mutex? _pci;
    ulong? _lastEnergy;
    long _lastTicks;

    /// <summary>What is read, for the report (e.g. "Intel package, TjMax 100 °C").</summary>
    public string Source { get; }

    CpuSensors(PawnIo io, bool intel, bool package, double tjMax, double energyUnit, uint energyMsr, double amdOffset, Mutex? pci, string source) =>
        (_io, _intel, _package, _tjMax, _energyUnit, _energyMsr, _amdOffset, _pci, Source) = (io, intel, package, tjMax, energyUnit, energyMsr, amdOffset, pci, source);

    /// <summary>Opens PawnIO with the module for this CPU. Throws with the reason (not installed, not elevated, CPU not supported).</summary>
    public static CpuSensors Open()
    {
        if (!X86Base.IsSupported) throw new InvalidOperationException("only x64 CPUs are read.");
        var vendor = Vendor();
        if (vendor == "GenuineIntel")
        {
            var io = PawnIo.Open("IntelMSR");
            try
            {
                var tjMax = TjMax(io.Execute("ioctl_read_msr", IntelTemperatureTarget));
                var package = (X86Base.CpuId(6, 0).Eax & (1 << 6)) != 0; // package thermal management
                var unit = EnergyUnit(io.Execute("ioctl_read_msr", IntelRaplUnit));
                return new CpuSensors(io, true, package, tjMax, unit, IntelPackageEnergy, 0, null, $"Intel {(package ? "package" : "core")}, TjMax {tjMax:0} °C");
            }
            catch { io.Dispose(); throw; }
        }
        if (vendor == "AuthenticAMD")
        {
            var eax = X86Base.CpuId(1, 0).Eax;
            var family = (eax >> 8) & 0xF;
            if (family == 0xF) family += (eax >> 20) & 0xFF;
            if (family is < 0x17 or > 0x1A) throw new InvalidOperationException($"AMD family {family:X}h isn't supported (Zen and newer only).");
            var io = PawnIo.Open("AMDFamily17");
            try
            {
                var unit = EnergyUnit(io.Execute("ioctl_read_msr", AmdPowerUnit));
                var offset = AmdTctlOffset(BrandName());
                Mutex? pci;
                try { pci = new Mutex(false, @"Global\Access_PCI"); }
                catch (UnauthorizedAccessException) { pci = Mutex.TryOpenExisting(@"Global\Access_PCI", out var existing) ? existing : null; }
                if (pci is null) throw new InvalidOperationException("the PCI access lock other monitoring tools share couldn't be opened.");
                return new CpuSensors(io, false, true, 0, unit, AmdPackageEnergy, offset, pci, offset > 0 ? $"AMD Tctl − {offset:0} °C" : "AMD Tctl");
            }
            catch { io.Dispose(); throw; }
        }
        throw new InvalidOperationException($"{vendor} CPUs aren't supported.");
    }

    public CpuSensorReading Read()
    {
        double? temp = null;
        bool? throttling = null, limited = null;
        if (_intel)
        {
            var status = _io.Execute("ioctl_read_msr", _package ? IntelPackageThermStatus : IntelThermStatus);
            temp = IntelTemperature(status, _tjMax, _package);
            throttling = (status & 1) != 0;                 // thermal status: PROCHOT or the thermal monitor is limiting now
            if (_package) limited = (status & (1 << 10)) != 0; // package power limitation status
        }
        else if (_pci is not null && Wait(_pci))
        {
            try { temp = AmdTemperature((uint)_io.Execute("ioctl_read_smn", AmdThmTconCurTmp)) - _amdOffset; }
            finally { _pci.ReleaseMutex(); }
        }
        return new CpuSensorReading(temp, Watts(), throttling, limited);
    }

    /// <summary>Package power from the RAPL energy counter since the previous reading (null on the first).</summary>
    double? Watts()
    {
        var energy = _io.Execute("ioctl_read_msr", _energyMsr);
        var now = Environment.TickCount64;
        double? watts = _lastEnergy is { } before && now > _lastTicks ? EnergyWatts(before, energy, _energyUnit, (now - _lastTicks) / 1000.0) : null;
        (_lastEnergy, _lastTicks) = (energy, now);
        return watts;
    }

    static bool Wait(Mutex m)
    {
        try { return m.WaitOne(100); }
        catch (AbandonedMutexException) { return true; } // the owner died holding it: now ours
    }

    /// <summary>TjMax from MSR_TEMPERATURE_TARGET bits 23:16; 100 °C when the CPU reports 0.</summary>
    internal static double TjMax(ulong target) => ((target >> 16) & 0xFF) is var t and > 0 ? t : 100;

    /// <summary>Degrees below TjMax in bits 22:16; the core register also needs its "reading valid" bit 31.</summary>
    internal static double? IntelTemperature(ulong status, double tjMax, bool package) =>
        package || (status & (1UL << 31)) != 0 ? tjMax - ((status >> 16) & 0x7F) : null;

    /// <summary>THM_TCON_CUR_TMP: bits 31:21 in 0.125 °C, minus 49 °C when the range select bit 19 is set.</summary>
    internal static double AmdTemperature(uint value) => ((value >> 21) & 0x7FF) * 0.125 - ((value & (1u << 19)) != 0 ? 49 : 0);

    /// <summary>Energy status unit (bits 12:8 of the RAPL/power unit MSR): 1 / 2^n joules.</summary>
    internal static double EnergyUnit(ulong units) => 1.0 / (1UL << (int)((units >> 8) & 0x1F));

    /// <summary>The 32-bit energy counter wraps around.</summary>
    internal static double EnergyWatts(ulong before, ulong after, double unit, double seconds) => ((after - before) & 0xFFFFFFFF) * unit / seconds;

    /// <summary>First-generation Ryzen X and Threadripper report Tctl above the real temperature by a fixed offset.</summary>
    internal static double AmdTctlOffset(string brand) =>
        brand.Contains("Threadripper 19") || brand.Contains("Threadripper 29") ? 27
        : brand.Contains("1600X") || brand.Contains("1700X") || brand.Contains("1800X") ? 20
        : brand.Contains("2700X") ? 10 : 0;

    static string Vendor()
    {
        var (_, b, c, d) = X86Base.CpuId(0, 0);
        Span<int> words = stackalloc int[] { b, d, c };
        return System.Text.Encoding.ASCII.GetString(System.Runtime.InteropServices.MemoryMarshal.AsBytes(words));
    }

    static string BrandName()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return k?.GetValue("ProcessorNameString") as string ?? "";
    }

    public void Dispose()
    {
        _io.Dispose();
        _pci?.Dispose();
    }
}
