using System.Reflection;
using System.Text.Json;

namespace KosGovernor.Core;

/// <summary>
/// A program in the game list, in the format of CapFrameX's process list: Name without ".exe" and IsBlacklisted = never
/// treated as a game (launchers, browsers, tools).
/// </summary>
public sealed record GameEntry(string Name, string? DisplayName, bool IsBlacklisted);

/// <summary>
/// Game list for automatic gamemode: the base built into the governor (Resources\games.json) with the user's changes
/// on top (config.json → AutoGamemode.Games). The same user name replaces the base one.
/// </summary>
public sealed class GameList
{
    static readonly Lazy<IReadOnlyList<GameEntry>> Base = new(LoadBase);
    readonly Dictionary<string, GameEntry> _all = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _user = new(StringComparer.OrdinalIgnoreCase);

    public GameList(IEnumerable<GameEntry> user) : this(Base.Value, user) { }

    internal GameList(IEnumerable<GameEntry> baseEntries, IEnumerable<GameEntry> user)
    {
        foreach (var e in baseEntries) _all[Key(e.Name)] = e;
        foreach (var e in user)
        {
            _all[Key(e.Name)] = e;
            _user.Add(Key(e.Name));
        }
    }

    public static IReadOnlyList<GameEntry> BuiltIn => Base.Value;

    /// <summary>The list in effect now: the built-in one with the user's changes on top.</summary>
    public IEnumerable<GameEntry> Entries => _all.Values;

    /// <summary>"cs2.exe" and "cs2" become "cs2" (the list stores names without the extension).</summary>
    public static string Key(string name)
    {
        var n = name.Trim();
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
    }

    public GameEntry? Find(string processName) => _all.GetValueOrDefault(Key(processName));
    public bool IsGame(string processName) => Find(processName) is { IsBlacklisted: false };
    public bool IsUserEntry(string processName) => _user.Contains(Key(processName));
    public string Label(string processName) => Find(processName)?.DisplayName is { Length: > 0 } d ? d : Key(processName);

    static IReadOnlyList<GameEntry> LoadBase()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("KosGovernor.Core.Resources.games.json")
                           ?? throw new InvalidOperationException("The built-in game list is missing from the governor.");
        return JsonSerializer.Deserialize(stream, GovernorJson.Default.ListGameEntry) ?? new();
    }
}
