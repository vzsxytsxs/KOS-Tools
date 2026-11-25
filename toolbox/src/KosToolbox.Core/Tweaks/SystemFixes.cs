using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Settings;
using KosToolbox.Core.Text;
using Microsoft.Win32;

namespace KosToolbox.Core.Tweaks;

/// <summary>One-off repairs behind Tools actions. Each runs as administrator (the elevated process when needed).</summary>
public static class SystemFixes
{
    const string AdapterClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
    const string VbsEntry = "{0cb3b571-2f2e-4343-a879-d86a476d7215}";

    /// <summary>Adapter power-saving and wake overrides the reset removes, so each driver goes back to its own default.</summary>
    internal static readonly string[] AdapterValues =
    {
        "EnablePME", "*DeviceSleepOnDisconnect", "*EEE", "AdvancedEEE", "*SipsEnabled", "EnableAspm", "ASPM", "*ModernStandbyWoLMagicPacket",
        "*SelectiveSuspend", "EnableGigaLite", "GigaLite", "*WakeOnMagicPacket", "*WakeOnPattern", "AutoPowerSaveModeEnabled", "EEELinkAdvertisement",
        "EeePhyEnable", "EnableGreenEthernet", "EnableModernStandby", "PowerDownPll", "PowerSavingMode", "ReduceSpeedOnPowerDown", "S5WakeOnLan",
        "SavePowerNowEnabled", "ULPMode", "WakeOnLink", "WakeOnSlot", "WakeOnLinkChg", "WakeOnLinkUp", "WakeUpModeCap", "*NicAutoPowerSaver",
        "PowerSaveEnable", "EnablePowerManagement", "ForceWakeFromMagicPacketOnModernStandby", "WakeFromS5", "WakeOn", "EnableSavePowerNow",
        "*EnableDynamicPowerGating", "DynamicPowerGating", "EnableD3ColdInS0", "WakeFromPowerOff", "LogLinkStateEvent",
        "PnPCapabilities", "WakeOnMagicPacketFromS5", "WolShutdownLinkSpeed",
    };

    static string Sys(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    static ProcessResult Run(string exe, string args, bool mustSucceed = true, TimeSpan? timeout = null)
    {
        var r = ProcessRunner.Run(Sys(exe), args, timeout ?? TimeSpan.FromMinutes(2));
        Log.Info($"{exe} {args}: exit code {r.ExitCode}");
        if (mustSucceed && r.ExitCode != 0) throw new InvalidOperationException(Txt.CommandFailed($"{exe} {args}", r.ExitCode, r.Output.Trim()));
        return r;
    }

    /// <summary>Restarts now into the UEFI firmware settings (shutdown /fw needs administrator and a UEFI system).</summary>
    public static Task<string> RestartToFirmwareAsync(CancellationToken ct) => Task.Run(() =>
    {
        Run("shutdown.exe", "/r /fw /t 0");
        return Txt.RestartingToFirmware;
    }, ct);

    /// <summary>
    /// Removes the power-saving and wake overrides from every network adapter and the network power policies, then resets
    /// Winsock and TCP/IP, renews the address and flushes the DNS and ARP caches. The firewall isn't reset (that would also
    /// delete custom rules), and neither is Nagle's setting.
    /// </summary>
    public static Task<string> ResetNetworkAsync(CancellationToken ct) => Task.Run(() =>
    {
        var removed = 0;
        using (var adapters = Registry.LocalMachine.OpenSubKey(AdapterClass))
            foreach (var sub in adapters?.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsAsciiDigit)) ?? Enumerable.Empty<string>())
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"{AdapterClass}\{sub}", writable: true);
                if (key is null) continue;
                foreach (var name in AdapterValues.Where(n => key.GetValue(n) is not null)) { key.DeleteValue(name); removed++; }
            }
        foreach (var path in new[] { @"SOFTWARE\Policies\Microsoft\Windows\WcmSvc\Local", @"SOFTWARE\Policies\Microsoft\Windows\WcmSvc\GroupPolicy" })
            new RegistrySetting(Hive.LocalMachine, path, "fDisablePowerManagement", RegistryValueKind.DWord).Write(null);
        new RegistrySetting(Hive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters", "DisableCoalescing", RegistryValueKind.DWord).Write(null);
        Log.Info($"Network reset: {removed} adapter values removed.");

        Run("ipconfig.exe", "/flushdns", mustSucceed: false);
        Run("ipconfig.exe", "/release", mustSucceed: false);
        Run("ipconfig.exe", "/renew", mustSucceed: false);
        Run("netsh.exe", "winsock reset");
        Run("netsh.exe", "int ip reset", mustSucceed: false);   // reports access denied on a few protected keys even when it resets the rest
        Run("netsh.exe", "int ipv6 reset", mustSucceed: false);
        Run("arp.exe", "-d *", mustSucceed: false);
        return Txt.NetworkResetDone(removed);
    }, ct);

    /// <summary>
    /// Registers the Windows Time service again, keeps the server chosen in "Time server (NTP)" (read before, since
    /// unregistering deletes it) and syncs the clock now.
    /// </summary>
    public static Task<string> FixTimeSyncAsync(CancellationToken ct) => Task.Run(() =>
    {
        var servers = new RegistrySetting(Hive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\W32Time\Parameters", "NtpServer", RegistryValueKind.String).Read();
        if (string.IsNullOrWhiteSpace(servers) || servers.Contains('"')) servers = "time.windows.com,0x9";
        Run("sc.exe", "config w32time start= demand", mustSucceed: false);
        Run("net.exe", "stop w32time", mustSucceed: false);
        Run("w32tm.exe", "/unregister", mustSucceed: false);
        Run("w32tm.exe", "/register");
        Run("net.exe", "start w32time");
        Run("w32tm.exe", $"/config /manualpeerlist:\"{servers}\" /syncfromflags:manual /update");
        var sync = Run("w32tm.exe", "/resync /rediscover", mustSucceed: false, timeout: TimeSpan.FromMinutes(1));
        return sync.ExitCode == 0 ? Txt.TimeSynced(servers) : Txt.TimeServiceFixedNoSync(servers);
    }, ct);

    const string SessionManager = @"SYSTEM\CurrentControlSet\Control\Session Manager";

    /// <summary>
    /// Partition tools (MiniTool Partition Wizard) that must work on the system drive before the restart add their job to the
    /// session manager's BootExecute list; with the value deleted they stop with "Failed to modify registry". KOS keeps it empty
    /// (no autochk at boot) so they can, and this puts it back when it was deleted.
    /// </summary>
    public static Task<string> FixPartitionToolsAsync(CancellationToken ct) => Task.Run(() => EnsureBootExecute(Registry.LocalMachine, SessionManager), ct);

    /// <summary>Creates BootExecute as an empty REG_MULTI_SZ when it is missing (or not a multi-string); a value that exists stays as it is. Reads it back.</summary>
    internal static string EnsureBootExecute(RegistryKey root, string path)
    {
        using var key = root.CreateSubKey(path, writable: true);
        if (key.GetValue("BootExecute") is string[]) return Txt.BootExecuteAlreadyThere;
        key.SetValue("BootExecute", Array.Empty<string>(), RegistryValueKind.MultiString);
        if (key.GetValue("BootExecute") is not string[] || key.GetValueKind("BootExecute") != RegistryValueKind.MultiString)
            throw new InvalidOperationException(Txt.BootExecuteNotWritten);
        Log.Info("BootExecute created as an empty multi-string.");
        return Txt.BootExecuteFixed;
    }

    /// <summary>
    /// Schedules Microsoft's SecConfig.efi for the next boot to clear the UEFI lock of VBS and Credential Guard (Microsoft's
    /// documented steps): it asks for confirmation at boot. Only when VBS runs although nothing is configured to start it.
    /// </summary>
    public static Task<string> ScheduleVbsUnlockAsync(CancellationToken ct) => Task.Run(() =>
    {
        var efi = Sys("SecConfig.efi");
        if (!File.Exists(efi)) throw new FileNotFoundException(Txt.SecConfigMissing);
        var ps = Run(@"WindowsPowerShell\v1.0\powershell.exe", "-NoProfile -NonInteractive -Command \"$d = Get-CimInstance -Namespace root\\Microsoft\\Windows\\DeviceGuard -ClassName Win32_DeviceGuard; " +
            "'' + $d.VirtualizationBasedSecurityStatus + ';' + (@($d.SecurityServicesConfigured) -join ',')\"");
        if (!VbsLocked(ps.Output)) throw new InvalidOperationException(Txt.VbsNotLocked);

        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var letter = Enumerable.Range('D', 23).Select(c => (char)c).Reverse().FirstOrDefault(c => !used.Contains(c));
        if (letter == default) throw new InvalidOperationException(Txt.NoFreeDriveLetter);
        Run("mountvol.exe", $"{letter}: /s");
        try
        {
            var boot = $@"{letter}:\EFI\Microsoft\Boot";
            Directory.CreateDirectory(boot);
            File.Copy(efi, Path.Combine(boot, "SecConfig.efi"), overwrite: true);
            Run("bcdedit.exe", $"/delete {VbsEntry}", mustSucceed: false); // left over from an earlier run
            try
            {
                Run("bcdedit.exe", $"/create {VbsEntry} /d \"DebugTool\" /application osloader");
                Run("bcdedit.exe", $"/set {VbsEntry} path \"\\EFI\\Microsoft\\Boot\\SecConfig.efi\"");
                Run("bcdedit.exe", $"/set {VbsEntry} loadoptions DISABLE-LSA-ISO,DISABLE-VBS");
                Run("bcdedit.exe", $"/set {VbsEntry} device partition={letter}:");
                // Last, once the entry is complete: the next boot never goes to a half-written one.
                Run("bcdedit.exe", $"/set {{bootmgr}} bootsequence {VbsEntry}");
            }
            catch
            {
                Run("bcdedit.exe", $"/delete {VbsEntry}", mustSucceed: false); // also takes it out of the boot sequence
                throw;
            }
        }
        finally { Run("mountvol.exe", $"{letter}: /d", mustSucceed: false); }
        return Txt.VbsUnlockScheduled;
    }, ct);

    /// <summary>"2;0,0" (VBS running, nothing configured) is what a UEFI lock looks like after VBS was turned off.</summary>
    internal static bool VbsLocked(string output)
    {
        var line = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Contains(';'));
        var parts = line?.Split(';');
        return parts is { Length: 2 } && parts[0] == "2"
            && parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).All(s => s == "0");
    }
}
