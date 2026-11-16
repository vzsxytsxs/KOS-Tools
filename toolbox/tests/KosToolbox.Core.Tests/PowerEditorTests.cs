using KosToolbox.Core.Execution;
using KosToolbox.Core.Power;
using Xunit;

namespace KosToolbox.Core.Tests;

/// <summary>Pure plan-editor logic: nothing here reads or writes plans, the Registry or a driver.</summary>
public class PowerEditorTests
{
    [Theory]
    [InlineData("0", 0u)]
    [InlineData(" 42 ", 42u)]
    [InlineData("0x1F", 31u)]
    [InlineData("0XffFFffFF", uint.MaxValue)]
    [InlineData("4294967295", uint.MaxValue)]
    public void ParsesDecimalAndHex(string text, uint expected)
    {
        Assert.True(PowerPlans.TryParseValue(text, out var v));
        Assert.Equal(expected, v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0x")]
    [InlineData("-1")]
    [InlineData("+5")]
    [InlineData("4294967296")]
    [InlineData("0x100000000")]
    [InlineData("1.5")]
    [InlineData("0xG1")]
    public void RejectsInvalidValues(string text) => Assert.False(PowerPlans.TryParseValue(text, out _));

    [Fact]
    public void CustomValueBypassesOnlyTheList()
    {
        var choice = new PowerSettingInfo { Subgroup = Guid.Empty, Setting = Guid.Empty, Name = "c", Kind = PowerValueKind.Choice,
            Choices = new[] { new PowerChoice(0, "Off"), new PowerChoice(1, "On") } };
        Assert.NotNull(PowerPlans.Validate(choice, 7));
        Assert.Null(PowerPlans.Validate(choice, 7, custom: true));
        var range = new PowerSettingInfo { Subgroup = Guid.Empty, Setting = Guid.Empty, Name = "r", Kind = PowerValueKind.Range, Min = 0, Max = 100, Increment = 1 };
        Assert.NotNull(PowerPlans.Validate(range, 101, custom: true)); // range is still validated
        Assert.NotNull(PowerPlans.Validate(new PowerSettingInfo { Subgroup = Guid.Empty, Setting = Guid.Empty, Name = "x", Kind = PowerValueKind.ReadOnly }, 0, custom: true));
    }

    [Fact]
    public void NamesAndDescriptions()
    {
        Assert.Null(PowerPlans.NameProblem("KOS Ultimate"));
        Assert.NotNull(PowerPlans.NameProblem(""));
        Assert.NotNull(PowerPlans.NameProblem(" padded"));
        Assert.NotNull(PowerPlans.NameProblem("line\nbreak"));
        Assert.NotNull(PowerPlans.NameProblem(new string('a', PowerPlans.MaxNameLength + 1)));
        Assert.NotNull(PowerPlans.NameProblem("@Work"));          // would be read as a MUI resource
        Assert.Null(PowerPlans.NameProblem("Work @home"));
        Assert.Null(PowerPlans.DescriptionProblem(""));
        Assert.NotNull(PowerPlans.DescriptionProblem("@%SystemRoot%\\system32\\powrprof.dll,-1"));
        Assert.NotNull(PowerPlans.DescriptionProblem(null));
        Assert.NotNull(PowerPlans.DescriptionProblem(new string('a', PowerPlans.MaxDescriptionLength + 1)));
    }

    [Fact]
    public void PowFilePaths()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kos-pow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "plan.pow");
            Assert.Null(PowerPlans.PowFileProblem(file, mustExist: false));
            Assert.NotNull(PowerPlans.PowFileProblem(file, mustExist: true));   // doesn't exist yet
            File.WriteAllBytes(file, new byte[] { 1 });
            Assert.Null(PowerPlans.PowFileProblem(file, mustExist: true));
            Assert.NotNull(PowerPlans.PowFileProblem("plan.pow", mustExist: false));                       // relative
            Assert.NotNull(PowerPlans.PowFileProblem(Path.Combine(dir, "plan.reg"), mustExist: false));    // extension
            Assert.NotNull(PowerPlans.PowFileProblem(Path.Combine(dir, "nope", "plan.pow"), mustExist: false)); // nonexistent folder
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void PlanRequestShape()
    {
        var plan = Guid.NewGuid();
        PowerPlans.CheckShape(new PowerPlanOp { Op = PowerPlanOp.Duplicate, Scheme = plan, NewScheme = Guid.NewGuid(), Name = "Copy" });
        PowerPlans.CheckShape(new PowerPlanOp { Op = PowerPlanOp.Rename, Scheme = plan, Name = "New", Description = "" });
        PowerPlans.CheckShape(new PowerPlanOp { Op = PowerPlanOp.Rename, Scheme = plan, Name = "New" });           // only the name changes
        PowerPlans.CheckShape(new PowerPlanOp { Op = PowerPlanOp.Rename, Scheme = plan, Description = "Text" });   // only the description changes
        PowerPlans.CheckShape(new PowerPlanOp { Op = PowerPlanOp.Delete, Scheme = plan });
        PowerPlans.CheckShape(new PowerPlanOp { Op = PowerPlanOp.RestoreDefaults });
        PowerPlans.CheckShape(new PowerPlanOp { Op = PowerPlanOp.RemoveValue, Scheme = plan, Subgroup = Guid.NewGuid(), Setting = Guid.NewGuid(), Ac = true });
        PowerPlans.CheckShape(new PowerPlanOp { Op = PowerPlanOp.SetHidden, Subgroup = Guid.NewGuid(), Setting = Guid.NewGuid(), Hidden = true });

        Assert.ThrowsAny<InvalidOperationException>(() => PowerPlans.CheckShape(new PowerPlanOp { Op = "format" }));
        Assert.ThrowsAny<InvalidOperationException>(() => PowerPlans.CheckShape(new PowerPlanOp { Op = PowerPlanOp.Duplicate, Scheme = plan, Name = "Copy" })); // no new GUID
        Assert.ThrowsAny<InvalidOperationException>(() => PowerPlans.CheckShape(new PowerPlanOp { Op = PowerPlanOp.Delete, Scheme = plan, Name = "x" }));    // extra field
        Assert.ThrowsAny<InvalidOperationException>(() => PowerPlans.CheckShape(new PowerPlanOp { Op = PowerPlanOp.Rename, Scheme = plan }));                // nothing changes
        Assert.ThrowsAny<InvalidOperationException>(() => PowerPlans.CheckShape(new PowerPlanOp { Op = PowerPlanOp.Rename, Scheme = plan, Name = "@x" }));
        Assert.ThrowsAny<InvalidOperationException>(() => PowerPlans.CheckShape(new PowerPlanOp { Op = PowerPlanOp.Duplicate, Scheme = plan, NewScheme = Guid.NewGuid() })); // copy without a name
        Assert.ThrowsAny<InvalidOperationException>(() => PowerPlans.CheckShape(new PowerPlanOp { Op = PowerPlanOp.RestoreDefaults, Scheme = plan }));
        Assert.ThrowsAny<InvalidOperationException>(() => PowerPlans.CheckShape(new PowerPlanOp { Op = PowerPlanOp.Export, Scheme = plan, Path = "rel.pow" }));
        Assert.ThrowsAny<InvalidOperationException>(() => PowerPlans.CheckShape(new PowerPlanOp { Op = PowerPlanOp.Import, NewScheme = Guid.NewGuid(), Path = @"C:\nope\missing.pow" }));
        Assert.ThrowsAny<InvalidOperationException>(() => PowerPlans.CheckShape(new PowerPlanOp { Op = PowerPlanOp.SetHidden, Scheme = plan, Subgroup = Guid.NewGuid(), Setting = Guid.NewGuid() }));
        Assert.ThrowsAny<InvalidOperationException>(() => PowerPlans.CheckShape(new PowerPlanOp { Op = PowerPlanOp.Delete, Scheme = plan, Hidden = true }));
    }

    [Theory]
    [InlineData(@"\\server\share\plan.pow", true)]
    [InlineData(@"\\?\C:\Windows\System32\x.pow", true)]
    [InlineData(@"\\.\C:\x.pow", true)]
    [InlineData("//server/share/plan.pow", true)]
    [InlineData(@"C:\Users\me\plan.pow", false)]
    [InlineData(@"\plan.pow", false)]
    public void NetworkAndDevicePaths(string path, bool expected) => Assert.Equal(expected, PowerPlans.IsNetworkOrDevicePath(path));

    [Fact]
    public void ElevatedPlanRequestIsExclusive()
    {
        var op = new PowerPlanOp { Op = PowerPlanOp.Delete, Scheme = Guid.NewGuid() };
        Assert.ThrowsAny<InvalidOperationException>(() => new ElevatedJob { PowerPlan = op, Power = { new PowerWrite() } }.Validate());
        Assert.ThrowsAny<InvalidOperationException>(() => new ElevatedJob { PowerPlan = new PowerPlanOp { Op = "format" } }.Validate());
    }
}
