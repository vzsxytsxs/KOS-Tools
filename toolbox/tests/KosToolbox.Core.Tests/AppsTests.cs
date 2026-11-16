using KosToolbox.Core.Apps;
using KosToolbox.Core.Startup;
using Xunit;

namespace KosToolbox.Core.Tests;

public class AppsTests
{
    [Fact]
    public void SplitsQuotedUninstallCommand()
    {
        var r = CommandLine.Parse("\"C:\\Program Files\\App\\unins000.exe\" /SILENT /LOG", _ => false);
        Assert.Equal((@"C:\Program Files\App\unins000.exe", "/SILENT /LOG"), r);
    }

    [Fact]
    public void SplitsUnquotedPathWithSpaces()
    {
        var r = CommandLine.Parse(@"C:\Program Files\App\uninstall.exe -x --quiet", p => p == @"C:\Program Files\App\uninstall.exe");
        Assert.Equal((@"C:\Program Files\App\uninstall.exe", "-x --quiet"), r);
    }

    [Fact]
    public void ResolvesBareNameInSystem32()
    {
        var msiexec = Path.Combine(Environment.SystemDirectory, "MsiExec.exe");
        var r = CommandLine.Parse("MsiExec.exe /I{11111111-2222-3333-4444-555555555555}", p => string.Equals(p, msiexec, StringComparison.OrdinalIgnoreCase));
        Assert.Equal((msiexec, "/I{11111111-2222-3333-4444-555555555555}"), r);
    }

    [Fact]
    public void ParsesDismFeatureTable()
    {
        const string output = "Deployment Image Servicing and Management tool\r\nVersion: 10.0\r\n\r\n------------------------------------------- | --------\r\n" +
            "Feature Name                                | State   \r\n------------------------------------------- | --------\r\n" +
            "TelnetClient                                | Disabled\r\nNetFx3                                      | Enabled \r\n" +
            "Microsoft-Windows-Subsystem-Linux           | Enable Pending\r\nSMB1Protocol                                | Disabled with Payload Removed\r\n\r\nThe operation completed successfully.\r\n";
        var list = OptionalFeatures.ParseTable(output);
        Assert.Equal(new[] { "Microsoft-Windows-Subsystem-Linux", "NetFx3", "SMB1Protocol", "TelnetClient" }, list.Select(f => f.Name));
        Assert.True(list[0].IsEnabled && list[0].IsPending);
        Assert.True(list[1].IsEnabled);
        Assert.False(list[2].IsEnabled);
    }

    [Fact]
    public void ParsesDismFeatureInfo()
    {
        const string output = "Feature Information:\r\n\r\nFeature Name : NetFx3\r\nDisplay Name : .NET Framework 3.5 (includes .NET 2.0 and 3.0)\r\n" +
            "Description : .NET Framework 3.5 (includes .NET 2.0 and 3.0)\r\nRestart Required : Possible\r\nState : Enabled\r\n";
        Assert.Equal(new OptionalFeatureInfo(".NET Framework 3.5 (includes .NET 2.0 and 3.0)", ".NET Framework 3.5 (includes .NET 2.0 and 3.0)", "Possible"),
            OptionalFeatures.ParseInfo(output));
    }

    [Theory]
    [InlineData("NetFx3 /all")]
    [InlineData("A&B")]
    [InlineData("")]
    public void FeatureNamesCantCarryExtraArguments(string name) => Assert.ThrowsAny<Exception>(() => OptionalFeatures.Validate(name));

    [Fact]
    public void MsiUsesUninstallSwitchNotTheRegisteredModifyCommand()
    {
        const string code = "{11111111-2222-3333-4444-555555555555}";
        var p = new InstalledProgram("HKLM\\" + code, "App", null, null, null, null, null, "MsiExec.exe /I" + code, code, "HKLM 64-bit", null);
        Assert.EndsWith("msiexec.exe\" /x " + code, p.UninstallCommand);
        var exe = new InstalledProgram("HKCU\\App", "App", null, null, null, null, null, "\"C:\\A\\u.exe\"", null, "HKCU", null);
        Assert.Equal("\"C:\\A\\u.exe\"", exe.UninstallCommand);
    }
}
