using KosToolbox.Core.Text;

namespace KosToolbox.Core.Startup;

public enum StartupGroup { Logon, ScheduledTasks, Services, Drivers, Hooks }

/// <summary>
/// A startup entry. Different entries that point to the same executable stay separate:
/// the identity is the source + the name within it.
/// </summary>
public sealed class StartupEntry
{
    public required IStartupProvider Provider { get; init; }
    /// <summary>Stable identity: provider + location + name.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Where the entry is registered (key, folder or task folder).</summary>
    public required string Location { get; init; }
    /// <summary>Short label of the source ("Source" column).</summary>
    public required string Source { get; init; }
    public string Command { get; init; } = "";
    /// <summary>File used for the icon and details; null when it couldn't be identified.</summary>
    public string? ImagePath { get; init; }
    /// <summary>null = unknown state (never shown as Disabled).</summary>
    public bool? Enabled { get; init; }
    public string StartupType { get; init; } = "";
    /// <summary>Run state (services and drivers). Separate from the startup type: stopped isn't disabled.</summary>
    public string? RunState { get; init; }
    /// <summary>How the state was determined, or what prevented determining it.</summary>
    public string StatusDetail { get; init; } = "";
    /// <summary>null = can be enabled/disabled; otherwise the reason.</summary>
    public string? ReadOnlyReason { get; init; }
    public bool MachineWide { get; init; }
    /// <summary>Component whose change may prevent Windows from starting.</summary>
    public string? CriticalReason { get; init; }
    /// <summary>File inside the Windows folder.</summary>
    public bool WindowsComponent { get; init; }
    /// <summary>Executable's vendor (CompanyName from the file version), when read.</summary>
    public string? Publisher { get; init; }
    public bool FromMicrosoft => Publisher?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true;
    /// <summary>Extra lines for the details panel (label, value).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Details { get; init; } = Array.Empty<KeyValuePair<string, string>>();

    public StartupGroup Group => Provider.Group;
    public string StatusText => Enabled switch { true => Txt.Enabled, false => Txt.Disabled, null => Txt.Unknown };
    public bool CanToggle => ReadOnlyReason is null && Enabled is not null;

    // Internal data the provider uses to change the entry.
    internal string? Hive { get; init; }
    internal string? ApprovedSubKey { get; init; }
    internal string? ValueName { get; init; }
    internal string? TaskPath { get; init; }
    internal string? ServiceName { get; init; }
    internal int? Start { get; init; }
    internal bool Delayed { get; init; }
}

public interface IStartupProvider
{
    /// <summary>Name of the source, in English, for the coverage list and error messages.</summary>
    string Name { get; }
    StartupGroup Group { get; }
    /// <summary>Only appears in Advanced mode.</summary>
    bool AdvancedOnly { get; }
    IEnumerable<StartupEntry> Collect(bool advanced);
    /// <summary>Changes it through the source's own mechanism and confirms by re-reading. Never deletes the entry.</summary>
    Task SetEnabledAsync(StartupEntry entry, bool enable, CancellationToken ct);
}
