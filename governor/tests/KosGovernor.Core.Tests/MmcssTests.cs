using KosGovernor.Core;
using Xunit;

namespace KosGovernor.Core.Tests;

public class MmcssTests
{
    [Theory]
    [InlineData(10, 2, null)]
    [InlineData(20, 3, null)]
    [InlineData(null, null, null)]                              // values absent: Windows defaults, MMCSS runs
    [InlineData(100, 2, "SystemResponsiveness is 100, which turns MMCSS off")]
    [InlineData(10, 4, "the MMCSS service is disabled")]
    [InlineData(100, 4, "the MMCSS service is disabled")]
    public void ProblemFromValues(int? responsiveness, int? start, string? expected) =>
        Assert.Equal(expected, Mmcss.Problem(responsiveness, start));
}
