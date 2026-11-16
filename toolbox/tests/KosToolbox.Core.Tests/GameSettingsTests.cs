using KosToolbox.Core.Settings;
using Xunit;

namespace KosToolbox.Core.Tests;

public class GameSettingsTests
{
    [Fact]
    public void FullscreenFlagsKeepTheOtherCompatibilityFlags()
    {
        Assert.Equal("~ DISABLEDXMAXIMIZEDWINDOWEDMODE HIGHDPIAWARE", GameSettings.Layers(null, on: true));
        Assert.Equal("~ RUNASADMIN DISABLEDXMAXIMIZEDWINDOWEDMODE HIGHDPIAWARE", GameSettings.Layers("~ RUNASADMIN", on: true));
        Assert.Equal("~ RUNASADMIN", GameSettings.Layers("~ RUNASADMIN DISABLEDXMAXIMIZEDWINDOWEDMODE HIGHDPIAWARE", on: false));
        Assert.Null(GameSettings.Layers("~ DISABLEDXMAXIMIZEDWINDOWEDMODE HIGHDPIAWARE", on: false));
        Assert.Equal("~ DISABLEDXMAXIMIZEDWINDOWEDMODE HIGHDPIAWARE", GameSettings.Layers("~ DISABLEDXMAXIMIZEDWINDOWEDMODE HIGHDPIAWARE", on: true)); // no duplicates
        Assert.True(GameSettings.HasFullscreenFlags("~ HIGHDPIAWARE DISABLEDXMAXIMIZEDWINDOWEDMODE"));
        Assert.False(GameSettings.HasFullscreenFlags("~ HIGHDPIAWARE"));
    }

    [Fact]
    public void QosPoliciesMatchByPathOrByFileName()
    {
        Assert.True(GameSettings.Matches(@"C:\Games\Valorant\VALORANT.exe", @"c:\games\valorant\valorant.exe"));
        Assert.True(GameSettings.Matches("VALORANT.exe", @"C:\Games\Valorant\VALORANT.exe"));
        Assert.False(GameSettings.Matches(@"D:\Other\VALORANT.exe", @"C:\Games\Valorant\VALORANT.exe"));
        Assert.False(GameSettings.Matches("cs2.exe", @"C:\Games\Valorant\VALORANT.exe"));
    }

    [Fact]
    public void ReadsThisPcsList()
    {
        // Read-only.
        Assert.All(GameSettings.List(), s => Assert.True(s.Dscp || s.FullscreenOptimizationsOff || s.HighPerformanceGpu));
    }
}
