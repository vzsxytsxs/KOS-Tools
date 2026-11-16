using KosToolbox.Core.Apps;
using Xunit;

namespace KosToolbox.Core.Tests;

public class SilentUninstallTests
{
    [Fact]
    public void RecognizesInnoAndNsisUninstallers()
    {
        byte[] Nothing() => new byte[64];
        Assert.Equal("Inno Setup", InstalledPrograms.UninstallerKind(@"C:\Program Files\App\unins000.exe", () => throw new InvalidOperationException("not read")));
        Assert.Equal("NSIS", InstalledPrograms.UninstallerKind(@"C:\App\uninst.exe", () => System.Text.Encoding.ASCII.GetBytes("MZ....NullsoftInst....")));
        Assert.Null(InstalledPrograms.UninstallerKind(@"C:\App\uninstall.exe", Nothing));
        Assert.Null(InstalledPrograms.UninstallerKind(@"C:\App\unins00.exe", Nothing));
    }

    [Fact]
    public void MsiAndQuietCommandsComeFirst()
    {
        var msi = new InstalledProgram("id", "App", null, null, null, null, null, "MsiExec.exe /I{X}", "{11111111-1111-1111-1111-111111111111}", "HKLM 64-bit", null);
        var s = InstalledPrograms.Silent(msi);
        Assert.Equal("MSI", s?.Kind);
        Assert.Contains("/x {11111111-1111-1111-1111-111111111111} /qn", s?.Args);
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var quiet = new InstalledProgram("id", "App", null, null, null, null, null, $"\"{cmd}\" /c", null, "HKLM 64-bit", null) { QuietUninstallString = $"\"{cmd}\" /c exit" };
        Assert.Equal(("quiet command", "/c exit"), (InstalledPrograms.Silent(quiet)?.Kind, InstalledPrograms.Silent(quiet)?.Args));
        Assert.Null(InstalledPrograms.Silent(new InstalledProgram("id", "App", null, null, null, null, null, $"\"{cmd}\" /c", null, "HKLM 64-bit", null)));
        // A user's own entry never runs silently (MSI or quiet command), nor an uninstaller outside the admin-only folders.
        Assert.Null(InstalledPrograms.Silent(msi with { Source = "HKCU" }));
        Assert.Null(InstalledPrograms.Silent(quiet with { Source = "HKCU" }));
        var temp = Path.Combine(Path.GetTempPath(), "unins000.exe");
        Assert.False(InstalledPrograms.AdminFolder(temp));
        Assert.True(InstalledPrograms.AdminFolder(cmd));
    }

    /// <summary>Which programs on this PC would uninstall silently (read-only).</summary>
    [Fact]
    public void ClassifiesThisPc()
    {
        var all = InstalledPrograms.List();
        if (Environment.GetEnvironmentVariable("KOS_TEST_REPORT") is null) return;
        foreach (var p in all) Console.WriteLine($"{p.Name}: {InstalledPrograms.Silent(p)?.Kind ?? "interactive"}");
    }
}
