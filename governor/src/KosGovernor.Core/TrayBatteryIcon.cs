using System.Runtime.InteropServices;

namespace KosGovernor.Core;

/// <summary>What the tray icon draws inside the battery ring.</summary>
public enum TrayGlyph { Mouse, Headset, Keyboard, Controller, Other }

/// <summary>
/// A device battery as a tray icon: a ring that empties clockwise from the top as the battery drops, with the device's outline inside.
/// White on the dark taskbar, amber at 15 % or less when not charging, a yellow bolt top right while charging. Drawn in managed code at
/// the tray size (no System.Drawing, which the Native AOT engine can't use): 4×4 samples per pixel for the edges.
/// </summary>
public static class TrayBatteryIcon
{
    const uint White = 0xFFFFFF, Amber = 0xFFB020, Track = 0x5A5A5A, Bolt = 0xFFD400;
    const int Samples = 4;

    /// <summary>BGRA pixels, top row first, straight (not premultiplied) alpha, as a 32-bit icon holds them.</summary>
    public static uint[] Render(int size, int percent, TrayGlyph glyph, bool charging)
    {
        percent = Math.Clamp(percent, 0, 100);
        var color = percent <= 15 && !charging ? Amber : White;
        var px = new uint[size * size];
        var s = size / 16.0; // shapes are laid out on a 16-unit grid
        double ringOuter = 7.6 * s, ringWidth = Math.Max(1.2, 1.7 * s), stroke = Math.Max(1.0, 1.25 * s);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                double r = 0, g = 0, b = 0, a = 0;
                for (var sy = 0; sy < Samples; sy++)
                    for (var sx = 0; sx < Samples; sx++)
                    {
                        var u = (x + (sx + 0.5) / Samples) / s; // 0..16
                        var v = (y + (sy + 0.5) / Samples) / s;
                        uint? c = null;
                        if (charging && InBolt(u, v)) c = Bolt;
                        else if (Ring(u, v, ringOuter / s, ringWidth / s) is { } angle) c = angle <= percent / 100.0 ? color : Track;
                        else if (InGlyph(glyph, u, v, stroke / s)) c = color;
                        if (c is not { } k) continue;
                        r += (k >> 16) & 0xFF; g += (k >> 8) & 0xFF; b += k & 0xFF; a += 1;
                    }
                if (a == 0) continue;
                // straight alpha, as in an .ico: the color of the covered samples, the alpha their share
                px[y * size + x] = (uint)(a * 255 / (Samples * Samples)) << 24 | (uint)(r / a) << 16 | (uint)(g / a) << 8 | (uint)(b / a);
            }
        return px;
    }

    /// <summary>Inside the ring: how far round from the top, clockwise (0..1); null outside it.</summary>
    static double? Ring(double u, double v, double outer, double width)
    {
        double dx = u - 8, dy = v - 8, d = Math.Sqrt(dx * dx + dy * dy);
        if (d > outer || d < outer - width) return null;
        var angle = Math.Atan2(dx, -dy); // 0 at the top, growing clockwise
        return (angle < 0 ? angle + 2 * Math.PI : angle) / (2 * Math.PI);
    }

    static bool InGlyph(TrayGlyph glyph, double u, double v, double w) => glyph switch
    {
        // a mouse seen from above: a rounded body and the wheel
        TrayGlyph.Mouse => Math.Abs(RoundRect(u - 8, v - 8.1, 2.3, 3.4, 2.3)) < w / 2 || (Math.Abs(u - 8) < w / 2 && v > 5.4 && v < 7.3),
        // a headband and two ear cups
        TrayGlyph.Headset => (Math.Abs(Math.Sqrt((u - 8) * (u - 8) + (v - 8.6) * (v - 8.6)) - 3.1) < w / 2 && v < 8.6)
            || RoundRect(u - 5.2, v - 9.6, 0.75, 1.5, 0.5) < 0 || RoundRect(u - 10.8, v - 9.6, 0.75, 1.5, 0.5) < 0,
        TrayGlyph.Keyboard => Math.Abs(RoundRect(u - 8, v - 8, 3.6, 2.4, 0.7)) < w / 2 || (Math.Abs(v - 9) < w / 2 && Math.Abs(u - 8) < 1.6),
        TrayGlyph.Controller => Math.Abs(RoundRect(u - 8, v - 8.3, 3.6, 2.0, 1.6)) < w / 2,
        _ => (u - 8) * (u - 8) + (v - 8) * (v - 8) < 2.2 * 2.2,
    };

    /// <summary>Signed distance to a rounded rectangle centered on 0 (negative inside).</summary>
    static double RoundRect(double x, double y, double halfW, double halfH, double radius)
    {
        double qx = Math.Abs(x) - halfW + radius, qy = Math.Abs(y) - halfH + radius;
        double ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
        return Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - radius;
    }

    static readonly (double X, double Y)[] BoltShape = { (13.2, 0.2), (10.6, 4.6), (12.4, 4.6), (11.2, 8.0), (15.4, 3.2), (13.4, 3.2), (15.0, 0.2) };

    static bool InBolt(double u, double v)
    {
        var inside = false;
        for (int i = 0, j = BoltShape.Length - 1; i < BoltShape.Length; j = i++)
        {
            var (xi, yi) = BoltShape[i];
            var (xj, yj) = BoltShape[j];
            if ((yi > v) != (yj > v) && u < (xj - xi) * (v - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }

    /// <summary>A Windows icon from <see cref="Render"/>'s pixels; the caller destroys it (DestroyIcon).</summary>
    public static IntPtr ToIcon(uint[] pixels, int size)
    {
        var header = new BitmapV5Header
        {
            Size = (uint)Marshal.SizeOf<BitmapV5Header>(), Width = size, Height = -size, Planes = 1, BitCount = 32, Compression = 3, // BI_BITFIELDS
            RedMask = 0x00FF0000, GreenMask = 0x0000FF00, BlueMask = 0x000000FF, AlphaMask = 0xFF000000,
        };
        var dc = GetDC(IntPtr.Zero);
        var color = CreateDIBSection(dc, ref header, 0, out var bits, IntPtr.Zero, 0);
        ReleaseDC(IntPtr.Zero, dc);
        if (color == IntPtr.Zero) return IntPtr.Zero;
        var mask = CreateBitmap(size, size, 1, 1, IntPtr.Zero);
        try
        {
            Marshal.Copy(MemoryMarshal.Cast<uint, int>(pixels).ToArray(), 0, bits, pixels.Length);
            var info = new IconInfo { IsIcon = 1, Mask = mask, Color = color };
            return CreateIconIndirect(ref info);
        }
        finally { DeleteObject(color); DeleteObject(mask); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BitmapV5Header
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant, RedMask, GreenMask, BlueMask, AlphaMask, CSType;
        public int EndpointsRedX, EndpointsRedY, EndpointsRedZ, EndpointsGreenX, EndpointsGreenY, EndpointsGreenZ, EndpointsBlueX, EndpointsBlueY, EndpointsBlueZ;
        public uint GammaRed, GammaGreen, GammaBlue, Intent, ProfileData, ProfileSize, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IconInfo { public int IsIcon, XHotspot, YHotspot; public IntPtr Mask, Color; } // blittable for the AOT engine

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapV5Header header, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bitCount, IntPtr bits);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] static extern IntPtr CreateIconIndirect(ref IconInfo info);
}
