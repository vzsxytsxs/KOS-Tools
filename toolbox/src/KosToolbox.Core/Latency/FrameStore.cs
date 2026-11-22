using System.Globalization;
using System.Text;
using System.Text.Json;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;

namespace KosToolbox.Core.Latency;

/// <summary>A saved frame capture: one JSON file in the captures folder, named by its id.</summary>
public sealed class SavedCapture
{
    public const int CurrentVersion = 1;
    public int Version { get; set; } = CurrentVersion;
    /// <summary>The file name without .json; set from the file when loading, never from its content.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Note { get; set; } = "";
    public string Program { get; set; } = "";
    public DateTime Date { get; set; }
    public int RequestedSeconds { get; set; }
    public List<double> FrameTimesMs { get; set; } = new();
    /// <summary>When each frame ended, in seconds from the start (the same length as <see cref="FrameTimesMs"/>).</summary>
    public List<double> TimesS { get; set; } = new();
    public List<PresentModeShare> PresentModes { get; set; } = new();
    public double TearingShare { get; set; }
    /// <summary>Written for other readers of the file; computed again from the frame times when loading.</summary>
    public FrameStats? Stats { get; set; }

    public FrameReport ToReport()
    {
        var s = Stats ?? FrameStats.Of(FrameTimesMs)!;
        return new FrameReport(Program, s.Frames, s.Seconds, s.AverageFps, s.P1LowFps, s.P01LowFps, s.MedianMs, s.MaxMs, s.Stutters, PresentModes, TearingShare, FrameTimesMs);
    }
}

/// <summary>
/// Saved frame captures under %LocalAppData%\KOS\Toolbox\captures (<see cref="AppPaths.DataRoot"/>): save, list (a damaged file is skipped
/// and named), rename, delete, CSV export, and import of a PresentMon CSV or of a capture file.
/// </summary>
public sealed class FrameStore(string folder)
{
    public const int MaxFiles = 500;
    /// <summary>Files larger than this aren't imported (an hour of frames at 1000 FPS is well under it).</summary>
    const long MaxImportBytes = 200L << 20;
    static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static FrameStore Default => new(Path.Combine(AppPaths.DataRoot, "captures"));
    public string Folder { get; } = folder;

    public static string DefaultName(string program, DateTime date) => $"{Path.GetFileNameWithoutExtension(program)} · {date:yyyy-MM-dd HH:mm}";

    public static SavedCapture FromReport(FrameReport r, int requestedSeconds, DateTime date) => Build(r.Program, date, requestedSeconds, r.FrameTimesMs, r.PresentModes, r.TearingShare);

    static SavedCapture Build(string program, DateTime date, int seconds, IReadOnlyList<double> ms, IReadOnlyList<PresentModeShare> modes, double tearing) => new()
    {
        Id = NewId(date), Name = DefaultName(program, date), Program = program, Date = date, RequestedSeconds = seconds,
        FrameTimesMs = ms.ToList(), TimesS = FrameStats.Cumulative(ms).ToList(), PresentModes = modes.ToList(), TearingShare = tearing, Stats = FrameStats.Of(ms),
    };

    static string NewId(DateTime date) => $"{date:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..24];

    string PathOf(SavedCapture c) => Path.Combine(Folder, c.Id + ".json");

    /// <summary>Writes the capture (temp file, then moved over); a new one is refused when the folder already holds <see cref="MaxFiles"/>.</summary>
    public void Save(SavedCapture c)
    {
        if (c.Id.Length == 0 || c.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || c.Id.Contains("..")) throw new ArgumentException("Not a capture id.");
        var path = PathOf(c);
        if (!File.Exists(path) && Directory.Exists(Folder) && Directory.EnumerateFiles(Folder, "*.json").Count() >= MaxFiles)
            throw new InvalidOperationException(Txt.FramesStoreFull(MaxFiles));
        c.Version = SavedCapture.CurrentVersion;
        JsonFile.Save(path, c);
    }

    /// <summary>Every capture, newest first, and the names of the files that couldn't be read.</summary>
    public (List<SavedCapture> Captures, List<string> Unreadable) LoadAll()
    {
        var list = new List<SavedCapture>();
        var bad = new List<string>();
        if (!Directory.Exists(Folder)) return (list, bad);
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
        {
            string? json;
            try { json = File.ReadAllText(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { json = null; }
            if (json is not null && Read(json) is { } c) { c.Id = Path.GetFileNameWithoutExtension(file); list.Add(c); }
            else { bad.Add(Path.GetFileName(file)); Log.Warn($"Frames: capture {file} is unreadable; skipped."); }
        }
        return (list.OrderByDescending(c => c.Date).ToList(), bad);
    }

    /// <summary>A capture file's content, checked (version, at least 10 frame times between 0 and 10 s); null when it isn't one.</summary>
    internal static SavedCapture? Read(string json)
    {
        SavedCapture? c;
        try { c = JsonSerializer.Deserialize<SavedCapture>(json, Options); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException) { return null; }
        if (c is null || c.Version is < 1 or > SavedCapture.CurrentVersion || c.FrameTimesMs is not { Count: >= 10 } ms
            || ms.Any(t => !double.IsFinite(t) || t <= 0 || t > 10_000)) return null;
        c.Name = c.Name?.Trim() is { Length: > 0 } n ? n[..Math.Min(n.Length, 120)] : DefaultName(c.Program ?? "", c.Date);
        c.Note ??= "";
        c.Program ??= "";
        c.PresentModes = (c.PresentModes ?? new()).Where(m => m is { Mode: not null } && double.IsFinite(m.Share)).ToList();
        c.TimesS = FrameStats.Cumulative(ms).ToList();
        c.Stats = FrameStats.Of(ms);
        return c;
    }

    public void Rename(SavedCapture c, string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 120) throw new ArgumentException(Txt.FramesNameInvalid);
        c.Name = name;
        Save(c);
    }

    public void Delete(SavedCapture c) => File.Delete(PathOf(c));

    /// <summary>Frame, end time (s) and frame time (ms). The frame time column has PresentMon's name, so the file imports again.</summary>
    public static string ToCsv(SavedCapture c)
    {
        var sb = new StringBuilder("Frame,TimeInSeconds,MsBetweenPresents\r\n");
        var times = FrameStats.Cumulative(c.FrameTimesMs);
        for (var i = 0; i < c.FrameTimesMs.Count; i++)
            sb.Append(CultureInfo.InvariantCulture, $"{i + 1},{times[i]:0.######},{c.FrameTimesMs[i]:0.####}\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// A capture from a file: this store's JSON, or a PresentMon CSV (its busiest swap chain, as a capture keeps; also this store's CSV export).
    /// It gets a new id and is saved. Throws <see cref="FormatException"/> when the file is neither.
    /// </summary>
    public SavedCapture Import(string path)
    {
        if (new FileInfo(path).Length > MaxImportBytes) throw new FormatException(Txt.FramesImportTooLarge);
        SavedCapture c;
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            c = Read(File.ReadAllText(path)) ?? throw new FormatException(Txt.FramesImportUnreadable(Path.GetFileName(path)));
            c.Id = NewId(DateTime.Now);
        }
        else
        {
            var lines = File.ReadLines(path).ToList();
            var program = ProgramOf(lines) ?? Path.GetFileNameWithoutExtension(path);
            var r = FrameCapture.Parse(program, lines) ?? throw new FormatException(Txt.FramesImportUnreadable(Path.GetFileName(path)));
            c = Build(program, File.GetLastWriteTime(path), 0, r.FrameTimesMs, r.PresentModes, r.TearingShare);
        }
        Save(c);
        return c;
    }

    /// <summary>The Application column of a PresentMon CSV's first row.</summary>
    static string? ProgramOf(List<string> lines)
    {
        if (lines.Count < 2) return null;
        var col = Array.FindIndex(lines[0].Split(','), h => h.Trim().Equals("Application", StringComparison.OrdinalIgnoreCase));
        var f = lines[1].Split(',');
        return col >= 0 && col < f.Length && f[col].Trim() is { Length: > 0 } a ? a : null;
    }
}
