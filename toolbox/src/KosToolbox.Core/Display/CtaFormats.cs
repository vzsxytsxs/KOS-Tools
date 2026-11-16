namespace KosToolbox.Core.Display;

/// <summary>A CTA-861 video format (VIC): size, interlacing, picture aspect and frame rate.</summary>
public sealed record Vic(int Code, int Width, int Height, bool Interlaced, int Aspect, int Hz)
{
    public string AspectText => Aspect switch { 4 => "4:3", 16 => "16:9", 17 => "256:135", 21 => "64:27", 12 => "16:9 (1250 lines)", _ => "" };
    public override string ToString() => $"{Width}x{Height}{(Interlaced ? "i" : "")} @ {Hz} Hz ({AspectText})";
}

/// <summary>A TV resolution of a video data block; native only for VICs 1–64 (bit 7).</summary>
public sealed record VideoEntry(int Code, bool Native);

/// <summary>Field layouts of the CTA-861 data blocks the editor understands (CTA-861-H; HDMI 1.4b/2.1 vendor blocks; AMD FreeSync).</summary>
public static class CtaFormats
{
    // ---------- TV resolutions (video data block) ----------
    static readonly (int Code, int W, int H, int I, int A, int Hz)[] VicTable =
    {
        (1, 640, 480, 0, 4, 60), (2, 720, 480, 0, 4, 60), (3, 720, 480, 0, 16, 60), (4, 1280, 720, 0, 16, 60), (5, 1920, 1080, 1, 16, 60), (6, 1440, 480, 1, 4, 60),
        (7, 1440, 480, 1, 16, 60), (8, 1440, 240, 0, 4, 60), (9, 1440, 240, 0, 16, 60), (10, 2880, 480, 1, 4, 60), (11, 2880, 480, 1, 16, 60), (12, 2880, 240, 0, 4, 60),
        (13, 2880, 240, 0, 16, 60), (14, 1440, 480, 0, 4, 60), (15, 1440, 480, 0, 16, 60), (16, 1920, 1080, 0, 16, 60), (17, 720, 576, 0, 4, 50), (18, 720, 576, 0, 16, 50),
        (19, 1280, 720, 0, 16, 50), (20, 1920, 1080, 1, 16, 50), (21, 1440, 576, 1, 4, 50), (22, 1440, 576, 1, 16, 50), (23, 1440, 288, 0, 4, 50), (24, 1440, 288, 0, 16, 50),
        (25, 2880, 576, 1, 4, 50), (26, 2880, 576, 1, 16, 50), (27, 2880, 288, 0, 4, 50), (28, 2880, 288, 0, 16, 50), (29, 1440, 576, 0, 4, 50), (30, 1440, 576, 0, 16, 50),
        (31, 1920, 1080, 0, 16, 50), (32, 1920, 1080, 0, 16, 24), (33, 1920, 1080, 0, 16, 25), (34, 1920, 1080, 0, 16, 30), (35, 2880, 480, 0, 4, 60), (36, 2880, 480, 0, 16, 60),
        (37, 2880, 576, 0, 4, 50), (38, 2880, 576, 0, 16, 50), (39, 1920, 1080, 1, 12, 50), (40, 1920, 1080, 1, 16, 100), (41, 1280, 720, 0, 16, 100), (42, 720, 576, 0, 4, 100),
        (43, 720, 576, 0, 16, 100), (44, 1440, 576, 1, 4, 100), (45, 1440, 576, 1, 16, 100), (46, 1920, 1080, 1, 16, 120), (47, 1280, 720, 0, 16, 120), (48, 720, 480, 0, 4, 120),
        (49, 720, 480, 0, 16, 120), (50, 1440, 480, 1, 4, 120), (51, 1440, 480, 1, 16, 120), (52, 720, 576, 0, 4, 200), (53, 720, 576, 0, 16, 200), (54, 1440, 576, 1, 4, 200),
        (55, 1440, 576, 1, 16, 200), (56, 720, 480, 0, 4, 240), (57, 720, 480, 0, 16, 240), (58, 1440, 480, 1, 4, 240), (59, 1440, 480, 1, 16, 240), (60, 1280, 720, 0, 16, 24),
        (61, 1280, 720, 0, 16, 25), (62, 1280, 720, 0, 16, 30), (63, 1920, 1080, 0, 16, 120), (64, 1920, 1080, 0, 16, 100), (65, 1280, 720, 0, 21, 24), (66, 1280, 720, 0, 21, 25),
        (67, 1280, 720, 0, 21, 30), (68, 1280, 720, 0, 21, 50), (69, 1280, 720, 0, 21, 60), (70, 1280, 720, 0, 21, 100), (71, 1280, 720, 0, 21, 120), (72, 1920, 1080, 0, 21, 24),
        (73, 1920, 1080, 0, 21, 25), (74, 1920, 1080, 0, 21, 30), (75, 1920, 1080, 0, 21, 50), (76, 1920, 1080, 0, 21, 60), (77, 1920, 1080, 0, 21, 100), (78, 1920, 1080, 0, 21, 120),
        (79, 1680, 720, 0, 21, 24), (80, 1680, 720, 0, 21, 25), (81, 1680, 720, 0, 21, 30), (82, 1680, 720, 0, 21, 50), (83, 1680, 720, 0, 21, 60), (84, 1680, 720, 0, 21, 100),
        (85, 1680, 720, 0, 21, 120), (86, 2560, 1080, 0, 21, 24), (87, 2560, 1080, 0, 21, 25), (88, 2560, 1080, 0, 21, 30), (89, 2560, 1080, 0, 21, 50), (90, 2560, 1080, 0, 21, 60),
        (91, 2560, 1080, 0, 21, 100), (92, 2560, 1080, 0, 21, 120), (93, 3840, 2160, 0, 16, 24), (94, 3840, 2160, 0, 16, 25), (95, 3840, 2160, 0, 16, 30), (96, 3840, 2160, 0, 16, 50),
        (97, 3840, 2160, 0, 16, 60), (98, 4096, 2160, 0, 17, 24), (99, 4096, 2160, 0, 17, 25), (100, 4096, 2160, 0, 17, 30), (101, 4096, 2160, 0, 17, 50), (102, 4096, 2160, 0, 17, 60),
        (103, 3840, 2160, 0, 21, 24), (104, 3840, 2160, 0, 21, 25), (105, 3840, 2160, 0, 21, 30), (106, 3840, 2160, 0, 21, 50), (107, 3840, 2160, 0, 21, 60), (108, 1280, 720, 0, 16, 48),
        (109, 1280, 720, 0, 21, 48), (110, 1680, 720, 0, 21, 48), (111, 1920, 1080, 0, 16, 48), (112, 1920, 1080, 0, 21, 48), (113, 2560, 1080, 0, 21, 48), (114, 3840, 2160, 0, 16, 48),
        (115, 4096, 2160, 0, 17, 48), (116, 3840, 2160, 0, 21, 48), (117, 3840, 2160, 0, 16, 100), (118, 3840, 2160, 0, 16, 120), (119, 3840, 2160, 0, 21, 100), (120, 3840, 2160, 0, 21, 120),
        (121, 5120, 2160, 0, 21, 24), (122, 5120, 2160, 0, 21, 25), (123, 5120, 2160, 0, 21, 30), (124, 5120, 2160, 0, 21, 48), (125, 5120, 2160, 0, 21, 50), (126, 5120, 2160, 0, 21, 60),
        (127, 5120, 2160, 0, 21, 100), (193, 5120, 2160, 0, 21, 120), (194, 7680, 4320, 0, 16, 24), (195, 7680, 4320, 0, 16, 25), (196, 7680, 4320, 0, 16, 30), (197, 7680, 4320, 0, 16, 48),
        (198, 7680, 4320, 0, 16, 50), (199, 7680, 4320, 0, 16, 60), (200, 7680, 4320, 0, 16, 100), (201, 7680, 4320, 0, 16, 120), (202, 7680, 4320, 0, 21, 24), (203, 7680, 4320, 0, 21, 25),
        (204, 7680, 4320, 0, 21, 30), (205, 7680, 4320, 0, 21, 48), (206, 7680, 4320, 0, 21, 50), (207, 7680, 4320, 0, 21, 60), (208, 7680, 4320, 0, 21, 100), (209, 7680, 4320, 0, 21, 120),
        (210, 10240, 4320, 0, 21, 24), (211, 10240, 4320, 0, 21, 25), (212, 10240, 4320, 0, 21, 30), (213, 10240, 4320, 0, 21, 48), (214, 10240, 4320, 0, 21, 50), (215, 10240, 4320, 0, 21, 60),
        (216, 10240, 4320, 0, 21, 100), (217, 10240, 4320, 0, 21, 120), (218, 4096, 2160, 0, 17, 100), (219, 4096, 2160, 0, 17, 120),
    };

    public static readonly IReadOnlyList<Vic> Vics = VicTable.Select(v => new Vic(v.Code, v.W, v.H, v.I == 1, v.A, v.Hz)).ToList();
    public static Vic? FindVic(int code) => Vics.FirstOrDefault(v => v.Code == code);

    /// <summary>Video data block entries: 129–192 are VICs 1–64 marked native; 193+ are VICs as they are.</summary>
    public static List<VideoEntry> ReadVideo(CtaDataBlock b) => b.Raw.Skip(1).Where(v => v is not (0 or 128 or 255))
        .Select(v => v is >= 129 and <= 192 ? new VideoEntry(v & 0x7F, true) : new VideoEntry(v, false)).ToList();

    public static CtaDataBlock BuildVideo(IEnumerable<VideoEntry> entries) => CtaDataBlock.Create(2,
        entries.Select(e => (byte)(e.Native && e.Code is >= 1 and <= 64 ? e.Code | 0x80 : e.Code)).ToArray());

    // ---------- audio formats (short audio descriptors) ----------
    public static string AudioName(int code) => code switch
    {
        1 => "LPCM", 2 => "AC-3", 3 => "MPEG-1", 4 => "MP3", 5 => "MPEG-2", 6 => "AAC", 7 => "DTS", 8 => "ATRAC", 9 => "DSD",
        10 => "E-AC-3", 11 => "DTS-HD", 12 => "TrueHD", 13 => "DST", 14 => "WMA Pro", _ => $"Format {code}",
    };

    /// <summary>The 3-byte descriptors of an audio data block.</summary>
    public static List<byte[]> ReadAudio(CtaDataBlock b) =>
        Enumerable.Range(0, b.Length / 3).Select(i => b.Raw.AsSpan(1 + i * 3, 3).ToArray()).ToList();

    public static CtaDataBlock BuildAudio(IEnumerable<byte[]> sads) => CtaDataBlock.Create(1, sads.SelectMany(s => s).ToArray());

    /// <summary>A descriptor for a format with the usual capabilities (as the HDMI audio card adds them).</summary>
    public static byte[]? DefaultSad(int code) => code switch
    {
        1 => new byte[] { 0x09, 0x07, 0x07 },   // LPCM 2 channels, 32/44.1/48 kHz, 16/20/24 bits
        2 => new byte[] { 0x15, 0x07, 0x50 },   // AC-3 6 channels, 640 kbps
        7 => new byte[] { 0x3D, 0x06, 0xC0 },   // DTS 6 channels, 1536 kbps
        10 => new byte[] { 0x57, 0x06, 0x00 },  // E-AC-3 8 channels
        11 => new byte[] { 0x5F, 0x7E, 0x01 },  // DTS-HD 8 channels, up to 192 kHz
        12 => new byte[] { 0x67, 0x7E, 0x00 },  // TrueHD 8 channels, up to 192 kHz
        _ => null,
    };

    // ---------- speaker setup ----------
    /// <summary>The speaker allocation bits (byte 1 bit 0 first), with CRU's names.</summary>
    public static readonly string[] Speakers =
    {
        "Front left/right", "Low frequency effects", "Front center", "Rear left/right", "Rear center", "Front left/right center", "Rear left/right center", "Front left/right wide",
        "Top front left/right", "Top center", "Top front center", "Left/right surround", "Low frequency effects 2", "Top back center", "Side left/right", "Top side left/right",
        "Top back left/right", "Bottom front center", "Bottom front left/right", "Top left/right surround",
    };

    public static int ReadSpeakers(CtaDataBlock b) => b.Raw.Skip(1).Take(3).Select((v, i) => v << i * 8).Aggregate(0, (a, v) => a | v);
    public static CtaDataBlock BuildSpeakers(int bits) => CtaDataBlock.Create(4, new[] { (byte)bits, (byte)(bits >> 8), (byte)(bits >> 16 & 0x0F) });
    public const int Stereo = 0x01, Surround51 = 0x0F, Surround71 = 0x4F;

    public static string SpeakerSummary(CtaDataBlock b) => ReadSpeakers(b) switch
    {
        Stereo => "Stereo", Surround51 => "5.1 surround", Surround71 => "7.1 surround", var bits => $"{Enumerable.Range(0, 20).Count(i => (bits >> i & 1) != 0)} speaker groups",
    };

    // ---------- FreeSync range (AMD vendor block) ----------
    /// <summary>Min and max refresh; the 10-bit max of the longer versions when present.</summary>
    public static (int Min, int Max)? ReadFreeSync(CtaDataBlock b)
    {
        if (b.Kind != CtaKind.FreeSync || b.Length < 7) return null;
        var max = b.Length >= 15 ? (b.Raw[15] & 3) << 8 | b.Raw[14] : b.Raw[7];
        return (b.Raw[6], max);
    }

    /// <summary>Sets the range in an existing AMD block (or a new version 1 block), keeping its other bytes.</summary>
    public static CtaDataBlock BuildFreeSync(CtaDataBlock? existing, int min, int max)
    {
        var raw = existing?.Raw.ToArray() ?? new byte[] { 0x68, 0x1A, 0x00, 0x00, 0x01, 0x01, 0, 0, 0 };
        var wide = raw.Length > 15;
        if (min is < 1 or > 255 || max < min || max > (wide ? 1023 : 255)) throw new ArgumentOutOfRangeException(nameof(max), $"Enter 1–255 Hz for the minimum and up to {(wide ? 1023 : 255)} Hz for the maximum, minimum not above maximum.");
        raw[6] = (byte)min;
        raw[7] = (byte)Math.Min(max, 255);
        if (wide)
        {
            raw[14] = (byte)max;
            raw[15] = (byte)(raw[15] & 0xFC | max >> 8);
        }
        return new CtaDataBlock(raw);
    }

    // ---------- colorimetry, HDR static metadata, video capability (extended tags 5, 6, 0) ----------
    /// <summary>Colorimetry bits (bytes 3 and 4 of the block, low bit first) with CRU's names; bits 8–11 are MD0–MD3.</summary>
    public static readonly (int Bit, string Name)[] ColorimetryBits =
    {
        (0, "xvYCC601"), (1, "xvYCC709"), (2, "sYCC601"), (3, "Adobe/opYCC601"), (4, "Adobe/opRGB"), (5, "BT.2020 cYCC"), (6, "BT.2020 YCC"), (7, "BT.2020 RGB"),
        (15, "DCI-P3"), (14, "ICtCp"), (13, "sRGB"), (12, "Default RGB"), (8, "MD0"), (9, "MD1"), (10, "MD2"), (11, "MD3"),
    };

    public static int ReadColorimetry(CtaDataBlock b) => (b.Raw.Length > 2 ? b.Raw[2] : 0) | (b.Raw.Length > 3 ? b.Raw[3] << 8 : 0);
    public static CtaDataBlock BuildColorimetry(int bits) => CtaDataBlock.Create(7, new byte[] { 5, (byte)bits, (byte)(bits >> 8) });

    public static readonly string[] Eotfs = { "Traditional gamma - SDR", "Traditional gamma - HDR", "SMPTE ST 2084 (PQ)", "Hybrid Log-Gamma" };

    /// <summary>HDR static metadata: EOTF bits, metadata type 1, and the three luminance codes (0 = not given).</summary>
    public sealed record HdrStatic(int Eotfs, bool Type1, int MaxCode, int AvgCode, int MinCode)
    {
        /// <summary>CTA-861: 50 × 2^(code/32) cd/m².</summary>
        public static double Luminance(int code) => 50 * Math.Pow(2, code / 32.0);
        /// <summary>Max luminance × (code/255)² / 100.</summary>
        public double MinLuminance => MaxCode == 0 ? 0 : Luminance(MaxCode) * Math.Pow(MinCode / 255.0, 2) / 100;
    }

    public static HdrStatic ReadHdr(CtaDataBlock b)
    {
        int At(int i) => b.Raw.Length > i ? b.Raw[i] : 0;
        return new HdrStatic(At(2) & 0x0F, (At(3) & 1) != 0, At(4), At(5), At(6));
    }

    public static CtaDataBlock BuildHdr(HdrStatic h)
    {
        var payload = new List<byte> { 6, (byte)h.Eotfs, (byte)(h.Type1 ? 1 : 0) };
        // The luminance bytes are optional, but each needs the ones before it.
        var codes = new[] { h.MaxCode, h.AvgCode, h.MinCode };
        var last = Array.FindLastIndex(codes, c => c != 0);
        for (var i = 0; i <= last; i++) payload.Add((byte)codes[i]);
        return CtaDataBlock.Create(7, payload.ToArray());
    }

    public static readonly string[] ScanModes = { "Not supported", "Overscanned", "Not overscanned", "Selectable overscan" };

    /// <summary>Video capability: QY (selectable RGB range), QS (selectable YCC range), and the PT/IT/CE scan modes (0–3).</summary>
    public sealed record VideoCapability(bool Qy, bool Qs, int Pt, int It, int Ce);

    public static VideoCapability ReadVideoCapability(CtaDataBlock b)
    {
        var v = b.Raw.Length > 2 ? b.Raw[2] : 0;
        return new VideoCapability((v & 0x80) != 0, (v & 0x40) != 0, v >> 4 & 3, v >> 2 & 3, v & 3);
    }

    public static CtaDataBlock BuildVideoCapability(VideoCapability c) => CtaDataBlock.Create(7,
        new byte[] { 0, (byte)((c.Qy ? 0x80 : 0) | (c.Qs ? 0x40 : 0) | (c.Pt & 3) << 4 | (c.It & 3) << 2 | c.Ce & 3) });

    // ---------- HDMI (HDMI 1.4b vendor block) and HDMI 2.x (HDMI Forum block) ----------
    /// <summary>The fields the editor changes; the rest of the block (latency, 3D, ...) is kept.</summary>
    public sealed record HdmiSupport(string PhysicalAddress, bool Ai, bool Dc48, bool Dc36, bool Dc30, bool DcY444, int MaxTmdsMhz);

    public static HdmiSupport ReadHdmi(CtaDataBlock b)
    {
        int At(int i) => b.Raw.Length > i ? b.Raw[i] : 0;
        var address = $"{At(4) >> 4}.{At(4) & 15}.{At(5) >> 4}.{At(5) & 15}";
        var f = At(6);
        return new HdmiSupport(address, (f & 0x80) != 0, (f & 0x40) != 0, (f & 0x20) != 0, (f & 0x10) != 0, (f & 0x08) != 0, b.Length >= 7 ? At(7) * 5 : 0);
    }

    public static CtaDataBlock BuildHdmi(CtaDataBlock? existing, HdmiSupport h)
    {
        var parts = h.PhysicalAddress.Split('.');
        if (parts.Length != 4 || parts.Any(p => !int.TryParse(p, out var n) || n is < 0 or > 15)) throw new ArgumentException("Enter the physical address as four numbers from 0 to 15, like 1.0.0.0.");
        if (h.MaxTmdsMhz is < 0 or > 1275 || h.MaxTmdsMhz % 5 != 0) throw new ArgumentException("Max TMDS clock: 0 (not given) or 5–1275 MHz in steps of 5.");
        var raw = (existing?.Raw ?? new byte[] { 0x65, 0x03, 0x0C, 0x00, 0x10, 0x00 }).ToList();
        var n = parts.Select(int.Parse).ToArray();
        raw[4] = (byte)(n[0] << 4 | n[1]);
        raw[5] = (byte)(n[2] << 4 | n[3]);
        var flags = (h.Ai ? 0x80 : 0) | (h.Dc48 ? 0x40 : 0) | (h.Dc36 ? 0x20 : 0) | (h.Dc30 ? 0x10 : 0) | (h.DcY444 ? 0x08 : 0);
        if (raw.Count < 7 && (flags != 0 || h.MaxTmdsMhz > 0)) raw.Add(0);
        if (raw.Count >= 7) raw[6] = (byte)(raw[6] & 0x07 | flags);
        if (raw.Count < 8 && h.MaxTmdsMhz > 0) raw.Add(0);
        if (raw.Count >= 8) raw[7] = (byte)(h.MaxTmdsMhz / 5);
        return CtaDataBlock.Create(3, raw.Skip(1).ToArray());
    }

    /// <summary>HDMI 2.x: max TMDS character rate, SCDC, scrambling below 340 Mcsc, 4:2:0 deep color and the VRR range (HDMI 2.1).</summary>
    public sealed record HdmiForum(int MaxTmdsMhz, bool Scdc, bool Scramble340, bool Dc420_30, bool Dc420_36, bool Dc420_48, int VrrMin, int VrrMax);

    public static HdmiForum ReadHdmiForum(CtaDataBlock b)
    {
        int At(int i) => b.Raw.Length > i ? b.Raw[i] : 0;
        var vrr = b.Length >= 10;
        return new HdmiForum(At(5) * 5, (At(6) & 0x80) != 0, (At(6) & 0x08) != 0, (At(7) & 1) != 0, (At(7) & 2) != 0, (At(7) & 4) != 0,
            vrr ? At(9) & 0x3F : 0, vrr ? (At(9) & 0xC0) << 2 | At(10) : 0);
    }

    public static CtaDataBlock BuildHdmiForum(CtaDataBlock? existing, HdmiForum h)
    {
        if (h.MaxTmdsMhz is < 0 or > 1275 || h.MaxTmdsMhz % 5 != 0) throw new ArgumentException("Max TMDS rate: 0 (not given) or 5–1275 MHz in steps of 5.");
        if (h.VrrMin is < 0 or > 63 || h.VrrMax is < 0 or > 1023 || (h.VrrMax > 0 && h.VrrMax < h.VrrMin)) throw new ArgumentException("VRR range: minimum 0–63 Hz, maximum 0–1023 Hz (0 = none).");
        var raw = (existing?.Raw ?? new byte[] { 0x67, 0xD8, 0x5D, 0xC4, 0x01, 0x00, 0x00, 0x00 }).ToList();
        raw[5] = (byte)(h.MaxTmdsMhz / 5);
        raw[6] = (byte)(raw[6] & 0x77 | (h.Scdc ? 0x80 : 0) | (h.Scramble340 ? 0x08 : 0));
        raw[7] = (byte)(raw[7] & 0xF8 | (h.Dc420_30 ? 1 : 0) | (h.Dc420_36 ? 2 : 0) | (h.Dc420_48 ? 4 : 0));
        if (h.VrrMin > 0 || h.VrrMax > 0 || raw.Count > 10)
        {
            while (raw.Count < 11) raw.Add(0);
            raw[9] = (byte)((h.VrrMax >> 2 & 0xC0) | h.VrrMin & 0x3F);
            raw[10] = (byte)h.VrrMax;
        }
        return CtaDataBlock.Create(3, raw.Skip(1).ToArray());
    }

    // ---------- any block, as hex ----------
    public static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).Chunk(2).Select(c => new string(c)).Aggregate("", (a, b) => a.Length == 0 ? b : $"{a} {b}");

    /// <summary>Hex bytes separated by spaces, commas or dashes (or run together); null when a group isn't whole bytes of hex.</summary>
    public static byte[]? ParseHex(string text)
    {
        var groups = text.Split(new[] { ' ', '\t', '\r', '\n', ',', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (groups.Any(g => g.Length % 2 != 0 || !g.All(Uri.IsHexDigit))) return null;
        return Convert.FromHexString(string.Concat(groups));
    }
}
