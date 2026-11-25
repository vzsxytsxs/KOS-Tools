using KosToolbox.Core.Tweaks;
using Microsoft.Win32;
using Xunit;

namespace KosToolbox.Core.Tests;

public class PartitionToolsFixTests
{
    [Fact]
    public void BootExecuteIsCreatedEmptyWhenMissingAndLeftAloneWhenThere()
    {
        var path = $@"Software\KosToolboxTest\{Guid.NewGuid():N}";
        try
        {
            Assert.Contains("is back", SystemFixes.EnsureBootExecute(Registry.CurrentUser, path));
            using (var key = Registry.CurrentUser.OpenSubKey(path)!)
            {
                Assert.Equal(RegistryValueKind.MultiString, key.GetValueKind("BootExecute"));
                Assert.Empty((string[])key.GetValue("BootExecute")!);
            }

            Assert.Contains("nothing changed", SystemFixes.EnsureBootExecute(Registry.CurrentUser, path));

            // Windows' own value is never replaced.
            using (var key = Registry.CurrentUser.OpenSubKey(path, writable: true)!)
                key.SetValue("BootExecute", new[] { "autocheck autochk *" }, RegistryValueKind.MultiString);
            Assert.Contains("nothing changed", SystemFixes.EnsureBootExecute(Registry.CurrentUser, path));
            using (var key = Registry.CurrentUser.OpenSubKey(path)!)
                Assert.Equal(new[] { "autocheck autochk *" }, (string[])key.GetValue("BootExecute")!);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(@"Software\KosToolboxTest", throwOnMissingSubKey: false); }
    }
}
