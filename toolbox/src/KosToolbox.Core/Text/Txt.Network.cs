namespace KosToolbox.Core.Text;

/// <summary>Texts for the Network page (advanced network adapter properties).</summary>
public static partial class Txt
{
    public const string PageNetwork = "Network", PageNetworkSubtitle = "Advanced properties of your network adapters.";
    public const string NetworkNote = "The same properties as Device Manager › adapter › Advanced, described by the adapter's driver. Changes stay pending until Apply; the adapter restarts so the driver reads them, which drops the connection for a few seconds.";
    public const string ShowUnlistedSettings = "Show settings this driver doesn't list";
    public const string NetworkAdapter = "Adapter", ShowVirtualAdapters = "Show virtual adapters", RestartAdapter = "Restart the adapter after applying",
        NoAdapters = "No network adapter found.", NoProperties = "This adapter's driver doesn't describe any advanced properties.",
        DefaultPill = "Default", DefaultTooltip = "No value is saved, so the driver uses its default.", ResetToDefault = "Use driver default";
    public const string NicNoInterface = "No network interface", NicDown = "Not connected", NicNotAnOption = "Choose one of the listed values.";
    public static string NicUp(long bitsPerSecond) => bitsPerSecond >= 1_000_000_000 ? $"Connected · {bitsPerSecond / 1_000_000_000.0:0.#} Gbps" : $"Connected · {bitsPerSecond / 1_000_000} Mbps";
    public static string NicNotANumber(int numberBase) => numberBase == 16 ? "Enter a hexadecimal number." : "Enter a whole number.";
    public static string NicOutOfRange(long? min, long? max) => $"Enter a value from {min?.ToString() ?? "…"} to {max?.ToString() ?? "…"}.";
    public static string NicStep(long step) => $"The value must go in steps of {step}.";
    public static string NicTextTooLong(int max) => $"Up to {max} characters, without control characters.";
    public static string NicRange(long? min, long? max, long? step) => $"{min}–{max}" + (step is > 1 ? $", steps of {step}" : "");
    public static string NicApplied(int count, bool restarted, bool reboot) =>
        $"{count} value{(count == 1 ? "" : "s")} saved and verified." + (reboot ? " Windows needs a restart to finish." : restarted ? " The adapter was restarted." : " Restart the adapter for them to take effect.");
    public static string NicPending(int count) => count == 1 ? "1 property changed and not applied." : $"{count} properties changed and not applied.";
    public static string NetworkReadFailed(string message) => $"The network adapters couldn't be read: {message}";
}
