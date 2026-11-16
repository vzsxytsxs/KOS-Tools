namespace KosToolbox.Core.Display;

/// <summary>Timing modes of the editor (CRU's names).</summary>
public enum TimingMode { Manual, LcdStandard, LcdNative, LcdReduced, Exact, ExactReduced }

/// <summary>
/// EDID Detailed Timing Descriptor (18 bytes, VESA E-EDID 1.4 §3.10.2). Pixel clock in kHz (stored in steps of 10 kHz).
/// </summary>
public sealed record DetailedTiming(int PixelClockKhz, int HActive, int HBlank, int HFront, int HSync, int VActive, int VBlank, int VFront, int VSync,
    bool Interlaced = false, bool HPositive = true, bool VPositive = false, int WidthMm = 0, int HeightMm = 0)
{
    public const int MaxPixelClockKhz = 655_350;
    public int HTotal => HActive + HBlank;
    public int VTotal => VActive + VBlank;
    public double RefreshHz => HTotal == 0 || VTotal == 0 ? 0 : PixelClockKhz * 1000.0 / ((double)HTotal * VTotal);

    /// <summary>Null when the 18 bytes are a monitor descriptor (pixel clock 0), not a timing.</summary>
    public static DetailedTiming? Decode(ReadOnlySpan<byte> d)
    {
        var clock = d[0] | d[1] << 8;
        if (clock == 0) return null;
        var flags = d[17];
        var digitalSeparate = (flags & 0x18) == 0x18;
        return new DetailedTiming(clock * 10,
            d[2] | (d[4] & 0xF0) << 4, d[3] | (d[4] & 0x0F) << 8,
            d[8] | (d[11] & 0xC0) << 2, d[9] | (d[11] & 0x30) << 4,
            d[5] | (d[7] & 0xF0) << 4, d[6] | (d[7] & 0x0F) << 8,
            (d[10] >> 4) | (d[11] & 0x0C) << 2, (d[10] & 0x0F) | (d[11] & 0x03) << 4,
            (flags & 0x80) != 0, digitalSeparate && (flags & 0x02) != 0, digitalSeparate && (flags & 0x04) != 0,
            d[12] | (d[14] & 0xF0) << 4, d[13] | (d[14] & 0x0F) << 8);
    }

    /// <summary>Null when some field does not fit the format (e.g. pixel clock above 655.35 MHz).</summary>
    public string? Problem() =>
        PixelClockKhz <= 0 || PixelClockKhz > MaxPixelClockKhz ? "pixel clock" :
        HActive is <= 0 or > 4095 || HBlank is <= 0 or > 4095 || VActive is <= 0 or > 4095 || VBlank is <= 0 or > 4095 ? "size" :
        HFront is < 0 or > 1023 || HSync is <= 0 or > 1023 || VFront is < 0 or > 63 || VSync is <= 0 or > 63 || HFront + HSync > HBlank || VFront + VSync > VBlank ? "sync" :
        WidthMm is < 0 or > 4095 || HeightMm is < 0 or > 4095 ? "image size" : null;

    public byte[] Encode()
    {
        if (Problem() is { } p) throw new InvalidOperationException($"The timing can't be stored in an EDID ({p}).");
        var clock = PixelClockKhz / 10;
        return new byte[]
        {
            (byte)clock, (byte)(clock >> 8),
            (byte)HActive, (byte)HBlank, (byte)((HActive >> 8) << 4 | HBlank >> 8),
            (byte)VActive, (byte)VBlank, (byte)((VActive >> 8) << 4 | VBlank >> 8),
            (byte)HFront, (byte)HSync, (byte)((VFront & 0x0F) << 4 | VSync & 0x0F),
            (byte)((HFront >> 8) << 6 | (HSync >> 8) << 4 | (VFront >> 4) << 2 | VSync >> 4),
            (byte)WidthMm, (byte)HeightMm, (byte)((WidthMm >> 8) << 4 | HeightMm >> 8),
            0, 0,
            (byte)((Interlaced ? 0x80 : 0) | 0x18 | (VPositive ? 0x04 : 0) | (HPositive ? 0x02 : 0)),
        };
    }

    /// <summary>Same timing (same totals and sync) at another frequency: only the pixel clock changes.</summary>
    public DetailedTiming WithRefresh(double hz) => this with { PixelClockKhz = (int)Math.Round(HTotal * (double)VTotal * hz / 10_000.0, MidpointRounding.AwayFromZero) * 10 };

    /// <summary>CVT vertical sync width by screen aspect ratio.</summary>
    static int CvtVSync(int w, int h) => (w * 3 == h * 4) ? 4 : (w * 9 == h * 16) ? 5 : (w * 10 == h * 16) ? 6 : (w * 4 == h * 5 || w * 9 == h * 15) ? 7 : 10;

    /// <summary>Exact pixel clock for the totals and frequency (in steps of 10 kHz, what the EDID stores).</summary>
    static int ExactClockKhz(int totalH, int totalV, double hz) => (int)Math.Round(totalH * (double)totalV * hz / 10_000.0, MidpointRounding.AwayFromZero) * 10;

    /// <summary>VESA CVT 1.2, Reduced Blanking v1 (no interlacing and no margins). <paramref name="exactClock"/>: exact frequency instead of 0.25 MHz steps.</summary>
    public static DetailedTiming CvtReducedBlanking(int width, int height, double hz, bool exactClock = false)
    {
        const double MinVBlankUs = 460, ClockStepMhz = 0.25;
        const int HBlank = 160, HFront = 48, HSync = 32, VFront = 3, MinVBackPorch = 6;
        var w = width / 8 * 8; // cell granularity: 8 pixels
        var vSync = CvtVSync(w, height);
        var hPeriodEstUs = (1_000_000 / hz - MinVBlankUs) / height;
        var vbiLines = Math.Max((int)Math.Floor(MinVBlankUs / hPeriodEstUs) + 1, VFront + vSync + MinVBackPorch);
        var totalV = height + vbiLines;
        var totalH = w + HBlank;
        var clockKhz = exactClock ? ExactClockKhz(totalH, totalV, hz) : (int)Math.Round(ClockStepMhz * Math.Floor(hz * totalV * totalH / 1_000_000 / ClockStepMhz) * 1000);
        return new DetailedTiming(clockKhz, w, HBlank, HFront, HSync, height, vbiLines, VFront, vSync, HPositive: true, VPositive: false);
    }

    /// <summary>VESA CVT 1.2 with standard blanking (CRT/old LCD): negative horizontal sync, positive vertical.</summary>
    public static DetailedTiming Cvt(int width, int height, double hz, bool exactClock = false)
    {
        const double MinVSyncBpUs = 550, CPrime = 30, MPrime = 300, ClockStepMhz = 0.25, HSyncPercent = 8;
        const int Cell = 8, MinVPorch = 3, MinVBackPorch = 6;
        var w = width / Cell * Cell;
        var vSync = CvtVSync(w, height);
        var hPeriodEstUs = (1_000_000 / hz - MinVSyncBpUs) / (height + MinVPorch);
        var vSyncBp = Math.Max((int)Math.Floor(MinVSyncBpUs / hPeriodEstUs) + 1, vSync + MinVBackPorch);
        var totalV = height + vSyncBp + MinVPorch;
        var idealDuty = Math.Max(20, CPrime - MPrime * hPeriodEstUs / 1000);
        var hBlank = (int)Math.Floor(w * idealDuty / (100 - idealDuty) / (2 * Cell)) * 2 * Cell;
        var totalH = w + hBlank;
        var hSync = (int)Math.Floor(HSyncPercent / 100 * totalH / Cell) * Cell;
        var clockKhz = exactClock ? ExactClockKhz(totalH, totalV, hz) : (int)Math.Round(ClockStepMhz * Math.Floor(totalH / hPeriodEstUs / ClockStepMhz) * 1000);
        return new DetailedTiming(clockKhz, w, hBlank, hBlank / 2 - hSync, hSync, height, vSyncBp + MinVPorch, MinVPorch, vSync, HPositive: false, VPositive: true);
    }

    /// <summary>CTA-861 standard formats (what TVs and monitors accept most), at the requested frequency with exact clock.</summary>
    public static DetailedTiming? CtaStandard(int width, int height, double hz)
    {
        DetailedTiming? t = (width, height) switch
        {
            (1280, 720) => new(0, 1280, 370, 110, 40, 720, 30, 5, 5, HPositive: true, VPositive: true),
            (1920, 1080) => new(0, 1920, 280, 88, 44, 1080, 45, 4, 5, HPositive: true, VPositive: true),
            (3840, 2160) => new(0, 3840, 560, 176, 88, 2160, 90, 8, 10, HPositive: true, VPositive: true),
            (720, 480) => new(0, 720, 138, 16, 62, 480, 45, 9, 6, HPositive: false, VPositive: false),
            (720, 576) => new(0, 720, 144, 12, 64, 576, 49, 5, 5, HPositive: false, VPositive: false),
            _ => null,
        };
        return t?.WithRefresh(hz);
    }

    /// <summary>Automatic timing like the CRU modes. "LCD native" uses the blanking of the native timing.</summary>
    public static DetailedTiming Generate(TimingMode mode, int width, int height, double hz, DetailedTiming? native) => mode switch
    {
        TimingMode.LcdStandard => CtaStandard(width, height, hz) ?? Cvt(width, height, hz),
        TimingMode.LcdNative when native is not null => (native with
        {
            HActive = width, VActive = height, Interlaced = false,
        }).WithRefresh(hz),
        TimingMode.LcdNative or TimingMode.LcdReduced => CvtReducedBlanking(width, height, hz),
        TimingMode.Exact => Cvt(width, height, hz, exactClock: true),
        TimingMode.ExactReduced => CvtReducedBlanking(width, height, hz, exactClock: true),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), "Manual timings aren't generated."),
    };

    public override string ToString() => $"{HActive} × {VActive}{(Interlaced ? "i" : "")} @ {RefreshHz:0.###} Hz";
}
