using System.Security.AccessControl;
using System.Security.Principal;
using KosToolbox.Core.Execution;

namespace KosToolbox.Core.Persistence;

/// <summary>
/// Daily log in %ProgramData%\TheKOS\Logs\Toolbox, shared by the normal and the elevated process.
/// Size-based rotation and age/count-based retention. If writing fails, the destination doesn't change:
/// <see cref="Problem"/> describes the failure so the UI can report it.
/// </summary>
public static class Log
{
    public const long MaxFileBytes = 5 * 1024 * 1024;
    public const int RetentionDays = 30, MaxFiles = 60;

    static readonly object Gate = new();
    static string? _problem;

    /// <summary>Null when the log is being written; otherwise the reason, in English.</summary>
    public static string? Problem { get => _problem; private set { _problem = value; ProblemChanged?.Invoke(); } }
    public static event Action? ProblemChanged;

    public static string Directory => AppPaths.LogsDirectory;
    public static string CurrentFile => FileFor(DateTime.Now, 0);

    static string FileFor(DateTime day, int part) =>
        Path.Combine(Directory, part == 0 ? $"toolbox-{day:yyyyMMdd}.log" : $"toolbox-{day:yyyyMMdd}.{part}.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    /// <summary>
    /// Creates Logs\Toolbox if missing and, as administrator, gives users modify permission on that folder only
    /// (the elevated and the normal process write the same files). Then applies retention.
    /// </summary>
    public static void Initialize()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            if (Elevation.IsAdmin) GrantUsersModify(Directory);
            Prune(System.IO.Directory.GetFiles(Directory, "toolbox-*.log"), DateTime.Now);
        }
        catch (Exception ex) { Problem = $"The log folder {Directory} can't be prepared: {ex.Message}"; }
    }

    static void GrantUsersModify(string dir)
    {
        var info = new DirectoryInfo(dir);
        var acl = info.GetAccessControl();
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var rule = new FileSystemAccessRule(users, FileSystemRights.Modify | FileSystemRights.Synchronize,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow);
        if (acl.GetAccessRules(true, false, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
            .Any(r => r.IdentityReference == users && r.AccessControlType == AccessControlType.Allow && (r.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify
                      && r.InheritanceFlags == rule.InheritanceFlags)) return;
        acl.AddAccessRule(rule);
        info.SetAccessControl(acl);
    }

    /// <summary>Removes logs older than <see cref="RetentionDays"/> and keeps at most <see cref="MaxFiles"/>.</summary>
    internal static List<string> Prune(IEnumerable<string> files, DateTime now)
    {
        var ordered = files.Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTime).ToList();
        var remove = ordered.Where((f, i) => i >= MaxFiles || f.LastWriteTime < now.AddDays(-RetentionDays)).Select(f => f.FullName).ToList();
        foreach (var f in remove) { try { File.Delete(f); } catch { /* in use by the other process; tries again next time */ } }
        return remove;
    }

    /// <summary>Today's file that still has room for the line; past <see cref="MaxFileBytes"/>, opens the next part.</summary>
    internal static string Target(DateTime now, Func<string, long> sizeOf)
    {
        for (var part = 0; ; part++)
        {
            var f = FileFor(now, part);
            if (sizeOf(f) < MaxFileBytes) return f;
        }
    }

    static long SizeOf(string f) { var i = new FileInfo(f); return i.Exists ? i.Length : 0; }

    static void Write(string level, string message)
    {
        var now = DateTime.Now;
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [pid {Environment.ProcessId}{(Elevation.IsAdmin ? ", admin" : "")}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            Exception? last = null;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    File.AppendAllText(Target(now, SizeOf), line);
                    if (_problem is not null) Problem = null;
                    return;
                }
                catch (IOException ex) { last = ex; Thread.Sleep(30); } // the other process is writing
                catch (UnauthorizedAccessException ex) { last = ex; break; }
            }
            var text = $"The toolbox can't write its log to {Directory}: {last?.Message}";
            if (_problem != text) Problem = text;
        }
    }
}
