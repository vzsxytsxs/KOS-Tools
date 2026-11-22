using KosToolbox.Core.Persistence;

namespace KosToolbox.Core.Devices;

/// <summary>
/// Restart of a device so it picks up new interrupt settings (SetupDi property change, like "devcon restart"). Only PCI USB
/// host controllers and network adapters: display, audio and storage controllers keep running until Windows restarts.
/// </summary>
public static class DeviceRestart
{
    public static bool Allowed(string deviceClass) => deviceClass.Equals("USB", StringComparison.OrdinalIgnoreCase) || deviceClass.Equals("Net", StringComparison.OrdinalIgnoreCase);

    /// <summary>A connected PCI device of an allowed class (checked again in the elevated process).</summary>
    internal static void Validate(string id, IReadOnlyList<PnpDevice> pci)
    {
        if (!id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) || id.Contains("..") || id.Length > 200)
            throw new InvalidOperationException($"Invalid device: {id}");
        var d = pci.FirstOrDefault(d => d.InstanceId.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No connected PCI device has the id {id}.");
        if (!Allowed(d.Class)) throw new InvalidOperationException($"{d.Name} ({d.Class}) isn't restarted here: it needs a Windows restart.");
    }

    /// <summary>Restarts each device; a failure doesn't stop the others. Returns how many failed. Requires administrator.</summary>
    internal static int RestartAll(IReadOnlyList<string> ids)
    {
        var pci = Interrupts.PciDevices().ToList();
        var failed = 0;
        foreach (var id in ids)
        {
            try
            {
                Validate(id, pci);
                if (PnpDevices.Restart(id))
                {
                    failed++;
                    Log.Warn($"Device restart {id}: Windows wants a restart to finish it.");
                }
            }
            catch (Exception ex)
            {
                failed++;
                Log.Warn($"Device restart {id} failed: {ex.Message}");
            }
        }
        return failed;
    }
}
