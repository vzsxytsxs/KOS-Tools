using System.Text.Json;
using KosGovernor.Core;
using Xunit;

namespace KosGovernor.Core.Tests;

public class ThreadDirectorTests
{
    // Two CCDs of 4 cores with SMT (core = CPU / 2); CCD 0 has the V-Cache.
    static readonly CpuTopology X3d = new(Enumerable.Range(0, 16).Select(i => new LogicalCpu(i, (uint)(256 + i), i / 2, 0, i < 8 ? 0 : 8)),
        new Dictionary<int, long> { [0] = 96L << 20, [8] = 32L << 20 });
    // 6 P-cores and 6 E-cores, no SMT.
    static readonly CpuTopology Hybrid = new(Enumerable.Range(0, 12).Select(i => new LogicalCpu(i, (uint)(256 + i), i, i < 6 ? 1 : 0)));
    static readonly CpuTopology Plain = new(Enumerable.Range(0, 4).Select(i => new LogicalCpu(i, (uint)(256 + i), i, 0)));

    [Fact]
    public void GameCoresFollowTheProcessor()
    {
        Assert.Equal(0x00FFUL, ThreadDirector.GameCores(X3d, 0));
        Assert.Equal(0x3FUL, ThreadDirector.GameCores(Hybrid, 0));
        Assert.Equal(0xFUL, ThreadDirector.GameCores(Plain, 0));
        Assert.Equal(0xF0UL, ThreadDirector.GameCores(X3d, 0xF0)); // CPU sets the game already has win
    }

    [Fact]
    public void ReservesWholeCoresAwayFromInterrupts()
    {
        // Without a trace the core of CPU 0 comes last.
        Assert.Equal(new[] { 0x0CUL, 0x30UL }, ThreadDirector.Reserve(X3d, 0x00FF, 2, null));
        // The trace saw most interrupt work on CPUs 2 and 3 (core 1) and a little on CPU 0: cores 2 and 3.
        var irq = new Dictionary<int, double> { [0] = 1, [2] = 40, [3] = 5 };
        Assert.Equal(new[] { 0x30UL, 0xC0UL }, ThreadDirector.Reserve(X3d, 0x00FF, 2, irq));
        // At least two cores stay for the rest of the game.
        Assert.Equal(2, ThreadDirector.Reserve(X3d, 0x00FF, 4, null).Count);
        Assert.Empty(ThreadDirector.Reserve(Plain, 0x3, 1, null));
        // Only whole cores among the game's CPUs: CPU 1 alone isn't one.
        Assert.DoesNotContain(0x2UL, ThreadDirector.Reserve(X3d, 0x00FE, 3, null));
    }

    [Fact]
    public void OtherProgramsGoWhereTheGameIsnt()
    {
        Assert.Equal(0xFF00UL, ThreadDirector.OthersMask(0xFFFF, 0x00FF, 0x0030)); // the other CCD
        Assert.Equal(0xFC0UL, ThreadDirector.OthersMask(0xFFF, 0x3F, 0x6));       // the E-cores
        Assert.Equal(0xBUL, ThreadDirector.OthersMask(0xF, 0xF, 0x4));            // one CCD: off the busiest threads' cores
    }

    [Fact]
    public void BusiestThreadsGetTheFreeCoresAndKeepThem()
    {
        var measured = new List<(int, double, ulong)> { (1, 0.9, 900), (2, 0.6, 1200), (3, 0.3, 300), (4, 0.5, 500) };
        var (release, add) = ThreadDirector.Choose<int>(measured, new HashSet<int>(), 2);
        Assert.Empty(release);
        Assert.Equal(new[] { 2, 1 }, add); // most cycles first, at least 40 % of a core
        // Thread 3 keeps its core at 30 % (over 20 %); thread 9 is gone: its core goes to the busiest newcomer.
        (release, add) = ThreadDirector.Choose<int>(measured, new HashSet<int> { 3, 9 }, 2);
        Assert.Equal(new[] { 9 }, release);
        Assert.Equal(new[] { 2 }, add);
    }

    [Fact]
    public void JournalSurvivesTheStateFile()
    {
        var state = new GamemodeSystemState();
        state.Isolated.Add(new DirectedThread { Pid = 10, Tid = 11, Create = 12, Mask = 0x30, Ideal = 4 });
        state.Placed.Add(new PlacedProcess { Pid = 20, Create = 21, Mask = 0xFF00, Previous = 0xF });
        state.LoweredRealtime.Add(new ThrottledProcess { Pid = 30, Create = 31 });
        state.LoweredThreads.Add(new DirectedThread { Pid = 40, Tid = 41, Create = 42 });
        var back = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, GovernorJson.Default.GamemodeSystemState), GovernorJson.Default.GamemodeSystemState)!;
        Assert.Equal((11, 0x30UL, 4), (back.Isolated.Single().Tid, back.Isolated.Single().Mask, back.Isolated.Single().Ideal));
        Assert.Equal((0xFF00UL, 0xFUL), (back.Placed.Single().Mask, back.Placed.Single().Previous));
        Assert.Equal(30, back.LoweredRealtime.Single().Pid);
        Assert.Equal(-1, back.LoweredThreads.Single().Ideal);
    }
}
