using KosGovernor.Core;
using Xunit;

namespace KosGovernor.Core.Tests;

public class GameExtrasTests
{
    static ProcessEntry P(int pid, int parent, string name, long create, long cpu = 0) =>
        new(pid, parent, name, create, 8, 0, 1, 0, Array.Empty<ThreadEntry>(), cpu);

    [Fact]
    public void CpuUsageIsTheShareOfTheWholeMachine()
    {
        var usage = new CpuUsage();
        usage.Update(new[] { P(10, 1, "a.exe", 100, cpu: 0) }, 1_000, cpus: 4);
        // 1 s later the process used 2 s of CPU time (2 of 4 processors busy) = 50 %.
        var later = P(10, 1, "a.exe", 100, cpu: 2 * 10_000_000);
        usage.Update(new[] { later }, 2_000, cpus: 4);
        Assert.Equal(50, usage.Percent(later), 3);
        // Same pid, another start time: a new process, no history.
        var reused = P(10, 1, "b.exe", 200, cpu: 5 * 10_000_000);
        usage.Update(new[] { reused }, 3_000, cpus: 4);
        Assert.Equal(0, usage.Percent(reused));
    }

    [Theory]
    [InlineData(0b1111UL, 1, 0b0111UL)]
    [InlineData(0b1111UL, 3, 0b0001UL)]
    [InlineData(0b1111UL, 9, 0b0001UL)]
    [InlineData(0b1010UL, 1, 0b0010UL)]
    public void LimiterTakesTheHighestProcessorsAndLeavesOne(ulong mask, int cores, ulong expected) =>
        Assert.Equal(expected, CpuLimiter.Without(mask, cores));

    [Fact]
    public void LimiterLeavesASingleProcessorAlone() => Assert.Null(CpuLimiter.Without(0b100UL, 2));

    [Fact]
    public void GameTreeHasTheGameWhatItOpenedAndItsLauncher()
    {
        var snapshot = new List<ProcessEntry>
        {
            P(1, 0, "steam.exe", 10),
            P(2, 1, "game.exe", 20),
            P(3, 2, "crashhandler.exe", 30),
            P(4, 3, "helper.exe", 40),
            P(5, 1, "steamwebhelper.exe", 15), // the launcher's own child stays out
            P(6, 2, "old.exe", 5),            // its parent pid was reused: started before the game
        };
        var tree = Engine.GameTree(snapshot, n => n == "game.exe");
        Assert.Equal(new[] { 1, 2, 3, 4 }, tree.Order());
    }

    [Theory]
    [InlineData("EasyAntiCheat_EOS.exe", true)]
    [InlineData("vgc.exe", true)]
    [InlineData("BEService.exe", true)]
    [InlineData("discord.exe", false)]
    public void KnowsAntiCheats(string name, bool expected) => Assert.Equal(expected, GamemodeSystem.IsAntiCheat(name));

    [Fact]
    public void ValidatesTheNewRuleFields()
    {
        var topo = new CpuTopology(Enumerable.Range(0, 4).Select(i => new LogicalCpu(i, (uint)(256 + i), i, 0)));
        string? Problem(ProcessRule r) => Validation.RuleProblem(r, new[] { r }, new List<CpuMask>(), topo);
        Assert.Null(Problem(new ProcessRule { Exe = "a.exe", CpuLimit = new CpuLimit { Cores = 3 } }));
        Assert.NotNull(Problem(new ProcessRule { Exe = "a.exe", CpuLimit = new CpuLimit { Cores = 4 } })); // would leave none
        Assert.NotNull(Problem(new ProcessRule { Exe = "a.exe", CpuLimit = new CpuLimit { Percent = 0 } }));
        Assert.Null(Problem(new ProcessRule { Exe = "a.exe", DisplayMode = new DisplayModeSetting { Width = 1280, Height = 960 } }));
        Assert.NotNull(Problem(new ProcessRule { Exe = "a.exe", DisplayMode = new DisplayModeSetting { Width = 0, Height = 960 } }));
        Assert.NotNull(Problem(new ProcessRule { Exe = "a.exe", Action = RuleAction.Terminate, PowerPlan = Guid.NewGuid() }));
    }

    [Fact]
    public void OldConfigsLoadWithTheNewDefaults()
    {
        var c = System.Text.Json.JsonSerializer.Deserialize("""{"Version":3,"Profiles":[{"Name":"P","Rules":[{"Exe":"a.exe"}]}]}""", GovernorJson.Default.GovernorConfig)!;
        Assert.Equal(TimerMode.Off, c.Timer.Mode);
        Assert.Equal(5000, c.Timer.Resolution);
        Assert.Equal(60, c.Profiles[0].System.CleanMemoryDelaySeconds);
        Assert.False(c.Profiles[0].Rules[0].HasExtras);
    }
}
