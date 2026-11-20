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
}
