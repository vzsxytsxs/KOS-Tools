using KosToolbox.Core.Execution;
using KosToolbox.Core.Storage;
using KosToolbox.Core.Text;
using Xunit;

namespace KosToolbox.Core.Tests;

public class StorageTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "kos-storage-" + Guid.NewGuid().ToString("N"));

    public StorageTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    string File(string relative, int bytes, TimeSpan? age = null)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        if (age is { } a) System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow - a);
        return path;
    }

    [Fact]
    public void ScannerSumsFoldersAndSortsBySize()
    {
        File(@"a\1.bin", 100);
        File(@"a\deep\2.bin", 300);
        File(@"b\3.bin", 1000);
        File("root.bin", 5);

        var r = DiskScanner.Scan(_dir);

        Assert.Equal(1405, r.Root.Size);
        Assert.Equal(4, r.Files);
        Assert.Equal(new[] { "b", "a" }, r.Root.Children!.Select(c => c.Name));
        Assert.Equal(400, r.Root.Children![1].Size);
        Assert.Equal(Path.Combine(_dir, "a", "deep"), r.Root.Children[1].Children![0].Path);
        Assert.Equal(new long[] { 1000, 300, 100, 5 }, r.Largest.Select(f => f.Size));
    }

    [Fact]
    public void ScannerCanBeCancelled()
    {
        File(@"x\1.bin", 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => DiskScanner.Scan(_dir, null, cts.Token));
    }

    [Fact]
    public void CleanerDeletesOldFilesKeepsRootAndRemovesEmptyFolders()
    {
        var old = File(@"sub\old.tmp", 50, TimeSpan.FromHours(2));
        var fresh = File("fresh.tmp", 20);
        var readOnly = File(@"sub2\ro.tmp", 10, TimeSpan.FromHours(3));
        System.IO.File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        var category = new JunkCategory("test", "Test", "", false, true, TimeSpan.FromHours(1), () => new[] { new JunkTarget(_dir) });

        var before = JunkCleaner.Measure(category);
        var result = JunkCleaner.Clean(category);

        Assert.Equal(new JunkMeasure(60, 2, true), before);
        Assert.Equal(60, result.FreedBytes);
        Assert.Equal(2, result.Deleted);
        Assert.False(System.IO.File.Exists(old));
        Assert.True(System.IO.File.Exists(fresh));
        Assert.True(Directory.Exists(_dir));
        Assert.False(Directory.Exists(Path.Combine(_dir, "sub")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "sub2")));
    }

    [Fact]
    public void CleanerPatternAndSingleFile()
    {
        File(@"thumbcache_256.db", 7);
        File(@"keep.txt", 3);
        var single = File(@"MEMORY.DMP", 9);
        var pattern = new JunkCategory("p", "P", "", false, true, TimeSpan.Zero, () => new[] { new JunkTarget(_dir, "thumbcache_*.db"), new JunkTarget(single, SingleFile: true) });

        Assert.Equal(16, JunkCleaner.Clean(pattern).FreedBytes);
        Assert.True(System.IO.File.Exists(Path.Combine(_dir, "keep.txt")));
    }

    [Fact]
    public void ElevatedCleanupAcceptsOnlySystemCategoryIds()
    {
        new ElevatedJob { CleanJunk = { "windows-temp", "prefetch" } }.Validate();
        Assert.ThrowsAny<Exception>(() => new ElevatedJob { CleanJunk = { "user-temp" } }.Validate());      // user's: never elevated
        Assert.ThrowsAny<Exception>(() => new ElevatedJob { CleanJunk = { @"C:\Windows" } }.Validate());    // a path is not an id
        Assert.ThrowsAny<Exception>(() => new ElevatedJob { CleanJunk = { "prefetch", "prefetch" } }.Validate());
        Assert.ThrowsAny<Exception>(() => new ElevatedJob { CleanJunk = { "prefetch" }, PrepareLogs = true }.Validate());
    }

    [Theory]
    [InlineData(512, "512 bytes")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5.0 GB")]
    [InlineData(300L * 1024 * 1024, "300 MB")]
    public void FormatsSizes(long bytes, string text) => Assert.Equal(text, Txt.Size(bytes));

    void Junction(string link, string target)
    {
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })!)
            mklink.WaitForExit();
        Assert.True(Directory.Exists(link));
    }

    [Fact]
    public void CleanupNeverEntersALinkedRoot()
    {
        var outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(Path.Combine(outside, "empty"));
        System.IO.File.WriteAllText(Path.Combine(outside, "keep.txt"), "y");
        var root = Path.Combine(_dir, "root");
        Junction(root, outside);

        JunkCleaner.Clean(new JunkCategory("test", "test", "test", true, false, TimeSpan.Zero, () => new[] { new JunkTarget(root) }));
        Assert.True(System.IO.File.Exists(Path.Combine(outside, "keep.txt")));
        Assert.True(Directory.Exists(Path.Combine(outside, "empty"))); // RemoveEmptyFolders didn't go through the link either
    }

    [Fact]
    public void DeleteRefusesAPathRedirectedByAJunction()
    {
        // As if "x" had been listed as a real folder and swapped for a junction before the delete.
        var outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        System.IO.File.WriteAllText(Path.Combine(outside, "keep.txt"), "y");
        Directory.CreateDirectory(Path.Combine(_dir, "root"));
        Junction(Path.Combine(_dir, "root", "x"), outside);

        Assert.False(JunkCleaner.DeleteExact(Path.Combine(_dir, "root", "x", "keep.txt")));
        Assert.True(System.IO.File.Exists(Path.Combine(outside, "keep.txt")));
        Assert.True(JunkCleaner.DeleteExact(Path.Combine(outside, "keep.txt"))); // its real path: deleted
        Assert.False(System.IO.File.Exists(Path.Combine(outside, "keep.txt")));
    }
}
