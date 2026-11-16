namespace KosToolbox.Core.Text;

/// <summary>Texts for the NVIDIA driver settings card (Profile Inspector) on the GPU page.</summary>
public static partial class Txt
{
    public const string NvSettingsNote = "Global and per-game driver profiles (frame rate limit, low latency mode, texture filtering, V-Sync, power management and the rest), in NVIDIA Profile Inspector from the KOS tools folder. It asks for administrator rights; changes apply to games started afterwards.";
    public const string OpenHiddenSettings = "Show hidden settings", ImportNip = "Import profile (.nip)…",
        NipFilter = "NVIDIA Profile Inspector profiles (*.nip)|*.nip", NipInvalid = "Choose a .nip file exported by NVIDIA Profile Inspector.",
        ImportNipTitle = "Import NVIDIA profile";
    public static string ProfileInspectorMissing(string path) => $"NVIDIA Profile Inspector wasn't found at {path}.";
}
