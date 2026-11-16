using System.ComponentModel;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Interop;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Settings;
using KosToolbox.Core.Text;
using Microsoft.Win32;

namespace KosToolbox.Core.Startup;

/// <summary>Previous startup type of a service or driver disabled by the Startup page.</summary>
public sealed class SavedStart
{
    public int Start { get; set; }
    public int? Delayed { get; set; }
    public DateTime SavedAt { get; set; }
}

/// <summary>startup-backups.json: previous type per service, so "Enable" goes back to exactly what it was.</summary>
public static class StartupBackups
{
    static readonly object Gate = new();

    public static SavedStart? Get(string service)
    {
        lock (Gate) return JsonFile.Load<Dictionary<string, SavedStart>>(AppPaths.StartupBackups).GetValueOrDefault(Key(service));
    }

    public static void Save(string service, int start, int? delayed)
    {
        lock (Gate)
        {
            var all = JsonFile.Load<Dictionary<string, SavedStart>>(AppPaths.StartupBackups);
            all[Key(service)] = new SavedStart { Start = start, Delayed = delayed, SavedAt = DateTime.Now };
            JsonFile.Save(AppPaths.StartupBackups, all);
        }
    }

    static string Key(string service) => "service:" + service.ToLowerInvariant();
}

/// <summary>
/// Win32 services or drivers. The startup state (Start in the registry) is kept separate from the SCM's run
/// state: a stopped service with manual start is still "Enabled".
/// </summary>
public sealed class ServiceStartupProvider(bool drivers) : IStartupProvider
{
    const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    public string Name => drivers ? Txt.SourceDrivers : Txt.SourceServices;
    public StartupGroup Group => drivers ? StartupGroup.Drivers : StartupGroup.Services;
    // Services also appear in Standard (only non-Microsoft ones); drivers only in Advanced.
    public bool AdvancedOnly => drivers;

    /// <summary>The file's CompanyName (empty if it can't be read).</summary>
    internal static string? Publisher(string? image)
    {
        try { return image is not null && File.Exists(image) ? System.Diagnostics.FileVersionInfo.GetVersionInfo(image).CompanyName?.Trim() : null; }
        catch { return null; }
    }

    public IEnumerable<StartupEntry> Collect(bool advanced)
    {
        var list = new List<StartupEntry>();
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        foreach (var svc in ServiceControl.Enumerate(drivers ? ServiceControl.TypeDriver : ServiceControl.TypeWin32))
        {
            int? start = null, error = null, delayed = null;
            string? imagePath = null, serviceDll = null, readProblem = null;
            var trigger = false;
            try
            {
                using var key = root.OpenSubKey($@"{ServicesKey}\{svc.Name}");
                if (key is null) readProblem = Txt.ServiceKeyMissing;
                else
                {
                    start = key.GetValue("Start") as int?;
                    error = key.GetValue("ErrorControl") as int?;
                    delayed = key.GetValue("DelayedAutoStart") as int?;
                    imagePath = key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    using (var t = key.OpenSubKey("TriggerInfo")) trigger = t is not null;
                    using (var p = key.OpenSubKey("Parameters")) serviceDll = p?.GetValue("ServiceDll") as string;
                }
            }
            catch (Exception ex) { readProblem = Txt.ServiceKeyUnreadable(ex.Message); }

            var image = ResolveImage(svc.Name, imagePath);
            var publisher = drivers ? null : Publisher(image);
            // Standard: only services installed by others (the executable's vendor isn't Microsoft). The folder alone
            // wouldn't work: NVIDIA Display Container and VMware services live inside the Windows folder.
            // With no readable vendor, the folder decides (outside Windows = installed by others).
            if (!advanced && !drivers && (image is null
                || (publisher is null ? CommandLine.InWindowsFolder(image) : publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)))) continue;
            var critical = Critical(start, error);
            var details = new List<KeyValuePair<string, string>>
            {
                RunKeyProvider.Pair(Txt.DetailServiceName, svc.Name),
                RunKeyProvider.Pair(Txt.DetailDisplayName, svc.DisplayName),
                RunKeyProvider.Pair(Txt.DetailRegistryKey, $@"HKLM\{ServicesKey}\{svc.Name}"),
            };
            if (serviceDll is not null) details.Add(RunKeyProvider.Pair(Txt.DetailServiceDll, serviceDll));
            if (error is { } ec) details.Add(RunKeyProvider.Pair(Txt.DetailErrorControl, Txt.ErrorControlName(ec)));
            var saved = start == 4 ? StartupBackups.Get(svc.Name) : null;
            if (saved is not null) details.Add(RunKeyProvider.Pair(Txt.DetailPreviousStart, $"{Txt.StartName(saved.Start, saved.Delayed == 1, false)} ({saved.SavedAt:g})"));

            list.Add(new StartupEntry
            {
                Provider = this,
                Id = $"{(drivers ? "driver" : "service")}|{svc.Name}",
                Name = svc.Name,
                Location = $@"HKLM\{ServicesKey}\{svc.Name}",
                Source = drivers ? Txt.SourceDriver : Txt.SourceService,
                Command = imagePath ?? (drivers ? Txt.DriverDefaultImage : ""),
                ImagePath = image,
                Enabled = readProblem is not null || start is null or < 0 or > 4 ? null : start != 4,
                StartupType = start is { } s and >= 0 and <= 4 ? Txt.StartName(s, delayed == 1, trigger) : Txt.Unknown,
                RunState = Txt.RunStateName(svc.State),
                StatusDetail = readProblem ?? (start is { } v and >= 0 and <= 4 ? Txt.StartDetail(v) : Txt.StartMissing),
                ReadOnlyReason = readProblem,
                MachineWide = true,
                CriticalReason = critical,
                WindowsComponent = CommandLine.InWindowsFolder(image),
                Publisher = publisher,
                ServiceName = svc.Name,
                Start = start,
                Delayed = delayed == 1,
                Details = details,
            });
        }
        return list;
    }

    /// <summary>Boot/System, or a failure Windows treats as severe (ErrorControl 2 or 3).</summary>
    internal static string? Critical(int? start, int? errorControl) =>
        start is 0 or 1 ? Txt.CriticalBootStart
        : errorControl is >= 2 ? Txt.CriticalErrorControl
        : null;

    /// <summary>Path of the service/driver file. Without ImagePath, a driver is System32\drivers\name.sys.</summary>
    string? ResolveImage(string name, string? imagePath)
    {
        if (imagePath is null)
            return drivers ? Path.Combine(Environment.SystemDirectory, "drivers", name + ".sys") : null;
        if (imagePath.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase) || imagePath.StartsWith(@"SysWOW64\", StringComparison.OrdinalIgnoreCase))
            imagePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), imagePath);
        return CommandLine.Executable(imagePath);
    }

    public async Task SetEnabledAsync(StartupEntry e, bool enable, CancellationToken ct)
    {
        var name = e.ServiceName!;
        var (start, delayed) = ReadStart(name);
        int target; int? targetDelayed = null;
        if (!enable)
        {
            if (start == 4) return;
            StartupBackups.Save(name, start ?? 3, delayed);
            target = 4;
        }
        else
        {
            var saved = StartupBackups.Get(name);
            target = saved?.Start ?? 3;
            if (target == 2) targetDelayed = saved?.Delayed ?? 0;
        }
        var job = new ServiceStartChange { Name = name, Start = target, Delayed = targetDelayed };
        if (Elevation.IsAdmin) await Task.Run(() => Apply(job), ct).ConfigureAwait(false);
        else await ElevatedRunner.RunAsync(new ElevatedJob { Service = job }, ct).ConfigureAwait(false);

        var (after, afterDelayed) = ReadStart(name);
        if (after != target || (targetDelayed is { } d && (afterDelayed ?? 0) != d))
            throw new InvalidOperationException(Txt.ChangeNotConfirmed);
    }

    internal static (int? Start, int? Delayed) ReadStart(string name)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey($@"{ServicesKey}\{name}") ?? throw new InvalidOperationException(Txt.ServiceKeyMissing);
        return (key.GetValue("Start") as int?, key.GetValue("DelayedAutoStart") as int?);
    }

    /// <summary>
    /// Through the SCM (ChangeServiceConfig). Protected services that refuse the SCM get the same value in the registry,
    /// using the KOS scripts' TrustedInstaller fallback when needed.
    /// </summary>
    internal static void Apply(ServiceStartChange c)
    {
        try
        {
            ServiceControl.SetStartType(c.Name, c.Start, c.Delayed is null ? null : c.Delayed == 1);
            Log.Info($"Service {c.Name}: Start = {c.Start}{(c.Delayed is null ? "" : $", DelayedAutoStart = {c.Delayed}")} (Service Control Manager)");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            Log.Warn($"Service {c.Name}: the Service Control Manager denied the change; writing the registry value instead.");
            RegistrySetting.ServiceStart(c.Name).Write(c.Start.ToString());
            if (c.Delayed is { } d)
                new RegistrySetting(Hive.LocalMachine, $@"{ServicesKey}\{c.Name}", "DelayedAutoStart", RegistryValueKind.DWord).Write(d.ToString());
            Log.Info($"Service {c.Name}: Start = {c.Start} (registry)");
        }
    }

    /// <summary>Elevated-side validation: existing service, Start 0–4, Boot/System only for drivers.</summary>
    internal static void Validate(ServiceStartChange c)
    {
        if (c.Name.Length is 0 or > 256 || c.Name.IndexOfAny(new[] { '\\', '/' }) >= 0) throw new InvalidOperationException($"Invalid service name: {c.Name}");
        if (c.Start is < 0 or > 4) throw new InvalidOperationException($"Invalid start type: {c.Start}");
        if (c.Delayed is not null && (c.Start != 2 || c.Delayed is not (0 or 1))) throw new InvalidOperationException("DelayedAutoStart applies only to automatic start.");
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey($@"{ServicesKey}\{c.Name}") ?? throw new InvalidOperationException($"The service {c.Name} doesn't exist.");
        var type = key.GetValue("Type") as int? ?? 0;
        if (c.Start is 0 or 1 && (type & (int)ServiceControl.TypeDriver) == 0)
            throw new InvalidOperationException($"Boot and System start types are only valid for drivers ({c.Name}).");
    }
}
