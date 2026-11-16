using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;

namespace KosToolbox.Core.Startup;

/// <summary>
/// Delayed start: a Run entry is turned off through StartupApproved (the value stays, as with Disable) and a scheduled task starts
/// the same command a while after signing in, so launchers and RGB suites don't compete with the desktop. A user's entry gets a
/// task of that account; an all-users entry gets one for every member of Users, as the Run entry was. Removing the delay deletes
/// the task and turns the entry back on.
/// </summary>
public static class DelayedStartup
{
    public const string Prefix = "KOS delayed start - ";
    public static readonly int[] Choices = { 30, 60, 120 };
    const string UsersSid = "S-1-5-32-545";
    static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    static string Schtasks => Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    /// <summary>A Run value (not RunOnce) whose state can be changed.</summary>
    public static bool CanDelay(StartupEntry e) => e.Provider is RunKeyProvider && e.ApprovedSubKey is not null && e.CanToggle;

    /// <summary>One task per Run value: the same name in HKCU, HKLM and the 32-bit HKLM key are different entries.</summary>
    public static string TaskName(StartupEntry e) =>
        $"{Prefix}{Regex.Replace(e.Name, @"[\\/:*?""<>|]", "_")} [{(e.MachineWide ? "HKLM" : "HKCU")} {e.ApprovedSubKey}]";

    /// <summary>Seconds of this entry's delay task; null = none.</summary>
    public static int? Delay(StartupEntry e) => Query(e) is { } xml ? ParseDelay(xml) : null;

    static string? Query(StartupEntry e)
    {
        var r = ProcessRunner.Run(Schtasks, $"/Query /TN {ProcessRunner.Quote(TaskName(e))} /XML");
        return r.ExitCode == 0 ? r.Output : null;
    }

    public static async Task SetAsync(StartupEntry e, int seconds, CancellationToken ct)
    {
        if (!CanDelay(e)) throw new InvalidOperationException("Only a Run entry that can be turned off can start later.");
        if (seconds is < 5 or > 3600) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (e.MachineWide && !Elevation.IsAdmin) throw new InvalidOperationException("A delayed start for all users needs the toolbox running as administrator.");
        var (file, args) = CommandLine.Split(e.Command) ?? throw new InvalidOperationException($"The program in \"{e.Command}\" wasn't found.");
        var user = e.MachineWide ? null : WindowsIdentity.GetCurrent().User!.Value;
        var xml = Xml(user, file, args, seconds, e.Name);
        // Elevated, the definition goes to an admin-only folder: in %TEMP% a program of this user could rewrite it before schtasks reads it.
        var folder = Path.GetTempPath();
        if (Elevation.IsAdmin)
        {
            folder = Path.Combine(AppPaths.TheKosRoot, "Toolbox");
            Gpu.GpuTuner.PrepareDirectory(folder);
        }
        var tmp = Path.Combine(folder, $"kos-delay-{Guid.NewGuid():N}.xml");
        try
        {
            await File.WriteAllTextAsync(tmp, xml, Encoding.Unicode, ct).ConfigureAwait(false);
            var r = await ProcessRunner.RunAsync(Schtasks, $"/Create /TN {ProcessRunner.Quote(TaskName(e))} /XML {ProcessRunner.Quote(tmp)} /F").ConfigureAwait(false);
            if (r.ExitCode != 0) throw new InvalidOperationException($"The task wasn't created: {r.Output.Trim()}");
        }
        finally { File.Delete(tmp); }
        try
        {
            if (Query(e) is not { } created || !Matches(created, user, file, args, seconds))
                throw new InvalidOperationException("The task was created but doesn't read back as written; it was deleted.");
            if (e.Enabled == true) await e.Provider.SetEnabledAsync(e, false, ct).ConfigureAwait(false);
        }
        catch
        {
            // Never leave the task and the Run entry both on: the program would start twice.
            await ProcessRunner.RunAsync(Schtasks, $"/Delete /TN {ProcessRunner.Quote(TaskName(e))} /F").ConfigureAwait(false);
            throw;
        }
        Log.Info($"Startup: \"{e.Name}\" starts {seconds} s after sign-in (task {TaskName(e)}; Run entry turned off).");
    }

    /// <summary>Deletes the task (if any) and, when <paramref name="enable"/>, turns the Run entry back on.</summary>
    public static async Task RemoveAsync(StartupEntry e, bool enable, CancellationToken ct)
    {
        if (Query(e) is not null)
        {
            var r = await ProcessRunner.RunAsync(Schtasks, $"/Delete /TN {ProcessRunner.Quote(TaskName(e))} /F").ConfigureAwait(false);
            if (r.ExitCode != 0) throw new InvalidOperationException($"The task wasn't deleted: {r.Output.Trim()}");
            Log.Info($"Startup: delay task of \"{e.Name}\" deleted.");
        }
        if (enable && e.Enabled == false) await e.Provider.SetEnabledAsync(e, true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Task definition: at sign-in (of this account, or of any member of Users when <paramref name="userSid"/> is null), after the delay,
    /// the command with the user's own limited token, never stopped for running long.
    /// </summary>
    internal static string Xml(string? userSid, string file, string args, int seconds, string name)
    {
        string X(string s) => SecurityElement.Escape(s);
        var trigger = userSid is null ? "" : $"<UserId>{X(userSid)}</UserId>";
        var principal = userSid is null ? $"<GroupId>{UsersSid}</GroupId>" : $"<UserId>{X(userSid)}</UserId><LogonType>InteractiveToken</LogonType>";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Author>KOS Toolbox</Author><Description>{X($"Starts \"{name}\" {seconds} s after sign-in, in place of its Run entry (Startup page).")}</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled>{trigger}<Delay>PT{seconds}S</Delay></LogonTrigger></Triggers>
              <Principals><Principal id="Author">{principal}<RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority></Settings>
              <Actions Context="Author"><Exec><Command>{X(file)}</Command>{(args.Length > 0 ? $"<Arguments>{X(args)}</Arguments>" : "")}</Exec></Actions>
            </Task>
            """;
    }

    /// <summary>The registered task is exactly what was written: the command, its delay, limited rights and the intended account or group.</summary>
    internal static bool Matches(string xml, string? userSid, string file, string args, int seconds)
    {
        try
        {
            var start = xml.IndexOf("<Task", StringComparison.Ordinal);
            if (start < 0) return false;
            var doc = XDocument.Parse(xml[start..]);
            string? One(string name) => doc.Descendants(Ns + name).Select(x => x.Value.Trim()).FirstOrDefault();
            var principal = doc.Descendants(Ns + "Principal").Single();
            var who = userSid is null ? principal.Element(Ns + "GroupId")?.Value : principal.Element(Ns + "UserId")?.Value;
            return ParseDelay(xml) == seconds && doc.Descendants(Ns + "Exec").Count() == 1
                && string.Equals(One("Command"), file, StringComparison.OrdinalIgnoreCase) && (One("Arguments") ?? "") == args
                && (One("RunLevel") ?? "LeastPrivilege") == "LeastPrivilege" // an export leaves out the default level
                && who is not null && Sid(who) == (userSid ?? UsersSid)
                && (userSid is null ? principal.Element(Ns + "UserId") is null : principal.Element(Ns + "GroupId") is null);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException) { return false; }
    }

    static string? Sid(string account)
    {
        if (account.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)) return account.ToUpperInvariant();
        try { return new NTAccount(account).Translate(typeof(SecurityIdentifier)).Value; }
        catch (IdentityNotMappedException) { return null; }
        catch (SystemException) { return null; }
    }

    /// <summary>The logon trigger's delay in seconds (PT60S, PT2M, PT1M30S).</summary>
    internal static int? ParseDelay(string xml)
    {
        var m = Regex.Match(xml, @"<Delay>PT(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?</Delay>");
        if (!m.Success) return null;
        int G(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0;
        return G(1) * 3600 + G(2) * 60 + G(3);
    }
}
