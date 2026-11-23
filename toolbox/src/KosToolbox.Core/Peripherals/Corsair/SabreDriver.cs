using KosToolbox.Core.Peripherals.Hid;
using KosToolbox.Core.Peripherals.Odm;

namespace KosToolbox.Core.Peripherals.Corsair;

/// <summary>A SABRE v2 PRO product: exact PID and whether it is the 2.4 GHz receiver (the mouse is then asleep unless it answers).</summary>
public sealed record SabreModel(ushort ProductId, string Name, bool Receiver);

/// <summary>The SABRE settings that have no shared page row.</summary>
public sealed record SabreExtras(SabreLift? LiftOff, bool LiftOffLocked, bool? MotionSync, bool? AngleSnap, bool? Ripple,
    IReadOnlyList<SabreAction?>? Buttons, IReadOnlyList<int>? StageColors);

/// <summary>
/// The Corsair SABRE v2 PRO ULTRALIGHT WIRELESS (<c>1B1C:2B28</c> and its receiver <c>2B2A</c>, the CF and MG variants) over the report-8
/// collection (usage page FF02, 17-byte reports). Reads send the commands the Web Hub sends; command 03 (the hub's "mouse present"
/// check) and every other command stay unsent. A write reads the field first (it must validate: bytes adding up to 0x55), sends
/// <c>07</c> with the new bytes and their checksum in one packet, then reads the field back with <c>08</c>; what is reported is that
/// read-back. Memory is the mouse's own (stored): every write is permanent, so gamemode writes flash. Reads match the friend's
/// iCUE screens; no write has reached a real mouse yet, hence untested.
/// </summary>
public sealed class SabreDriver : PeripheralDriver
{
    public static readonly IReadOnlyList<SabreModel> Models = new SabreModel[]
    {
        new(0x2B28, "SABRE v2 PRO ULTRALIGHT WIRELESS", false), new(0x2B2A, "SABRE v2 PRO ULTRALIGHT WIRELESS", true),
        new(0x2B29, "SABRE v2 PRO ULTRALIGHT WIRELESS (MG)", false), new(0x2B34, "SABRE v2 PRO ULTRALIGHT WIRELESS (MG)", true),
        new(0x2B25, "SABRE v2 PRO ULTRALIGHT WIRELESS (CF)", false), new(0x2B35, "SABRE v2 PRO ULTRALIGHT WIRELESS (CF)", true),
    };

    const int ReplyTimeoutMs = 300;

    readonly SabreModel _model;
    readonly HidCollection _collection;
    SabreSettings? _settings;

    SabreDriver(HidDevice device, SabreModel model, HidCollection collection) : base(device, "Corsair", model.Name, tested: false)
    {
        _model = model;
        _collection = collection;
    }

    public static SabreDriver? Claim(HidDevice d) =>
        d.VendorId == 0x1B1C && Models.FirstOrDefault(m => m.ProductId == d.ProductId) is { } m
        && d.Collections.FirstOrDefault(c => c.UsagePage == 0xFF02 && c.Usage == 0x0002 && c.InputLength == 17 && c.OutputLength == 17) is { } c
            ? new SabreDriver(d, m, c) : null;

    public override PeripheralKind Kind => PeripheralKind.Mouse;
    public override PeripheralConnection? Connection => _model.Receiver ? PeripheralConnection.Receiver : PeripheralConnection.Wired;
    public override bool StoredOnly => true;

    /// <summary>A setting is writable once its field was read with a valid checksum.</summary>
    public override IReadOnlySet<PeripheralSetting> Writable
    {
        get
        {
            var set = new HashSet<PeripheralSetting>();
            if (_settings is not { } s) return set;
            if (s.StageDpi is not null && s.ActiveStage is not null) { set.Add(PeripheralSetting.Dpi); set.Add(PeripheralSetting.ActiveStage); }
            if (s.PollingHz is not null) set.Add(PeripheralSetting.PollingRate);
            if (s.SleepSeconds is not null) set.Add(PeripheralSetting.Sleep);
            return set;
        }
    }

    /// <summary>The settings without a shared row, as last read; null before a read or when the mouse didn't answer.</summary>
    public SabreExtras? Extras => _settings is { } s
        ? new SabreExtras(s.LiftOff, s.SurfaceCalibration != false, s.MotionSync, s.AngleSnap, s.Ripple, s.Buttons, s.StageColors) : null;

    public override PeripheralState Read() => With(_collection, true, t => State(Snapshot(t)));

    public override PeripheralBattery? ReadBattery() => With(_collection, true, Battery);

    // ---------- reads ----------

    /// <summary>One request and its reply: the same command echoed, checksum right. Sent twice at most; null when the mouse stays silent.</summary>
    static byte[]? Exchange(IHidTransport t, byte[] payload, int attempts = 2)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            t.FlushQueue();
            try { t.Write(Report8Codec.ReportId, payload); }
            catch (TimeoutException) { continue; }
            var end = Environment.TickCount64 + ReplyTimeoutMs;
            while (Environment.TickCount64 < end)
            {
                if (t.Read((int)Math.Max(1, end - Environment.TickCount64)) is not { } r) break;
                if (Report8Codec.Answers(r, payload[0])) return r;
            }
        }
        return null;
    }

    static byte[]? ReadBytes(IHidTransport t, int address, int length) =>
        Exchange(t, SabreCodec.ReadRequest(address, length)) is { } r ? SabreCodec.MemoryData(r, SabreCodec.ReadMemory, address, length) : null;

    static PeripheralBattery? Battery(IHidTransport t) =>
        Exchange(t, SabreCodec.Request(SabreCodec.Battery)) is { } r ? SabreCodec.DecodeBattery(r) : null;

    /// <summary>The memory image 00..E5 (every field the driver uses); null when a chunk isn't answered.</summary>
    static byte[]? ReadImage(IHidTransport t)
    {
        var image = new byte[SabreCodec.ImageLength];
        for (var at = 0; at < image.Length; at += SabreCodec.ChunkLength)
        {
            var length = Math.Min(SabreCodec.ChunkLength, image.Length - at);
            if (ReadBytes(t, at, length) is not { } chunk) return null;
            chunk.CopyTo(image, at);
        }
        return image;
    }

    sealed record Reading(PeripheralBattery? Battery, string? Firmware, string? ReceiverFirmware);

    Reading Snapshot(IHidTransport t)
    {
        var battery = Battery(t);
        var fw = Exchange(t, SabreCodec.Request(SabreCodec.MouseFirmware)) is { } f ? SabreCodec.DecodeFirmware(f, SabreCodec.MouseFirmware) : null;
        var rx = _model.Receiver && Exchange(t, SabreCodec.Request(SabreCodec.ReceiverFirmware)) is { } g ? SabreCodec.DecodeFirmware(g, SabreCodec.ReceiverFirmware) : null;
        var image = ReadImage(t);
        _settings = image is null ? null : SabreCodec.Decode(image);
        return new Reading(battery, fw, rx);
    }

    PeripheralState State(Reading s)
    {
        var battery = s.Battery ?? new PeripheralBattery(null, null, Asleep: true);
        if (_settings is not { } x) return new PeripheralState { Name = _model.Name, Battery = battery };
        return new PeripheralState
        {
            Name = _model.Name,
            Firmware = s.Firmware is null ? null : s.ReceiverFirmware is null ? s.Firmware : $"{s.Firmware} (receiver {s.ReceiverFirmware})",
            Battery = battery,
            Dpi = x.StageDpi is { } d && x.ActiveStage is { } a
                ? new PeripheralDpi(d[a], d[a], SabreCodec.DpiMin, SabreCodec.DpiMax, SabreCodec.DpiStep, d.Select(v => new DpiStage(v, v)).ToList(), a + 1)
                : null,
            Polling = x.PollingHz is { } hz ? new PeripheralPolling(hz, SabreCodec.Rates) : null,
            SleepSeconds = x.SleepSeconds,
        };
    }

    // ---------- writes ----------

    public override PeripheralState SetDpi(int x, int y)
    {
        Require(x == y, "This mouse has one DPI for both axes.");
        Require(SabreCodec.ValidDpi(x), $"DPI goes from {SabreCodec.DpiMin} to {SabreCodec.DpiMax} in steps of {SabreCodec.DpiStep}.");
        return Write(s => (SabreCodec.StagesAt + 4 * (s.ActiveStage ?? throw Unread("active DPI stage")), SabreCodec.EncodeStage(x)));
    }

    public override PeripheralState SetActiveStage(int stage)
    {
        var count = _settings?.StageCount ?? 0;
        Require(stage >= 1 && stage <= count, $"Choose a stage from 1 to {count}.");
        return Write(_ => (SabreCodec.ActiveStageAt, SabreCodec.Pair(stage - 1)));
    }

    public override PeripheralState SetPollingRate(int hz)
    {
        Require(SabreCodec.Rates.Contains(hz), $"This mouse takes {string.Join(", ", SabreCodec.Rates)} Hz.");
        return Write(_ => (SabreCodec.PollingAt, SabreCodec.Pair(SabreCodec.RateCode(hz))));
    }

    /// <summary>The sleep time in seconds: one of the Web Hub's choices (1, 3, 5, 10, 20, 30 or 40 minutes).</summary>
    public override PeripheralState SetSleep(int seconds)
    {
        Require(seconds % 60 == 0 && SabreCodec.SleepMinutes.Contains(seconds / 60), $"The sleep time is {string.Join(", ", SabreCodec.SleepMinutes)} minutes.");
        return Write(_ => (SabreCodec.SleepAt, SabreCodec.Pair(seconds / 10)));
    }

    public PeripheralState SetLiftOff(SabreLift lift) => Write(s =>
    {
        if (s.SurfaceCalibration != false) throw new PeripheralException("The lift-off distance follows the surface calibration while that is on (iCUE greys it out too).");
        return (SabreCodec.LiftOffAt, SabreCodec.Pair((int)lift));
    });

    public PeripheralState SetMotionSync(bool on) => Write(_ => (SabreCodec.MotionSyncAt, SabreCodec.Pair(on ? 1 : 0)));
    public PeripheralState SetAngleSnap(bool on) => Write(_ => (SabreCodec.AngleSnapAt, SabreCodec.Pair(on ? 1 : 0)));
    public PeripheralState SetRipple(bool on) => Write(_ => (SabreCodec.RippleAt, SabreCodec.Pair(on ? 1 : 0)));

    /// <summary>Button <paramref name="key"/> (1 left, 2 right, 3 middle, 4 back, 5 forward). A map without a left click is refused.</summary>
    public PeripheralState SetButton(int key, SabreAction action)
    {
        Require(key is >= 1 and <= SabreCodec.Keys, "Choose a button from 1 to 5.");
        return Write(s =>
        {
            var after = (s.Buttons ?? throw Unread("buttons")).ToArray();
            after[key - 1] = action;
            if (!after.Contains(SabreAction.Left)) throw new PeripheralException("A left click has to stay on one button.");
            return (SabreCodec.ButtonsAt + 4 * (key - 1), SabreCodec.EncodeButton(action));
        });
    }

    static PeripheralException Unread(string what) => new($"The mouse's {what} didn't read back as expected; nothing is written.");

    /// <summary>
    /// Reads the memory, lets <paramref name="plan"/> choose the field and its new bytes (it may refuse), checks the field's current
    /// bytes validate, writes it (07) and reads the field back (08). A refusal by the mouse or a different read-back is a
    /// <see cref="ReadBackException"/> carrying the state as it is now.
    /// </summary>
    PeripheralState Write(Func<SabreSettings, (int Address, byte[] Data)> plan)
    {
        var problem = With(_collection, true, t =>
        {
            var image = ReadImage(t) ?? throw new PeripheralAsleepException();
            var (address, data) = plan(SabreCodec.Decode(image));
            if (!SabreCodec.BlockValid(image, address, data.Length)) throw Unread($"field at {address:X2}");
            if (Exchange(t, SabreCodec.Request(SabreCodec.WriteMemory, address, data), attempts: 1) is { } ack && ack[2] != 0)
                return $"The mouse refused the change (status {ack[2]:X2}).";
            return ReadBytes(t, address, data.Length) is { } back && back.AsSpan().SequenceEqual(data) ? null
                : "The mouse doesn't show the new value when read back.";
        });
        var state = Read();
        return problem is null ? state : throw new ReadBackException(problem, state);
    }
}
