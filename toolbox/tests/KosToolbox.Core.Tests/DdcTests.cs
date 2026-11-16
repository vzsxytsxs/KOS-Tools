using KosToolbox.Core.Display;
using Xunit;

namespace KosToolbox.Core.Tests;

public class DdcTests
{
    [Fact]
    public void ReadsTheVcpCodesAndTheirValues()
    {
        const string caps = "(prot(monitor)type(LCD)model(XG2402)cmds(01 02 03 07 0C E3 F3)vcp(02 04 05 08 0B 0C 10 12 14(01 05 06 08 0B) 16 18 1A 52 60(0F 11 12) 62 87 AC AE B2 B6 C6 C8 CA CC(01 02 03 04 06 09 0A 0D 12 14 1E) D6(01 04 05) DC(00 01 02 03 05 08 0B) DF E1 FF)mswhql(1)asset_eep(40)mccs_ver(2.2))";
        var codes = Ddc.ParseVcp(caps);
        Assert.True(codes.ContainsKey(Ddc.Brightness));
        Assert.True(codes.ContainsKey(Ddc.Restore));
        Assert.Null(codes[Ddc.Contrast]);
        Assert.Equal(new uint[] { 0x0F, 0x11, 0x12 }, codes[Ddc.Input]!);
        Assert.Equal(new uint[] { 1, 4, 5 }, codes[0xD6]!);
        Assert.True(codes.ContainsKey(0xFF));
        Assert.False(codes.ContainsKey(0x01)); // the cmds() list isn't VCP codes
        Assert.Empty(Ddc.ParseVcp("(prot(monitor)type(LCD))"));
        Assert.Equal("HDMI 1", Ddc.InputName(0x11));
    }
}
