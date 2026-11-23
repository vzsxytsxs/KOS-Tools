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
    public void TaskbarStyleReadsOnlyKnownValuesAndSwapsTheTintToAbgr()
    {
        const uint blue = 0xFFD47800; // Windows' default accent, ABGR
        Assert.Null(LookFor(null, null, blue, true));
        Assert.Null(LookFor(0, "#80000000", blue, true));
        Assert.Null(LookFor(4, null, blue, true));
        Assert.Null(LookFor("1", null, blue, true)); // a string isn't the style
        Assert.Equal(new Look(2, 2, 0, "Transparent"), LookFor(1, null, blue, true));
        Assert.Equal(new Look(2, 2, 0x800000FF, "Transparent"), LookFor(1, "#80FF0000", blue, false)); // red: R and B swapped
        Assert.Equal(new Look(4, 1, 0xC0000000, "Blur"), LookFor(2, "#C0000000", blue, true));
        Assert.Equal(new Look(4, 1, 0x80D47800, "Blur"), LookFor(2, "accent", blue, true)); // the accent at half strength
        Assert.Equal(new Look(4, 2, 0xFF202020, "Acrylic"), LookFor(3, null, blue, true)); // acrylic needs a tint: dark gray
        foreach (var bad in new object[] { "80000000", "#8000000", "#GG000000", "#800000000", "Accent", 5 })
            Assert.Equal(0u, LookFor(2, bad, blue, true)!.Color); // no tint
        // Without Transparency effects DWM draws blur and acrylic black: transparent with the tint meanwhile.
        var off = LookFor(3, "#80000000", blue, false)!;
        Assert.Equal((2, 2, 0x80000000u), (off.State, off.Flags, off.Color));
        Assert.StartsWith("Acrylic needs Transparency effects", off.Name);
    }

    [Fact]
    public void TheDllsLookCarriesTheButtonColorAndTheOverflowTintAsArgb()
    {
        const uint blue = 0xFFD47800; // ABGR: RGB 00 78 D4
        var transparent = LookFor(1, "#80FF0000", blue, true)!;
        Assert.Equal([1u, 0, 1, 0x80FF0000], PartsFor(transparent, null, blue)); // the flyout gets the tint back as ARGB
        Assert.Equal([1u, 0xFFFFFFFF, 1, 0], PartsFor(LookFor(1, null, blue, true)!, 1, blue));
        Assert.Equal([1u, 0xFF0078D4, 0, 0], PartsFor(LookFor(2, "#80000000", blue, true)!, 2, blue)); // blur: Windows' own flyout
        Assert.Equal(0u, PartsFor(transparent, 3, blue)[1]);
        Assert.Equal(0u, PartsFor(transparent, "1", blue)[1]); // a string isn't the setting
        Assert.Equal(1u, PartsFor(LookFor(3, null, blue, false)!, null, blue)[2]); // acrylic without Transparency effects is transparent
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
        Assert.False(DarkApps.Wants(wanted, @"C:\Windows\System32\charmap.exe", "#32770", Never, @"C:\Windows")); // DarkTools' (color hooks)
        var tools = DarkApps.Paths(n => n == "DarkTools" ? 1 : null, @"C:\Windows");
        Assert.True(DarkApps.Wants(tools, @"C:\Windows\System32\charmap.exe", "#32770", Never, @"C:\Windows"));
        Assert.False(DarkApps.Wants(tools, @"C:\Windows\System32\msconfig.exe", "#32770", Never, @"C:\Windows")); // dark without them
        Assert.False(DarkApps.Wants(tools, @"C:\Windows\explorer.exe", "#32770", Never, @"C:\Windows"));
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
    public void TheMenuIconsWaitForAMenuInExplorerOnly()
    {
        var wanted = DarkApps.Paths(DarkApps.MenuApps, n => n == "MenuIcons" ? 1 : null, @"C:\Windows");
        Assert.Equal([@"C:\Windows\explorer.exe"], wanted);
        Assert.Empty(DarkApps.Paths(DarkApps.MenuApps, n => n == "NoBlankFolders" ? 1 : null, @"C:\Windows"));
        string? Never() => throw new InvalidOperationException("command line read");
        // Started when Explorer shows its taskbar, before any menu; other Explorer windows wait for that one.
        Assert.True(DarkApps.Wants(wanted, @"C:\Windows\explorer.exe", "Shell_TrayWnd", Never, @"C:\Windows", "Shell_TrayWnd"));
        Assert.Null(DarkApps.Wants(wanted, @"C:\Windows\explorer.exe", "#32768", Never, @"C:\Windows", "Shell_TrayWnd"));
        Assert.Null(DarkApps.Wants(wanted, @"C:\Windows\explorer.exe", "CabinetWClass", Never, @"C:\Windows", "Shell_TrayWnd"));
        Assert.False(DarkApps.Wants(wanted, @"C:\Windows\notepad.exe", "Shell_TrayWnd", Never, @"C:\Windows", "Shell_TrayWnd")); // another app
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
