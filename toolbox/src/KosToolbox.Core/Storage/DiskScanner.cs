using System.Security;

namespace KosToolbox.Core.Storage;

/// <summary>A folder in the scan. Stores only the name (the path comes from the parent chain) to fit millions of folders.</summary>
public sealed class DirNode(string name, DirNode? parent)
{
    public string Name { get; } = name;
    public DirNode? Parent { get; } = parent;
    public long Size { get; internal set; }
    public long Files { get; internal set; }
    public List<DirNode>? Children { get; internal set; }
    public string Path => Parent is null ? Name : System.IO.Path.Combine(Parent.Path, Name);
}

public sealed record LargeFile(string Path, long Size);

public sealed record ScanResult(DirNode Root, IReadOnlyList<LargeFile> Largest, long Files, int Skipped);

/// <summary>
/// Size of each folder of a disk or folder (like WizTree/TreeSize, but through normal file system
/// enumeration). Doesn't follow junctions or symbolic links and doesn't count cloud files that aren't on disk.
/// </summary>
public static class DiskScanner
{
    public const int LargestCount = 100;

    static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false, // we want to count what couldn't be read
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    public static ScanResult Scan(string root, IProgress<(long Files, string Folder)>? progress = null, CancellationToken ct = default)
    {
        var top = new DirNode(Path.GetFullPath(root), null);
        var order = new List<DirNode>();
        var stack = new Stack<DirNode>();
        stack.Push(top);
        var largest = new PriorityQueue<LargeFile, long>();
        long files = 0;
        var skipped = 0;
        var lastReport = Environment.TickCount64;

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var node = stack.Pop();
            order.Add(node);
            var path = node.Path;
            try
            {
                foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", Options))
                {
                    if (entry is DirectoryInfo)
                    {
                        var child = new DirNode(entry.Name, node);
                        (node.Children ??= new()).Add(child);
                        stack.Push(child);
                        continue;
                    }
                    var size = ((FileInfo)entry).Length;
                    node.Size += size;
                    node.Files++;
                    files++;
                    largest.Enqueue(new LargeFile(entry.FullName, size), size);
                    if (largest.Count > LargestCount) largest.Dequeue();
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
            {
                skipped++;
            }
            if (Environment.TickCount64 - lastReport > 200)
            {
                lastReport = Environment.TickCount64;
                progress?.Report((files, path));
            }
        }

        // Post-order: each folder adds into its parent after its children (the list is in pre-order).
        for (var i = order.Count - 1; i > 0; i--)
        {
            var n = order[i];
            n.Parent!.Size += n.Size;
            n.Parent.Files += n.Files;
        }
        foreach (var n in order) n.Children?.Sort((a, b) => b.Size.CompareTo(a.Size));

        var list = new List<LargeFile>(largest.Count);
        while (largest.Count > 0) list.Add(largest.Dequeue());
        list.Reverse();
        return new ScanResult(top, list, files, skipped);
    }
}
