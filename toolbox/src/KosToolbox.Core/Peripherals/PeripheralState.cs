using KosToolbox.Core.Peripherals.Hid;

namespace KosToolbox.Core.Peripherals;

/// <summary>How the device reaches the PC: its own cable, a vendor receiver (2.4 GHz) or Bluetooth.</summary>
public enum PeripheralConnection { Wired, Receiver, Bluetooth }

/// <summary>
/// A battery reading. <paramref name="Percent"/> null with <paramref name="Level"/> set: the device reports a coarse level only.
/// <paramref name="Asleep"/>: the receiver answered but the device didn't (asleep, off or out of range); the values are unknown.
/// </summary>
public sealed record PeripheralBattery(int? Percent, bool? Charging, bool Asleep = false, string? Level = null);

public sealed record DpiStage(int X, int Y);

/// <summary>Current DPI and, when the device stores them, its stages (<paramref name="ActiveStage"/> is 1-based).</summary>
public sealed record PeripheralDpi(int X, int Y, int Min, int Max, int Step, IReadOnlyList<DpiStage>? Stages = null, int? ActiveStage = null);

public sealed record PeripheralPolling(int Hz, IReadOnlyList<int> Supported);

/// <summary>A lighting effect; <see cref="Other"/> is one the device has that the toolbox doesn't offer (it can't be written back).</summary>
public enum LightingEffect { Off, Static, Breathing, Spectrum, Other }

/// <summary>The effect of one light and its color (0xRRGGBB), null when the effect has none.</summary>
public sealed record PeripheralLighting(LightingEffect Effect, int? Color);

/// <summary>
/// Magnetic-switch travel applied to every key, in mm: actuation, rapid trigger on/off with its press and release steps, the bottom
/// dead zone. Read: the value most keys have; <paramref name="Uniform"/> false when keys differ (Apply sets them all).
/// <paramref name="StepMm"/> is the board's travel unit.
/// </summary>
public sealed record PeripheralMagnetic(double ActuationMm, bool RapidTrigger, double RtPressMm, double RtReleaseMm, double BottomDeadZoneMm,
    bool Uniform = true, double StepMm = 0.01)
{
    public const double ActuationMinMm = 0.1, ActuationMaxMm = 3.3, RtMaxMm = 2.5, DeadZoneMaxMm = 1.0;
}

/// <summary>
/// What a driver read from the device. Every part is nullable: null means the device doesn't have it or it couldn't be read, and the
/// page hides it. A field exists only because a driver fills it.
/// </summary>
public sealed record PeripheralState
{
    /// <summary>The device's own name when it reports one (HID++ name feature); else the catalog's model name.</summary>
    public string? Name { get; init; }
    public string? Firmware { get; init; }
    public string? Serial { get; init; }
    public PeripheralBattery? Battery { get; init; }
    public PeripheralDpi? Dpi { get; init; }
    public PeripheralPolling? Polling { get; init; }
    public int? SleepSeconds { get; init; }
    /// <summary>The battery level under which the device dims or warns, in percent.</summary>
    public int? LowBatteryPercent { get; init; }
    /// <summary>Lighting brightness in percent (all zones).</summary>
    public int? Brightness { get; init; }
    /// <summary>The stored lighting effect and color.</summary>
    public PeripheralLighting? Lighting { get; init; }
    /// <summary>Logitech: true while the onboard profile drives the mouse (its live settings then come from the profile).</summary>
    public bool? OnboardMode { get; init; }
    /// <summary>A keyboard protocol and its version (VIA 12).</summary>
    public string? Protocol { get; init; }
    public int? Layers { get; init; }
    /// <summary>The active onboard profile, 1-based, of <see cref="Profiles"/>.</summary>
    public int? Profile { get; init; }
    public int? Profiles { get; init; }
    /// <summary>Magnetic-switch travel of the active profile, applied to every key.</summary>
    public PeripheralMagnetic? Magnetic { get; init; }
}

/// <summary>A setting a driver can write; the page offers only these.</summary>
public enum PeripheralSetting { Dpi, ActiveStage, PollingRate, Sleep, LowBattery, Brightness, HostMode, Lighting, Profile, Magnetic }

/// <summary>The device answered, but not as expected (status, timeout, a reply to something else). Shown as an error toast.</summary>
public class PeripheralException(string message) : Exception(message);

/// <summary>The receiver answered for a wireless device that didn't (asleep, off or out of range).</summary>
public sealed class PeripheralAsleepException() : PeripheralException("The device didn't answer: it may be asleep, off or out of range.");

/// <summary>A write that the read-back doesn't confirm; <see cref="State"/> holds what the device has now.</summary>
public sealed class ReadBackException(string message, PeripheralState state) : PeripheralException(message)
{
    public PeripheralState State { get; } = state;
}

/// <summary>
/// Talks to one physical device through <see cref="IHidTransport"/> only. Getters may retry; a setter is sent once, read back, and
/// reported as the read-back shows it. Every value is checked against the catalog before anything is sent. Not thread-safe: one
/// caller at a time (the page and the engine each keep their own).
/// </summary>
public abstract class PeripheralDriver
{
    /// <summary>Opens a collection (read/write = overlapped output and input reports; otherwise feature reports only). Tests swap it.</summary>
    public static Func<HidCollection, bool, IHidTransport> Opener { get; set; } =
        (c, readWrite) => readWrite ? HidHandle.OpenReadWrite(c) : HidHandle.OpenFeatureOnly(c);
    /// <summary>Waits between a request and its reply; tests make it instant.</summary>
    public static Action<int> Delay { get; set; } = Thread.Sleep;

    protected PeripheralDriver(HidDevice device, string family, string? model, bool tested)
    {
        Device = device;
        Family = family;
        Model = model;
        Tested = tested;
    }

    public HidDevice Device { get; }
    /// <summary>The protocol family (Razer, Logitech HID++…), shown in Info.</summary>
    public string Family { get; }
    /// <summary>The catalog's model name; null when the device names itself.</summary>
    public string? Model { get; }
    /// <summary>Checked on hardware (by us or upstream for this exact PID and connection); false shows "Untested model".</summary>
    public bool Tested { get; }
    /// <summary>Only reads the battery (headsets, controllers, generic HID): no settings, and the page shows Power and Info only.</summary>
    public virtual bool BatteryOnly => false;
    public virtual PeripheralKind Kind => Device.Kind;
    public virtual PeripheralConnection? Connection => Device.Bluetooth ? PeripheralConnection.Bluetooth : null;
    public virtual IReadOnlySet<PeripheralSetting> Writable => new HashSet<PeripheralSetting>();
    /// <summary>Every write goes to the device's flash (no live-only setting): gamemode writes it on each start and stop, and the page says so.</summary>
    public virtual bool StoredOnly => false;

    /// <summary>Everything the driver knows how to read. Getters only.</summary>
    public abstract PeripheralState Read();

    /// <summary>Just the battery (the 60 s refresh and KOS Engine). Getters only.</summary>
    public virtual PeripheralBattery? ReadBattery() => Read().Battery;

    public virtual PeripheralState SetDpi(int x, int y) => throw Unsupported();
    /// <summary>The DPI in volatile memory only, never the stored value (gamemode); unsupported where the device can't tell them apart.</summary>
    public virtual PeripheralState SetDpiLive(int x, int y) => throw Unsupported();
    /// <summary>Whether <see cref="SetDpiLive"/> works on this device.</summary>
    public virtual bool HasLiveDpi => false;
    public virtual PeripheralState SetActiveStage(int stage) => throw Unsupported();
    public virtual PeripheralState SetPollingRate(int hz) => throw Unsupported();
    public virtual PeripheralState SetSleep(int seconds) => throw Unsupported();
    public virtual PeripheralState SetLowBattery(int percent) => throw Unsupported();
    public virtual PeripheralState SetBrightness(int percent) => throw Unsupported();
    /// <summary>Whether the live brightness can be read and set on its own (gamemode turns the lights off live only).</summary>
    public virtual bool HasLiveBrightness => false;
    /// <summary>The brightness the light has now, in percent; null when unreadable. Getters only.</summary>
    public virtual int? ReadLiveBrightness() => null;
    /// <summary>The live brightness only, nothing stored; returns what it reads back.</summary>
    public virtual int SetBrightnessLive(int percent) => throw Unsupported();
    /// <summary>The effects <see cref="SetLighting"/> takes on this device.</summary>
    public virtual IReadOnlyList<LightingEffect> Effects => new[] { LightingEffect.Off, LightingEffect.Static, LightingEffect.Breathing, LightingEffect.Spectrum };
    /// <summary>The effect and color (0xRRGGBB; ignored by Off and Spectrum). Off, Static, Breathing and Spectrum only.</summary>
    public virtual PeripheralState SetLighting(LightingEffect effect, int color) => throw Unsupported();
    public virtual PeripheralState SetHostMode() => throw Unsupported();
    /// <summary>Switches the active onboard profile (1-based); everything per profile is read again.</summary>
    public virtual PeripheralState SetProfile(int profile) => throw Unsupported();
    /// <summary>The same travel on every key of the active profile (<see cref="PeripheralMagnetic.Uniform"/> and the step are ignored).</summary>
    public virtual PeripheralState SetMagnetic(PeripheralMagnetic travel) => throw Unsupported();

    protected static NotSupportedException Unsupported() => new("This device doesn't offer that setting.");

    /// <summary>Opens <paramref name="collection"/> through <see cref="Opener"/>, runs <paramref name="work"/> and closes it.</summary>
    protected static T With<T>(HidCollection collection, bool readWrite, Func<IHidTransport, T> work)
    {
        var transport = Opener(collection, readWrite);
        try { return work(transport); }
        finally { (transport as IDisposable)?.Dispose(); }
    }

    protected static void Require(bool ok, string problem)
    {
        if (!ok) throw new ArgumentOutOfRangeException(null, problem);
    }
}
