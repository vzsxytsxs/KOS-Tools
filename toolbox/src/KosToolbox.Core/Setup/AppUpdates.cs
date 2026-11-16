using KosToolbox.Core.Apps;
using KosToolbox.Core.Persistence;

namespace KosToolbox.Core.Setup;

/// <summary>A Setup program installed in an older version than its source offers.</summary>
public sealed record AppUpdate(SetupApp App, string Installed, string Available, string Source);

/// <summary>
/// Install › Updates: the catalog programs with a newer version. Chocolatey's own list for what it installed, and for downloaded
/// programs the installed version ("Programs and Features") against the newest GitHub release or winget manifest. Programs that
/// update themselves through a fixed link (Discord, Steam's own updater) aren't listed.
/// </summary>
public static class AppUpdates
{
    /// <summary>Returns the updates and the sources that couldn't be read (shown, not fatal).</summary>
    public static async Task<(IReadOnlyList<AppUpdate> Updates, IReadOnlyList<string> Problems)> CheckAsync(IReadOnlyList<InstalledProgram> programs, CancellationToken ct)
    {
        var updates = new List<AppUpdate>();
        var problems = new List<string>();
        var checks = SetupCatalog.All.Where(a => a.Latest is not null && a.Choco is null)
            .Select(a => (App: a, Installed: SetupCatalog.Installed(a, programs)?.Version))
            .Where(x => !string.IsNullOrWhiteSpace(x.Installed))
            .Select(async x =>
            {
                try { return (x.App, x.Installed, Latest: await x.App.Latest!(Downloader.Http, ct).ConfigureAwait(false), Error: (string?)null); }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or KeyNotFoundException or System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    return (x.App, x.Installed, Latest: "", Error: $"{x.App.Name}: {ex.Message}");
                }
            }).ToList();
        var chocoTask = Chocolatey.Exe is { } exe ? Chocolatey.OutdatedAsync(exe, ct) : null;

        foreach (var (app, installed, latest, error) in await Task.WhenAll(checks).ConfigureAwait(false))
        {
            if (error is not null) problems.Add(error);
            else if (SetupCatalog.IsNewer(latest, installed!)) updates.Add(new AppUpdate(app, installed!, System.Text.RegularExpressions.Regex.Replace(latest, @"^\D+", ""), "Download"));
        }
        if (chocoTask is not null)
        {
            try
            {
                foreach (var (id, current, available, pinned) in await chocoTask.ConfigureAwait(false))
                    if (!pinned && SetupCatalog.All.FirstOrDefault(a => string.Equals(a.Choco, id, StringComparison.OrdinalIgnoreCase)) is { } app)
                        updates.Add(new AppUpdate(app, current, available, "Chocolatey"));
            }
            catch (InvalidOperationException ex) { problems.Add($"Chocolatey: {ex.Message}"); }
        }
        Log.Info($"Setup updates: {updates.Count} found{(problems.Count > 0 ? $"; not checked: {string.Join("; ", problems)}" : "")}.");
        return (updates.OrderBy(u => u.App.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), problems);
    }
}
