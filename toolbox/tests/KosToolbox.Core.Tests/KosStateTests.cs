using System.Text.Json;
using KosToolbox.Core.KosState;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Settings;
using Microsoft.Win32;
using Xunit;
using Xunit.Abstractions;

namespace KosToolbox.Core.Tests;

public class KosStateTests(ITestOutputHelper output)
{
    static KosExpected Expected => KosExpected.Embedded;

    /// <summary>This PC's registry: the Policies key is a link to the 64-bit one, the CurrentVersion key is a separate 32-bit copy.</summary>
    [Fact]
    public void SharedWow64KeysAreFoundAsTheNativeKey()
    {
        Assert.Equal(@"SOFTWARE\Policies\Microsoft\Windows", KosReg.SharedNative("HKLM", @"SOFTWARE\WOW6432Node\Policies\Microsoft\Windows"));
        Assert.Null(KosReg.SharedNative("HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion"));
        Assert.Null(KosReg.SharedNative("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"));
        Assert.Null(KosReg.SharedNative("HKCU", @"SOFTWARE\WOW6432Node\Policies\Microsoft\Windows"));
    }

    /// <summary>The embedded copy is KOS-W11's (when that repository sits next to this one).</summary>
    [Fact]
    public void EmbeddedListIsTheKosOne()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "KOS-W11"))) dir = dir.Parent;
        var kos = dir is null ? null : Path.Combine(dir.FullName, @"KOS-W11\src\ProgramData\TheKOS\bin\2\kos-expected.json");
        if (kos is null || !File.Exists(kos)) { output.WriteLine("KOS-W11 not found next to this repository; not compared."); return; }
        Assert.True(File.ReadAllBytes(kos).AsSpan().SequenceEqual(KosExpected.EmbeddedBytes),
            @"toolbox\src\KosToolbox.Core\KosState\kos-expected.json differs from KOS-W11's: copy it again.");
    }

    [Fact]
    public void EveryListedValueCanBeReadAndWritten()
    {
        Assert.True(Expected.Registry.Count > 1000 && Expected.SnapshotKeys.Count > 500 && Expected.Tasks.Count > 50);
        Assert.All(Expected.Registry, e => Assert.NotNull(KosReg.Setting(e.Root, e.Key, e.Name, e.Type)));
        Assert.All(Expected.Tasks, t => Assert.StartsWith(@"\", t.Path));
    }

    [Theory]
    [InlineData("HKCU", @"Control Panel\Desktop", "WallPaper", true)]
    [InlineData("HKCU", @"Control Panel\Desktop", "MenuShowDelay", false)]
    [InlineData("HKCU", @"Software\Sysinternals\Process Explorer", "Anything", true)]
    [InlineData("HKLM", @"SOFTWARE\Classes\7-Zip.7z", "", true)]
    [InlineData("HKLM", @"SOFTWARE\Classes\7-ZipX", "", false)]
    public void IgnorePatternsWorkLikePowerShellLike(string root, string key, string name, bool ignored) =>
        Assert.Equal(ignored, Expected.Ignored(root, key, name));

    [Fact]
    public void HkcrIsTheMachinesClassesAndHkcuIsTheUsers()
    {
        Assert.Equal((Hive.LocalMachine, @"SOFTWARE\Classes\x"), KosReg.Map("HKCR", "x")!.Value);
        Assert.True(KosReg.Machine("HKCR") && KosReg.Machine("HKLM") && !KosReg.Machine("HKCU"));
        Assert.Null(KosReg.Map("HKU", "x"));
    }

    [Theory]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\WinDefend", KosArea.Defender)]
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection", KosArea.Defender)]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SmartScreenEnabled", KosArea.Defender)]
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", KosArea.Ai)]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\SensorService", null)]
    [InlineData(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SoftwareProtectionPlatform\License", null)]
    public void DefenderAndAiItemsAreGroupedByTheirKeys(string text, KosArea? area) => Assert.Equal(area, KosCheck.AreaOf(text));

    /// <summary>A listed value the check offers on this PC (not a catalog setting's, and it applies here).</summary>
    static int Offered(string root, string type) => Expected.Registry.FindIndex(x => x.Root == root && x.Type == type && KosCheck.Applies(x)
        && !KosCheck.Owned.Value.Contains(KosReg.Setting(x.Root, x.Key, x.Name, x.Type)!.Key));

    static KosSnapshotValue V(string name, string type, object data) => new() { Name = name, Type = type, Data = JsonSerializer.SerializeToElement(data) };

    [Fact]
    public void SnapshotValuesAreWrittenLikeTheSetupBaseline()
    {
        Assert.Equal("4294967295", KosReg.Text(KosSnapshots.Value("a", RegistryValueKind.DWord, -1).Data));
        Assert.Equal("18446744073709551615", KosReg.Text(KosSnapshots.Value("a", RegistryValueKind.QWord, -1L).Data));
        Assert.Equal("01ab", KosReg.Text(KosSnapshots.Value("a", RegistryValueKind.Binary, new byte[] { 1, 0xab }).Data));
        var multi = KosSnapshots.Value("a", RegistryValueKind.MultiString, new[] { "x" });
        Assert.Equal(JsonValueKind.Array, multi.Data.ValueKind);
        Assert.True(KosSnapshots.Same(V("a", "binary", "01AB"), V("a", "binary", "01ab")));
        Assert.False(KosSnapshots.Same(V("a", "sz", "1"), V("a", "dword", "1")));
    }

    [Fact]
    public void DifferencesAreChangedAddedAndRemovedValues()
    {
        var before = new KosSnapshotKey { Exists = true, Values = new() { V("same", "dword", "1"), V("changed", "dword", "1"), V("gone", "sz", "x") } };
        var now = new KosSnapshotKey { Exists = true, Values = new() { V("same", "dword", "1"), V("changed", "dword", "0"), V("new", "sz", "y") } };
        var d = KosCheck.Differences(before, now).ToDictionary(x => x.Name);
        Assert.Equal(new[] { "changed", "gone", "new" }, d.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Null(d["gone"].Now);
        Assert.Null(d["new"].Before);
        // A key that didn't exist: everything in it is new.
        Assert.Single(KosCheck.Differences(new KosSnapshotKey { Exists = false }, now), x => x.Name == "same");
    }

    [Fact]
    public void TheUsersPartComesOnlyFromTheSameAccount()
    {
        var setup = new KosSnapshot { UserSid = "S-1-5-21-1" };
        var last = new KosSnapshot { UserSid = "S-1-5-21-1" };
        Assert.Equal(KosReferenceKind.SetupBaseline, KosSnapshots.UserPart(null, setup, "S-1-5-21-1")!.Kind);
        Assert.Null(KosSnapshots.UserPart(null, setup, "S-1-5-21-2"));
        Assert.Equal(KosReferenceKind.LastGood, KosSnapshots.UserPart(last, setup, "S-1-5-21-1")!.Kind);
        Assert.Null(KosSnapshots.UserPart(last, setup, "S-1-5-21-9"));
    }

    [Fact]
    public void AValueTheToolboxLeftIsItsChange()
    {
        var setting = new RegistrySetting(Hive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\wuauserv", "Start", RegistryValueKind.DWord);
        var older = new HistoryEntry { At = new DateTime(2026, 1, 1), TweakId = "a", AfterValues = new() { [setting.Key] = "4" } };
        var newer = new HistoryEntry { At = new DateTime(2026, 2, 1), TweakId = "b", AfterValues = new() { [setting.Key] = "3" } };
        var left = KosCheck.ToolboxValues(new[] { newer, older });
        Assert.True(KosCheck.ByToolbox(left, setting, "3"));
        Assert.False(KosCheck.ByToolbox(left, setting, "4"));
        Assert.False(KosCheck.ByToolbox(left, setting, null));
    }

    [Fact]
    public void IdsAreResolvedFromTheListAndTheReferenceOnly()
    {
        var i = Offered("HKLM", "dword");
        var owned = Expected.Registry.FindIndex(e => KosReg.Setting(e.Root, e.Key, e.Name, e.Type) is { } s && KosCheck.Owned.Value.Contains(s.Key));
        Assert.Throws<InvalidOperationException>(() => KosStateFix.Resolve($"e:{owned}", Expected, _ => null)); // a catalog setting's: through the tweak engine
        var e = KosStateFix.Resolve($"e:{i}", Expected, _ => null);
        Assert.Equal(Expected.Registry[i].Key, e.Key);
        Assert.Equal(KosReg.Text(Expected.Registry[i].Data), e.Value);
        foreach (var bad in new[] { "e:999999", "e:-1", "e:1x", "z:1", "e", "s:0", "s:0|name", $"t:{Expected.Tasks.Count}" })
            Assert.Throws<InvalidOperationException>(() => KosStateFix.Resolve(bad, Expected, _ => null));

        // Snapshot values: only what differs from the last good state now, with the name and data from the registry and the reference.
        var sk = Expected.SnapshotKeys[0];
        var current = KosSnapshots.CaptureKey(Expected, sk.Root, sk.Key);
        var reference = new KosSnapshot { Keys = { new KosSnapshotKey { Root = sk.Root, Key = sk.Key, Exists = true,
            Values = (current.Values ?? new()).Append(V("kosTestGone", "dword", "5")).Append(V("kosTestOdd", "unknown", "00")).ToList() } } };
        var restore = KosStateFix.Resolve("s:0|KOSTESTGONE", Expected, _ => reference);
        Assert.Equal(("kosTestGone", "dword", "5"), (restore.Name, restore.Type, restore.Value));
        Assert.Throws<InvalidOperationException>(() => KosStateFix.Resolve("s:0|kosTestOdd", Expected, _ => reference)); // can't be written
        Assert.Throws<InvalidOperationException>(() => KosStateFix.Resolve("s:0|kosTestNeverThere", Expected, _ => reference)); // doesn't differ

        // A value that appeared since the last good state is deleted; one the check doesn't offer is refused.
        var found = false;
        for (var k = 0; k < Expected.SnapshotKeys.Count && !found; k++)
        {
            var key = Expected.SnapshotKeys[k];
            var now = KosSnapshots.CaptureKey(Expected, key.Root, key.Key);
            foreach (var v in now.Values ?? new())
            {
                var without = new KosSnapshotKey { Root = key.Root, Key = key.Key, Exists = true, Values = now.Values!.Where(x => x != v).ToList() };
                var offered = KosCheck.OtherDifferences(Expected, key, without, now).Any(d => d.Name == v.Name);
                var snapshot = new KosSnapshot { Keys = { without } };
                if (!offered) { Assert.Throws<InvalidOperationException>(() => KosStateFix.Resolve($"s:{k}|{v.Name}", Expected, _ => snapshot)); continue; }
                var delete = KosStateFix.Resolve($"s:{k}|{v.Name.ToUpperInvariant()}", Expected, _ => snapshot);
                Assert.Equal((v.Name, (string?)null), (delete.Name, delete.Value));
                found = true;
                break;
            }
        }
        Assert.True(found);
    }

    [Fact]
    public void DataWithVariablesIsMarkedForTheElevatedSideToRefuse()
    {
        var e = Offered("HKLM", "sz");
        var tampered = KosExpected.Parse(KosExpected.EmbeddedBytes);
        tampered.Registry[e].ExpandData = true;
        Assert.True(KosStateFix.Resolve($"e:{e}", tampered, _ => null).Expanded);
    }

    [Fact]
    public void TheElevatedSideRefusesUserItems()
    {
        var user = Offered("HKCU", "dword");
        var machine = Offered("HKLM", "dword");
        Assert.Throws<InvalidOperationException>(() => KosStateFix.Validate(new KosFixJob { Ids = { $"e:{machine}", $"e:{user}" } }));
        Assert.Single(KosStateFix.Validate(new KosFixJob { Ids = { $"e:{machine}", "t:0" } }), c => c.Kind == KosStateFix.ChangeKind.Value);
        Assert.Throws<InvalidOperationException>(() => KosStateFix.Validate(new KosFixJob()));
    }

    [Fact]
    public void AppsFeaturesAndFilesAreResolvedFromTheList()
    {
        Assert.True(Expected.Packages.Count > 0 && Expected.Components.Count > 0 && Expected.Features.Count > 0 && Expected.Files.Count > 0);
        var p = KosStateFix.Resolve("p:0", Expected, _ => null);
        Assert.Equal((KosStateFix.ChangeKind.Package, Expected.Packages[0].Name, true), (p.Kind, p.Key, p.Machine));
        Assert.Equal(Expected.Components[3].Id, KosStateFix.Resolve("c:3", Expected, _ => null).Key);

        var feature = Expected.Features.FindIndex(f => !f.Capability && !f.Enabled);
        var on = Expected.Features.FindIndex(f => !f.Capability && f.Enabled);
        var capability = Expected.Features.FindIndex(f => f.Capability && !f.Enabled);
        Assert.Equal((KosStateFix.ChangeKind.Feature, "disabled"), (KosStateFix.Resolve($"f:{feature}", Expected, _ => null) is var a ? (a.Kind, a.Value) : default));
        Assert.Equal("enabled", KosStateFix.Resolve($"f:{on}", Expected, _ => null).Value);
        Assert.Equal(KosStateFix.ChangeKind.Capability, KosStateFix.Resolve($"f:{capability}", Expected, _ => null).Kind);

        // Defender's own files only go with Defender, in Safe Mode; the others are deleted as the image build does.
        var defender = Expected.Files.FindIndex(f => f.Path.EndsWith("WdFilter.sys", StringComparison.OrdinalIgnoreCase));
        var smartscreen = Expected.Files.FindIndex(f => f.Path.EndsWith(@"System32\smartscreen.exe", StringComparison.OrdinalIgnoreCase));
        var folder = Expected.Files.FindIndex(f => f.Path.EndsWith("inboxapps", StringComparison.OrdinalIgnoreCase));
        Assert.Throws<InvalidOperationException>(() => KosStateFix.Resolve($"x:{defender}", Expected, _ => null));
        Assert.Equal(KosStateFix.ChangeKind.File, KosStateFix.Resolve($"x:{smartscreen}", Expected, _ => null).Kind);
        Assert.Equal("folder", KosStateFix.Resolve($"x:{folder}", Expected, _ => null).Name);
        foreach (var bad in new[] { $"p:{Expected.Packages.Count}", $"c:{Expected.Components.Count}", $"f:{Expected.Features.Count}", $"x:{Expected.Files.Count}" })
            Assert.Throws<InvalidOperationException>(() => KosStateFix.Resolve(bad, Expected, _ => null));
        Assert.All(KosStateFix.Validate(new KosFixJob { Ids = { "p:0", $"f:{feature}", $"x:{folder}" } }), c => Assert.True(c.Machine));
    }

    [Theory]
    [InlineData("*OutlookForWindows*", "Microsoft.OutlookForWindows", true)]
    [InlineData("microsoft.sechealthui", "Microsoft.SecHealthUI", true)] // NTLite ids are lower case
    [InlineData("Microsoft.SecHealthUI", "Microsoft.SecHealthUIExtra", false)]
    [InlineData("f46d4000-fd22-4db4-ac8e-4e1ddde828fe", "F46D4000-FD22-4DB4-AC8E-4E1DDDE828FE", true)]
    public void PackagesMatchLikePowerShell(string pattern, string name, bool match) => Assert.Equal(match, KosExpected.Like(pattern, name));

    [Fact]
    public void FilesAreOnTheWindowsDriveAndDefendersWaitForSafeMode()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory)!;
        Assert.Equal(Path.Combine(root, @"Windows\System32\x.exe"), KosSystemItems.LocalPath(@"C:\Windows\System32\x.exe"));
        Assert.True(KosSystemItems.DefenderFile(@"C:\Windows\System32\drivers\WdFilter.sys"));
        Assert.True(KosSystemItems.DefenderFile(@"C:\Windows\System32\SecurityHealthService.exe"));
        Assert.False(KosSystemItems.DefenderFile(@"C:\Windows\System32\smartscreen.exe"));
        Assert.Equal(KosArea.Ai, KosCheck.AreaOf(@"C:\Windows\systemapps\MicrosoftWindows.Client.CoreAI_cw5n1h2txyewy"));
        Assert.Equal(KosArea.Defender, KosCheck.AreaOf("Microsoft.SecHealthUI"));
        // cmd reads these even inside quotes: such a path never reaches its command line (nothing runs).
        foreach (var bad in new[] { @"C:\x&y", @"C:\%windir%", @"C:\a""b" })
            Assert.Throws<InvalidOperationException>(() => TrustedInstaller.DeletePath(bad, folder: false));
    }

    [Fact]
    public void CapabilitiesAreReadFromDismsTable()
    {
        const string output = "Deployment Image Servicing and Management tool\r\n\r\n------------------------------------- | -----------\r\n" +
            "Capability Identity                   | State\r\n------------------------------------- | -----------\r\n" +
            "App.StepsRecorder~~~~0.0.1.0          | Not Present\r\nLanguage.OCR~~~en-US~0.0.1.0          | Installed\r\n\r\nThe operation completed successfully.\r\n";
        var list = Apps.OptionalFeatures.ParseTable(output, "Capability Identity");
        Assert.Equal(new[] { ("App.StepsRecorder~~~~0.0.1.0", "Not Present"), ("Language.OCR~~~en-US~0.0.1.0", "Installed") }, list.Select(c => (c.Name, c.State)));
    }

    [Theory]
    [InlineData("Verification 42% complete.", 42.0, null)]
    [InlineData("Verificação 7% concluída.", 7.0, null)] // sfc in another language: only the number counts
    [InlineData("[=====                      10.3%                          ]", 10.3, null)]
    [InlineData("[==========================100.0%==========================] The restore operation completed successfully.", 100.0,
        "The restore operation completed successfully.")]
    [InlineData("Beginning verification phase of system scan.", null, "Beginning verification phase of system scan.")]
    [InlineData("\0 \t", null, null)]
    public void RepairProgressIsReadFromSfcAndDismLines(string line, double? percent, string? text) =>
        Assert.Equal((percent, text), WindowsRepair.ParseLine(line));

    [Fact]
    public void TheElevatedSideRunsOnlySfcAndDismWithTheirOwnCommand()
    {
        var id = Guid.NewGuid().ToString();
        WindowsRepair.Validate(new RepairJob { Tool = WindowsRepair.Sfc, RunId = id });
        WindowsRepair.Validate(new RepairJob { Tool = WindowsRepair.Dism, RunId = id });
        Assert.Throws<InvalidOperationException>(() => WindowsRepair.Validate(new RepairJob { Tool = "cmd", RunId = id }));
        Assert.Throws<InvalidOperationException>(() => WindowsRepair.Validate(new RepairJob { Tool = WindowsRepair.Sfc, RunId = @"..\x" }));
        Assert.True(WindowsRepair.SourceMissing(new RepairResult { Tool = WindowsRepair.Dism, ExitCode = unchecked((int)0x800f081f) }));
        // A KOS install with Windows Update off (DISM's "repair content could not be found anywhere").
        Assert.True(WindowsRepair.SourceMissing(new RepairResult { Tool = WindowsRepair.Dism, ExitCode = unchecked((int)0x800f0915) }));
        Assert.False(WindowsRepair.SourceMissing(new RepairResult { Tool = WindowsRepair.Dism, ExitCode = 0 }));
    }

    /// <summary>The whole check on this PC, read-only.</summary>
    [Fact]
    public void CheckRunsOnThisPc()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = KosCheck.Run();
        output.WriteLine($"{sw.ElapsedMilliseconds} ms: compared {r.Compared}, unreadable {r.Unreadable}, items {r.Items.Count} " +
            $"({string.Join(", ", r.Items.GroupBy(x => x.Section).Select(g => $"{g.Key} {g.Count()}"))})");
        foreach (var x in r.Items.Where(x => x.Id[0] is 'p' or 'c' or 'f' or 'x').Concat(r.Items.Take(20))) output.WriteLine($"{x.Section} {x.Area} {x.Id} {x.Title} | {x.Path} | {x.Expected} -> {x.Current}");
        Assert.True(r.Compared > 1000);
        Assert.Equal(Expected.SnapshotKeys.Count, r.MachineNow.Keys.Count + r.UserNow.Keys.Count);
        Assert.All(r.Items.Where(x => x.TweakId is null && x.NotFixable is null), x => KosStateFix.Resolve(x.Id, Expected,
            root => (KosReg.Machine(root) ? r.MachineReference : r.UserReference)?.Snapshot));
    }
}
