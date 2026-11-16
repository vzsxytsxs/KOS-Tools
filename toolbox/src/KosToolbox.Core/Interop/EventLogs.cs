using System.ComponentModel;
using System.Runtime.InteropServices;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;
using Microsoft.Win32;

namespace KosToolbox.Core.Interop;

/// <summary>
/// Port of "Clear EventLogs.ps1" (Get-EventLog -LogName * | Clear-EventLog): clears the classic logs
/// registered under Services\EventLog through the Windows API (ClearEventLog), without PowerShell.
/// </summary>
public static class EventLogs
{
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenEventLogW")]
    static extern IntPtr OpenEventLog(string? server, string source);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ClearEventLogW")]
    static extern bool ClearEventLog(IntPtr log, string? backupFile);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool CloseEventLog(IntPtr log);

    public static IReadOnlyList<string> ClassicLogs()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\EventLog");
        return key?.GetSubKeyNames() ?? Array.Empty<string>();
    }

    public static Task<string> ClearAllAsync(CancellationToken ct) => Task.Run(() =>
    {
        var logs = ClassicLogs();
        var failed = new List<string>();
        foreach (var name in logs)
        {
            ct.ThrowIfCancellationRequested();
            var handle = OpenEventLog(null, name);
            if (handle == IntPtr.Zero) { failed.Add($"{name} ({new Win32Exception().Message})"); continue; }
            try
            {
                if (ClearEventLog(handle, null)) Log.Info($"Event log cleared: {name}");
                else failed.Add($"{name} ({new Win32Exception().Message})");
            }
            finally { CloseEventLog(handle); }
        }
        if (failed.Count > 0) Log.Warn("Event logs not cleared: " + string.Join("; ", failed));
        return failed.Count == 0 ? Txt.EventLogsCleared(logs.Count) : throw new InvalidOperationException(Txt.EventLogsPartial(logs.Count - failed.Count, logs.Count, failed));
    }, ct);
}
