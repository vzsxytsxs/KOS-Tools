using System.Text;
using KosToolbox.Core.Storage;
using Xunit;

namespace KosToolbox.Core.Tests;

public class DiskMapTests
{
    // ---------- MFT records built by hand ----------

    /// <summary>A 1024-byte FILE record: header, update sequence (two sectors), the attributes given, the end marker.</summary>
    static byte[] Record(ushort flags, long baseRecord, params byte[][] attributes)
    {
        var r = new byte[1024];
        "FILE"u8.CopyTo(r);
        BitConverter.GetBytes((ushort)0x30).CopyTo(r, 4);   // update sequence offset
        BitConverter.GetBytes((ushort)3).CopyTo(r, 6);      // 1 number + 2 sectors
        BitConverter.GetBytes((ushort)0x38).CopyTo(r, 0x14); // first attribute
        BitConverter.GetBytes(flags).CopyTo(r, 0x16);
        BitConverter.GetBytes((ulong)baseRecord).CopyTo(r, 0x20);
        var at = 0x38;
        foreach (var a in attributes) { a.CopyTo(r, at); at += a.Length; }
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(r, at);
        // The update sequence: number 0x1234, the real last bytes of both sectors (zero here), the number at the sector ends.
        BitConverter.GetBytes((ushort)0x1234).CopyTo(r, 0x30);
        BitConverter.GetBytes((ushort)0x1234).CopyTo(r, 510);
        BitConverter.GetBytes((ushort)0x1234).CopyTo(r, 1022);
        return r;
    }

    static byte[] Resident(uint type, byte[] content, byte nameLength = 0)
    {
        var length = (24 + content.Length + 7) / 8 * 8;
        var a = new byte[length];
        BitConverter.GetBytes(type).CopyTo(a, 0);
        BitConverter.GetBytes(length).CopyTo(a, 4);
        a[9] = nameLength;
        BitConverter.GetBytes(content.Length).CopyTo(a, 0x10);
        BitConverter.GetBytes((ushort)24).CopyTo(a, 0x14);
        content.CopyTo(a, 24);
        return a;
    }

    static byte[] StandardInfo(uint attributes)
    {
        var c = new byte[48];
        BitConverter.GetBytes(attributes).CopyTo(c, 32);
        return Resident(0x10, c);
    }

    static byte[] FileName(long parent, string name, byte ns = 1)
    {
        var chars = Encoding.Unicode.GetBytes(name);
        var c = new byte[0x42 + chars.Length];
        BitConverter.GetBytes((ulong)parent).CopyTo(c, 0);
        c[0x40] = (byte)name.Length;
        c[0x41] = ns;
        chars.CopyTo(c, 0x42);
        return Resident(0x30, c);
    }

    static byte[] DataResident(int size) => Resident(0x80, new byte[size]);

    static byte[] DataNonResident(long realSize, long startVcn = 0, byte nameLength = 0)
    {
        var a = new byte[0x48];
        BitConverter.GetBytes(0x80u).CopyTo(a, 0);
        BitConverter.GetBytes(a.Length).CopyTo(a, 4);
        a[8] = 1;
        a[9] = nameLength;
        BitConverter.GetBytes(startVcn).CopyTo(a, 0x10);
        BitConverter.GetBytes(realSize).CopyTo(a, 0x30);
        return a;
    }

    static MftScanner.Entry Parse(byte[] record, bool wantName = false)
    {
        var e = new MftScanner.Entry { Parent = -1, Size = -1 };
        MftScanner.ParseRecord(record, ref e, wantName);
        return e;
    }

    [Fact]
    public void FixupsPutTheRealBytesBackAndRefuseATornRecord()
    {
        var r = Record(1, 0);
        BitConverter.GetBytes((ushort)0xBEEF).CopyTo(r, 0x32); // the real last bytes of sector 1
        BitConverter.GetBytes((ushort)0xCAFE).CopyTo(r, 0x34);
        Assert.True(MftScanner.ApplyFixups(r, 512));
        Assert.Equal(0xBEEF, BitConverter.ToUInt16(r, 510));
        Assert.Equal(0xCAFE, BitConverter.ToUInt16(r, 1022));

        var torn = Record(1, 0);
        torn[1023] ^= 0xFF; // a sector that doesn't end with the update sequence number
        Assert.False(MftScanner.ApplyFixups(torn, 512));
        var garbage = Record(1, 0);
        garbage[0] = (byte)'B';
        Assert.False(MftScanner.ApplyFixups(garbage, 512));
    }

    [Fact]
    public void AFileKeepsItsFirstLongNameParentAndDataSize()
    {
        var e = Parse(Record(1, 0, StandardInfo(0x20), FileName(5, "SHORT~1.TXT", ns: 2), FileName(31, "long name.txt", ns: 1), DataResident(100)));
        Assert.True(e.InUse);
        Assert.False(e.IsDirectory);
        Assert.Equal(31, e.Parent);   // not the DOS name's, which names the same parent anyway
        Assert.Equal(100, e.Size);
        Assert.Null(e.Name);          // a file's name isn't read for the sizes
        Assert.True(e.HasName && e.HasSize && !e.Skip);

        var big = Parse(Record(1, 0, FileName(5, "disk.vmem"), DataNonResident(4_290_000_000)));
        Assert.Equal(4_290_000_000, big.Size);
        Assert.Equal("disk.vmem", Parse(Record(1, 0, FileName(5, "disk.vmem"), DataNonResident(1)), wantName: true).Name);

        // The later extents of a fragmented stream (VCN above 0) and named streams don't carry the file's size.
        Assert.Equal(-1, Parse(Record(1, 0, FileName(5, "f"), DataNonResident(999, startVcn: 4096))).Size);
        Assert.Equal(-1, Parse(Record(1, 0, FileName(5, "f"), DataNonResident(999, nameLength: 4))).Size);
    }

    [Fact]
    public void AFolderIsReadWithItsNameAndReparsePointsAndCloudFilesAreSkipped()
    {
        var folder = Parse(Record(3, 0, StandardInfo(0x10), FileName(5, "Documents")));
        Assert.True(folder.IsDirectory);
        Assert.Equal("Documents", folder.Name);
        Assert.False(folder.Skip);

        Assert.True(Parse(Record(3, 0, StandardInfo(0x400 | 0x10), FileName(5, "Link"))).Skip);        // junction or symbolic link
        Assert.True(Parse(Record(1, 0, StandardInfo(0x400 | 0x400000), FileName(5, "cloud.docx"), DataNonResident(5000))).Skip); // a cloud placeholder is a reparse point
        // Other bits NTFS keeps in that field aren't Win32's: 0x40000 sat on an ordinary folder of a game library, and nothing under it may vanish.
        Assert.False(Parse(Record(3, 0, StandardInfo(0x40020), FileName(5, "common"))).Skip);
        Assert.False(Parse(Record(0, 0, StandardInfo(0x20), FileName(5, "deleted"), DataResident(10))).InUse); // a free record
    }

    [Fact]
    public void RunListsDecodeWithSignedDeltas()
    {
        // 0x21: a 1-byte length and a 2-byte offset. Then 0x11: 1 and 1, the offset a negative delta (-16).
        var runs = MftScanner.DecodeRuns(new byte[] { 0x21, 0x18, 0x34, 0x56, 0x11, 0x10, 0xF0, 0x00 });
        Assert.Equal(new[] { (0x5634L, 0x18L), (0x5624L, 0x10L) }, runs);
        Assert.Null(MftScanner.DecodeRuns(new byte[] { 0x01, 0x10, 0x00 })); // a sparse run: not in the MFT
        Assert.Empty(MftScanner.DecodeRuns(new byte[] { 0x00 })!);
    }

    [Fact]
    public void TheMftsOwnRunsAreFoundInRecordZero()
    {
        var runs = new byte[] { 0x12, 0x00, 0x08, 0x01, 0x00 }; // a 2-byte length (0x0800 clusters), a 1-byte offset (LCN 1), the end
        var attribute = new byte[(0x48 + runs.Length + 7) / 8 * 8];
        BitConverter.GetBytes(0x80u).CopyTo(attribute, 0);
        BitConverter.GetBytes(attribute.Length).CopyTo(attribute, 4);
        attribute[8] = 1;                                              // non-resident, unnamed
        BitConverter.GetBytes((ushort)0x48).CopyTo(attribute, 0x20);  // where the run list starts
        runs.CopyTo(attribute, 0x48);
        var found = MftScanner.FindMftRuns(Record(1, 0, attribute));
        Assert.NotNull(found);
        Assert.Single(found!);
        Assert.Equal((1L, 0x0800L), found![0]);
    }

    // ---------- the folder tree ----------

    static MftScanner.Entry Folder(int parent, string name, bool skip = false) =>
        new() { InUse = true, IsDirectory = true, HasName = true, Parent = parent, Name = name, Size = -1, Skip = skip };

    static MftScanner.Entry File(int parent, long size, bool skip = false) =>
        new() { InUse = true, HasName = true, HasSize = true, Parent = parent, Size = size, Skip = skip };

    static MftScanner.Entry[] Table(int length) =>
        Enumerable.Range(0, length).Select(_ => new MftScanner.Entry { Parent = -1, Size = -1 }).ToArray();

    [Fact]
    public void FoldersAddUpTheirFilesAndSubfolders()
    {
        var t = Table(60);
        t[5] = Folder(5, ".");
        t[30] = Folder(5, "Users");
        t[31] = Folder(30, "me");
        t[32] = File(31, 100);
        t[33] = File(30, 50);
        t[34] = Folder(5, "Link", skip: true);       // a junction: not counted, nothing under it
        t[35] = File(34, 7000);                       // belongs to the skipped folder: not reachable
        t[36] = Folder(99, "orphan");
        t[37] = File(5, 9, skip: true);               // a cloud file
        t[38] = File(31, 0);                          // an empty file still counts as a file
        var names = new Dictionary<long, string> { [32] = "big.bin", [33] = "small.bin" };

        var r = MftScanner.Build(t, @"C:\", @"C:\", n => names.GetValueOrDefault(n), null)!;
        Assert.Equal(150, r.Root.Size);
        Assert.Equal(3, r.Files);
        Assert.Equal(1, r.Root.Children!.Count);
        var users = r.Root.Children![0];
        Assert.Equal((150L, 3L), (users.Size, users.Files));
        Assert.Equal(100, users.Children![0].Size);
        Assert.Equal(@"C:\Users\me", users.Children[0].Path);
        Assert.Equal(new[] { 100L, 50L }, r.Largest.Take(2).Select(f => f.Size));
        Assert.Equal(@"C:\Users\me\big.bin", r.Largest[0].Path);
        Assert.True(r.Skipped >= 2); // the orphan folder and the file under the skipped one
    }

    [Fact]
    public void AnExtensionRecordHandsItsSizeAndNameToItsBase()
    {
        var t = Table(60);
        t[5] = Folder(5, ".");
        t[40] = new MftScanner.Entry { InUse = true, Size = -1, Parent = -1 };                                                   // a file whose $DATA lives elsewhere
        t[41] = new MftScanner.Entry { InUse = true, Base = 40, HasSize = true, Size = 777, HasName = true, Parent = 5, Name = "x" }; // its extension record
        var r = MftScanner.Build(t, @"C:\", @"C:\", _ => "x", null)!;
        Assert.Equal(777, r.Root.Size);
    }

    [Fact]
    public void ASubfolderIsItsOwnRootLikeTheWalkReturnsIt()
    {
        var t = Table(60);
        t[5] = Folder(5, ".");
        t[30] = Folder(5, "Users");
        t[31] = Folder(30, "me");
        t[32] = File(31, 100);
        t[33] = File(5, 400);
        var names = new Dictionary<long, string> { [32] = "a.bin", [33] = "b.bin" };
        var r = MftScanner.Build(t, @"C:\", @"C:\users", n => names[n], null)!;
        Assert.Null(r.Root.Parent);               // nothing above it
        Assert.Equal(@"C:\users", r.Root.Path);
        Assert.Equal(100, r.Root.Size);
        Assert.Single(r.Largest);                 // only the files inside
        Assert.Equal(@"C:\Users\me\a.bin", r.Largest[0].Path);
        Assert.Null(MftScanner.Build(t, @"C:\", @"C:\Nowhere", n => names[n], null)); // not in the table: the caller walks it
    }

    // ---------- the treemap layout ----------

    [Fact]
    public void TheTreemapFillsTheRectangleInProportionToTheWeights()
    {
        var weights = new double[] { 600, 300, 150, 100, 50, 30, 20 };
        var cells = TreemapLayout.Squarify(weights, 10, 20, 400, 250);
        Assert.Equal(weights.Length, cells.Count);
        Assert.Equal(400 * 250, cells.Sum(c => c.Width * c.Height), 3);
        var total = weights.Sum();
        foreach (var c in cells)
        {
            Assert.InRange(c.X, 10 - 1e-6, 410 + 1e-6);
            Assert.InRange(c.Y, 20 - 1e-6, 270 + 1e-6);
            Assert.True(c.X + c.Width <= 410 + 1e-6 && c.Y + c.Height <= 270 + 1e-6);
            Assert.Equal(weights[c.Index] / total * 400 * 250, c.Width * c.Height, 3);
        }
        // No two cells overlap.
        for (var i = 0; i < cells.Count; i++)
            for (var j = i + 1; j < cells.Count; j++)
            {
                var a = cells[i]; var b = cells[j];
                var w = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
                var h = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
                Assert.False(w > 1e-6 && h > 1e-6, $"cells {i} and {j} overlap");
            }
        // Squarified: no cell is a sliver (the plain slice-and-dice layout makes 20:1 strips for these weights).
        Assert.All(cells, c => Assert.InRange(Math.Max(c.Width / c.Height, c.Height / c.Width), 1, 6));
    }

    [Fact]
    public void TheTreemapSkipsEmptyWeightsAndHandlesTinyInput()
    {
        var cells = TreemapLayout.Squarify(new double[] { 10, 0, 5 }, 0, 0, 100, 100);
        Assert.Equal(new[] { 0, 2 }, cells.Select(c => c.Index));
        Assert.Empty(TreemapLayout.Squarify(Array.Empty<double>(), 0, 0, 100, 100));
        Assert.Empty(TreemapLayout.Squarify(new double[] { 5 }, 0, 0, 0, 100));
        var one = TreemapLayout.Squarify(new double[] { 5 }, 1, 2, 30, 40);
        Assert.Equal(new TreemapCell(1, 2, 30, 40, 0), one[0]);
    }
}
