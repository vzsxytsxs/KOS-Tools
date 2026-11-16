using KosToolbox.Core.Persistence;
using Microsoft.Win32;

namespace KosToolbox.Core.Gpu;

/// <summary>Elevated request: load a VBIOS file into the NVIDIA driver's registry key (<see cref="Path"/>) or remove the loaded ones (null).</summary>
public sealed class VbiosJob
{
    public string? Path { get; set; }
}

/// <summary>
/// Loads a VBIOS without flashing it: the image goes to the card's display class key ("vbios", REG_BINARY) with EnableGpuFirmware = 1,
/// and the driver (616.56 or later) uses it from the next start. The card's own flash isn't touched: removing the value brings the flashed one back.
/// </summary>
public static class VbiosLoader
{
    public const int MaxSize = 512 * 1024;
    const string Image = "vbios", Enable = "EnableGpuFirmware", Backup = "KosPreviousEnableGpuFirmware";
    const int NoValue = -1; // the backup when EnableGpuFirmware didn't exist

    /// <summary>
    /// The PCI device id of the NVIDIA image in the file: the first PCI expansion ROM image (55 AA at a 512-byte boundary, with a PCIR header;
    /// some dumps have a header before it), whose 8-bit sum must be 0. Or why the file isn't one.
    /// </summary>
    public static (ushort DeviceId, string? Problem) Inspect(byte[] rom)
    {
        if (rom.Length == 0 || rom.Length > MaxSize) return (0, "The file must be a VBIOS of at most 512 KB.");
        for (var at = 0; at + 0x1A <= rom.Length; at += 512)
        {
            if (rom[at] != 0x55 || rom[at + 1] != 0xAA) continue;
            var pcir = at + BitConverter.ToUInt16(rom, at + 0x18);
            if (pcir + 8 > rom.Length || rom[pcir] != 'P' || rom[pcir + 1] != 'C' || rom[pcir + 2] != 'I' || rom[pcir + 3] != 'R') continue;
            if (BitConverter.ToUInt16(rom, pcir + 4) != 0x10DE) return (0, "It isn't an NVIDIA VBIOS.");
            var length = rom[at + 2] * 512;
            if (length == 0 || at + length > rom.Length) return (0, "The VBIOS image is cut short.");
            byte sum = 0;
            for (var i = at; i < at + length; i++) sum += rom[i];
            if (sum != 0) return (0, "The VBIOS checksum is wrong: the file is damaged or was edited without fixing it.");
            return (BitConverter.ToUInt16(rom, pcir + 6), null);
        }
        return (0, "There's no VBIOS image in the file (55 AA with a PCIR header).");
    }

    /// <summary>"PCI\VEN_10DE&amp;DEV_2187&amp;..." → 0x2187.</summary>
    public static ushort? DeviceId(string instanceId)
    {
        var i = instanceId.IndexOf("DEV_", StringComparison.OrdinalIgnoreCase);
        return i >= 0 && i + 8 <= instanceId.Length && ushort.TryParse(instanceId.AsSpan(i + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out var id) ? id : null;
    }

    static IEnumerable<(ushort? Device, string Key)> Cards() => GpuTuner.NvidiaAdapters().Where(g => g.DriverKey is not null)
        .Select(g => (DeviceId(g.InstanceId), $@"SYSTEM\CurrentControlSet\Control\Class\{g.DriverKey}"));

    /// <summary>The file, read and checked, and the class key of the installed card it is for.</summary>
    static (byte[] Rom, string Key) Prepare(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("The VBIOS file doesn't exist.", path);
        if (info.Length is 0 or > MaxSize) throw new InvalidOperationException("The file must be a VBIOS of at most 512 KB.");
        var rom = File.ReadAllBytes(path);
        var (device, problem) = Inspect(rom);
        if (problem is not null) throw new InvalidOperationException(problem);
        var key = Cards().FirstOrDefault(c => c.Device == device).Key
            ?? throw new InvalidOperationException($"The VBIOS is for device {device:X4}, and no installed NVIDIA card with a driver has that id.");
        return (rom, key);
    }

    /// <summary>Elevated side, before running: the request is refused unless the file is a valid image for an installed card.</summary>
    public static void Validate(VbiosJob job)
    {
        if (job.Path is not null) Prepare(job.Path);
    }

    /// <summary>Unelevated side, before asking for administrator: the same checks, so a wrong file doesn't open UAC.</summary>
    public static void Check(string path) => Prepare(path);

    public static void Execute(VbiosJob job)
    {
        if (job.Path is null)
        {
            foreach (var (_, key) in Cards()) Remove(key);
            return;
        }
        var (rom, cardKey) = Prepare(job.Path); // read again here: the file may have changed since the check
        using var k = Registry.LocalMachine.OpenSubKey(cardKey, writable: true) ?? throw new InvalidOperationException("The card's driver key can't be opened.");
        if (k.GetValue(Backup) is null) k.SetValue(Backup, k.GetValue(Enable) is int before ? before : NoValue, RegistryValueKind.DWord);
        foreach (var name in k.GetValueNames().Where(n => n.StartsWith("romOverride", StringComparison.OrdinalIgnoreCase))) k.DeleteValue(name, false);
        k.SetValue(Image, rom, RegistryValueKind.Binary);
        k.SetValue(Enable, 1, RegistryValueKind.DWord);
        Log.Info($"VBIOS loaded into {cardKey} ({rom.Length} bytes).");
    }

    static void Remove(string key)
    {
        using var k = Registry.LocalMachine.OpenSubKey(key, writable: true);
        if (k is null || k.GetValue(Image) is null) return;
        k.DeleteValue(Image, false);
        if (k.GetValue(Backup) is int before)
        {
            if (before == NoValue) k.DeleteValue(Enable, false);
            else k.SetValue(Enable, before, RegistryValueKind.DWord);
            k.DeleteValue(Backup, false);
        }
        Log.Info($"Loaded VBIOS removed from {key}.");
    }

    /// <summary>Read back: the image in the key of the card it's for equals the file (null = no card has one).</summary>
    public static bool? Loaded(string? path = null)
    {
        var loaded = Cards().Select(c => Registry.LocalMachine.OpenSubKey(c.Key)?.GetValue(Image) as byte[]).Where(b => b is not null).ToList();
        if (path is null) return loaded.Count > 0;
        var rom = File.ReadAllBytes(path);
        return loaded.Any(b => b!.AsSpan().SequenceEqual(rom));
    }
}
