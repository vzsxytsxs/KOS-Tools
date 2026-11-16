using System.Runtime.InteropServices;
using KosToolbox.Core.Persistence;

namespace KosToolbox.Core.Gpu;

/// <summary>Monitor on the Windows desktop: position, current mode and orientation (0 = landscape, 1 = portrait, 2/3 = flipped).</summary>
public sealed record WinDisplay(string GdiName, string Name, int Number, bool Primary, int X, int Y, int Width, int Height, int Hz, int Orientation);

/// <summary>A video mode the driver offers for the monitor (32 bits per pixel).</summary>
public sealed record WinMode(int Width, int Height, int Hz);

/// <summary>
/// Windows display settings (the ones in Settings › Display): arrangement, primary monitor, resolution, rotation and refresh
/// rate, through EnumDisplaySettings/ChangeDisplaySettingsEx.
/// </summary>
public static class WindowsDisplays
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion, DriverVersion, Size, DriverExtra;
        public int Fields;
        public int PositionX, PositionY, DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public int IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DisplayDevice
    {
        public int Cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    const int EnumCurrent = -1;
    const int DmPosition = 0x20, DmOrientation = 0x80, DmBitsPerPel = 0x40000, DmWidth = 0x80000, DmHeight = 0x100000, DmFrequency = 0x400000;
    const int CdsUpdateRegistry = 0x01, CdsSetPrimary = 0x10, CdsNoReset = 0x10000000;
    const int AttachedToDesktop = 0x1, PrimaryDevice = 0x4;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice dd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettingsEx(string device, int mode, ref DevMode dm, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string device, ref DevMode dm, IntPtr hwnd, int flags, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string? device, IntPtr dm, IntPtr hwnd, int flags, IntPtr param);

    static DevMode NewMode() => new() { Size = (short)Marshal.SizeOf<DevMode>(), DeviceName = "", FormName = "" };

    static DevMode Current(string gdi)
    {
        var dm = NewMode();
        if (!EnumDisplaySettingsEx(gdi, EnumCurrent, ref dm, 0)) throw new InvalidOperationException($"Couldn't read the current mode of {gdi}.");
        return dm;
    }

    /// <summary>Monitors attached to the desktop, with the CCD friendly name when available.</summary>
    public static List<WinDisplay> List()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try { foreach (var t in DisplaySettings.Targets()) names[t.GdiName] = t.Name; } catch { /* no CCD: use the adapter name */ }
        var list = new List<WinDisplay>();
        var dd = new DisplayDevice { Cb = Marshal.SizeOf<DisplayDevice>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++, dd = new DisplayDevice { Cb = Marshal.SizeOf<DisplayDevice>() })
        {
            if ((dd.StateFlags & AttachedToDesktop) == 0) continue;
            var dm = NewMode();
            if (!EnumDisplaySettingsEx(dd.DeviceName, EnumCurrent, ref dm, 0)) continue;
            var number = int.TryParse(new string(dd.DeviceName.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray()), out var n) ? n : list.Count + 1;
            list.Add(new WinDisplay(dd.DeviceName, names.GetValueOrDefault(dd.DeviceName, dd.DeviceString), number, (dd.StateFlags & PrimaryDevice) != 0,
                dm.PositionX, dm.PositionY, dm.PelsWidth, dm.PelsHeight, dm.DisplayFrequency, dm.DisplayOrientation));
        }
        return list;
    }

    /// <summary>32-bit modes of the monitor, without repeats, from largest to smallest (in landscape orientation).</summary>
    public static List<WinMode> Modes(string gdi)
    {
        var set = new HashSet<WinMode>();
        var dm = NewMode();
        for (var i = 0; EnumDisplaySettingsEx(gdi, i, ref dm, 0); i++, dm = NewMode())
            if (dm.BitsPerPel == 32 && dm.DisplayFrequency > 1) set.Add(new WinMode(dm.PelsWidth, dm.PelsHeight, dm.DisplayFrequency));
        return set.OrderByDescending(m => m.Width * m.Height).ThenByDescending(m => m.Width).ThenByDescending(m => m.Hz).ToList();
    }

    /// <summary>ChangeDisplaySettingsEx code as text (0 = ok).</summary>
    public static string Describe(int code) => code switch
    {
        1 => "Windows needs a restart for this change.",
        -1 => "The display driver failed to apply this mode.",
        -2 => "This mode isn't supported by the display.",
        -3 => "Couldn't save the display settings.",
        -4 => "Invalid display settings flags.",
        -5 => "Invalid display settings (code -5).",
        -6 => "Windows refused these display settings (code -6).",
        -8 => "Windows refused these display settings (code -8).",
        _ => $"Display settings failed (code {code}).",
    };

    static void Check(int code)
    {
        if (code != 0) throw new InvalidOperationException(Describe(code));
    }

    const int CdsFullscreen = 0x04;

    /// <summary>
    /// Temporary mode (CDS_FULLSCREEN, not saved): KOS Governor's screen mode per program. <paramref name="hz"/> null keeps the current rate.
    /// <see cref="ResetTemporaryMode"/> goes back to the saved mode.
    /// </summary>
    public static void SetTemporaryMode(string gdi, int width, int height, int? hz)
    {
        var dm = Current(gdi);
        dm.PelsWidth = width;
        dm.PelsHeight = height;
        dm.Fields = DmWidth | DmHeight;
        if (hz is { } rate) { dm.DisplayFrequency = rate; dm.Fields |= DmFrequency; }
        Check(ChangeDisplaySettingsEx(gdi, ref dm, IntPtr.Zero, CdsFullscreen, IntPtr.Zero));
    }

    /// <summary>Back to the mode saved in the registry (no DEVMODE = the saved settings).</summary>
    public static void ResetTemporaryMode(string gdi) => Check(ChangeDisplaySettingsEx(gdi, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero));

    /// <summary>Changes the monitor's resolution, frequency and orientation (width/height already in the requested orientation).</summary>
    public static void SetMode(string gdi, int width, int height, int hz, int orientation)
    {
        var dm = Current(gdi);
        dm.PelsWidth = width;
        dm.PelsHeight = height;
        dm.DisplayFrequency = hz;
        dm.DisplayOrientation = orientation;
        dm.BitsPerPel = 32;
        dm.Fields = DmWidth | DmHeight | DmFrequency | DmOrientation | DmBitsPerPel;
        Check(ChangeDisplaySettingsEx(gdi, ref dm, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero));
        Log.Info($"Display {gdi}: {width}x{height} @ {hz} Hz, orientation {orientation}");
    }

    /// <summary>
    /// New arrangement (position of each monitor). The primary stays at (0, 0): positions are shifted for that.
    /// Writes everything without applying and applies at once at the end, as Windows does.
    /// </summary>
    public static void SetLayout(IReadOnlyDictionary<string, (int X, int Y)> positions, string? primary = null)
    {
        var displays = List();
        primary ??= displays.FirstOrDefault(d => d.Primary)?.GdiName ?? throw new InvalidOperationException("No primary display.");
        var origin = positions.TryGetValue(primary, out var p) ? p : (0, 0);
        foreach (var d in displays)
        {
            var (x, y) = positions.TryGetValue(d.GdiName, out var pos) ? pos : (d.X, d.Y);
            var dm = Current(d.GdiName);
            dm.PositionX = x - origin.Item1;
            dm.PositionY = y - origin.Item2;
            dm.Fields = DmPosition;
            var flags = CdsUpdateRegistry | CdsNoReset | (d.GdiName == primary ? CdsSetPrimary : 0);
            Check(ChangeDisplaySettingsEx(d.GdiName, ref dm, IntPtr.Zero, flags, IntPtr.Zero));
        }
        Check(ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero));
        Log.Info($"Display layout applied (primary {primary}).");
    }
}
