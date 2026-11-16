using System.Globalization;
using System.Text.RegularExpressions;

namespace KosToolbox.Core.SystemInfo;

/// <summary>A warning or error from runonce.log, with the explanation of the KOS code.</summary>
public sealed record PostSetupIssue(string Time, string Event, bool IsError, string Detail, int? NativeExit, int? KosCode, string Explanation)
{
    public string Meta => string.Join(" · ", new[] { Event, KosCode is { } c ? $"KOS code {c}" : null, Time.Length > 0 ? Time : null }.Where(s => s is not null));
}

/// <summary>Last run of the KOS RunOnce (status.txt and runonce.log).</summary>
public sealed record PostSetupReport(string Status, int Warnings, int Errors, DateTime? Started, string Folder, IReadOnlyList<PostSetupIssue> Issues)
{
    public bool HasProblems => Issues.Count > 0 || Status != "COMPLETED";

    public string StatusText => Status switch
    {
        "COMPLETED" => "Completed",
        "COMPLETED_WITH_WARNINGS" => "Completed with warnings",
        "COMPLETED_WITH_ERRORS" => "Completed with errors",
        "INCOMPLETE" => "Didn't finish (the setup stopped before the restart request)",
        var s => s,
    };

    public string Counts => $"{Warnings} warning(s), {Errors} error(s)" + (Started is { } t ? $" · run on {t:g}" : "");
    public string Summary => $"{StatusText} · {Counts}";
}

/// <summary>
/// Post-install logs (RunOnce.cmd): one RunOnce-yyyyMMdd-HHmmss-fff folder per run in
/// %ProgramData%\TheKOS\Logs\PostSetup, with status.txt and lines "event=… status=… native_exit=… kos_code=… detail=…".
/// The codes follow KOS's Setup\3-Support\PostSetup-Logs.txt.
/// </summary>
public static class PostSetupLog
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"TheKOS\Logs\PostSetup");

    public static PostSetupReport? Latest()
    {
        if (!Directory.Exists(Root)) return null;
        var folder = Directory.GetDirectories(Root, "RunOnce-*").OrderBy(d => d, StringComparer.Ordinal).LastOrDefault();
        if (folder is null) return null;
        var status = Path.Combine(folder, "status.txt");
        var log = Path.Combine(folder, "runonce.log");
        return Parse(File.Exists(status) ? File.ReadAllText(status) : "INCOMPLETE",
            File.Exists(log) ? File.ReadAllLines(log) : Array.Empty<string>(), folder);
    }

    static readonly Regex Line = new(@"^\[(?<time>[^\]]*)\]\s+event=(?<event>\S+)\s+status=(?<status>\S+)\s+native_exit=(?<native>\S*)\s+kos_code=(?<code>\S*)\s+detail=(?<detail>.*)$");
    static readonly Regex Counts = new(@"^(?<status>\S+)(?:\s+warnings=(?<w>\d+))?(?:\s+errors=(?<e>\d+))?");

    internal static PostSetupReport Parse(string status, IEnumerable<string> lines, string folder)
    {
        var c = Counts.Match(status.Trim());
        var issues = new List<PostSetupIssue>();
        foreach (var line in lines)
        {
            var m = Line.Match(line.Trim());
            if (!m.Success || m.Groups["status"].Value.ToUpperInvariant() is not ("WARN" or "FAIL" or "ERROR")) continue;
            int? Int(string g) => int.TryParse(m.Groups[g].Value, out var v) ? v : null;
            var (native, code, detail) = (Int("native"), Int("code"), m.Groups["detail"].Value.Trim());
            issues.Add(new PostSetupIssue(m.Groups["time"].Value.Trim(), m.Groups["event"].Value, m.Groups["status"].Value.ToUpperInvariant() != "WARN",
                detail, native, code, Explain(code, native, detail)));
        }
        // Folder name: RunOnce-20260927-194122-998.
        var name = Path.GetFileName(folder);
        DateTime? started = name.Length >= 23 && DateTime.TryParseExact(name.Substring(8, 15), "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var t) ? t : null;
        return new PostSetupReport(c.Success ? c.Groups["status"].Value : "INCOMPLETE",
            int.TryParse(c.Groups["w"].Value, out var w) ? w : issues.Count(i => !i.IsError),
            int.TryParse(c.Groups["e"].Value, out var e) ? e : issues.Count(i => i.IsError), started, folder, issues);
    }

    internal static string Explain(int? code, int? native, string detail)
    {
        var meaning = code switch
        {
            >= 201 and <= 207 => $"The {detail} reported a failure, so the program may be missing or only partly installed. Install it again from the Setup page.",
            211 => "Applying the laptop/desktop service and power defaults failed.",
            212 => @"Applying the security profile (Basic or Advanced) failed in part. Run Setup\3-Support\Windows Features\Security Profile Basic-Advanced.cmd again as administrator.",
            213 => @"The logon task that runs the startup scripts (\TheKOS\Startup) wasn't registered, so cleanup.cmd and startuptweaks.cmd won't run at logon.",
            301 => "Saving the default services snapshot failed, so the service comparison has no starting point.",
            302 => "Saving the services snapshot after the KOS changes failed.",
            303 => "Comparing the service snapshots failed; the TheKOS.Services.Diff.csv report wasn't written.",
            304 => @"Saving the KOS baseline (TheKOS\Baseline\kos-baseline.json) failed, so the KOS state check can compare only with the values every KOS install sets. Details are in kos-baseline.log in this run's log folder.",
            401 => @"Setting Windows Photo Viewer as the default image viewer failed. Details are in %LocalAppData%\TheKOS\PhotoViewer-Defaults.log of the user who ran the setup.",
            402 => "The script that removes the Windows Security app reported an error.",
            403 => "The Edge/WebView script returned an error.",
            404 => "OneDrive wasn't fully removed (exit 1) or the removal couldn't start (exit 2). Details are in onedrive.log in this run's log folder; synced files are never deleted.",
            405 => @"Setting Notepad++ as the default text editor failed. Details are in %LocalAppData%\TheKOS\NotepadPP-Defaults.log of the user who ran the setup.",
            408 => "7-Zip isn't installed: 7z.exe wasn't found after its installer ran.",
            501 => @"The dependency folder TheKOS\bin\1 wasn't deleted at the end of the setup.",
            502 => @"The one-time scripts folder TheKOS\bin\2 wasn't deleted at the end of the setup.",
            503 => "Windows rejected the restart request at the end of the setup.",
            998 or 999 => "Simulated failure written by the logger self-test; nothing was changed.",
            _ => detail,
        };
        var exit = native switch
        {
            null or 0 => null,
            1602 => "the installation was cancelled",
            1603 => "fatal error during installation",
            1618 => "another installation was already running",
            1619 => "the installer package couldn't be opened",
            1638 => "another version is already installed",
            1641 => "the installer started a restart",
            3010 => "a restart is needed to finish",
            _ => "",
        };
        return exit is null ? meaning : $"{meaning} Exit code {native}{(exit.Length > 0 ? $": {exit}" : "")}.";
    }
}
