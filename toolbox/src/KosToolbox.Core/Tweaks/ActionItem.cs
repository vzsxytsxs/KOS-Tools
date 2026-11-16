using KosToolbox.Core.Execution;

namespace KosToolbox.Core.Tweaks;

/// <summary>A one-off action (button), with no on/off state.</summary>
public sealed class ActionItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Page { get; init; }
    public string? Tab { get; init; }
    public string? Group { get; init; }
    public string ButtonText { get; init; } = Text.Txt.Run;
    /// <summary>Reviewed script the action runs (elevated when required).</summary>
    public ScriptRef? Script { get; init; }
    /// <summary>C# action (e.g. restart Explorer); runs in the current process.</summary>
    public Func<CancellationToken, Task<string>>? Run { get; init; }
    /// <summary>The C# action needs administrator: without elevation, it runs in the elevated process.</summary>
    public bool RunElevated { get; init; }
    public bool RequiresReboot { get; init; }
    public string Source { get; init; } = "";

    /// <summary>Set by the app: a file chosen by the user (title, filter as in an Open dialog), or null when cancelled. Actions that need a file use it.</summary>
    public static Func<string, string, string?>? PickFile { get; set; }
    public bool NeedsAdmin => Script?.RequiresAdmin == true || RunElevated;
    public string SearchText => $"{Name} {Description} {Tab} {Group}";
}
