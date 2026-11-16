using System.Runtime.InteropServices;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;

namespace KosToolbox.Core.Startup;

/// <summary>
/// Scheduled tasks with a system startup or logon trigger (in Advanced mode, every task), through the Task Scheduler COM API.
/// Disabling uses the task's own Enabled flag, which preserves triggers and actions; enabling restores it.
/// </summary>
public sealed class ScheduledTaskProvider : IStartupProvider
{
    const int TriggerBoot = 8, TriggerLogon = 9, ActionExec = 0, ActionCom = 5, EnumHidden = 1;

    public string Name => Txt.SourceScheduledTasks;
    public StartupGroup Group => StartupGroup.ScheduledTasks;
    public bool AdvancedOnly => false;

    /// <summary>Tasks that couldn't be read in the last collection (no permission, for example).</summary>
    public int Skipped { get; private set; }

    static object Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException(Txt.TaskSchedulerMissing);
        dynamic service = Activator.CreateInstance(type) ?? throw new InvalidOperationException(Txt.TaskSchedulerMissing);
        service.Connect();
        return (object)service;
    }

    public IEnumerable<StartupEntry> Collect(bool advanced)
    {
        var list = new List<StartupEntry>();
        Skipped = 0;
        object service = Connect();
        try { Walk((object)((dynamic)service).GetFolder("\\"), advanced, list); }
        finally { Marshal.FinalReleaseComObject(service); }
        if (Skipped > 0) Log.Warn($"Startup: {Skipped} scheduled tasks couldn't be read.");
        return list;
    }

    void Walk(object folderObject, bool advanced, List<StartupEntry> list)
    {
        dynamic folder = folderObject;
        dynamic tasks = folder.GetTasks(advanced ? EnumHidden : 0);
        int count = tasks.Count;
        for (var i = 1; i <= count; i++)
        {
            try { if (Read((object)tasks[i], advanced) is { } e) list.Add(e); }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                Skipped++;
            }
        }
        dynamic folders = folder.GetFolders(0);
        int subCount = folders.Count;
        for (var i = 1; i <= subCount; i++)
        {
            dynamic sub = folders[i];
            string path = sub.Path;
            // Standard mode hides Windows' own tasks (\Microsoft\...).
            if (!advanced && path.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
            try { Walk((object)sub, advanced, list); }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException) { Skipped++; }
        }
    }

    StartupEntry? Read(object taskObject, bool advanced)
    {
        dynamic task = taskObject;
        dynamic definition = task.Definition;
        dynamic triggers = definition.Triggers;
        var kinds = new List<string>();
        int triggerCount = triggers.Count;
        for (var i = 1; i <= triggerCount; i++)
        {
            dynamic t = triggers[i];
            int type = t.Type;
            // Standard mode: startup and logon triggers only; Advanced lists every task with all its triggers.
            if (type is not (TriggerBoot or TriggerLogon) && !advanced) continue;
            bool triggerEnabled = t.Enabled;
            var label = type switch { TriggerBoot => Txt.TypeAtStartup, TriggerLogon => Txt.TypeAtLogon, _ => Txt.TriggerKind(type) };
            kinds.Add(triggerEnabled ? label : Txt.TriggerDisabled(label));
        }
        if (kinds.Count == 0) { if (!advanced) return null; kinds.Add(Txt.TypeOnDemand); }

        var commands = new List<string>();
        string? image = null;
        dynamic actions = definition.Actions;
        int actionCount = actions.Count;
        for (var i = 1; i <= actionCount; i++)
        {
            dynamic a = actions[i];
            int type = a.Type;
            if (type == ActionExec)
            {
                string path = a.Path ?? "";
                string args = a.Arguments ?? "";
                commands.Add(args.Length == 0 ? path : $"{path} {args}");
                image ??= CommandLine.Executable(path.Contains(' ') && !path.StartsWith('"') ? $"\"{path}\"" : path);
            }
            else if (type == ActionCom) commands.Add(Txt.TaskComHandler((string)a.ClassId));
            else commands.Add(Txt.TaskOtherAction(type));
        }

        string fullPath = task.Path;
        string name = task.Name;
        bool enabled = task.Enabled;
        int state = task.State;
        var folder = fullPath.Length > name.Length ? fullPath[..^name.Length] : "\\";
        return new StartupEntry
        {
            Provider = this,
            Id = $"task|{fullPath}",
            Name = name,
            Location = folder,
            Source = Txt.SourceTask,
            Command = string.Join("  |  ", commands),
            ImagePath = image,
            Enabled = enabled,
            StartupType = string.Join(", ", kinds),
            RunState = Txt.TaskState(state),
            StatusDetail = Txt.TaskEnabledDetail(enabled),
            MachineWide = true,
            WindowsComponent = fullPath.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase),
            TaskPath = fullPath,
            Details = new[] { RunKeyProvider.Pair(Txt.DetailTaskPath, fullPath) },
        };
    }

    public async Task SetEnabledAsync(StartupEntry e, bool enable, CancellationToken ct)
    {
        try { await Task.Run(() => SetEnabled(e.TaskPath!, enable), ct).ConfigureAwait(false); }
        catch (UnauthorizedAccessException) when (!Elevation.IsAdmin)
        {
            // System tasks can only be changed as administrator.
            await ElevatedRunner.RunAsync(new ElevatedJob { TaskChange = new TaskToggle { Path = e.TaskPath!, Enabled = enable } }, ct).ConfigureAwait(false);
        }
        if (IsEnabled(e.TaskPath!) != enable) throw new InvalidOperationException(Txt.ChangeNotConfirmed);
    }

    /// <summary>Task and program of each "start a program" action, outside \Microsoft (leftovers of uninstalled programs).</summary>
    internal static List<(string Task, string Program)> ExecActions()
    {
        var list = new List<(string, string)>();
        object service = Connect();
        try { WalkExec((object)((dynamic)service).GetFolder("\\"), list); }
        finally { Marshal.FinalReleaseComObject(service); }
        return list;
    }

    static void WalkExec(object folderObject, List<(string, string)> list)
    {
        dynamic folder = folderObject;
        dynamic tasks = folder.GetTasks(EnumHidden);
        int count = tasks.Count;
        for (var i = 1; i <= count; i++)
        {
            try
            {
                dynamic task = tasks[i];
                dynamic actions = task.Definition.Actions;
                int actionCount = actions.Count;
                for (var j = 1; j <= actionCount; j++)
                {
                    dynamic a = actions[j];
                    string program = (int)a.Type == ActionExec ? a.Path ?? "" : "";
                    if (program.Length > 0) list.Add(((string)task.Path, program));
                }
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        }
        dynamic folders = folder.GetFolders(0);
        int subCount = folders.Count;
        for (var i = 1; i <= subCount; i++)
        {
            dynamic sub = folders[i];
            string path = sub.Path;
            if (path.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
            try { WalkExec((object)sub, list); }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException) { }
        }
    }

    static object Open(object serviceObject, string path)
    {
        dynamic service = serviceObject;
        var sep = path.LastIndexOf('\\');
        var folderPath = sep <= 0 ? "\\" : path[..sep];
        return (object)service.GetFolder(folderPath).GetTask(path[(sep + 1)..]);
    }

    internal static void SetEnabled(string path, bool enable)
    {
        object service = Connect();
        try
        {
            dynamic task = Open(service, path);
            task.Enabled = enable;
            Log.Info($"Scheduled task {path}: Enabled = {enable}");
        }
        catch (COMException ex) when ((uint)ex.HResult == 0x80070005) { throw new UnauthorizedAccessException(ex.Message, ex); }
        finally { Marshal.FinalReleaseComObject(service); }
    }

    /// <summary>Each task's Enabled flag through one connection: null for a task that doesn't exist; one that can't be read goes to <paramref name="errors"/>.</summary>
    internal static Dictionary<string, bool?> EnabledStates(IEnumerable<string> paths, out Dictionary<string, string> errors)
    {
        var states = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        object service = Connect();
        try
        {
            foreach (var path in paths)
            {
                try { states[path] = (bool)((dynamic)Open(service, path)).Enabled; }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { states[path] = null; }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException) { errors[path] = ex.Message; }
            }
        }
        finally { Marshal.FinalReleaseComObject(service); }
        return states;
    }

    /// <summary>Deletes a task (one the KOS deletes and Windows put back). A task that's already gone is left as it is.</summary>
    internal static void Delete(string path)
    {
        object service = Connect();
        try
        {
            var sep = path.LastIndexOf('\\');
            ((dynamic)service).GetFolder(sep <= 0 ? "\\" : path[..sep]).DeleteTask(path[(sep + 1)..], 0);
            Log.Info($"Scheduled task {path}: deleted");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        catch (COMException ex) when ((uint)ex.HResult == 0x80070005) { throw new UnauthorizedAccessException(ex.Message, ex); }
        finally { Marshal.FinalReleaseComObject(service); }
    }

    internal static bool IsEnabled(string path)
    {
        object service = Connect();
        try { return (bool)((dynamic)Open(service, path)).Enabled; }
        finally { Marshal.FinalReleaseComObject(service); }
    }

    /// <summary>Elevated-side validation: a well-formed path of a task that exists.</summary>
    internal static void ValidateStartupTask(string path)
    {
        if (!path.StartsWith('\\') || path.Contains("..")) throw new InvalidOperationException($"Invalid task path: {path}");
        // Advanced mode lists every task, so any existing one may be switched; only its Enabled flag is ever changed.
        object service = Connect();
        try { _ = (string)((dynamic)Open(service, path)).Path; }
        catch (Exception ex) when (ex is COMException or FileNotFoundException) { throw new InvalidOperationException($"No such task: {path}", ex); }
        finally { Marshal.FinalReleaseComObject(service); }
    }
}
