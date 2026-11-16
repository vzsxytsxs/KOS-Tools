using KosToolbox.Core.Gpu;
using Xunit;

namespace KosToolbox.Core.Tests;

public class DrsProgramTests
{
    /// <summary>Read-only on this PC's driver (no NVIDIA → nothing to check): a well-known game resolves to its driver profile, which lists it.</summary>
    [Fact]
    public void FindsTheDriverProfileOfAGame()
    {
        DrsSession s;
        try { s = DriverSettings.Open(); }
        catch (Exception) when (Environment.GetEnvironmentVariable("KOS_TEST_REPORT") is null) { return; }
        using (s)
        {
            var found = Nvapi.DrsFindApplication(s.Handle, "cs2.exe");
            Assert.NotNull(found);
            Assert.True(found.Value.Predefined);
            var profile = DriverSettings.Profiles(s).Single(p => p.Name == found.Value.ProfileName);
            Assert.Contains(DriverSettings.Programs(s, profile), a => a.EndsWith("cs2.exe", StringComparison.OrdinalIgnoreCase));
            Assert.Null(Nvapi.DrsFindApplication(s.Handle, "kos-no-such-program-4711.exe"));
            Assert.Empty(DriverSettings.Programs(s, DriverSettings.Profiles(s)[0]));
        }
    }

    /// <summary>Creates a profile for a program the driver doesn't know, finds it with the program in it, then deletes it. KOS_TEST_DRS=1.</summary>
    [Fact]
    public void CreatesAndDeletesAProgramProfile()
    {
        if (Environment.GetEnvironmentVariable("KOS_TEST_DRS") != "1") return;
        const string exe = "kos-test-drs-4711.exe";
        using (var s = DriverSettings.Open())
        {
            var (name, created) = DriverSettings.ProfileForProgram(s, Path.Combine(@"C:\Games", exe));
            Assert.True(created);
            Assert.Equal(exe, name);
        }
        using (var s = DriverSettings.Open()) // read again from the saved database
        {
            var profile = DriverSettings.Profiles(s).Single(p => p.Name == exe);
            Assert.Contains(DriverSettings.Programs(s, profile), a => a.EndsWith(exe, StringComparison.OrdinalIgnoreCase));
            Assert.Equal((exe, false), DriverSettings.ProfileForProgram(s, exe)); // found now, not created twice
            DriverSettings.DeleteProfile(s, profile);
        }
        using (var s = DriverSettings.Open())
        {
            Assert.DoesNotContain(DriverSettings.Profiles(s), p => p.Name == exe);
            Assert.Null(Nvapi.DrsFindApplication(s.Handle, exe));
        }
    }
}
