using KosToolbox.Core.Execution;

namespace KosToolbox.Core.Devices;

/// <summary>Ghost devices: installed before, but not connected now (Device Manager › Show hidden).</summary>
public static class GhostDevices
{
    public static List<PnpDevice> List() => PnpDevices.Enumerate().Where(d => !d.Present).Select(d => d with { HasAffinity = HasAffinity(d.InstanceId) }).ToList();

    /// <summary>An interrupt affinity was set for the device (Affinity Policy: DevicePolicy 3 or more, or a processor mask override).</summary>
    static bool HasAffinity(string instanceId)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}\Device Parameters\Interrupt Management\Affinity Policy");
            return AffinitySet(key?.GetValue("DevicePolicy"), key?.GetValue("AssignmentSetOverride"));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    internal static bool AffinitySet(object? policy, object? mask) =>
        policy is int p && p >= 3 || mask switch { byte[] b => b.Any(x => x != 0), long l => l != 0, int i => i != 0, _ => false };

    /// <summary>Removes (elevated if needed) and confirms by re-reading the list. Returns how many disappeared.</summary>
    public static async Task<int> RemoveAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        Validate(ids);
        if (Elevation.IsAdmin) await Task.Run(() => PnpDevices.RemoveNotPresent(ids, out _), ct).ConfigureAwait(false);
        else await ElevatedRunner.RunAsync(new ElevatedJob { RemoveDevices = ids.ToList() }, ct).ConfigureAwait(false);
        var left = PnpDevices.Enumerate().Select(d => d.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ids.Count(id => !left.Contains(id));
    }

    internal static void Validate(IReadOnlyCollection<string> ids)
    {
        if (ids.Count is 0 or > 5000) throw new InvalidOperationException("Invalid device list.");
        foreach (var id in ids)
            if (id.Length is 0 or > 400 || id.Contains("..") || id.IndexOfAny(new[] { '\0', '/' }) >= 0)
                throw new InvalidOperationException($"Invalid device id: {id}");
    }
}
