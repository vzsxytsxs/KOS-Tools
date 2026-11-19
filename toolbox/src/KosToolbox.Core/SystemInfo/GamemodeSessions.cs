using KosToolbox.Core.Gpu;
using KosToolbox.Core.Persistence;

namespace KosToolbox.Core.SystemInfo;

/// <summary>Report of one gamemode session, written by KOS Engine when gamemode turns off; shown on Hardware › Stability.</summary>
public sealed class GamemodeSession
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    /// <summary>The listed game open during the session; null = none.</summary>
    public string? Game { get; set; }
    public string Profile { get; set; } = "";
    /// <summary>One line, also the notification's text.</summary>
    public string Summary { get; set; } = "";
    public List<SessionSection> Sections { get; set; } = new();
}

public sealed class SessionSection
{
    public string Title { get; set; } = "";
    public List<SessionLine> Lines { get; set; } = new();
}

public sealed class SessionLine
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Warning { get; set; }
}

public sealed class GamemodeSessionList
{
    public List<GamemodeSession> Sessions { get; set; } = new();
}

/// <summary>sessions.json in the admin-only governor folder: the newest <see cref="Kept"/> session reports, newest first.</summary>
public static class GamemodeSessions
{
    public const int Kept = 20;
    public static string FilePath => Path.Combine(GovernorFanCurve.Directory, "sessions.json");

    /// <summary>Newest first; empty with no file or with the folder/file refused.</summary>
    public static List<GamemodeSession> Load() =>
        File.Exists(FilePath) && GovernorFanCurve.Problem(FilePath) is null ? JsonFile.Load(FilePath, KosJson.Default.GamemodeSessionList).Sessions : new();

    /// <summary>Engine only (already elevated): adds the session and keeps the newest <see cref="Kept"/>.</summary>
    public static void Add(GamemodeSession session)
    {
        GpuTuner.PrepareDirectory(GovernorFanCurve.Directory);
        var list = Load();
        list.Insert(0, session);
        File.Delete(FilePath + ".tmp"); // an old .tmp could be a hard link to another file
        JsonFile.Save(FilePath, new GamemodeSessionList { Sessions = list.Take(Kept).ToList() }, KosJson.Default.GamemodeSessionList);
    }
}
