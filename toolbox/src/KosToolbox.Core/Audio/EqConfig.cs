using System.Text.Json;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Tools;

namespace KosToolbox.Core.Audio;

/// <summary>One device's profiles in KOS Equalizer.</summary>
public sealed class EqDevice
{
    public string EndpointId { get; set; } = "";
    public string Name { get; set; } = "";
    public List<EqProfile> Profiles { get; set; } = new();
    /// <summary>The profile it plays (by name); null = flat.</summary>
    public string? Active { get; set; }
    /// <summary>The profile it plays while gamemode is on; null = the active one stays.</summary>
    public string? Gamemode { get; set; }

    public EqProfile? Find(string? name) => name is null ? null : Profiles.FirstOrDefault(p => p.Name == name);

    /// <summary>What it plays now: the gamemode profile during gamemode (when it has one), else the active one; null = flat.</summary>
    public EqProfile? Playing(bool gamemode) => (gamemode ? Find(Gamemode) : null) ?? Find(Active);
}

/// <summary>
/// KOS Equalizer's profiles per device: %ProgramData%\TheKOS\Audio\equalizer.json (admin-only; written by KOS Equalizer, read by
/// KOS Engine, which switches each device to its gamemode profile and back).
/// </summary>
public sealed class EqConfig
{
    public List<EqDevice> Devices { get; set; } = new();

    public static string FilePath => Path.Combine(EqSettingsFile.AudioDirectory, "equalizer.json");
    /// <summary>Exists while KOS Engine has the gamemode profiles playing.</summary>
    static string GamemodeMarker => Path.Combine(EqSettingsFile.AudioDirectory, "gamemode.on");

    /// <summary>Whether the gamemode profiles are playing now (KOS Engine applied them and hasn't switched back).</summary>
    public static bool GamemodeOn => File.Exists(GamemodeMarker);

    /// <summary>Exists while KOS Equalizer's master switch is off: every device it's on for plays flat; the profiles stay.</summary>
    static string BypassMarker => Path.Combine(EqSettingsFile.AudioDirectory, "bypass.on");
    public static bool Bypassed => File.Exists(BypassMarker);

    /// <summary>
    /// The master switch. Off (<paramref name="bypass"/>): every device KOS Equalizer is on for is written flat at once (the effect
    /// reloads its file: no audio restart, the registration stays); on: each plays its profile again. The marker goes first, so a
    /// failure halfway leaves the next write (Play, Apply) doing the same. Returns the devices written.
    /// </summary>
    public static int SetBypass(bool bypass)
    {
        GpuTuner.PrepareDirectory(EqSettingsFile.AudioDirectory); // refuses a link or someone else's folder
        if (bypass) File.WriteAllText(BypassMarker, "");
        else File.Delete(BypassMarker);
        return Apply(GamemodeOn);
    }

    /// <summary>The config (empty when there's none); throws when it can't be trusted or read, so a save never replaces it with an empty one.</summary>
    public static EqConfig Load()
    {
        if (!File.Exists(FilePath)) return new EqConfig();
        if ((GpuTuner.FolderProblem(AppPaths.TheKosRoot, EqSettingsFile.AudioDirectory) ?? ToolDiscovery.OwnerProblem(FilePath)) is { } problem)
            throw new InvalidOperationException(problem);
        return JsonSerializer.Deserialize(File.ReadAllText(FilePath), KosJson.Default.EqConfig) ?? new EqConfig();
    }

    /// <summary>Saves it (administrator: the folder is admin-only).</summary>
    public void Save()
    {
        GpuTuner.PrepareDirectory(EqSettingsFile.AudioDirectory);
        File.Delete(FilePath + ".tmp"); // an old .tmp could be a hard link to another file
        JsonFile.Save(FilePath, this, KosJson.Default.EqConfig);
    }

    /// <summary>The device's entry, added when it has none; its name is kept current.</summary>
    public EqDevice Device(string endpointId, string name)
    {
        var d = Devices.FirstOrDefault(d => string.Equals(d.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase));
        if (d is null) Devices.Add(d = new EqDevice { EndpointId = endpointId });
        d.Name = name;
        return d;
    }

    /// <summary>
    /// KOS Equalizer after a change to a device's profiles (save the config first): what the device should play now goes to its
    /// settings file, the gamemode profile while KOS Engine has gamemode on, flat while the master switch is off. Nothing when
    /// KOS Equalizer isn't on for the device. Throws when the profile can't be played (<see cref="EqSettingsFile.Problem"/>) or the
    /// file can't be written.
    /// </summary>
    public static void Play(EqDevice device)
    {
        if (EqSettingsFile.Read(device.EndpointId) is null) return;
        EqSettingsFile.Write(device.EndpointId, Bypassed ? null : device.Playing(GamemodeOn));
    }

    /// <summary>
    /// KOS Engine at a gamemode switch (and at its start, with off: a gamemode it didn't end): every device KOS Equalizer is on for
    /// (it has a settings file) gets what it should play now (flat while the master switch is off); only a file whose text differs
    /// is written. Returns the devices written; one that fails is logged and skipped.
    /// </summary>
    public static int Apply(bool gamemode)
    {
        var written = 0;
        var bypassed = Bypassed;
        foreach (var d in Load().Devices)
        {
            if (EqSettingsFile.Read(d.EndpointId) is not { } now) continue; // not on for this device
            var profile = bypassed ? null : d.Playing(gamemode);
            if (now == (profile is null ? EqSettingsFile.FlatText : EqSettingsFile.Format(profile))) continue;
            try
            {
                EqSettingsFile.Write(d.EndpointId, profile);
                written++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Equalizer: {d.Name} couldn't switch to {profile?.Name ?? "flat"}: {ex.Message}");
                // A profile that can't be played (saved past +24 dB, say): flat rather than the other profile staying on.
                if (profile is not null)
                    try { EqSettingsFile.Write(d.EndpointId, null); written++; Log.Warn($"Equalizer: {d.Name} plays flat instead."); }
                    catch (Exception flat) when (flat is IOException or UnauthorizedAccessException or InvalidOperationException) { Log.Warn($"Equalizer: {d.Name} couldn't go flat either: {flat.Message}"); }
            }
        }
        if (!Directory.Exists(EqSettingsFile.AudioDirectory)) return written; // KOS Equalizer never ran: nothing to mark
        GpuTuner.PrepareDirectory(EqSettingsFile.AudioDirectory); // refuses a link or someone else's folder
        if (gamemode) File.WriteAllText(GamemodeMarker, "");
        else File.Delete(GamemodeMarker);
        return written;
    }
}
