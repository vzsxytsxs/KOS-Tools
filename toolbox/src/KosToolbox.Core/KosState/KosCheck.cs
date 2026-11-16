using System.Globalization;
using KosToolbox.Core.Apps;
using KosToolbox.Core.Catalog;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Settings;
using KosToolbox.Core.Startup;
using KosToolbox.Core.SystemInfo;
using KosToolbox.Core.Text;
using KosToolbox.Core.Tweaks;

namespace KosToolbox.Core.KosState;

/// <summary>Where a difference comes from.</summary>
public enum KosSection
{
    /// <summary>Differs from what the KOS sets, not through the toolbox: Windows Update, sfc, DISM or a manual change.</summary>
    Outside,
    /// <summary>A value in the KOS keys that changed since the last good state (or the setup's baseline).</summary>
    Other,
    /// <summary>Changed on purpose in the toolbox: its history says it left it this way.</summary>
    Toolbox,
}

public enum KosArea { Settings, Services, Tasks, Apps, Features, Files, Ai, Defender, Registry }

/// <summary>
/// Something that differs. <see cref="Id"/> is what goes back to be fixed: a catalog setting ("tweak:id"), or an index into the
/// embedded list ("e:N" value, "k:N" key to delete, "t:N" task, "s:N|name" value of snapshot key N, "p:N" package, "c:N" NTLite
/// component, "f:N" feature or capability, "x:N" file), which the elevated side resolves again instead of trusting values sent to it.
/// </summary>
public sealed record KosItem(string Id, KosSection Section, KosArea Area, string Title, string Path, string Expected, string Current, string Detail)
{
    public bool Machine { get; init; }
    /// <summary>Null when it can be put back; otherwise why not.</summary>
    public string? NotFixable { get; init; }
    public string? TweakId => Id.StartsWith("tweak:", StringComparison.Ordinal) ? Id[6..] : null;
}

/// <summary>A check's result, with what the KOS keys hold now (saved as the last good state when nothing differs).</summary>
public sealed record KosReport(IReadOnlyList<KosItem> Items, int Compared, int Unreadable, KosReference? MachineReference,
    KosReference? UserReference, KosSnapshot MachineNow, KosSnapshot UserNow)
{
    /// <summary>Nothing differs except what the toolbox changed on purpose.</summary>
    public bool Clean => Items.All(i => i.Section == KosSection.Toolbox);
}

/// <summary>
/// Compares the system with the embedded KOS list (catalog settings at their KOS default for this profile, registry values, deleted
/// keys, scheduled tasks) and the other values of the KOS keys with the last good state. Read-only.
/// </summary>
public static class KosCheck
{
    public static KosReport Run(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var expected = KosExpected.Embedded;
        var items = new List<KosItem>();
        int compared = 0, unreadable = 0;
        var history = TweakEngine.Instance.History.Load();
        var latest = history.GroupBy(e => e.TweakId).ToDictionary(g => g.Key, g => g.Last());
        var left = ToolboxValues(latest.Values);
        var profile = KosProfiles.Current;
        var tweaks = KosCatalog.Tweaks.Where(t => t.DefaultFor(profile) is not null).ToList();
        var owned = Owned.Value;
        double total = tweaks.Count + expected.Registry.Count + expected.KeysAbsent.Count + expected.SnapshotKeys.Count
            + expected.Packages.Count + expected.Components.Count + expected.Features.Count + expected.Files.Count + 1, done = 0;
        void Step() { if (++done % 25 == 0) progress?.Report(done / total); }

        foreach (var t in tweaks)
        {
            ct.ThrowIfCancellationRequested();
            Step();
            var state = TweakEngine.Instance.Query(t);
            if (state.Status == TweakStatus.Unavailable) continue;
            compared++;
            if (state.Status == TweakStatus.ReadError) { unreadable++; continue; }
            var want = t.DefaultFor(profile)!;
            if (state.Status == TweakStatus.Matched && state.Option == want) continue;
            var byToolbox = latest.TryGetValue(t.Id, out var change) && TweakEngine.StillAsLeft(t, change, state);
            var label = (string? o) => t.Options.FirstOrDefault(x => x.Key == o)?.Label;
            items.Add(new KosItem($"tweak:{t.Id}", byToolbox ? KosSection.Toolbox : KosSection.Outside,
                AreaOf($"{t.Id} {t.Name}") ?? KosArea.Settings, t.Name, t.Group ?? t.Tab ?? t.Page,
                label(want) ?? want, Txt.StatusName(state.Status, label(state.Option)), Txt.KosSourceDetail(t.Source))
            { Machine = t.NeedsAdmin });
        }

        for (var i = 0; i < expected.Registry.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            Step();
            var e = expected.Registry[i];
            if (KosReg.Setting(e.Root, e.Key, e.Name, e.Type) is not { } setting || owned.Contains(setting.Key)) continue;
            string? now;
            try
            {
                if (!Applies(e)) continue;
                now = setting.Read();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { unreadable++; continue; }
            compared++;
            var want = Want(e);
            if (setting.ValueEquals(now, want)) continue;
            var section = ByToolbox(left, setting, now) ? KosSection.Toolbox : KosSection.Outside;
            items.Add(ValueItem($"e:{i}", section, e.Root, e.Key, e.Name, e.Type, want, now, e.Source));
        }

        for (var i = 0; i < expected.KeysAbsent.Count; i++)
        {
            Step();
            var k = expected.KeysAbsent[i];
            compared++;
            if (!KosReg.KeyExists(k.Root, k.Key)) continue;
            var name = k.Key[(k.Key.LastIndexOf('\\') + 1)..];
            items.Add(new KosItem($"k:{i}", KosSection.Outside, AreaOf(k.Key) ?? (IsService(k.Key) ? KosArea.Services : KosArea.Registry),
                IsService(k.Key) ? Txt.KosServiceKey(name) : Txt.KosKey(name), $@"{k.Root}\{k.Key}", Txt.KosDeleted, Txt.KosExists,
                Txt.KosSourceDetail(k.Source)) { Machine = KosReg.Machine(k.Root) });
        }

        ct.ThrowIfCancellationRequested();
        Dictionary<string, bool?> tasks;
        Dictionary<string, string> taskErrors;
        try { tasks = ScheduledTaskProvider.EnabledStates(expected.Tasks.Select(t => t.Path), out taskErrors); }
        catch (Exception ex)
        {
            Log.Warn($"KOS state: scheduled tasks not read ({ex.Message})");
            tasks = new();
            taskErrors = expected.Tasks.ToDictionary(t => t.Path, _ => ex.Message, StringComparer.OrdinalIgnoreCase);
        }
        for (var i = 0; i < expected.Tasks.Count; i++)
        {
            var t = expected.Tasks[i];
            if (taskErrors.ContainsKey(t.Path)) { unreadable++; continue; }
            compared++;
            var enabled = tasks.GetValueOrDefault(t.Path);
            var differs = t.State switch { "absent" => enabled is not null, "disabled" => enabled == true, _ => enabled == false };
            if (!differs) continue;
            items.Add(new KosItem($"t:{i}", KosSection.Outside, AreaOf(t.Path) ?? KosArea.Tasks, t.Path[(t.Path.LastIndexOf('\\') + 1)..], t.Path,
                Txt.KosTaskState(t.State), Txt.KosTaskState(enabled is null ? "absent" : enabled.Value ? "enabled" : "disabled"),
                Txt.KosSourceDetail(t.Source)) { Machine = true });
        }

        ct.ThrowIfCancellationRequested();
        CheckSystemItems(expected, items, ref compared, ref unreadable, Step);

        // Every other value of the KOS keys, against the last good state.
        var machineRef = KosSnapshots.MachineReference();
        var userRef = KosSnapshots.UserReference();
        var machineNow = new KosSnapshot { Windows = KosSnapshots.WindowsBuild() };
        var userNow = new KosSnapshot();
        for (var i = 0; i < expected.SnapshotKeys.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            Step();
            var sk = expected.SnapshotKeys[i];
            var machine = KosReg.Machine(sk.Root);
            var now = KosSnapshots.CaptureKey(expected, sk.Root, sk.Key);
            (machine ? machineNow : userNow).Keys.Add(now);
            if (now.Unreadable == true) { unreadable++; continue; }
            if ((machine ? machineRef : userRef)?.Snapshot.Find(sk.Root, sk.Key) is not { Unreadable: not true } before) continue;
            foreach (var d in OtherDifferences(expected, sk, before, now))
            {
                var type = d.Before?.Type ?? d.Now?.Type ?? "sz";
                var setting = KosReg.Setting(sk.Root, sk.Key, d.Name, type);
                var nowText = d.Now is null ? null : KosReg.Text(d.Now.Data);
                var section = setting is not null && ByToolbox(left, setting, nowText) ? KosSection.Toolbox : KosSection.Other;
                var item = ValueItem($"s:{i}|{d.Name}", section, sk.Root, sk.Key, d.Name, type,
                    d.Before is null ? null : KosReg.Text(d.Before.Data), nowText, null);
                items.Add(d.Before is not null && KosReg.Kind(d.Before.Type) is null ? item with { NotFixable = Txt.KosCantWriteType(d.Before.Type) } : item);
            }
        }
        machineNow.CapturedAt = userNow.CapturedAt = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        machineNow.UserSid = userNow.UserSid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        machineNow.ExpectedSha256 = userNow.ExpectedSha256 = KosExpected.EmbeddedSha256;
        userNow.Windows = machineNow.Windows;
        progress?.Report(1);

        items.Sort((a, b) => a.Section != b.Section ? a.Section.CompareTo(b.Section)
            : a.Area != b.Area ? a.Area.CompareTo(b.Area) : string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
        return new KosReport(items, compared, unreadable, machineRef, userRef, machineNow, userNow);
    }

    /// <summary>
    /// Apps and NTLite components that came back (every user's and the provisioned ones as administrator, the current user's
    /// otherwise), optional features and capabilities (DISM: administrator only) and deleted files that are back.
    /// </summary>
    static void CheckSystemItems(KosExpected expected, List<KosItem> items, ref int compared, ref int unreadable, Action step)
    {
        List<KosPackage> packages;
        try
        {
            packages = KosSystemItems.Packages();
            compared += expected.Packages.Count + expected.Components.Count;
        }
        catch (Exception ex)
        {
            Log.Warn($"KOS state: packages not read ({ex.Message})");
            packages = new();
            unreadable += expected.Packages.Count + expected.Components.Count;
        }
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Package(string id, string pattern, string? label, string source)
        {
            step();
            var found = KosSystemItems.Matching(packages, pattern);
            // An NTLite component that is also in the package list is shown once.
            if (found.Count == 0 || found.All(p => reported.Contains(p.FullName))) return;
            reported.UnionWith(found.Select(p => p.FullName));
            items.Add(new KosItem(id, KosSection.Outside, AreaOf($"{found[0].Name} {label}") ?? KosArea.Apps, label ?? found[0].Name,
                string.Join(", ", found.Select(p => p.FullName)), Txt.KosRemoved,
                Txt.KosInstalled(found[0].Version, found.All(p => p.ProvisionedOnly)), Txt.KosSourceDetail(source)) { Machine = true });
        }
        for (var i = 0; i < expected.Packages.Count; i++) Package($"p:{i}", expected.Packages[i].Name, null, expected.Packages[i].Source);
        for (var i = 0; i < expected.Components.Count; i++) Package($"c:{i}", expected.Components[i].Id, expected.Components[i].Label, expected.Components[i].Source);

        Dictionary<string, OptionalFeature>? features = null, capabilities = null;
        if (Elevation.IsAdmin && expected.Features.Count > 0)
        {
            try
            {
                features = OptionalFeatures.ListAsync().GetAwaiter().GetResult().ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
                capabilities = OptionalFeatures.ListCapabilitiesAsync().GetAwaiter().GetResult().ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) { Log.Warn($"KOS state: optional features not read ({ex.Message})"); features = capabilities = null; }
        }
        for (var i = 0; i < expected.Features.Count; i++)
        {
            step();
            var f = expected.Features[i];
            if ((f.Capability ? capabilities : features) is not { } list) { unreadable++; continue; }
            if (!list.TryGetValue(f.Name, out var now)) continue; // not on this edition or language
            compared++;
            var on = f.Capability ? now.State.StartsWith("Install", StringComparison.OrdinalIgnoreCase) : now.IsEnabled;
            if (on == f.Enabled) continue;
            items.Add(new KosItem($"f:{i}", KosSection.Outside, AreaOf(f.Name) ?? KosArea.Features, f.Name.Split('~')[0], f.Name,
                Txt.KosFeatureState(f.Capability, f.Enabled), Txt.KosFeatureState(f.Capability, on), Txt.KosSourceDetail(f.Source))
            {
                Machine = true,
                // Capabilities the KOS image has aren't installed back: only removed ones are put back.
                NotFixable = f.Capability && f.Enabled ? Txt.KosCapabilityNotInstalled : null,
            });
        }

        for (var i = 0; i < expected.Files.Count; i++)
        {
            step();
            var f = expected.Files[i];
            compared++;
            if (!KosSystemItems.Exists(f)) continue;
            var path = KosSystemItems.LocalPath(f.Path);
            var defender = KosSystemItems.DefenderFile(path);
            items.Add(new KosItem($"x:{i}", KosSection.Outside, defender ? KosArea.Defender : AreaOf(path) ?? KosArea.Files, Path.GetFileName(path), path,
                Txt.KosDeleted, Txt.KosExists, Txt.KosSourceDetail(f.Source))
            { Machine = true, NotFixable = defender ? Txt.KosDefenderSafeMode : null });
        }
    }

    /// <summary>The data the KOS leaves: null for a deleted value, %variables% expanded where cmd expanded them.</summary>
    internal static string? Want(KosExpectedValue e) =>
        e.Type == "absent" ? null : KosReg.Text(e.Data) is { } text && e.ExpandData ? Environment.ExpandEnvironmentVariables(text) : KosReg.Text(e.Data);

    /// <summary>Each setting key's value after the latest toolbox change of each catalog setting (the newest wins).</summary>
    internal static Dictionary<string, string?> ToolboxValues(IEnumerable<HistoryEntry> latest)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in latest.OrderBy(e => e.At))
            foreach (var (k, v) in e.AfterValues) map[k] = v;
        return map;
    }

    /// <summary>The toolbox left this value as it is now.</summary>
    internal static bool ByToolbox(IReadOnlyDictionary<string, string?> left, ISetting setting, string? now) =>
        left.TryGetValue(setting.Key, out var v) && setting.ValueEquals(now, v);

    /// <summary>Keys of the catalog settings with a KOS default: their values are checked as that setting (its default knows the profile and the hardware).</summary>
    internal static readonly Lazy<HashSet<string>> Owned = new(() => KosCatalog.Tweaks.Where(t => t.DefaultFor(KosProfiles.Current) is not null)
        .SelectMany(t => t.Specs).Select(s => s.Setting.Key).ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>The value applies on this PC: its guard key exists, and so does the service or autologger it configures.</summary>
    internal static bool Applies(KosExpectedValue e) => (e.IfKeyExists is not { } guard || KeyExists(guard)) && !KosReg.OwnerMissing(e.Root, e.Key);

    /// <summary>
    /// What the check shows as other changes in a snapshot key, and all the elevated side accepts: not a value of the KOS list, of a
    /// catalog setting, or one Windows changes by itself.
    /// </summary>
    internal static IEnumerable<Difference> OtherDifferences(KosExpected expected, KosKey sk, KosSnapshotKey before, KosSnapshotKey now) =>
        Differences(before, now).Where(d => !expected.Listed(sk.Root, sk.Key, d.Name) && !expected.Ignored(sk.Root, sk.Key, d.Name)
            && !(KosReg.Setting(sk.Root, sk.Key, d.Name, "sz") is { } probe && Owned.Value.Contains(probe.Key)));

    internal sealed record Difference(string Name, KosSnapshotValue? Before, KosSnapshotValue? Now);

    /// <summary>Values that changed, appeared or went away between two reads of a key.</summary>
    internal static IEnumerable<Difference> Differences(KosSnapshotKey before, KosSnapshotKey now)
    {
        var a = (before.Values ?? new()).ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);
        var b = (now.Values ?? new()).ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var name in a.Keys.Union(b.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.Ordinal))
        {
            var x = a.GetValueOrDefault(name);
            var y = b.GetValueOrDefault(name);
            if (x is not null && y is not null && KosSnapshots.Same(x, y)) continue;
            yield return new Difference(name, x, y);
        }
    }

    static readonly string[] DefenderSegments = { "WinDefend", "WdFilter", "WdBoot", "WdNisDrv", "WdNisSvc", "Sense", "wscsvc", "SgrmAgent",
        "SgrmBroker", "MsSecFlt", "MsSecCore", "MDCoreSvc", "webthreatdefsvc", "webthreatdefusersvc", "WTDS" };
    static readonly string[] DefenderWords = { "Defender", "SmartScreen", "SecurityHealth", "SecHealthUI" };
    static readonly string[] AiWords = { "WindowsAI", "Copilot", "Recall", "ClickToDo", "CoreAI", "Client.AIX", "AIFabric", "AugLoop" };

    /// <summary>Defender and SmartScreen, or AI, from the key, task or setting text; null for anything else.</summary>
    internal static KosArea? AreaOf(string text)
    {
        var segments = text.Split('\\', ' ');
        if (segments.Any(s => DefenderSegments.Contains(s, StringComparer.OrdinalIgnoreCase))
            || DefenderWords.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase))) return KosArea.Defender;
        if (AiWords.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase))) return KosArea.Ai;
        return null;
    }

    static bool IsService(string key) => key.StartsWith(@"SYSTEM\CurrentControlSet\Services\", StringComparison.OrdinalIgnoreCase);

    internal static bool KeyExists(string path) => path.IndexOf('\\') is var i and > 0 && KosReg.KeyExists(path[..i], path[(i + 1)..]);

    static KosItem ValueItem(string id, KosSection section, string root, string key, string name, string type, string? want, string? now, string? source)
    {
        var service = IsService(key) && key.Count(c => c == '\\') == 3 ? key[(key.LastIndexOf('\\') + 1)..] : null;
        var title = service is not null ? Txt.KosService(service, name) : name.Length == 0 ? Txt.KosDefaultValue : name;
        string Show(string? v) => v is null ? Txt.KosAbsent
            : service is not null && name.Equals("Start", StringComparison.OrdinalIgnoreCase) && int.TryParse(v, out var start) ? Txt.StartName(start, false, false)
            : Txt.KosShortValue(v);
        var area = AreaOf($@"{key}\{name}") ?? (service is not null ? KosArea.Services : KosArea.Registry);
        return new KosItem(id, section, area, title, $@"{root}\{key}", Show(want), Show(now),
            source is null ? $@"{root}\{key} → {(name.Length == 0 ? Txt.KosDefaultValue : name)} ({type})" : Txt.KosSourceDetail(source))
        { Machine = KosReg.Machine(root) };
    }
}
