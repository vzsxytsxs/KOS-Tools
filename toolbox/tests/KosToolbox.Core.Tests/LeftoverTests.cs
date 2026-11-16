using System.Diagnostics;
using KosToolbox.Core.Apps;
using Xunit;

namespace KosToolbox.Core.Tests;

public class LeftoverTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "kos-leftovers-" + Guid.NewGuid().ToString("N"));

    public LeftoverTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Theory]
    [InlineData("7-Zip 23.01 (x64)", "7-Zip")]
    [InlineData("7-Zip 26.02 (x64 edition)", "7-Zip")]
    [InlineData("Notepad++ (64-bit x64)", "Notepad++")]
    [InlineData("Equibop 3.3.1", "Equibop")]
    [InlineData("Attack Shark Driver v4 3.1.12", "Attack Shark Driver v4")]
    [InlineData("Python 3.12.1 (64-bit)", "Python")]
    [InlineData("Quick CPU", "Quick CPU")]
    [InlineData("1.2.3", "1.2.3")]
    public void BaseNameDropsVersionAndArchitecture(string name, string expected) => Assert.Equal(expected, Leftovers.BaseName(name));

    [Fact]
    public void NamesSkipGenericWordsAndMicrosoft()
    {
        Assert.Equal(new[] { "attacksharkdriverv43112", "attacksharkdriverv4" }, Leftovers.ProductNames("Attack Shark Driver v4 3.1.12", null).OrderByDescending(n => n.Length));
        Assert.Contains("quickcpu", Leftovers.ProductNames("Quick CPU", @"C:\Program Files\CoderBag\QuickCPU"));
        Assert.Empty(Leftovers.ProductNames("Update", @"C:\Program Files\Steam"));
        Assert.Equal(new[] { "coderbagllc", "coderbag" }, Leftovers.VendorNames("CoderBag LLC").OrderByDescending(n => n.Length));
        Assert.Contains("famatech", Leftovers.VendorNames("Famatech Corp."));
        Assert.Empty(Leftovers.VendorNames("Microsoft Corporation"));
    }

    [Fact]
    public void NamesMatchExactlyOrWithAKnownSuffix()
    {
        Assert.True(Leftovers.Matches("QuickCPU64", new HashSet<string> { "quickcpu" }));
        Assert.True(Leftovers.Matches("attack shark driver v4-updater", new HashSet<string> { "attacksharkdriverv4" }));
        Assert.True(Leftovers.Matches("Attack_Shark Driver V4", new HashSet<string> { "attacksharkdriverv4" }));
        Assert.False(Leftovers.Matches("OBS-Studio", new HashSet<string> { "obs" }));
        Assert.False(Leftovers.Matches("ab", new HashSet<string> { "ab" }));
    }

    [Fact]
    public void ProtectsRootsWindowsAndTheKos()
    {
        var roots = new[] { @"C:\Users\u\AppData\Roaming", @"C:\Program Files" };
        var untouchable = new[] { @"C:\Windows", @"C:\ProgramData\TheKOS" };
        bool P(string path) => Leftovers.IsProtected(path, roots, untouchable);

        Assert.True(P(@"C:\"));
        Assert.True(P(@"C:\Users\u\AppData")); // above a root
        Assert.True(P(@"C:\Users\u\AppData\Roaming"));
        Assert.True(P(@"C:\Windows\Temp\x"));
        Assert.True(P(@"C:\ProgramData\TheKOS\tools"));
        Assert.False(P(@"C:\Users\u\AppData\Roaming\App"));
        Assert.False(P(@"C:\Program Files\App"));
        Assert.False(P(@"C:\Program Files Extra\App"));
        Assert.True(Leftovers.IsInside(@"C:\App\x", @"C:\App"));
        Assert.False(Leftovers.IsInside(@"C:\Apple", @"C:\App"));
    }

    [Fact]
    public void ReadsPathsFromCommandsIconsAndKnownFolders()
    {
        Assert.True(Leftovers.StartsInside("\"C:\\App\\app.exe\" -minimized", @"C:\App"));
        Assert.True(Leftovers.StartsInside(@"\??\C:\App\drv.sys", @"C:\App"));
        Assert.False(Leftovers.StartsInside(@"C:\Apple\a.exe", @"C:\App"));
        Assert.Equal(@"C:\App\unins000.exe", Leftovers.ExePath("\"C:\\App\\unins000.exe\" /x"));
        Assert.Equal(@"C:\App\app.exe", Leftovers.ExePath(@"C:\App\app.exe,0"));
        Assert.Null(Leftovers.ExePath("MsiExec.exe /X{AC76BA86-7AD7-1033-7B44-AC0F074E4100}"));
        Assert.Equal(@"C:\Program Files\QuickCPU\QuickCPU.exe",
            Leftovers.ExpandKnownFolder(@"{6D809377-6AF0-444B-8957-A3773F02200E}\QuickCPU\QuickCPU.exe", _ => @"C:\Program Files"));
        Assert.Equal("Microsoft.Windows.Explorer", Leftovers.ExpandKnownFolder("Microsoft.Windows.Explorer", _ => @"C:\X"));
    }

    [Fact]
    public void FindsTheProgramFolderWithoutGenericPlaces()
    {
        InstalledProgram P(string? location, string? uninstall, string? icon, string? msi = null) =>
            new("HKLM 64-bit\\X", "App", null, null, null, null, location, uninstall, msi, "HKLM 64-bit", icon);

        Assert.Equal(@"C:\Games\Tool", Leftovers.FolderOf(P(@"C:\Games\Tool\", "\"C:\\Other\\u.exe\"", null)));
        Assert.Equal(@"C:\Games\Tool", Leftovers.FolderOf(P(null, "\"C:\\Games\\Tool\\uninstall\\unins000.exe\"", null)));
        Assert.Equal(@"C:\Games\Tool", Leftovers.FolderOf(P(null, "MsiExec.exe /X{1}", @"C:\Games\Tool\tool.exe,0", "{1}")));
        Assert.Equal(@"C:\Games\Tool", Leftovers.FolderOf(P(null, null, @"C:\Games\Tool\bin\tool.exe"))); // "bin" goes up a level
        Assert.Null(Leftovers.FolderOf(P(null, null, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Installer\{1}\icon.exe"))));
        Assert.Null(Leftovers.FolderOf(P(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), null, null)));
    }

    [Fact]
    public void KnowsTheUninstallKeyAndThePackedMsiCode()
    {
        var p = new InstalledProgram("HKLM 32-bit\\{X}", "App", null, null, null, null, null, "u.exe", null, "HKLM 32-bit", null);
        Assert.Equal(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{X}", InstalledPrograms.KeyPath(p));
        Assert.Equal("68AB67CA7DA73301B744CAF070E41400", Leftovers.PackedGuid("{AC76BA86-7AD7-1033-7B44-AC0F074E4100}"));
    }

    [Fact]
    public void DeletesOnlyBelowAllowedRegistryPaths()
    {
        Assert.True(Leftovers.KeyAllowed(@"HKCU\Software\CoderBag\QuickCPU64"));
        Assert.True(Leftovers.KeyAllowed(@"HKLM\SOFTWARE\WOW6432Node\Famatech"));
        Assert.True(Leftovers.KeyAllowed(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\App"));
        Assert.True(Leftovers.KeyAllowed(@"HKLM\SOFTWARE\Microsoft\Tracing\app_RASAPI32"));
        Assert.False(Leftovers.KeyAllowed(@"HKCU\Software\Microsoft\Windows"));
        Assert.False(Leftovers.KeyAllowed(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\App"));
        Assert.False(Leftovers.KeyAllowed(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"));
        Assert.False(Leftovers.KeyAllowed(@"HKLM\SYSTEM\CurrentControlSet\Services\App"));
        Assert.False(Leftovers.KeyAllowed(@"HKCU\Software\"));
        Assert.True(Leftovers.ValueAllowed(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run"));
        Assert.False(Leftovers.ValueAllowed(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer"));
    }

    [Fact]
    public void ASystemFolderNeverLeadsToWindowsItems()
    {
        // Real search, read-only: an entry pointing to System32 doesn't match services, tasks or anything from Windows.
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var items = Leftovers.Scan(new ProgramTrace { Name = "Zz Kos Probe Name", Folder = system }, force: true);

        Assert.DoesNotContain(items, i => i.Kind is LeftoverKind.Service or LeftoverKind.Task);
        Assert.DoesNotContain(items, i => Leftovers.IsInside(i.Path, windows) || i.ValueName is { } v && v.StartsWith(windows, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnEntryTheAccountCanWriteOnlyReachesTheAccount()
    {
        var items = Leftovers.Scan(new ProgramTrace
        {
            Name = "Zz Kos Probe Name", Folder = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + @"\Zz Kos Probe", UserOnly = true,
        }, force: true);
        Assert.DoesNotContain(items, i => i.MachineWide);
    }

    [Fact]
    public void ShortNamesAreExpandedOrRefused()
    {
        var full = Leftovers.Full(@"C:\PROGRA~1");
        Assert.True(full is null || !full.Contains('~'), full);
    }

    [Fact]
    public async Task WaitsForTheProcessesTheUninstallerStarted()
    {
        // Like NSIS: the first process launches another and exits right away; the wait continues until the second exits (~2 s).
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c start \"\" /b ping -n 3 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        var started = p.StartTime;
        await p.WaitForExitAsync();
        var exited = sw.Elapsed;
        await KosToolbox.Core.Execution.ProcessTree.WaitAsync(p.Id, started, () => false);
        Assert.True(sw.Elapsed - exited > TimeSpan.FromSeconds(1), $"returned {(sw.Elapsed - exited).TotalSeconds:0.0} s after the first process");
    }

    [Fact]
    public void DeleteTreeRemovesReadOnlyFilesAndLeavesLinkTargetsAlone()
    {
        var target = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "x");
        var tree = Path.Combine(_dir, "App");
        Directory.CreateDirectory(Path.Combine(tree, "sub"));
        var readOnly = Path.Combine(tree, "sub", "ro.dat");
        File.WriteAllText(readOnly, "x");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        // Junction inside the folder pointing outside: the link goes away, the target stays.
        using (var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(tree, "link")}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false }))
            mk!.WaitForExit();

        Assert.Null(Leftovers.DeleteTree(tree));

        Assert.False(Directory.Exists(tree));
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
    }
}
