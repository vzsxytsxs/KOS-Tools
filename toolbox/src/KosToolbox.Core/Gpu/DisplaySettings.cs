using System.Runtime.InteropServices;
using System.Text;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using Microsoft.Win32;

namespace KosToolbox.Core.Gpu;

/// <summary>Active monitor: GDI name (\\.\DISPLAYn), friendly name and the Windows target (adapter + id) for CCD.</summary>
/// <summary><see cref="Product"/>: the monitor's EDID product id from its device path (e.g. SAM727E), "" when unknown.</summary>
public sealed record DisplayTarget(string GdiName, string Name, uint AdapterLow, int AdapterHigh, uint TargetId, uint SourceId = 0, string Product = "")
{
    public override string ToString() => Name;
}

/// <summary>Per-channel adjustment in the NVIDIA panel style: brightness and contrast 0–100 (50 = neutral), gamma 0.30–2.80.</summary>
public sealed record ChannelAdjust(int Brightness, int Contrast, double Gamma)
{
    public static ChannelAdjust Neutral { get; } = new(50, 50, 1.0);
}

/// <summary>
/// NVIDIA control panel options for the monitor: output color, vibrance and hue (NVAPI); HDR and scaling (Windows,
/// QueryDisplayConfig/SetDisplayConfig); per-channel brightness/contrast/gamma (GDI gamma ramp); GPU performance counters
/// for all users (driver registry).
/// </summary>
public static class DisplaySettings
{
    // ---------- NVAPI ----------
    public static NvColor ReadColor(string gdiName) => Nvapi.ReadColor(gdiName);
    public static bool ColorSupported(string gdiName, NvColor color) => Nvapi.ColorSupported(gdiName, color);
    public static void SetColor(string gdiName, NvColor color) => Nvapi.SetColor(gdiName, color);
    public static (int Current, int Min, int Max, int Default) ReadVibrance(string gdiName) => Nvapi.ReadVibrance(gdiName);
    public static void SetVibrance(string gdiName, int level) => Nvapi.SetVibrance(gdiName, level);
    public static (int Current, int Default) ReadHue(string gdiName) => Nvapi.ReadHue(gdiName);
    public static void SetHue(string gdiName, int degrees) => Nvapi.SetHueAngle(gdiName, degrees);

    // ---------- Windows CCD ----------
    const uint QdcOnlyActivePaths = 2;
    const uint SdcApply = 0x80, SdcUseSuppliedConfig = 0x20, SdcSaveToDatabase = 0x200, SdcAllowChanges = 0x400;
    // DISPLAYCONFIG_PATH_INFO: source (20 bytes), target (48: adapter, id, mode, output, rotation, scaling @24...), flags.
    const int PathSize = 72, ModeSize = 64, ScalingOffset = 20 + 24, ErrorInsufficientBuffer = 122;
    const int GetSourceName = 1, GetTargetName = 2, GetAdvancedColor = 9, SetAdvancedColor = 10;
    /// <summary>DISPLAYCONFIG_SCALING: 1 = no GPU scaling (the monitor scales), 2 = centered, 3 = full screen, 4 = aspect ratio.</summary>
    public const uint ScalingIdentity = 1, ScalingCentered = 2, ScalingStretched = 3, ScalingAspect = 4, ScalingPreferred = 128;
    /// <summary>Integer scaling: the toolbox's own value, since DISPLAYCONFIG_SCALING has none (only NVAPI sets it).</summary>
    public const uint ScalingInteger = 0x1000;

    /// <summary>
    /// NV_SCALING for the control panel's two choices: who scales (GPU, or the display: "Balanced" in NVAPI) and the mode
    /// (<see cref="ScalingAspect"/>, <see cref="ScalingStretched"/>, <see cref="ScalingCentered"/> or <see cref="ScalingInteger"/>).
    /// </summary>
    public static int ToNvScaling(bool gpu, uint mode) => (gpu, mode) switch
    {
        (false, ScalingStretched) => 1, (true, ScalingStretched) => 2, (true, ScalingCentered) => 3, (true, ScalingAspect) => 5,
        (false, ScalingAspect) => 6, (false, ScalingCentered) => 7, (true, ScalingInteger) => 8,
        (false, ScalingInteger) => 6, // integer scaling needs the GPU; the display falls back to aspect ratio, as in the panel
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>The control panel's choices for an NV_SCALING; 0 (default) and unknown values read as the panel's default.</summary>
    public static (bool Gpu, uint Mode) FromNvScaling(int scaling) => scaling switch
    {
        1 => (false, ScalingStretched), 2 => (true, ScalingStretched), 3 => (true, ScalingCentered), 5 => (true, ScalingAspect),
        7 => (false, ScalingCentered), 8 => (true, ScalingInteger), _ => (false, ScalingAspect),
    };

    /// <summary>The NVIDIA scaling of the monitor, or null when NVAPI doesn't drive it (another GPU, no driver).</summary>
    public static int? ReadNvScaling(string gdiName)
    {
        try { return Nvapi.ReadScaling(gdiName); }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException) { return null; }
    }

    // ---------- "Override the scaling mode set by games and programs" ----------
    // The panel's box is bit 0x10 of the first payload byte of the driver's ScalingConfig (found by toggling it in the panel):
    // version 0x2DB, size 16, 4 payload bytes, then the sum of the 12 bytes before as a 32-bit checksum.
    internal const string DisplayDatabase = @"SYSTEM\CurrentControlSet\Services\nvlddmkm\State\DisplayDatabase";
    const byte OverrideBit = 0x10;

    static bool ValidScalingConfig(byte[]? v) =>
        v is { Length: 16 } && U(v, 0) == 0x2DB && U(v, 4) == 16 && U(v, 12) == (uint)v.Take(12).Sum(b => b);

    /// <summary>The box with its checksum rebuilt; null when the value isn't the known layout (nothing is written then).</summary>
    public static byte[]? WithScalingOverride(byte[]? config, bool on)
    {
        if (!ValidScalingConfig(config)) return null;
        var v = config!.ToArray();
        v[8] = (byte)(on ? v[8] | OverrideBit : v[8] & ~OverrideBit);
        BitConverter.GetBytes((uint)v.Take(12).Sum(b => b)).CopyTo(v, 12);
        return v;
    }

    /// <summary>The driver's database entries of a monitor model (one per connection it was seen on).</summary>
    // shortcut: matched by product id, so two monitors of the same model share the box; key them by serial if that matters.
    internal static IEnumerable<string> ScalingKeys(RegistryKey database, string product) =>
        database.GetSubKeyNames().Where(k => product.Length > 0 && k.StartsWith(product, StringComparison.OrdinalIgnoreCase));

    /// <summary>The box for this monitor: on when any of its entries has it (the panel only changes the current one); null when unknown.</summary>
    public static bool? ReadScalingOverride(string product)
    {
        try
        {
            using var db = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(DisplayDatabase);
            if (db is null) return null;
            var configs = ScalingKeys(db, product).Select(k => { using var key = db.OpenSubKey(k); return key?.GetValue("ScalingConfig") as byte[]; })
                .Where(ValidScalingConfig).ToList();
            return configs.Count == 0 ? null : configs.Any(c => (c![8] & OverrideBit) != 0);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    public static void SetNvScaling(string gdiName, int scaling)
    {
        Nvapi.SetScaling(gdiName, scaling);
        Log.Info($"Display {gdiName}: NVIDIA scaling = {scaling}");
    }

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint paths, IntPtr pathArray, ref uint modes, IntPtr modeArray, IntPtr topology);
    [DllImport("user32.dll")] static extern int SetDisplayConfig(uint paths, IntPtr pathArray, uint modes, IntPtr modeArray, uint flags);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(IntPtr packet);
    [DllImport("user32.dll")] static extern int DisplayConfigSetDeviceInfo(IntPtr packet);

    sealed record Config(byte[] Paths, byte[] Modes, uint PathCount, uint ModeCount);

    static Config Query()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Win32(GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var np, out var nm), "GetDisplayConfigBufferSizes");
            var paths = Marshal.AllocHGlobal((int)Math.Max(1, np) * PathSize);
            var modes = Marshal.AllocHGlobal((int)Math.Max(1, nm) * ModeSize);
            try
            {
                var code = QueryDisplayConfig(QdcOnlyActivePaths, ref np, paths, ref nm, modes, IntPtr.Zero);
                if (code == ErrorInsufficientBuffer) continue; // the configuration changed between the two calls
                Win32(code, "QueryDisplayConfig");
                var p = new byte[np * PathSize];
                var m = new byte[nm * ModeSize];
                Marshal.Copy(paths, p, 0, p.Length);
                Marshal.Copy(modes, m, 0, m.Length);
                return new Config(p, m, np, nm);
            }
            finally
            {
                Marshal.FreeHGlobal(paths);
                Marshal.FreeHGlobal(modes);
            }
        }
        throw new InvalidOperationException("QueryDisplayConfig kept changing.");
    }

    static void Win32(int code, string action)
    {
        if (code != 0) throw new InvalidOperationException($"{action} failed (error {code}).");
    }

    static int I(byte[] d, int at) => BitConverter.ToInt32(d, at);
    static uint U(byte[] d, int at) => BitConverter.ToUInt32(d, at);

    /// <summary>DISPLAYCONFIG_DEVICE_INFO_HEADER packet (type, size, adapter, id) followed by the body.</summary>
    static byte[] Packet(int type, int size, uint adapterLow, int adapterHigh, uint id)
    {
        var data = new byte[size];
        BitConverter.GetBytes(type).CopyTo(data, 0);
        BitConverter.GetBytes(size).CopyTo(data, 4);
        BitConverter.GetBytes(adapterLow).CopyTo(data, 8);
        BitConverter.GetBytes(adapterHigh).CopyTo(data, 12);
        BitConverter.GetBytes(id).CopyTo(data, 16);
        return data;
    }

    static int DeviceInfo(byte[] packet, bool set)
    {
        var p = Marshal.AllocHGlobal(packet.Length);
        try
        {
            Marshal.Copy(packet, 0, p, packet.Length);
            var code = set ? DisplayConfigSetDeviceInfo(p) : DisplayConfigGetDeviceInfo(p);
            Marshal.Copy(p, packet, 0, packet.Length);
            return code;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    static string Wide(byte[] d, int at, int chars)
    {
        var s = Encoding.Unicode.GetString(d, at, chars * 2);
        var end = s.IndexOf('\0');
        return end >= 0 ? s[..end] : s;
    }

    /// <summary>Active monitors, in Windows order.</summary>
    public static List<DisplayTarget> Targets()
    {
        var c = Query();
        var list = new List<DisplayTarget>();
        for (var i = 0; i < c.PathCount; i++)
        {
            var at = i * PathSize;
            var source = Packet(GetSourceName, 84, U(c.Paths, at), I(c.Paths, at + 4), U(c.Paths, at + 8));
            if (DeviceInfo(source, false) != 0) continue;
            var gdi = Wide(source, 20, 32);
            var target = Packet(GetTargetName, 420, U(c.Paths, at + 20), I(c.Paths, at + 24), U(c.Paths, at + 28));
            var named = DeviceInfo(target, false) == 0;
            var name = named ? Wide(target, 36, 64) : "";
            // edidManufactureId keeps the EDID's byte order (bytes 8-9: three 5-bit letters); edidProductCodeId is the product code.
            var mfg = target[28] << 8 | target[29];
            var product = named && mfg != 0
                ? $"{(char)('A' - 1 + (mfg >> 10 & 31))}{(char)('A' - 1 + (mfg >> 5 & 31))}{(char)('A' - 1 + (mfg & 31))}{BitConverter.ToUInt16(target, 30):X4}" : "";
            if (name.Length == 0) name = gdi.Replace(@"\\.\", "");
            if (list.Any(t => t.GdiName == gdi)) continue; // clone: one target per source is enough
            list.Add(new DisplayTarget(gdi, name, U(c.Paths, at + 20), I(c.Paths, at + 24), U(c.Paths, at + 28), U(c.Paths, at + 8), product));
        }
        return list;
    }

    static int PathOf(Config c, DisplayTarget t)
    {
        for (var i = 0; i < c.PathCount; i++)
        {
            var at = i * PathSize;
            if (U(c.Paths, at + 20) == t.AdapterLow && I(c.Paths, at + 24) == t.AdapterHigh && U(c.Paths, at + 28) == t.TargetId) return at;
        }
        throw new InvalidOperationException($"{t.Name} is no longer active.");
    }

    public static uint ReadScaling(DisplayTarget t)
    {
        var c = Query();
        return U(c.Paths, PathOf(c, t) + ScalingOffset);
    }

    /// <summary>Writes the scaling to the monitor's path and saves it in the Windows configuration database.</summary>
    public static void SetScaling(DisplayTarget t, uint scaling)
    {
        var c = Query();
        BitConverter.GetBytes(scaling).CopyTo(c.Paths, PathOf(c, t) + ScalingOffset);
        var paths = Marshal.AllocHGlobal(c.Paths.Length);
        var modes = Marshal.AllocHGlobal(Math.Max(1, c.Modes.Length));
        try
        {
            Marshal.Copy(c.Paths, 0, paths, c.Paths.Length);
            Marshal.Copy(c.Modes, 0, modes, c.Modes.Length);
            Win32(SetDisplayConfig(c.PathCount, paths, c.ModeCount, modes, SdcApply | SdcUseSuppliedConfig | SdcSaveToDatabase | SdcAllowChanges), "SetDisplayConfig");
        }
        finally
        {
            Marshal.FreeHGlobal(paths);
            Marshal.FreeHGlobal(modes);
        }
        Log.Info($"Display {t.Name}: scaling = {scaling}");
    }

    // Per-monitor scaling (DPI): undocumented DisplayConfig packets (the same ones Windows Settings uses),
    // in steps relative to the recommended value.
    const int GetDpiScale = -3, SetDpiScale = -4;
    public static readonly int[] DpiValues = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };

    /// <summary>Current scaling, the recommended one and the allowed ones (in %), or null if Windows does not report it.</summary>
    public static (int Current, int Recommended, IReadOnlyList<int> Options)? ReadScale(DisplayTarget t)
    {
        var p = Packet(GetDpiScale, 32, t.AdapterLow, t.AdapterHigh, t.SourceId);
        if (DeviceInfo(p, false) != 0) return null;
        int min = I(p, 20), cur = I(p, 24), max = I(p, 28);
        var rec = -min;
        if (rec < 0 || rec >= DpiValues.Length) return null;
        var options = Enumerable.Range(rec + min, max - min + 1).Where(i => i >= 0 && i < DpiValues.Length).Select(i => DpiValues[i]).ToList();
        return (DpiValues[Math.Clamp(rec + cur, 0, DpiValues.Length - 1)], DpiValues[rec], options);
    }

    public static void SetScale(DisplayTarget t, int percent)
    {
        var scale = ReadScale(t) ?? throw new NotSupportedException("Windows didn't report the display scale.");
        var index = Array.IndexOf(DpiValues, percent);
        if (index < 0 || !scale.Options.Contains(percent)) throw new ArgumentOutOfRangeException(nameof(percent));
        var p = Packet(SetDpiScale, 24, t.AdapterLow, t.AdapterHigh, t.SourceId);
        BitConverter.GetBytes(index - Array.IndexOf(DpiValues, scale.Recommended)).CopyTo(p, 20);
        Win32(DeviceInfo(p, true), "DisplayConfigSetDeviceInfo (scale)");
        Log.Info($"Display {t.Name}: scale = {percent}%");
    }

    public static (bool Supported, bool Enabled) ReadHdr(DisplayTarget t)
    {
        var p = Packet(GetAdvancedColor, 32, t.AdapterLow, t.AdapterHigh, t.TargetId);
        if (DeviceInfo(p, false) != 0) return (false, false);
        var v = U(p, 20);
        return ((v & 1) != 0, (v & 2) != 0);
    }

    public static void SetHdr(DisplayTarget t, bool on)
    {
        var p = Packet(SetAdvancedColor, 24, t.AdapterLow, t.AdapterHigh, t.TargetId);
        BitConverter.GetBytes(on ? 1u : 0u).CopyTo(p, 20);
        Win32(DeviceInfo(p, true), "DisplayConfigSetDeviceInfo (HDR)");
        Log.Info($"Display {t.Name}: HDR = {on}");
    }

    // ---------- brightness / contrast / gamma ----------
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateDC(string driver, string? device, string? output, IntPtr init);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool SetDeviceGammaRamp(IntPtr dc, ushort[] ramp);

    /// <summary>Ramp of 256 values (0–65535) for a channel: gamma, then contrast around the midpoint, then brightness.</summary>
    public static ushort[] BuildRamp(ChannelAdjust a)
    {
        var ramp = new ushort[256];
        var gamma = Math.Clamp(a.Gamma, 0.3, 2.8);
        var contrast = 0.5 + Math.Clamp(a.Contrast, 0, 100) / 100.0;   // 50 → 1.0
        var brightness = (Math.Clamp(a.Brightness, 0, 100) - 50) / 100.0; // 50 → 0
        for (var i = 0; i < 256; i++)
        {
            var v = Math.Pow(i / 255.0, 1 / gamma);
            v = (v - 0.5) * contrast + 0.5 + brightness;
            ramp[i] = (ushort)Math.Round(Math.Clamp(v, 0, 1) * 65535);
        }
        return ramp;
    }

    /// <summary>Applies the ramp to the monitor. Windows refuses ramps too far from neutral (GDI limit).</summary>
    public static void SetGamma(string gdiName, ChannelAdjust red, ChannelAdjust green, ChannelAdjust blue)
    {
        var ramp = BuildRamp(red).Concat(BuildRamp(green)).Concat(BuildRamp(blue)).ToArray();
        var dc = CreateDC(gdiName, null, null, IntPtr.Zero);
        if (dc == IntPtr.Zero) throw new InvalidOperationException($"Couldn't open {gdiName}.");
        try
        {
            if (!SetDeviceGammaRamp(dc, ramp))
                throw new InvalidOperationException("Windows refused this brightness/contrast/gamma (it limits how far the gamma ramp can go). Move the slider back toward the middle.");
        }
        finally { DeleteDC(dc); }
    }

    // ---------- performance counters ----------
    const string NvTweak = @"SYSTEM\CurrentControlSet\Services\nvlddmkm\Global\NVTweak";

    /// <summary>GPU performance counters made available to all users (RmProfilingAdminOnly = 0). Without the value, administrators only.</summary>
    public static bool CountersForAllUsers()
    {
        using var key = Registry.LocalMachine.OpenSubKey(NvTweak);
        return key?.GetValue("RmProfilingAdminOnly") is int v && v == 0;
    }

    public static void SetCountersForAllUsers(bool on)
    {
        if (!Elevation.IsAdmin) throw new InvalidOperationException("Changing this needs the toolbox running as administrator.");
        using var key = Registry.LocalMachine.CreateSubKey(NvTweak, true);
        key.SetValue("RmProfilingAdminOnly", on ? 0 : 1, RegistryValueKind.DWord);
        Log.Info($"GPU performance counters for all users = {on}");
    }
}
