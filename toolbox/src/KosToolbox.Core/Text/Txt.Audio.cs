using KosToolbox.Core.Audio;

namespace KosToolbox.Core.Text;

/// <summary>Texts for the Audio page and the per-device audio settings (KOS Equalizer shows the same panel).</summary>
public static partial class Txt
{
    public const string PageAudio = "Audio",
        PageAudioSubtitle = "Playback and recording devices: default device, volume, format, exclusive mode and enhancements.";
    public const string AudioPlaybackHeader = "PLAYBACK", AudioRecordingHeader = "RECORDING", AudioNone = "No active audio device found.",
        AudioPlaybackDevice = "Playback device", AudioRecordingDevice = "Recording device",
        AudioDefaultBadge = "Default", AudioCommunicationsBadge = "Communications", AudioMutedBadge = "Muted",
        AudioDefaultTitle = "Default device", AudioMakeDefault = "Make default", AudioMakeCommunications = "Default for communications",
        AudioVolumeTitle = "Volume", AudioMute = "Mute",
        AudioFormatTitle = "Format",
        AudioFormatNote = "The sample rate and bit depth Windows mixes this device at in shared mode (the Sound control panel's Default Format).",
        AudioFormatOnly = "The device reports no other format at its channel count.",
        AudioExclusiveTitle = "Exclusive mode", AudioExclusiveAllow = "Allow apps to take exclusive control of this device",
        AudioExclusivePriority = "Give exclusive mode apps priority", AudioExclusiveNone = "Windows doesn't report exclusive mode settings for this device.",
        AudioExclusiveNote = "Apps in exclusive mode, and those using ASIO or kernel streaming, send their audio straight to the device: Windows' audio effects, enhancements and equalizers don't apply to them.",
        AudioEnhancementsTitle = "Audio enhancements", AudioEnhancementsOff = "Turn audio enhancements off",
        AudioNoEnhancements = "This device has no audio enhancements to turn off.",
        AudioGone = "The device isn't connected anymore.";

    public static string AudioReadFailed(string error) => $"Reading the audio devices failed: {error}";
    public static string AudioApplied(string device, string state) => $"{device}: {state}.";
    public static string AudioNotConfirmed(string device, string setting, string state) => $"{device}: {setting} didn't take; the device reads back {state}.";
    public static string AudioFailed(string device, string setting, string error) => $"{device}: {setting} failed: {error}";

    public static string AudioFormatText(AudioFormat? f) => f?.ToString() ?? "—";
    public static string AudioVolumeText(int percent) => $"{percent} %";
    public static string AudioVolumeState(int percent) => $"volume {percent} %";
    public static string AudioMuteState(bool muted) => muted ? "muted" : "not muted";
    public static string AudioEnhancementsState(bool? off) => off == true ? "audio enhancements off" : "audio enhancements on";
    public static string AudioExclusiveState(bool? allow, bool? priority) =>
        allow != true ? "exclusive mode not allowed" : priority == true ? "exclusive mode allowed, with priority" : "exclusive mode allowed, without priority";
    public static string AudioDefaultState(bool main, bool communications) => (main, communications) switch
    {
        (true, true) => "the default device, for communications too",
        (true, false) => "the default device; another one handles communications",
        (false, true) => "the default communications device",
        _ => "not a default device",
    };
    public static string AudioDefaultLine(bool main, bool communications) => $"This is {AudioDefaultState(main, communications)}.";

    /// <summary>The list's second line: the default markers, muted, and the format.</summary>
    public static string AudioRowText(AudioDevice d) => string.Join(" · ", new[]
    {
        d.Default ? AudioDefaultBadge : null, d.DefaultCommunications ? AudioCommunicationsBadge : null, d.Muted ? AudioMutedBadge : null,
        d.Format?.ToString(),
    }.Where(s => s is not null));
}
