namespace KosToolbox.Core.Network;

/// <summary>A named value of a known keyword (for the ones the driver doesn't describe).</summary>
public sealed record NetTweakOption(string Value, string Label);

/// <summary>
/// A network adapter keyword known to the KOS Network Tweaker (standard NDIS "*..." or vendor-specific), with the
/// page section and the recommended values (per vendor, by PCI VEN; "*" = any).
/// </summary>
public sealed record NetKnownKeyword(string Keyword, string? Name, string Section, IReadOnlyList<NetTweakOption> Options, long? Min, long? Max,
    IReadOnlyDictionary<string, string> Recommended);

/// <summary>Recommended values from the KOS Network Tweaker (Setup\4-Tweaking\Network\Advanced\5-Network_Tweaker.ps1).</summary>
public static partial class NetTweaks
{
    public const string SectionAdvanced = "advanced", SectionInterrupt = "interrupt", SectionRss = "rss";

    /// <summary>Vendor from the PCI VEN (for values that differ per vendor).</summary>
    public static string Vendor(string instanceId)
    {
        var i = instanceId.IndexOf("VEN_", StringComparison.OrdinalIgnoreCase);
        var ven = i >= 0 && instanceId.Length >= i + 8 ? instanceId.Substring(i + 4, 4).ToUpperInvariant() : "";
        return ven switch
        {
            "10EC" => "realtek",
            "8086" => "intel",
            "1969" or "168C" => "killer",
            "14E4" => "broadcom",
            "11AB" or "1B4B" => "marvell",
            "1D6A" => "aquantia",
            _ => "other",
        };
    }

    // Built on first use: the static fields of the other file (the option lists) are already ready.
    static Dictionary<string, NetKnownKeyword>? _byKeyword;
    static Dictionary<string, NetKnownKeyword> ByKeyword => _byKeyword ??= KeywordList().ToDictionary(k => k.Keyword, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<NetKnownKeyword> Keywords => ByKeyword.Values;

    public static string SectionOf(string keyword) => ByKeyword.TryGetValue(keyword, out var k) ? k.Section : SectionAdvanced;

    /// <summary>Recommended value for the keyword on that vendor; null if the script doesn't touch it.</summary>
    public static string? Recommended(string keyword, string vendor) =>
        ByKeyword.TryGetValue(keyword, out var k) ? k.Recommended.GetValueOrDefault(vendor) ?? k.Recommended.GetValueOrDefault("*") : null;
}
