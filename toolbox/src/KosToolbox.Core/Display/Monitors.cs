using KosToolbox.Core.Devices;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using Microsoft.Win32;

namespace KosToolbox.Core.Display;

/// <summary>Monitor known to Windows (Enum\DISPLAY), with its EDID and the saved override, if any.</summary>
public sealed record MonitorInfo(string InstanceId, bool Active, byte[] Edid, byte[]? Override, string? Description = null)
{
    public byte[] Effective => Override ?? Edid;
}

public sealed class DisplayJob
{
    public string? InstanceId { get; set; }
    /// <summary>Full EDID in hexadecimal (128-byte blocks); null with <see cref="InstanceId"/> = delete the override.</summary>
    public string? Override { get; set; }
    /// <summary>Restart the video adapters so the overrides take effect now.</summary>
    public bool RestartAdapters { get; set; }
    /// <summary>NVIDIA's "Override the scaling mode set by games and programs" for this monitor model (EDID product id).</summary>
    public string? ScalingOverrideProduct { get; set; }
    public bool ScalingOverride { get; set; }
}

/// <summary>
/// EDID of the monitors and the override in the format Windows and CRU use: Device Parameters\EDID_OVERRIDE, one binary
/// value per block ("0", "1", …). The video driver only reads the override when it restarts.
/// </summary>
public static class Monitors
{
    const string Root = @"SYSTEM\CurrentControlSet\Enum\DISPLAY";

    static RegistryKey Hklm => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    public static List<MonitorInfo> List()
    {
        var active = PnpDevices.Enumerate("DISPLAY", presentOnly: true).Select(d => d.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = new List<MonitorInfo>();
        using var hklm = Hklm;
        using var root = hklm.OpenSubKey(Root);
        if (root is null) return list;
        foreach (var model in root.GetSubKeyNames())
        {
            using var m = root.OpenSubKey(model);
            if (m is null) continue;
            foreach (var instance in m.GetSubKeyNames())
            {
                var id = $@"DISPLAY\{model}\{instance}";
                using var p = hklm.OpenSubKey($@"{Root}\{model}\{instance}\Device Parameters");
                if (p?.GetValue("EDID") is not byte[] { Length: >= 128 } edid) continue;
                // DeviceDesc comes as "@monitor.inf,%pnpmonitor.devicedesc%;Generic PnP Monitor": the text is after the ';'.
                using var device = hklm.OpenSubKey($@"{Root}\{model}\{instance}");
                var description = device?.GetValue("DeviceDesc") is string desc ? desc[(desc.LastIndexOf(';') + 1)..] : null;
                list.Add(new MonitorInfo(id, active.Contains(id), edid, ReadOverride(p), description));
            }
        }
        return list.OrderByDescending(x => x.Active).ThenBy(x => x.InstanceId, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static byte[]? ReadOverride(RegistryKey parameters)
    {
        using var o = parameters.OpenSubKey("EDID_OVERRIDE");
        if (o is null) return null;
        var blocks = new List<byte>();
        for (var i = 0; o.GetValue(i.ToString()) is byte[] { Length: 128 } block; i++) blocks.AddRange(block);
        return blocks.Count > 0 ? blocks.ToArray() : null;
    }

    public static byte[]? Override(string instanceId)
    {
        using var hklm = Hklm;
        using var p = hklm.OpenSubKey($@"{Root.Replace(@"\DISPLAY", "")}\{instanceId}\Device Parameters");
        return p is null ? null : ReadOverride(p);
    }

    /// <summary>Writes (or deletes, with null) the override and confirms by re-reading.</summary>
    public static async Task SaveAsync(string instanceId, byte[]? edid, CancellationToken ct = default)
    {
        var job = new DisplayJob { InstanceId = instanceId, Override = edid is null ? null : Convert.ToHexString(edid) };
        Validate(job);
        if (Elevation.IsAdmin) await Task.Run(() => Execute(job), ct).ConfigureAwait(false);
        else await ElevatedRunner.RunAsync(new ElevatedJob { Display = job }, ct).ConfigureAwait(false);
        var now = Override(instanceId);
        if (edid is null ? now is not null : now is null || !now.AsSpan().SequenceEqual(edid)) throw new InvalidOperationException(Text.Txt.ChangeNotConfirmed);
    }

    /// <summary>Restarts all connected video adapters. Returns true if Windows asks for a restart.</summary>
    public static async Task<bool> RestartAdaptersAsync(CancellationToken ct = default)
    {
        var job = new DisplayJob { RestartAdapters = true };
        if (Elevation.IsAdmin) return await Task.Run(() => Execute(job), ct).ConfigureAwait(false);
        await ElevatedRunner.RunAsync(new ElevatedJob { Display = job }, ct).ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Sets NVIDIA's scaling override box. The driver keeps its database in memory and writes it back when it stops, so the value
    /// is written with the NVIDIA adapters disabled and they are enabled again (the screen goes black for a few seconds).
    /// </summary>
    public static async Task SetScalingOverrideAsync(string product, bool on, CancellationToken ct = default)
    {
        var job = new DisplayJob { ScalingOverrideProduct = product, ScalingOverride = on };
        Validate(job);
        if (Elevation.IsAdmin) await Task.Run(() => Execute(job), ct).ConfigureAwait(false);
        else await ElevatedRunner.RunAsync(new ElevatedJob { Display = job }, ct).ConfigureAwait(false);
        Gpu.Nvapi.Reset(); // the adapters restarted: this process' NVAPI session is gone
        // The driver takes a few seconds to come back; reading the monitor through NVAPI before that fails.
        for (var i = 0; i < 20; i++)
        {
            try { if (Gpu.DisplaySettings.Targets().Any(t => Gpu.DisplaySettings.ReadNvScaling(t.GdiName) is not null)) break; }
            catch (InvalidOperationException) { /* displays still changing */ }
            Gpu.Nvapi.Reset();
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        if (Gpu.DisplaySettings.ReadScalingOverride(product) != on) throw new InvalidOperationException(Text.Txt.ChangeNotConfirmed);
    }

    /// <summary>Elevated side: existing monitor in Enum\DISPLAY and an EDID with a valid header, size and checksums.</summary>
    internal static void Validate(DisplayJob job)
    {
        if (job.RestartAdapters || job.ScalingOverrideProduct is not null)
        {
            if (job.InstanceId is not null || job.Override is not null || job.RestartAdapters && job.ScalingOverrideProduct is not null)
                throw new InvalidOperationException("Invalid display request.");
            if (job.ScalingOverrideProduct is { } p && !System.Text.RegularExpressions.Regex.IsMatch(p, "^[A-Z]{3}[0-9A-F]{4}$"))
                throw new InvalidOperationException($"Invalid monitor product id: {p}");
            return;
        }
        var id = job.InstanceId ?? "";
        var parts = id.Split('\\');
        if (parts.Length != 3 || !parts[0].Equals("DISPLAY", StringComparison.OrdinalIgnoreCase) || parts.Any(p => p.Length == 0 || p == ".."))
            throw new InvalidOperationException($"Invalid monitor: {id}");
        using (var hklm = Hklm)
        using (var p = hklm.OpenSubKey($@"{Root}\{parts[1]}\{parts[2]}\Device Parameters"))
            if (p?.GetValue("EDID") is not byte[]) throw new InvalidOperationException($"Monitor not found: {id}");
        if (job.Override is null) return;
        byte[] data;
        try { data = Convert.FromHexString(job.Override); } catch (FormatException) { throw new InvalidOperationException(Text.Txt.EdidInvalid); }
        if (data.Length is < 128 or > 128 * 4 || !Edid.ChecksumsValid(data) || Edid.Parse(data).Extensions != data.Length / 128 - 1)
            throw new InvalidOperationException(Text.Txt.EdidInvalid);
    }

    internal static bool Execute(DisplayJob job)
    {
        Validate(job);
        if (job.RestartAdapters)
        {
            var reboot = false;
            foreach (var adapter in PnpDevices.Enumerate(presentOnly: true).Where(d => d.Class.Equals("Display", StringComparison.OrdinalIgnoreCase)))
                reboot |= PnpDevices.Restart(adapter.InstanceId);
            Log.Info($"Display adapters restarted (reboot needed: {reboot}).");
            return reboot;
        }
        if (job.ScalingOverrideProduct is { } product)
        {
            SetScalingOverride(product, job.ScalingOverride);
            return false;
        }
        var parts = job.InstanceId!.Split('\\');
        using var hklm = Hklm;
        using var parameters = hklm.OpenSubKey($@"{Root}\{parts[1]}\{parts[2]}\Device Parameters", writable: true)
            ?? throw new InvalidOperationException($"Monitor not found: {job.InstanceId}");
        parameters.DeleteSubKeyTree("EDID_OVERRIDE", throwOnMissingSubKey: false);
        if (job.Override is { } hex)
        {
            using var o = parameters.CreateSubKey("EDID_OVERRIDE", writable: true);
            var data = Convert.FromHexString(hex);
            for (var i = 0; i < data.Length / 128; i++) o.SetValue(i.ToString(), data.AsSpan(i * 128, 128).ToArray(), RegistryValueKind.Binary);
        }
        Log.Info($"EDID override for {job.InstanceId}: {(job.Override is null ? "removed" : $"{job.Override.Length / 256} blocks saved")}");
        return false;
    }

    static void SetScalingOverride(string product, bool on)
    {
        using var hklm = Hklm;
        using var db = hklm.OpenSubKey(Gpu.DisplaySettings.DisplayDatabase) ?? throw new InvalidOperationException("The NVIDIA driver has no display database.");
        // The names come from the driver; they go on reg.exe's command line, so only plain ones.
        var keys = Gpu.DisplaySettings.ScalingKeys(db, product).Where(k => k.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')).ToList();
        // Only SYSTEM may write the database (administrators read it): PowerRun, as the KOS does for protected keys.
        if (!Settings.TrustedInstaller.Available) throw new InvalidOperationException("Writing the NVIDIA display database needs PowerRun (in the Windows folder on KOS).");
        // Checked before touching the adapters: an unknown layout writes nothing.
        if (!keys.Any(k => { using var key = db.OpenSubKey(k); return Gpu.DisplaySettings.WithScalingOverride(key?.GetValue("ScalingConfig") as byte[], on) is not null; }))
            throw new InvalidOperationException("The NVIDIA driver has no scaling settings saved for this monitor yet (or an unknown format).");
        var adapters = PnpDevices.Enumerate(presentOnly: true)
            .Where(d => d.Class.Equals("Display", StringComparison.OrdinalIgnoreCase) && d.InstanceId.StartsWith(@"PCI\VEN_10DE", StringComparison.OrdinalIgnoreCase)).ToList();
        if (adapters.Count == 0) throw new InvalidOperationException("No NVIDIA display adapter.");
        foreach (var a in adapters) PnpDevices.SetEnabled(a.InstanceId, false);
        try
        {
            foreach (var k in keys)
            {
                using var key = db.OpenSubKey(k);
                if (Gpu.DisplaySettings.WithScalingOverride(key?.GetValue("ScalingConfig") as byte[], on) is { } value)
                    Settings.TrustedInstaller.RegWriteBinary($@"{Gpu.DisplaySettings.DisplayDatabase}\{k}", "ScalingConfig", value);
            }
            Log.Info($"NVIDIA scaling override for {product}: {(on ? "on" : "off")} ({keys.Count} entries)");
        }
        finally
        {
            foreach (var a in adapters) PnpDevices.SetEnabled(a.InstanceId, true);
        }
    }
}
