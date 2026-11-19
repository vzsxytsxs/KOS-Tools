using KosToolbox.Core.Execution;
using KosToolbox.Core.Settings;
using KosToolbox.Core.SystemInfo;
using KosToolbox.Core.Text;
using Microsoft.Win32;

namespace KosToolbox.Core.Tweaks;

/// <summary>Target value of a setting for an option.</summary>
public readonly record struct Target(bool Ignore, string? Value, bool AbsentAlsoMatches = false)
{
    public static Target Set(string value) => new(false, value);
    /// <summary>Writes <paramref name="value"/>, but also recognizes the absent value as this option (implicit Windows default).</summary>
    public static Target SetOrAbsent(string value) => new(false, value, true);
    public static readonly Target Delete = new(false, null);
    public static readonly Target Any = new(true, null);

    public bool Matches(Settings.ISetting s, string? current) =>
        s.ValueEquals(current, Value) || (AbsentAlsoMatches && current is null);
}

public sealed record TweakOption(string Key, string Label);

public sealed class SettingSpec
{
    public required ISetting Setting { get; init; }
    public required IReadOnlyDictionary<string, Target> Targets { get; init; }
    /// <summary>If the setting doesn't exist on this installation (e.g. removed service), it is ignored instead of making the tweak unavailable.</summary>
    public bool Optional { get; init; }
}

public enum TweakKind { Toggle, Choice }

/// <summary>
/// Definition of a tweak. Enabled/Disabled always refer to the feature named in <see cref="Name"/>.
/// The state is always computed by reading <see cref="Specs"/>; scripts, when present, apply and the specs verify.
/// </summary>
public sealed class Tweak
{
    public const string On = "on", Off = "off";

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Page { get; init; }
    public string? Tab { get; init; }
    public string? Group { get; init; }
    public TweakKind Kind { get; init; } = TweakKind.Toggle;
    public required IReadOnlyList<TweakOption> Options { get; init; }
    public List<SettingSpec> Specs { get; } = new();

    /// <summary>Script per option. When present, it applies in place of writing the specs directly.</summary>
    public Dictionary<string, ScriptRef> Scripts { get; } = new();

    public bool RequiresReboot { get; set; }
    public bool RestartExplorer { get; set; }
    /// <summary>Warning shown (in a notification) after applying the option that reduces security protections.</summary>
    public string? SecurityNote { get; set; }
    /// <summary>Option considered less secure (warns with <see cref="SecurityNote"/>).</summary>
    public string? LessSecureOption { get; set; }
    /// <summary>Read when the <see cref="SecurityNote"/> warning shows: what applies to this PC (anti-cheats installed), or null.</summary>
    public Func<string?>? SecurityDetail { get; set; }
    /// <summary>Option with an irreversible effect, even without reducing protection (warns with <see cref="ConfirmationNote"/>).</summary>
    public string? ConfirmationOption { get; set; }
    public string? ConfirmationNote { get; set; }
    /// <summary>KOS default option, when verified in the project files.</summary>
    public string? KosDefault { get; set; }
    /// <summary>KOS defaults that change with the security profile (null = no default in that profile). When set,
    /// it applies in place of <see cref="KosDefault"/>.</summary>
    public (string? Basic, string? Advanced)? ProfileDefaults { get; set; }
    /// <summary>KOS default in the given profile. With an unknown profile, tweaks that depend on it have no default.</summary>
    public string? DefaultFor(KosProfile? profile) => ProfileDefaults is not { } p ? KosDefault
        : profile switch { KosProfile.Basic => p.Basic, KosProfile.Advanced => p.Advanced, _ => null };
    /// <summary>Windows default option, only when documented.</summary>
    public string? WindowsDefault { get; set; }
    /// <summary>When false, "restore previous state" isn't reliable (e.g. script with effects outside the specs).</summary>
    public bool Restorable { get; set; } = true;
    /// <summary>KOS project files this tweak was derived from.</summary>
    public string Source { get; set; } = "";
    /// <summary>Additional compatibility check; returns the reason it is unavailable, or null.</summary>
    public Func<string?>? Compatibility { get; set; }
    /// <summary>
    /// Why this option (null: values that match no option, a custom restore) can't be applied right now, read from the system;
    /// null = allowed. <see cref="TweakEngine"/> checks it and the elevated process checks it again.
    /// </summary>
    public Func<string?, string?>? Refuse { get; set; }

    public bool NeedsAdmin => Specs.Any(s => s.Setting.MachineWide) || Scripts.Values.Any(s => s.RequiresAdmin);
    public string SearchText => $"{Name} {Description} {Tab} {Group}";

    // ---------- fluent construction used by the catalog ----------

    public static Tweak Toggle(string id, string name, string description, string page, string? tab = null, string? group = null) => new()
    {
        Id = id, Name = name, Description = description, Page = page, Tab = tab, Group = group,
        Options = new[] { new TweakOption(Off, Txt.Disabled), new TweakOption(On, Txt.Enabled) },
    };

    public static Tweak Choice(string id, string name, string description, string page, string? tab, string? group,
        params (string key, string label)[] options) => new()
    {
        Id = id, Name = name, Description = description, Page = page, Tab = tab, Group = group, Kind = TweakKind.Choice,
        Options = options.Select(o => new TweakOption(o.key, o.label)).ToArray(),
    };

    /// <summary>Spec for toggles: <paramref name="on"/>/<paramref name="off"/> = value, null = delete the value.</summary>
    public Tweak Reg(Hive hive, string path, string name, RegistryValueKind kind, string? on, string? off)
        => Add(new RegistrySetting(hive, path, name, kind), (On, T(on)), (Off, T(off)));

    /// <summary>Service Start value for toggles (2 automatic, 3 manual, 4 disabled), as in the scripts.</summary>
    public Tweak Svc(string name, string on, string off, bool optional = false)
        => AddSpec(RegistrySetting.ServiceStart(name), optional, (On, T(on)), (Off, T(off)));

    public Tweak Add(ISetting setting, params (string option, Target target)[] targets) => AddSpec(setting, false, targets);

    public Tweak AddSpec(ISetting setting, bool optional, params (string option, Target target)[] targets)
    {
        foreach (var (o, _) in targets)
            if (Options.All(x => x.Key != o)) throw new ArgumentException($"{Id}: unknown option '{o}'");
        if (Specs.Any(s => s.Setting.Key.Equals(setting.Key, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"{Id}: duplicate setting {setting.Key}");
        Specs.Add(new SettingSpec { Setting = setting, Optional = optional, Targets = targets.ToDictionary(t => t.option, t => t.target) });
        return this;
    }

    public Tweak Script(string option, ScriptRef script) { Scripts[option] = script; return this; }

    static Target T(string? v) => v is null ? Target.Delete : Target.Set(v);
}
