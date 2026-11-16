using KosToolbox.Core.Bios;
using KosToolbox.Core.Execution;
using Xunit;

namespace KosToolbox.Core.Tests;

public class BiosTests
{
    // Real export from SCEWIN 5.05 on an X99 (AMI 5.11): the header has lines ending in \r\r\n.
    const string RealExport =
        "// Script File Name : nvram.txt\r\n// Created on 10/23/26 at 15:47:28    \r\n// AMISCE Utility. Ver 5.05.01.0002\r\n" +
        "// Copyright (c) 2021 AMI. All rights reserved.\r\r\nHIICrc32= 80CAF3B2\r\r\n\r\n" +
        "Setup Question\t= System Language\r\nHelp String\t= Choose the system default language\r\nToken\t=07\t// Do NOT change this line\r\n" +
        "Offset\t=00\r\nWidth\t=02\r\nOptions\t=*[00]en-US\r\n\r\n" +
        "Setup Question\t= Setup Prompt Timeout\r\nHelp String\t= Number of seconds to wait for setup activation key. 65535(0xFFFF) means indefinite waiting.\r\n" +
        "Token\t=AE\t// Do NOT change this line\r\nOffset\t=00\r\nWidth\t=02\r\nValue\t=<1>\r\n";

    // The other formats SCEWIN writes: /v mode comments, a commented-out duplicate, a blank line in the middle of the
    // block, negative number, checkbox, text and time (display only).
    static readonly string Kinds = string.Join("\r\n", new[]
    {
        "// Script File Name : nvram.txt", "// AMISCE Utility. Ver 5.05.01.0002", "HIICrc32= 1A2B3C4D", "",
        "// FORM SET", "// GUID\t= ec87d643-eba4-4bb5-a1e5-3f3e36b20da9", "// Title\t= Advanced", "",
        "Setup Question\t= Above 4G Decoding", "Help String\t= Enables 64bit decoding.", "Token\t=5D\t// Do NOT change this line",
        "Offset\t=CA", "Width\t=01", "BIOS Default\t=[00]Disabled", "Options\t=[00]Disabled\t// Move \"*\" to the desired Option", "         *[01]Enabled", "",
        "Setup Question\t= Package C State", "Help String\t= Package C State limit", "Token\t=1F\t// Do NOT change this line",
        "Offset\t=D9", "Width\t=01", "BIOS Default\t=[FF]Auto", "Options\t=*[00]C0/C1\t// Move \"*\" to the desired Option", "         [01]C2", "         [FF]Auto", "",
        "Setup Question\t= Power Limit 1", "Help String\t= PL1 in watts", "Token\t=2D\t// Do NOT change this line",
        "Offset\t=A1", "Width\t=02", "BIOS Default\t=<0> ", "Value\t=<125>", "",
        "Setup Question\t= Core Voltage Offset", "Help String\t= Offset in mV", "Token\t=2E\t// Do NOT change this line",
        "Offset\t=A3", "Width\t=01", "BIOS Default\t=<0> ", "Value\t=<-50>", "",
        "Setup Question\t= Hot-Plug Support", "Help String\t= ", "Token\t=7008\t// Do NOT change this line",
        "Offset\t=02", "Width\t=01", "BIOS Default\t=1 ", "MFG Default\t=1 ", "Value\t=1\t// Enabled = 1, Disabled = 0", "",
        "Setup Question\t= SGX Reserved Memory Size", "Help String\t= ", "Token\t=63\t// Do NOT change this line",
        "Offset\t=798", "Width\t=04", "", "Options\t=[2000000]32MB\t// Move \"*\" to the desired Option", "         [4000000]64MB", "",
        "// Setup Question\t= Above 4G Decoding", "// Token\t=5E\t// Do NOT change this line", "// Offset\t=CB", "// Width\t=01",
        "// Options\t=*[00]Disabled\t// Move \"*\" to the desired Option", "//         [01]Enabled", "",
        "Setup Question\t= Asset Tag", "Help String\t= ", "Token\t=90\t// Do NOT change this line", "Offset\t=10", "Width\t=20",
        "Value\t={0041 0042} //Unicode, Do NOT change the format", "",
        "Setup Question\t= System Time", "Help String\t= ", "Token\t=91\t// Do NOT change this line", "Offset\t=00", "Width\t=03",
        "Time\t=15:47:28\t// HH:MM:SS", "",
    });

    [Fact]
    public void ParsesRealExport()
    {
        var s = NvramScript.Parse(RealExport);

        Assert.Equal("80CAF3B2", s.Crc);
        Assert.Equal("5.05.01.0002", s.Version);
        Assert.Equal(2, s.Questions.Count);
        var lang = s.Questions[0];
        Assert.Equal(("System Language", "Choose the system default language", "07/00/02"), (lang.Name, lang.Help, lang.Key));
        Assert.Equal(BiosKind.Options, lang.Kind);
        Assert.Equal("00", lang.Current);
        Assert.Equal("en-US", NvramScript.Describe(lang, lang.Current));
        Assert.Null(lang.Default);
        var timeout = s.Questions[1];
        Assert.Equal((BiosKind.Numeric, "1", "AE/00/02"), (timeout.Kind, timeout.Current, timeout.Key));
    }

    [Fact]
    public void ParsesEveryKind()
    {
        var s = NvramScript.Parse(Kinds);

        Assert.Equal(new[] { "Above 4G Decoding", "Package C State", "Power Limit 1", "Core Voltage Offset", "Hot-Plug Support", "SGX Reserved Memory Size", "Asset Tag", "System Time" },
            s.Questions.Select(q => q.Name)); // the commented-out duplicate is left out
        var above = s.Find("5D/CA/01")!;
        Assert.Equal((BiosKind.Options, "01", "00"), (above.Kind, above.Current, above.Default));
        Assert.Equal(new[] { "Disabled", "Enabled" }, above.Options.Select(o => o.Label)); // without the "Move *" comment
        Assert.Equal(("Auto", "C0/C1"), (NvramScript.Describe(s.Find("1F/D9/01")!, "FF"), NvramScript.Describe(s.Find("1F/D9/01")!, s.Find("1F/D9/01")!.Current)));
        Assert.Equal((BiosKind.Numeric, "125", "0"), (s.Find("2D/A1/02")!.Kind, s.Find("2D/A1/02")!.Current, s.Find("2D/A1/02")!.Default));
        Assert.Equal("-50", s.Find("2E/A3/01")!.Current);
        var hotPlug = s.Find("7008/02/01")!;
        Assert.Equal((BiosKind.Checkbox, "1", "1", null), (hotPlug.Kind, hotPlug.Current, hotPlug.Default, hotPlug.Help));
        Assert.Equal("Enabled", NvramScript.Describe(hotPlug, "1"));
        var sgx = s.Find("63/798/04")!; // blank line between Width and Options, and no option marked
        Assert.Equal((BiosKind.Options, null, 2), (sgx.Kind, sgx.Current, sgx.Options.Count));
        Assert.Equal("Not set", NvramScript.Describe(sgx, sgx.Current));
        Assert.Equal((BiosKind.ReadOnly, "{0041 0042}"), (s.Find("90/10/20")!.Kind, s.Find("90/10/20")!.Raw));
        Assert.Equal((BiosKind.ReadOnly, "15:47:28"), (s.Find("91/00/03")!.Kind, s.Find("91/00/03")!.Raw));
    }

    [Fact]
    public void ImportScriptHasHeaderAndOnlyChangedBlocks()
    {
        var s = NvramScript.Parse(Kinds);
        var script = s.ImportScript(new Dictionary<string, string>
        {
            ["5D/CA/01"] = "00", ["1F/D9/01"] = "ff", ["2D/A1/02"] = "0095", ["7008/02/01"] = "0", ["63/798/04"] = "4000000",
        });

        Assert.StartsWith("// Script File Name : nvram.txt\r\n// AMISCE Utility. Ver 5.05.01.0002\r\nHIICrc32= 1A2B3C4D\r\n\r\nSetup Question\t= Above 4G Decoding", script);
        Assert.Contains("Options\t=*[00]Disabled\t// Move \"*\" to the desired Option\r\n         [01]Enabled\r\n", script);
        Assert.Contains("Options\t=[00]C0/C1\t// Move \"*\" to the desired Option\r\n         [01]C2\r\n         *[FF]Auto\r\n", script);
        Assert.Contains("Value\t=<95>\r\n", script);
        Assert.Contains("Value\t=0\t// Enabled = 1, Disabled = 0\r\n", script);
        Assert.Contains("Width\t=04\r\n\r\nOptions\t=[2000000]32MB\t// Move \"*\" to the desired Option\r\n         *[4000000]64MB", script);
        Assert.DoesNotContain("Core Voltage Offset", script);
        Assert.DoesNotContain("Asset Tag", script);
        Assert.DoesNotContain("FORM SET", script);

        var back = NvramScript.Parse(script);
        Assert.Equal(new[] { "00", "FF", "95", "0", "4000000" }, back.Questions.Select(q => q.Current));
        Assert.Equal("1A2B3C4D", back.Crc);
    }

    [Fact]
    public void ImportScriptKeepsTheRealHeaderBytes()
    {
        var script = NvramScript.Parse(RealExport).ImportScript(new Dictionary<string, string> { ["AE/00/02"] = "5" });

        Assert.Equal(RealExport[..RealExport.IndexOf("\r\n\r\nSetup", StringComparison.Ordinal)] + "\r\n" +
            "\r\nSetup Question\t= Setup Prompt Timeout\r\nHelp String\t= Number of seconds to wait for setup activation key. 65535(0xFFFF) means indefinite waiting.\r\n" +
            "Token\t=AE\t// Do NOT change this line\r\nOffset\t=00\r\nWidth\t=02\r\nValue\t=<5>\r\n", script);
    }

    [Fact]
    public void ChecksValuesAgainstTheQuestion()
    {
        var s = NvramScript.Parse(Kinds);

        Assert.Null(NvramScript.Check(s.Find("1F/D9/01")!, "FF"));
        Assert.NotNull(NvramScript.Check(s.Find("1F/D9/01")!, "02")); // not one of the options
        Assert.Null(NvramScript.Check(s.Find("2E/A3/01")!, "-128"));
        Assert.Null(NvramScript.Check(s.Find("2E/A3/01")!, "255"));
        Assert.NotNull(NvramScript.Check(s.Find("2E/A3/01")!, "256")); // doesn't fit in 1 byte
        Assert.NotNull(NvramScript.Check(s.Find("2E/A3/01")!, "-129"));
        Assert.NotNull(NvramScript.Check(s.Find("2D/A1/02")!, "12a"));
        Assert.NotNull(NvramScript.Check(s.Find("7008/02/01")!, "2"));
        Assert.NotNull(NvramScript.Check(s.Find("90/10/20")!, "0"));
        Assert.Throws<InvalidOperationException>(() => s.ImportScript(new Dictionary<string, string> { ["1F/D9/01"] = "02" }));
        Assert.Throws<InvalidOperationException>(() => s.ImportScript(new Dictionary<string, string> { ["FF/FF/01"] = "00" }));
    }

    [Fact]
    public void ComparesEditableSettingsOnly()
    {
        var before = NvramScript.Parse(Kinds);
        var after = NvramScript.Parse(before.ImportScript(new Dictionary<string, string> { ["5D/CA/01"] = "00", ["2D/A1/02"] = "95" }));

        var diffs = NvramScript.Compare(before, after);

        // Only the two blocks are in the new script: the others show up as "no longer exist", without restoring.
        var changed = diffs.Where(d => d.Old is not null && d.New is not null).ToList();
        Assert.Equal(new[] { "Above 4G Decoding", "Power Limit 1" }, changed.Select(d => d.Name));
        Assert.All(changed, d => Assert.True(d.Restorable));
        Assert.Contains(diffs, d => d.Name == "Hot-Plug Support" && d.New is null && !d.Restorable);
        Assert.DoesNotContain(diffs, d => d.Name is "Asset Tag" or "System Time");
        Assert.Empty(NvramScript.Compare(before, NvramScript.Parse(Kinds)));
    }

    [Fact]
    public void BackupsSavedAfterAnotherChangeInTheSameBootAreMarked()
    {
        var boot = new DateTime(2026, 10, 23, 10, 0, 0, DateTimeKind.Utc);
        var first = new BiosBackupMeta { BootedAtUtc = boot, CapturedAtUtc = boot.AddMinutes(10) };
        var second = new BiosBackupMeta { BootedAtUtc = boot, CapturedAtUtc = boot.AddMinutes(20) };
        var afterRestart = new BiosBackupMeta { BootedAtUtc = boot.AddHours(1), CapturedAtUtc = boot.AddHours(2) };
        var all = new[] { first, second, afterRestart };

        Assert.False(Scewin.NotBooted(first, all));
        Assert.True(Scewin.NotBooted(second, all));
        Assert.False(Scewin.NotBooted(afterRestart, all));
    }

    [Fact]
    public void KeepsOnlyScewinWarningsAndErrors()
    {
        const string output = "\r\n------\r\n|        AMISCE Utility. Ver 5.05.01.0002        |\r\n------\r\n" +
            "WARNING: Duplicate questions found. Only one question will be enabled in the script.\r\n\r\n" +
            "Warning: Error in writing variable Setup to NVRAM\r\nSystem configuration not modified\r\nScript file imported successfully.\r\n";

        Assert.Equal(new[] { "Warning: Error in writing variable Setup to NVRAM", "System configuration not modified" }, Scewin.Messages(output));
    }

    [Fact]
    public void ElevatedBiosRequestIsValidatedStructurally()
    {
        BiosJob Job(string mode, string? crc, params (string Key, string Value)[] changes) => new()
        {
            Mode = mode, RunId = Guid.NewGuid().ToString(), Crc = crc,
            Changes = changes.Select(c => new BiosChange { Key = c.Key, Value = c.Value }).ToList(),
        };

        new ElevatedJob { Bios = Job(BiosJob.Read, null) }.Validate();
        new ElevatedJob { Bios = Job(BiosJob.Apply, "80CAF3B2", ("AE/00/02", "5"), ("5D/CA/01", "-1")) }.Validate();
        Assert.Throws<InvalidOperationException>(() => new ElevatedJob { Bios = Job(BiosJob.Read, null, ("AE/00/02", "5")) }.Validate());
        Assert.Throws<InvalidOperationException>(() => new ElevatedJob { Bios = Job(BiosJob.Apply, "80CAF3B2") }.Validate());
        Assert.Throws<InvalidOperationException>(() => new ElevatedJob { Bios = Job(BiosJob.Apply, null, ("AE/00/02", "5")) }.Validate());
        Assert.Throws<InvalidOperationException>(() => new ElevatedJob { Bios = Job(BiosJob.Apply, "80CAF3B2", ("AE/00/02\r\nSetup Question", "5")) }.Validate());
        Assert.Throws<InvalidOperationException>(() => new ElevatedJob { Bios = Job(BiosJob.Apply, "80CAF3B2", ("AE/00/02", "5>")) }.Validate());
        Assert.Throws<InvalidOperationException>(() => new ElevatedJob { Bios = Job(BiosJob.Apply, "80CAF3B2", ("AE/00/02", "5\n")) }.Validate());
        Assert.Throws<InvalidOperationException>(() => new ElevatedJob { Bios = Job(BiosJob.Apply, "80CAF3B2", ("AE/00/02", "5"), ("AE/00/02", "6")) }.Validate());
        Assert.Throws<InvalidOperationException>(() => new ElevatedJob { Bios = Job("flash", null) }.Validate());
        Assert.Throws<InvalidOperationException>(() => new ElevatedJob { Bios = new BiosJob { Mode = BiosJob.Read, RunId = "x" } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new ElevatedJob { Bios = Job(BiosJob.Read, null), PrepareLogs = true }.Validate());
    }
}
