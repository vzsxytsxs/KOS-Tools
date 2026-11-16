using System.Diagnostics;
using KosGovernor.Core;
using Xunit;

namespace KosGovernor.Core.Tests;

public class CpuSpecTests
{
    // 4 cores with SMT (0/1, 2/3, 4/5, 6/7); the last two are E-cores (class 0).
    static readonly CpuTopology Hybrid = new(Enumerable.Range(0, 8).Select(i => new LogicalCpu(i, (uint)(256 + i), i / 2 * 2, i < 4 ? 1 : 0)));
    static readonly CpuTopology Plain = new(Enumerable.Range(0, 4).Select(i => new LogicalCpu(i, (uint)(256 + i), i, 0)));
    static readonly List<CpuMask> Masks = new() { new CpuMask { Name = "Game", Cpus = "2-5" } };

    [Theory]
    [InlineData("all", 0xFFUL)]
    [InlineData("smt-off", 0b01010101UL)]
    [InlineData("p-cores", 0x0FUL)]
    [InlineData("e-cores", 0xF0UL)]
    [InlineData("none", 0UL)]
    [InlineData("mask:game", 0b00111100UL)]
    [InlineData("0-1, 7", 0b10000011UL)]
    public void Resolves(string spec, ulong expected) => Assert.Equal(expected, CpuSpec.Resolve(spec, Hybrid, Masks, out _));

    [Theory]
    [InlineData("8")]
    [InlineData("3-1")]
    [InlineData("a")]
    [InlineData("mask:missing")]
    public void RejectsBadSpecs(string spec)
    {
        Assert.Null(CpuSpec.Resolve(spec, Hybrid, Masks, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void PCoresNeedAHybridCpu() => Assert.Null(CpuSpec.Resolve(CpuSpec.PCores, Plain, Masks, out _));

    [Fact]
    public void FormatsRanges() => Assert.Equal("0-2,5,7-8", CpuSpec.FormatList(0b110100111));

    [Fact]
    public void MapsCpuSetIds()
    {
        Assert.Equal(new uint[] { 258, 259 }, Hybrid.CpuSetIds(0b1100));
        Assert.Equal(0b1100UL, Hybrid.MaskFromCpuSetIds(new uint[] { 258, 259, 999 }));
        Assert.Equal(4, Hybrid.Cores.Count);
        Assert.True(Hybrid.HasSmt && Hybrid.IsHybrid && !Plain.HasSmt && !Plain.IsHybrid);
    }

    // Two CCDs of 4 cores with SMT; CCD 0 has the 3D V-Cache (96 MB of L3 against 32 MB).
    static readonly CpuTopology X3d = new(Enumerable.Range(0, 16).Select(i => new LogicalCpu(i, (uint)(256 + i), i / 2, 0, i < 8 ? 0 : 8)),
        new Dictionary<int, long> { [0] = 96L << 20, [8] = 32L << 20 });

    [Fact]
    public void FindsCcdsAndTheVCache()
    {
        Assert.Equal(0x00FFUL, CpuSpec.Resolve("ccd:0", X3d, Masks, out _));
        Assert.Equal(0xFF00UL, CpuSpec.Resolve("ccd:1", X3d, Masks, out _));
        Assert.Null(CpuSpec.Resolve("ccd:2", X3d, Masks, out _));
        Assert.Equal(0x00FFUL, CpuSpec.Resolve(CpuSpec.VCache, X3d, Masks, out _));
        Assert.Equal(0xFF00UL, CpuSpec.Resolve(CpuSpec.FrequencyCcd, X3d, Masks, out _));
        Assert.Equal(0xFFFCUL, CpuSpec.Resolve(CpuSpec.NoCore0, X3d, Masks, out _));
        Assert.Equal(new[] { "smt-off", "ccd:0", "ccd:1", "vcache", "frequency-ccd", "no-core-0" }, CpuSpec.Presets(X3d).Select(p => p.Spec));
        Assert.Equal(new[] { "CCD 0 · 3D V-Cache", "CCD 1" }, X3d.Groups().Select(g => g.Title));
        // The same L3 on both CCDs, or a single CCD: no V-Cache and no CCD presets.
        Assert.Null(CpuSpec.Resolve(CpuSpec.VCache, new CpuTopology(X3d.Cpus, new Dictionary<int, long> { [0] = 32L << 20, [8] = 32L << 20 }), Masks, out _));
        Assert.Null(CpuSpec.Resolve("ccd:0", Plain, Masks, out _));
    }

    [Fact]
    public void FindsLowPowerECores()
    {
        // 2 P-cores (class 1) and 2 E-cores share cache 0; the 2 LP E-cores have their own (cache 4).
        var topo = new CpuTopology(new[] { new LogicalCpu(0, 256, 0, 1), new LogicalCpu(1, 257, 1, 1), new LogicalCpu(2, 258, 2, 0),
            new LogicalCpu(3, 259, 3, 0), new LogicalCpu(4, 260, 4, 0, 4), new LogicalCpu(5, 261, 5, 0, 4) });
        Assert.Equal(0b110000UL, CpuSpec.Resolve(CpuSpec.LpECores, topo, Masks, out _));
        Assert.Null(CpuSpec.Resolve("ccd:1", topo, Masks, out _)); // the LP cluster isn't a CCD
        Assert.Null(CpuSpec.Resolve(CpuSpec.LpECores, Hybrid, Masks, out _));
        Assert.Equal(new[] { "P-cores", "E-cores", "LP E-cores" }, topo.Groups().Select(g => g.Title));
    }

    [Fact]
    public void ChildProgramsFollowTheirParent()
    {
        var child = new ProcessEntry(20, 10, "game.exe", 500, 8, 0, 1, 0, Array.Empty<ThreadEntry>());
        Assert.True(Engine.Opened(100, child, new Dictionary<int, long> { [10] = 100 }));   // parent still open
        Assert.True(Engine.Opened(100, child, new Dictionary<int, long>()));               // parent closed after opening it
        Assert.False(Engine.Opened(100, child, new Dictionary<int, long> { [10] = 400 })); // pid reused by another process
        Assert.False(Engine.Opened(600, child, new Dictionary<int, long>()));              // recorded parent started later
        // After a tick, recorded processes that closed or whose pid was reused are dropped.
        var tree = new Dictionary<int, (long Create, ProcessRule Rule)> { [10] = (100, new()), [11] = (100, new()), [12] = (100, new()) };
        Engine.PruneTree(tree, new Dictionary<int, long> { [10] = 100, [11] = 400 });
        Assert.Equal(new[] { 10 }, tree.Keys);
        Assert.NotNull(Validation.RuleProblem(new ProcessRule { Exe = "launcher.exe", Action = RuleAction.Terminate, IncludeChildren = true },
            Array.Empty<ProcessRule>(), Masks, Plain));
    }

    [Fact]
    public void RulesWaitTheirDelay()
    {
        var rule = new ProcessRule { Exe = "game.exe", DelaySeconds = 10 };
        var started = DateTime.UtcNow.ToFileTimeUtc();
        Assert.True(Engine.Waiting(rule, started, started + 9 * TimeSpan.TicksPerSecond));
        Assert.False(Engine.Waiting(rule, started, started + 10 * TimeSpan.TicksPerSecond));
        Assert.False(Engine.Waiting(new ProcessRule { Exe = "game.exe" }, started, started));
        Assert.Null(Validation.RuleProblem(rule, Array.Empty<ProcessRule>(), Masks, Plain));
        rule.DelaySeconds = Validation.MaxRuleDelay + 1;
        Assert.NotNull(Validation.RuleProblem(rule, Array.Empty<ProcessRule>(), Masks, Plain));
    }

    [Fact]
    public void GpuPriorityValidatedAndSaved()
    {
        var rule = new ProcessRule { Exe = "game.exe", GpuPriority = GpuPriority.High };
        Assert.Null(Validation.RuleProblem(rule, Array.Empty<ProcessRule>(), Masks, Plain));
        Assert.False(rule.IsEmpty);
        rule.GpuPriority = (GpuPriority)9; // a number the kernel doesn't know, from a hand-edited config
        Assert.NotNull(Validation.RuleProblem(rule, Array.Empty<ProcessRule>(), Masks, Plain));
        Assert.Equal((int)GpuPriority.AboveNormal, 5); // D3DKMT_SCHEDULINGPRIORITYCLASS_ABOVE_NORMAL
    }

    [Fact]
    public void GamemodeCoresAndBackgroundSettings()
    {
        Assert.Null(Validation.GameCoresProblem(CpuSpec.VCache, Masks, X3d));
        Assert.Null(Validation.GameCoresProblem("mask:game", Masks, X3d));
        Assert.NotNull(Validation.GameCoresProblem(CpuSpec.VCache, Masks, Plain)); // no V-Cache on this CPU
        Assert.NotNull(Validation.GameCoresProblem(null, Masks, Plain));
        Assert.NotNull(Validation.GameCoresProblem(CpuSpec.None, Masks, Plain));
        // Older configurations without the setting get efficiency mode for background programs.
        var old = System.Text.Json.JsonSerializer.Deserialize("""{"PowerPlan":false}""", GovernorJson.Default.GamemodeSystemSettings)!;
        Assert.True(old.BackgroundEfficiency && old.Any && !old.GameCores);
        Assert.True(GamemodeSystem.NeverThrottled("Discord.exe") && GamemodeSystem.NeverThrottled("voicemeeter8x64.exe") && GamemodeSystem.NeverThrottled("OBS64.EXE"));
        Assert.False(GamemodeSystem.NeverThrottled("obsidian.exe") || GamemodeSystem.NeverThrottled("chrome.exe"));
    }
}

public class ValidationTests
{
    static readonly CpuTopology Topo = new(Enumerable.Range(0, 4).Select(i => new LogicalCpu(i, (uint)(256 + i), i, 0)));

    static string? Problem(ProcessRule r, params ProcessRule[] others) => Validation.RuleProblem(r, others.Append(r), new List<CpuMask>(), Topo);

    [Fact]
    public void AcceptsEmptyAndFullRules()
    {
        Assert.Null(Problem(new ProcessRule { Exe = "ctfmon.exe" })); // parent of +S/+T rules, shows "—"
        Assert.Null(Problem(new ProcessRule { Exe = "game.exe", Priority = CpuPriority.High, Io = IoPriority.High, Affinity = "smt-off", CpuSets = "0-1" }));
    }

    [Fact]
    public void RejectsBadRules()
    {
        Assert.NotNull(Problem(new ProcessRule { Exe = "" }));
        Assert.NotNull(Problem(new ProcessRule { Exe = "a/b.exe" }));
        Assert.NotNull(Problem(new ProcessRule { Exe = "game.exe" }, new ProcessRule { Exe = "GAME.exe" }));
        Assert.NotNull(Problem(new ProcessRule { Exe = "game.exe", Affinity = "9" }));
        Assert.NotNull(Problem(new ProcessRule { Exe = "game.exe", CpuSets = "none" })); // "None" only for threads
        Assert.NotNull(Problem(new ProcessRule { Exe = "svchost.exe", Services = { new ServiceRule { Service = "Dnscache", Action = RuleAction.Terminate } } }));
        Assert.NotNull(Problem(new ProcessRule { Exe = "game.exe", Threads = { new ThreadRule { Priority = ThreadPriority.Highest } } }));
        Assert.NotNull(Problem(new ProcessRule { Exe = "game.exe", Threads = { new ThreadRule { Description = "Render", IdealCpu = 7 } } }));
    }

    [Fact]
    public void ThreadCpuSetsMayBeNone() =>
        Assert.Null(Problem(new ProcessRule { Exe = "game.exe", Threads = { new ThreadRule { Module = "game.exe", CpuSets = "none" } } }));

    [Fact]
    public void ChecksConfig()
    {
        var c = new GovernorConfig();
        Assert.Empty(Validation.Problems(c, Topo));
        c.Masks.Add(new CpuMask { Name = "A", Cpus = "0-9" });
        c.Profiles.Add(new Profile { Name = "profile 1" });
        c.General.GuiIntervalMs = 10;
        c.Profiles[0].OnEnable.Start.Add(new StartEntry { Path = "relative.exe" });
        Assert.Equal(4, Validation.Problems(c, Topo).Count()); // mask, duplicate name, interval, relative path
    }
}

public class EngineLogicTests
{
    [Fact]
    public void GamemodeRulesOverrideProfileRules()
    {
        var p = new Profile
        {
            Rules = { new ProcessRule { Exe = "game.exe", Priority = CpuPriority.Normal }, new ProcessRule { Exe = "discord.exe", Priority = CpuPriority.Idle } },
            OnEnable = { Rules = { new ProcessRule { Exe = "GAME.EXE", Priority = CpuPriority.High } } },
        };
        Assert.Equal(CpuPriority.Normal, Engine.ActiveRules(p, false)["game.exe"].Priority);
        var on = Engine.ActiveRules(p, true);
        Assert.Equal(CpuPriority.High, on["game.exe"].Priority);
        Assert.Equal(2, on.Count);
    }

    [Theory]
    [InlineData("CrRendererMain", null, "CrRendererMain", "x.dll", true)]
    [InlineData("crrenderermain", null, "CrRendererMain", null, true)]
    [InlineData("ThreadPool*", null, "ThreadPoolForegroundWorker", null, true)]
    [InlineData("ThreadPool*", null, "Compositor", null, false)]
    [InlineData(null, "ntdll.dll", null, "ntdll.dll", true)]
    [InlineData("Render", "game.exe", "Render", "ntdll.dll", false)]
    [InlineData(null, null, "Render", "game.exe", false)]
    [InlineData("Render", null, null, null, false)]
    public void MatchesThreads(string? description, string? module, string? actualDescription, string? actualModule, bool expected) =>
        Assert.Equal(expected, Engine.Matches(new ThreadRule { Description = description, Module = module }, actualDescription, actualModule));
}

public class StoreTests
{
    [Fact]
    public void ImportsExportedProfileWithItsMasks()
    {
        var mask = new CpuMask { Name = "First", Cpus = "0" };
        var source = new GovernorConfig { Masks = { mask } };
        source.Profiles[0].Rules.Add(new ProcessRule { Exe = "game.exe", Affinity = "mask:First" });
        var json = ConfigStore.Export(source, source.Profiles[0]);

        var target = new GovernorConfig();
        var imported = ConfigStore.Import(target, json);
        Assert.Equal("Profile 1 (2)", imported.Name);
        Assert.Equal(2, target.Profiles.Count);
        Assert.Single(target.Masks);

        target.Masks[0].Cpus = "0,1";
        Assert.Throws<InvalidOperationException>(() => ConfigStore.Import(target, json)); // same mask, different processors
        Assert.Equal(2, target.Profiles.Count); // nothing changed
    }

    [Fact]
    public void MigratesToolboxRules()
    {
        var file = Path.GetTempFileName();
        File.WriteAllText(file, """[{"Exe":"game.exe","Priority":"High","Cpus":"0-1"},{"Exe":"x.exe","Priority":null,"Cpus":null}]""");
        try
        {
            var rules = ConfigStore.FromToolbox(file);
            var r = Assert.Single(rules);
            Assert.Equal(("game.exe", CpuPriority.High, "0-1"), (r.Exe, r.Priority!.Value, r.Affinity!));
        }
        finally { File.Delete(file); }
    }
}

/// <summary>Real reads from this machine (nothing is changed).</summary>
public class LiveReadTests
{
    [Fact]
    public void ReadsThisProcess()
    {
        var self = Assert.Single(Snapshot.Take(), p => p.Pid == Environment.ProcessId);
        Assert.Equal(Process.GetCurrentProcess().ProcessName + ".exe", self.Name, ignoreCase: true);
        Assert.Contains(self.Threads, t => t.Tid > 0);
        var d = ProcessControl.Read(Environment.ProcessId, true, CpuTopology.Current);
        Assert.Equal(CpuPriority.Normal, d.Priority);
        Assert.Equal(CpuTopology.Current.AllMask, d.Affinity);
        Assert.Equal(0UL, d.CpuSets);
        Assert.NotNull(d.ImagePath);
        // The snapshot's start time matches the opened process; another start time is refused before anything is written.
        var keep = new ProcessRule { Exe = self.Name, Priority = CpuPriority.Normal };
        Assert.Empty(ProcessControl.Apply(self.Pid, keep, new List<CpuMask>(), CpuTopology.Current, self.CreateTime));
        Assert.Single(ProcessControl.Apply(self.Pid, keep, new List<CpuMask>(), CpuTopology.Current, self.CreateTime + 1));
    }

    [Fact]
    public void ReadsTopologyAndServices()
    {
        Assert.Equal(Math.Min(Environment.ProcessorCount, 64), CpuTopology.Current.Cpus.Count);
        Assert.Contains(Services.ByProcess().Values.SelectMany(s => s), s => s.Name.Equals("EventLog", StringComparison.OrdinalIgnoreCase));
    }
}
