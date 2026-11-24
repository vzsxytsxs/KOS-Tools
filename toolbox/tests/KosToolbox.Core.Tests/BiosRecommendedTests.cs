using KosToolbox.Core.Bios;
using Xunit;

namespace KosToolbox.Core.Tests;

public class BiosRecommendedTests
{
    static readonly BiosCpu Intel = new("GenuineIntel", 6, 0xB7);
    static readonly BiosCpu Zen2 = new("AuthenticAMD", 0x17, 0x71), Zen3 = new("AuthenticAMD", 0x19, 0x21), Zen4 = new("AuthenticAMD", 0x19, 0x61), Zen5 = new("AuthenticAMD", 0x1A, 0x44);

    /// <summary>A script in the SCEWIN format with these questions; each one is a list of lines after "Setup Question".</summary>
    static NvramScript Script(params string[] questions)
    {
        var token = 0;
        var text = new List<string> { "// AMISCE Utility. Ver 5.05.01.0002", "HIICrc32= 1A2B3C4D", "" };
        foreach (var q in questions)
        {
            text.Add($"Setup Question\t= {q.Split('\n')[0]}");
            if (!q.Contains("Help String")) text.Add("Help String\t= ");
            text.Add($"Token\t={++token:X}\t// Do NOT change this line");
            text.Add("Offset\t=00");
            text.Add("Width\t=01");
            text.AddRange(q.Split('\n')[1..]);
            text.Add("");
        }
        return NvramScript.Parse(string.Join("\r\n", text));
    }

    /// <summary>List question; the first option line starts with "Options\t=", the others are indented.</summary>
    static string List(string name, params string[] options) =>
        name + "\nOptions\t=" + options[0] + "\t// Move \"*\" to the desired Option" + string.Concat(options.Skip(1).Select(o => "\n         " + o));

    static BiosRecommendation? Rec(NvramScript s, BiosCpu cpu, string name) =>
        BiosRecommended.For(s, cpu).GetValueOrDefault(s.Questions.First(q => q.Name == name).Key);

    [Fact]
    public void TableParsesWithoutRepeatedNames()
    {
        var repeated = BiosRecommended.DuplicateNames().ToList();
        Assert.True(repeated.Count == 0, string.Join(", ", repeated));
    }

    [Theory]
    [InlineData("Global C-States Control", "Global C-state Control")]
    [InlineData("Intel(R) Speed Shift Technology", "Intel Speed Shift Technology")]
    [InlineData("AMD Cool & Quiet", "AMD  COOL&QUIET")]
    [InlineData("ACPI _CST C1 Declaration", "ACPI_CST C1 Declaration")]
    [InlineData("PEG0 - ASPM", "PEG0 ASPM")]
    public void NamesMatchWithoutCasePunctuationAndPlurals(string a, string b) =>
        Assert.Equal(BiosRecommended.Normalize(a), BiosRecommended.Normalize(b));

    [Fact]
    public void NamesWithDifferentWordsStayApart()
    {
        Assert.NotEqual(BiosRecommended.Normalize("CPPC"), BiosRecommended.Normalize("CPPC Preferred Cores"));
        Assert.NotEqual(BiosRecommended.Normalize("PECI"), BiosRecommended.Normalize("PECI Reset"));
    }

    [Fact]
    public void FindsTheOptionByMeaningNotByNumber()
    {
        // The same setting on two BIOSes: Disabled is 00 on one and 02 on the other, and the board calls it by another name.
        var a = Script(List("EIST", "*[01]Enabled", "[00]Disabled"));
        var b = Script(List("CPU EIST Function", "[00]Auto", "*[01]Enabled", "[02]Disabled"));

        Assert.Equal("00", Rec(a, Intel, "EIST")!.Value);
        Assert.Equal("02", Rec(b, Intel, "CPU EIST Function")!.Value);
        Assert.Equal("Disabled", Rec(b, Intel, "CPU EIST Function")!.Label);
    }

    [Fact]
    public void RecommendedValuesWriteAndReadBack()
    {
        var s = Script(
            List("EIST", "*[01]Enabled", "[00]Disabled"),
            "BIOS Hot-Plug Support\nValue\t=1\t// Enabled = 1, Disabled = 0",
            List("Interrupt Redirection Mode Selection", "[00]Fixed Priority", "*[02]Hash Vector", "[01]Round robin"),
            List("PCI Latency Timer", "[20]32 PCI Bus Clocks", "*[40]64 PCI Bus Clocks", "[F8]248 PCI Bus Clocks"),
            List("PEG0 - Max Link Speed", "[00]Auto", "[01]Gen1", "*[02]Gen2", "[04]Gen4", "[03]Gen3"),
            List("PEP CPU", "*[01]Enabled", "[00]Disabled"),
            List("PEP SATA PORT0", "*[01]Enabled", "[00]Disabled"));

        var recs = BiosRecommended.For(s, Intel);
        var changes = recs.ToDictionary(r => r.Key, r => r.Value.Value);
        Assert.Equal(new[] { "00", "0", "01", "F8", "04", "00", "00" }, s.Questions.Select(q => changes[q.Key]));

        var back = NvramScript.Parse(s.ImportScript(changes));
        Assert.All(back.Questions, q => Assert.True(NvramScript.SameValue(q, q.Current, changes[q.Key])));
    }

    [Fact]
    public void NoRecommendationWhenTheBiosHasNoSuchOption()
    {
        var s = Script(
            List("Intel C-State", "*[02]Auto", "[01]Enabled"),
            List("Hardware Prefetcher", "*[00]Auto", "[01]Manual"));

        Assert.Empty(BiosRecommended.For(s, Intel));
    }

    [Fact]
    public void NumbersAndOptionalSettings()
    {
        var s = Script(
            "Package Power Limit\nHelp String\t= Package power limit in watts\nValue\t=<65>",
            "PPT Limit\nHelp String\t= Limit in milliwatts\nValue\t=<88000>",
            List("Promontory Presence", "*[00]Auto", "[01]Disabled"),
            List("SVM Mode", "*[01]Enabled", "[00]Disabled"));

        var power = Rec(s, Zen3, "Package Power Limit")!;
        Assert.Equal(("100", true), (power.Value, power.Optional));
        Assert.NotNull(power.Note);
        Assert.Null(Rec(s, Zen3, "PPT Limit")); // not watts, so no ★: 100 would be a tiny limit
        Assert.True(Rec(s, Zen3, "Promontory Presence")!.Optional);
        Assert.True(Rec(s, Zen3, "SVM Mode")!.Optional);
    }

    [Fact]
    public void ValuesOnlyTheRawScriptHasAreOptional()
    {
        // Three Strike Counter is 1 in the hidden-settings script, but a real board numbers Enabled as 00: the script's 1 isn't proof.
        var s = Script(
            List("Three Strike Counter", "[01]Disabled", "*[00]Enabled"),
            List("Hardware Prefetcher", "[00]Disabled", "*[01]Enabled")); // the readable list confirms this one

        Assert.True(Rec(s, Intel, "Three Strike Counter")!.Optional);
        Assert.False(Rec(s, Intel, "Hardware Prefetcher")!.Optional);
    }

    [Fact]
    public void EachVendorHasItsOwnList()
    {
        var s = Script(
            List("EIST", "*[01]Enabled", "[00]Disabled"),
            List("Global C-State Control", "[00]Disabled", "*[0F]Auto"),
            "Port 60/64 Emulation\nValue\t=1\t// Enabled = 1, Disabled = 0");

        Assert.Equal(new[] { "EIST", "Port 60/64 Emulation" }, BiosRecommended.For(s, Intel).Keys.Select(k => s.Find(k)!.Name));
        Assert.Equal(new[] { "Global C-State Control", "Port 60/64 Emulation" }, BiosRecommended.For(s, Zen3).Keys.Select(k => s.Find(k)!.Name));
        // Where the lists disagree the readable one wins: Intel keeps it on, AMD turns it off.
        Assert.Equal("1", Rec(s, Intel, "Port 60/64 Emulation")!.Value);
        Assert.Equal("0", Rec(s, Zen3, "Port 60/64 Emulation")!.Value);
        Assert.Empty(BiosRecommended.For(s, new BiosCpu("VMwareVMware", 6, 0)));
    }

    [Fact]
    public void NewerRyzenKeepsCStatesOnAuto()
    {
        var s = Script(List("Global C-state Control", "[00]Disabled", "[01]Enabled", "*[0F]Auto"));

        foreach (var old in new[] { Zen2, Zen3 })
        {
            var r = Rec(s, old, "Global C-state Control")!;
            Assert.Equal(("00", false), (r.Value, r.NewerRyzen));
        }
        foreach (var newer in new[] { Zen4, Zen5 })
        {
            var r = Rec(s, newer, "Global C-state Control")!;
            Assert.Equal(("0F", true, "Auto"), (r.Value, r.NewerRyzen, r.Label));
        }
    }

    [Theory]
    [InlineData(0x17, 0x71, false)] // Zen 2
    [InlineData(0x19, 0x21, false)] // Zen 3 (Vermeer)
    [InlineData(0x19, 0x44, false)] // Zen 3 (Rembrandt)
    [InlineData(0x19, 0x50, false)] // Zen 3 (Cezanne)
    [InlineData(0x19, 0x61, true)]  // Zen 4 (Raphael)
    [InlineData(0x19, 0x74, true)]  // Zen 4 (Phoenix)
    [InlineData(0x19, 0x11, true)]  // Zen 4 (Genoa)
    [InlineData(0x1A, 0x44, true)]  // Zen 5
    public void KnowsWhichRyzenIsNewer(int family, int model, bool newer) =>
        Assert.Equal(newer, new BiosCpu("AuthenticAMD", family, model).IsNewerRyzen);

    [Fact]
    public void AnIntelCpuIsNeverANewerRyzen() => Assert.False(new BiosCpu("GenuineIntel", 0x1A, 0x44).IsNewerRyzen);
}
