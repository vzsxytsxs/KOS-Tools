using KosToolbox.Core.Persistence;

namespace KosToolbox.ViewModels;

/// <summary>Toolbox preferences, in %LocalAppData%\KOS\Toolbox\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Transitions and smooth scrolling; independent of the Windows animation setting.</summary>
    public bool EnableAnimations { get; set; } = true;
    /// <summary>Reads the memory SPD and timings through CPU-Z when the toolbox opens.</summary>
    public bool ReadMemoryOnStart { get; set; } = true;
    /// <summary>The sidebar shows icons only (its Collapse button); open, with names, by default.</summary>
    public bool NavCollapsed { get; set; }
    /// <summary>Windows build and update number ("26200.7171") at the last KOS state check; Home checks again when it changes.</summary>
    public string? KosCheckedBuild { get; set; }

    public static AppSettings Current { get; } = JsonFile.Load<AppSettings>(AppPaths.Settings);

    public void Save()
    {
        try { JsonFile.Save(AppPaths.Settings, this); }
        catch (Exception ex) { Log.Error("Couldn't save the preferences", ex); }
    }
}
