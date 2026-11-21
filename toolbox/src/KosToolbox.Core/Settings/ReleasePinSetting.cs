using KosToolbox.Core.Text;
using Microsoft.Win32;

namespace KosToolbox.Core.Settings;

/// <summary>
/// Windows Update's feature update pin at the installed version: the TargetReleaseVersion policy with ProductVersion and
/// TargetReleaseVersionInfo = this DisplayVersion (e.g. 26H2), as KOS RunOnce.cmd sets it. Reads "1" when the policy pins this
/// version and null otherwise (absent, or a pin left at an older version). Writing "1" pins the current version; null removes it.
/// </summary>
public sealed class ReleasePinSetting : ISetting
{
    const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    const string VersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    static readonly string[] Names = { "TargetReleaseVersion", "ProductVersion", "TargetReleaseVersionInfo" };

    public string Key => "wu:release-pin";
    public string Display => @"HKLM\" + PolicyKey + " → TargetReleaseVersion, ProductVersion, TargetReleaseVersionInfo (this version)";
    public bool MachineWide => true;
    public string? Unavailable() => DisplayVersion() is null ? Txt.ReleasePinNoVersion : null;

    static string? DisplayVersion()
    {
        using var key = Registry.LocalMachine.OpenSubKey(VersionKey);
        return key?.GetValue("DisplayVersion") as string;
    }

    static string Product()
    {
        using var key = Registry.LocalMachine.OpenSubKey(VersionKey);
        return int.TryParse(key?.GetValue("CurrentBuild") as string, out var build) && build >= 22000 ? "Windows 11" : "Windows 10";
    }

    /// <summary>"1" when the three values pin <paramref name="displayVersion"/>; otherwise null.</summary>
    internal static string? State(object? target, object? product, object? info, string? displayVersion) =>
        target is 1 && product is string p && p.Length > 0 && displayVersion is not null
        && info is string i && string.Equals(i, displayVersion, StringComparison.OrdinalIgnoreCase) ? "1" : null;

    public string? Read()
    {
        using var key = Registry.LocalMachine.OpenSubKey(PolicyKey);
        return State(key?.GetValue(Names[0]), key?.GetValue(Names[1]), key?.GetValue(Names[2]), DisplayVersion());
    }

    public void Write(string? value)
    {
        using var key = Registry.LocalMachine.CreateSubKey(PolicyKey, writable: true);
        if (value is null)
        {
            foreach (var n in Names) key.DeleteValue(n, throwOnMissingValue: false);
            return;
        }
        if (value != "1") throw new ArgumentException($"Invalid release pin value: {value}");
        var version = DisplayVersion() ?? throw new InvalidOperationException(Txt.ReleasePinNoVersion);
        key.SetValue(Names[0], 1, RegistryValueKind.DWord);
        key.SetValue(Names[1], Product(), RegistryValueKind.String);
        key.SetValue(Names[2], version, RegistryValueKind.String);
    }
}
