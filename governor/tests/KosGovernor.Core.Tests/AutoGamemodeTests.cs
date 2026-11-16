using KosGovernor.Core;
using Xunit;

namespace KosGovernor.Core.Tests;

public class GameListTests
{
    [Theory]
    [InlineData("cs2.exe", true)]
    [InlineData("CS2.EXE", true)]
    [InlineData("League of Legends.exe", true)]
    [InlineData("VALORANT-Win64-Shipping.exe", true)]
    [InlineData("steam.exe", false)]
    [InlineData("EpicGamesLauncher.exe", false)]
    [InlineData("RiotClientServices.exe", false)]
    [InlineData("LeagueClient.exe", false)]
    [InlineData("upc.exe", false)]
    [InlineData("WoWSLauncher.exe", false)] // launcher that was marked as a game in the original list
    [InlineData("chrome.exe", false)]
    [InlineData("notepad.exe", false)] // not in the list: not a game
    public void TheBuiltInListKnowsGamesAndLaunchers(string exe, bool game) =>
        Assert.Equal(game, new GameList(Array.Empty<GameEntry>()).IsGame(exe));

    [Fact]
    public void NoLauncherIsAGame()
    {
        var games = GameList.BuiltIn.Where(g => !g.IsBlacklisted).Select(g => g.Name).ToList();
        Assert.DoesNotContain(games, n => n.Contains("Launcher", StringComparison.OrdinalIgnoreCase));
        Assert.True(games.Count > 500);
    }

    [Fact]
    public void TheUserListWins()
    {
        var list = new GameList(new[] { new GameEntry("cs2", null, true), new GameEntry("mygame", null, false) });
        Assert.False(list.IsGame("cs2.exe"));
        Assert.True(list.IsGame("mygame.exe"));
        Assert.True(list.IsUserEntry("MYGAME.EXE"));
        Assert.False(list.IsUserEntry("dota2.exe"));
        Assert.Equal("Counter Strike 2", new GameList(Array.Empty<GameEntry>()).Label("cs2.exe"));
    }

    [Fact]
    public void GameNamesAreValidated()
    {
        Assert.Null(Validation.GameNameProblem("game.exe"));
        Assert.Null(Validation.GameNameProblem("League of Legends"));
        Assert.NotNull(Validation.GameNameProblem(" game.exe"));
        Assert.NotNull(Validation.GameNameProblem(".exe"));
        Assert.NotNull(Validation.GameNameProblem("C:\\games\\game.exe"));
    }
}

public class AutoGamemodeTests
{
    static readonly DateTime T0 = new(2026, 1, 1);
    static readonly TimeSpan Delay = TimeSpan.FromSeconds(15);

    [Fact]
    public void TurnsOnWithAGameAndOffAfterTheDelay()
    {
        var a = new AutoGamemode();
        Assert.Equal(AutoGamemode.Step.TurnOn, a.Next(true, false, T0, Delay));
        Assert.Equal(AutoGamemode.Step.None, a.Next(true, true, T0.AddSeconds(2), Delay));
        Assert.Equal(AutoGamemode.Step.None, a.Next(false, true, T0.AddSeconds(4), Delay)); // closed: the delay starts counting
        Assert.Equal(AutoGamemode.Step.None, a.Next(false, true, T0.AddSeconds(18), Delay));
        Assert.Equal(AutoGamemode.Step.TurnOff, a.Next(false, true, T0.AddSeconds(19), Delay));
    }

    [Fact]
    public void AGameThatReopensWithinTheDelayKeepsItOn()
    {
        var a = new AutoGamemode();
        a.Next(true, false, T0, Delay);
        a.Next(false, true, T0.AddSeconds(2), Delay);
        Assert.Equal(AutoGamemode.Step.None, a.Next(true, true, T0.AddSeconds(10), Delay)); // reopened
        Assert.Equal(AutoGamemode.Step.None, a.Next(false, true, T0.AddSeconds(20), Delay)); // the delay restarts
        Assert.Equal(AutoGamemode.Step.TurnOff, a.Next(false, true, T0.AddSeconds(35), Delay));
    }

    [Fact]
    public void TurnedOnByHandStaysOn()
    {
        var a = new AutoGamemode();
        a.Manual(turnedOn: true, gameRunning: false);
        Assert.Equal(AutoGamemode.Step.None, a.Next(true, true, T0, Delay));
        Assert.Equal(AutoGamemode.Step.None, a.Next(false, true, T0.AddMinutes(5), Delay));
    }

    [Fact]
    public void TurnedOffByHandDuringAGameStaysOffUntilNoGameRuns()
    {
        var a = new AutoGamemode();
        a.Next(true, false, T0, Delay);
        a.Manual(turnedOn: false, gameRunning: true);
        Assert.Equal(AutoGamemode.Step.None, a.Next(true, false, T0.AddSeconds(2), Delay));
        Assert.Equal(AutoGamemode.Step.None, a.Next(false, false, T0.AddSeconds(4), Delay)); // the game closed
        Assert.Equal(AutoGamemode.Step.TurnOn, a.Next(true, false, T0.AddSeconds(6), Delay)); // the next one turns it on
    }
}

public class GamemodeSystemTests
{
    [Fact]
    public void SourceGeneratedJsonKeepsTheFileFormat()
    {
        // The options config.json was written with before the source-generated context (reflection).
        var reflection = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        var c = new GovernorConfig { ActiveProfile = 0, Masks = { new CpuMask { Name = "P", Cpus = "0-3" } } };
        c.Active.Rules.Add(new ProcessRule
        {
            Exe = "game.exe", Action = RuleAction.Apply, Priority = CpuPriority.High, Io = IoPriority.High, Memory = MemoryPriority.Normal,
            Efficiency = Efficiency.Off, Affinity = "mask:P", CpuSets = "p-cores",
            Services = { new ServiceRule { Service = "Audiosrv", Priority = CpuPriority.AboveNormal } },
            Threads = { new ThreadRule { Module = "render.dll", Priority = ThreadPriority.Highest, IdealCpu = 2 } },
        });
        c.Active.OnEnable.Start.Add(new StartEntry { Path = @"C:\x\obs.exe", Arguments = "--min" });
        c.Active.System = new GamemodeSystemSettings { PowerPlan = true, PowerPlanId = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"), StopSearch = true };
        c.AutoGamemode.Games.Add(new GameEntry("mygame.exe", "My Game", false));

        var old = System.Text.Json.JsonSerializer.Serialize(c, reflection);
        Assert.Equal(old, System.Text.Json.JsonSerializer.Serialize(c, GovernorJson.Default.GovernorConfig));
        Assert.Equal(old, System.Text.Json.JsonSerializer.Serialize(System.Text.Json.JsonSerializer.Deserialize(old, GovernorJson.Default.GovernorConfig)!, reflection));
        Assert.Contains("\"Priority\": \"High\"", old);
        Assert.NotEmpty(GameList.BuiltIn); // games.json through the context
    }

    [Fact]
    public void DefaultRulesAreValidAndOnlyFillGaps()
    {
        var fresh = new GovernorConfig();
        Assert.Empty(Validation.Problems(fresh, CpuTopology.Current));
        var dwm = fresh.Active.Rules.Single(r => r.Exe == "dwm.exe");
        Assert.False(dwm.Boost);
        Assert.Equal(2, dwm.Threads.Count(t => t.Priority == ThreadPriority.TimeCritical));

        // v2 file: a rule chosen by the user stays as it is, the missing defaults are added once
        var mine = new ProcessRule { Exe = "Discord.exe", Priority = CpuPriority.BelowNormal };
        var old = new GovernorConfig { Version = 2, Profiles = new() { new Profile { Rules = new() { mine } } } };
        ConfigStore.Migrate(old);
        var rules = old.Profiles.Single().Rules;
        Assert.Same(mine, rules.Single(r => r.Exe == "Discord.exe"));
        Assert.Null(mine.Boost);
        Assert.Equal(fresh.Active.Rules.Count, rules.Count);
        Assert.Equal(3, ConfigStore.Migrate(old).Version);
        Assert.Equal(fresh.Active.Rules.Count, rules.Count); // migrating again adds nothing
    }

    [Fact]
    public void OldConfigsMoveToTheNewEngineInterval()
    {
        var old = new GovernorConfig { Version = 1, General = new GeneralSettings { EngineIntervalMs = 2000 } };
        Assert.Equal(3000, ConfigStore.Migrate(old).General.EngineIntervalMs);
        Assert.Equal(3, old.Version);
        var chosen = new GovernorConfig { Version = 1, General = new GeneralSettings { EngineIntervalMs = 1000 } };
        Assert.Equal(1000, ConfigStore.Migrate(chosen).General.EngineIntervalMs); // chosen value stays
        Assert.Equal(3000, new GovernorConfig().General.EngineIntervalMs);
    }

    [Fact]
    public void ThePowerPlanNeedsAPlan()
    {
        var c = new GovernorConfig();
        c.Active.System.PowerPlan = true;
        Assert.Contains(Validation.Problems(c, CpuTopology.Current), p => p.Contains("power plan"));
        c.Active.System.PowerPlanId = Guid.NewGuid();
        Assert.DoesNotContain(Validation.Problems(c, CpuTopology.Current), p => p.Contains("power plan"));
    }

    [Fact]
    public void NothingSelectedMeansNothingApplied()
    {
        var s = new GamemodeSystemSettings();
        Assert.True(s.Any); // efficiency mode for background programs is on by default
        s.BackgroundEfficiency = false;
        Assert.False(s.Any);
        Assert.Null(GamemodeSystem.Apply(s)); // doesn't touch the system
        s.CleanMemory = true;
        Assert.True(s.Any);
    }
}
