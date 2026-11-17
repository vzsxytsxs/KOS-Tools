using KosToolbox.Core.Peripherals.Hid;

namespace KosToolbox.Core.Peripherals.Razer;

/// <summary>
/// Razer mice over the 90-byte feature report, on the collection whose feature report is 91 bytes (the mouse collection, MI_00, on the
/// Viper Ultimate and its receiver). Exchange: SetFeature, wait, GetFeature while the status is busy. Getters retry on a stale reply;
/// setters are sent once and only after a getter has confirmed the transaction id. Timing from mouse-protocol's drivers
/// (src/drivers/razer/viper-hid.ts 100 ms response delay; hid.ts 150 ms link settle after a wireless polling change) and
/// Wireless Battery (15 ms then 40 ms polls, 8 attempts).
/// </summary>
public sealed class RazerDriver : PeripheralDriver
{
    const int FirstWait = 30, BusyWait = 40, Polls = 8, GetterAttempts = 3, PollingSettleMs = 150;
    const int SleepMin = 60, SleepMax = 900, DpiMin = 100;

    readonly RazerModel _model;
    readonly HidCollection _control;
    byte? _transactionId;
    bool? _extendedPolling;
    string? _serial;
    PeripheralState? _last;

    public RazerDriver(HidDevice device, RazerModel model) : base(device, "Razer", model.Name, model.Tested)
    {
        _model = model;
        _control = ControlCollection(device) ?? throw new ArgumentException("This device has no 91-byte Razer control collection.");
    }

    /// <summary>The 91-byte feature collection, the mouse collection first (where the Viper Ultimate answers).</summary>
    public static HidCollection? ControlCollection(HidDevice device) => device.Collections
        .Where(c => c.FeatureLength == RazerCodec.Length + 1)
        .OrderBy(c => c.UsagePage == 0x01 && c.Usage == 0x02 ? 0 : 1).FirstOrDefault();

    public override PeripheralKind Kind => PeripheralKind.Mouse;
    public override PeripheralConnection? Connection => _model.Wireless ? PeripheralConnection.Receiver : PeripheralConnection.Wired;
    /// <summary>The transaction id that answered a getter; null until one did.</summary>
    public byte? TransactionId => _transactionId;

    /// <summary>Settings whose current value was read: nothing is written that couldn't be read first.</summary>
    public override IReadOnlySet<PeripheralSetting> Writable
    {
        get
        {
            var set = new HashSet<PeripheralSetting>();
            if (_last is not { } s || _transactionId is null) return set;
            if (s.Dpi is not null) set.Add(PeripheralSetting.Dpi);
            if (s.Dpi?.Stages is { Count: > 1 }) set.Add(PeripheralSetting.ActiveStage);
            if (s.Polling is not null) set.Add(PeripheralSetting.PollingRate);
            if (s.SleepSeconds is not null) set.Add(PeripheralSetting.Sleep);
            if (s.LowBatteryPercent is not null) set.Add(PeripheralSetting.LowBattery);
            if (s.Brightness is not null) set.Add(PeripheralSetting.Brightness);
            return set;
        }
    }

    public override PeripheralState Read() => With(_control, false, t =>
    {
        try
        {
            ResolveTransactionId(t);
            var dpi = Try(t, RazerCodec.DpiGet, new[] { _model.DpiStorage }) is { } d ? RazerCodec.DecodeDpi(d) : null;
            var stages = Try(t, RazerCodec.StagesGet, new[] { _model.DpiStorage }) is { } st ? RazerCodec.DecodeStages(st) : default;
            _serial ??= Try(t, RazerCodec.Serial) is { } sn ? RazerCodec.DecodeSerial(sn) : null;
            _last = new PeripheralState
            {
                Name = _model.Name,
                Firmware = Try(t, RazerCodec.Firmware) is { } fw ? RazerCodec.DecodeFirmware(fw) : null,
                Serial = _serial,
                Battery = ReadBattery(t),
                Dpi = dpi is null ? null : new PeripheralDpi(dpi.X, dpi.Y, DpiMin, _model.MaxDpi, 50,
                    stages.Stages is { Count: > 0 } ? stages.Stages : null, stages.Stages is { Count: > 0 } ? stages.Active : null),
                Polling = ReadPolling(t) is { } hz ? new PeripheralPolling(hz, _model.Rates) : null,
                SleepSeconds = Try(t, RazerCodec.SleepGet) is { } sl ? RazerCodec.DecodeSleep(sl) : null,
                LowBatteryPercent = Try(t, RazerCodec.LowBatteryGet) is { } lb ? RazerCodec.DecodeLowBattery(lb) : null,
                Brightness = _model.Led is { } led && Try(t, RazerCodec.BrightnessGet, new[] { RazerCodec.Stored, led }) is { } br ? RazerCodec.DecodeBrightness(br) : null,
            };
            return _last;
        }
        catch (PeripheralAsleepException)
        {
            _last = null;
            return new PeripheralState { Name = _model.Name, Battery = new PeripheralBattery(null, null, Asleep: true) };
        }
    });

    public override PeripheralBattery? ReadBattery() => With(_control, false, t =>
    {
        try
        {
            ResolveTransactionId(t);
            return ReadBattery(t);
        }
        catch (PeripheralAsleepException) { return new PeripheralBattery(null, null, Asleep: true); }
    });

    PeripheralBattery? ReadBattery(IHidTransport t) => Try(t, RazerCodec.Battery) is { } b
        ? new PeripheralBattery(RazerCodec.DecodeBattery(b), Try(t, RazerCodec.Charging) is { } c ? RazerCodec.DecodeCharging(c) : null)
        : null;

    /// <summary>The extended command first (the stock receivers of this generation), the legacy one when it's unsupported. Getters only.</summary>
    int? ReadPolling(IHidTransport t)
    {
        if (_extendedPolling != false && Try(t, RazerCodec.PollingExtGet, new byte[] { 0 }) is { } ext && ext[1] != 0)
        {
            _extendedPolling = true;
            return RazerCodec.DecodePollingExtended(ext);
        }
        if (Try(t, RazerCodec.PollingGet) is { } legacy && legacy[0] != 0)
        {
            _extendedPolling = false;
            return RazerCodec.DecodePolling(legacy);
        }
        return null;
    }

    // ---------- setters: validated, sent once, read back ----------
    public override PeripheralState SetDpi(int x, int y) => SetDpi(x, y, RazerCodec.Stored);

    /// <summary>The live DPI only (no-store byte): what gamemode uses, so the stored value stays the user's.</summary>
    public override PeripheralState SetDpiLive(int x, int y) => SetDpi(x, y, RazerCodec.NoStore);
    public override bool HasLiveDpi => true;

    PeripheralState SetDpi(int x, int y, byte storage)
    {
        Require(x >= DpiMin && x <= _model.MaxDpi && y >= DpiMin && y <= _model.MaxDpi, $"DPI goes from {DpiMin} to {_model.MaxDpi}.");
        return Write(t =>
        {
            Set(t, RazerCodec.DpiSet, RazerCodec.EncodeDpi(storage, x, y));
            // A live write is read back live; a stored one the way this generation reads its DPI.
            var now = RazerCodec.DecodeDpi(Get(t, RazerCodec.DpiGet, new[] { storage == RazerCodec.NoStore ? storage : _model.DpiStorage }));
            return now == new DpiStage(x, y) ? null : $"The mouse shows {now.X}×{now.Y} DPI instead of {x}×{y}.";
        });
    }

    public override PeripheralState SetActiveStage(int stage) => Write(t =>
    {
        var table = Get(t, RazerCodec.StagesGet, new[] { _model.DpiStorage }); // read-modify-write: only the active byte changes
        var copy = RazerCodec.WithActiveStage(table, stage);
        copy[0] = RazerCodec.Stored;
        Set(t, RazerCodec.StagesSet, copy);
        var now = RazerCodec.DecodeStages(Get(t, RazerCodec.StagesGet, new[] { _model.DpiStorage })).Active;
        return now == stage ? null : $"The mouse shows stage {now} instead of {stage}.";
    });

    public override PeripheralState SetPollingRate(int hz)
    {
        Require(_model.Rates.Contains(hz), $"This mouse takes {string.Join(", ", _model.Rates)} Hz.");
        if (_extendedPolling is null) throw new PeripheralException("The polling rate hasn't been read yet.");
        return Write(t =>
        {
            if (_extendedPolling == true) Set(t, RazerCodec.PollingExtSet, new byte[] { 0, RazerCodec.Divisor(8000, hz) });
            else Set(t, RazerCodec.PollingSet, new[] { RazerCodec.Divisor(1000, hz) });
            if (_model.Wireless) Delay(PollingSettleMs); // the link reconfigures; the first replies can be corrupt
            var now = ReadPolling(t);
            return now == hz ? null : $"The mouse shows {(now is null ? "no polling rate" : $"{now} Hz")} instead of {hz} Hz.";
        });
    }

    public override PeripheralState SetSleep(int seconds)
    {
        Require(seconds is >= SleepMin and <= SleepMax, $"The sleep timer goes from {SleepMin / 60} to {SleepMax / 60} minutes.");
        return Write(t =>
        {
            Set(t, RazerCodec.SleepSet, RazerCodec.EncodeSleep(seconds));
            var now = RazerCodec.DecodeSleep(Get(t, RazerCodec.SleepGet));
            return now == seconds ? null : $"The mouse shows {now} s instead of {seconds} s.";
        });
    }

    public override PeripheralState SetLowBattery(int percent)
    {
        Require(percent is >= 5 and <= 100, "The low-battery threshold goes from 5 to 100 %.");
        return Write(t =>
        {
            Set(t, RazerCodec.LowBatterySet, RazerCodec.EncodeLowBattery(percent));
            var now = RazerCodec.DecodeLowBattery(Get(t, RazerCodec.LowBatteryGet));
            return now == percent ? null : $"The mouse shows {now} % instead of {percent} %.";
        });
    }

    public override PeripheralState SetBrightness(int percent)
    {
        Require(percent is >= 0 and <= 100, "Brightness goes from 0 to 100 %.");
        var led = _model.Led ?? throw Unsupported();
        return Write(t =>
        {
            Set(t, RazerCodec.BrightnessSet, new[] { RazerCodec.Stored, led, RazerCodec.Level(percent) });
            var now = RazerCodec.DecodeBrightness(Get(t, RazerCodec.BrightnessGet, new[] { RazerCodec.Stored, led }));
            return now == percent ? null : $"The mouse shows {now} % instead of {percent} %.";
        });
    }

    /// <summary>Runs a write with its read-back (<paramref name="work"/> returns the mismatch, or null), then reads the whole state.</summary>
    PeripheralState Write(Func<IHidTransport, string?> work)
    {
        if (_transactionId is null) throw new PeripheralException("The mouse hasn't answered a read yet; nothing is written.");
        var problem = With(_control, false, work);
        var state = Read();
        return problem is null ? state : throw new ReadBackException(problem, state);
    }

    // ---------- exchange ----------
    void ResolveTransactionId(IHidTransport t)
    {
        if (_transactionId is not null) return;
        foreach (var id in RazerCatalog.TransactionIds(_model))
        {
            try
            {
                Exchange(t, id, RazerCodec.Firmware, default);
                _transactionId = id;
                return;
            }
            catch (PeripheralAsleepException) { } // asleep, or not this product's id: the next one
        }
        throw new PeripheralAsleepException();
    }

    byte[]? Try(IHidTransport t, RazerCommand c, byte[]? args = null)
    {
        try { return Get(t, c, args); }
        catch (PeripheralAsleepException) { throw; }
        catch (PeripheralException) { return null; } // unsupported or failed: the page hides it
    }

    byte[] Get(IHidTransport t, RazerCommand c, byte[]? args = null) => Exchange(t, _transactionId!.Value, c, args);

    void Set(IHidTransport t, RazerCommand c, byte[] args) => Exchange(t, _transactionId!.Value, c, args);

    /// <summary>One request and its reply. Getters retry a stale or busy reply; a setter never sends twice.</summary>
    static byte[] Exchange(IHidTransport t, byte transactionId, RazerCommand command, ReadOnlySpan<byte> args)
    {
        var request = RazerCodec.Request(transactionId, command, args);
        for (var attempt = 0; ; attempt++)
        {
            var last = attempt + 1 >= (command.IsGetter ? GetterAttempts : 1);
            t.SetFeature(0, request);
            Delay(FirstWait);
            RazerReply reply = RazerCodec.Parse(t.GetFeature(0), command);
            for (var poll = 1; poll < Polls && reply.Status is RazerStatus.Busy or RazerStatus.Request; poll++)
            {
                Delay(BusyWait);
                reply = RazerCodec.Parse(t.GetFeature(0), command);
            }
            if (reply.Status is RazerStatus.Busy or RazerStatus.Request || !reply.Matches)
            {
                if (!last) continue;
                throw new PeripheralException(command.IsGetter
                    ? "The mouse didn't answer this read."
                    : "The mouse didn't confirm the change; it wasn't sent again. Read it again to see what it has.");
            }
            return reply.Status switch
            {
                RazerStatus.Ok => reply.Args,
                RazerStatus.NoAnswer => throw new PeripheralAsleepException(),
                RazerStatus.Unsupported => throw new PeripheralException("The mouse doesn't support this command."),
                _ => throw new PeripheralException($"The mouse refused the command (status {(byte)reply.Status:X2})."),
            };
        }
    }
}
