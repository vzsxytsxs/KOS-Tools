using KosToolbox.Core.Peripherals.Hid;

namespace KosToolbox.Core.Peripherals.Logitech;

/// <summary>A Logitech product the HID++ driver claims: a receiver (devices on slots 1–6) or a mouse on its own cable (index 0xFF).</summary>
public sealed record LogitechModel(ushort ProductId, string Name, bool Receiver);

/// <summary>
/// Our entries. Receivers: Lightspeed, Unifying and Bolt ids (Solaar's and mouse-protocol's lists, src/logitech/index.ts at
/// 5a4f6433da3d). Direct mice: mouse-protocol's LOGITECH_DIRECT_PRODUCT_IDS. None is verified on hardware by us.
/// </summary>
public static class LogitechCatalog
{
    public const ushort VendorId = 0x046D;

    public static readonly IReadOnlyList<LogitechModel> Models = new LogitechModel[]
    {
        new(0xC52B, "Logitech Unifying receiver", true), new(0xC534, "Logitech nano receiver", true),
        new(0xC539, "Logitech Lightspeed receiver", true), new(0xC53A, "Logitech Lightspeed receiver", true),
        new(0xC53F, "Logitech Lightspeed receiver", true), new(0xC541, "Logitech Lightspeed receiver", true),
        new(0xC545, "Logitech Lightspeed receiver", true), new(0xC547, "Logitech Lightspeed receiver", true),
        new(0xC548, "Logitech Bolt receiver", true),
        new(0xC07D, "Logitech G502", false), new(0xC084, "Logitech G203 Prodigy", false), new(0xC089, "Logitech G102 Lightsync", false),
        new(0xC092, "Logitech G203 Lightsync", false), new(0xC08B, "Logitech G502 HERO", false), new(0xC08C, "Logitech G Pro HERO", false),
        new(0xC08D, "Logitech G502 Lightspeed", false), new(0xC088, "Logitech G Pro Wireless", false), new(0xC090, "Logitech G703 HERO", false),
        new(0xC091, "Logitech G903 HERO", false), new(0xC094, "Logitech G Pro X Superlight", false), new(0xC095, "Logitech G502 X Plus", false),
        new(0xC098, "Logitech G502 X Lightspeed", false), new(0xC099, "Logitech G502 X", false), new(0xC09A, "Logitech G Pro 2 Lightspeed", false),
        new(0xC09B, "Logitech Pro X Superlight 2", false),
    };

    public static LogitechModel? Find(ushort vendorId, ushort productId) =>
        vendorId == VendorId ? Models.FirstOrDefault(m => m.ProductId == productId) : null;

    /// <summary>The short (FF00:1, 7 bytes) and long (FF00:2 or Bluetooth FF43:0202, 20 bytes) HID++ collections.</summary>
    public static (HidCollection? Short, HidCollection? Long) Collections(HidDevice device) => (
        device.Collections.FirstOrDefault(c => c.UsagePage == 0xFF00 && c.Usage == 0x0001 && c.OutputLength == HidppCodec.ShortLength),
        device.Collections.FirstOrDefault(c => (c.UsagePage == 0xFF00 && c.Usage == 0x0002 || c.UsagePage == 0xFF43 && c.Usage == 0x0202)
                                               && c.OutputLength == HidppCodec.LongLength));
}

/// <summary>
/// Logitech HID++ 2.0 mice: name, firmware, battery (0x1004, 0x1000, 0x1001), DPI (0x2201), report rate (0x8060) and the onboard
/// profile mode (0x8100). HID++ 1.0 devices give their battery (register 0x0D) only. Live writes only in host mode, or on a mouse on its
/// own cable (which takes 0x2201 in either mode, per mouse-protocol's ensureHostControl); onboard profile sectors are never written.
/// </summary>
public sealed class HidppDriver : PeripheralDriver
{
    const int TimeoutMs = 500, SliceMs = 15;
    readonly LogitechModel _model;
    readonly HidCollection? _short, _long;
    readonly Dictionary<ushort, byte> _features = new();
    byte? _index;
    bool _v1;
    PeripheralState? _last;

    public HidppDriver(HidDevice device, LogitechModel model) : base(device, "Logitech HID++", model.Name, tested: false)
    {
        _model = model;
        (_short, _long) = LogitechCatalog.Collections(device);
        if (_short is null && _long is null) throw new ArgumentException("This device has no HID++ collection.");
    }

    public override PeripheralKind Kind => _model.Receiver || Device.Kind == PeripheralKind.Other ? PeripheralKind.Mouse : Device.Kind;
    public override PeripheralConnection? Connection => Device.Bluetooth ? PeripheralConnection.Bluetooth : _model.Receiver ? PeripheralConnection.Receiver : PeripheralConnection.Wired;

    bool LiveWrites => !_model.Receiver || _last?.OnboardMode == false;

    public override IReadOnlySet<PeripheralSetting> Writable
    {
        get
        {
            var set = new HashSet<PeripheralSetting>();
            if (_last is not { } s || _v1) return set;
            if (s.OnboardMode == true && _model.Receiver) set.Add(PeripheralSetting.HostMode);
            if (!LiveWrites) return set;
            if (s.Dpi is not null) set.Add(PeripheralSetting.Dpi);
            if (s.Polling is not null) set.Add(PeripheralSetting.PollingRate);
            return set;
        }
    }

    public override bool HasLiveDpi => true;

    public override PeripheralState Read() => Run(s =>
    {
        if (!s.Find())
            return new PeripheralState { Name = _model.Name, Battery = _model.Receiver ? new PeripheralBattery(null, null, Asleep: true) : null };
        if (_v1) return _last = new PeripheralState { Name = _model.Name, Battery = s.Battery() };
        PeripheralDpi? dpi = null;
        if (s.Feature(HidppCodec.AdjustableDpi) is { } fd && s.Call(fd, 2, 0) is { } cur)
        {
            var range = s.Call(fd, 1, 0) is { } list ? HidppCodec.DpiRange(list.AsSpan(1)) : null;
            dpi = new PeripheralDpi(cur[1] << 8 | cur[2], cur[1] << 8 | cur[2], range?.Min ?? 100, range?.Max ?? 25600, range?.Step ?? 50);
        }
        PeripheralPolling? polling = null;
        if (s.Feature(HidppCodec.ReportRate) is { } fr && s.Call(fr, 1) is { } ms && ms[0] > 0)
            polling = new PeripheralPolling(1000 / ms[0], s.Call(fr, 0) is { } bits ? HidppCodec.Rates(bits[0]) : new List<int> { 1000 / ms[0] });
        bool? onboard = s.Feature(HidppCodec.OnboardProfiles) is { } fo && s.Call(fo, 2) is { } mode ? mode[0] == 1 : null;
        return _last = new PeripheralState
        {
            Name = s.Name() ?? _model.Name, Firmware = s.Firmware(), Battery = s.Battery(), Dpi = dpi, Polling = polling, OnboardMode = onboard,
        };
    });

    public override PeripheralBattery? ReadBattery() => Run(s => s.Find() ? s.Battery() : _model.Receiver ? new PeripheralBattery(null, null, Asleep: true) : null);

    public override PeripheralState SetDpi(int x, int y) => SetDpiLive(x, y);

    public override PeripheralState SetDpiLive(int x, int y)
    {
        var d = _last?.Dpi ?? throw new PeripheralException("The DPI hasn't been read yet.");
        Require(x == y && x >= d.Min && x <= d.Max && (x - d.Min) % d.Step == 0, $"DPI goes from {d.Min} to {d.Max} in steps of {d.Step}.");
        return Write(s =>
        {
            var f = s.Feature(HidppCodec.AdjustableDpi)!.Value;
            s.Call(f, 3, 0, (byte)(x >> 8), (byte)x, once: true);
            var now = s.Call(f, 2, 0) is { } r ? r[1] << 8 | r[2] : -1;
            return now == x ? null : $"The mouse shows {now} DPI instead of {x}.";
        });
    }

    public override PeripheralState SetPollingRate(int hz)
    {
        var p = _last?.Polling ?? throw new PeripheralException("The report rate hasn't been read yet.");
        Require(p.Supported.Contains(hz), $"This mouse takes {string.Join(", ", p.Supported)} Hz.");
        return Write(s =>
        {
            var f = s.Feature(HidppCodec.ReportRate)!.Value;
            s.Call(f, 2, (byte)(1000 / hz), once: true);
            var now = s.Call(f, 1) is { } r && r[0] > 0 ? 1000 / r[0] : -1;
            return now == hz ? null : $"The mouse shows {now} Hz instead of {hz} Hz.";
        });
    }

    /// <summary>Host mode (0x8100 fn 1, mode 2): the live settings stop coming from the onboard profile. The profile itself isn't written.</summary>
    public override PeripheralState SetHostMode() => Write(s =>
    {
        var f = s.Feature(HidppCodec.OnboardProfiles) ?? throw Unsupported();
        s.Call(f, 1, 2, once: true);
        return s.Call(f, 2) is { } m && m[0] == 2 ? null : "The mouse stayed in onboard profile mode.";
    }, requireLive: false);

    PeripheralState Write(Func<Session, string?> work, bool requireLive = true)
    {
        if (_index is null || _last is null) throw new PeripheralException("The mouse hasn't answered a read yet; nothing is written.");
        if (requireLive && !LiveWrites) throw new PeripheralException("The mouse is in onboard profile mode; switch it to host mode first.");
        var problem = Run(work);
        var state = Read();
        return problem is null ? state : throw new ReadBackException(problem, state);
    }

    T Run<T>(Func<Session, T> work)
    {
        var shortT = _short is null ? null : Opener(_short, true);
        try
        {
            var longT = _long is null ? null : Opener(_long, true);
            try { return work(new Session(this, shortT, longT)); }
            finally { (longT as IDisposable)?.Dispose(); }
        }
        finally { (shortT as IDisposable)?.Dispose(); }
    }

    /// <summary>One open of the collections: requests go out long when the device has a long collection, replies are read from both.</summary>
    sealed class Session(HidppDriver driver, IHidTransport? shortT, IHidTransport? longT)
    {
        byte Index => driver._index!.Value;

        /// <summary>The device index that answers a ping (0xFF on a cable, slots 1–6 behind a receiver), and whether it speaks HID++ 2.0.</summary>
        public bool Find()
        {
            if (driver._index is not null) return true;
            foreach (byte i in driver._model.Receiver ? new byte[] { 1, 2, 3, 4, 5, 6 } : new byte[] { HidppCodec.Direct })
            {
                var (kind, code, p) = Exchange(i, 0, 1, new byte[] { 0, 0, 0x5A }, getter: true);
                if (kind == HidppMatch.Reply && p.Length >= 1 && p[0] >= 2) { driver._index = i; driver._v1 = false; return true; }
                if (kind == HidppMatch.Error && code == 0x01) { driver._index = i; driver._v1 = true; return true; } // "invalid sub id": HID++ 1.0
            }
            return false;
        }

        public byte? Feature(ushort id)
        {
            if (driver._features.TryGetValue(id, out var known)) return known == 0 ? null : known;
            var r = Call(0, 0, (byte)(id >> 8), (byte)id);
            if (r is null) return null;
            driver._features[id] = r[0];
            return r[0] == 0 ? null : r[0];
        }

        public byte[]? Call(byte feature, byte function, params byte[] parameters) => Call(feature, function, parameters, once: false);

        public byte[]? Call(byte feature, byte function, byte p0, bool once) => Call(feature, function, new[] { p0 }, once);
        public byte[]? Call(byte feature, byte function, byte p0, byte p1, byte p2, bool once) => Call(feature, function, new[] { p0, p1, p2 }, once);

        byte[]? Call(byte feature, byte function, byte[] parameters, bool once)
        {
            var (kind, _, p) = Exchange(Index, feature, function, parameters, getter: !once);
            if (kind == HidppMatch.Reply) return p;
            if (once && kind == HidppMatch.Other) throw new PeripheralException("The mouse didn't confirm the change; it wasn't sent again. Read it again to see what it has.");
            return null;
        }

        public PeripheralBattery? Battery()
        {
            if (driver._v1)
            {
                var (kind, _, p) = Exchange(Index, 0x81, 0, new byte[] { }, getter: true, register: 0x0D);
                return kind == HidppMatch.Reply ? HidppCodec.Register0D(p) : null;
            }
            if (Feature(HidppCodec.UnifiedBattery) is { } u && Call(u, 1) is { } pu) return HidppCodec.Unified(pu);
            if (Feature(HidppCodec.BatteryStatus) is { } b && Call(b, 0) is { } pb) return HidppCodec.Status(pb);
            if (Feature(HidppCodec.BatteryVoltage) is { } v && Call(v, 0) is { } pv) return HidppCodec.Voltage(pv);
            return null;
        }

        public string? Name()
        {
            if (longT is null || Feature(HidppCodec.DeviceName) is not { } f || Call(f, 0) is not { } len) return null;
            var name = new List<byte>();
            while (name.Count < len[0] && Call(f, 1, (byte)name.Count) is { } chunk)
            {
                var before = name.Count;
                foreach (var c in chunk) { if (c == 0 || name.Count >= len[0]) break; name.Add(c); }
                if (name.Count == before) break;
            }
            var text = System.Text.Encoding.ASCII.GetString(name.ToArray()).Trim();
            return text.Length == 0 ? null : text.StartsWith("Logitech", StringComparison.OrdinalIgnoreCase) ? text : "Logitech " + text;
        }

        public string? Firmware()
        {
            if (Feature(HidppCodec.FirmwareInfo) is not { } f || Call(f, 0) is not { } count) return null;
            for (byte e = 0; e < Math.Min((int)count[0], 8); e++)
                if (Call(f, 1, e) is { } info && info[0] == 0) return HidppCodec.Firmware(info);
            return null;
        }

        /// <summary>
        /// One request, then replies read from both collections until the matching one (or an error naming it) or the timeout; other
        /// programs' replies and notifications are skipped. A getter is asked once more after a timeout; a setter never.
        /// <paramref name="register"/>: a HID++ 1.0 register read (sub id 0x81), sent short.
        /// </summary>
        (HidppMatch Kind, byte Code, byte[] Params) Exchange(byte device, byte feature, byte function, byte[] parameters, bool getter, byte? register = null)
        {
            for (var attempt = 0; attempt < (getter ? 2 : 1); attempt++)
            {
                byte[] payload;
                byte matchFunction = function;
                if (register is { } reg) { payload = new byte[] { device, feature, reg, 0, 0, 0 }; }
                else payload = HidppCodec.Request(device, feature, function, parameters);
                var useLong = longT is not null && register is null;
                var target = useLong ? longT! : shortT ?? longT!;
                shortT?.FlushQueue();
                longT?.FlushQueue();
                target.Write(useLong ? HidppCodec.LongId : HidppCodec.ShortId, payload);
                var end = Environment.TickCount64 + TimeoutMs;
                while (Environment.TickCount64 < end)
                    foreach (var t in new[] { longT, shortT })
                    {
                        if (t?.Read(SliceMs) is not { } report) continue;
                        var m = register is { } r ? MatchRegister(report, device, feature, r) : HidppCodec.Match(report, device, feature, matchFunction);
                        if (m.Kind != HidppMatch.Other) return m;
                    }
            }
            return (HidppMatch.Other, 0, Array.Empty<byte>());
        }

        static (HidppMatch Kind, byte Code, byte[] Params) MatchRegister(byte[] report, byte device, byte subId, byte register)
        {
            if (report.Length < 7 || report[1] != device) return (HidppMatch.Other, 0, Array.Empty<byte>());
            if (report[2] == subId && report[3] == register) return (HidppMatch.Reply, 0, report[4..]);
            if (report[2] == 0x8F && report[3] == subId && report[4] == register) return (HidppMatch.Error, report[5], Array.Empty<byte>());
            return (HidppMatch.Other, 0, Array.Empty<byte>());
        }
    }
}
