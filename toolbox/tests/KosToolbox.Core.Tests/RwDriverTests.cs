using KosToolbox.Core.Devices;
using KosToolbox.Core.Text;
using Xunit;

namespace KosToolbox.Core.Tests;

public class RwDriverTests
{
    [Theory]
    [InlineData(0, null, (byte)0, null)]
    [InlineData(0, 0, (byte)0, null)]
    [InlineData(null, null, (byte)0, Txt.RwBlocklistOn)]   // absent = Windows default, on
    [InlineData(1, null, (byte)0, Txt.RwBlocklistOn)]
    [InlineData(0, 1, (byte)0, Txt.RwBlocklistOn)]         // Smart App Control forces the blocklist
    [InlineData(0, 2, (byte)0, Txt.RwBlocklistOn)]         // ... in evaluation mode too
    [InlineData(0, null, (byte)1, Txt.RwVbsRunning)]
    [InlineData(0, null, (byte)3, Txt.RwHvciRunning)]
    [InlineData(0, null, null, Txt.RwVbsRunning)]          // unreadable counts as blocked
    public void BlockedWhenTheBlocklistVbsOrHvciIsOn(int? blocklist, int? smartAppControl, byte? isolation, string? reason) =>
        Assert.Equal(reason is null ? null : Txt.RwBlocked(reason), RwDriver.Reason(blocklist, smartAppControl, isolation));

    [Fact]
    public void EveryPortableProgramPinsItsPublisher() =>
        Assert.All(Setup.SetupCatalog.Apps.SelectMany(a => a.Variants.Prepend(a)).Where(a => a.Portable), a => Assert.False(string.IsNullOrEmpty(a.Publisher), a.Id));
}
