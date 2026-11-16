using KosToolbox.Core.Power;

namespace KosToolbox.Core.Settings;

/// <summary>AC index of a setting in the active power plan (like "powercfg /setacvalueindex scheme_current").</summary>
public sealed class PowerSetting : ISetting
{
    public Guid Subgroup { get; }
    public Guid SettingId { get; }
    readonly string _label;

    public PowerSetting(Guid subgroup, Guid setting, string label) { Subgroup = subgroup; SettingId = setting; _label = label; }

    public string Key => $"power:active\\{Subgroup}\\{SettingId}";
    public string Display => $"Active plan → {_label} (AC)";
    public bool MachineWide => true;
    public string? Unavailable() => null;

    public string? Read() => PowerPlans.ReadValue(PowerPlans.ActiveScheme(), Subgroup, SettingId, ac: true).ToString();

    // Re-applies the active plan after writing, like "powercfg /setactive scheme_current".
    public void Write(string? value) => PowerPlans.Apply(new[]
    {
        new PowerWrite { Scheme = PowerPlans.ActiveScheme(), Subgroup = Subgroup, Setting = SettingId, Ac = true,
            Value = uint.Parse(value ?? throw new ArgumentNullException(nameof(value))) },
    });

    public bool ValueEquals(string? a, string? b) => a == b;
}
