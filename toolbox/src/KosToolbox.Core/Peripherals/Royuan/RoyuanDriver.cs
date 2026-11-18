using KosToolbox.Core.Peripherals.Hid;

namespace KosToolbox.Core.Peripherals.Royuan;

/// <summary>A running travel stream: <see cref="Next"/> gives each key report; Dispose (or the process exiting) turns it off.</summary>
public sealed class RoyuanTravelStream : IDisposable
{
    readonly IHidTransport _input;
    readonly double _unit;
    readonly Action _stop;
    readonly EventHandler _onExit;
    int _disposed;

    internal RoyuanTravelStream(IHidTransport input, double unitMm, Action stop)
    {
        _input = input;
        _unit = unitMm;
        _stop = stop;
        _onExit = (_, _) => Dispose();
        AppDomain.CurrentDomain.ProcessExit += _onExit;
    }

    /// <summary>The next key report (slot, travel in mm), or null when none arrives in time (a key held still sends nothing).</summary>
    public (int Slot, double Mm)? Next(int timeoutMs)
    {
        if (_disposed != 0) return null;
        var end = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < end)
        {
            if (_input.Read((int)Math.Max(1, end - Environment.TickCount64)) is not { } r) return null;
            if (RoyuanCodec.ParseTravel(r) is { } t) return (t.Slot, Math.Round(t.Counts * _unit, 3));
        }
        return null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        AppDomain.CurrentDomain.ProcessExit -= _onExit;
        try { _stop(); }
        catch (Exception ex) when (ex is PeripheralException or System.ComponentModel.Win32Exception or IOException) { } // unplugged: nothing to stop
        finally { (_input as IDisposable)?.Dispose(); }
    }
}

/// <summary>
/// ROYUAN gen2 keyboards over the 65-byte feature report of collection FFFF:0002. Claim sends nothing. The first Read sends Identify
/// (0x8F) and nothing else until the board's id is in <see cref="RoyuanCatalog"/>: the PID is shared by boards of another command
/// family, where the same opcodes write other registers. Exchange: SET, 10 ms, GET, opcode echoed. A write is sent once, given
/// 100 ms for the flash (the vendor's wait), read back, and spaced at least 300 ms from the previous one.
/// </summary>
public sealed class RoyuanDriver : PeripheralDriver
{
    const int ReplyWait = 10, FlashWait = 100, WriteSpacing = 300, ReadyPoll = 50, ReadyTries = 40, PicturePageGap = 100, PictureSettle = 2000;
    const long PictureSpacingMs = 10_000;
    static long _lastPicture = long.MinValue / 2; // one board per PC: the spacing is the process's

    readonly HidCollection _config;
    RoyuanModel? _model;
    uint? _foreignId;
    int? _version;
    RoyuanLight? _light;
    RoyuanColumns? _columns;
    RoyuanKey[][]? _layers;
    RoyuanKey[]? _fn;
    PeripheralState? _last;
    long _lastSet = long.MinValue / 2;

    RoyuanDriver(HidDevice device, HidCollection config, bool tested) : base(device, "ROYUAN gen2", null, tested) => _config = config;

    public static RoyuanDriver? Claim(HidDevice d) =>
        RoyuanCatalog.Models.FirstOrDefault(m => m.VendorId == d.VendorId && m.ProductId == d.ProductId) is { } m
        && d.Collections.FirstOrDefault(c => c.UsagePage == RoyuanCodec.UsagePage && c.Usage == RoyuanCodec.Usage
            && c.FeatureLength == RoyuanCodec.PayloadLength + 1) is { } cfg
            ? new RoyuanDriver(d, cfg, m.Tested) : null;

    public override PeripheralKind Kind => PeripheralKind.Keyboard;
    public override PeripheralConnection? Connection => PeripheralConnection.Wired;
    /// <summary>Every setting here is stored in flash: gamemode would wear it on each game start and stop.</summary>
    public override bool GamemodeWrites => false;
    /// <summary>No Off: mode 0 isn't confirmed to turn the light off on this board; brightness 0 does.</summary>
    public override IReadOnlyList<LightingEffect> Effects => new[] { LightingEffect.Static, LightingEffect.Breathing, LightingEffect.Spectrum };
    /// <summary>The identified board; null before the first Read, or when Identify named a board outside the catalog.</summary>
    public RoyuanModel? Identified => _model;
    /// <summary>The firmware version Identify returned (BCD, 0x0505 = 5.05).</summary>
    public int? FirmwareVersion => _version;
    /// <summary>The lighting bytes last read, as stored (restore them with <see cref="SetLight"/>).</summary>
    public RoyuanLight? StoredLight => _light;
    /// <summary>The travel columns last read (restore them with <see cref="WriteColumns"/>).</summary>
    public RoyuanColumns? Columns => _columns?.Copy();
    /// <summary>The active profile's base keymap (sub-layer 0) last read, one entry per slot.</summary>
    public IReadOnlyList<RoyuanKey>? Keymap => _layers?[0];
    /// <summary>Sub-layer 0..3 of the active profile's keymap as last read (every slot, keys or not).</summary>
    public IReadOnlyList<RoyuanKey>? SubLayer(int layer) => _layers?[layer];
    /// <summary>The active profile's Fn layer (Windows) as last read; an empty entry falls through to the base layer.</summary>
    public IReadOnlyList<RoyuanKey>? FnLayer => _fn;
    /// <summary>The board's key positions from the catalog; null before Identify.</summary>
    public IReadOnlyList<RoyuanKeyPosition>? Layout => _model?.Layout;

    /// <summary>The travel these keys have now (what most of them have; not uniform when they differ), from the last read.</summary>
    public PeripheralMagnetic? KeyTravel(IEnumerable<int> slots) =>
        _columns is { } c && _version is { } v ? RoyuanCodec.Summarize(c, RoyuanCodec.TravelUnitMm(v), slots) : null;

    public override IReadOnlySet<PeripheralSetting> Writable
    {
        get
        {
            var set = new HashSet<PeripheralSetting>();
            if (_model is null || _last is not { } s) return set;
            if (s.Polling is not null) set.Add(PeripheralSetting.PollingRate);
            if (_light is not null) { set.Add(PeripheralSetting.Brightness); set.Add(PeripheralSetting.Lighting); }
            if (s.Profile is not null) set.Add(PeripheralSetting.Profile);
            if (s.Magnetic is not null) set.Add(PeripheralSetting.Magnetic);
            return set;
        }
    }

    public override PeripheralState Read() => With(_config, false, t =>
    {
        if (_foreignId is { } foreign) return Unidentified(foreign);
        if (_model is null)
        {
            var id = Ask(t, RoyuanCodec.Request(new[] { RoyuanCodec.Identify }), RoyuanCodec.Identify)
                ?? throw new PeripheralException("The keyboard didn't answer Identify.");
            var deviceId = RoyuanCodec.DeviceId(id);
            if (RoyuanCatalog.Find(Device.VendorId, Device.ProductId, deviceId) is not { } model)
            {
                _foreignId = deviceId; // nothing else is ever sent to this board
                return Unidentified(deviceId);
            }
            _model = model;
            _version = RoyuanCodec.Version(id);
        }
        var polling = Ask(t, RoyuanCodec.Request(new[] { RoyuanCodec.GetPolling }), RoyuanCodec.GetPolling);
        var light = Ask(t, RoyuanCodec.Request(new[] { RoyuanCodec.GetLight }), RoyuanCodec.GetLight);
        var profile = Ask(t, RoyuanCodec.Request(new[] { RoyuanCodec.GetProfile }), RoyuanCodec.GetProfile);
        _light = light is null ? null : RoyuanCodec.DecodeLight(light);
        _columns = ReadColumns(t);
        _layers = ReadLayers(t, out var active);
        _fn = _layers is null ? null : ReadFn(t, active);
        return _last = new PeripheralState
        {
            Name = _model.Name,
            Firmware = RoyuanCodec.VersionText(_version!.Value),
            Polling = polling is not null && RoyuanCodec.DecodePolling(polling) is { } hz ? new PeripheralPolling(hz, _model.PollingRates) : null,
            Brightness = _light is { Brightness: <= 4 } l ? l.Brightness * 25 : null,
            Lighting = _light is null ? null : RoyuanCodec.ToLighting(_light),
            Profile = profile is not null && RoyuanCodec.DecodeProfile(profile) is var p && p < _model.Profiles ? p + 1 : null,
            Profiles = _model.Profiles,
            Magnetic = _columns is null ? null : RoyuanCodec.Summarize(_columns, RoyuanCodec.TravelUnitMm(_version.Value)),
        };
    });

    PeripheralState Unidentified(uint id) => new() { Name = Device.Product, Protocol = $"Board {id} (not supported: nothing is written)" };

    /// <summary>Nothing to read and nothing sent: KOS Governor asks every device for its battery on a timer.</summary>
    public override PeripheralBattery? ReadBattery() => null;

    // ---------- setters: validated, sent once, read back ----------
    public override PeripheralState SetPollingRate(int hz)
    {
        var model = CatalogModel();
        Require(model.PollingRates.Contains(hz), $"This keyboard takes {string.Join(", ", model.PollingRates)} Hz.");
        return Write(RoyuanCodec.EncodePolling(hz), t =>
        {
            var now = Ask(t, RoyuanCodec.Request(new[] { RoyuanCodec.GetPolling }), RoyuanCodec.GetPolling) is { } r ? RoyuanCodec.DecodePolling(r) : null;
            return now == hz ? null : $"The keyboard shows {(now is null ? "no polling rate" : $"{now} Hz")} instead of {hz} Hz.";
        });
    }

    /// <summary>The board stores five steps (0..4): the percentage is rounded to 0, 25, 50, 75 or 100 % and that step is read back.</summary>
    public override PeripheralState SetBrightness(int percent)
    {
        Require(percent is >= 0 and <= 100, "Brightness goes from 0 to 100 %.");
        var light = _light ?? throw new PeripheralException("The lighting hasn't been read yet; nothing is written.");
        return SetLight(light with { Brightness = (byte)Math.Round(percent / 25.0, MidpointRounding.AwayFromZero) });
    }

    /// <summary>Static, breathing (with the color) or spectrum; speed and brightness stay as read.</summary>
    public override PeripheralState SetLighting(LightingEffect effect, int color)
    {
        Require(effect is LightingEffect.Static or LightingEffect.Breathing or LightingEffect.Spectrum, "Choose static, breathing or spectrum.");
        Require(color is >= 0 and <= 0xFFFFFF, "The color is #RRGGBB.");
        var light = _light ?? throw new PeripheralException("The lighting hasn't been read yet; nothing is written.");
        return SetLight(effect == LightingEffect.Spectrum
            ? light with { Mode = 3, Flags = RoyuanCodec.Rainbow }
            : light with { Mode = (byte)(effect == LightingEffect.Static ? 1 : 2), Flags = RoyuanCodec.FixedColor, Color = color });
    }

    /// <summary>The stored lighting bytes as given (modes 0..19, speed and brightness 0..4); restores what <see cref="StoredLight"/> read.</summary>
    public PeripheralState SetLight(RoyuanLight light)
    {
        Require(light.Mode <= 19 && light.Speed <= 4 && light.Brightness <= 4 && light.Color is >= 0 and <= 0xFFFFFF, "Lighting: mode 0..19, speed and brightness 0..4, color #RRGGBB.");
        var want = light with { Color = light.Color == 0xFFFFFF ? 0xFAFAFA : light.Color };
        return Write(RoyuanCodec.EncodeLight(light), t =>
        {
            var now = Ask(t, RoyuanCodec.Request(new[] { RoyuanCodec.GetLight }), RoyuanCodec.GetLight) is { } r ? RoyuanCodec.DecodeLight(r) : null;
            return now == want ? null : now is null ? "The keyboard didn't answer the lighting read." : $"The keyboard shows lighting {now} instead of {want}.";
        });
    }

    public override PeripheralState SetProfile(int profile)
    {
        var model = CatalogModel();
        Require(profile >= 1 && profile <= model.Profiles, $"Profiles go from 1 to {model.Profiles}.");
        return Write(RoyuanCodec.EncodeProfile(profile - 1), t =>
        {
            var now = Ask(t, RoyuanCodec.Request(new[] { RoyuanCodec.GetProfile }), RoyuanCodec.GetProfile) is { } r ? RoyuanCodec.DecodeProfile(r) + 1 : (int?)null;
            return now == profile ? null : $"The keyboard shows profile {now?.ToString() ?? "(none)"} instead of {profile}.";
        });
    }

    /// <summary>
    /// Every key of the active profile: actuation and release point, bottom dead zone, rapid trigger on or off (each key's kind kept)
    /// and, with it on, its steps. A read-modify-write of the columns, from a fresh read; the travel unit comes from the firmware
    /// version Identify returned.
    /// </summary>
    public override PeripheralState SetMagnetic(PeripheralMagnetic travel) => SetKeyTravel(null, travel);

    /// <summary>The same as <see cref="SetMagnetic"/> on these slots only (null: every key); the other keys are left as they are.</summary>
    public PeripheralState SetKeyTravel(IReadOnlyCollection<int>? slots, PeripheralMagnetic travel)
    {
        CatalogModel();
        Require(slots is null || slots.Count > 0 && slots.All(s => s is >= 0 and < RoyuanCodec.Slots), "Choose at least one key.");
        var unit = RoyuanCodec.TravelUnitMm(_version ?? throw new PeripheralException("The firmware version wasn't read; nothing is written."));
        const double A0 = PeripheralMagnetic.ActuationMinMm, A1 = PeripheralMagnetic.ActuationMaxMm;
        Require(travel.ActuationMm is >= A0 and <= A1, $"Actuation goes from {A0} to {A1} mm.");
        Require(travel.BottomDeadZoneMm is >= 0 and <= PeripheralMagnetic.DeadZoneMaxMm, $"The bottom dead zone goes from 0 to {PeripheralMagnetic.DeadZoneMaxMm} mm.");
        if (travel.RapidTrigger)
            Require(travel.RtPressMm >= unit && travel.RtPressMm <= PeripheralMagnetic.RtMaxMm && travel.RtReleaseMm >= unit && travel.RtReleaseMm <= PeripheralMagnetic.RtMaxMm,
                $"Rapid-trigger steps go from {unit} to {PeripheralMagnetic.RtMaxMm} mm.");
        ushort Counts(double mm) => (ushort)Math.Round(mm / unit);
        return Write(t =>
        {
            var now = ReadColumns(t) ?? throw new PeripheralException("The keyboard didn't answer the switch read; nothing is written.");
            if (!now.Keys.Any()) throw new PeripheralException("The keyboard reports no keys; nothing is written.");
            return SendColumns(t, now, RoyuanCodec.WithTravel(now, Counts(travel.ActuationMm), travel.RapidTrigger,
                Counts(travel.RtPressMm), Counts(travel.RtReleaseMm), Counts(travel.BottomDeadZoneMm), slots));
        });
    }

    /// <summary>The columns as given (a backup from <see cref="Columns"/>): only the columns that differ from a fresh read are sent.</summary>
    public PeripheralState WriteColumns(RoyuanColumns want)
    {
        CatalogModel();
        Require(want.Travel.All(c => c.Values.Length == RoyuanCodec.Slots) && want.Mode.Length == RoyuanCodec.Slots, $"A column has {RoyuanCodec.Slots} values.");
        return Write(t => SendColumns(t, ReadColumns(t) ?? throw new PeripheralException("The keyboard didn't answer the switch read; nothing is written."), want));
    }

    /// <summary>
    /// Each changed column whole, <c>last</c> (flash) on its final page only, in order: travel columns, hold time, changed DKS actions
    /// slot by slot, the mode column, then changed snap partners slot by slot. Then every column sent is read back.
    /// </summary>
    string? SendColumns(IHidTransport t, RoyuanColumns now, RoyuanColumns want)
    {
        var sent = new List<byte>();
        foreach (var ((subop, have), (_, values)) in now.Travel.Zip(want.Travel))
            if (!have.AsSpan().SequenceEqual(values)) { SendPages(t, RoyuanCodec.ColumnWriteU16(subop, values)); sent.Add(subop); }
        foreach (var ((subop, have), (_, values)) in now.Bytes.Zip(want.Bytes))
        {
            if (subop == RoyuanCodec.ModeColumn)
                for (var slot = 0; slot < RoyuanCodec.Slots; slot++)
                    if (!now.Actions(slot).AsSpan().SequenceEqual(want.Actions(slot)))
                    {
                        Send(t, RoyuanCodec.DksActionsWrite(slot, want.Actions(slot)));
                        if (!sent.Contains(RoyuanCodec.DksActionsColumn)) sent.Add(RoyuanCodec.DksActionsColumn);
                    }
            if (have.AsSpan().SequenceEqual(values)) continue;
            sent.Add(subop);
            if (subop != RoyuanCodec.PartnerColumn) { SendPages(t, RoyuanCodec.ColumnWriteU8(subop, values)); continue; }
            // A whole partner column is rebuilt from the board's own partner store on apply (R82HE: snap partners read back 255).
            for (var slot = 0; slot < RoyuanCodec.Slots; slot++)
                if (have[slot] != values[slot]) Send(t, RoyuanCodec.ColumnWriteOne(subop, slot, values[slot]));
        }
        if (sent.Count == 0) return null;
        var back = ReadColumns(t);
        if (back is null) return "The keyboard didn't answer the switch read-back.";
        var wrong = back.Travel.Zip(want.Travel).Where(p => sent.Contains(p.First.SubOp) && !p.First.Values.AsSpan().SequenceEqual(p.Second.Values))
            .Select(p => p.First.SubOp)
            .Concat(back.Bytes.Zip(want.Bytes).Where(p => sent.Contains(p.First.SubOp) && !p.First.Values.AsSpan().SequenceEqual(p.Second.Values)).Select(p => p.First.SubOp))
            .ToList();
        if (sent.Contains(RoyuanCodec.DksActionsColumn) && !back.DksActions.AsSpan().SequenceEqual(want.DksActions)) wrong.Add(RoyuanCodec.DksActionsColumn);
        return wrong.Count == 0 ? null : $"The keyboard shows other values for: {string.Join(", ", wrong.Select(Name))}.";
    }

    static string Name(byte subop) => subop switch
    {
        RoyuanCodec.ActuationColumn => "actuation", RoyuanCodec.ReleaseColumn => "release point", RoyuanCodec.RtPressColumn => "rapid-trigger press",
        RoyuanCodec.RtReleaseColumn => "rapid-trigger release", RoyuanCodec.DksPointColumn => "dynamic-keystroke first point",
        RoyuanCodec.HoldTimeColumn => "mod-tap hold time", RoyuanCodec.DeadZoneColumn => "bottom dead zone", RoyuanCodec.DksActionsColumn => "dynamic-keystroke actions",
        RoyuanCodec.PartnerColumn => "snap-tap partner", _ => "key mode",
    };

    void SendPages(IHidTransport t, List<byte[]> pages)
    {
        foreach (var page in pages) Send(t, page, commits: page[4] == 1);
    }

    /// <summary>Every switch column and the DKS actions; null when a read isn't answered with a page.</summary>
    static RoyuanColumns? ReadColumns(IHidTransport t)
    {
        ushort[]? U16(byte subop)
        {
            var v = new ushort[RoyuanCodec.Slots];
            for (var page = 0; page < 4; page++)
            {
                if (Page(t, subop, page) is not { } r) return null;
                RoyuanCodec.DecodeU16Page(r, page, v);
            }
            return v;
        }
        byte[]? U8(byte subop, int pages)
        {
            var v = new byte[pages * 64];
            for (var page = 0; page < pages; page++)
            {
                if (Page(t, subop, page) is not { } r) return null;
                RoyuanCodec.DecodeU8Page(r, page, v);
            }
            return pages == 2 ? v[..RoyuanCodec.Slots] : v;
        }
        if (U16(RoyuanCodec.ActuationColumn) is not { } a || U16(RoyuanCodec.ReleaseColumn) is not { } rel || U16(RoyuanCodec.RtPressColumn) is not { } p
            || U16(RoyuanCodec.RtReleaseColumn) is not { } q || U16(RoyuanCodec.DeadZoneColumn) is not { } dz || U8(RoyuanCodec.ModeColumn, 2) is not { } mode
            || U16(RoyuanCodec.DksPointColumn) is not { } dks || U8(RoyuanCodec.HoldTimeColumn, 2) is not { } hold || U8(RoyuanCodec.PartnerColumn, 2) is not { } partner
            || U8(RoyuanCodec.AllDksActionsColumn, 8) is not { } actions) return null;
        return new RoyuanColumns(a, rel, p, q, dz, mode, dks, hold, partner, actions);
    }

    /// <summary>One sub-layer of a profile's keymap (8 pages); null when a page isn't answered.</summary>
    static RoyuanKey[]? ReadKeymap(IHidTransport t, int profile, int subLayer)
    {
        var keys = new RoyuanKey[RoyuanCodec.Slots];
        for (var page = 0; page < 8; page++)
        {
            t.SetFeature(0, RoyuanCodec.KeymapRead(profile, page, subLayer));
            Delay(ReplyWait);
            var r = t.GetFeature(0);
            if (!RoyuanCodec.IsKeymapPage(r, profile, page, subLayer)) return null;
            RoyuanCodec.DecodeKeymapPage(r, page, keys);
        }
        return keys;
    }

    /// <summary>All four sub-layers of the active profile, fresh; null when the profile or a page isn't answered.</summary>
    RoyuanKey[][]? ReadLayers(IHidTransport t, out int profile)
    {
        profile = -1;
        if (Ask(t, RoyuanCodec.Request(new[] { RoyuanCodec.GetProfile }), RoyuanCodec.GetProfile) is not { } r) return null;
        profile = RoyuanCodec.DecodeProfile(r);
        var layers = new RoyuanKey[4][];
        for (var l = 0; l < 4; l++)
            if (ReadKeymap(t, profile, l) is { } keys) layers[l] = keys;
            else return null;
        return layers;
    }

    /// <summary>Writes the entries of <paramref name="slot"/> that differ (each stored at once), then reads the sub-layers back.</summary>
    string? SendKeys(IHidTransport t, int profile, int slot, RoyuanKey[][] now, IReadOnlyList<RoyuanKey> want)
    {
        var changed = Enumerable.Range(0, want.Count).Where(l => now[l][slot] != want[l]).ToList();
        if (changed.Count == 0) return null;
        foreach (var l in changed) Send(t, RoyuanCodec.KeymapWrite(profile, slot, l, want[l]));
        foreach (var l in changed)
            if (ReadKeymap(t, profile, l) is not { } back || back[slot] != want[l])
                return $"The keyboard shows another key in sub-layer {l}.";
        return null;
    }

    /// <summary>A key's mode as last read: its kind and what the kind uses. Null for a slot without a key.</summary>
    public RoyuanKeyMode? KeyMode(int slot)
    {
        if (_columns is not { } c || _layers is not { } layers || _version is not { } v || slot is < 0 or >= RoyuanCodec.Slots || c.Actuation[slot] == 0) return null;
        return new RoyuanKeyMode((byte)(c.Mode[slot] & 0x7F), c.Partner[slot] < RoyuanCodec.Slots ? c.Partner[slot] : null, c.HoldTime[slot] * 10,
            Math.Round(c.DksPoint[slot] * RoyuanCodec.TravelUnitMm(v), 3), c.Actions(slot), layers.Select(l => l[slot]).ToArray());
    }

    /// <summary>
    /// A key's mode, stored in the active profile: plain, toggle (4, or 5 repeating), mod-tap (hold time; sub-layer 0 is the hold key,
    /// 1 the tap key), dynamic keystroke (first point; four action bytes; the keys of sub-layers 0..3) or snap tap (both keys take kind 7
    /// and each other's slot; a pair it leaves is undone on both keys). The key's rapid-trigger bit is kept. Sub-layer entries are
    /// written first, then the columns (the mode before the snap partners), each read back.
    /// </summary>
    public PeripheralState SetKeyMode(int slot, RoyuanKeyMode mode)
    {
        CatalogModel();
        var unit = RoyuanCodec.TravelUnitMm(_version!.Value);
        Require(_columns is { } c0 && slot is >= 0 and < RoyuanCodec.Slots && c0.Actuation[slot] != 0, "Choose a key.");
        Require(mode.Kind is RoyuanCodec.KindPlain or RoyuanCodec.KindDks or RoyuanCodec.KindModTap or RoyuanCodec.KindToggle
            or RoyuanCodec.KindToggleRepeat or RoyuanCodec.KindSnap, "Unknown key mode.");
        Require(mode.Layers.Count == 4 && mode.Actions.Count == 4, "A key has four sub-layers.");
        Require(mode.Kind != RoyuanCodec.KindSnap || mode.Partner is { } p0 && p0 != slot && p0 is >= 0 and < RoyuanCodec.Slots && _columns!.Actuation[p0] != 0,
            "Snap tap needs another key as its partner.");
        Require(mode.Kind != RoyuanCodec.KindModTap || mode.HoldMs is >= 10 and <= 2550, "The mod-tap hold time goes from 10 to 2550 ms.");
        Require(mode.Kind != RoyuanCodec.KindDks || mode.DksPointMm is >= PeripheralMagnetic.ActuationMinMm and <= PeripheralMagnetic.ActuationMaxMm,
            $"The first point goes from {PeripheralMagnetic.ActuationMinMm} to {PeripheralMagnetic.ActuationMaxMm} mm.");
        return Write(t =>
        {
            var now = ReadColumns(t) ?? throw new PeripheralException("The keyboard didn't answer the switch read; nothing is written.");
            var layers = ReadLayers(t, out var profile) ?? throw new PeripheralException("The keyboard didn't answer the keymap read; nothing is written.");
            var want = now.Copy();
            void Plain(int s) { want.Mode[s] = (byte)(want.Mode[s] & RoyuanCodec.RapidTriggerBit); want.Partner[s] = RoyuanCodec.NoPartner; }
            void LeavePair(int s)
            {
                if ((now.Mode[s] & 0x7F) == RoyuanCodec.KindSnap && now.Partner[s] is var old && old < RoyuanCodec.Slots && now.Partner[old] == s) Plain(old);
            }
            LeavePair(slot);
            want.Mode[slot] = (byte)(now.Mode[slot] & RoyuanCodec.RapidTriggerBit | mode.Kind);
            want.Partner[slot] = RoyuanCodec.NoPartner;
            if (mode.Kind == RoyuanCodec.KindSnap)
            {
                var p = mode.Partner!.Value;
                LeavePair(p);
                want.Partner[slot] = (byte)p;
                want.Mode[p] = (byte)(now.Mode[p] & RoyuanCodec.RapidTriggerBit | RoyuanCodec.KindSnap);
                want.Partner[p] = (byte)slot;
            }
            if (mode.Kind == RoyuanCodec.KindModTap) want.HoldTime[slot] = (byte)Math.Round(mode.HoldMs / 10.0);
            if (mode.Kind == RoyuanCodec.KindDks)
            {
                want.DksPoint[slot] = (ushort)Math.Round(mode.DksPointMm / unit);
                for (var l = 0; l < 4; l++) want.DksActions[l * RoyuanCodec.Slots + slot] = mode.Actions[l];
            }
            return SendKeys(t, profile, slot, layers, mode.Layers) ?? SendColumns(t, now, want);
        });
    }

    static RoyuanKey[]? ReadFn(IHidTransport t, int profile)
    {
        var keys = new RoyuanKey[RoyuanCodec.Slots];
        for (var page = 0; page < 8; page++)
        {
            t.SetFeature(0, RoyuanCodec.FnRead(profile, page));
            Delay(ReplyWait);
            var r = t.GetFeature(0);
            if (!RoyuanCodec.IsFnPage(r, profile, page)) return null;
            RoyuanCodec.DecodeKeymapPage(r, page, keys);
        }
        return keys;
    }

    /// <summary>One Fn-layer entry of the active profile (Windows layer), stored and read back.</summary>
    public PeripheralState SetFnKey(int slot, RoyuanKey key)
    {
        CatalogModel();
        Require(slot is >= 0 and < RoyuanCodec.Slots, "Choose a key.");
        return Write(t =>
        {
            if (Ask(t, RoyuanCodec.Request(new[] { RoyuanCodec.GetProfile }), RoyuanCodec.GetProfile) is not { } p) throw new PeripheralException("The keyboard didn't answer the profile read; nothing is written.");
            var profile = RoyuanCodec.DecodeProfile(p);
            Send(t, RoyuanCodec.FnWrite(profile, slot, key));
            return ReadFn(t, profile) is { } back && back[slot] == key ? null : "The keyboard shows another key in the Fn layer.";
        });
    }

    /// <summary>Whether the catalog says the macro write stores (the R82HE's doesn't: a saved slot read back unchanged, 2026-11-17).</summary>
    public bool MacroWrites => _model?.MacroWrites == true;

    /// <summary>A macro slot (0..49) as stored; null when a page isn't answered. Getters only.</summary>
    public RoyuanMacro? ReadMacro(int index)
    {
        CatalogModel();
        Require(index is >= 0 and < RoyuanMacro.Slots, $"Macros go from 1 to {RoyuanMacro.Slots}.");
        return With(_config, false, t => ReadMacroBlob(t, index) is { } blob ? RoyuanMacro.Decode(blob) : null);
    }

    static byte[]? ReadMacroBlob(IHidTransport t, int index)
    {
        var blob = new byte[RoyuanMacro.Bytes];
        for (var page = 0; page < 4; page++)
        {
            t.SetFeature(0, RoyuanCodec.MacroRead(index, page));
            Delay(ReplyWait);
            var r = t.GetFeature(0);
            if (!RoyuanCodec.IsMacroPage(r, index, page)) return null;
            r.AsSpan(1, 64).CopyTo(blob.AsSpan(page * 64));
        }
        return blob;
    }

    /// <summary>
    /// Stores a macro in slot 0..49 (an empty one clears it) and reads it back: the steps as sent, compared up to the end marker. Refused,
    /// with nothing sent, unless the catalog marks the board's macro writes as kept (<see cref="MacroWrites"/>; not the R82HE's). Bind it
    /// to a key with <see cref="SetKey"/> and a [09, mode, index, 0] entry (mode 0 repeat count, 1 toggle, 2 while held).
    /// </summary>
    public PeripheralState SetMacro(int index, RoyuanMacro macro)
    {
        if (!CatalogModel().MacroWrites) throw new PeripheralException("This keyboard doesn't keep macros sent by KOS Toolbox; nothing is written.");
        Require(index is >= 0 and < RoyuanMacro.Slots, $"Macros go from 1 to {RoyuanMacro.Slots}.");
        var blob = macro.Encode(); // refuses steps out of range or too long, before anything is sent
        return Write(t =>
        {
            SendPages(t, RoyuanMacro.Pages(index, blob));
            return ReadMacroBlob(t, index) is { } back && RoyuanMacro.Decode(back) is var got && got.Repeat == macro.Repeat && got.Steps.SequenceEqual(macro.Steps)
                ? null : "The keyboard shows another macro in that slot.";
        });
    }

    /// <summary>
    /// Uploads a per-key picture (126 colors in slot order) to picture slot 0 and shows it (light mode 13, speed and brightness kept).
    /// The picture can't be read back (the board's read returns old data): only the lighting mode is confirmed, which the caller must say.
    /// One upload at least 10 s after the last, pages 100 ms apart, 2 s to settle, then the board must answer Identify again: faster
    /// uploads stalled the endpoint until USB was reconnected (sharkfin's measurements).
    /// </summary>
    public PeripheralState SendPicture(IReadOnlyList<int> colors)
    {
        CatalogModel();
        var pages = RoyuanCodec.PicturePages(0, colors); // validates before anything is sent
        var light = _light ?? throw new PeripheralException("The lighting hasn't been read yet; nothing is written.");
        var since = Environment.TickCount64 - _lastPicture;
        if (since < PictureSpacingMs) throw new PeripheralException($"Wait {(PictureSpacingMs - since + 999) / 1000} s before the next picture: quick uploads can stall the keyboard.");
        With(_config, false, t =>
        {
            foreach (var page in pages)
            {
                t.SetFeature(0, page);
                Delay(PicturePageGap);
            }
            _lastPicture = Environment.TickCount64;
            Delay(PictureSettle);
            WaitReady(t);
            return true;
        });
        return light.Mode == 13 && (light.Flags >> 4) == 0 ? Read() : SetLight(light with { Mode = 13, Flags = (byte)(light.Flags & 0x0F) });
    }

    /// <summary>One keymap entry of the active profile (sub-layer 0 is the key itself), stored and read back.</summary>
    public PeripheralState SetKey(int slot, int subLayer, RoyuanKey key)
    {
        CatalogModel();
        Require(slot is >= 0 and < RoyuanCodec.Slots && subLayer is >= 0 and < 4, "Choose a key and a sub-layer.");
        return Write(t =>
        {
            var layers = ReadLayers(t, out var profile) ?? throw new PeripheralException("The keyboard didn't answer the keymap read; nothing is written.");
            var want = layers.Select(l => l[slot]).ToArray();
            want[subLayer] = key;
            return SendKeys(t, profile, slot, layers, want);
        });
    }

    static byte[]? Page(IHidTransport t, byte subop, int page)
    {
        t.SetFeature(0, RoyuanCodec.ColumnRead(subop, page));
        Delay(ReplyWait);
        var r = t.GetFeature(0);
        return RoyuanCodec.IsPage(r, subop, page) ? r : null;
    }

    /// <summary>
    /// Starts the live key travel (nothing stored): reports arrive on collection FFFF:0001 while a key moves, none while it's held
    /// still, 0 on release. Dispose turns it off; so does the process exiting.
    /// </summary>
    public RoyuanTravelStream StartTravelStream()
    {
        CatalogModel();
        var unit = RoyuanCodec.TravelUnitMm(_version!.Value);
        var input = Device.Collections.FirstOrDefault(c => c.UsagePage == RoyuanCodec.UsagePage && c.Usage == RoyuanCodec.TravelUsage && c.InputLength >= 5)
            ?? throw new PeripheralException("This keyboard has no travel collection.");
        var transport = Opener(input, true);
        try
        {
            (transport as HidHandle)?.SetInputBuffers(512); // more than ~5 keys at once overflow the default 32
            transport.FlushQueue();
            SetStream(true);
            return new RoyuanTravelStream(transport, unit, () => SetStream(false));
        }
        catch
        {
            (transport as IDisposable)?.Dispose();
            throw;
        }
    }

    void SetStream(bool on) => With(_config, false, t =>
    {
        t.SetFeature(0, RoyuanCodec.EncodeTravelStream(on));
        Delay(ReplyWait);
        return true;
    });

    RoyuanModel CatalogModel() => _model ?? throw new PeripheralException("The keyboard hasn't been identified yet; nothing is written.");

    PeripheralState Write(byte[] request, Func<IHidTransport, string?> verify) => Write(t => { Send(t, request); return verify(t); });

    /// <summary>Runs a write with its read-back (<paramref name="work"/> returns the mismatch, or null), then reads the whole state.</summary>
    PeripheralState Write(Func<IHidTransport, string?> work)
    {
        CatalogModel();
        var problem = With(_config, false, work);
        var state = Read();
        return problem is null ? state : throw new ReadBackException(problem, state);
    }

    /// <summary>A SET that stores to flash waits <see cref="WriteSpacing"/> after the previous one and <see cref="FlashWait"/> after itself.</summary>
    void Send(IHidTransport t, byte[] request, bool commits = true)
    {
        if (!commits)
        {
            t.SetFeature(0, request);
            Delay(ReplyWait);
            return;
        }
        var wait = WriteSpacing - (Environment.TickCount64 - _lastSet);
        if (wait > 0) Delay((int)wait);
        t.SetFeature(0, request);
        Delay(FlashWait);
        WaitReady(t);
        _lastSet = Environment.TickCount64;
    }

    /// <summary>
    /// While it stores to flash the board drops packets and answers every GET with the bare request (checked on an R82HE: a switch
    /// column sent 100 ms after the previous column's commit was lost). Identify answers with the board id only once it's back.
    /// </summary>
    void WaitReady(IHidTransport t)
    {
        var identify = RoyuanCodec.Request(new[] { RoyuanCodec.Identify });
        for (var i = 0; i < ReadyTries; i++)
        {
            t.SetFeature(0, identify);
            Delay(ReplyWait);
            var r = t.GetFeature(0);
            if (RoyuanCodec.Answers(r, RoyuanCodec.Identify) && RoyuanCodec.DeviceId(r) == _model!.DeviceId) return;
            Delay(ReadyPoll);
        }
        throw new PeripheralException("The keyboard is still busy storing the last change; nothing more was sent. Read it again in a moment.");
    }

    /// <summary>A getter: SET, wait, GET; the reply must echo the opcode. One retry.</summary>
    static byte[]? Ask(IHidTransport t, byte[] request, byte command)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            t.SetFeature(0, request);
            Delay(ReplyWait);
            var r = t.GetFeature(0);
            if (RoyuanCodec.Answers(r, command)) return r;
        }
        return null;
    }
}
