using System.Diagnostics;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;

namespace KosToolbox.Core.Debloat;

/// <summary>Read from the system every time; never remembered.</summary>
public enum DebloatState
{
    NotApplied,
    Partial,
    Applied,
    /// <summary>One-shot action (cleaning): there is nothing to read.</summary>
    OneShot,
}

/// <summary>One separate function of an app's debloater.</summary>
public sealed class DebloatOption
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    /// <summary>Changes HKLM or Program Files: runs in the elevated process, which only receives the ids.</summary>
    public bool NeedsAdmin { get; init; }
    /// <summary>Files the app keeps open: it is closed first.</summary>
    public bool ClosesApp { get; init; }
    /// <summary>Risk shown as a warning after applying.</summary>
    public string? Warning { get; init; }
    /// <summary>Runs several functions together; listed apart from the single functions.</summary>
    public bool IsPreset { get; init; }
    public Func<DebloatState> Read { get; init; } = () => DebloatState.OneShot;
    public required Func<CancellationToken, Task> Apply { get; init; }
    public Func<CancellationToken, Task>? Undo { get; init; }
}

public sealed class DebloatApp
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Image in Assets\Setup (without extension).</summary>
    public required string Icon { get; init; }
    /// <summary>Main executable; the app counts as installed when it exists.</summary>
    public required Func<string?> Executable { get; init; }
    /// <summary>Process names to close before options with <see cref="DebloatOption.ClosesApp"/>.</summary>
    public string[] Processes { get; init; } = Array.Empty<string>();
    /// <summary>False: if the app doesn't close when asked, the option refuses instead of ending it (OBS can be recording).</summary>
    public bool ForceClose { get; init; } = true;
    public required IReadOnlyList<DebloatOption> Options { get; init; }

    public string? InstalledVersion(string exe)
    {
        try { return FileVersionInfo.GetVersionInfo(exe).ProductVersion; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>Elevated request: only ids. The paths and values come from the catalog on the elevated side.</summary>
public sealed class DebloatJob
{
    public string App { get; set; } = "";
    public string Option { get; set; } = "";
    public bool Undo { get; set; }
}

public static class DebloatCatalog
{
    static readonly Lazy<IReadOnlyList<DebloatApp>> All = new(() => DiscordDebloat.Channels.Select(DiscordDebloat.App).Concat(new[]
    {
        SpotifyDebloat.App(SpotifyDebloat.Default),
        BrowserDebloat.Chrome(), BrowserDebloat.Brave(), BrowserDebloat.Firefox(), BrowserDebloat.LibreWolf(), BrowserDebloat.Thorium(),
        ObsDebloat.App(ObsDebloat.Default),
    }).ToList());

    public static IReadOnlyList<DebloatApp> Apps => All.Value;

    /// <summary>Refuses anything that isn't exactly an option of the catalog that runs elevated.</summary>
    internal static (DebloatApp App, DebloatOption Option) Validate(DebloatJob job)
    {
        var app = Apps.FirstOrDefault(a => a.Id == job.App) ?? throw new InvalidOperationException($"Unknown app: {job.App}");
        var option = app.Options.FirstOrDefault(o => o.Id == job.Option) ?? throw new InvalidOperationException($"Unknown option: {job.Option}");
        if (!option.NeedsAdmin) throw new InvalidOperationException($"The option {job.Option} doesn't run elevated.");
        if (job.Undo && option.Undo is null) throw new InvalidOperationException($"The option {job.Option} can't be undone.");
        return (app, option);
    }

    internal static async Task ExecuteAsync(DebloatJob job, CancellationToken ct = default)
    {
        var (app, option) = Validate(job);
        Log.Info($"[elevated] Debloat {app.Id}/{option.Id}{(job.Undo ? " (undo)" : "")}");
        await RunAsync(app, option, job.Undo, ct);
    }

    /// <summary>
    /// Several per-user functions in order; it closes the app once, before the first. One failing (a download, say) doesn't stop the
    /// others: they all run, then the failures are reported and the state reads as partial.
    /// </summary>
    internal static DebloatOption Preset(string id, string name, string description, IReadOnlyList<DebloatOption> functions) => new()
    {
        Id = id, Name = name, Description = description, IsPreset = true, ClosesApp = true,
        Warning = functions.Select(f => f.Warning).FirstOrDefault(w => w is not null),
        Read = () => CombinedState(functions.Select(f => f.Read())),
        Apply = async ct =>
        {
            var failed = new List<string>();
            foreach (var f in functions)
            {
                try { await f.Apply(ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { failed.Add($"{f.Name}: {ex.Message}"); }
            }
            if (failed.Count > 0) throw new InvalidOperationException(string.Join(" ", failed));
        },
    };

    /// <summary>One-shot functions have nothing to read, so they don't count.</summary>
    internal static DebloatState CombinedState(IEnumerable<DebloatState> states)
    {
        var read = states.Where(s => s != DebloatState.OneShot).ToList();
        return read.All(s => s == DebloatState.Applied) ? DebloatState.Applied : read.All(s => s == DebloatState.NotApplied) ? DebloatState.NotApplied : DebloatState.Partial;
    }

    /// <summary>Closes the app if the option needs it and runs the option; used by both the interface and the elevated process.</summary>
    public static async Task RunAsync(DebloatApp app, DebloatOption option, bool undo, CancellationToken ct)
    {
        if (!option.NeedsAdmin) RefuseElevatedUserChange();
        if (option.ClosesApp) await CloseAsync(app, ct);
        await (undo ? option.Undo! : option.Apply)(ct);
    }

    /// <summary>
    /// With UAC on, an unelevated program of the same user can plant links in that user's folders, and a toolbox elevated by UAC would
    /// write through them as administrator. Per-user changes don't need elevation, so they refuse it. With UAC off nothing is
    /// unelevated: every program of the user already is administrator.
    /// </summary>
    public static void RefuseElevatedUserChange()
    {
        if (RefusesUserChange(Elevation.TokenElevationType())) throw new InvalidOperationException(Txt.DebloatOpenUnelevated);
    }

    /// <summary>Only a token with no split (1) or the unelevated half (3) may go on; one that can't be read counts as elevated.</summary>
    internal static bool RefusesUserChange(int? tokenElevationType) => tokenElevationType is not (1 or 3);

    /// <summary>Only this Windows session: asks the windows to close and waits; what stays open (apps that go to the tray) is ended, unless the app must not be.</summary>
    public static async Task CloseAsync(DebloatApp app, CancellationToken ct)
    {
        var session = Process.GetCurrentProcess().SessionId;
        var running = app.Processes.SelectMany(Process.GetProcessesByName).Where(p => p.SessionId == session).ToList();
        if (running.Count == 0) return;
        foreach (var p in running) { try { p.CloseMainWindow(); } catch (InvalidOperationException) { } }
        using var grace = CancellationTokenSource.CreateLinkedTokenSource(ct);
        grace.CancelAfter(TimeSpan.FromSeconds(5));
        foreach (var p in running)
        {
            using (p)
            {
                await WaitAsync(p, grace.Token);
                ct.ThrowIfCancellationRequested();
                if (p.HasExited) continue;
                if (!app.ForceClose) throw new InvalidOperationException(Txt.DebloatStillOpen(app.Name));
                try { p.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                using var killed = CancellationTokenSource.CreateLinkedTokenSource(ct);
                killed.CancelAfter(TimeSpan.FromSeconds(10));
                await WaitAsync(p, killed.Token);
                ct.ThrowIfCancellationRequested();
                if (!p.HasExited) throw new InvalidOperationException(Txt.DebloatStillOpen(app.Name));
            }
        }
    }

    static async Task WaitAsync(Process p, CancellationToken token)
    {
        try { await p.WaitForExitAsync(token); }
        catch (OperationCanceledException) { }
    }
}
