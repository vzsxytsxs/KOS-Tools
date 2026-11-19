using System.Net;
using KosGovernor.Core;
using Xunit;

namespace KosGovernor.Core.Tests;

public class FirewallWatchTests
{
    static readonly DateTime T0 = new(2026, 11, 19, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AProgramIsAskedOnceUntilAnsweredOrTheCooldownPasses()
    {
        var asks = new FirewallAsks();
        Assert.True(asks.ShouldAsk(@"C:\Games\game.exe", T0));
        Assert.False(asks.ShouldAsk(@"c:\games\GAME.exe", T0.AddSeconds(1))); // its popup is open: retries don't stack
        Assert.True(asks.ShouldAsk(@"C:\Other\app.exe", T0)); // another program is asked separately

        asks.Dismissed(@"C:\Games\game.exe", T0.AddSeconds(5)); // closed without an answer: stays blocked
        Assert.False(asks.ShouldAsk(@"C:\Games\game.exe", T0.AddSeconds(30)));
        Assert.True(asks.ShouldAsk(@"C:\Games\game.exe", T0.AddSeconds(5) + FirewallAsks.Cooldown)); // asked again later

        asks.Answered(@"C:\Games\game.exe");
        Assert.True(asks.ShouldAsk(@"C:\Games\game.exe", T0.AddSeconds(100))); // a rule decides; if it's removed, asked again
    }

    [Fact]
    public void PopupClicksCountOnlyWhenPressedAndReleasedOnTheSameButtonAfterTheGuard()
    {
        const int Allow = 0, Block = 1;
        Assert.Equal(-1, FirewallPopup.Pressed(Allow, 0)); // the question just changed: a click already on its way doesn't answer it
        Assert.Equal(-1, FirewallPopup.Pressed(Allow, FirewallPopup.ClickGuardMs - 1));
        Assert.Equal(Allow, FirewallPopup.Pressed(Allow, FirewallPopup.ClickGuardMs));
        Assert.Equal(Allow, FirewallPopup.Released(Allow, Allow));
        Assert.Equal(-1, FirewallPopup.Released(Allow, Block)); // pressed on one, released on the other
        Assert.Equal(-1, FirewallPopup.Released(-1, Allow)); // a release with no press (or one inside the guard)
        Assert.Equal(-1, FirewallPopup.Released(Allow, -1)); // dragged off the button
    }

    [Fact]
    public void PopupNamesTheTarget()
    {
        Assert.Equal("1.1.1.1:443 (TCP)", FirewallPopup.Target(new FirewallAsk(@"C:\a.exe", "A", IPAddress.Parse("1.1.1.1"), 443, 6)));
        Assert.Equal("[2606:4700::1111]:53 (UDP)", FirewallPopup.Target(new FirewallAsk(@"C:\a.exe", "A", IPAddress.Parse("2606:4700::1111"), 53, 17)));
        Assert.Equal("8.8.8.8 (ICMP)", FirewallPopup.Target(new FirewallAsk(@"C:\a.exe", "A", IPAddress.Parse("8.8.8.8"), 0, 1)));
        Assert.Equal("an unknown address", FirewallPopup.Target(new FirewallAsk(@"C:\a.exe", "A", null, 443, 0)));
    }

    [Fact]
    public void StatusLineShowsTheFirewall()
    {
        string State(EngineFacts f) => EngineStatus.Build(f, new TaskMeter.Sample(0, 0, new TaskMeter.Usage[Enum.GetValues<EngineTask>().Length]))
            .Lines.Single(l => l.Name == "KOS Firewall").State;
        Assert.Equal("Off", State(new EngineFacts()));
        Assert.Equal("98 filters · 12 blocked", State(new EngineFacts { FirewallOn = true, FirewallFilters = 98, FirewallBlocked = 12, FirewallPopups = true }));
        Assert.Equal("98 filters · 0 blocked · no popups", State(new EngineFacts { FirewallOn = true, FirewallFilters = 98 }));
        Assert.StartsWith("On, but no filters", State(new EngineFacts { FirewallOn = true }));
    }
}
