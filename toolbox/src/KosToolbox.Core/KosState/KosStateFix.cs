using KosToolbox.Core.Apps;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Startup;

namespace KosToolbox.Core.KosState;

/// <summary>Elevated request: only ids of the KOS check (machine values, keys and tasks); the values come from the embedded list or the admin-only last good state.</summary>
public sealed class KosFixJob
{
    public List<string> Ids { get; set; } = new();
    /// <summary>Save the machine keys as the last good state afterwards: only on the last request, and only when every write before it worked.</summary>
    public bool SaveLastGood { get; set; }
}

/// <summary>Puts checked items of a <see cref="KosCheck"/> back: to the KOS value, or to the last good state's.</summary>
public static class KosStateFix
{
    /// <summary>Ids per elevated request: the request travels on the command line.</summary>
    const int PerJob = 400;
    const int MaxIds = 5000;

    internal enum ChangeKind { Value, DeleteKey, Task, Package, Feature, Capability, File }

    /// <summary>
    /// What an id asks for, recomputed here: a value (null = delete it), a key to delete, a task's state, a package pattern to
    /// remove, a feature to turn on or off, a capability to remove or a file or folder (Name) to delete.
    /// </summary>
    internal sealed record Change(string Id, ChangeKind Kind, string Root, string Key, string Name, string Type, string? Value)
    {
        public bool Machine => Kind is not (ChangeKind.Value or ChangeKind.DeleteKey) || KosReg.Machine(Root);
        /// <summary>The data had %variables%, expanded in this process's environment (which the user can set in HKCU\Environment).</summary>
        public bool Expanded { get; init; }
    }

    /// <summary>
    /// Refuses an id that isn't in the embedded list, a value the check doesn't offer (a catalog setting's, one that doesn't apply
    /// here, or a snapshot value that doesn't differ now), or one the reference doesn't hold in a type that can be written.
    /// A snapshot value's name and data come from the registry and the reference, not from the id.
    /// </summary>
    internal static Change Resolve(string id, KosExpected expected, Func<string, KosSnapshot?> reference)
    {
        var colon = id.IndexOf(':');
        var kind = colon > 0 ? id[..colon] : "";
        var rest = colon > 0 ? id[(colon + 1)..] : "";
        var bar = rest.IndexOf('|');
        var index = Index(kind == "s" && bar > 0 ? rest[..bar] : rest, kind switch
        {
            "e" => expected.Registry.Count, "k" => expected.KeysAbsent.Count, "t" => expected.Tasks.Count, "s" => expected.SnapshotKeys.Count,
            "p" => expected.Packages.Count, "c" => expected.Components.Count, "f" => expected.Features.Count, "x" => expected.Files.Count, _ => 0,
        }, id);
        switch (kind)
        {
            case "e":
                var e = expected.Registry[index];
                if (KosReg.Setting(e.Root, e.Key, e.Name, e.Type) is not { } setting || KosCheck.Owned.Value.Contains(setting.Key) || !KosCheck.Applies(e))
                    throw Refused(id);
                return new Change(id, ChangeKind.Value, e.Root, e.Key, e.Name, e.Type, KosCheck.Want(e)) { Expanded = e.ExpandData };
            case "k":
                var k = expected.KeysAbsent[index];
                return new Change(id, ChangeKind.DeleteKey, k.Root, k.Key, "", "", null);
            case "t":
                var t = expected.Tasks[index];
                return new Change(id, ChangeKind.Task, "", t.Path, "", "", t.State);
            case "s" when bar > 0:
                var sk = expected.SnapshotKeys[index];
                if (KosCheck.ComparedAsNative(expected, sk)) throw Refused(id); // the check offers it as the 64-bit key
                var name = rest[(bar + 1)..];
                var before = reference(sk.Root)?.Find(sk.Root, sk.Key);
                if (before is null || before.Unreadable == true) throw Refused(id);
                var now = KosSnapshots.CaptureKey(expected, sk.Root, sk.Key);
                if (now.Unreadable == true) throw Refused(id);
                var d = KosCheck.OtherDifferences(expected, sk, before, now).FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? throw Refused(id);
                if (d.Before is null) return new Change(id, ChangeKind.Value, sk.Root, sk.Key, d.Now!.Name, "sz", null); // appeared since: deleted
                if (KosReg.Kind(d.Before.Type) is null || d.Before.Type == "absent") throw Refused(id);
                return new Change(id, ChangeKind.Value, sk.Root, sk.Key, d.Before.Name, d.Before.Type, KosReg.Text(d.Before.Data));
            case "p":
                return new Change(id, ChangeKind.Package, "", expected.Packages[index].Name, "", "", null);
            case "c":
                return new Change(id, ChangeKind.Package, "", expected.Components[index].Id, "", "", null);
            case "f":
                var f = expected.Features[index];
                if (f.Capability && f.Enabled) throw Refused(id); // only removed capabilities are put back
                return f.Capability ? new Change(id, ChangeKind.Capability, "", f.Name, "", "", null)
                    : new Change(id, ChangeKind.Feature, "", f.Name, "", "", f.Enabled ? "enabled" : "disabled");
            case "x":
                var x = expected.Files[index];
                if (KosSystemItems.DefenderFile(x.Path)) throw Refused(id); // only with Defender, in Safe Mode
                return new Change(id, ChangeKind.File, "", x.Path, x.Kind, "", null);
            default:
                throw Refused(id);
        }
    }

    static int Index(string text, int count, string id) =>
        int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var i) && i < count ? i : throw Refused(id);

    static InvalidOperationException Refused(string id) => new($"KOS state: item not allowed: {id}");

    internal static void Apply(Change c)
    {
        switch (c.Kind)
        {
            case ChangeKind.Value:
                if (c.Value is not null && KosReg.OwnerMissing(c.Root, c.Key)) throw new InvalidOperationException($@"{c.Root}\{c.Key}: not installed here.");
                var setting = KosReg.Setting(c.Root, c.Key, c.Name, c.Type)!;
                setting.Write(c.Value);
                Log.Info($"KOS state: {setting.Display} = {c.Value ?? "(deleted)"}");
                break;
            case ChangeKind.DeleteKey:
                KosReg.DeleteKey(c.Root, c.Key);
                break;
            case ChangeKind.Task:
                if (c.Value == "absent") ScheduledTaskProvider.Delete(c.Key);
                else ScheduledTaskProvider.SetEnabled(c.Key, c.Value == "enabled");
                break;
            case ChangeKind.Package:
                KosSystemItems.RemovePackages(c.Key);
                break;
            case ChangeKind.Feature: // turned off, never removed: it can be turned on again in Windows features
                OptionalFeatures.SetAsync(c.Key, c.Value == "enabled").GetAwaiter().GetResult();
                break;
            case ChangeKind.Capability:
                OptionalFeatures.RemoveCapabilityAsync(c.Key).GetAwaiter().GetResult();
                break;
            case ChangeKind.File:
                KosSystemItems.Delete(c.Key, c.Name == "folder");
                break;
        }
    }

    static Func<string, KosSnapshot?> References()
    {
        var machine = new Lazy<KosSnapshot?>(() => KosSnapshots.MachineReference()?.Snapshot);
        var user = new Lazy<KosSnapshot?>(() => KosSnapshots.UserReference()?.Snapshot);
        return root => KosReg.Machine(root) ? machine.Value : user.Value;
    }

    /// <summary>Elevated side: machine items only, each one resolved again.</summary>
    internal static List<Change> Validate(KosFixJob job)
    {
        if (job.Ids.Count is 0 or > MaxIds) throw new InvalidOperationException("KOS state: invalid number of items.");
        var reference = References();
        var changes = job.Ids.Distinct(StringComparer.Ordinal).Select(id => Resolve(id, KosExpected.Embedded, reference)).ToList();
        if (changes.FirstOrDefault(c => !c.Machine) is { } user) throw new InvalidOperationException($"KOS state: a user item can't be written elevated: {user.Id}");
        if (changes.FirstOrDefault(c => c.Expanded) is { } expanded) throw new InvalidOperationException($"KOS state: data with %variables% isn't written elevated: {expanded.Id}");
        return changes;
    }

    /// <summary>
    /// Elevated side: writes, then (last request, nothing failed) saves the machine keys as they are now as the last good state:
    /// what the user chose to keep. A write that failed never becomes the last good state.
    /// </summary>
    internal static int Execute(KosFixJob job)
    {
        var failed = Run(Validate(job));
        if (failed == 0 && job.SaveLastGood) SaveMachine();
        return failed == 0 ? ElevatedJob.ExitOk : ElevatedJob.ExitPartial;
    }

    static int Run(IEnumerable<Change> changes)
    {
        var failed = 0;
        // Apps before files: a SystemApps folder goes after its package.
        foreach (var c in changes.OrderBy(c => c.Kind == ChangeKind.File))
        {
            try { Apply(c); }
            catch (Exception ex) { failed++; Log.Warn($"KOS state: {c.Id} ({c.Root}\\{c.Key} {c.Name}) not changed: {ex.Message}"); }
        }
        return failed;
    }

    static void SaveMachine()
    {
        try { KosSnapshots.SaveMachine(KosSnapshots.Capture(KosExpected.Embedded, machine: true)); }
        catch (Exception ex) { Log.Warn($"KOS state: the last good state wasn't saved: {ex.Message}"); }
    }

    /// <summary>
    /// Writes the raw items (catalog settings go through the tweak engine, by the caller): the user's here, the machine's directly
    /// as administrator or through the elevated process. When every write of a part worked, that part is saved as the last good
    /// state (the machine's only when written here or by the elevated process). The caller confirms by checking again.
    /// </summary>
    public static async Task ApplyAsync(IReadOnlyCollection<string> ids, Func<ElevatedJob, CancellationToken, Task<int>> runElevated, CancellationToken ct = default)
    {
        var reference = References();
        var changes = await Task.Run(() => ids.Select(id => Resolve(id, KosExpected.Embedded, reference)).ToList(), ct);
        var userFailed = await Task.Run(() => Run(changes.Where(c => !c.Machine)), ct);
        var machine = changes.Where(c => c.Machine).ToList();
        try
        {
            if (Elevation.IsAdmin) await Task.Run(() => { if (Run(machine) == 0) SaveMachine(); }, ct);
            else if (machine.Count > 0)
            {
                // One save, after the last request: every request resolves its snapshot values against the same last good state.
                var chunks = machine.Select(c => c.Id).Chunk(PerJob).ToList();
                var allOk = true;
                for (var i = 0; i < chunks.Count; i++)
                {
                    var job = new KosFixJob { Ids = chunks[i].ToList(), SaveLastGood = allOk && i == chunks.Count - 1 };
                    allOk &= await runElevated(new ElevatedJob { KosFix = job }, ct) == ElevatedJob.ExitOk;
                }
            }
        }
        finally
        {
            if (userFailed == 0) await Task.Run(() => SaveUser(), ct);
        }
    }

    /// <summary>After a check with nothing to fix: the state as the new last good one (the machine's only as administrator).</summary>
    public static void SaveClean(KosReport report)
    {
        SaveUser(report.UserNow);
        if (!Elevation.IsAdmin) return;
        try { KosSnapshots.SaveMachine(report.MachineNow); }
        catch (Exception ex) { Log.Warn($"KOS state: the last good state wasn't saved: {ex.Message}"); }
    }

    static void SaveUser(KosSnapshot? now = null)
    {
        try { KosSnapshots.SaveUser(now ?? KosSnapshots.Capture(KosExpected.Embedded, machine: false)); }
        catch (Exception ex) { Log.Warn($"KOS state: the user's last good state wasn't saved: {ex.Message}"); }
    }
}
