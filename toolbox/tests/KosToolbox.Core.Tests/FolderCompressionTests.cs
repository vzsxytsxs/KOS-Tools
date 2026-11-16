using KosToolbox.Core.Storage;
using Xunit;

namespace KosToolbox.Core.Tests;

public class FolderCompressionTests
{
    [Fact]
    public void RefusesWindowsAndDriveRoots()
    {
        Assert.NotNull(FolderCompression.Problem(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.NotNull(FolderCompression.Problem(Environment.SystemDirectory));
        Assert.NotNull(FolderCompression.Problem(Path.GetPathRoot(Environment.SystemDirectory)!));
        Assert.Null(FolderCompression.Problem(Path.GetTempPath()));
    }

    /// <summary>Compresses and uncompresses a folder of its own in %TEMP%, and refuses one with a junction inside.</summary>
    [Fact]
    public async Task CompressesAndUncompressesATempFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"kos-compact-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        try
        {
            var text = string.Concat(Enumerable.Repeat("KOS compression test line. ", 40_000)); // ~1 MB, very compressible
            File.WriteAllText(Path.Combine(dir, "a.txt"), text);
            File.WriteAllText(Path.Combine(dir, "sub", "b.dat"), text);
            var (before, compressed, code) = await FolderCompression.RunAsync(dir, "XPRESS4K");
            Assert.Equal(0, code);
            Assert.Equal(2, before.Files);
            Assert.True(compressed.OnDisk < before.OnDisk / 4, $"{before.OnDisk} → {compressed.OnDisk}");
            Assert.Equal(before.Logical, compressed.Logical);
            var (_, uncompressed, _) = await FolderCompression.RunAsync(dir, null);
            Assert.True(uncompressed.OnDisk >= before.OnDisk * 9 / 10, $"{uncompressed.OnDisk}");
            Assert.Equal(text, File.ReadAllText(Path.Combine(dir, "sub", "b.dat")));

            var target = Path.Combine(Path.GetTempPath(), $"kos-compact-target-{Guid.NewGuid():N}");
            Directory.CreateDirectory(target);
            try
            {
                KosToolbox.Core.Execution.ProcessRunner.Run(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /J \"{Path.Combine(dir, "sub", "link")}\" \"{target}\"");
                Assert.NotNull(FolderCompression.LinkInside(dir));
                await Assert.ThrowsAsync<InvalidOperationException>(() => FolderCompression.RunAsync(dir, "XPRESS4K"));
                Assert.NotNull(FolderCompression.Problem(Path.Combine(dir, "sub", "link")));          // the chosen folder is the link
                Directory.CreateDirectory(Path.Combine(target, "inner"));
                Assert.NotNull(FolderCompression.Problem(Path.Combine(dir, "sub", "link", "inner"))); // a parent is the link
            }
            finally
            {
                if (Directory.Exists(Path.Combine(dir, "sub", "link"))) Directory.Delete(Path.Combine(dir, "sub", "link"));
                Directory.Delete(target, recursive: true);
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
