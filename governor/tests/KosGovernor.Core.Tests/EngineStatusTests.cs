using KosGovernor.Core;
using Xunit;

namespace KosGovernor.Core.Tests;

public class EngineStatusTests
{
    /// <summary>Runs with a fake cycle counter and a fake private-bytes reading, both advanced by the test.</summary>
    static void WithFakeClock(Action<Action<long, long>> test)
    {
        var (cycles, bytes) = (TaskMeter.ThreadCycles, TaskMeter.PrivateBytes);
        long c = 0, b = 0;
        try
        {
            TaskMeter.Reset();
            TaskMeter.ThreadCycles = () => c;
            TaskMeter.PrivateBytes = () => b;
            test((dc, db) => { c += dc; b += db; });
        }
        finally
        {
            (TaskMeter.ThreadCycles, TaskMeter.PrivateBytes) = (cycles, bytes);
            TaskMeter.On = false;
            TaskMeter.Reset();
        }
    }

    [Fact]
    public void NestedScopeIsTakenOutOfTheOuterOne() => WithFakeClock(work =>
    {
        TaskMeter.On = true;
        using (TaskMeter.Measure(EngineTask.Rules))
        {
            work(100, 10);
            using (TaskMeter.Measure(EngineTask.GpuProfile)) work(40, 4);
            using (TaskMeter.Measure(EngineTask.Peripherals)) work(25, 2);
            work(10, 1);
        }
        Assert.Equal(110, TaskMeter.TaskCycles(EngineTask.Rules));
        Assert.Equal(40, TaskMeter.TaskCycles(EngineTask.GpuProfile));
        Assert.Equal(25, TaskMeter.TaskCycles(EngineTask.Peripherals));
        Assert.Equal(11, TaskMeter.TaskBytes(EngineTask.Rules));
        Assert.Equal(4, TaskMeter.TaskBytes(EngineTask.GpuProfile));
    });

    [Fact]
    public void AnotherThreadsScopeIsTakenOutOfAnOverlappingOne() => WithFakeClock(work =>
    {
        TaskMeter.On = true;
        using (TaskMeter.Measure(EngineTask.Peripherals)) // the battery scan's slow first round
        {
            work(0, 3);
            var worker = new Thread(() => { using (TaskMeter.Measure(EngineTask.FanCurve)) work(0, 20_000); }); // NVML opening meanwhile
            worker.Start();
            worker.Join();
            work(0, 2);
        }
        Assert.Equal(20_000, TaskMeter.TaskBytes(EngineTask.FanCurve));
        Assert.Equal(5, TaskMeter.TaskBytes(EngineTask.Peripherals));
    });

    [Fact]
    public void ClosedWindowMeasuresOnlyATasksFirstRun() => WithFakeClock(work =>
    {
        using (TaskMeter.Measure(EngineTask.FanCurve)) work(50, 5000); // first run: what starting it took
        using (TaskMeter.Measure(EngineTask.FanCurve)) work(70, 7);
        Assert.Equal(50, TaskMeter.TaskCycles(EngineTask.FanCurve));
        Assert.Equal(5000, TaskMeter.TaskBytes(EngineTask.FanCurve));
        TaskMeter.On = true;
        using (TaskMeter.Measure(EngineTask.FanCurve)) work(30, 3);
        Assert.Equal(80, TaskMeter.TaskCycles(EngineTask.FanCurve));
    });

    [Fact]
    public void SharesSplitTheProcessPercentByCycles()
    {
        var shares = TaskMeter.Shares(new long[] { 500, 250, 0 }, 1000, 2.0);
        Assert.Equal(new[] { 1.0, 0.5, 0.0 }, shares);
        Assert.All(TaskMeter.Shares(new long[] { 5 }, 0, 2.0), s => Assert.Equal(0, s)); // nothing ran
        Assert.Equal(2.0, TaskMeter.Shares(new long[] { 1500 }, 1000, 2.0)[0]); // never above the process itself
    }

    [Fact]
    public void LinesFollowTheFactsAndTheRestIsWhatNoTaskTook()
    {
        var tasks = new TaskMeter.Usage[Enum.GetValues<EngineTask>().Length];
        tasks[(int)EngineTask.Rules] = new(0.30, 6_000_000);
        tasks[(int)EngineTask.Peripherals] = new(0.10, 1_000_000);
        var view = EngineStatus.Build(new EngineFacts
        {
            Profile = "Profile 1", Rules = 45, Gamemode = true, GamemodeGame = "Valorant", Batteries = 1, PeripheralProfiles = 2,
            PeripheralProfilesApplied = true, TimerHeld = 5070, FanCurveRunning = true,
        }, new TaskMeter.Sample(0.5, 20_000_000, tasks));

        StatusLine L(string name) => view.Lines.Single(l => l.Name == name);
        Assert.Equal("45 rules · Profile 1", L("Process rules").State);
        Assert.Equal("On · Valorant", L("Gamemode").State);
        Assert.Null(L("Gamemode").CpuPercent); // its work is in the rules' tick
        Assert.Equal("1 battery · gamemode profile applied (2 devices)", L("Peripherals").State);
        Assert.Equal("0.507 ms held", L("Timer resolution").State);
        Assert.True(L("GPU fan curve").Active);
        Assert.False(L("RTSS overlay").Active);
        Assert.Equal(0.10, L("Everything else").CpuPercent!.Value, 6);
        Assert.Equal(13_000_000, L("Everything else").Bytes);
    }

    [Fact]
    public void EstimatesNeverTakeMoreThanTheProcessHoldsAboveItsBase()
    {
        var tasks = new TaskMeter.Usage[Enum.GetValues<EngineTask>().Length];
        tasks[(int)EngineTask.FanCurve] = new(0, 30_000_000);  // took 30 MB at start, gave most of it back
        tasks[(int)EngineTask.Peripherals] = new(0, 10_000_000);
        var view = EngineStatus.Build(new EngineFacts(), new TaskMeter.Sample(0, 25_000_000, tasks, BaseBytes: 5_000_000));
        Assert.Equal(15_000_000, view.Lines.Single(l => l.Name == "GPU fan curve").Bytes);
        Assert.Equal(5_000_000, view.Lines.Single(l => l.Name == "Peripherals").Bytes);
        Assert.Equal(5_000_000, view.Lines.Single(l => l.Name == "Everything else").Bytes); // the base is left alone
    }

    [Fact]
    public void Formats()
    {
        Assert.Equal("0 %", EngineStatus.Cpu(0));
        Assert.Equal("< 0.01 %", EngineStatus.Cpu(0.001));
        Assert.Equal("0.03 %", EngineStatus.Cpu(0.031));
        Assert.Equal("≈ 512 KB", EngineStatus.Memory(512 * 1024, true));
        Assert.Equal("21.4 MB", EngineStatus.Memory(22_439_526, false));
        Assert.Equal("Off · turns on when a game starts", EngineStatus.Build(new EngineFacts { AutoGamemode = true },
            new TaskMeter.Sample(0, 0, new TaskMeter.Usage[Enum.GetValues<EngineTask>().Length])).Lines[1].State);
    }
}
