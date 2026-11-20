using KosGovernor.Core;
using Xunit;
using static KosGovernor.Core.ShellHost;

namespace KosGovernor.Core.Tests;

public class ShellHostTests
{
    static readonly DateTime Loaded = new(2026, 11, 20, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ExplorerEndingRightAfterTheDllLoadsTripsTheGuardForGood()
    {
        var g = new GuardState(4242, Loaded, Crashed: false);
        Assert.Equal(GuardVerdict.Wait, Judge(g, alive: true, Loaded.AddSeconds(10)));   // still in the window
        Assert.Equal(GuardVerdict.Trip, Judge(g, alive: false, Loaded.AddSeconds(10)));  // ended inside it
        Assert.Equal(GuardVerdict.Clear, Judge(g, alive: true, Loaded.AddMinutes(1)));   // outlived it
        Assert.Equal(GuardVerdict.Clear, Judge(g, alive: false, Loaded.AddMinutes(1)));  // ended later: not KOS Shell's doing
        Assert.Equal(GuardVerdict.Tripped, Judge(g with { Crashed = true }, alive: true, Loaded.AddDays(3))); // until turned off and on
    }

    [Fact]
    public void DarkModeWantsOnlyTheAppsWhoseSwitchIsADwordOne()
    {
        var values = new Dictionary<string, object?> { ["DarkRegedit"] = 1, ["DarkConsoles"] = 0 };
        Assert.Equal([@"C:\Windows\regedit.exe"], DarkApps.Paths(n => values.GetValueOrDefault(n), @"C:\Windows"));
        values["DarkConsoles"] = 1;
        Assert.Equal([@"C:\Windows\regedit.exe", @"C:\Windows\System32\mmc.exe"], DarkApps.Paths(n => values.GetValueOrDefault(n), @"C:\Windows"));
        values["DarkRegedit"] = "1"; // a string isn't the switch
        Assert.Equal([@"C:\Windows\System32\mmc.exe"], DarkApps.Paths(n => values.GetValueOrDefault(n), @"C:\Windows"));
        Assert.Empty(DarkApps.Paths(_ => null, @"C:\Windows"));
        values["DarkDialogs"] = 1;
        Assert.Contains(@"C:\Windows\System32\SystemProperties*.exe", DarkApps.Paths(n => values.GetValueOrDefault(n), @"C:\Windows"));
    }

    [Fact]
    public void DarkModeMatchesSystemPropertiesOnlyInSystem32()
    {
        const string wanted = @"C:\Windows\System32\SystemProperties*.exe";
        Assert.True(DarkApps.Matches(wanted, @"C:\WINDOWS\system32\SystemPropertiesAdvanced.exe"));
        Assert.False(DarkApps.Matches(wanted, @"C:\Windows\System32\SystemProperties.exe")); // no name after the prefix
        Assert.False(DarkApps.Matches(wanted, @"C:\Windows\System32\SystemPropertiesX\a.exe"));
        Assert.False(DarkApps.Matches(wanted, @"C:\Windows\System32\SystemPropertiesAdvanced.dll"));
        Assert.True(DarkApps.Matches(@"C:\Windows\regedit.exe", @"c:\windows\REGEDIT.EXE"));
        Assert.False(DarkApps.Matches(@"C:\Windows\regedit.exe", @"C:\Windows\regedit.exe.exe"));
    }

    [Fact]
    public void DarkDialogsWaitForAnExplorerDialogAndAnAppletInRundll32()
    {
        var wanted = DarkApps.Paths(n => n == "DarkDialogs" ? 1 : null, @"C:\Windows");
        string? Never() => throw new InvalidOperationException("command line read");
        Assert.Null(DarkApps.Wants(wanted, @"C:\Windows\explorer.exe", "CabinetWClass", Never, @"C:\Windows"));
        Assert.True(DarkApps.Wants(wanted, @"C:\Windows\explorer.exe", "#32770", Never, @"C:\Windows"));
        Assert.True(DarkApps.Wants(wanted, @"C:\Windows\System32\rundll32.exe", "#32770",
            () => @"""C:\Windows\system32\rundll32.exe"" Shell32.dll,Control_RunDLL mmsys.cpl", @"C:\Windows"));
        Assert.False(DarkApps.Wants(wanted, @"C:\Windows\System32\rundll32.exe", "#32770", () => "rundll32 other.dll,Entry", @"C:\Windows"));
        Assert.False(DarkApps.Wants(wanted, @"C:\Windows\System32\rundll32.exe", "#32770", () => null, @"C:\Windows"));
        Assert.True(DarkApps.Wants(wanted, @"C:\Windows\System32\SystemPropertiesRemote.exe", "#32770", Never, @"C:\Windows"));
        Assert.True(DarkApps.Wants(wanted, @"C:\Windows\System32\msconfig.exe", "#32770", Never, @"C:\Windows"));
        Assert.True(DarkApps.Wants(wanted, @"C:\Windows\System32\colorcpl.exe", "#32770", Never, @"C:\Windows"));
        Assert.False(DarkApps.Wants(wanted, @"C:\Windows\System32\cleanmgr.exe", "#32770", Never, @"C:\Windows")); // its drive list stays white
        Assert.True(DarkApps.Wants(wanted, @"C:\Windows\System32\odbcad32.exe", "#32770", Never, @"C:\Windows"));
        Assert.False(DarkApps.Wants(wanted, @"C:\Windows\System32\charmap.exe", "#32770", Never, @"C:\Windows")); // its grid stays white
        Assert.False(DarkApps.Wants(wanted, @"C:\Windows\SysWOW64\msconfig.exe", "#32770", Never, @"C:\Windows"));
        Assert.False(DarkApps.Wants(wanted, @"C:\Windows\System32\notepad.exe", "Notepad", Never, @"C:\Windows")); // its own switch
        Assert.False(DarkApps.Wants(wanted, @"C:\Games\game.exe", "#32770", Never, @"C:\Windows"));
    }

    [Fact]
    public void TheFolderFixWaitsForAFolderWindowInExplorerOnly()
    {
        var wanted = DarkApps.Paths(DarkApps.FolderApps, n => n == "NoBlankFolders" ? 1 : null, @"C:\Windows");
        Assert.Equal([@"C:\Windows\explorer.exe"], wanted);
        Assert.Empty(DarkApps.Paths(DarkApps.FolderApps, n => n == "DarkDialogs" ? 1 : null, @"C:\Windows"));
        string? Never() => throw new InvalidOperationException("command line read");
        Assert.Null(DarkApps.Wants(wanted, @"C:\Windows\explorer.exe", "#32770", Never, @"C:\Windows", "CabinetWClass"));
        Assert.Null(DarkApps.Wants(wanted, @"C:\Windows\explorer.exe", "Shell_TrayWnd", Never, @"C:\Windows", "CabinetWClass"));
        Assert.True(DarkApps.Wants(wanted, @"C:\Windows\explorer.exe", "CabinetWClass", Never, @"C:\Windows", "CabinetWClass"));
        Assert.False(DarkApps.Wants(wanted, @"C:\Windows\System32\rundll32.exe", "CabinetWClass", Never, @"C:\Windows", "CabinetWClass"));
        Assert.False(DarkApps.Wants(wanted, @"C:\Tools\explorer.exe", "CabinetWClass", Never, @"C:\Windows", "CabinetWClass"));
    }

    [Fact]
    public void DarkModeReadsAPeHeadersLinkTimeAndSize()
    {
        var header = new byte[0x200];
        header[0] = (byte)'M'; header[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(header, 0x3C);
        BitConverter.GetBytes(0x4550u).CopyTo(header, 0x80);
        BitConverter.GetBytes(0x6560AB12u).CopyTo(header, 0x88);
        BitConverter.GetBytes(0x32000u).CopyTo(header, 0xD0);
        Assert.Equal((0x6560AB12u, 0x32000u), DarkApps.Stamp(header));
        header[0x80] = 0; // not "PE"
        Assert.Null(DarkApps.Stamp(header));
        BitConverter.GetBytes(0x1F0).CopyTo(header, 0x3C); // past the bytes read
        Assert.Null(DarkApps.Stamp(header));
        BitConverter.GetBytes(0x7FFFFFFF).CopyTo(header, 0x3C); // would overflow an offset check
        Assert.Null(DarkApps.Stamp(header));
    }
}
