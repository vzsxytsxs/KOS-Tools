using KosToolbox.Core.Gpu;
using Xunit;

namespace KosToolbox.Core.Tests;

// The live NVAPI tests share one session (a reset in one would close it under another): they run one at a time.
[Collection("NVAPI")]
public class DriverSettingsTests
{
    [Fact]
    public void NipExportImportsBack()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kos-nip-{Guid.NewGuid():N}.nip");
        try
        {
            DriverSettings.ExportNip(path, "Base Profile", new[]
            {
                new DriverSettings.NipSetting(0x10835002, "Frame Rate Limiter V3", false, 240, null),
                new DriverSettings.NipSetting(0x00000001, "Some string", true, 0, "abc"),
            });
            var profile = Assert.Single(DriverSettings.ImportNip(path));
            Assert.Equal("Base Profile", profile.Profile);
            Assert.Equal((0x10835002u, 240u), (profile.Settings[0].Id, profile.Settings[0].Dword));
            Assert.Equal("abc", profile.Settings[1].Text);
        }
        finally { File.Delete(path); }
    }
}
