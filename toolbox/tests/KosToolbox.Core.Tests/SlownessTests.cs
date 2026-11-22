using KosToolbox.Core.SystemInfo;
using KosToolbox.Core.Text;
using Xunit;

namespace KosToolbox.Core.Tests;

public class SlownessTests
{
    // A real Application Hang event from this PC (Application log, 2026-11-17), as wevtutil prints it.
    const string HangXml = "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Application Hang' Guid='{c631c3dc-c676-59e4-2db3-5c0af00f9675}'/>" +
        "<EventID>1002</EventID><Version>0</Version><Level>2</Level><Task>101</Task><Opcode>0</Opcode><Keywords>0x8000000000000000</Keywords>" +
        "<TimeCreated SystemTime='2026-11-17T16:24:42.9294562Z'/><EventRecordID>6752</EventRecordID><Correlation/><Execution ProcessID='8832' ThreadID='11912'/>" +
        "<Channel>Application</Channel><Computer>PC</Computer><Security UserID='S-1-5-18'/></System><EventData><Data Name='AppName'>soffice.bin</Data>" +
        "<Data Name='AppVersion'>26.8.1.1</Data><Data Name='ProcessId'>0x24e4</Data><Data Name='StartTime'>0x1dd76b00db87c29</Data><Data Name='TerminationTime'>4</Data>" +
        "<Data Name='ExeFileName'>C:\\Program Files\\LibreOffice\\program\\soffice.bin</Data><Data Name='ReportId'>515f7a5c-3cf5-4253-9517-f770444f4df9</Data>" +
        "<Data Name='PackageFullName'></Data><Data Name='PackageRelativeAppId'></Data><Data Name='HangType'>Unknown</Data></EventData></Event>";

    [Fact]
    public void ParsesAnApplicationHangEvent()
    {
        var h = Slowness.ParseHang(HangXml)!;
        Assert.Equal("soffice.bin", h.Program);
        Assert.Equal(@"C:\Program Files\LibreOffice\program\soffice.bin", h.Path);
        Assert.Equal(new DateTime(2026, 11, 17, 16, 24, 42, DateTimeKind.Utc).ToLocalTime(), h.Time, TimeSpan.FromSeconds(1));
        // Unnamed data (older layout) by position; another event id is not a hang.
        var unnamed = HangXml.Replace(" Name='AppName'", "").Replace(" Name='ExeFileName'", "");
        Assert.Equal("soffice.bin", Slowness.ParseHang(unnamed)!.Program);
        Assert.Null(Slowness.ParseHang(HangXml.Replace("<EventID>1002", "<EventID>1001")));
    }

    [Fact]
    public void GroupsHangsByProgramAndDropsFutureOnes()
    {
        var now = new DateTime(2026, 11, 22, 12, 0, 0);
        var groups = Slowness.Group(new[]
        {
            new AppHang(now.AddDays(-1), "a.exe", @"C:\a.exe"), new AppHang(now.AddDays(-3), "A.EXE", null), new AppHang(now.AddDays(-2), "b.exe", null),
            new AppHang(now.AddYears(2), "b.exe", null),
        }, now);
        Assert.Equal(2, groups.Count);
        Assert.Equal(("a.exe", 2, now.AddDays(-1), @"C:\a.exe"), (groups[0].Program, groups[0].Count, groups[0].Last, groups[0].Path));
        Assert.Equal(1, groups[1].Count);
    }

    [Fact]
    public void RatesDisksByLatencyAndIgnoresIdleOnes()
    {
        Assert.Equal(DiskSpeed.Fine, Slowness.Rate(5, 100));
        Assert.Equal(DiskSpeed.Slow, Slowness.Rate(Slowness.SlowDiskMs, 100));
        Assert.Equal(DiskSpeed.VerySlow, Slowness.Rate(120, 100));
        Assert.Equal(DiskSpeed.Idle, Slowness.Rate(400, 0.3)); // one slow request on an idle disk
    }

    [Fact]
    public void StartOffsetSkipsClockChangesAndLaterRestarts()
    {
        var boot = new DateTime(2026, 11, 22, 8, 0, 0);
        Assert.Equal(90, Slowness.StartOffset(boot.AddSeconds(90), boot));
        Assert.Null(Slowness.StartOffset(boot.AddSeconds(-5), boot));
        Assert.Null(Slowness.StartOffset(boot + Slowness.MaxStartupOffset + TimeSpan.FromSeconds(1), boot)); // started again by hand, or Fast Startup's old boot
    }

    static readonly MemorySample Calm = new(40, 16000, 32000);
    static readonly DiskSample FastDisk = new("Disk 0 (C:)", 0.4, 0, 2, 50);

    [Fact]
    public void VerdictRules()
    {
        Assert.Equal(new[] { Txt.SlowVerdictNothing }, Slowness.Verdict(new List<HungWindow>(), new[] { FastDisk }, Calm, new List<HangGroup>()));

        var hung = new[] { new HungWindow("game", "Game"), new HungWindow("game", "Launcher") };
        var slowDisk = new DiskSample("Disk 1 (D:)", 38, 4, 99, 120);
        var full = new MemorySample(95, 900, 32000);
        var verdict = Slowness.Verdict(hung, new[] { FastDisk, slowDisk }, full, new List<HangGroup>());
        Assert.Equal(2, verdict.Count); // at most two sentences, the most pressing first
        Assert.Equal("Not responding now: game.", verdict[0]);
        Assert.Equal("The disk answers slowly: 38 ms per request (Disk 1 (D:)).", verdict[1]);

        Assert.StartsWith("Memory is nearly full", Slowness.Verdict(new List<HungWindow>(), new[] { FastDisk }, new MemorySample(50, 500, 32000), new List<HangGroup>())[0]);
        var often = new[] { new HangGroup("x.exe", 2, DateTime.Now, null), new HangGroup("y.exe", Slowness.FrequentHangs, DateTime.Now, null) };
        Assert.Equal("y.exe stopped responding 3 times in the last 30 days.", Slowness.Verdict(new List<HungWindow>(), new[] { FastDisk }, Calm, often)[0]);
        Assert.Equal(Txt.SlowVerdictNothing, Slowness.Verdict(new List<HungWindow>(), new List<DiskSample>(), null, often.Take(1).ToList())[0]);
    }

    [Fact]
    public void ReadsThisPc()
    {
        Assert.NotNull(Slowness.HungWindows());
        Assert.All(Slowness.Hangs(), h => Assert.False(string.IsNullOrEmpty(h.Program)));
        Assert.True(Slowness.BootTime() < DateTime.Now);
    }
}
