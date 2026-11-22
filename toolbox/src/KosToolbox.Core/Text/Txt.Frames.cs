namespace KosToolbox.Core.Text;

/// <summary>Texts for Latency › Frames: saved captures, their graphs and the comparison.</summary>
public static partial class Txt
{
    public const string FramesNameInvalid = "A capture name has 1 to 120 characters.",
        FramesImportTooLarge = "That file is too large for a frame capture.",
        FramesCaptures = "Captures", FramesSave = "Save capture", FramesAutoSave = "Save every capture automatically",
        FramesAutoSaveTip = "Each capture is saved to the list as soon as it ends.",
        FramesImport = "Import…", FramesImportTip = "A PresentMon CSV (also from CapFrameX's capture folder or this list's CSV export) or a saved capture file (.json).",
        FramesCompare = "Compare", FramesCompareTip = "Tick 2 to 4 captures: the first one ticked is the reference the others are measured against.",
        FramesCloseCompare = "Close comparison", FramesLoad = "Load", FramesRename = "Rename", FramesRenameDone = "Done", FramesDelete = "Delete", FramesExport = "Export CSV…",
        FramesNoCaptures = "No saved captures yet. Capture a game, then Save capture, or import a PresentMon CSV.",
        FramesGraphLabel = "Graph", FramesComparison = "Comparison", FramesReference = "reference",
        FramesGraphFrameTime = "Frame time over time", FramesGraphFps = "FPS over time", FramesGraphHistogram = "Frame-time histogram",
        FramesGraphPercentiles = "Frame time at percentiles", FramesGraphBars = "Average FPS and lows",
        FramesAxisSeconds = "Seconds", FramesAxisFrameTime = "Frame time (ms)", FramesAxisFps = "FPS", FramesAxisShare = "Frames (%)",
        FramesLowsNote = "1 % and 0.1 % low: the 99th and 99.9th percentile frame time as FPS. Worst 1 % / 0.1 % average: the average FPS of the slowest 1 % / 0.1 % of frames (CapFrameX's \"1 % low\").";
    public static string FramesStoreFull(int max) => $"The captures folder already holds {max} captures: delete some before saving more.";
    public static string FramesImportUnreadable(string file) => $"{file} isn't a PresentMon CSV with at least 10 frames or a saved capture.";
    public static string FramesUnreadable(IReadOnlyList<string> files) =>
        $"{files.Count} capture file{(files.Count == 1 ? " is" : "s are")} damaged and left out: {string.Join(", ", files.Take(3))}{(files.Count > 3 ? ", …" : "")}.";
    public static string FramesSaved(string name) => $"Saved as \"{name}\".";
    public static string FramesDeleted(string name) => $"\"{name}\" deleted.";
    public static string FramesExported(string file) => $"Exported to {file}.";
    public static string FramesImported(string name) => $"Imported as \"{name}\".";
    public static string FramesLoaded(string name, string program, DateTime date) => $"{name}: {program}, {date:yyyy-MM-dd HH:mm}.";
    public static string FramesPicked(int count) => count switch
    {
        0 => "Tick 2 to 4 captures to compare them.",
        1 => "1 ticked: tick 1 to 3 more to compare.",
        > 4 => $"{count} ticked: a comparison takes up to 4.",
        _ => $"{count} ticked.",
    };
}
