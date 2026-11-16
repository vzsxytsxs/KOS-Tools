using KosToolbox.Core.Settings;
using KosToolbox.Core.Text;
namespace KosToolbox.Core.Tweaks;

public enum TweakStatus
{
    /// <summary>The values read match an option exactly.</summary>
    Matched,
    /// <summary>Some of the values match an option; the rest are absent or different.</summary>
    Partial,
    /// <summary>No value is stored and absence isn't an option: Windows uses its built-in behavior.</summary>
    NotConfigured,
    /// <summary>There are stored values, but none matches an option.</summary>
    Unknown,
    /// <summary>Feature missing or incompatible on this installation.</summary>
    Unavailable,
    /// <summary>The read failed (e.g. access denied); must never be shown as "Disabled".</summary>
    ReadError,
}

/// <summary>A value read. <see cref="Value"/> null without <see cref="Error"/> = absent value.</summary>
public sealed record SettingReading(string Display, string? Value, string? Error, IReadOnlyDictionary<string, bool?> Matches)
{
    public bool IsMissing => Error is null && Value is null;
}

public sealed record TweakState(TweakStatus Status, string? Option, string? Detail)
{
    public IReadOnlyList<SettingReading> Readings { get; init; } = Array.Empty<SettingReading>();
    /// <summary>For <see cref="TweakStatus.Partial"/>: the option with the most matching values.</summary>
    public string? ClosestOption { get; init; }
    public int ClosestMatches { get; init; }
    public int ClosestTotal { get; init; }

    public static TweakState ReadError(string detail) => new(TweakStatus.ReadError, null, detail);
    public static TweakState Unavailable(string detail) => new(TweakStatus.Unavailable, null, detail);
}

public static class TweakEvaluator
{
    /// <summary>Reads the specs and determines the current option.</summary>
    public static TweakState Evaluate(Tweak tweak)
    {
        var reason = tweak.Compatibility?.Invoke();
        if (reason is not null) return TweakState.Unavailable(reason);
        var specs = new List<SettingSpec>();
        foreach (var spec in tweak.Specs)
        {
            var missing = spec.Setting.Unavailable();
            if (missing is null) specs.Add(spec);
            else if (!spec.Optional) return TweakState.Unavailable(missing);
        }
        if (specs.Count == 0) return TweakState.Unavailable(Txt.NoComponentsPresent);

        var current = new Dictionary<ISetting, string?>();
        var errors = new Dictionary<ISetting, string>();
        foreach (var spec in specs)
        {
            try { current[spec.Setting] = spec.Setting.Read(); }
            catch (Exception ex) { errors[spec.Setting] = ex.Message; current[spec.Setting] = null; }
        }
        return Match(tweak, specs, current, errors);
    }

    internal static TweakState Match(Tweak tweak, IReadOnlyList<SettingSpec> specs, IReadOnlyDictionary<ISetting, string?> current,
        IReadOnlyDictionary<ISetting, string>? errors = null)
    {
        errors ??= new Dictionary<ISetting, string>();
        var readings = specs.Select(s => new SettingReading(s.Setting.Display, current[s.Setting], errors.GetValueOrDefault(s.Setting),
            tweak.Options.ToDictionary(o => o.Key, o => s.Targets.TryGetValue(o.Key, out var t) && !t.Ignore && !errors.ContainsKey(s.Setting)
                ? t.Matches(s.Setting, current[s.Setting]) : (bool?)null))).ToList();

        // A read that failed never becomes an invented state.
        var failed = specs.FirstOrDefault(s => errors.ContainsKey(s.Setting));
        if (failed is not null)
            return new TweakState(TweakStatus.ReadError, null, Txt.ReadFailed(failed.Setting.Display, errors[failed.Setting])) { Readings = readings };

        string? best = null;
        int bestHits = 0, bestTotal = 0;
        foreach (var option in tweak.Options)
        {
            var relevant = specs.Where(s => s.Targets.TryGetValue(option.Key, out var t) && !t.Ignore).ToList();
            if (relevant.Count == 0) continue;
            var hits = relevant.Count(s => s.Targets[option.Key].Matches(s.Setting, current[s.Setting]));
            if (hits == relevant.Count) return new TweakState(TweakStatus.Matched, option.Key, null) { Readings = readings };
            if (hits > bestHits) { best = option.Key; bestHits = hits; bestTotal = relevant.Count; }
        }

        if (specs.All(s => current[s.Setting] is null))
            return new TweakState(TweakStatus.NotConfigured, null, Txt.NotConfiguredDetail) { Readings = readings };
        if (best is null)
            return new TweakState(TweakStatus.Unknown, null, Txt.UnknownDetail) { Readings = readings };
        var label = tweak.Options.First(o => o.Key == best).Label;
        return new TweakState(TweakStatus.Partial, null, Txt.PartialDetail(bestHits, bestTotal, label))
        {
            Readings = readings, ClosestOption = best, ClosestMatches = bestHits, ClosestTotal = bestTotal,
        };
    }
}
