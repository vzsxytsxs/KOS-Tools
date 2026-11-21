using KosToolbox.Core.Catalog;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Settings;
using KosToolbox.Core.Startup;
using KosToolbox.Core.SystemInfo;
using KosToolbox.Core.Text;
using KosToolbox.Core.Tweaks;
using Xunit;

namespace KosToolbox.Core.Tests;

/// <summary>In-memory configuration (user scope, without touching the system).</summary>
sealed class FakeSetting(string key, string? value = null) : ISetting
{
    public string? Value = value;
    public string? Missing;
    public bool FailRead, Machine;
    public string Key => key;
    public string Display => key;
    public bool MachineWide => Machine;
    public string? Unavailable() => Missing;
    public string? Read() => FailRead ? throw new IOException("falha simulada") : Value;
    public void Write(string? v) => Value = v;
}

public class EvaluatorTests
{
    static (Tweak t, FakeSetting a, FakeSetting b) Make()
    {
        var a = new FakeSetting("a", "1");
        var b = new FakeSetting("b", "1");
        var t = Tweak.Toggle("t", "T", "d", "p")
            .Add(a, (Tweak.On, Target.Set("1")), (Tweak.Off, Target.Set("0")))
            .Add(b, (Tweak.On, Target.Set("1")), (Tweak.Off, Target.Delete));
        return (t, a, b);
    }

    [Fact]
    public void MatchesOnOffAndPartial()
    {
        var (t, a, b) = Make();
        Assert.Equal(Tweak.On, TweakEvaluator.Evaluate(t).Option);
        a.Value = "0"; b.Value = null;
        Assert.Equal(Tweak.Off, TweakEvaluator.Evaluate(t).Option);
        b.Value = "1";
        var s = TweakEvaluator.Evaluate(t);
        Assert.Equal(TweakStatus.Partial, s.Status);
        Assert.Null(s.Option);
    }

    [Fact]
    public void ReadErrorIsReadErrorNeverOff()
    {
        var (t, a, _) = Make();
        a.Value = "0"; a.FailRead = true;
        var s = TweakEvaluator.Evaluate(t);
        Assert.Equal(TweakStatus.ReadError, s.Status);
        Assert.Null(s.Option);
        Assert.Contains(s.Readings, r => r.Error is not null);
    }

    [Fact]
    public void NothingWrittenIsNotConfigured_OtherValuesAreUnknown()
    {
        var (t, a, b) = Make();
        a.Value = null; b.Value = null;
        Assert.Equal(TweakStatus.NotConfigured, TweakEvaluator.Evaluate(t).Status);
        a.Value = "9"; b.Value = "9";
        Assert.Equal(TweakStatus.Unknown, TweakEvaluator.Evaluate(t).Status);
    }

    [Fact]
    public void PartialReportsClosestOption()
    {
        var (t, a, b) = Make();
        a.Value = "1"; b.Value = "9";
        var s = TweakEvaluator.Evaluate(t);
        Assert.Equal(TweakStatus.Partial, s.Status);
        Assert.Equal(Tweak.On, s.ClosestOption);
        Assert.Equal((1, 2), (s.ClosestMatches, s.ClosestTotal));
    }

    [Fact]
    public void MissingRequiredIsUnavailable_OptionalIsSkipped()
    {
        var (t, a, b) = Make();
        b.Missing = "não existe";
        Assert.Equal(TweakStatus.Unavailable, TweakEvaluator.Evaluate(t).Status);

        var c = new FakeSetting("c", "1") { Missing = "removido" };
        var t2 = Tweak.Toggle("t2", "T", "d", "p")
            .Add(a, (Tweak.On, Target.Set("1")), (Tweak.Off, Target.Set("0")))
            .AddSpec(c, true, (Tweak.On, Target.Set("1")), (Tweak.Off, Target.Set("0")));
        Assert.Equal(Tweak.On, TweakEvaluator.Evaluate(t2).Option);

        a.Missing = "removido";
        Assert.Equal(TweakStatus.Unavailable, TweakEvaluator.Evaluate(WithOnlyOptional()).Status);
        static Tweak WithOnlyOptional() => Tweak.Toggle("t3", "T", "d", "p")
            .AddSpec(new FakeSetting("x") { Missing = "-" }, true, (Tweak.On, Target.Set("1")));
    }

    [Fact]
    public void SetOrAbsentAndAny()
    {
        var a = new FakeSetting("a", null);
        var b = new FakeSetting("b", "qualquer");
        var t = Tweak.Toggle("t", "T", "d", "p")
            .Add(a, (Tweak.On, Target.SetOrAbsent("0")), (Tweak.Off, Target.Set("1")))
            .Add(b, (Tweak.On, Target.Any), (Tweak.Off, Target.Set("4")));
        Assert.Equal(Tweak.On, TweakEvaluator.Evaluate(t).Option);
        a.Value = "0";
        Assert.Equal(Tweak.On, TweakEvaluator.Evaluate(t).Option);
        a.Value = "1";
        Assert.Equal(TweakStatus.Partial, TweakEvaluator.Evaluate(t).Status);
        b.Value = "4";
        Assert.Equal(Tweak.Off, TweakEvaluator.Evaluate(t).Option);
    }

    [Fact]
    public void AbsentMatchingBothOptionsLeavesTheDecisionToTheOthers()
    {
        // As with hibernation: the absent policy doesn't decide; HibernateEnabled does.
        var state = new FakeSetting("state", "0");
        var policy = new FakeSetting("policy");
        var t = Tweak.Toggle("t", "T", "d", "p")
            .Add(policy, (Tweak.On, Target.SetOrAbsent("1")), (Tweak.Off, Target.SetOrAbsent("0")))
            .Add(state, (Tweak.On, Target.Set("1")), (Tweak.Off, Target.Set("0")));
        Assert.Equal(Tweak.Off, TweakEvaluator.Evaluate(t).Option);
        state.Value = "1";
        Assert.Equal(Tweak.On, TweakEvaluator.Evaluate(t).Option);
        policy.Value = "0"; // the policy contradicts the state
        Assert.Equal(TweakStatus.Partial, TweakEvaluator.Evaluate(t).Status);
    }

    [Fact]
    public void CompatibilityMakesUnavailable()
    {
        var (t, _, _) = Make();
        t.Compatibility = () => "só Intel";
        Assert.Equal("só Intel", TweakEvaluator.Evaluate(t).Detail);
    }

    [Fact]
    public void BuilderRejectsUnknownOptionAndDuplicates()
    {
        var t = Tweak.Toggle("t", "T", "d", "p");
        Assert.Throws<ArgumentException>(() => t.Add(new FakeSetting("a"), ("talvez", Target.Set("1"))));
        t.Add(new FakeSetting("a"), (Tweak.On, Target.Set("1")));
        Assert.Throws<ArgumentException>(() => t.Add(new FakeSetting("A"), (Tweak.On, Target.Set("1"))));
    }
}

public class EngineTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "kos-tests-" + Guid.NewGuid().ToString("N"));
    public EngineTests() { AppPaths.DataRoot = _dir; }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task ApplyVerifiesBacksUpAndRestores()
    {
        var a = new FakeSetting("a", "0");
        var t = Tweak.Toggle("eng", "T", "d", "p").Add(a, (Tweak.On, Target.Set("1")), (Tweak.Off, Target.Set("0")));
        var engine = new TweakEngine { VerifyDelay = TimeSpan.Zero };

        var r = await engine.ApplyAsync(t, Tweak.On);
        Assert.True(r.Success, r.Message);
        Assert.Equal("1", a.Value);
        Assert.Equal(Tweak.Off, engine.Backups.Get("eng")!.Original!.Option);

        a.Value = "7"; // someone changed it to a custom value
        r = await engine.ApplyAsync(t, Tweak.Off);
        Assert.True(r.Success);
        Assert.Equal(Tweak.Off, engine.Backups.Get("eng")!.Original!.Option); // the original is never overwritten
        Assert.Null(engine.Backups.Get("eng")!.Previous!.Option);

        r = await engine.RestoreAsync(t, RestoreTarget.Previous);
        Assert.True(r.Success, r.Message);
        Assert.Equal("7", a.Value);
    }

    [Fact]
    public async Task RefusedOptionChangesNothing()
    {
        var a = new FakeSetting("a", "1");
        var t = Tweak.Toggle("refused", "T", "d", "p").Add(a, (Tweak.On, Target.Set("1")), (Tweak.Off, Target.Set("0")));
        t.Refuse = o => o != Tweak.On ? "not now" : null;
        var engine = new TweakEngine { VerifyDelay = TimeSpan.Zero, RunElevated = (_, _) => throw new InvalidOperationException("must not elevate") };

        var r = await engine.ApplyAsync(t, Tweak.Off);
        Assert.False(r.Success);
        Assert.Equal("not now", r.Message);
        Assert.Equal("1", a.Value);

        // A custom restore (values that match no option) asks with null and is refused too.
        a.Value = "7";
        Assert.True((await engine.ApplyAsync(t, Tweak.On)).Success);
        r = await engine.RestoreAsync(t, RestoreTarget.Previous);
        Assert.False(r.Success);
        Assert.Equal("1", a.Value);
    }

    /// <summary>The Windows Firewall toggle's Off (it disables BFE) is refused while KOS Firewall is on, in the elevated process too. Validation only: nothing is applied.</summary>
    [Fact]
    public void FirewallToggleOffRefusedWhileKosFirewallOn()
    {
        var saved = KosToolbox.Core.Firewall.KosFirewall.IsOn;
        try
        {
            KosToolbox.Core.Firewall.KosFirewall.IsOn = () => true;
            var tweak = KosCatalog.Find("firewall")!;
            Assert.Equal(Txt.FirewallToggleRefused, tweak.Refuse!(Tweak.Off));
            Assert.NotNull(tweak.Refuse(null));
            Assert.Null(tweak.Refuse(Tweak.On));
            var ex = Assert.Throws<InvalidOperationException>(() => new ElevatedJob { TweakId = "firewall", Option = Tweak.Off }.Validate());
            Assert.Equal(Txt.FirewallToggleRefused, ex.Message);
            new ElevatedJob { TweakId = "firewall", Option = Tweak.On }.Validate();
            // The Startup page and a service snapshot restore ask the same: BFE disabled, nothing else.
            Assert.Equal(Txt.FirewallToggleRefused, KosToolbox.Core.Firewall.KosFirewall.ServiceStartRefused("bfe", 4));
            Assert.Null(KosToolbox.Core.Firewall.KosFirewall.ServiceStartRefused("BFE", 2));
            Assert.Null(KosToolbox.Core.Firewall.KosFirewall.ServiceStartRefused("mpssvc", 4));

            KosToolbox.Core.Firewall.KosFirewall.IsOn = () => false;
            new ElevatedJob { TweakId = "firewall", Option = Tweak.Off }.Validate();
            Assert.Null(KosToolbox.Core.Firewall.KosFirewall.ServiceStartRefused("BFE", 4));
        }
        finally { KosToolbox.Core.Firewall.KosFirewall.IsOn = saved; }
    }

    [Fact]
    public async Task FailedVerificationIsReported()
    {
        var a = new StickySetting();
        var t = Tweak.Toggle("sticky", "T", "d", "p").Add(a, (Tweak.On, Target.Set("1")), (Tweak.Off, Target.Set("0")));
        var r = await new TweakEngine { VerifyDelay = TimeSpan.Zero }.ApplyAsync(t, Tweak.On);
        Assert.False(r.Success);
        Assert.Equal(Tweak.Off, r.State.Option);
    }

    [Fact]
    public async Task RebootPendingIsMarked()
    {
        var a = new FakeSetting("a", "0");
        var t = Tweak.Toggle("rb", "T", "d", "p").Add(a, (Tweak.On, Target.Set("1")), (Tweak.Off, Target.Set("0")));
        t.RequiresReboot = true;
        var engine = new TweakEngine { VerifyDelay = TimeSpan.Zero };
        Assert.False(engine.IsRebootPending("rb"));
        await engine.ApplyAsync(t, Tweak.On);
        Assert.True(engine.IsRebootPending("rb"));
    }

    [Fact]
    public async Task HistoryRecordsChangesAndUndoesOnlyWhatIsUnchanged()
    {
        var a = new FakeSetting("a", "0");
        var t = Tweak.Toggle("hist", "T", "d", "p").Add(a, (Tweak.On, Target.Set("1")), (Tweak.Off, Target.Set("0")));
        var engine = new TweakEngine { VerifyDelay = TimeSpan.Zero };

        await engine.ApplyAsync(t, Tweak.On);
        var on = Assert.Single(engine.History.Load());
        Assert.Equal((Tweak.Off, Tweak.On, Tweak.On, true), (on.Before.Option, on.Option, on.After, on.Confirmed));
        Assert.Equal("1", on.AfterValues["a"]);

        a.Value = "7"; // changed outside the toolbox since: undo refuses
        var r = await engine.UndoAsync(t, on);
        Assert.False(r.Success);
        Assert.Equal("7", a.Value);

        a.Value = "1";
        r = await engine.UndoAsync(t, on);
        Assert.True(r.Success, r.Message);
        Assert.Equal("0", a.Value);
        var all = engine.History.Load();
        Assert.Equal(2, all.Count);
        Assert.False(ChangeHistory.IsLatest(all, all[0]));
        Assert.True(ChangeHistory.IsLatest(all, all[1]));

        // A custom value restored (no option) is recorded with its values and undone by values.
        a.Value = "7";
        await engine.ApplyAsync(t, Tweak.On);
        r = await engine.RestoreAsync(t, RestoreTarget.Previous);
        Assert.True(r.Success, r.Message);
        var restored = engine.History.Load()[^1];
        Assert.Null(restored.Option);
        Assert.Equal("7", restored.AfterValues["a"]);
        r = await engine.UndoAsync(t, restored);
        Assert.True(r.Success, r.Message);
        Assert.Equal("1", a.Value);
    }

    [Fact]
    public void MachineSettingsRestoreOnlyTheirOwnOptionValues()
    {
        var user = new FakeSetting("u");
        var machine = new FakeSetting("m") { Machine = true };
        var t = Tweak.Toggle("rest", "T", "d", "p")
            .Add(user, (Tweak.On, Target.Set("1")), (Tweak.Off, Target.Set("0")))
            .Add(machine, (Tweak.On, Target.Set("1")), (Tweak.Off, Target.Any));
        var (u, m) = (t.Specs[0], t.Specs[1]);
        Assert.True(TweakEngine.Restorable(u, @"C:\Users\x\evil.dll")); // the user's own settings: any saved value
        Assert.True(TweakEngine.Restorable(m, "1"));
        Assert.False(TweakEngine.Restorable(m, null)); // "any" isn't a value the option writes
        Assert.False(TweakEngine.Restorable(m, "7"));
        Assert.False(TweakEngine.Restorable(m, @"C:\Users\x\evil.dll"));
    }

    [Fact]
    public void AntiCheatNoteOnlyWhenInstalled()
    {
        Assert.Null(AntiCheats.Note(Array.Empty<string>(), memoryIntegrity: true));
        Assert.Equal(Txt.AntiCheatVanguard, AntiCheats.Note(new[] { AntiCheats.Vanguard }, memoryIntegrity: false));
        Assert.Equal(Txt.AntiCheatFaceitHvci, AntiCheats.Note(new[] { AntiCheats.Faceit }, memoryIntegrity: true));
        Assert.Equal(Txt.AntiCheatFaceitVbs, AntiCheats.Note(new[] { AntiCheats.Faceit }, memoryIntegrity: false));
    }

    [Fact]
    public void CorruptJsonLoadsEmpty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(AppPaths.Backups, "{ quebrado");
        Assert.Empty(new BackupStore().Load());
    }

    sealed class StickySetting : ISetting
    {
        public string Key => "sticky";
        public string Display => "sticky";
        public bool MachineWide => false;
        public string? Unavailable() => null;
        public string? Read() => "0";
        public void Write(string? value) { }
    }
}

public class ParsingTests
{
    [Fact]
    public void BcdParse()
    {
        const string output = "Windows Boot Loader\r\n-------------------\r\nidentifier              {current}\r\nnx                      OptIn\r\nhypervisorlaunchtype    Off\r\nUseplatformtick         Yes\r\n                        continuação\r\n";
        var d = Bcd.Parse(output);
        Assert.Equal("OptIn", d["nx"]);
        Assert.Equal("Off", d["hypervisorlaunchtype"]);
        Assert.Equal("Yes", d["useplatformtick"]);
        Assert.False(d.ContainsKey("windows"));
    }

    [Theory]
    [InlineData(0x02, true)]
    [InlineData(0x06, true)]
    [InlineData(0x03, false)]
    [InlineData(0x07, false)]
    public void StartupFlag(byte b, bool enabled) => Assert.Equal(enabled, StartupApproved.IsEnabledFlag(b));

    [Theory]
    [InlineData("simples", "simples")]
    [InlineData(@"C:\Program Data\x.cmd", "\"C:\\Program Data\\x.cmd\"")]
    [InlineData(@"C:\a b\", "\"C:\\a b\\\\\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("", "\"\"")]
    public void Quote(string input, string expected) => Assert.Equal(expected, ProcessRunner.Quote(input));
}

public class CatalogTests
{
    [Theory]
    [InlineData("OFF", "off")]
    [InlineData("ON", "on")]
    [InlineData("NOTSET", "on")]
    public void CfgStateParsed(string text, string expected) => Assert.Equal(expected, CfgSetting.Parse(text));

    [Theory]
    [InlineData("utilman2.exe.mui", "off")] // original Utilman in System32 (read from the .mui)
    [InlineData("utilman2.exe", "off")]
    [InlineData("Cmd.Exe", "on")]          // old copy of cmd.exe
    [InlineData("Cmd.Exe.MUI", "on")]
    public void UtilmanStateParsed(string name, string expected) => Assert.Equal(expected, UtilmanSetting.Parse(name));

    [Fact]
    public void FirewallRulesComparedWithDefaults()
    {
        var defaults = new[] { "CoreNet-DHCP-In", "Block-Windows-Error-Reporting" };
        Assert.Equal("on", FirewallRulesSetting.Classify(new[] { "corenet-dhcp-in", "Block-Windows-Error-Reporting", "{app-guid}" }, defaults));
        Assert.Equal("off", FirewallRulesSetting.Classify(new[] { "{app-guid}", "Claude" }, defaults)); // apps recreate their own
        Assert.Equal("partial", FirewallRulesSetting.Classify(new[] { "CoreNet-DHCP-In" }, defaults));
    }

    [Fact]
    public void UtilmanUnknownFileThrows() => Assert.Throws<InvalidOperationException>(() => UtilmanSetting.Parse("notepad.exe"));

    [Theory]
    [InlineData("VRROptimizeEnable=0;SwapEffectUpgradeEnable=1;", "SwapEffectUpgradeEnable", "1")]
    [InlineData("VRROptimizeEnable=0;SwapEffectUpgradeEnable=1;", "vrroptimizeenable", "0")]
    [InlineData("VRROptimizeEnable=0;", "AutoHDREnable", null)]
    [InlineData(null, "AutoHDREnable", null)]
    public void DxPairRead(string? settings, string name, string? expected) => Assert.Equal(expected, DxSetting.Get(settings, name));

    [Theory]
    [InlineData("VRROptimizeEnable=0;SwapEffectUpgradeEnable=1;", "VRROptimizeEnable", "1", "VRROptimizeEnable=1;SwapEffectUpgradeEnable=1;")] // replaced in place
    [InlineData("VRROptimizeEnable=0;", "AutoHDREnable", "1", "VRROptimizeEnable=0;AutoHDREnable=1;")]                                         // appended
    [InlineData("A=1;AutoHDREnable=0;B=2;AutoHDREnable=1", "AutoHDREnable", null, "A=1;B=2;")]                                                 // removed, duplicates too
    [InlineData("AutoHDREnable=1;", "AutoHDREnable", null, "")]
    [InlineData(null, "VRROptimizeEnable", "0", "VRROptimizeEnable=0;")]
    [InlineData("junk;A=1", "B", "2", "junk;A=1;B=2;")]                                                                                         // unknown tokens kept
    public void DxPairWrite(string? settings, string name, string? value, string expected) => Assert.Equal(expected, DxSetting.Set(settings, name, value));

    [Theory]
    [InlineData("v2.33|Action=Allow|Active=TRUE|Dir=In|Protocol=6|LPort=3389|", true)]
    [InlineData("v2.33|Action=Allow|Active=FALSE|Dir=In|Protocol=6|LPort=3389|", false)]
    [InlineData("v2.33|Action=Allow|Dir=In|", false)]
    public void FirewallRuleActiveParsed(string rule, bool active) => Assert.Equal(active, FirewallRuleSetting.Active(rule));

    [Theory]
    [InlineData(1, 1, "0")]          // Nagle off on this interface
    [InlineData(null, null, "1")]    // Windows default
    [InlineData(1, null, "mixed")]
    [InlineData(2, 1, "mixed")]
    public void NagleStateParsed(int? ack, int? noDelay, string expected) => Assert.Equal(expected, NagleSetting.State(ack, noDelay));

    [Theory]
    [InlineData(1, "Windows 11", "26H2", "26H2", "1")]
    [InlineData(1, "Windows 11", "26h2", "26H2", "1")]
    [InlineData(1, "Windows 11", "25H2", "26H2", null)]   // a pin left at an older version
    [InlineData(0, "Windows 11", "26H2", "26H2", null)]
    [InlineData(1, null, "26H2", "26H2", null)]
    [InlineData(null, null, null, "26H2", null)]
    [InlineData(1, "Windows 11", "26H2", null, null)]
    public void ReleasePinState(int? target, string? product, string? info, string? current, string? expected) =>
        Assert.Equal(expected, ReleasePinSetting.State(target, product, info, current));

    [Theory]
    [InlineData("1", "1", "0")]
    [InlineData(null, null, "1")]
    [InlineData("1", null, "mixed")]
    public void WriteCacheStateParsed(string? cache, string? powerProtected, string expected) => Assert.Equal(expected, WriteCacheFlushSetting.State(cache, powerProtected));

    [Theory]
    [InlineData(@"SCSI\Disk&Ven_NVMe&Prod_KINGSTON_SNVS250\5&112f546d&0&000000", true)]
    [InlineData(@"USBSTOR\Disk&Ven_SanDisk&Prod_Ultra\4C530001", false)]
    [InlineData(@"SCSI\..\..\Control", false)]
    public void WriteCacheSkipsUsbDrives(string instance, bool included) => Assert.Equal(included, WriteCacheFlushSetting.IsFixed(instance));

    [Theory]
    [InlineData(1, false, "1")]
    [InlineData(0, true, "0")]
    [InlineData(null, false, "0")] // never changed: the feature's default
    [InlineData(null, true, "1")]
    public void OptionalFeatureSelectionParsed(int? selection, bool enabledByDefault, string expected) =>
        Assert.Equal(expected, OptionalFeatureSetting.State(selection, enabledByDefault));

    [Theory]
    [InlineData("2;0,0", true)]
    [InlineData("2;", true)]
    [InlineData("WARNING: x\r\n2;0\r\n", true)]
    [InlineData("2;2", false)] // memory integrity still configured: the registry, not a lock, keeps VBS on
    [InlineData("0;0", false)] // VBS off
    [InlineData("", false)]
    public void VbsLockDetected(string output, bool locked) => Assert.Equal(locked, SystemFixes.VbsLocked(output));

    [Theory]
    [InlineData("Advanced Micro Devices, Inc.", true)]
    [InlineData("AMD", true)]
    [InlineData("NVIDIA", false)]
    [InlineData("Intel Corporation", false)]
    [InlineData(null, false)]
    public void AmdProviderRecognized(string? provider, bool amd) => Assert.Equal(amd, AmdGpus.IsAmd(provider));

    [Fact]
    public void AmdSectionOnlyWithAnAmdAdapter()
    {
        _ = KosCatalog.Tweaks.Count; // the catalog is built before the adapter list is swapped below
        var saved = AmdGpus.ClassKeys;
        try
        {
            AmdGpus.ClassKeys = () => new();
            Assert.Empty(KosCatalog.AmdTweaks());
            Assert.Empty(KosCatalog.AmdActions());
            AmdGpus.ClassKeys = () => new() { @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0001" };
            var driver = KosCatalog.AmdTweaks().Single(t => t.Id == "amd-radeon-tweaks");
            foreach (var o in driver.Options) Assert.Contains(driver.Specs, s => s.Targets.TryGetValue(o.Key, out var x) && !x.Ignore);
            var forced = driver.Specs.Single(s => s.Setting.Key.EndsWith(@"\PP_ForceHighDPMLevel"));
            Assert.Equal("1", forced.Targets["experimental"].Value);
            Assert.Null(forced.Targets["safe"].Value); // Safe removes what Experimental adds
            Assert.Single(KosCatalog.AmdActions());
        }
        finally { AmdGpus.ClassKeys = saved; }
    }

    [Fact]
    public void GameBarAndSearchBlockTheirProcesses()
    {
        static IEnumerable<SettingSpec> Ifeo(string id) => KosCatalog.Find(id)!.Specs.Where(s => s.Setting.Key.EndsWith(@"\Debugger"));
        Assert.Equal(7, Ifeo("game-bar").Count());
        Assert.All(Ifeo("game-bar"), s => { Assert.Null(s.Targets[Tweak.On].Value); Assert.EndsWith("systray.exe", s.Targets[Tweak.Off].Value); });
        Assert.All(Ifeo("windows-search"), s => { Assert.Null(s.Targets["no-index"].Value); Assert.EndsWith("systray.exe", s.Targets[Tweak.Off].Value); });
    }

    [Theory]
    [InlineData(true, false, "on")]
    [InlineData(false, true, "off")]  // renamed to .old by the KOS script
    [InlineData(false, false, null)]  // absent: unavailable
    public void StartMenuHostState(bool exe, bool old, string? expected) => Assert.Equal(expected, StartMenus.HostState(exe, old));

    [Fact]
    public void StartMenuHostWithBothFilesIsAReadError() => Assert.Throws<InvalidOperationException>(() => StartMenus.HostState(true, true));

    [Theory]
    [InlineData(null, "on")] // Open-Shell default
    [InlineData(1, "on")]
    [InlineData(0, "off")]
    [InlineData("0", "on")]  // Open-Shell only reads DWORD
    public void OpenShellAutoStartParsed(object? raw, string expected) => Assert.Equal(expected, StartMenus.AutoStart(raw));

    [Theory]
    [InlineData(true, false, false)] // Windows menu, Open-Shell off: search applies
    [InlineData(true, true, true)]   // Open-Shell intercepts the menu
    [InlineData(false, true, true)]  // KOS default
    [InlineData(false, false, true)] // no menu
    public void StartMenuSearchOnlyWithTheWindowsMenu(bool hostExe, bool openShellStarts, bool unavailable)
    {
        var reason = StartMenus.SearchUnavailable(hostExe, openShellStarts);
        Assert.Equal(unavailable, reason is not null);
        if (openShellStarts) Assert.Equal(Txt.StartSearchOpenShell, reason);
    }

    [Fact]
    public void IdsUniqueAndDefaultsValid()
    {
        var ids = KosCatalog.Tweaks.Select(t => t.Id).Concat(KosCatalog.Actions.Select(a => a.Id)).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        foreach (var t in KosCatalog.Tweaks)
        {
            var keys = t.Options.Select(o => o.Key).ToList();
            Assert.Equal(keys.Count, keys.Distinct().Count());
            if (t.KosDefault is not null) Assert.Contains(t.KosDefault, keys);
            if (t.ProfileDefaults is { } p)
            {
                Assert.Null(t.KosDefault); // with a per-profile default, the single one would be ignored
                foreach (var d in new[] { p.Basic, p.Advanced }) if (d is not null) Assert.Contains(d, keys);
            }
            if (t.WindowsDefault is not null) Assert.Contains(t.WindowsDefault, keys);
            if (t.LessSecureOption is not null) { Assert.Contains(t.LessSecureOption, keys); Assert.NotNull(t.SecurityNote); }
            if (t.ConfirmationOption is not null) { Assert.Contains(t.ConfirmationOption, keys); Assert.NotNull(t.ConfirmationNote); }
            foreach (var s in t.Scripts) Assert.Contains(s.Key, keys);
            Assert.False(string.IsNullOrWhiteSpace(t.Source), t.Id);
        }
    }

    [Theory]
    [InlineData("basic", KosProfile.Basic)]
    [InlineData(" Basic ", KosProfile.Basic)]
    [InlineData("advanced", KosProfile.Advanced)]
    [InlineData("extreme", KosProfile.Advanced)] // old name of Advanced
    [InlineData(null, KosProfile.Advanced)]      // KOS 4.0.0 or earlier, without the value
    public void ProfileParsed(string? value, KosProfile expected) => Assert.Equal(expected, KosProfiles.Parse(value));

    [Fact]
    public void KosDefaultsFollowProfile()
    {
        Tweak T(string id) => KosCatalog.Tweaks.Single(t => t.Id == id);
        foreach (var id in new[] { "uac", "dep", "cfg", "hypervisor", "process-mitigations", "dma-remapping", "firewall-rules" })
        {
            Assert.Equal(Tweak.On, T(id).DefaultFor(KosProfile.Basic));
            Assert.Equal(Tweak.Off, T(id).DefaultFor(KosProfile.Advanced));
            Assert.Null(T(id).DefaultFor(null));
        }
        Assert.Null(T("vbs").DefaultFor(KosProfile.Basic));
        Assert.Null(T("hvci").DefaultFor(KosProfile.Basic));
        Assert.Equal(Tweak.Off, T("vbs").DefaultFor(KosProfile.Advanced));
        Assert.Equal(Tweak.Off, T("hvci").DefaultFor(KosProfile.Advanced));
        Assert.Equal(Tweak.On, T("spectre-meltdown").DefaultFor(KosProfile.Basic));
        Assert.Null(T("spectre-meltdown").DefaultFor(KosProfile.Advanced));
        Assert.Equal(Tweak.Off, T("login-screen-command-prompt").KosDefault);
        // The other defaults don't depend on the profile, even when it couldn't be read.
        var plain = KosCatalog.Tweaks.First(t => t.KosDefault is not null);
        Assert.All(new KosProfile?[] { KosProfile.Basic, KosProfile.Advanced, null }, p => Assert.Equal(plain.KosDefault, plain.DefaultFor(p)));
    }

    [Fact]
    public void ScriptsArePinnedAndRelative()
    {
        var scripts = KosCatalog.Tweaks.SelectMany(t => t.Scripts.Values)
            .Concat(KosCatalog.Actions.Where(a => a.Script is not null).Select(a => a.Script!));
        foreach (var s in scripts)
        {
            Assert.Matches("^[0-9A-F]{64}$", s.Sha256);
            Assert.False(Path.IsPathRooted(s.RelativePath));
            Assert.DoesNotContain("..", s.RelativePath);
        }
    }

    [Fact]
    public void EveryTweakVerifiesEveryOption()
    {
        // Each option needs at least one verifiable value; otherwise the state would never be confirmed.
        foreach (var t in KosCatalog.Tweaks.Where(t => t.Specs.Count > 0))
            foreach (var o in t.Options)
                Assert.True(t.Specs.Any(s => s.Targets.TryGetValue(o.Key, out var x) && !x.Ignore), $"{t.Id}/{o.Key}");
    }
}

public class ElevatedJobTests
{
    const string Approved = ElevatedJob.StartupApprovedPrefix;

    [Fact]
    public void AcceptsOnlyCatalogRequests()
    {
        new ElevatedJob { TweakId = "uac", Option = Tweak.On }.Validate();
        new ElevatedJob { ActionId = "cleanup" }.Validate();
        new ElevatedJob { ActionId = "clear-eventlogs" }.Validate();
        new ElevatedJob { Writes = { new JobWrite { Key = Approved + @"Run\App", Value = "030000000000000000000000" } } }.Validate();

        Assert.ThrowsAny<Exception>(() => new ElevatedJob { TweakId = "uac", Option = "talvez" }.Validate());
        Assert.ThrowsAny<Exception>(() => new ElevatedJob { TweakId = "nao-existe", Option = Tweak.On }.Validate());
        // Values never come in a tweak request; the elevated process recalculates from the catalog.
        Assert.ThrowsAny<Exception>(() => new ElevatedJob { TweakId = "uac", Option = Tweak.On,
            Writes = { new JobWrite { Key = @"reg:HKLM\SYSTEM\CurrentControlSet\Services\Appinfo\Start", Value = "4" } } }.Validate());
        Assert.ThrowsAny<Exception>(() => new ElevatedJob { ActionId = "open-setup" }.Validate());       // doesn't need admin
        Assert.ThrowsAny<Exception>(() => new ElevatedJob { ActionId = "restart-explorer" }.Validate()); // runs in the user's process
        Assert.ThrowsAny<Exception>(() => new ElevatedJob().Validate());                                 // empty
        Assert.ThrowsAny<Exception>(() => new ElevatedJob { ActionId = "cleanup",                        // mixed
            Writes = { new JobWrite { Key = Approved + @"Run\App", Value = "030000000000000000000000" } } }.Validate());
    }

    [Theory]
    [InlineData(@"Run\App", "FF0000000000000000000000")]   // invalid flag
    [InlineData(@"Run\App", "0300")]                       // wrong size
    [InlineData(@"Outro\App", "030000000000000000000000")] // subkey not in the list
    [InlineData(@"Run", "030000000000000000000000")]       // no name
    public void StartupWritesAreRestricted(string rest, string value) =>
        Assert.ThrowsAny<Exception>(() => new ElevatedJob {
            Writes = { new JobWrite { Key = Approved + rest, Value = value } } }.Validate());
}
