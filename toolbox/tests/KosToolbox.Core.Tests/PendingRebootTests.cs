using KosToolbox.Core.Persistence;
using Xunit;

namespace KosToolbox.Core.Tests;

public class PendingRebootTests
{
    static readonly DateTime Now = new(2026, 11, 22, 22, 30, 0, DateTimeKind.Utc);
    static readonly DateTime Boot = Now.AddMinutes(-20);

    [Fact]
    public void AChangeAfterBootIsPending() => Assert.True(PendingRebootStore.IsPending(Boot.AddMinutes(5), Boot, Now));

    [Fact]
    public void AChangeMadeJustBeforeARestartIsNoLongerPending() => Assert.False(PendingRebootStore.IsPending(Boot.AddSeconds(-26), Boot, Now));

    [Fact]
    public void AChangeADayBeforeBootIsNotPending() => Assert.False(PendingRebootStore.IsPending(Boot.AddDays(-1), Boot, Now));

    [Fact] // the NTP record dated 2116 stayed on the page for weeks
    public void ARecordFromAWrongClockInTheFutureIsNotPending() => Assert.False(PendingRebootStore.IsPending(new DateTime(2116, 1, 1, 6, 36, 5, DateTimeKind.Utc), Boot, Now));
}
