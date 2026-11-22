using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace KosToolbox.Core.Storage;

/// <summary>
/// The size of each folder of an NTFS volume from its master file table (MFT), read straight from the volume: one sequential pass
/// instead of a directory walk. Needs administrator (raw volume read). Counts like <see cref="DiskScanner"/>: a file's logical size,
/// the first long name of a file with hard links, no junctions or symbolic links (nor anything with a reparse point), no cloud files
/// that aren't on disk, no alternate streams; the NTFS metadata files ($MFT, $LogFile…) aren't counted.
/// <see cref="TryScan"/> returns null when this can't be used (not NTFS, not elevated, a volume it can't read); the caller then walks the folders.
/// </summary>
public static class MftScanner
{
    const uint FsctlGetNtfsVolumeData = 0x00090064;
    const int ChunkBytes = 8 * 1024 * 1024, FirstUserRecord = 24;
    const int RootRecord = 5;

    // The one $STANDARD_INFORMATION attribute that keeps a file or folder out of the count, as in the directory walk: a reparse point (a junction,
    // a symbolic link, a cloud placeholder). The other bits NTFS keeps there aren't the ones Win32 shows (0x40000 was set on a plain folder).
    const uint AttrReparse = 0x400;

    internal sealed record VolumeInfo(int BytesPerSector, int BytesPerCluster, int BytesPerRecord, long MftStartLcn, long MftValidLength);

    /// <summary>One MFT record, reduced to what the scan needs. A name is kept only for folders (and for the few files the largest list asks for later).</summary>
    internal struct Entry
    {
        public int Base;     // 0 for a base record, else the base record's number (this is an extension record)
        public int Parent;   // parent folder's record number, -1 without a usable name
        public long Size;    // logical size of the unnamed data stream, -1 when not seen
        public string? Name;
        public bool InUse, IsDirectory, Skip, HasName, HasSize;
    }

    /// <summary>Why the last <see cref="TryScan"/> returned null (for the log and the harness); null after a success.</summary>
    public static string? Problem { get; private set; }

    public static ScanResult? TryScan(string path, IProgress<(long Files, string Folder)>? progress = null, CancellationToken ct = default)
    {
        Problem = null;
        string full;
        try { full = Path.GetFullPath(path); } catch (ArgumentException) { return Fail("bad path"); }
        var volumeRoot = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(volumeRoot) || volumeRoot.StartsWith(@"\\", StringComparison.Ordinal) || !Directory.Exists(full)) return Fail("not a local folder");
        if (!Execution.Elevation.IsAdmin) return Fail("not administrator");
        try
        {
            if (new DriveInfo(volumeRoot).DriveFormat != "NTFS") return Fail("not NTFS");
            using var volume = OpenDisk(volumeRoot[0]);
            if (ReadVolumeInfo(volume.Control) is not { } info) return Fail("the volume data wasn't readable");
            if (ReadMft(volume, info, progress, ct) is not var (entries, reader)) return Fail("the MFT's own extents weren't readable");
            return Build(entries, volumeRoot, full, reader.NameOf, progress) ?? Fail("the folder isn't in the table (a junction on the way?)");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return Fail(ex.Message);
        }
    }

    static ScanResult? Fail(string why) { Problem = why; return null; }

    // ---------- reading the volume ----------

    /// <summary>Where the bytes are read from: the volume, or, when Windows refuses raw reads of it (seen on a system volume: error 50), its disk at the partition's offset.</summary>
    sealed class RawDisk(SafeFileHandle control, SafeFileHandle data, long baseOffset) : IDisposable
    {
        public SafeFileHandle Control { get; } = control; // the volume handle: for the ioctls
        public SafeFileHandle Data { get; } = data;
        public long Base { get; } = baseOffset;
        public void Dispose() { if (!ReferenceEquals(Data, Control)) Data.Dispose(); Control.Dispose(); }
    }

    static SafeFileHandle Open(string name)
    {
        var handle = CreateFile(name, 0x80000000 /* GENERIC_READ */, 3 /* share read and write */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        return handle;
    }

    static RawDisk OpenDisk(char letter)
    {
        var volume = Open($@"\\.\{char.ToUpperInvariant(letter)}:");
        var direct = new RawDisk(volume, volume, 0);
        if (CanRead(direct)) return direct;
        // The disk the volume lies on, and where: one extent only (a volume spanning disks isn't read this way).
        var extents = new byte[64];
        if (!DeviceIoControl(volume, 0x00560000 /* IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS */, IntPtr.Zero, 0, extents, extents.Length, out var returned, IntPtr.Zero)
            || returned < 32 || BitConverter.ToInt32(extents, 0) != 1)
        {
            volume.Dispose();
            throw new IOException("The volume can't be read directly and its disk isn't known.");
        }
        var disk = Open($@"\\.\PhysicalDrive{BitConverter.ToInt32(extents, 8)}");
        var viaDisk = new RawDisk(volume, disk, BitConverter.ToInt64(extents, 16));
        if (CanRead(viaDisk)) return viaDisk;
        viaDisk.Dispose();
        throw new IOException("Neither the volume nor its disk can be read directly.");
    }

    static bool CanRead(RawDisk disk)
    {
        try { ReadAt(disk, 0, new byte[4096], 4096); return true; }
        catch (Win32Exception) { return false; }
    }

    static VolumeInfo? ReadVolumeInfo(SafeFileHandle volume)
    {
        // NTFS_VOLUME_DATA_BUFFER: BytesPerSector 0x28, BytesPerCluster 0x2C, BytesPerFileRecordSegment 0x30, MftValidDataLength 0x38, MftStartLcn 0x40.
        var buffer = new byte[96];
        if (!DeviceIoControl(volume, FsctlGetNtfsVolumeData, IntPtr.Zero, 0, buffer, buffer.Length, out var returned, IntPtr.Zero) || returned < 0x48) return null;
        var info = new VolumeInfo(BitConverter.ToInt32(buffer, 0x28), BitConverter.ToInt32(buffer, 0x2C), BitConverter.ToInt32(buffer, 0x30), BitConverter.ToInt64(buffer, 0x40), BitConverter.ToInt64(buffer, 0x38));
        return info.BytesPerSector is >= 512 and <= 65536 && info.BytesPerCluster >= info.BytesPerSector && info.BytesPerCluster % info.BytesPerSector == 0 && info.BytesPerRecord is >= 512 and <= 4096 && info.MftValidLength > 0 && info.MftStartLcn > 0 ? info : null;
    }

    /// <summary>Reads records by number through the MFT's own extents (used for the names of the largest files).</summary>
    sealed class MftReader(RawDisk volume, VolumeInfo info, List<(long Lcn, long Clusters)> runs)
    {
        public string? NameOf(long record)
        {
            var position = record * info.BytesPerRecord;
            foreach (var (lcn, clusters) in runs)
            {
                var length = clusters * info.BytesPerCluster;
                if (position >= length) { position -= length; continue; }
                // A raw volume is read in whole sectors: the sectors that hold the record, then the record out of them.
                var start = position / info.BytesPerSector * info.BytesPerSector;
                var buffer = new byte[(position - start + info.BytesPerRecord + info.BytesPerSector - 1) / info.BytesPerSector * info.BytesPerSector];
                ReadAt(volume, lcn * info.BytesPerCluster + start, buffer, buffer.Length);
                var e = new Entry { Parent = -1, Size = -1 };
                ParseRecord(buffer.AsSpan((int)(position - start), info.BytesPerRecord), ref e, wantName: true);
                return e.Name;
            }
            return null;
        }
    }

    /// <summary>All records of the MFT by record number, and a reader for single records. Null when the MFT's own extents can't be read.</summary>
    static (Entry[] Entries, MftReader Reader)? ReadMft(RawDisk volume, VolumeInfo info, IProgress<(long Files, string Folder)>? progress, CancellationToken ct)
    {
        // Record 0 describes the MFT itself: its $DATA run list says where the table lies on the disk.
        var first = new byte[Math.Max(info.BytesPerSector, info.BytesPerRecord) / info.BytesPerSector * info.BytesPerSector];
        ReadAt(volume, info.MftStartLcn * info.BytesPerCluster, first, first.Length);
        var recordZero = first.AsSpan(0, info.BytesPerRecord).ToArray();
        if (!ApplyFixups(recordZero, 512) || FindMftRuns(recordZero) is not { Count: > 0 } runs) return null;

        // A record split across two extents (clusters smaller than a record) isn't handled: the folders are walked instead.
        if (runs.Any(r => r.Clusters * info.BytesPerCluster % info.BytesPerRecord != 0)) return null;
        var total = info.MftValidLength / info.BytesPerRecord;
        if (total <= RootRecord || total > int.MaxValue / 2) return null;
        var entries = new Entry[total];
        for (var i = 0; i < entries.Length; i++) { entries[i].Parent = -1; entries[i].Size = -1; }

        var chunk = new byte[ChunkBytes]; // a multiple of every sector and record size NTFS uses
        long record = 0;
        var lastReport = Environment.TickCount64;
        foreach (var (lcn, clusters) in runs)
        {
            var runBytes = clusters * info.BytesPerCluster;
            long done = 0;
            while (done < runBytes && record < total)
            {
                ct.ThrowIfCancellationRequested();
                // The records still to read, rounded up to whole sectors (a raw volume is read in sectors) but never past the run.
                var records = (int)Math.Min(chunk.Length / info.BytesPerRecord, Math.Min((runBytes - done) / info.BytesPerRecord, total - record));
                if (records <= 0) break;
                var read = (int)Math.Min(runBytes - done, (long)(records * info.BytesPerRecord + info.BytesPerSector - 1) / info.BytesPerSector * info.BytesPerSector);
                ReadAt(volume, lcn * info.BytesPerCluster + done, chunk, read);
                for (var off = 0; off < records * info.BytesPerRecord; off += info.BytesPerRecord, record++)
                    ParseRecord(chunk.AsSpan(off, info.BytesPerRecord), ref entries[record], wantName: false);
                done += records * info.BytesPerRecord;
                if (Environment.TickCount64 - lastReport > 200)
                {
                    lastReport = Environment.TickCount64;
                    progress?.Report((record, $"MFT {record:N0} / {total:N0}"));
                }
            }
        }
        return (entries, new MftReader(volume, info, runs));
    }

    /// <summary>A read at an offset of the volume handle (.NET's RandomAccess refuses a volume): seek, then read. Offsets and lengths are cluster multiples (sector-aligned).</summary>
    static void ReadAt(RawDisk disk, long offset, byte[] buffer, int count)
    {
        if (!SetFilePointerEx(disk.Data, disk.Base + offset, IntPtr.Zero, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), $"seek to {offset} failed");
        if (!ReadFile(disk.Data, buffer, count, out var read, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), $"reading {count} bytes at {offset} failed");
        if (read != count) throw new IOException("Short read of the MFT.");
    }

    // ---------- parsing (pure: tested with built records) ----------

    /// <summary>
    /// Puts the real bytes back at the end of each sector (NTFS stores the update sequence number there). False when this isn't a valid
    /// FILE record: a bad signature, or a sector whose end doesn't carry the sequence number (a torn or garbage record).
    /// </summary>
    internal static bool ApplyFixups(Span<byte> record, int sectorSize)
    {
        if (record.Length < 48 || record[0] != (byte)'F' || record[1] != (byte)'I' || record[2] != (byte)'L' || record[3] != (byte)'E') return false;
        var usaOffset = BitConverter.ToUInt16(record[4..]);
        var usaCount = BitConverter.ToUInt16(record[6..]);
        if (usaCount < 2 || usaOffset + usaCount * 2 > record.Length || (usaCount - 1) * sectorSize > record.Length) return false;
        var usn = BitConverter.ToUInt16(record[usaOffset..]);
        for (var i = 1; i < usaCount; i++)
        {
            var end = i * sectorSize - 2;
            if (BitConverter.ToUInt16(record[end..]) != usn) return false;
            record[end] = record[usaOffset + i * 2];
            record[end + 1] = record[usaOffset + i * 2 + 1];
        }
        return true;
    }

    /// <summary>
    /// Reads one record into <paramref name="e"/>. The name is decoded for a folder, an extension record or when asked for;
    /// a file's name isn't needed for the sizes, so it stays unread.
    /// </summary>
    internal static void ParseRecord(Span<byte> record, ref Entry e, bool wantName)
    {
        if (!ApplyFixups(record, 512)) return;
        var flags = BitConverter.ToUInt16(record[0x16..]);
        e.InUse = (flags & 1) != 0;
        e.IsDirectory = (flags & 2) != 0;
        if (!e.InUse) return;
        e.Base = (int)(BitConverter.ToUInt64(record[0x20..]) & 0x0000FFFFFFFFFFFF);
        wantName |= e.IsDirectory || e.Base != 0;
        var attr = (int)BitConverter.ToUInt16(record[0x14..]);
        while (attr + 8 <= record.Length)
        {
            var type = BitConverter.ToUInt32(record[attr..]);
            if (type == 0xFFFFFFFF) break;
            var length = BitConverter.ToInt32(record[(attr + 4)..]);
            if (length <= 0 || attr + length > record.Length) break;
            var nonResident = record[attr + 8] != 0;
            switch (type)
            {
                case 0x10 when !nonResident: // $STANDARD_INFORMATION
                    {
                        var c = attr + BitConverter.ToUInt16(record[(attr + 0x14)..]);
                        if (c + 36 <= attr + length && (BitConverter.ToUInt32(record[(c + 32)..]) & AttrReparse) != 0) e.Skip = true;
                        break;
                    }
                case 0x30 when !nonResident && !e.HasName: // $FILE_NAME: the first long name (the DOS 8.3 one, namespace 2, is skipped)
                    {
                        var c = attr + BitConverter.ToUInt16(record[(attr + 0x14)..]);
                        if (c + 0x42 > attr + length) break;
                        var chars = record[c + 0x40];
                        if (record[c + 0x41] == 2 || c + 0x42 + chars * 2 > attr + length) break;
                        e.Parent = (int)(BitConverter.ToUInt64(record[c..]) & 0x0000FFFFFFFFFFFF);
                        if (wantName) e.Name = Encoding.Unicode.GetString(record.Slice(c + 0x42, chars * 2));
                        e.HasName = true;
                        break;
                    }
                case 0x80 when record[attr + 9] == 0: // the unnamed $DATA
                    {
                        if (!nonResident) { e.Size = BitConverter.ToUInt32(record[(attr + 0x10)..]); e.HasSize = true; }
                        else if (BitConverter.ToInt64(record[(attr + 0x10)..]) == 0) { e.Size = BitConverter.ToInt64(record[(attr + 0x30)..]); e.HasSize = true; } // the first extent holds the real size
                        break;
                    }
            }
            attr += length;
        }
    }

    /// <summary>The run list of the unnamed $DATA of record 0 ($MFT): (first cluster, cluster count) per extent, in file order.</summary>
    internal static List<(long Lcn, long Clusters)>? FindMftRuns(ReadOnlySpan<byte> record)
    {
        var attr = (int)BitConverter.ToUInt16(record[0x14..]);
        while (attr + 8 <= record.Length)
        {
            var type = BitConverter.ToUInt32(record[attr..]);
            if (type == 0xFFFFFFFF) return null;
            var length = BitConverter.ToInt32(record[(attr + 4)..]);
            if (length <= 0 || attr + length > record.Length) return null;
            if (type == 0x80 && record[attr + 9] == 0 && record[attr + 8] != 0)
                return DecodeRuns(record[(attr + BitConverter.ToUInt16(record[(attr + 0x20)..]))..(attr + length)]);
            attr += length;
        }
        return null;
    }

    /// <summary>NTFS mapping pairs: a header byte (the size of the length in the low nibble, of the offset in the high), the two numbers; offsets are signed deltas.</summary>
    internal static List<(long Lcn, long Clusters)>? DecodeRuns(ReadOnlySpan<byte> data)
    {
        var runs = new List<(long, long)>();
        long lcn = 0;
        var i = 0;
        while (i < data.Length && data[i] != 0)
        {
            int lengthSize = data[i] & 0x0F, offsetSize = data[i] >> 4;
            i++;
            if (lengthSize is 0 or > 8 || offsetSize is 0 or > 8 || i + lengthSize + offsetSize > data.Length) return null; // a sparse run (no offset) doesn't occur in the MFT
            long clusters = 0, delta = 0;
            for (var b = 0; b < lengthSize; b++) clusters |= (long)data[i + b] << (8 * b);
            i += lengthSize;
            for (var b = 0; b < offsetSize; b++) delta |= (long)data[i + b] << (8 * b);
            if (offsetSize < 8 && (data[i + offsetSize - 1] & 0x80) != 0) delta -= 1L << (8 * offsetSize);
            i += offsetSize;
            lcn += delta;
            if (clusters <= 0 || lcn < 0) return null;
            runs.Add((lcn, clusters));
        }
        return runs;
    }

    // ---------- the tree ----------

    internal static ScanResult? Build(Entry[] entries, string volumeRoot, string startPath, Func<long, string?> nameOf, IProgress<(long Files, string Folder)>? progress)
    {
        // An extension record hands the name, size and flags it holds to its base record.
        for (var n = 0; n < entries.Length; n++)
        {
            ref var x = ref entries[n];
            if (!x.InUse || x.Base == 0 || x.Base >= entries.Length || !entries[x.Base].InUse) continue;
            ref var b = ref entries[x.Base];
            if (x.HasName && !b.HasName) { b.Name = x.Name; b.Parent = x.Parent; b.HasName = true; }
            if (x.HasSize && !b.HasSize) { b.Size = x.Size; b.HasSize = true; }
            b.Skip |= x.Skip;
        }

        // Folders under the root, by their parent. A folder the root doesn't reach (an orphan) is left out.
        var top = new DirNode(volumeRoot.TrimEnd('\\') + '\\', null);
        var children = new Dictionary<int, List<int>>();
        var folders = 0;
        for (var n = FirstUserRecord; n < entries.Length; n++)
        {
            ref var x = ref entries[n];
            if (!x.InUse || x.Base != 0 || !x.IsDirectory || !x.HasName || x.Skip || x.Name is null) continue;
            folders++;
            if (!children.TryGetValue(x.Parent, out var list)) children[x.Parent] = list = new List<int>();
            list.Add(n);
        }
        var nodes = new DirNode?[entries.Length];
        nodes[RootRecord] = top;
        var order = new List<DirNode> { top };
        var stack = new Stack<int>();
        stack.Push(RootRecord);
        while (stack.Count > 0)
        {
            var p = stack.Pop();
            if (!children.TryGetValue(p, out var list)) continue;
            foreach (var n in list)
            {
                var node = new DirNode(entries[n].Name!, nodes[p]);
                (nodes[p]!.Children ??= new()).Add(node);
                nodes[n] = node;
                order.Add(node);
                stack.Push(n);
            }
        }
        var skipped = folders - (order.Count - 1);

        // Files add into their folder. The largest list is chosen by record number first; names are read for the winners only.
        var largest = new PriorityQueue<(int Record, DirNode Folder), long>();
        long files = 0;
        var lastReport = Environment.TickCount64;
        for (var n = FirstUserRecord; n < entries.Length; n++)
        {
            ref var x = ref entries[n];
            if (!x.InUse || x.Base != 0 || x.IsDirectory || x.Skip || !x.HasName) continue;
            if (x.Parent < 0 || x.Parent >= nodes.Length || nodes[x.Parent] is not { } folder) { skipped++; continue; }
            var size = Math.Max(0, x.Size);
            folder.Size += size;
            folder.Files++;
            files++;
            if (largest.Count < DiskScanner.LargestCount) largest.Enqueue((n, folder), size);
            else if (largest.TryPeek(out _, out var smallest) && size > smallest)
            {
                largest.Dequeue();
                largest.Enqueue((n, folder), size);
            }
            if (Environment.TickCount64 - lastReport > 200) { lastReport = Environment.TickCount64; progress?.Report((files, folder.Path)); }
        }

        for (var i = order.Count - 1; i > 0; i--)
        {
            order[i].Parent!.Size += order[i].Size;
            order[i].Parent!.Files += order[i].Files;
        }
        foreach (var d in order) d.Children?.Sort((a, b) => b.Size.CompareTo(a.Size));

        // The folder asked for: down from the volume root by name; a junction on the way, or a folder made since the scan, lets the caller walk it.
        var start = top;
        var rest = Path.GetRelativePath(volumeRoot, startPath);
        if (rest != ".")
            foreach (var part in rest.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var next = start.Children?.FirstOrDefault(c => string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase));
                if (next is null) return null;
                start = next;
            }

        var winners = new List<LargeFile>();
        var prefix = start.Path.TrimEnd('\\') + '\\';
        while (largest.Count > 0)
        {
            largest.TryDequeue(out var item, out var size);
            if (nameOf(item.Record) is not { } name) continue;
            var path = Path.Combine(item.Folder.Path, name);
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) winners.Add(new LargeFile(path, size));
        }
        winners.Reverse();
        var root = rest == "." ? start : Detach(start, Path.GetFullPath(startPath));
        return new ScanResult(root, winners, root.Files, skipped);
    }

    /// <summary>A copy of a subtree as its own root (like the directory walk's result: nothing above it, its path the one asked for).</summary>
    static DirNode Detach(DirNode source, string path)
    {
        var copy = new DirNode(path, null) { Size = source.Size, Files = source.Files };
        var work = new Stack<(DirNode Source, DirNode Copy)>();
        work.Push((source, copy));
        while (work.Count > 0)
        {
            var (s, c) = work.Pop();
            if (s.Children is null) continue;
            c.Children = new List<DirNode>(s.Children.Count);
            foreach (var child in s.Children)
            {
                var n = new DirNode(child.Name, c) { Size = child.Size, Files = child.Files };
                c.Children.Add(n);
                work.Push((child, n));
            }
        }
        return copy;
    }

    // ---------- Win32 ----------

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFilePointerEx(SafeFileHandle file, long distance, IntPtr newPosition, uint method);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(SafeFileHandle file, [Out] byte[] buffer, int bytes, out int read, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
}
