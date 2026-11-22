using KosToolbox.Core.Storage;
using Xunit;

namespace KosToolbox.Core.Tests;

public class DiskDeleteTests
{
    static string TempFolder()
    {
        var d = Path.Combine(Path.GetTempPath(), "kos-diskmap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void AFoldersBiggestFilesAreListedAndTheRestIsTotalled()
    {
        var d = TempFolder();
        try
        {
            for (var i = 1; i <= 6; i++) File.WriteAllBytes(Path.Combine(d, $"f{i}.bin"), new byte[i * 100]);
            Directory.CreateDirectory(Path.Combine(d, "sub"));
            File.WriteAllBytes(Path.Combine(d, "sub", "inner.bin"), new byte[5000]); // not directly in the folder
            var r = FolderFiles.Read(d, top: 4);
            Assert.Equal(new[] { "f6.bin", "f5.bin", "f4.bin", "f3.bin" }, r.Top.Select(f => Path.GetFileName(f.Path)));
            Assert.Equal(new long[] { 600, 500, 400, 300 }, r.Top.Select(f => f.Size));
            Assert.Equal((300L, 2), (r.RestSize, r.RestCount)); // f1 + f2
            Assert.Empty(FolderFiles.Read(Path.Combine(d, "nothing")).Top);
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void DeletedItemsLeaveTheTreeWithTheirSizes()
    {
        var root = new DirNode(@"C:\", null);
        var a = new DirNode("a", root);
        var b = new DirNode("b", a);
        root.Children = new() { a };
        a.Children = new() { b };
        root.Size = 1000; root.Files = 10; a.Size = 900; a.Files = 9; b.Size = 600; b.Files = 6;

        b.RemoveFile(100); // one file of b
        Assert.Equal(new long[] { 900, 9, 800, 8, 500, 5 }, new[] { root.Size, root.Files, a.Size, a.Files, b.Size, b.Files });

        b.Detach();        // the folder b itself: its 500 bytes and 5 files
        Assert.Equal(new long[] { 400, 4, 300, 3 }, new[] { root.Size, root.Files, a.Size, a.Files });
        Assert.Empty(a.Children!);
    }

    [Fact]
    public void WhatMustStayIsRefusedAndTheRestCanGo()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.NotNull(StorageDelete.Problem(@"C:\"));                                           // a drive
        Assert.NotNull(StorageDelete.Problem(windows));
        Assert.NotNull(StorageDelete.Problem(Path.Combine(windows, "System32")));
        Assert.NotNull(StorageDelete.Problem(Path.Combine(windows, "WinSxS", "x")));             // inside Windows
        Assert.NotNull(StorageDelete.Problem(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        Assert.NotNull(StorageDelete.Problem(profile));
        Assert.NotNull(StorageDelete.Problem(Path.GetDirectoryName(profile)!));                  // the folder that holds every profile
        Assert.NotNull(StorageDelete.Problem(@"C:\pagefile.sys"));
        Assert.NotNull(StorageDelete.Problem(@"relative\path"));
        Assert.NotNull(StorageDelete.Problem(Path.Combine(windows, "Temp")));                    // the folder, not what is inside it
        Assert.Null(StorageDelete.Problem(Path.Combine(windows, "Temp", "junk.tmp")));
        Assert.Null(StorageDelete.Problem(Path.Combine(profile, "Downloads", "old.iso")));
        Assert.Null(StorageDelete.Problem(@"D:\games\old"));
    }

    [Fact]
    public void PermanentDeleteRemovesAFileAndAFolderButNotWhatALinkPointsTo()
    {
        var d = TempFolder();
        try
        {
            var keep = Path.Combine(d, "keep");
            Directory.CreateDirectory(keep);
            File.WriteAllText(Path.Combine(keep, "important.txt"), "x");
            var victim = Path.Combine(d, "victim");
            Directory.CreateDirectory(victim);
            File.WriteAllText(Path.Combine(victim, "a.txt"), "x");
            var readOnly = Path.Combine(victim, "readonly.txt");
            File.WriteAllText(readOnly, "x");
            File.SetAttributes(readOnly, FileAttributes.ReadOnly);
            try { Directory.CreateSymbolicLink(Path.Combine(victim, "link"), keep); } catch (Exception) { /* no right to make a link here: the rest still runs */ }
            Assert.Null(StorageDelete.Permanent(victim));
            Assert.False(Directory.Exists(victim));
            Assert.True(File.Exists(Path.Combine(keep, "important.txt"))); // a link inside is removed, not followed

            var single = Path.Combine(d, "one.bin");
            File.WriteAllBytes(single, new byte[10]);
            Assert.Null(StorageDelete.Permanent(single));
            Assert.False(File.Exists(single));
            Assert.Null(StorageDelete.Permanent(Path.Combine(d, "already-gone")));
        }
        finally { Directory.Delete(d, true); }
    }
}
