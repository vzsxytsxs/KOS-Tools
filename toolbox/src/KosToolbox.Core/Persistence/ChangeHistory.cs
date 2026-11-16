namespace KosToolbox.Core.Persistence;

/// <summary>A change made to a catalog setting: the values before, the option asked for and what was read back after.</summary>
public sealed class HistoryEntry
{
    public DateTime At { get; set; }
    public string TweakId { get; set; } = "";
    /// <summary>Values (and the option they matched) before the change; undoing puts them back.</summary>
    public Snapshot Before { get; set; } = new();
    /// <summary>Option asked for; null when values were restored instead.</summary>
    public string? Option { get; set; }
    /// <summary>Option read back after the change (null = partial or unknown).</summary>
    public string? After { get; set; }
    /// <summary>ISetting key → value read back after (null = absent).</summary>
    public Dictionary<string, string?> AfterValues { get; set; } = new();
    public bool Confirmed { get; set; }
}

/// <summary>history.json: the latest <see cref="Max"/> changes, oldest first.</summary>
public sealed class ChangeHistory
{
    public const int Max = 200;
    readonly object _gate = new();

    /// <summary>The saved changes, without damaged entries (the file is the user's to edit).</summary>
    public List<HistoryEntry> Load() => JsonFile.Load<List<HistoryEntry>>(AppPaths.History)
        .Where(e => e is { TweakId: not null, Before.Values: not null, AfterValues: not null }).ToList();

    public void Add(HistoryEntry entry)
    {
        lock (_gate)
        {
            var all = Load();
            all.Add(entry);
            if (all.Count > Max) all.RemoveRange(0, all.Count - Max);
            JsonFile.Save(AppPaths.History, all);
        }
    }

    /// <summary>Whether <paramref name="entry"/> is the latest change of its setting: only that one can be undone.</summary>
    public static bool IsLatest(IReadOnlyList<HistoryEntry> all, HistoryEntry entry) =>
        ReferenceEquals(all.LastOrDefault(e => e.TweakId == entry.TweakId), entry);
}
