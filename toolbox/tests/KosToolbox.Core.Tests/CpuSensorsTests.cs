using KosToolbox.Core.SystemInfo;
using Xunit;

namespace KosToolbox.Core.Tests;

public class CpuSensorsTests
{
    [Fact]
    public void IntelTemperatureIsTjMaxMinusTheReadout()
    {
        Assert.Equal(100, CpuSensors.TjMax(0x0064_0000));
        Assert.Equal(100, CpuSensors.TjMax(0)); // not reported
        Assert.Equal(95, CpuSensors.TjMax(0x0A5F_0000)); // offset bits 29:24 ignored
        Assert.Equal(58, CpuSensors.IntelTemperature(0x882A_0000, 100, package: false)); // 42 below, reading valid
        Assert.Null(CpuSensors.IntelTemperature(0x082A_0000, 100, package: false));      // core reading not valid
        Assert.Equal(64, CpuSensors.IntelTemperature(0x0024_0001, 100, package: true));
    }

    [Fact]
    public void AmdTctlAndOffsets()
    {
        Assert.Equal(65, CpuSensors.AmdTemperature(520u << 21));                 // 520 × 0.125
        Assert.Equal(65, CpuSensors.AmdTemperature((912u << 21) | (1u << 19)));  // 114 − 49 with the range select bit
        Assert.Equal(20, CpuSensors.AmdTctlOffset("AMD Ryzen 7 1800X Eight-Core Processor"));
        Assert.Equal(10, CpuSensors.AmdTctlOffset("AMD Ryzen 7 2700X Eight-Core Processor"));
        Assert.Equal(0, CpuSensors.AmdTctlOffset("AMD Ryzen 7 7800X3D 8-Core Processor"));
    }

    [Fact]
    public void PackagePowerFromTheWrappingEnergyCounter()
    {
        var unit = CpuSensors.EnergyUnit(0x000A_0E03); // bits 12:8 = 14 → 1/16384 J
        Assert.Equal(1.0 / 16384, unit);
        Assert.Equal(50, CpuSensors.EnergyWatts(1000, 1000 + 819_200, unit, 1.0), 6);
        Assert.Equal(50, CpuSensors.EnergyWatts(0xFFFF_FF00, 819_200 - 0x100, unit, 1.0), 6); // wrapped past 2^32
    }

    [Fact]
    public void ModulesAreEmbedded()
    {
        Assert.True(PawnIo.Module("IntelMSR").Length > 1000);
        Assert.True(PawnIo.Module("AMDFamily17").Length > 1000);
    }

    [Fact]
    public void ReadsThisCpuWhenPawnIoIsThere()
    {
        // Only elevated with PawnIO installed; read-only (MSR/SMN reads the modules allow).
        if (PawnIo.InstallFolder is null || !Execution.Elevation.IsAdmin) return;
        using var cpu = CpuSensors.Open();
        var first = cpu.Read();
        Thread.Sleep(300);
        var second = cpu.Read();
        Assert.InRange(second.TemperatureC ?? -1, 10, 110);
        Assert.Null(first.PackageWatts);
        Assert.InRange(second.PackageWatts ?? -1, 0.1, 500);
        if (Environment.GetEnvironmentVariable("KOS_TEST_REPORT") is { Length: > 0 } path)
            File.WriteAllText(path, $"{cpu.Source}: {second}");
    }
}
