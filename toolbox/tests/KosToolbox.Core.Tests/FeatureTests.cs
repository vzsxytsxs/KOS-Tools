using System.Reflection;
using System.Text;
using KosToolbox.Core.Catalog;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Power;
using KosToolbox.Core.Services;
using KosToolbox.Core.Startup;
using KosToolbox.Core.Text;
using KosToolbox.Core.Tools;
using Xunit;

namespace KosToolbox.Core.Tests;

public class SnapshotTests
{
    static string PsJson(int count, int version = 1, int? declared = null)
    {
        // Format of Windows PowerShell 5.1's ConvertTo-Json: BOM, two spaces after ":", null and empty {}.
        var sb = new StringBuilder("\uFEFF{\r\n    \"SchemaVersion\":  " + version + ",\r\n    \"Snapshot\":  \"Windows.Default.Services\",\r\n" +
            "    \"CapturedAt\":  \"2026-06-01T10:00:00.0000000-03:00\",\r\n    \"Computer\":  \"KOS-VM\",\r\n" +
            $"    \"ServiceCount\":  {declared ?? count},\r\n    \"Services\":  [\r\n");
        for (var i = 0; i < count; i++)
        {
            var ov = i == 0 ? "{ \"0\": 4, \"1\": 4 }" : i == 1 ? "\"\"" : "{\r\n}";
            var delayed = i == 2 ? "1" : "null";
            sb.Append($"        {{ \"Name\":  \"svc{i:00}\", \"Start\":  {i % 5}, \"DelayedAutoStart\":  {delayed}, \"StartOverride\":  {ov} }}");
            sb.Append(i < count - 1 ? ",\r\n" : "\r\n");
        }
        return sb.Append("    ]\r\n}").ToString();
    }

    [Fact]
    public void ParsesPowerShellSnapshot()
    {
        var s = ServiceSnapshots.Parse("x", PsJson(20), "x.json");
        Assert.Equal(20, s.Services.Count);
        Assert.Equal("KOS-VM", s.Computer);
        Assert.NotNull(s.CapturedAt);
        Assert.Equal(2, s.Services[0].StartOverride.Count);
        Assert.Empty(s.Services[1].StartOverride);
        Assert.Null(s.Services[0].DelayedAutoStart);
        Assert.Equal(1, s.Services[2].DelayedAutoStart);
    }

    [Fact]
    public void RefusesIncompleteOrNewerSnapshots()
    {
        Assert.ThrowsAny<Exception>(() => ServiceSnapshots.Parse("x", PsJson(19), "x.json"));
        Assert.ThrowsAny<Exception>(() => ServiceSnapshots.Parse("x", PsJson(20, declared: 21), "x.json"));
        var newer = Assert.ThrowsAny<Exception>(() => ServiceSnapshots.Parse("x", PsJson(20, version: 2), "x.json"));
        Assert.Contains("newer", newer.Message);
        Assert.ThrowsAny<Exception>(() => ServiceSnapshots.Parse("x", "{ broken", "x.json"));
    }

    [Fact]
    public void JsonRoundTripsAndRegHasOneStartPerService()
    {
        var states = Enumerable.Range(0, 20).Select(i => new ServiceState($"s{i}", i % 5, i == 3 ? 0 : null,
            i == 4 ? new Dictionary<string, int> { ["0"] = 4 } : new Dictionary<string, int>())).ToList();
        var json = ServiceSnapshots.ToJson("Custom.1", DateTimeOffset.Now, "PC", states);
        var back = ServiceSnapshots.Parse("Custom.1", json, "c.json");
        Assert.Equal(states.Select(ServiceSnapshots.FormatState), back.Services.Select(ServiceSnapshots.FormatState));

        var reg = ServiceSnapshots.ToReg(states);
        Assert.StartsWith("Windows Registry Editor Version 5.00\r\n", reg);
        Assert.Equal(20, reg.Split("\r\n").Count(l => l.StartsWith("\"Start\"=dword:")));
        Assert.Contains("\"Start\"=dword:00000004", reg);
    }

    [Fact]
    public void CompareListsOnlyDifferences()
    {
        var none = new Dictionary<string, int>();
        var a = new ServiceSnapshot("a", null, null, new[] { new ServiceState("Same", 3, null, none), new ServiceState("Changed", 2, 1, none), new ServiceState("Gone", 3, null, none) });
        var b = new ServiceSnapshot("b", null, null, new[] { new ServiceState("same", 3, null, none), new ServiceState("Changed", 4, null, none), new ServiceState("New", 3, null, none) });
        var diff = ServiceSnapshots.Compare(a, b);
        Assert.Equal(new[] { "Changed", "Gone", "New" }, diff.Select(d => d.Name));
        Assert.Equal("Start=2;Delayed=1;Override=", diff[0].Before);
        Assert.Equal("Start=4;Delayed=-;Override=", diff[0].After);
        Assert.Equal("missing", diff[1].After);
        Assert.Equal("missing", diff[2].Before);
    }

    [Theory]
    [InlineData("Custom.20260101-1200", true)]
    [InlineData("Windows.Default.Services", true)]
    [InlineData("a b", false)]
    [InlineData(@"..\x", false)]
    [InlineData("", false)]
    public void SnapshotNames(string name, bool valid) => Assert.Equal(valid, ServiceSnapshots.IsValidName(name));

    [Fact]
    public void DiffFileNames()
    {
        Assert.Equal("TheKOS.Services.Diff.csv", ServiceSnapshots.DiffFileName(ServiceSnapshots.Baseline, ServiceSnapshots.KosSnapshot));
        Assert.Equal("Diff.A.vs.B.csv", ServiceSnapshots.DiffFileName("A", "B"));
    }
}

public class RegExportTests
{
    static byte[] Sz(string s) => Encoding.Unicode.GetBytes(s + "\0");

    [Fact]
    public void FormatsLikeRegedit()
    {
        Assert.Equal("\"Path\"=\"C:\\\\x \\\"y\\\"\"", RegExport.FormatValue("Path", 1, Sz("C:\\x \"y\"")));
        Assert.Equal("@=\"v\"", RegExport.FormatValue("", 1, Sz("v")));
        Assert.Equal("\"Start\"=dword:00000004", RegExport.FormatValue("Start", 4, BitConverter.GetBytes(4)));
        Assert.Equal("\"B\"=hex:01,ff", RegExport.FormatValue("B", 3, new byte[] { 1, 255 }));
        Assert.StartsWith("\"E\"=hex(2):25,00", RegExport.FormatValue("E", 2, Sz("%x%")));
        // Text with a line break doesn't fit on a "text" line: it goes as hex(1).
        Assert.StartsWith("\"M\"=hex(1):", RegExport.FormatValue("M", 1, Sz("a\r\nb")));
    }

    [Fact]
    public void LongHexWrapsUnder80Columns()
    {
        var lines = RegExport.FormatValue("Data", 3, Enumerable.Range(0, 200).Select(i => (byte)i).ToArray()).Split("\r\n");
        Assert.True(lines.Length > 1);
        Assert.All(lines, l => Assert.True(l.Length <= 80, l));
        Assert.All(lines[..^1], l => Assert.EndsWith(",\\", l));
        Assert.All(lines[1..], l => Assert.StartsWith("  ", l));
    }
}

public class PowerTests
{
    [Theory]
    [InlineData(@"@%SystemRoot%\system32\powrprof.dll,-505,Turn off hard disk after", "Turn off hard disk after")]
    [InlineData(@"@%SystemRoot%\system32\powrprof.dll,-505", null)]
    [InlineData("High performance", "High performance")]
    [InlineData("  ", null)]
    public void EnglishText(string raw, string? expected) => Assert.Equal(expected, PowerPlans.EnglishText(raw));

    static PowerSettingInfo Range() => new() { Subgroup = Guid.Empty, Setting = Guid.Empty, Name = "x", Kind = PowerValueKind.Range, Min = 0, Max = 100, Increment = 5, Units = "%" };

    [Fact]
    public void ValidatesAgainstMetadata()
    {
        Assert.Null(PowerPlans.Validate(Range(), 50));
        Assert.NotNull(PowerPlans.Validate(Range(), 101));
        Assert.NotNull(PowerPlans.Validate(Range(), 7));
        var choice = new PowerSettingInfo { Subgroup = Guid.Empty, Setting = Guid.Empty, Name = "c", Kind = PowerValueKind.Choice,
            Choices = new[] { new PowerChoice(0, "Off"), new PowerChoice(1, "On") } };
        Assert.Null(PowerPlans.Validate(choice, 1));
        Assert.NotNull(PowerPlans.Validate(choice, 2));
        Assert.Equal(Txt.PowerNoMetadata, PowerPlans.Validate(new PowerSettingInfo { Subgroup = Guid.Empty, Setting = Guid.Empty, Name = "r", Kind = PowerValueKind.ReadOnly }, 0));
    }
}

public class StartupTests
{
    [Fact]
    public void ResolvesExecutableFromCommand()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Program Files\App\app.exe", @"C:\Tools\run.exe" };
        Func<string, bool> exists = files.Contains;
        Assert.Equal(@"C:\Program Files\App\app.exe", CommandLine.Executable("\"C:\\Program Files\\App\\app.exe\" --tray", exists));
        Assert.Equal(@"C:\Program Files\App\app.exe", CommandLine.Executable(@"C:\Program Files\App\app.exe --tray", exists));
        Assert.Equal(@"C:\Tools\run.exe", CommandLine.Executable(@"C:\Tools\run -min", exists));
        Assert.Equal(@"C:\Windows\system32\drivers\x.sys", CommandLine.Executable(@"\??\C:\Windows\system32\drivers\x.sys", exists));
        Assert.Null(CommandLine.Executable("   ", exists));
    }

    [Fact]
    public void WinlogonAndActiveSetupAreReadOnly()
    {
        // Reads this PC's registry (read-only): every Windows has Winlogon's Userinit.
        var winlogon = new WinlogonProvider().Collect(true).ToList();
        Assert.Contains(winlogon, e => e.Name == "Userinit" && e.MachineWide);
        Assert.All(winlogon.Concat(new ActiveSetupProvider().Collect(true)), e => Assert.False(e.CanToggle));
        Assert.Contains(StartupManager.Active(advanced: false, includeDrivers: false), p => p is RunKeyProvider);
        Assert.DoesNotContain(StartupManager.Active(advanced: false, includeDrivers: false), p => p is WinlogonProvider or ActiveSetupProvider);
    }

    [Fact]
    public void CriticalServices()
    {
        Assert.Equal(Txt.CriticalBootStart, ServiceStartupProvider.Critical(0, 1));
        Assert.Equal(Txt.CriticalBootStart, ServiceStartupProvider.Critical(1, 0));
        Assert.Equal(Txt.CriticalErrorControl, ServiceStartupProvider.Critical(3, 3));
        Assert.Null(ServiceStartupProvider.Critical(2, 1));
        Assert.Null(ServiceStartupProvider.Critical(null, null));
    }

    [Fact]
    public void ApprovedValueIsTwelveBytes()
    {
        var on = Convert.FromHexString(StartupApproved.Value(true));
        var off = Convert.FromHexString(StartupApproved.Value(false));
        Assert.Equal(12, on.Length);
        Assert.True(StartupApproved.IsEnabledFlag(on[0]));
        Assert.False(StartupApproved.IsEnabledFlag(off[0]));
    }

    // Structural validations run before any access to the system.
    [Theory]
    [InlineData("", 3, null)]
    [InlineData(@"a\b", 3, null)]
    [InlineData("svc", 5, null)]
    [InlineData("svc", 3, 1)]
    [InlineData("svc", 2, 2)]
    public void ServiceChangesAreRestricted(string name, int start, int? delayed) =>
        Assert.ThrowsAny<InvalidOperationException>(() => new ElevatedJob { Service = new ServiceStartChange { Name = name, Start = start, Delayed = delayed } }.Validate());

    [Theory]
    [InlineData("Microsoft\\Windows\\x")]
    [InlineData("\\..\\x")]
    public void TaskPathsAreRestricted(string path) =>
        Assert.ThrowsAny<InvalidOperationException>(() => new ElevatedJob { TaskChange = new TaskToggle { Path = path, Enabled = false } }.Validate());

    [Fact]
    public void SnapshotJobsAreRestricted()
    {
        new ElevatedJob { Snapshot = new SnapshotJob { Mode = SnapshotJob.Capture, Name = "Custom.1", Full = true } }.Validate();
        new ElevatedJob { Snapshot = new SnapshotJob { Mode = SnapshotJob.Organize } }.Validate();
        Assert.ThrowsAny<Exception>(() => new ElevatedJob { Snapshot = new SnapshotJob { Mode = SnapshotJob.Capture, Name = "a b" } }.Validate());
        Assert.ThrowsAny<Exception>(() => new ElevatedJob { Snapshot = new SnapshotJob { Mode = SnapshotJob.Capture, Name = ServiceSnapshots.Current } }.Validate());
        Assert.ThrowsAny<Exception>(() => new ElevatedJob { Snapshot = new SnapshotJob { Mode = SnapshotJob.Capture, Name = "x", Services = { "a" } } }.Validate());
        Assert.ThrowsAny<Exception>(() => new ElevatedJob { Snapshot = new SnapshotJob { Mode = SnapshotJob.Restore, Name = "x" } }.Validate());
        Assert.ThrowsAny<Exception>(() => new ElevatedJob { Snapshot = new SnapshotJob { Mode = SnapshotJob.Organize, Name = "x" } }.Validate());
        Assert.ThrowsAny<Exception>(() => new ElevatedJob { Snapshot = new SnapshotJob { Mode = "delete" } }.Validate());
    }
}

public class ToolDiscoveryTests
{
    // Real files from the ProgramData\TheKOS\tools subfolders (list read from Carla's machine).
    [Theory]
    [InlineData("CPUZ_3.01", "cpuz_x64.exe", "cpuz_x64.exe:7447032")]
    [InlineData("LatencyMon", "LatMon.exe", "LatMon.exe:5458128|unins000.exe:3220541")]
    [InlineData("SystemInformer", "SystemInformer.exe", "peview.exe:1714224|SystemInformer.exe:3429000")]
    [InlineData("Snappy Driver Installer Origin", "SDIO_x64.exe", "SDIO_x64.exe:5356744|SDIOTranslationTool.exe:1371848")]
    [InlineData("Display Driver Uninstaller", "Display Driver Uninstaller.exe", "Display Driver Uninstaller.exe:1778960")]
    [InlineData("Network Latency View", "NetworkLatencyView.exe", "NetworkLatencyView.exe:220160")]
    [InlineData("simplewall-3.8.7", "simplewall.exe", "simplewall.exe:983552")]
    [InlineData("winntsetup", "WinNTSetup_x64.exe", "WinNTSetup_x64.exe:1084928")]
    [InlineData("HWiNFO", "HWiNFO64.exe", "HWiNFO64.exe:10884584")]
    [InlineData("Dism++", "Dism++x64.exe", "Dism++x64.exe:1071616")]
    [InlineData("Shortcuts", "App.lnk", "App.lnk:1000|helper.exe:5000")]
    public void PicksTheMainProgram(string folder, string expected, string files)
    {
        var list = files.Split('|').Select(f => f.Split(':')).Select(p => (p[0], long.Parse(p[1]))).ToList();
        Assert.Equal(new[] { expected }, ToolDiscovery.PickFromFolder(folder, list));
    }

    [Fact]
    public void IgnoresLibrariesAndUninstallers() =>
        Assert.Empty(ToolDiscovery.PickFromFolder("X", new[] { ("x.dll", 10L), ("unins000.exe", 10L), ("readme.txt", 1L) }));
}

public class LogTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "kos-log-" + Guid.NewGuid().ToString("N"));
    public LogTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void PruneRemovesOldAndExcessFiles()
    {
        var now = new DateTime(2026, 10, 11, 12, 0, 0);
        var files = new List<string>();
        for (var i = 0; i < Log.MaxFiles + 5; i++)
        {
            var f = Path.Combine(_dir, $"toolbox-{i}.log");
            File.WriteAllText(f, "x");
            File.SetLastWriteTime(f, now.AddHours(-i));
            files.Add(f);
        }
        var old = Path.Combine(_dir, "toolbox-old.log");
        File.WriteAllText(old, "x");
        File.SetLastWriteTime(old, now.AddDays(-(Log.RetentionDays + 1)));
        files.Add(old);

        var removed = Log.Prune(files, now);
        Assert.Contains(old, removed);
        Assert.Equal(Log.MaxFiles, files.Count(File.Exists));
        Assert.True(File.Exists(files[0]));
    }

    [Fact]
    public void TargetRotatesBySize()
    {
        var day = new DateTime(2026, 10, 11);
        Assert.EndsWith("toolbox-20261011.log", Log.Target(day, _ => 0));
        Assert.EndsWith("toolbox-20261011.2.log", Log.Target(day, f => f.EndsWith(".2.log") ? 10 : Log.MaxFileBytes));
    }
}

public class WindowsCategoryTests
{
    [Fact]
    public void EverySettingOfTheWindowsPageIsInACategoryAndNoCategoryIsEmpty()
    {
        var used = KosCatalog.Tweaks.Where(t => t.Page == Pages.Windows).Select(t => t.Tab).Distinct().Order();
        Assert.Equal(Pages.WindowsTabs.Order(), used);
    }
}

public class EnglishTextTests
{
    // Letters that only appear in Portuguese; the interface is entirely in English.
    static readonly char[] Portuguese = "ãõçáéíóúâêôàÃÕÇÁÉÍÓÚÂÊÔÀ".ToCharArray();

    static void AssertEnglish(string? text, string where)
    {
        if (text is null) return;
        Assert.True(text.IndexOfAny(Portuguese) < 0, $"{where}: {text}");
    }

    [Fact]
    public void TextConstantsAreEnglish()
    {
        foreach (var f in typeof(Txt).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(string)))
            AssertEnglish((string?)f.GetValue(null), f.Name);
    }

    [Fact]
    public void CatalogIsEnglish()
    {
        foreach (var t in KosCatalog.Tweaks)
        {
            AssertEnglish(t.Name, t.Id);
            AssertEnglish(t.Description, t.Id);
            AssertEnglish(t.Group, t.Id);
            AssertEnglish(t.Tab, t.Id);
            AssertEnglish(t.SecurityNote, t.Id);
            foreach (var o in t.Options) AssertEnglish(o.Label, $"{t.Id}/{o.Key}");
        }
        foreach (var a in KosCatalog.Actions)
        {
            AssertEnglish(a.Name, a.Id);
            AssertEnglish(a.Description, a.Id);
            AssertEnglish(a.ButtonText, a.Id);
        }
    }

    [Fact]
    public void SmartScreenAndDefenderAreGone()
    {
        var text = string.Join("\n", KosCatalog.Tweaks.Select(t => $"{t.Id} {t.Name} {t.Description}")
            .Concat(KosCatalog.Actions.Select(a => $"{a.Id} {a.Name} {a.Description}")));
        Assert.DoesNotContain("smartscreen", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("defender", text, StringComparison.OrdinalIgnoreCase);
    }
}
