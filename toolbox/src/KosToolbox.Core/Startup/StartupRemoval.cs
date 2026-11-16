using System.Runtime.InteropServices;
using System.Text.Json;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Interop;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Settings;
using Microsoft.Win32;

namespace KosToolbox.Core.Startup;

/// <summary>
/// Removes a startup entry for good ("Disable" only turns it off). Before deleting, keeps a copy in
/// %LocalAppData%\KOS\Toolbox\startup-removed\: the Run value (JSON), the shortcut (the file itself), the task (XML) or the
/// service key (.reg), so it can be restored. Drivers, critical items, policy items and Microsoft items are not removed.
/// </summary>
public static class StartupRemoval
{
    static string BackupRoot => Path.Combine(AppPaths.DataRoot, "startup-removed");

    /// <summary>Null if it can be removed; otherwise the reason.</summary>
    public static string? CannotRemove(StartupEntry e)
    {
        if (e.Group == StartupGroup.Drivers) return "Drivers can't be removed here: removing the wrong one can stop Windows from starting.";
        if (e.Provider is not (RunKeyProvider or StartupFolderProvider or ScheduledTaskProvider or ServiceStartupProvider)) return e.ReadOnlyReason ?? "This entry can't be removed.";
        if (e.CriticalReason is not null) return "This entry is critical for Windows to start; it can't be removed.";
        if (e.Provider is PolicyRunProvider) return "This entry is set by a policy; remove it in the policy that created it.";
        if (e.Id.StartsWith("run|", StringComparison.Ordinal) && e.ValueName is null) return "This entry can't be removed.";
        if (e.Group == StartupGroup.ScheduledTasks && e.WindowsComponent) return "Windows' own scheduled tasks can't be removed here (you can disable them).";
        if (e.Group == StartupGroup.Services && (e.FromMicrosoft || e.Publisher is null && e.WindowsComponent))
            return "Windows' own services can't be removed here (you can disable them).";
        var needsAdmin = e.MachineWide || e.Group is StartupGroup.ScheduledTasks or StartupGroup.Services;
        return needsAdmin && !Elevation.IsAdmin ? "Removing this entry needs the toolbox running as administrator." : null;
    }

    public static async Task<string> RemoveAsync(StartupEntry e, CancellationToken ct = default)
    {
        if (CannotRemove(e) is { } reason) throw new InvalidOperationException(reason);
        var folder = Path.Combine(BackupRoot, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Safe(e.Name)}");
        Directory.CreateDirectory(folder);
        Log.Info($"Startup: remove {e.Id} (backup in {folder})");
        await Task.Run(() =>
        {
            switch (e.Provider)
            {
                case RunKeyProvider: RemoveRunValue(e, folder); break;
                case StartupFolderProvider: RemoveShortcut(e, folder); break;
                case ScheduledTaskProvider: RemoveTask(e, folder); break;
                case ServiceStartupProvider: RemoveService(e, folder); break;
                default: throw new InvalidOperationException("This entry can't be removed.");
            }
        }, ct).ConfigureAwait(false);
        Log.Info($"Startup: removed {e.Id}");
        return folder;
    }

    internal static string Safe(string name)
    {
        var s = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        return s.Length == 0 ? "entry" : s.Length > 60 ? s[..60] : s;
    }

    static RegistryKey Open(Hive hive) =>
        RegistryKey.OpenBaseKey(hive == Hive.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);

    /// <summary>"HKLM\SOFTWARE\..." → (hive, path).</summary>
    static (Hive Hive, string Path) Split(string location) =>
        location.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase) ? (Hive.LocalMachine, location[5..]) : (Hive.CurrentUser, location[5..]);

    static void RemoveRunValue(StartupEntry e, string folder)
    {
        var (hive, path) = Split(e.Location);
        using var root = Open(hive);
        using var key = root.OpenSubKey(path, writable: true) ?? throw new InvalidOperationException($"{e.Location} doesn't exist anymore.");
        var raw = key.GetValue(e.ValueName!, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
            ?? throw new InvalidOperationException($"{e.Name} isn't in {e.Location} anymore.");
        File.WriteAllText(Path.Combine(folder, "run-value.json"), JsonSerializer.Serialize(new
        {
            Key = e.Location, Name = e.ValueName, Kind = key.GetValueKind(e.ValueName!).ToString(), Value = raw.ToString(),
        }, new JsonSerializerOptions { WriteIndented = true }));
        key.DeleteValue(e.ValueName!);
        DeleteApproved(hive, e.ApprovedSubKey, e.ValueName!);
        if (key.GetValueNames().Contains(e.ValueName!)) throw new InvalidOperationException(Text.Txt.ChangeNotConfirmed);
    }

    static void RemoveShortcut(StartupEntry e, string folder)
    {
        var file = e.Id["folder|".Length..];
        if (!File.Exists(file)) throw new InvalidOperationException($"{file} doesn't exist anymore.");
        File.Move(file, Path.Combine(folder, Path.GetFileName(file)));
        DeleteApproved(Enum.Parse<Hive>(e.Hive!), e.ApprovedSubKey, e.ValueName!);
    }

    /// <summary>Task Manager's enable flag is orphaned without the entry: it goes with it.</summary>
    static void DeleteApproved(Hive hive, string? approvedSubKey, string name)
    {
        if (approvedSubKey is null) return;
        try
        {
            using var root = Open(hive);
            using var key = root.OpenSubKey($@"{StartupApproved.Root}\{approvedSubKey}", writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
        catch (Exception ex) { Log.Warn($"Startup: couldn't remove the StartupApproved flag of {name}: {ex.Message}"); }
    }

    static void RemoveTask(StartupEntry e, string folder) => RemoveTask(e.TaskPath!, Safe(e.Name), folder);

    /// <summary>Saves the task XML to <paramref name="folder"/> and deletes the task (also used by the cleanup of program leftovers).</summary>
    internal static void RemoveTask(string path, string fileName, string folder)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException(Text.Txt.TaskSchedulerMissing);
        dynamic service = Activator.CreateInstance(type)!;
        try
        {
            service.Connect();
            var sep = path.LastIndexOf('\\');
            dynamic taskFolder = service.GetFolder(sep <= 0 ? "\\" : path[..sep]);
            dynamic task = taskFolder.GetTask(path[(sep + 1)..]);
            File.WriteAllText(Path.Combine(folder, fileName + ".xml"), (string)task.Xml);
            taskFolder.DeleteTask(path[(sep + 1)..], 0);
        }
        catch (COMException ex) when ((uint)ex.HResult == 0x80070005) { throw new UnauthorizedAccessException(ex.Message, ex); }
        finally { Marshal.FinalReleaseComObject(service); }
    }

    static void RemoveService(StartupEntry e, string folder) => RemoveService(e.ServiceName!, folder);

    /// <summary>Exports the service key to .reg in <paramref name="folder"/>, stops and deletes the service.</summary>
    internal static void RemoveService(string name, string folder)
    {
        if (name.Length == 0 || name.IndexOfAny(new[] { '\\', '/', '"' }) >= 0) throw new InvalidOperationException($"Invalid service name: {name}");
        var reg = Path.Combine(folder, Safe(name) + ".reg");
        var export = ProcessRunner.Run(Path.Combine(Environment.SystemDirectory, "reg.exe"),
            $"export \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\{name}\" {ProcessRunner.Quote(reg)} /y", TimeSpan.FromSeconds(30));
        if (export.ExitCode != 0 || !File.Exists(reg)) throw new InvalidOperationException($"Couldn't save the service's registry key first: {export.Output.Trim()}");
        // Stop first: a running service only leaves the list once it stops.
        ProcessRunner.Run(Path.Combine(Environment.SystemDirectory, "sc.exe"), $"stop \"{name}\"", TimeSpan.FromSeconds(30));
        var delete = ProcessRunner.Run(Path.Combine(Environment.SystemDirectory, "sc.exe"), $"delete \"{name}\"", TimeSpan.FromSeconds(30));
        if (delete.ExitCode != 0) throw new InvalidOperationException($"sc delete {name}: {delete.Output.Trim()}");
    }
}
