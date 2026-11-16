using KosToolbox.Core.Execution;
using KosToolbox.Core.Network;
using Xunit;

namespace KosToolbox.Core.Tests;

public class NetworkTests
{
    static NetProperty Prop(string type, long? min = null, long? max = null, long? step = null, int numberBase = 10, params string[] options) =>
        new("*Test", "Test", type, null, null, options.Select(o => new KeyValuePair<string, string>(o, "Label " + o)).ToList(), min, max, step, numberBase, 8);

    [Theory]
    [InlineData(@"{4d36e972-e325-11ce-bfc1-08002be10318}\0007", "0007")]
    [InlineData(@"{4D36E972-E325-11CE-BFC1-08002BE10318}\0012", "0012")]
    [InlineData(@"{4d36e968-e325-11ce-bfc1-08002be10318}\0000", null)] // video class, not network
    [InlineData(@"{4d36e972-e325-11ce-bfc1-08002be10318}\..\x", null)]
    [InlineData(@"{4d36e972-e325-11ce-bfc1-08002be10318}\12", null)]
    [InlineData(null, null)]
    public void DriverKeyOnlyForNetClassInstances(string? driver, string? expected) => Assert.Equal(expected, NetAdapters.ParseDriverKey(driver));

    [Fact]
    public void EnumAcceptsOnlyListedValues()
    {
        var p = Prop("enum", options: new[] { "0", "1" });
        Assert.Null(NetAdapters.Problem(p, "1"));
        Assert.NotNull(NetAdapters.Problem(p, "2"));
        Assert.Null(NetAdapters.Problem(p, null)); // deleting goes back to the driver default
    }

    [Fact]
    public void NumbersRespectRangeStepAndBase()
    {
        var p = Prop("int", 0, 3000, 8);
        Assert.Null(NetAdapters.Problem(p, "200"));
        Assert.NotNull(NetAdapters.Problem(p, "201"));   // step
        Assert.NotNull(NetAdapters.Problem(p, "4000"));  // maximum
        Assert.NotNull(NetAdapters.Problem(p, "abc"));
        var hex = Prop("dword", 0, 255, numberBase: 16);
        Assert.Null(NetAdapters.Problem(hex, "FF"));
        Assert.NotNull(NetAdapters.Problem(hex, "100"));
    }

    [Fact]
    public void EditTextIsLimited()
    {
        var p = Prop("edit");
        Assert.Null(NetAdapters.Problem(p, "001122334455"[..8]));
        Assert.NotNull(NetAdapters.Problem(p, "0011223344556"));
        Assert.NotNull(NetAdapters.Problem(p, "a\nb"));
    }

    [Fact]
    public void ElevatedNetworkRequestsAreValidated()
    {
        Assert.ThrowsAny<Exception>(() => new ElevatedJob { NetAdapter = new NetAdapterJob { InstanceId = @"PCI\VEN_FFFF&DEV_FFFF\0", Writes = { new() { Keyword = "*JumboPacket", Value = "1514" } } } }.Validate());
        Assert.ThrowsAny<Exception>(() => new ElevatedJob { NetAdapter = new NetAdapterJob { InstanceId = @"PCI\VEN_FFFF&DEV_FFFF\0" } }.Validate()); // no values
        Assert.ThrowsAny<Exception>(() => new ElevatedJob { NetAdapter = new NetAdapterJob(), PrepareLogs = true }.Validate());
    }
}
