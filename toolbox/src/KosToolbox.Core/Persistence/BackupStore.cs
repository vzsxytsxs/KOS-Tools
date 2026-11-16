namespace KosToolbox.Core.Persistence;

/// <summary>Values of each setting before the changes made by the toolbox.</summary>
public sealed class Snapshot
{
    public DateTime TakenAt { get; set; }
    /// <summary>The option that matched the values at the time (null = partial/unknown).</summary>
    public string? Option { get; set; }
    /// <summary>ISetting key → value (null = absent).</summary>
    public Dictionary<string, string?> Values { get; set; } = new();
}

public sealed class TweakBackup
{
    /// <summary>Before the first change made by the toolbox. Never overwritten.</summary>
    public Snapshot? Original { get; set; }
    /// <summary>Before the most recent change.</summary>
    public Snapshot? Previous { get; set; }
}

public sealed class BackupStore
{
    readonly object _gate = new();

    public Dictionary<string, TweakBackup> Load() => JsonFile.Load<Dictionary<string, TweakBackup>>(AppPaths.Backups);

    public TweakBackup? Get(string tweakId) => Load().GetValueOrDefault(tweakId);

    public void Record(string tweakId, Snapshot snapshot)
    {
        lock (_gate)
        {
            var all = Load();
            var entry = all.GetValueOrDefault(tweakId) ?? new TweakBackup();
            entry.Original ??= snapshot;
            entry.Previous = snapshot;
            all[tweakId] = entry;
            JsonFile.Save(AppPaths.Backups, all);
        }
    }
}
