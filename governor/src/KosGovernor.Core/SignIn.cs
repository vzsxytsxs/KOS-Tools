using System.Diagnostics;
using System.Security;
using System.Security.Principal;

namespace KosGovernor.Core;

/// <summary>
/// "KOS Governor" scheduled task that opens the elevated engine at sign-in. Through XML because schtasks' default would kill the
/// engine after 72 h, wouldn't start on battery and would run at below-normal priority.
/// </summary>
public static class SignIn
{
    public const string TaskName = "KOS Governor";
    static string Schtasks => Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    /// <summary>
    /// The task runs the .exe as administrator without UAC: in a folder that standard users can modify (Downloads, Documents),
    /// swapping the file would give administrator to any program.
    /// </summary>
    public static string? FolderProblem(string exe) =>
        exe.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? null : "Starting with Windows needs KOS Governor installed under Program Files (a folder only administrators can change).";

    public static bool Enabled() => Run($"/Query /TN \"{TaskName}\"").ExitCode == 0;

    public static void Enable()
    {
        // The engine's own executable; without it (interface run from its build folder), the interface's with --engine.
        var engine = File.Exists(EngineHost.EnginePath);
        var exe = engine ? EngineHost.EnginePath : Environment.ProcessPath ?? throw new InvalidOperationException("The governor's path is unknown.");
        if (FolderProblem(exe) is { } problem) throw new InvalidOperationException(problem);
        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>KOS Governor: applies process rules.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(exe)}</Command>{(engine ? "" : "<Arguments>--engine</Arguments>")}</Exec></Actions>
            </Task>
            """;
        // In the protected folder: in the user's %TEMP%, another program could swap the XML before schtasks reads it.
        ConfigStore.PrepareDirectory();
        var file = Path.Combine(ConfigStore.Directory, $"task-{Guid.NewGuid():N}.xml");
        File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
        try
        {
            var r = Run($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F");
            if (r.ExitCode != 0) throw new InvalidOperationException($"schtasks /Create failed ({r.ExitCode}): {r.Output.Trim()}");
        }
        finally { File.Delete(file); }
        Log.Info("Sign-in task created.");
    }

    public static void Disable()
    {
        var r = Run($"/Delete /TN \"{TaskName}\" /F");
        if (r.ExitCode != 0 && Enabled()) throw new InvalidOperationException($"schtasks /Delete failed ({r.ExitCode}): {r.Output.Trim()}");
        Log.Info("Sign-in task removed.");
    }

    static (int ExitCode, string Output) Run(string args)
    {
        using var p = Process.Start(new ProcessStartInfo(Schtasks, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return (p.HasExited ? p.ExitCode : -1, output);
    }
}
