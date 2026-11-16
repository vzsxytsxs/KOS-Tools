using KosGovernor.Core;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.SystemInfo;
using Xunit;

namespace KosGovernor.Core.Tests;

public class SessionReportTests
{
    static SessionLine Find(GamemodeSession r, string label) => r.Sections.SelectMany(s => s.Lines).First(l => l.Label.StartsWith(label));

    [Fact]
    public void GpuLimitsCountOnlyUnderLoadAndThermalOnesWarn()
    {
        var rec = new SessionRecorder("Default", live: false);
        rec.AddGpu(new GpuSensors { GpuLoad = 10, TemperatureC = 40, ThrottleReasons = 0x1 | 0x40, GraphicsMhz = 300 }); // idle: not counted
        rec.AddGpu(new GpuSensors { GpuLoad = 98, TemperatureC = 84, ThrottleReasons = 0x4, GraphicsMhz = 1900, PowerMw = 250_000, PowerLimitMw = 250_000 });
        rec.AddGpu(new GpuSensors { GpuLoad = 97, TemperatureC = 86, ThrottleReasons = 0x4 | 0x20, GraphicsMhz = 1700, VramUsed = 7_900UL * 1048576, VramTotal = 8_192UL * 1048576 });
        var r = rec.Build(DateTime.Now, Array.Empty<StabilityEvent>());

        Assert.Equal("100% of the time under load", Find(r, "Limited by: Power limit").Value);
        Assert.Equal("50% of the time under load", Find(r, "Limited by: Thermal limit (driver)").Value);
        Assert.False(Find(r, "Limited by: Power limit").Warning);
        Assert.True(Find(r, "Limited by: Thermal limit (driver)").Warning);
        Assert.DoesNotContain(r.Sections.SelectMany(s => s.Lines), l => l.Label.Contains("hardware)"));
        Assert.Equal("average 1800 MHz, lowest 1700 MHz", Find(r, "Clock under load").Value);
        Assert.Equal("peak 86 °C", r.Sections.First(s => s.Title.StartsWith("GPU")).Lines.First(l => l.Label == "Temperature").Value);
        Assert.True(Find(r, "VRAM").Warning);
        Assert.EndsWith("GPU peak 86 °C · 2 warnings: VRAM full, Thermal limit (driver)", r.Summary);
    }

    [Fact]
    public void InterferenceAndEventsBecomeWarnings()
    {
        var rec = new SessionRecorder("Default", live: false);
        var at = DateTime.Now;
        SessionRecorder.Count(rec.Removed, "Headphones (USB Audio)");
        SessionRecorder.Count(rec.Removed, "Headphones (USB Audio)");
        rec.FocusLost["Discord.exe"] = 2;
        rec.GameSeen = true;
        var tdr = new StabilityEvent(at, StabilityKind.DriverReset, "Display driver reset (TDR)", "nvlddmkm stopped responding", false);
        var r = rec.Build(at.AddMinutes(5), new[] { tdr });

        Assert.Equal("2 times: Discord.exe (2)", Find(r, "Game lost focus").Value);
        Assert.True(Find(r, "Disconnected").Warning);
        Assert.StartsWith("Headphones (USB Audio) · 2 times, first at", Find(r, "Disconnected").Value);
        Assert.True(Find(r, "Display driver reset (TDR) at").Warning);
        Assert.Equal("Not recorded", Find(r, "Not recorded").Label); // no GPU readings
        Assert.EndsWith("2 warnings: Headphones (USB Audio) disconnected, Display driver reset (TDR)", r.Summary);
    }

    [Fact]
    public void RecordsThisPc()
    {
        // Read-only: counters, devices, network adapters, the NVIDIA card if any and the System log, over two samples.
        using var rec = new SessionRecorder("Default");
        var usage = new CpuUsage();
        for (var i = 0; i < 3; i++)
        {
            var snapshot = Snapshot.Take();
            usage.Update(snapshot, Environment.TickCount64, Environment.ProcessorCount);
            rec.Tick(snapshot, usage, _ => false, n => n);
            Thread.Sleep(1050);
        }
        var r = rec.Finish();
        Assert.StartsWith("average", Find(r, "Load").Value);
        Assert.Contains(r.Sections, s => s.Title == "Memory");
        Assert.Equal("Not tracked: no listed game was open", Find(r, "Game focus").Value);
        if (Environment.GetEnvironmentVariable("KOS_TEST_REPORT") is { Length: > 0 } path)
            File.WriteAllLines(path, r.Sections.SelectMany(s => s.Lines.Select(l => $"{s.Title} | {l.Label} | {l.Value}{(l.Warning ? " [!]" : "")}")).Prepend(r.Summary));
    }

    [Fact]
    public void CpuSensorsThroughPawnIo()
    {
        var rec = new SessionRecorder("Default", live: false) { CpuSensorSource = "Intel package, TjMax 100 °C" };
        rec.AddCpu(new CpuSensorReading(70, null, false, false));
        rec.AddCpu(new CpuSensorReading(96, 88, true, true));
        var r = rec.Build(DateTime.Now, Array.Empty<StabilityEvent>());
        var cpu = r.Sections.First(s => s.Title == "CPU").Lines;
        Assert.Equal("average 83 °C, peak 96 °C (Intel package, TjMax 100 °C)", cpu.First(l => l.Label == "Temperature").Value);
        Assert.Equal("50% of the time", cpu.First(l => l.Label == "Thermal throttling").Value);
        Assert.Equal("average 88 W, peak 88 W", cpu.First(l => l.Label == "Package power").Value);
        Assert.Contains("2 warnings: CPU temperature, CPU thermal throttling", r.Summary);

        var none = new SessionRecorder("Default", live: false) { CpuSensorProblem = "install PawnIO from the toolbox's Setup page to record it" };
        Assert.Equal("Not recorded: install PawnIO from the toolbox's Setup page to record it",
            none.Build(DateTime.Now, Array.Empty<StabilityEvent>()).Sections.First(s => s.Title == "CPU").Lines.First(l => l.Label == "Temperature").Value);
    }

    [Fact]
    public void Durations()
    {
        Assert.Equal("45 s", SessionRecorder.Duration(TimeSpan.FromSeconds(45)));
        Assert.Equal("12 min", SessionRecorder.Duration(TimeSpan.FromMinutes(12.5)));
        Assert.Equal("1 h 5 min", SessionRecorder.Duration(TimeSpan.FromMinutes(65)));
    }
}
