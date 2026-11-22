using KosToolbox.Core.Peripherals.Hid;

namespace KosToolbox.Core.Peripherals.AttackShark;

/// <summary>
/// The Attack Shark X11 on its 2.4 GHz receiver (<c>1D57:FA60</c>). That PID is shared by other Attack Shark mice with other report
/// layouts, so nothing is sent until a battery heartbeat on the event collection carries the X11's model byte (55); until then the
/// driver only listens. A read is the vendor app's own sequence: SetFeature A0 (the request), poll GetFeature A0 until it grants,
/// then GetFeature the report at its own length, checked by id, length and checksum. A write is a read-modify-write of the whole
/// report (other bytes as read), sent again while the event collection brings no acknowledgement (the vendor app resends every 500 ms:
/// over the receiver a write can be lost, seen once in 25 on the friend's X11; the whole report again is harmless), then read back.
/// </summary>
public sealed class AttackSharkDriver : PeripheralDriver
{
    public const ushort VendorId = 0x1D57, ReceiverPid = 0xFA60;
    const int HeartbeatWaitMs = 2500, GrantFirstWait = 500, GrantPoll = 100, GrantTries = 20, AckWaitMs = 700, WriteTries = 4, MacroChunkWaitMs = 1500, MacroTries = 3;

    readonly HidCollection _events, _config;
    readonly Dictionary<byte, byte[]> _reports = new();
    bool _isX11;
    PeripheralBattery? _battery;

    AttackSharkDriver(HidDevice device, HidCollection events, HidCollection config) : base(device, "Attack Shark", "Attack Shark mouse", tested: true)
    {
        _events = events;
        _config = config;
    }

    /// <summary>The receiver with both MI_02 collections: events (usage page 0x0A, 5-byte input) and settings (usage page 0x0B, feature only).</summary>
    public static AttackSharkDriver? Claim(HidDevice d) =>
        d.VendorId == VendorId && d.ProductId == ReceiverPid
        && d.Collections.FirstOrDefault(c => c.UsagePage == 0x0A && c.Usage == 0 && c.InputLength == 5) is { } ev
        && d.Collections.FirstOrDefault(c => c.UsagePage == 0x0B && c.Usage == 0 && c.FeatureLength >= AttackSharkCodec.LengthOf(AttackSharkCodec.Buttons)) is { } cfg
            ? new AttackSharkDriver(d, ev, cfg) : null;

    public override PeripheralKind Kind => PeripheralKind.Mouse;
    /// <summary>"Attack Shark X11" once a heartbeat carried its model byte (the tray reads the battery first); generic before.</summary>
    public override string? Model => _isX11 ? "Attack Shark X11" : base.Model;
    public override PeripheralConnection? Connection => PeripheralConnection.Receiver;
    public override bool StoredOnly => true;

    /// <summary>Settings whose report was read and decoded: nothing is written that couldn't be read first.</summary>
    public override IReadOnlySet<PeripheralSetting> Writable
    {
        get
        {
            var set = new HashSet<PeripheralSetting>();
            if (!_isX11) return set;
            if (_reports.TryGetValue(AttackSharkCodec.Dpi, out var d) && AttackSharkCodec.DecodeStages(d) is not null)
            {
                set.Add(PeripheralSetting.Dpi);
                set.Add(PeripheralSetting.ActiveStage);
            }
            if (_reports.TryGetValue(AttackSharkCodec.Polling, out var p) && AttackSharkCodec.DecodePolling(p) is not null) set.Add(PeripheralSetting.PollingRate);
            if (_reports.ContainsKey(AttackSharkCodec.Light))
            {
                set.Add(PeripheralSetting.Sleep);
                set.Add(PeripheralSetting.Brightness);
                set.Add(PeripheralSetting.Lighting);
            }
            return set;
        }
    }

    public override PeripheralState Read()
    {
        _battery = ListenBattery();
        // Nothing is asked of a model that isn't the X11 (the PID is shared) or of a mouse that's silent now (asleep, off, Bluetooth).
        if (!_isX11 || _battery.Asleep) return new PeripheralState { Name = Name, Battery = _battery };
        With(_config, false, t =>
        {
            foreach (var id in new[] { AttackSharkCodec.Version, AttackSharkCodec.Polling, AttackSharkCodec.Light, AttackSharkCodec.Dpi, AttackSharkCodec.Buttons })
            {
                if (id == AttackSharkCodec.Version && _reports.ContainsKey(id)) continue; // the firmware doesn't change while we run
                if (TryGet(t, id) is { } r) _reports[id] = r;
                else _reports.Remove(id);
            }
            return 0;
        });
        return State();
    }

    public override PeripheralBattery? ReadBattery() => _battery = ListenBattery();

    /// <summary>
    /// Waits for a heartbeat (every 2 s while the mouse is linked; silence means asleep, off or on Bluetooth, never 0 %). The model
    /// byte of the first one decides whether this is an X11.
    /// </summary>
    PeripheralBattery ListenBattery() => With(_events, true, t =>
    {
        var end = Environment.TickCount64 + HeartbeatWaitMs;
        while (Environment.TickCount64 < end)
        {
            if (t.Read((int)Math.Max(1, end - Environment.TickCount64)) is not { } e) break;
            if (AttackSharkCodec.Heartbeat(e) is not { } h) continue;
            _isX11 = h.Model == AttackSharkCodec.ModelX11;
            return new PeripheralBattery(h.Percent, h.Charging);
        }
        return new PeripheralBattery(null, null, Asleep: true);
    });

    /// <summary>"Attack Shark X11" once a heartbeat carried its model byte; the catalog name stays generic for the shared receiver PID.</summary>
    string Name => Model!;

    PeripheralState State()
    {
        var dpi = _reports.TryGetValue(AttackSharkCodec.Dpi, out var d) ? AttackSharkCodec.DecodeStages(d) : null;
        var light = _reports.TryGetValue(AttackSharkCodec.Light, out var l) ? l : null;
        return new PeripheralState
        {
            Name = Name,
            Firmware = _reports.TryGetValue(AttackSharkCodec.Version, out var v) ? AttackSharkCodec.DecodeVersion(v) : null,
            Battery = _battery,
            Dpi = dpi is { } s
                ? new PeripheralDpi(s.Stages[s.Active - 1], s.Stages[s.Active - 1], AttackSharkCodec.DpiMin, AttackSharkCodec.DpiMax, 50,
                    s.Stages.Select(x => new DpiStage(x, x)).ToList(), s.Active)
                : null,
            Polling = _reports.TryGetValue(AttackSharkCodec.Polling, out var p) && AttackSharkCodec.DecodePolling(p) is { } hz
                ? new PeripheralPolling(hz, AttackSharkCodec.Rates) : null,
            SleepSeconds = light is null ? null : AttackSharkCodec.DecodeSleepSeconds(light),
            Brightness = light is null ? null : AttackSharkCodec.DecodeBrightness(light),
            Lighting = light is null ? null : AttackSharkCodec.DecodeLighting(light),
        };
    }

    // ---------- setters ----------

    /// <summary>The DPI of the active stage (one axis: X and Y must match).</summary>
    public override PeripheralState SetDpi(int x, int y)
    {
        Require(x == y, "This mouse has one DPI for both axes.");
        Require(AttackSharkCodec.ValidDpi(x), "DPI goes from 50 to 22000 (steps of 50 up to 10000, 100 up to 20000, then 200).");
        return Write(AttackSharkCodec.Dpi, r => AttackSharkCodec.WithStageDpi(r, r[24] - 1, x),
            now => AttackSharkCodec.DecodeStages(now) is { } s && s.Stages[s.Active - 1] == x ? null : $"The mouse doesn't show {x} DPI on its active stage.");
    }

    public override PeripheralState SetActiveStage(int stage)
    {
        var count = Current(AttackSharkCodec.Dpi) is { } r && AttackSharkCodec.DecodeStages(r) is { } s ? s.Stages.Count : 0;
        Require(stage >= 1 && stage <= count, $"Choose a stage from 1 to {count}.");
        return Write(AttackSharkCodec.Dpi, r => AttackSharkCodec.WithActiveStage(r, stage),
            now => now[24] == stage ? null : $"The mouse shows stage {now[24]} instead of {stage}.");
    }

    public override PeripheralState SetPollingRate(int hz)
    {
        Require(AttackSharkCodec.Rates.Contains(hz), $"This mouse takes {string.Join(", ", AttackSharkCodec.Rates)} Hz.");
        return Write(AttackSharkCodec.Polling, r => AttackSharkCodec.WithPolling(r, hz),
            now => AttackSharkCodec.DecodePolling(now) == hz ? null : $"The mouse shows {AttackSharkCodec.DecodePolling(now)?.ToString() ?? "an unknown"} Hz instead of {hz} Hz.");
    }

    public override PeripheralState SetSleep(int seconds)
    {
        Require(seconds % 30 == 0 && seconds is >= AttackSharkCodec.SleepMinSeconds and <= AttackSharkCodec.SleepMaxSeconds,
            "The sleep timer goes from 0.5 to 30 minutes in half minutes.");
        return Write(AttackSharkCodec.Light, r => AttackSharkCodec.WithSleep(r, seconds),
            now => AttackSharkCodec.DecodeSleepSeconds(now) == seconds ? null : $"The mouse shows {AttackSharkCodec.DecodeSleepSeconds(now)} s instead of {seconds} s.");
    }

    public override PeripheralState SetBrightness(int percent)
    {
        Require(percent is >= 0 and <= 100, "Brightness goes from 0 to 100 %.");
        var level = AttackSharkCodec.Level(percent);
        return Write(AttackSharkCodec.Light, r => AttackSharkCodec.WithBrightness(r, percent),
            now => (now[5] & 0x0F) == level ? null : $"The mouse shows {AttackSharkCodec.DecodeBrightness(now)} % instead of {AttackSharkCodec.Percent(level)} %.");
    }

    public override PeripheralState SetLighting(LightingEffect effect, int color)
    {
        Require(effect is LightingEffect.Off or LightingEffect.Static or LightingEffect.Breathing or LightingEffect.Spectrum, "Choose off, static, breathing or spectrum.");
        Require(color is >= 0 and <= 0xFFFFFF, "The color is #RRGGBB.");
        var want = new PeripheralLighting(effect, effect is LightingEffect.Static or LightingEffect.Breathing ? color : null);
        return Write(AttackSharkCodec.Light, r => AttackSharkCodec.WithLighting(r, effect, color),
            now => AttackSharkCodec.DecodeLighting(now) is var l && l != want ? $"The mouse shows {l.Effect}{(l.Color is { } c ? $" #{c:X6}" : "")} instead." : null);
    }

    // ---------- X11-only settings (no page rows yet; the probe and the next page slice use them) ----------

    /// <summary>The X11's other settings as last read, or null before a read.</summary>
    public AttackSharkExtras? Extras => !_isX11 || Current(AttackSharkCodec.Light) is not { } l || Current(AttackSharkCodec.Dpi) is not { } d ? null
        : new AttackSharkExtras(AttackSharkCodec.DecodeDeepSleepMinutes(l), AttackSharkCodec.DecodeDebounceMs(l), AttackSharkCodec.DecodeRipple(d),
            AttackSharkCodec.DecodeAngleSnap(d), Enumerable.Range(0, AttackSharkCodec.DpiStages).Select(i => AttackSharkCodec.DecodeStageColor(d, i)).ToList(),
            Current(AttackSharkCodec.Buttons) is { } b ? Enumerable.Range(0, AttackSharkCodec.ButtonSlots).Select(k => AttackSharkCodec.DecodeButton(b, k)).ToList() : null);

    /// <summary>Deep sleep, 1..60 minutes.</summary>
    public PeripheralState SetDeepSleep(int minutes)
    {
        Require(minutes is >= 1 and <= 60, "Deep sleep goes from 1 to 60 minutes.");
        return Write(AttackSharkCodec.Light, r => AttackSharkCodec.WithDeepSleep(r, minutes),
            now => AttackSharkCodec.DecodeDeepSleepMinutes(now) == minutes ? null : $"The mouse shows {AttackSharkCodec.DecodeDeepSleepMinutes(now)} min instead of {minutes}.");
    }

    /// <summary>Key response (debounce), 4..50 ms in steps of 2.</summary>
    public PeripheralState SetDebounce(int ms)
    {
        Require(ms is >= 4 and <= 50 && ms % 2 == 0, "Key response goes from 4 to 50 ms in steps of 2.");
        return Write(AttackSharkCodec.Light, r => AttackSharkCodec.WithDebounce(r, ms),
            now => AttackSharkCodec.DecodeDebounceMs(now) == ms ? null : $"The mouse shows {AttackSharkCodec.DecodeDebounceMs(now)} ms instead of {ms} ms.");
    }

    public PeripheralState SetRipple(bool on) => Write(AttackSharkCodec.Dpi, r => AttackSharkCodec.WithRipple(r, on),
        now => AttackSharkCodec.DecodeRipple(now) == on ? null : "The mouse didn't keep the ripple control change.");

    public PeripheralState SetAngleSnap(bool on) => Write(AttackSharkCodec.Dpi, r => AttackSharkCodec.WithAngleSnap(r, on),
        now => AttackSharkCodec.DecodeAngleSnap(now) == on ? null : "The mouse didn't keep the angle snap change.");

    /// <summary>The DPI of one stage (1-based, of the enabled ones); the others stay byte-identical.</summary>
    public PeripheralState SetStageDpi(int stage, int dpi)
    {
        var count = Current(AttackSharkCodec.Dpi) is { } r0 && AttackSharkCodec.DecodeStages(r0) is { } s0 ? s0.Stages.Count : 0;
        Require(stage >= 1 && stage <= count, $"Choose a stage from 1 to {count}.");
        Require(AttackSharkCodec.ValidDpi(dpi), "DPI goes from 50 to 22000 (steps of 50 up to 10000, 100 up to 20000, then 200).");
        return Write(AttackSharkCodec.Dpi, r => AttackSharkCodec.WithStageDpi(r, stage - 1, dpi),
            now => AttackSharkCodec.DecodeStage(now, stage - 1) == dpi ? null : $"The mouse shows {AttackSharkCodec.DecodeStage(now, stage - 1)} DPI on stage {stage} instead of {dpi}.");
    }

    /// <summary>The colour the mouse shows for a stage (1..8), 0xRRGGBB.</summary>
    public PeripheralState SetStageColor(int stage, int color)
    {
        Require(stage is >= 1 and <= AttackSharkCodec.DpiStages, "Choose a stage from 1 to 8.");
        Require(color is >= 0 and <= 0xFFFFFF, "The color is #RRGGBB.");
        return Write(AttackSharkCodec.Dpi, r => AttackSharkCodec.WithStageColor(r, stage - 1, color),
            now => AttackSharkCodec.DecodeStageColor(now, stage - 1) == color ? null : $"The mouse shows #{AttackSharkCodec.DecodeStageColor(now, stage - 1):X6} on stage {stage}.");
    }

    /// <summary>One button slot (0..17; the app's buttons 1..5 are slots 0, 1, 2, 6, 7). A map without a left click is refused.</summary>
    public PeripheralState SetButton(int slot, ButtonAction action)
    {
        Require(slot is >= 0 and < AttackSharkCodec.ButtonSlots, "Choose a button slot from 0 to 17.");
        if (Current(AttackSharkCodec.Buttons) is { } cached) AttackSharkCodec.WithButton(cached, slot, action); // refuses a map without a left click before anything is sent
        return Write(AttackSharkCodec.Buttons, r => AttackSharkCodec.WithButton(r, slot, action),
            now => AttackSharkCodec.DecodeButton(now, slot) == action ? null : $"The mouse shows {AttackSharkCodec.DecodeButton(now, slot)} on slot {slot} instead of {action}.");
    }

    /// <summary>
    /// Stores a macro in <paramref name="slot"/> (1..18; the vendor app uses the button's slot + 1). The mouse can't report macros,
    /// so each of the three reports is confirmed by its acknowledgement only (all three are sent; a refusal stops it with an error);
    /// bind it with <see cref="SetButton"/> and <see cref="ButtonAction.RunMacro"/>. Returns each report's acknowledgement: true, or
    /// null when none arrived.
    /// </summary>
    public bool?[] WriteMacro(int slot, IReadOnlyList<MacroStep> steps, int playMode = 0, int loops = 1, byte lastLength = 0x0C)
    {
        if (!_isX11 || Current(AttackSharkCodec.Buttons) is null) throw new PeripheralException("The mouse hasn't answered a read yet; nothing is written.");
        var reports = AttackSharkCodec.EncodeMacro(slot, steps, playMode, loops, lastLength);
        return With(_events, true, ev => With(_config, false, t =>
        {
            // On the friend's X11 only the last report was acknowledged (the macro then ran): the whole macro is sent again when it isn't.
            var acks = new bool?[reports.Length];
            for (var attempt = 0; attempt < MacroTries && acks[^1] != true; attempt++)
                for (var j = 0; j < reports.Length; j++)
                {
                    ev.FlushQueue();
                    t.SetFeature(AttackSharkCodec.Macro, reports[j].AsSpan(1), reports[j].Length);
                    acks[j] = WaitAck(ev, AttackSharkCodec.Macro, MacroChunkWaitMs);
                    if (acks[j] == false) throw new PeripheralException($"The mouse refused part {j + 1} of the macro.");
                }
            return acks;
        }));
    }

    /// <summary>Writes back a report read earlier (04, 05, 06 or 08, checksum intact), e.g. to undo a test; read back byte for byte.</summary>
    public PeripheralState Restore(byte[] report)
    {
        var id = report[0];
        Require(id is AttackSharkCodec.Dpi or AttackSharkCodec.Light or AttackSharkCodec.Polling or AttackSharkCodec.Buttons && AttackSharkCodec.Valid(report, id),
            "Only a valid 04, 05, 06 or 08 report can be written back.");
        var want = report[..AttackSharkCodec.LengthOf(id)];
        return Write(id, _ => want, now => now.AsSpan().SequenceEqual(want) ? null : $"Report {id:X2} reads back differently.");
    }

    /// <summary>The raw report as last read (for a backup before changing it), or null.</summary>
    public byte[]? Raw(byte id) => Current(id)?.ToArray();

    byte[]? Current(byte id) => _reports.TryGetValue(id, out var r) ? r : null;

    /// <summary>
    /// Reads <paramref name="id"/> fresh, sends <paramref name="change"/> of it until acknowledged (at most <see cref="WriteTries"/> times),
    /// reads it back; <paramref name="check"/> returns the mismatch or null. A refusal is an error; no acknowledgement at all leaves it to the read-back.
    /// </summary>
    PeripheralState Write(byte id, Func<byte[], byte[]> change, Func<byte[], string?> check)
    {
        if (!_isX11 || Current(id) is null) throw new PeripheralException("The mouse hasn't answered a read yet; nothing is written.");
        var problem = With(_events, true, ev => With(_config, false, t =>
        {
            var before = Get(t, id);
            _reports[id] = before;
            var report = change(before);
            bool? ack = null;
            for (var attempt = 0; attempt < WriteTries && ack is null; attempt++)
            {
                ev.FlushQueue();
                t.SetFeature(id, report.AsSpan(1), report.Length);
                ack = WaitAck(ev, id, AckWaitMs);
            }
            if (ack == false) return "The mouse refused the change.";
            var now = Get(t, id);
            _reports[id] = now;
            return check(now);
        }));
        var state = State();
        return problem is null ? state : throw new ReadBackException(problem, state);
    }

    static bool? WaitAck(IHidTransport ev, byte id, int waitMs)
    {
        var end = Environment.TickCount64 + waitMs;
        while (Environment.TickCount64 < end)
        {
            if (ev.Read((int)Math.Max(1, end - Environment.TickCount64)) is not { } e) return null;
            if (AttackSharkCodec.Ack(e, id) is { } ok) return ok;
        }
        return null;
    }

    byte[]? TryGet(IHidTransport t, byte id)
    {
        try { return Get(t, id); }
        catch (PeripheralException) { return null; }
    }

    /// <summary>One read: the A0 request, its grant, the report at its own length. Retried once when the reply isn't the report.</summary>
    static byte[] Get(IHidTransport t, byte id)
    {
        var len = AttackSharkCodec.LengthOf(id);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            t.SetFeature(AttackSharkCodec.ReadRequest, AttackSharkCodec.ReadRequestPayload(id), AttackSharkCodec.RequestLength);
            Delay(GrantFirstWait);
            var granted = false;
            for (var poll = 0; poll < GrantTries && !granted; poll++)
            {
                granted = AttackSharkCodec.Granted(t.GetFeature(AttackSharkCodec.ReadRequest, AttackSharkCodec.RequestLength));
                if (!granted) Delay(GrantPoll);
            }
            if (!granted) continue;
            var r = t.GetFeature(id, len);
            if (AttackSharkCodec.Valid(r, id)) return r[..len];
        }
        throw new PeripheralException($"The mouse didn't answer the read of report {id:X2}.");
    }
}

/// <summary>The X11 settings the shared page has no row for yet. <paramref name="Buttons"/>: the 18 slots, null when unread.</summary>
public sealed record AttackSharkExtras(int DeepSleepMinutes, int DebounceMs, bool Ripple, bool AngleSnap, IReadOnlyList<int> StageColors,
    IReadOnlyList<ButtonAction>? Buttons);
