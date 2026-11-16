using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;

namespace KosToolbox.Core.Startup;

public sealed record StartupScan(IReadOnlyList<StartupEntry> Entries, IReadOnlyList<string> Problems);

/// <summary>Collects the startup entries, one provider per source. A source with an error doesn't stop the others.</summary>
public static class StartupManager
{
    public static IReadOnlyList<IStartupProvider> Providers { get; } = new IStartupProvider[]
    {
        new RunKeyProvider(),
        new PolicyRunProvider(),
        new StartupFolderProvider(),
        new WinlogonProvider(),
        new ActiveSetupProvider(),
        new ScheduledTaskProvider(),
        new ServiceStartupProvider(drivers: false),
        new ServiceStartupProvider(drivers: true),
        new SystemHookProvider(),
        new WmiSubscriptionProvider(),
    };

    /// <summary>Providers used for the chosen mode and filters (drivers only when explicitly requested).</summary>
    public static IReadOnlyList<IStartupProvider> Active(bool advanced, bool includeDrivers) =>
        Providers.Where(p => (advanced || !p.AdvancedOnly) && (includeDrivers || p.Group != StartupGroup.Drivers)).ToList();

    /// <summary><paramref name="progress"/> receives (completed, total, source being read).</summary>
    public static StartupScan Scan(bool advanced, bool includeDrivers, IProgress<(int Done, int Total, string Source)>? progress = null,
        CancellationToken ct = default)
    {
        var providers = Active(advanced, includeDrivers);
        var entries = new List<StartupEntry>();
        var problems = new List<string>();
        for (var i = 0; i < providers.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var p = providers[i];
            progress?.Report((i, providers.Count, p.Name));
            try
            {
                entries.AddRange(p.Collect(advanced));
                if (p is ScheduledTaskProvider { Skipped: > 0 } tasks) problems.Add(Txt.TasksSkipped(tasks.Skipped));
            }
            catch (Exception ex)
            {
                Log.Error($"Startup source {p.Name} failed", ex);
                problems.Add(Txt.SourceFailed(p.Name, ex.Message));
            }
        }
        progress?.Report((providers.Count, providers.Count, ""));
        Log.Info($"Startup scan ({(advanced ? "advanced" : "standard")}{(includeDrivers ? ", drivers" : "")}): {entries.Count} entries, {problems.Count} problems.");
        return new StartupScan(entries, problems);
    }

    public static async Task SetEnabledAsync(StartupEntry entry, bool enable, CancellationToken ct = default)
    {
        if (!entry.CanToggle) throw new InvalidOperationException(entry.ReadOnlyReason ?? Txt.StateUnknownCantChange);
        Log.Info($"Startup: {(enable ? "enable" : "disable")} {entry.Id}: started");
        try
        {
            await entry.Provider.SetEnabledAsync(entry, enable, ct).ConfigureAwait(false);
            Log.Info($"Startup: {(enable ? "enable" : "disable")} {entry.Id}: verified");
        }
        catch (Exception ex) when (ex is not Execution.ElevationCancelledException)
        {
            Log.Error($"Startup: {(enable ? "enable" : "disable")} {entry.Id}: failed", ex);
            throw;
        }
    }
}
