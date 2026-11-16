using KosToolbox.Core.Text;
using Microsoft.Win32;

namespace KosToolbox.Core.SystemInfo;

/// <summary>Anti-cheats that can require VBS or memory integrity, found by their Windows services.</summary>
public static class AntiCheats
{
    public const string Vanguard = "Riot Vanguard", Faceit = "FACEIT";

    // vgc/vgk: Vanguard's service and kernel driver; FACEIT's service and driver.
    static readonly (string Name, string[] Services)[] Known = { (Vanguard, new[] { "vgc", "vgk" }), (Faceit, new[] { "FACEIT", "FACEITService" }) };

    public static IReadOnlyList<string> Installed()
    {
        using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        return services is null ? Array.Empty<string>()
            : Known.Where(k => k.Services.Any(s => { using var key = services.OpenSubKey(s); return key is not null; })).Select(k => k.Name).ToList();
    }

    /// <summary>What the installed anti-cheats can ask for, added to the warning after turning VBS or memory integrity off; null when none is installed.</summary>
    public static string? Note(IReadOnlyList<string> installed, bool memoryIntegrity) => installed.Count == 0 ? null
        : string.Join(" ", installed.Select(a => a switch
        {
            Vanguard => Txt.AntiCheatVanguard,
            _ => memoryIntegrity ? Txt.AntiCheatFaceitHvci : Txt.AntiCheatFaceitVbs,
        }));
}
