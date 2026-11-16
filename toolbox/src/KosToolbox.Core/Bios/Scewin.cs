using System.Globalization;
using System.Text.RegularExpressions;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Setup;
using KosToolbox.Core.Text;
using KosToolbox.Core.Tools;

namespace KosToolbox.Core.Bios;

public sealed class BiosChange
{
    /// <summary><see cref="BiosQuestion.Key"/> of the question.</summary>
    public string Key { get; set; } = "";
    /// <summary>Value in the script format: option hex, number decimal, checkbox 0/1.</summary>
    public string Value { get; set; } = "";
}

/// <summary>Request to the elevated process: read the BIOS or write changes, revalidated against a fresh BIOS read.</summary>
public sealed class BiosJob
{
    public const string Read = "read", Apply = "apply";
    public string Mode { get; set; } = "";
    /// <summary>Identifies the result in <see cref="Scewin.ResultFile"/>.</summary>
    public string RunId { get; set; } = "";
    /// <summary>HIICrc32 of the script the changes were made on; a BIOS with another layout (updated) refuses.</summary>
    public string? Crc { get; set; }
    public List<BiosChange> Changes { get; set; } = new();
}

/// <summary>Result of a run, written to the protected folder (the unelevated side only reads).</summary>
public sealed class BiosResult
{
    public string RunId { get; set; } = "";
    public string? Error { get; set; }
    /// <summary>Backup saved before writing; null if nothing was written.</summary>
    public string? Backup { get; set; }
    /// <summary>Warnings and errors SCEWIN wrote during the import.</summary>
    public List<string> Messages { get; set; } = new();
}

/// <summary>Backup data, in the .json next to the .txt (which is the SCEWIN script, unchanged).</summary>
public sealed class BiosBackupMeta
{
    public DateTime CapturedAtUtc { get; set; }
    public DateTime BootedAtUtc { get; set; }
    public string? Computer { get; set; }
    /// <summary>Questions written right after this backup.</summary>
    public List<string> Changed { get; set; } = new();
}

/// <summary>Backup in the folder. <see cref="NotBooted"/>: taken after another write in the same boot, holds values the PC has not used yet.</summary>
public sealed record BiosBackupInfo(string Name, DateTime CapturedAt, string? Computer, IReadOnlyList<string> Changed, int? Count, string? Crc,
    bool NotBooted, string? Problem)
{
    public string ChangedText => Txt.BiosBackupChanged(Changed);
}

/// <summary>
/// AMI SCEWIN, from the KOS tools folder. Reading (/o) and writing (/i) require administrator: SCEWIN loads its signed driver.
/// Everything lives in %ProgramData%\TheKOS\Toolbox\Bios, writable only by administrators: the nvram.txt from the last read,
/// the backups (the state read before each write) and the copy of SCEWIN that actually runs.
/// </summary>
public static class Scewin
{
    public const string Exe = "SCEWIN_64.exe";
    static readonly string[] Files = { Exe, "amifldrv64.sys", "amigendrv64.sys" };
    // \z, not $: $ accepts a "\n" at the end.
    static readonly Regex KeyText = new(@"^[0-9A-F]{1,16}/[0-9A-F]{1,16}/[0-9A-F]{1,16}\z");
    static readonly Regex ValueText = new(@"^-?[0-9A-Fa-f]{1,20}\z");
    static readonly Regex CrcText = new(@"^[0-9A-Fa-f]{1,8}\z");

    public static string Folder => Path.Combine(AppPaths.TheKosRoot, "Toolbox", "Bios");
    public static string CurrentFile => Path.Combine(Folder, "nvram.txt");
    public static string BackupFolder => Path.Combine(Folder, "Backups");
    public static string ResultFile => Path.Combine(Folder, "last-result.json");
    static string WorkFolder => Path.Combine(Folder, "SCEWIN");

    /// <summary>SCEWIN_64.exe in the tools folder (at the root or in a subfolder, like tools\SCEWIN); null if absent.</summary>
    public static string? Find()
    {
        var root = ToolDiscovery.Root;
        try
        {
            if (!Directory.Exists(root)) return null;
            var direct = Path.Combine(root, Exe);
            if (File.Exists(direct)) return direct;
            return Directory.EnumerateDirectories(root).Select(d => Path.Combine(d, Exe)).Where(File.Exists)
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // ---------- elevated process ----------

    /// <summary>Refuses anything that is not exactly a well-formed request; values are revalidated against the fresh read.</summary>
    internal static void Validate(BiosJob job)
    {
        if (!Guid.TryParse(job.RunId, out _)) throw new InvalidOperationException("Invalid BIOS run id.");
        switch (job.Mode)
        {
            case BiosJob.Read:
                if (job.Crc is not null || job.Changes.Count > 0) throw new InvalidOperationException("Reading takes no changes.");
                break;
            case BiosJob.Apply:
                if (job.Crc is null || !CrcText.IsMatch(job.Crc)) throw new InvalidOperationException("Invalid HIICrc32.");
                if (job.Changes.Count is 0 or > 5000) throw new InvalidOperationException("Invalid number of BIOS changes.");
                if (job.Changes.Any(c => !KeyText.IsMatch(c.Key) || !ValueText.IsMatch(c.Value))) throw new InvalidOperationException("Invalid BIOS change.");
                if (job.Changes.Select(c => c.Key).Distinct().Count() != job.Changes.Count) throw new InvalidOperationException("Repeated BIOS setting.");
                break;
            default:
                throw new InvalidOperationException($"Unknown BIOS mode: {job.Mode}");
        }
    }

    /// <summary>
    /// Runs as administrator (in the elevated process, or in the elevated toolbox itself) and writes the result to
    /// <see cref="ResultFile"/>. Only preparing the protected folders throws: without them there is nowhere to write.
    /// </summary>
    public static async Task<BiosResult> RunAsync(BiosJob job, CancellationToken ct = default)
    {
        foreach (var dir in new[] { Path.GetDirectoryName(Folder)!, Folder, WorkFolder, BackupFolder }) GpuTuner.PrepareDirectory(dir);
        var result = new BiosResult { RunId = job.RunId };
        try
        {
            CopyTool();
            if (job.Mode == BiosJob.Read)
            {
                var (path, script, _) = await ExportAsync("", ct).ConfigureAwait(false);
                File.Move(path, CurrentFile, overwrite: true);
                Log.Info($"BIOS read: {script.Questions.Count} settings (HIICrc32 {script.Crc})");
            }
            else await ApplyAsync(job, result, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error($"BIOS {job.Mode} failed", ex);
            result.Error = ex.Message;
        }
        JsonFile.Save(ResultFile, result);
        return result;
    }

    /// <summary>Result of run <paramref name="runId"/>; null if the elevated process never got to write.</summary>
    public static BiosResult? LoadResult(string runId) => JsonFile.Load<BiosResult>(ResultFile) is { } r && r.RunId == runId ? r : null;

    /// <summary>
    /// Null if the three files sit next to <paramref name="exe"/>, without links and owned by SYSTEM/Administrators, as
    /// the toolbox requires to open a program from the tools folder elevated. The signature alone is not enough: a regular user could
    /// put an old, vulnerable, genuinely signed AMI driver there.
    /// </summary>
    public static string? SourceProblem(string exe)
    {
        foreach (var f in Files)
        {
            var path = Path.Combine(Path.GetDirectoryName(exe)!, f);
            if (!File.Exists(path)) return Txt.ScewinFileMissing(path);
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return Txt.ScewinIsLink(path);
            if (ToolDiscovery.OwnerProblem(path) is { } problem) return problem;
        }
        return null;
    }

    /// <summary>
    /// Runs a copy in the protected folder, checked against the AMI signature after being copied: nobody swaps the file between
    /// the check and the run, nor puts a DLL next to it.
    /// </summary>
    static void CopyTool()
    {
        var source = Find() ?? throw new FileNotFoundException(Txt.ScewinMissing(ToolDiscovery.Root));
        if (SourceProblem(source) is { } problem) throw new InvalidOperationException(problem);
        var dir = Path.GetDirectoryName(source)!;
        foreach (var f in Files)
        {
            var from = Path.Combine(dir, f);
            var to = Path.Combine(WorkFolder, f);
            File.Copy(from, to, overwrite: true);
            if (Installer.Signer(to) is not { } signer || !(signer.Contains("American Megatrends", StringComparison.OrdinalIgnoreCase)
                    || signer.StartsWith("AMI ", StringComparison.OrdinalIgnoreCase)))
            {
                File.Delete(to);
                throw new InvalidOperationException(Txt.ScewinNotSigned(from));
            }
        }
    }

    static async Task ApplyAsync(BiosJob job, BiosResult result, CancellationToken ct)
    {
        var (beforePath, before, flags) = await ExportAsync("", ct).ConfigureAwait(false);
        File.Copy(beforePath, CurrentFile, overwrite: true); // the page now shows the BIOS as it is right now
        if (!string.Equals(before.Crc, job.Crc, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(Txt.BiosLayoutChanged);
        var changes = new Dictionary<string, string>();
        foreach (var c in job.Changes)
        {
            var q = before.Find(c.Key) ?? throw new InvalidOperationException(Txt.BiosQuestionMissing);
            if (NvramScript.Check(q, c.Value) is { } problem) throw new InvalidOperationException(problem);
            changes[q.Key] = c.Value;
        }
        // The state in use goes to the backups before any write.
        var backup = result.Backup = SaveBackup(beforePath, changes.Keys.Select(k => before.Find(k)!.Name));
        File.WriteAllText(Path.Combine(WorkFolder, "import.txt"), before.ImportScript(changes), NvramScript.Encoding);
        var r = await RunScewinAsync("/i /s import.txt" + flags, ct).ConfigureAwait(false);
        result.Messages = Messages(r.Output);
        if (r.TimedOut) result.Messages.Insert(0, Txt.ScewinTimedOut);
        // What confirms is the re-read: SCEWIN's exit code does not say which questions it wrote.
        var (afterPath, after, _) = await ExportAsync(flags, ct).ConfigureAwait(false);
        File.Move(afterPath, CurrentFile, overwrite: true);
        if (NvramScript.Compare(before, after).Count == 0)
        {
            // Nothing changed: the backup would equal the current state.
            foreach (var ext in new[] { ".txt", ".json" }) File.Delete(Path.Combine(BackupFolder, backup + ext));
            result.Backup = null;
            Log.Warn($"BIOS apply: nothing changed after the import; backup {backup} discarded");
        }
        else Log.Info($"BIOS apply: {changes.Count} settings requested, backup {backup}");
    }

    /// <summary>
    /// Exports to nvram.txt in the working folder (the name goes into the file header, as in Export.bat).
    /// Returns the path, the script and the options used.
    /// </summary>
    static async Task<(string Path, NvramScript Script, string Flags)> ExportAsync(string flags, CancellationToken ct)
    {
        var path = Path.Combine(WorkFolder, "nvram.txt");
        File.Delete(path);
        var r = await RunScewinAsync("/o /s nvram.txt" + flags, ct).ConfigureAwait(false);
        // Old Aptio: "Platform identification failed"; /d skips that identification (SCEHUB) and also applies to writing.
        if (flags.Length == 0 && r.Output.Contains("Platform identification failed", StringComparison.OrdinalIgnoreCase))
            return await ExportAsync(" /d", ct).ConfigureAwait(false);
        if (r.TimedOut) throw new TimeoutException(Txt.ScewinTimedOut);
        if (r.ExitCode != 0 || !File.Exists(path)) throw new InvalidOperationException(Txt.ScewinFailed(r.ExitCode, Explain(r.Output)));
        var script = NvramScript.Load(path);
        if (script.Crc is null) throw new InvalidOperationException(Txt.ScewinInvalidExport);
        return (path, script, flags);
    }

    static async Task<ProcessResult> RunScewinAsync(string arguments, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(Path.Combine(WorkFolder, Exe), arguments, WorkFolder, timeout: TimeSpan.FromMinutes(3), ct: ct)
            .ConfigureAwait(false);
        Log.Info($"SCEWIN {arguments}: exit code {r.ExitCode}{(r.TimedOut ? " (timed out)" : "")}. {string.Join(" | ", Messages(r.Output))}");
        return r;
    }

    /// <summary>Warnings and errors from the SCEWIN output, without the banner and without the duplicates warning (which it resolves itself).</summary>
    internal static List<string> Messages(string output) => output.Split('\n').Select(l => l.Trim())
        .Where(l => Regex.IsMatch(l, "warn|error|unable|fail|not ", RegexOptions.IgnoreCase) && !l.StartsWith('|')
            && !l.Contains("Duplicate questions found", StringComparison.OrdinalIgnoreCase))
        .Distinct().Take(20).ToList();

    /// <summary>SCEWIN messages and, for known errors, what to do.</summary>
    static string Explain(string output)
    {
        var text = string.Join(" ", Messages(output));
        if (Regex.IsMatch(output, "unable to load driver|LoadDeviceDriver|DRIVER LOADING FAILED", RegexOptions.IgnoreCase)) text += " " + Txt.ScewinDriverHint;
        else if (output.Contains("not compatible", StringComparison.OrdinalIgnoreCase)) text += " " + Txt.ScewinNotCompatibleHint;
        return text.Trim();
    }

    // ---------- backups ----------

    static string SaveBackup(string scriptPath, IEnumerable<string> changed)
    {
        var stamp = "BIOS." + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var name = stamp;
        for (var i = 2; File.Exists(Path.Combine(BackupFolder, name + ".txt")); i++) name = $"{stamp}-{i}";
        File.Copy(scriptPath, Path.Combine(BackupFolder, name + ".txt"));
        JsonFile.Save(Path.Combine(BackupFolder, name + ".json"), new BiosBackupMeta
        {
            CapturedAtUtc = DateTime.UtcNow, BootedAtUtc = PendingRebootStore.BootTimeUtc, Computer = Environment.MachineName, Changed = changed.ToList(),
        });
        return name;
    }

    /// <summary>Backups from newest to oldest.</summary>
    public static List<BiosBackupInfo> Backups()
    {
        var list = new List<BiosBackupInfo>();
        if (!Directory.Exists(BackupFolder)) return list;
        var files = new DirectoryInfo(BackupFolder).GetFiles("*.txt");
        var metas = files.Select(f => JsonFile.Load<BiosBackupMeta>(Path.ChangeExtension(f.FullName, ".json"))).ToList();
        for (var i = 0; i < files.Length; i++)
            if (metas[i].CapturedAtUtc == default) metas[i].CapturedAtUtc = files[i].LastWriteTimeUtc;
        for (var i = 0; i < files.Length; i++)
        {
            var name = Path.GetFileNameWithoutExtension(files[i].Name);
            var meta = metas[i];
            var notBooted = NotBooted(meta, metas);
            try
            {
                var s = NvramScript.Load(files[i].FullName);
                list.Add(new BiosBackupInfo(name, meta.CapturedAtUtc.ToLocalTime(), meta.Computer, meta.Changed, s.Questions.Count, s.Crc, notBooted, null));
            }
            catch (Exception ex) { list.Add(new BiosBackupInfo(name, meta.CapturedAtUtc.ToLocalTime(), meta.Computer, meta.Changed, null, null, notBooted, ex.Message)); }
        }
        return list.OrderByDescending(b => b.CapturedAt).ToList();
    }

    /// <summary>Another write between the boot and this backup: its values have not been through a boot yet.</summary>
    internal static bool NotBooted(BiosBackupMeta backup, IEnumerable<BiosBackupMeta> all) =>
        backup.BootedAtUtc != default && all.Any(o => o.CapturedAtUtc > backup.BootedAtUtc && o.CapturedAtUtc < backup.CapturedAtUtc);

    public static NvramScript LoadBackup(string name)
    {
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("..")) throw new ArgumentException(name);
        return NvramScript.Load(Path.Combine(BackupFolder, name + ".txt"));
    }
}
