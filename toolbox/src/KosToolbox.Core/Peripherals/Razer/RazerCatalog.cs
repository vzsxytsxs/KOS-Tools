namespace KosToolbox.Core.Peripherals.Razer;

/// <summary>
/// A Razer mouse the toolbox writes to. <paramref name="TransactionId"/>: the id this product answers on (a wrong one gives silence,
/// not an error). <paramref name="DpiStorage"/>: the storage byte of DPI reads on that generation. <paramref name="Rates"/>: polling
/// rates it can be set to. <paramref name="Tested"/>: checked on hardware for this exact PID (by us, or upstream's verified flag).
/// <paramref name="LightOnly"/>: not a mouse (a dock): firmware, serial and the brightness of <paramref name="Led"/> only.
/// </summary>
public sealed record RazerModel(ushort ProductId, string Name, byte TransactionId, bool Wireless, int MaxDpi, IReadOnlyList<int> Rates, bool Tested,
    byte DpiStorage = RazerCodec.Stored, byte? Led = null, bool LightOnly = false);

/// <summary>
/// Our own entries, one per product id. Sources: mouse-protocol src/razer/devices.ts (transaction ids audited against OpenRazer,
/// DPI ceilings, verified flags) at 5a4f6433da3d; OpenRazer's razermouse_driver for the Viper Ultimate's storage byte. The Mouse Dock
/// (007E) is claimed for its ring light only; the Mouse Dock Pro stays unclaimed.
/// </summary>
public static class RazerCatalog
{
    public const ushort VendorId = 0x1532;
    static readonly int[] Rates1K = { 125, 500, 1000 };

    public static readonly IReadOnlyList<RazerModel> Models = new RazerModel[]
    {
        // Viper Ultimate: OpenRazer sends 0xFF; a hardware report upstream had 0x3F answering and 0x1F silent. 0x3F first, then the
        // getter-only fallback (RazerDriver) tries 0xFF. DPI is read with the no-store byte on this generation (OpenRazer get_dpi_xy).
        new(0x007A, "Razer Viper Ultimate", 0x3F, false, 20000, Rates1K, false, RazerCodec.NoStore, Led: 0x04),
        new(0x007B, "Razer Viper Ultimate", 0x3F, true, 20000, Rates1K, false, RazerCodec.NoStore, Led: 0x04),
        // Mouse Dock (Viper Ultimate's charging dock), checked on hardware: answers on any id (1F first), firmware 00/81 and serial
        // 00/82; the ring light is led 0x05 (0F/84 and 0F/04, stored and live both read back); battery, DPI and sleep are unsupported.
        new(0x007E, "Razer Mouse Dock", 0x1F, false, 0, Array.Empty<int>(), true, Led: 0x05, LightOnly: true),
        new(0x007C, "Razer DeathAdder V2 Pro", 0x3F, false, 20000, Rates1K, false, RazerCodec.NoStore),
        new(0x007D, "Razer DeathAdder V2 Pro", 0x3F, true, 20000, Rates1K, false, RazerCodec.NoStore),
        // Verified upstream (devices.ts `verified: true`), all on 0x1F.
        new(0x00A5, "Razer Viper V2 Pro", 0x1F, false, 30000, Rates1K, true),
        new(0x00A6, "Razer Viper V2 Pro", 0x1F, true, 30000, Rates1K, true),
        new(0x00A7, "Razer Naga V2 Pro", 0x1F, false, 30000, Rates1K, true),
        new(0x00A8, "Razer Naga V2 Pro", 0x1F, true, 30000, Rates1K, true),
        new(0x00B6, "Razer DeathAdder V3 Pro", 0x1F, false, 30000, Rates1K, true),
        new(0x00B7, "Razer DeathAdder V3 Pro", 0x1F, true, 30000, Rates1K, true),
        new(0x00B8, "Razer Viper V3 HyperSpeed", 0x1F, true, 30000, Rates1K, true),
    };

    public static RazerModel? Find(ushort vendorId, ushort productId) =>
        vendorId == VendorId ? Models.FirstOrDefault(m => m.ProductId == productId) : null;

    /// <summary>The ids to try for getters, the catalog's first (Wireless Battery tries 1F, 3F, FF in turn).</summary>
    public static IEnumerable<byte> TransactionIds(RazerModel model) =>
        new[] { model.TransactionId }.Concat(new byte[] { 0x1F, 0x3F, 0xFF }.Where(t => t != model.TransactionId));
}
