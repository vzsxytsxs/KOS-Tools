using Microsoft.Win32;

namespace KosToolbox.Core.Settings;

public enum Hive { CurrentUser, LocalMachine }

/// <summary>A registry value. DWORD/QWORD are compared numerically.</summary>
public sealed class RegistrySetting : ISetting
{
    public Hive Hive { get; }
    public string Path { get; }
    public string Name { get; }
    public RegistryValueKind Kind { get; }

    /// <summary>When set, the setting is unavailable if this key doesn't exist (e.g. removed service).</summary>
    public string? RequiredKey { get; init; }
    public string? RequiredKeyMessage { get; init; }

    public RegistrySetting(Hive hive, string path, string name, RegistryValueKind kind)
    {
        Hive = hive; Path = path; Name = name; Kind = kind;
    }

    /// <summary>Start value of a service/driver, written the way the KOS scripts do (effective after a reboot).</summary>
    public static RegistrySetting ServiceStart(string service) =>
        new(Hive.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{service}", "Start", RegistryValueKind.DWord)
        {
            RequiredKey = $@"SYSTEM\CurrentControlSet\Services\{service}",
            RequiredKeyMessage = $"The {service} service doesn't exist on this installation.",
        };

    string HiveName => Hive == Hive.LocalMachine ? "HKLM" : "HKCU";
    public string Key => $"reg:{HiveName}\\{Path}\\{Name}";
    public string Display => $"{HiveName}\\{Path} → {(Name.Length == 0 ? "(Default)" : Name)}";
    public bool MachineWide => Hive == Hive.LocalMachine;

    RegistryKey Root => Hive == Hive.LocalMachine
        ? RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
        : RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);

    public string? Unavailable()
    {
        if (RequiredKey is null) return null;
        using var root = Root;
        using var key = root.OpenSubKey(RequiredKey);
        return key is null ? RequiredKeyMessage ?? $"Key not found: {RequiredKey}" : null;
    }

    public string? Read()
    {
        using var root = Root;
        using var key = root.OpenSubKey(Path, writable: false);
        var raw = key?.GetValue(Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return raw switch
        {
            null => null,
            int i => unchecked((uint)i).ToString(),
            long l => unchecked((ulong)l).ToString(),
            string s => s,
            string[] arr => string.Join("\n", arr),
            byte[] b => Convert.ToHexString(b),
            _ => raw.ToString(),
        };
    }

    public void Write(string? value)
    {
        try { WriteDirect(value); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException
                                   && Hive == Hive.LocalMachine && TrustedInstaller.Available)
        {
            // Protected keys (e.g. some services): KOS uses PowerRun to write as TrustedInstaller.
            TrustedInstaller.RegWrite(this, value);
        }
    }

    void WriteDirect(string? value)
    {
        using var root = Root;
        if (value is null)
        {
            using var existing = root.OpenSubKey(Path, writable: true);
            existing?.DeleteValue(Name, throwOnMissingValue: false);
            return;
        }
        using var key = root.CreateSubKey(Path, writable: true)
            ?? throw new InvalidOperationException($"Couldn't open {Display}");
        object data = Kind switch
        {
            RegistryValueKind.DWord => unchecked((int)uint.Parse(value)),
            RegistryValueKind.QWord => unchecked((long)ulong.Parse(value)),
            RegistryValueKind.Binary => Convert.FromHexString(value),
            RegistryValueKind.MultiString => value.Split('\n'),
            _ => value,
        };
        key.SetValue(Name, data, Kind);
    }

    public bool ValueEquals(string? a, string? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (Kind is RegistryValueKind.DWord or RegistryValueKind.QWord
            && ulong.TryParse(a, out var x) && ulong.TryParse(b, out var y))
            return x == y;
        return string.Equals(a, b, Kind == RegistryValueKind.Binary
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
