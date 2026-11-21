using System.Runtime.InteropServices;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.Text;
using Xunit;

namespace KosToolbox.Core.Tests;

// Pure logic only: no test loads NVML or touches the card.
// The live NVAPI tests share one session (a reset in one would close it under another): they run one at a time.
[Collection("NVAPI")]
public class GpuTests
{
    const string Bus = "00000000:04:00.0";

    [Theory]
    [InlineData("0000:04:00.0", true)]
    [InlineData("00000000:04:00.0", true)]
    [InlineData("0000000a:0F:1f.7", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("0000:04:00.8", false)]
    [InlineData("000:04:00.0", false)]
    [InlineData("000000000:04:00.0", false)]
    [InlineData("0000:04-00.0", false)]
    [InlineData("0000:g4:00.0", false)]
    [InlineData("00000000:04:00.0\n", false)]
    public void ValidatesPciBusIds(string? id, bool valid) => Assert.Equal(valid, GpuTuner.IsBusId(id));

    // The engine loads this path elevated: only a driver package folder directly under FileRepository is accepted.
    [Theory]
    [InlineData(@"C:\Windows\System32\DriverStore\FileRepository\nv_dispi.inf_amd64_cee48bd6d33f95ab\nvldumdx.dll", @"C:\Windows\System32\DriverStore\FileRepository\nv_dispi.inf_amd64_cee48bd6d33f95ab\nvml.dll")]
    [InlineData(@"c:\windows\system32\driverstore\filerepository\nv_dispi.inf_amd64_x\nvldumdx.dll", @"c:\windows\system32\driverstore\filerepository\nv_dispi.inf_amd64_x\nvml.dll")]
    [InlineData(@"C:\Windows\System32\DriverStore\FileRepository\nvldumdx.dll", null)]
    [InlineData(@"C:\Windows\System32\DriverStore\FileRepository\pkg\sub\nvldumdx.dll", null)]
    [InlineData(@"C:\Windows\System32\DriverStore\FileRepository\pkg\..\..\..\..\Users\Public\x\nvldumdx.dll", null)]
    [InlineData(@"C:\Users\Public\FileRepository\pkg\nvldumdx.dll", null)]
    [InlineData(@"\\server\share\DriverStore\FileRepository\pkg\nvldumdx.dll", null)]
    [InlineData(@"\\?\C:\Windows\System32\DriverStore\FileRepository\pkg\nvldumdx.dll", null)]
    [InlineData(@"\\.\C:\Windows\System32\DriverStore\FileRepository\pkg\nvldumdx.dll", null)]
    [InlineData(@"DriverStore\FileRepository\pkg\nvldumdx.dll", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void AcceptsOnlyTheDriverStoresNvml(string? userModeDriver, string? expected) =>
        Assert.Equal(expected, Nvml.DriverStorePath(userModeDriver, @"C:\Windows\System32"));

    // Values read from this machine's GTX 1650 SUPER (driver 537.58), plus a fictitious offset range.
    static GpuState State(bool offsets = true) => new()
    {
        BusId = Bus,
        PowerLimitMw = 100000, PowerMinMw = 70000, PowerMaxMw = 100000, PowerDefaultMw = 100000,
        Core = offsets ? new GpuRange(0, -200, 300, false) : null,
        CoreProblem = offsets ? null : "not supported",
        Memory = offsets ? new GpuRange(0, -500, 1000, false) : null,
        MemoryProblem = offsets ? null : "not supported",
        FanCount = 1, FanMin = 40, FanMax = 100, FanAuto = true, FanTarget = 42,
    };

    [Fact]
    public void AcceptsValuesInsideTheDriverLimits()
    {
        Assert.Null(GpuTuner.Problem(new GpuTuning { PciBusId = Bus, PowerLimitMw = 70000, CoreOffsetMhz = -200, MemOffsetMhz = 1000, FanPercent = 40 }, State()));
        Assert.Null(GpuTuner.Problem(new GpuTuning { PciBusId = "00000000:04:00.0".ToLowerInvariant(), FanAuto = true }, State()));
    }

    [Theory]
    [InlineData(69999u, null, null, null, false)] // below the minimum
    [InlineData(100001u, null, null, null, false)] // above the maximum
    [InlineData(null, 301, null, null, false)]
    [InlineData(null, null, -501, null, false)]
    [InlineData(null, null, null, 39u, false)] // below the driver's minimum fan speed
    [InlineData(null, null, null, 101u, false)]
    [InlineData(null, null, null, 50u, true)] // automatic and fixed together
    [InlineData(null, null, null, null, false)] // nothing to apply
    public void RejectsValuesOutsideTheDriverLimits(uint? power, int? core, int? mem, uint? fan, bool auto) =>
        Assert.NotNull(GpuTuner.Problem(new GpuTuning { PciBusId = Bus, PowerLimitMw = power, CoreOffsetMhz = core, MemOffsetMhz = mem, FanPercent = fan, FanAuto = auto }, State()));

    [Theory]
    [InlineData("")]
    [InlineData("04:00.0")]
    [InlineData("00000000:05:00.0")] // another card
    [InlineData("00000000:04:00.0;x")]
    public void RejectsOtherDevices(string bus) =>
        Assert.NotNull(GpuTuner.Problem(new GpuTuning { PciBusId = bus, FanAuto = true }, State()));

    [Fact]
    public void RejectsOffsetsWhenTheDriverDoesNotSupportThem() =>
        Assert.Equal("not supported", GpuTuner.Problem(new GpuTuning { PciBusId = Bus, CoreOffsetMhz = 0 }, State(offsets: false)));

    [Fact]
    public void ReportsWhatTheCardDidNotKeep()
    {
        var t = new GpuTuning { PciBusId = Bus, PowerLimitMw = 90000, FanPercent = 60 };
        Assert.Equal(new[] { Txt.GpuPowerLimit, Txt.GpuFan }, GpuTuner.Unconfirmed(t, State()));
        var kept = State();
        Assert.Empty(GpuTuner.Unconfirmed(new GpuTuning { PciBusId = Bus, PowerLimitMw = 100000, FanAuto = true }, kept));
    }

    [Fact]
    public void ThermalAndVfChangesAreCheckedAgainstTheReadBackDriverState()
    {
        var state = State();
        state = new GpuState
        {
            BusId = state.BusId,
            Thermal = new GpuThermalLimit(83, 65, 87, 83),
            Curve = new GpuVfCurve(new[]
            {
                new GpuVfPoint(0, 450000, 420000, 0),
                new GpuVfPoint(1, 456250, 435000, 0),
            }, -1000000, 1000000),
            LockVoltageUv = 0,
        };
        var valid = new GpuTuning { PciBusId = Bus, TemperatureLimitC = 87,
            CurveChanges = new() { new GpuVfChange(1, 456250, 30000) }, LockVoltageUv = 456250 };
        Assert.Null(GpuTuner.Problem(valid, state));
        Assert.Equal(new[] { Txt.GpuThermalLimit, Txt.GpuVfCurve, Txt.GpuVfLock }, GpuTuner.Unconfirmed(valid, state));
        Assert.NotNull(GpuTuner.Problem(new GpuTuning { PciBusId = Bus, TemperatureLimitC = 88 }, state));
        Assert.NotNull(GpuTuner.Problem(new GpuTuning { PciBusId = Bus,
            CurveChanges = new() { new GpuVfChange(1, 456251, 15000) } }, state));
        Assert.NotNull(GpuTuner.Problem(new GpuTuning { PciBusId = Bus,
            CurveChanges = new() { new GpuVfChange(1, 456250, 1000001) } }, state));
        Assert.NotNull(GpuTuner.Problem(new GpuTuning { PciBusId = Bus, LockVoltageUv = 456251 }, state));
    }

    [Fact]
    public void ThermalSlowdownNeedsTheDriverControlAndIsReadBack()
    {
        var off = new GpuTuning { PciBusId = Bus, ThermalSlowdown = false };
        Assert.Equal("not exported", GpuTuner.Problem(off, new GpuState { BusId = Bus, ThermalSlowdownProblem = "not exported" }));
        var on = new GpuState { BusId = Bus, ThermalSlowdown = true };
        Assert.Null(GpuTuner.Problem(off, on)); // the only value: not "nothing to apply"
        Assert.Equal(new[] { Txt.GpuThermalSlowdown }, GpuTuner.Unconfirmed(off, on));
        Assert.Empty(GpuTuner.Unconfirmed(off, new GpuState { BusId = Bus, ThermalSlowdown = false }));
    }

    [Theory]
    [InlineData("100", 100000u)]
    [InlineData(" 92.5 ", 92500u)]
    [InlineData("92,5", 92500u)]
    [InlineData("0", null)]
    [InlineData("-5", null)]
    [InlineData("abc", null)]
    public void ParsesWatts(string text, uint? mw) => Assert.Equal(mw, GpuTuner.ParseWatts(text));

    [Theory]
    [InlineData("+100", 100)]
    [InlineData("-50", -50)]
    [InlineData("1.5", null)]
    public void ParsesMhz(string text, int? mhz) => Assert.Equal(mhz, GpuTuner.ParseMhz(text));

    [Theory]
    [InlineData("60", 60u)]
    [InlineData("60 %", 60u)]
    [InlineData("-1", null)]
    public void ParsesPercent(string text, uint? pct) => Assert.Equal(pct, GpuTuner.ParsePercent(text));

    [Fact]
    public void StructLayoutsMatchNvmlHeader()
    {
        Assert.Equal(68, Marshal.SizeOf<Nvml.PciInfo>()); // 16 + 5 × 4 + 32
        Assert.Equal(24, Marshal.SizeOf<Nvml.ClockOffset>());
        Assert.Equal(0x01000018u, Nvml.ClockOffsetV1); // NVML_STRUCT_VERSION(ClockOffset, 1)
    }

    [Fact]
    public void ElevatedRequestRejectsMixedOrEmptyGpuJobs()
    {
        // Refused before any access to the card.
        Assert.ThrowsAny<Exception>(() => new ElevatedJob { Gpu = new GpuJob() }.Validate());
        Assert.ThrowsAny<Exception>(() => new ElevatedJob { Gpu = new GpuJob { Tuning = new GpuTuning { PciBusId = Bus, FanAuto = true }, SignIn = false } }.Validate());
        Assert.ThrowsAny<Exception>(() => new ElevatedJob { Gpu = new GpuJob { SignIn = false }, PrepareLogs = true }.Validate());
    }

    [Fact]
    public void GpuJobShapes()
    {
        var t = new GpuTuning { PciBusId = Bus, FanAuto = true };
        Assert.Null(GpuTuner.ShapeProblem(new GpuJob { Tuning = t }));
        Assert.Null(GpuTuner.ShapeProblem(new GpuJob { Saved = t, SignIn = true }));
        Assert.Null(GpuTuner.ShapeProblem(new GpuJob { Tuning = t, Saved = t, SignIn = true }));
        Assert.Null(GpuTuner.ShapeProblem(new GpuJob { SignIn = false }));
        Assert.NotNull(GpuTuner.ShapeProblem(new GpuJob { Tuning = t, Saved = t })); // save without creating the task
        Assert.NotNull(GpuTuner.ShapeProblem(new GpuJob { Tuning = t, SignIn = true })); // enable without the saved set
        Assert.NotNull(GpuTuner.ShapeProblem(new GpuJob { Saved = t, SignIn = false }));
        Assert.NotNull(GpuTuner.ShapeProblem(new GpuJob { Tuning = t, Saved = new GpuTuning { PciBusId = "00000000:05:00.0", FanAuto = true }, SignIn = true }));
    }

    [Fact]
    public void SignInFolderIsRefusedWhenLinkedOrNotOwnedByAdministrators()
    {
        var root = Path.Combine(Path.GetTempPath(), "kos-gpu-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "Toolbox");
        Directory.CreateDirectory(root);
        try
        {
            Assert.Null(GpuTuner.FolderProblem(root, dir)); // doesn't exist yet: it will be created already protected
            // Junction: needs neither administrator nor developer mode.
            var target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
            using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{dir}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })!)
                p.WaitForExit();
            Assert.Contains(dir, GpuTuner.FolderProblem(root, dir));
            Directory.Delete(dir);
            // Folder that a standard user created beforehand (owner = the user themselves): refused, the ACL is not rewritten.
            var made = Directory.CreateDirectory(dir);
            var acl = made.GetAccessControl();
            acl.SetOwner(System.Security.Principal.WindowsIdentity.GetCurrent().User!);
            made.SetAccessControl(acl);
            Assert.NotNull(GpuTuner.FolderProblem(root, dir));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void WhatTheCardHasIsAValidRequestToPutItBack()
    {
        var s = new GpuState
        {
            BusId = Bus, PowerLimitMw = 90000, PowerMinMw = 70000, PowerMaxMw = 100000,
            Core = new GpuRange(50, -200, 300, false), Memory = new GpuRange(0, -500, 1000, false),
            Curve = new GpuVfCurve(new[] { new GpuVfPoint(0, 0, 0, 0), new GpuVfPoint(1, 700000, 1500000, 50000), new GpuVfPoint(2, 750000, 1600000, 60000) }, -200000, 300000),
            LockVoltageUv = 0, ClockLockMhz = 0, FanCount = 1, FanMin = 40, FanMax = 100, FanAuto = false, FanTarget = 55,
        };
        var t = GamemodeGpu.Current(s, fan: true);
        Assert.Null(GpuTuner.Problem(t, s));
        Assert.Empty(GpuTuner.Unconfirmed(t, s));
        Assert.Equal(new[] { 1, 2 }, t.CurveChanges!.Select(c => c.Index));
        Assert.Equal(55u, t.FanPercent);
        // A fan curve drives the fan: it is left out (the curve comes back by itself).
        var noFan = GamemodeGpu.Current(s, fan: false);
        Assert.Null(noFan.FanPercent);
        Assert.False(noFan.FanAuto);
    }

    [Fact]
    public void AGamemodeProfileCantPairItsFanCurveWithThermalSlowdownOff()
    {
        var curve = new GovernorFanCurve { Enabled = true, BusId = Bus, Points = { new(40, 40), new(80, 100) } };
        Assert.NotNull(GamemodeGpu.Problem(new GamemodeGpuProfile { Tuning = new GpuTuning { ThermalSlowdown = false }, Curve = curve }));
        Assert.Null(GamemodeGpu.Problem(new GamemodeGpuProfile { Tuning = new GpuTuning { ThermalSlowdown = true }, Curve = curve }));
        Assert.Null(GamemodeGpu.Problem(new GamemodeGpuProfile { Tuning = new GpuTuning { ThermalSlowdown = false, FanAuto = true } }));
    }

    [Fact]
    public void DisplayAdapterLuidsReadTheSameWhileNothingRestarts()
    {
        var first = DisplayAdapters.Luids();
        Assert.Matches("^[0-9A-F]+:[0-9A-F]+(,[0-9A-F]+:[0-9A-F]+)*$", first); // at least the basic render adapter
        Assert.Equal(first, DisplayAdapters.Luids());
    }

    [Fact]
    public void NvapiDecreaseReasonsMapToTheNvmlBitsTheSessionReportNames()
    {
        Assert.Equal(0UL, Nvapi.ReasonsAsNvml(0));
        Assert.Equal(0x20UL, Nvapi.ReasonsAsNvml(0x1)); // thermal protection: driver thermal limit
        Assert.Equal(0x4UL, Nvapi.ReasonsAsNvml(0x2)); // power control: power limit
        Assert.Equal(0x80UL, Nvapi.ReasonsAsNvml(0x10)); // insufficient power: power brake
        Assert.Equal(0x2UL, Nvapi.ReasonsAsNvml(0x8)); // API triggered: application clock setting
        Assert.Equal(0UL, Nvapi.ReasonsAsNvml(0x4)); // AC/battery: not reported
        Assert.Equal(0xA6UL, Nvapi.ReasonsAsNvml(0x1F));
    }

    [Fact]
    public void NvapiCardsUseTheBusIdFormThatOpensThem()
    {
        try
        {
            foreach (var g in Nvapi.List())
            {
                Assert.Matches(@"^00000000:[0-9A-F]{2}:[0-9A-F]{2}\.0$", g.BusId);
                Nvapi.ReadLight(g.BusId); // the same id opens it (Find)
            }
        }
        catch (NotSupportedException) { } // no NVIDIA driver here
    }
}
