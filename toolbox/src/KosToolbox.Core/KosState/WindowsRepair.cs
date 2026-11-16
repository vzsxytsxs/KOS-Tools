using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.Persistence;

namespace KosToolbox.Core.KosState;

/// <summary>Request to the elevated process: only which tool runs; its command line is fixed in <see cref="WindowsRepair"/>.</summary>
public sealed class RepairJob
{
    public string Tool { get; set; } = "";
    /// <summary>Identifies this run's result.</summary>
    public string RunId { get; set; } = "";
}

public sealed class RepairResult
{
    public string RunId { get; set; } = "";
    public string Tool { get; set; } = "";
    /// <summary>-1 when it ran past the time limit and was stopped.</summary>
    public int ExitCode { get; set; }
    /// <summary>What the tool printed after its last progress line: its verdict, and DISM's error.</summary>
    public List<string> Summary { get; set; } = new();
    public string? Error { get; set; }
}

/// <summary>Percent the tool printed last (null before the first) and its latest message.</summary>
public sealed record RepairProgress(double? Percent, string? Status);

/// <summary>
/// sfc /scannow, DISM /RestoreHealth and the component store (WinSxS) analysis and cleanup, each on its own, with the progress they print. Both need administrator: in the elevated
/// toolbox they run directly; otherwise the elevated process runs them and writes the progress and the result to an admin-only
/// folder, which the toolbox reads.
/// </summary>
public static class WindowsRepair
{
    public const string Sfc = "sfc", Dism = "dism", StoreAnalyze = "winsxs-analyze", StoreCleanup = "winsxs-cleanup", StoreResetBase = "winsxs-resetbase";
    static string Folder => Path.Combine(AppPaths.TheKosRoot, "Toolbox", "Repair");
    static string ProgressFile(string tool) => Path.Combine(Folder, $"{tool}.progress");
    static string ResultFile(string tool) => Path.Combine(Folder, $"{tool}-result.json");

    /// <summary>DISM's "source files could not be found or downloaded" errors: it repairs from Windows Update, which the KOS turns off.</summary>
    static readonly int[] SourceErrors = { unchecked((int)0x800f081f), unchecked((int)0x800f0906), unchecked((int)0x800f0907), unchecked((int)0x800f0950), unchecked((int)0x800f0954) };
    public static bool SourceMissing(RepairResult r) => r.Tool == Dism && SourceErrors.Contains(r.ExitCode);

    /// <summary>The fixed command of each tool, and how it writes its output (sfc writes UTF-16, even redirected).</summary>
    static (string Exe, string Arguments, Encoding Encoding) Command(string tool) => tool switch
    {
        Sfc => (Path.Combine(Environment.SystemDirectory, "sfc.exe"), "/scannow", Encoding.Unicode),
        Dism => (Path.Combine(Environment.SystemDirectory, "dism.exe"), "/Online /English /Cleanup-Image /RestoreHealth", Encoding.UTF8),
        StoreAnalyze => (Path.Combine(Environment.SystemDirectory, "dism.exe"), "/Online /English /Cleanup-Image /AnalyzeComponentStore", Encoding.UTF8),
        StoreCleanup => (Path.Combine(Environment.SystemDirectory, "dism.exe"), "/Online /English /Cleanup-Image /StartComponentCleanup", Encoding.UTF8),
        StoreResetBase => (Path.Combine(Environment.SystemDirectory, "dism.exe"), "/Online /English /Cleanup-Image /StartComponentCleanup /ResetBase", Encoding.UTF8),
        _ => throw new InvalidOperationException($"Unknown repair tool: {tool}"),
    };

    internal static void Validate(RepairJob job)
    {
        _ = Command(job.Tool);
        if (!Guid.TryParse(job.RunId, out _)) throw new InvalidOperationException("Invalid repair run id.");
    }

    /// <summary>Runs the tool as administrator; the result also goes to its file. Only preparing the protected folder throws.</summary>
    public static async Task<RepairResult> RunAsync(RepairJob job, IProgress<RepairProgress>? progress = null)
    {
        Validate(job);
        GpuTuner.PrepareDirectory(Path.GetDirectoryName(Folder)!);
        GpuTuner.PrepareDirectory(Folder);
        var (exe, args, encoding) = Command(job.Tool);
        var result = new RepairResult { RunId = job.RunId, Tool = job.Tool };
        var after = new List<string>();
        double? percent = null;
        string? status = null, written = null;
        // Called one line at a time (ProcessRunner serializes them).
        void Line(string raw)
        {
            var (p, text) = ParseLine(raw);
            if (p is null && text is null) return;
            if (p is not null) { percent = p; after.Clear(); }
            if (text is not null) { after.Add(text); status = text; }
            var now = new RepairProgress(percent, status);
            progress?.Report(now);
            var line = $"{percent?.ToString("F1", CultureInfo.InvariantCulture)}|{status}";
            if (line != written) { Report(job.Tool, line); written = line; }
        }
        try
        {
            Log.Info($"Windows repair: {Path.GetFileName(exe)} {args} started");
            var r = await ProcessRunner.RunAsync(exe, args, timeout: TimeSpan.FromHours(4), encoding: encoding, onLine: Line).ConfigureAwait(false);
            result.ExitCode = r.TimedOut ? -1 : r.ExitCode;
            result.Summary = after.ToList();
            Log.Info($"Windows repair: {job.Tool} finished, exit {result.ExitCode}: {string.Join(" ", result.Summary)}");
        }
        catch (Exception ex)
        {
            Log.Error($"Windows repair: {job.Tool} failed", ex);
            result.Error = ex.Message;
        }
        finally
        {
            try { File.Delete(ProgressFile(job.Tool)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        JsonFile.Save(ResultFile(job.Tool), result);
        return result;
    }

    static void Report(string tool, string line)
    {
        try
        {
            var tmp = ProgressFile(tool) + ".tmp";
            File.WriteAllText(tmp, line);
            File.Move(tmp, ProgressFile(tool), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // only shown: never stops the repair
    }

    /// <summary>The running tool's progress, read by the unelevated toolbox; null when none is running.</summary>
    public static RepairProgress? ReadProgress(string tool)
    {
        try
        {
            if (!File.Exists(ProgressFile(tool))) return null;
            var parts = File.ReadAllText(ProgressFile(tool)).Split('|', 2);
            return new RepairProgress(double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : null,
                parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The result of run <paramref name="runId"/>; null when the elevated process never got to write it.</summary>
    public static RepairResult? LoadResult(string tool, string runId) =>
        JsonFile.Load<RepairResult>(ResultFile(tool)) is { } r && r.RunId == runId ? r : null;

    static readonly Regex PercentRx = new(@"(\d{1,3}(?:[.,]\d+)?)\s?%", RegexOptions.CultureInvariant);

    /// <summary>
    /// One line of sfc or DISM: its percent ("Verification 42% complete.", "[====42.0%====]") and its text (DISM's after the bar),
    /// null text for a bare progress line, and (null, null) for an empty one.
    /// </summary>
    internal static (double? Percent, string? Text) ParseLine(string line)
    {
        var t = line.Trim('\0', ' ', '\t', '\r', '\n');
        if (t.Length == 0) return (null, null);
        var m = PercentRx.Match(t);
        if (!m.Success) return (null, t);
        var p = Math.Clamp(double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture), 0, 100);
        var end = t.LastIndexOf(']');
        var text = t.StartsWith('[') && end > 0 ? t[(end + 1)..].Trim() : "";
        return (p, text.Length > 0 ? text : null);
    }
}
