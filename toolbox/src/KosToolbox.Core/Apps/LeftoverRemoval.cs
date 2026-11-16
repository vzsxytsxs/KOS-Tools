using System.Text;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Services;
using KosToolbox.Core.Startup;
using KosToolbox.Core.Text;
using Microsoft.Win32;

namespace KosToolbox.Core.Apps;

public sealed record LeftoverFailure(Leftover Item, string Error);

/// <summary>Removed (confirmed by re-reading), kept (vendor folder or key that was not empty), failures and the backup folder.</summary>
public sealed record LeftoverResult(int Removed, int Kept, IReadOnlyList<LeftoverFailure> Failed, string? BackupFolder);

public static partial class Leftovers
{
    static string BackupRoot => Path.Combine(AppPaths.DataRoot, "leftovers-removed");

    /// <summary>
    /// Deletes the checked items. Files and folders are deleted permanently (no Recycle Bin, as the maintainer chose). Before deleting
    /// anything, keys and values go to .reg, services to .reg and tasks to .xml in
    /// %LocalAppData%\KOS\Toolbox\leftovers-removed\. Services and tasks go first (they may be using the files);
    /// the vendor folder and key are only removed when empty. Each item is confirmed by re-reading.
    /// </summary>
    public static LeftoverResult Remove(string programName, IReadOnlyList<Leftover> items)
    {
        var backup = Path.Combine(BackupRoot, $"{DateTime.Now:yyyyMMdd-HHmmss}-{StartupRemoval.Safe(programName)}");
        Directory.CreateDirectory(backup);
        var failed = new List<LeftoverFailure>();
        var todo = new List<Leftover>();
        foreach (var i in items)
            if (CannotRemove(i) is { } why) failed.Add(new(i, why)); else todo.Add(i);
        // Without a backup, the key or value is not removed.
        todo = BackupRegistry(todo, backup, failed);

        int removed = 0, kept = 0;
        foreach (var item in Order(todo))
        {
            try
            {
                switch (Delete(item, backup))
                {
                    case true: removed++; Log.Info($"Leftovers of {programName}: removed {Describe(item)}"); break;
                    case false: kept++; break;
                    default: throw new InvalidOperationException(Txt.ChangeNotConfirmed);
                }
            }
            catch (Exception ex)
            {
                failed.Add(new(item, ex.Message));
                Log.Warn($"Leftovers of {programName}: {Describe(item)} not removed: {ex.Message}");
            }
        }
        // With no registry entry, service or task, there was nothing to save.
        var used = Directory.EnumerateFileSystemEntries(backup).Any();
        if (!used) Directory.Delete(backup);
        return new LeftoverResult(removed, kept, failed, used ? backup : null);
    }

    static string Describe(Leftover i) => i.ValueName is null ? $"{i.Kind} {i.Path}" : $"{i.Kind} {i.Path} | {i.ValueName}";

    /// <summary>Services, tasks, files, folders (deepest first), folders removed only when empty, values, keys, keys removed only when empty.</summary>
    static IEnumerable<Leftover> Order(IEnumerable<Leftover> items) => items
        .OrderBy(i => i.Kind switch
        {
            LeftoverKind.Service => 0, LeftoverKind.Task => 1, LeftoverKind.File => 2,
            LeftoverKind.Folder => i.OnlyIfEmpty ? 4 : 3, LeftoverKind.RegistryValue => 5, _ => i.OnlyIfEmpty ? 7 : 6,
        })
        .ThenByDescending(i => i.Path.Length);

    /// <summary>Exports keys (one .reg each) and values (values.reg). Returns the items that can proceed.</summary>
    static List<Leftover> BackupRegistry(List<Leftover> items, string folder, List<LeftoverFailure> failed)
    {
        var values = new StringBuilder();
        var next = new List<Leftover>();
        var n = 0;
        foreach (var i in items)
        {
            if (i.Kind is not (LeftoverKind.RegistryKey or LeftoverKind.RegistryValue)) { next.Add(i); continue; }
            try
            {
                var (hive, sub) = SplitKey(i.Path) ?? throw new InvalidOperationException(Txt.LeftoverNotAllowed);
                if (i.Kind == LeftoverKind.RegistryKey)
                {
                    if (!KeyExists(i.Path)) { next.Add(i); continue; } // already gone; the confirmation counts it
                    using var w = new StreamWriter(Path.Combine(folder, $"key-{++n:000}.reg"), false, new UnicodeEncoding(false, true));
                    RegExport.Export(hive, sub, w);
                }
                else
                {
                    using var key = OpenKey(i.Path);
                    if (key is not null && RegExport.ValueLine(key, i.ValueName!) is { } line)
                        values.Append("\r\n[").Append(RegExport.HiveName(hive)).Append('\\').Append(sub).Append("]\r\n").Append(line).Append("\r\n");
                }
                next.Add(i);
            }
            catch (Exception ex) { failed.Add(new(i, Txt.LeftoverBackupFailed(ex.Message))); }
        }
        if (values.Length > 0) File.WriteAllText(Path.Combine(folder, "values.reg"), "Windows Registry Editor Version 5.00\r\n" + values, new UnicodeEncoding(false, true));
        return next;
    }

    /// <summary>true = removed (confirmed); false = kept (not empty); null = still there.</summary>
    static bool? Delete(Leftover i, string backup)
    {
        switch (i.Kind)
        {
            case LeftoverKind.Service:
                StartupRemoval.RemoveService(i.Path, backup); // throws if sc delete fails
                return true;
            case LeftoverKind.Task:
                StartupRemoval.RemoveTask(i.Path, StartupRemoval.Safe(i.Path.TrimStart('\\').Replace('\\', '_')), backup);
                return true;
            case LeftoverKind.File:
                if (File.Exists(i.Path))
                {
                    var f = new FileInfo(i.Path);
                    if (f.Attributes.HasFlag(FileAttributes.ReadOnly)) f.Attributes &= ~FileAttributes.ReadOnly;
                    f.Delete();
                }
                return File.Exists(i.Path) ? null : true;
            case LeftoverKind.Folder:
                if (!Directory.Exists(i.Path)) return true;
                if (new DirectoryInfo(i.Path).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException(Txt.LeftoverNotAllowed);
                if (i.OnlyIfEmpty)
                {
                    if (Directory.EnumerateFileSystemEntries(i.Path).Any()) return false;
                    Directory.Delete(i.Path);
                    return !Directory.Exists(i.Path);
                }
                if (DeleteTree(i.Path) is { } error && Directory.Exists(i.Path)) throw new IOException(Txt.LeftoverFolderPartly(error));
                return Directory.Exists(i.Path) ? null : true;
            case LeftoverKind.RegistryValue:
                using (var key = OpenKey(i.Path, writable: true))
                {
                    key?.DeleteValue(i.ValueName!, throwOnMissingValue: false);
                    return key?.GetValue(i.ValueName!) is null ? true : null;
                }
            default:
                var (hive, sub) = SplitKey(i.Path)!.Value;
                using (var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
                {
                    if (i.OnlyIfEmpty)
                    {
                        using (var k = root.OpenSubKey(sub))
                        {
                            if (k is null) return true;
                            if (k.SubKeyCount > 0 || k.ValueCount > 0) return false;
                        }
                        root.DeleteSubKey(sub, throwOnMissingSubKey: false);
                    }
                    else root.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
                }
                return KeyExists(i.Path) ? null : true;
        }
    }

    /// <summary>
    /// Deletes the folder without following links (a link is removed on its own, the target stays) and clearing read-only. Files in use
    /// stay; returns the first error.
    /// </summary>
    internal static string? DeleteTree(string path)
    {
        string? error = null;
        void Try(Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error ??= ex.Message; }
        }
        void Walk(DirectoryInfo dir)
        {
            dir.Refresh(); // enumeration caches the attributes: a folder swapped for a link meanwhile must not be walked
            if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) { Try(dir.Delete); return; }
            List<FileSystemInfo> entries;
            try { entries = dir.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error ??= ex.Message; return; }
            foreach (var e in entries)
            {
                if (e is DirectoryInfo d) Walk(d);
                else Try(() => { if (e.Attributes.HasFlag(FileAttributes.ReadOnly)) e.Attributes &= ~FileAttributes.ReadOnly; e.Delete(); });
            }
            Try(() => { if (dir.Attributes.HasFlag(FileAttributes.ReadOnly)) dir.Attributes &= ~FileAttributes.ReadOnly; dir.Delete(); });
        }
        Walk(new DirectoryInfo(path));
        return error;
    }
}
