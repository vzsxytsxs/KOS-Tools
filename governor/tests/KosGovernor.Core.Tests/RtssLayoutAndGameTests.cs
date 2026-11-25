using KosGovernor.Core;
using KosToolbox.Core.Gpu;
using Xunit;

namespace KosGovernor.Core.Tests;

public class RtssLayoutAndGameTests
{
    const ulong Gb = 1UL << 30;

    [Fact]
    public void TheCompactLayoutPutsTheGroupsOnOneLineAndKeepsTheGraphApart()
    {
        var items = RtssOsdItems.Fps | RtssOsdItems.Cpu | RtssOsdItems.Ram;
        Assert.Equal("FPS  <FR>  |  CPU  5%  RAM 1.0/2.0 GB", Rtss.OsdText(items, 5, Gb, 2 * Gb, null, compact: true));
        Assert.Equal("FPS  <FR>\nCPU  5%  RAM 1.0/2.0 GB", Rtss.OsdText(items, 5, Gb, 2 * Gb, null)); // the default stays as it was
        Assert.Equal("FPS  <FR>\n" + Rtss.GraphTag, Rtss.OsdText(RtssOsdItems.Fps | RtssOsdItems.FrametimeGraph, 5, Gb, 2 * Gb, null, compact: true));
    }

    [Fact]
    public void AnAccentColorTagsTheLabelsOnly()
    {
        Assert.Equal("<C0=00FF00><C0>FPS<C>  <FR>\n<C0>CPU<C>  5%", Rtss.OsdText(RtssOsdItems.Fps | RtssOsdItems.Cpu, 5, Gb, 2 * Gb, null, color: RtssOsdColor.PureGreen));
        Assert.Equal("", Rtss.OsdText(RtssOsdItems.None, 5, Gb, 2 * Gb, null, color: RtssOsdColor.HotPink)); // nothing to show, nothing to color
        Assert.Null(Rtss.AccentHex(RtssOsdColor.None));
        foreach (var c in Enum.GetValues<RtssOsdColor>().Where(c => c != RtssOsdColor.None))
            Assert.Matches("^[0-9A-F]{6}$", Rtss.AccentHex(c));
    }

    [Theory]
    [InlineData("game.exe", true)]
    [InlineData("Cyberpunk2077.exe", true)]
    [InlineData("My Game (x64).exe", true)]
    [InlineData("game", false)]
    [InlineData("..\\game.exe", false)]
    [InlineData("C:\\games\\game.exe", false)]
    [InlineData("a/b.exe", false)]
    [InlineData("game..exe", false)]
    [InlineData(".exe", false)]
    [InlineData("", false)]
    public void OnlyAProgramNameIsAnRtssProfile(string name, bool ok) => Assert.Equal(ok, Rtss.IsGameName(name));

    [Fact]
    public void TheSteamAndBenchmarkStylesHaveTheirOwnShape()
    {
        var items = RtssOsdItems.Fps | RtssOsdItems.FrametimeGraph | RtssOsdItems.Cpu | RtssOsdItems.Ram;
        var steam = Rtss.OsdText(items, 5, Gb, 2 * Gb, null, style: RtssOsdStyle.Steam);
        Assert.StartsWith("<C0=A8A8A8><C1=FFFFFF><S1=190>", steam);     // grey labels, white values, a big number
        Assert.Contains(Rtss.GraphTag, steam);
        Assert.DoesNotContain(Rtss.CoresTag, steam);                    // no per-core bars in this one
        Assert.Contains("<C0>CPU<C>  <C1>5%<C>   <C0>RAM<C>", steam);   // CPU and RAM share a line

        var bench = Rtss.OsdText(items, 5, Gb, 2 * Gb, null, color: RtssOsdColor.ElectricBlue, style: RtssOsdStyle.Benchmark);
        Assert.StartsWith("<C0=00A0FF><C1=FFFFFF><S1=230>", bench);     // the chosen accent on the labels
        var lines = bench.Split('\n');
        Assert.Equal(Rtss.CoresTag, lines[Array.FindIndex(lines, l => l.Contains("CPU")) + 1]); // the bars sit under the CPU line

        Assert.Equal("", Rtss.OsdText(RtssOsdItems.None, 5, Gb, 2 * Gb, null, style: RtssOsdStyle.Steam));
        // Classic is unchanged by the new parameter.
        Assert.Equal(Rtss.OsdText(items, 5, Gb, 2 * Gb, null), Rtss.OsdText(items, 5, Gb, 2 * Gb, null, style: RtssOsdStyle.Classic));
    }

    [Fact]
    public void TheStyleIsSavedWithTheProfile()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new GamemodeSystemSettings { RtssOsdStyle = RtssOsdStyle.Benchmark }, GovernorJson.Default.GamemodeSystemSettings);
        Assert.Equal(RtssOsdStyle.Benchmark, System.Text.Json.JsonSerializer.Deserialize(json, GovernorJson.Default.GamemodeSystemSettings)!.RtssOsdStyle);
        Assert.Equal(RtssOsdStyle.Classic, System.Text.Json.JsonSerializer.Deserialize("""{"RtssOsd":true}""", GovernorJson.Default.GamemodeSystemSettings)!.RtssOsdStyle);
        var c = new GovernorConfig();
        c.Active.System.RtssOsdStyle = (RtssOsdStyle)9;
        Assert.Contains(Validation.Problems(c, CpuTopology.Current), p => p.Contains("on-screen display style")); // an unknown style is refused like an unknown color
    }

    [Fact]
    public void KeepRtssRunningIsSavedAndIsNotAGamemodeChange()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize("""{"RtssOsd":true}""", GovernorJson.Default.GamemodeSystemSettings)!;
        Assert.False(old.RtssKeepRunning); // off unless asked for
        var json = System.Text.Json.JsonSerializer.Serialize(new GamemodeSystemSettings { RtssKeepRunning = true }, GovernorJson.Default.GamemodeSystemSettings);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, GovernorJson.Default.GamemodeSystemSettings)!;
        Assert.True(back.RtssKeepRunning);
        Assert.Equal(new GamemodeSystemSettings().Any, back.Any); // starting RTSS isn't something gamemode applies and undoes
    }

    [Fact]
    public void GameLimitsAreValidatedAndSavedWithTheProfile()
    {
        var c = new GovernorConfig();
        string[] Problems() => Validation.Problems(c, CpuTopology.Current).Where(p => p.Contains("RTSS") || p.Contains("frame limit")).ToArray();
        c.Active.System.RtssGameLimits.Add(new RtssGameLimit { Exe = "cs2.exe", Fps = 141 });
        Assert.Empty(Problems());
        c.Active.System.RtssGameLimits.Add(new RtssGameLimit { Exe = "CS2.EXE", Fps = 100 });
        Assert.Single(Problems()); // two limits for one game
        c.Active.System.RtssGameLimits.Clear();
        c.Active.System.RtssGameLimits.Add(new RtssGameLimit { Exe = "..\\x.exe", Fps = 60 });
        Assert.Single(Problems());
        c.Active.System.RtssGameLimits[0] = new RtssGameLimit { Exe = "x.exe", Fps = 9 };
        Assert.Single(Problems());
        c.Active.System.RtssOsdColor = (RtssOsdColor)99;
        Assert.Equal(2, Problems().Length);

        // A profile saved before this existed reads with no game limits, and the new fields round trip.
        var old = System.Text.Json.JsonSerializer.Deserialize("""{"RtssOsd":true}""", GovernorJson.Default.GamemodeSystemSettings)!;
        Assert.Empty(old.RtssGameLimits);
        Assert.False(old.RtssOsdCompact);
        Assert.False(old.Any && old.RtssGameLimits.Count > 0);
        var json = System.Text.Json.JsonSerializer.Serialize(new GamemodeSystemSettings { RtssOsdCompact = true, RtssOsdColor = RtssOsdColor.ElectricBlue, RtssGameLimits = { new RtssGameLimit { Exe = "a.exe", Fps = 90 } } }, GovernorJson.Default.GamemodeSystemSettings);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, GovernorJson.Default.GamemodeSystemSettings)!;
        Assert.True(back.RtssOsdCompact);
        Assert.Equal(RtssOsdColor.ElectricBlue, back.RtssOsdColor);
        Assert.Equal(("a.exe", 90), (back.RtssGameLimits.Single().Exe, back.RtssGameLimits.Single().Fps));
        Assert.True(back.Any); // a game limit alone counts as a gamemode setting
    }
}
