namespace KosToolbox.Core.Settings;

/// <summary>
/// A system setting that can be read and written as text.
/// A <c>null</c> value means "absent" (nonexistent registry value).
/// </summary>
public interface ISetting
{
    /// <summary>Stable key, used in backups and in validating elevated jobs.</summary>
    string Key { get; }

    /// <summary>Readable description, used in logs and tooltips.</summary>
    string Display { get; }

    /// <summary>True when writing requires administrator (HKLM, services).</summary>
    bool MachineWide { get; }

    /// <summary>Null when available; otherwise the reason it isn't.</summary>
    string? Unavailable();

    /// <summary>Reads the current value. Throws if the read fails (the caller shows "Unknown").</summary>
    string? Read();

    void Write(string? value);

    /// <summary>Compares values in this setting's normalized form.</summary>
    bool ValueEquals(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
