using KosToolbox.Core.Network;
using Xunit;

namespace KosToolbox.Core.Tests;

public class NetTweaksTests
{
    [Fact]
    public void KnownKeywordsHaveTheirOptions()
    {
        var rss = Assert.Single(NetTweaks.Keywords, k => k.Keyword == "*RSS");
        Assert.Equal(new[] { "0", "1" }, rss.Options.Select(o => o.Value));
        Assert.Equal(NetTweaks.SectionRss, NetTweaks.SectionOf("*rss"));
        Assert.Equal(4, NetTweaks.Keywords.Single(k => k.Keyword == "*FlowControl").Options.Count);
    }

    [Fact]
    public void RecommendedComeFromTheKosScripts()
    {
        Assert.Equal("0", NetTweaks.Recommended("*FlowControl", "realtek"));
        Assert.Equal("1024", NetTweaks.Recommended("*ReceiveBuffers", "intel"));
        Assert.Null(NetTweaks.Recommended("*InterruptModeration", "realtek"));
        Assert.Equal("newreno", NetTweaks.GlobalRecommended["congestion"]);
        Assert.True(NetTweaks.IsDword("PnPCapabilities"));
    }

    [Theory]
    [InlineData(@"PCI\VEN_10EC&DEV_8168&SUBSYS_012310EC&REV_10\01000000684CE00000", "realtek")]
    [InlineData(@"PCI\VEN_8086&DEV_15B8&SUBSYS_86721043&REV_00\3&11583659&0&FE", "intel")]
    [InlineData(@"USB\VID_0BDA&PID_8153\000001", "other")]
    public void VendorComesFromThePciId(string instanceId, string vendor) => Assert.Equal(vendor, NetTweaks.Vendor(instanceId));

    [Fact]
    public void GlobalValuesAreValidated()
    {
        Assert.Null(NetGlobals.Problem("autotuninglevel", "disabled"));
        Assert.NotNull(NetGlobals.Problem("autotuninglevel", "fast"));
        Assert.Null(NetGlobals.Problem("initialrto", "300"));
        Assert.NotNull(NetGlobals.Problem("initialrto", "50"));
        Assert.NotNull(NetGlobals.Problem("anything", "1"));
    }
}
