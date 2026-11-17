using KosToolbox.Core.Peripherals;
using KosToolbox.Core.Peripherals.Batteries;
using KosToolbox.Core.Peripherals.Hid;
using KosToolbox.Core.Peripherals.Keyboards;
using KosToolbox.Core.Peripherals.Logitech;
using KosToolbox.Core.Peripherals.Odm;
using KosToolbox.Core.Peripherals.Razer;
using Xunit;

namespace KosToolbox.Core.Tests;

/// <summary>A transport that answers each feature request through a script and records what was sent.</summary>
sealed class FakeTransport(HidCollection collection, Func<byte[], int, byte[]> reply) : IHidTransport
{
    public List<byte[]> Sent { get; } = new();
    byte[] _last = Array.Empty<byte>();
    int _gets;
    public HidCollection Collection { get; } = collection;
    public void SetFeature(byte reportId, ReadOnlySpan<byte> payload) { _last = HidBuffer.Build(reportId, payload, Collection.FeatureLength); Sent.Add(_last); _gets = 0; }
    public byte[] GetFeature(byte reportId) => reply(_last, _gets++);
    public void Write(byte reportId, ReadOnlySpan<byte> payload, int timeoutMs = 500) => throw new NotSupportedException();
    public byte[]? Read(int timeoutMs = 500) => null;
    public byte[] GetInputReport(byte reportId) => throw new NotSupportedException();
    public void FlushQueue() { }
}

[Collection("PeripheralDriver statics")]
public class PeripheralDriversTests
{
    static HidCollection Col(ushort vid, ushort pid, string hw, ushort page, ushort usage, int input, int output, int feature) =>
        new($@"\\?\hid#vid_{vid:x4}&pid_{pid:x4}&{hw}#8&1&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}", vid, pid, 0x0200, page, usage,
            input, output, feature, "Sample", Guid.NewGuid(), false);

    // This PC's Viper Ultimate receiver: the 91-byte feature report is on MI_00, the mouse collection (phase-1 finding).
    static HidDevice Viper(ushort pid = 0x007B) => new("viper", new[]
    {
        Col(0x1532, pid, "mi_00", 1, 2, 8, 0, 91), Col(0x1532, pid, "mi_01&col01", 1, 6, 9, 2, 0), Col(0x1532, pid, "mi_02", 1, 6, 9, 0, 0),
    });

    /// <summary>A device reply to the last request: status, then the request's own header and the given arguments, checksum fixed.</summary>
    static byte[] Reply(byte[] request, RazerStatus status, params byte[] args)
    {
        var r = (byte[])request.Clone();
        r[1] = (byte)status;
        args.CopyTo(r, 1 + RazerCodec.ArgsOffset);
        r[1 + 88] = RazerCodec.Checksum(r.AsSpan(1));
        return r;
    }

    static RazerDriver Driver(Func<byte[], int, byte[]> script, out List<byte[]> sent, ushort pid = 0x007B)
    {
        PeripheralDriver.Delay = _ => { };
        var log = new List<byte[]>();
        sent = log;
        PeripheralDriver.Opener = (c, _) => { var t = new FakeTransport(c, script); return new LoggingTransport(t, log); };
        return new RazerDriver(Viper(pid), RazerCatalog.Find(0x1532, pid)!);
    }

    sealed class LoggingTransport(FakeTransport inner, List<byte[]> log) : IHidTransport
    {
        public HidCollection Collection => inner.Collection;
        public void SetFeature(byte id, ReadOnlySpan<byte> p) { inner.SetFeature(id, p); log.Add(inner.Sent[^1]); }
        public byte[] GetFeature(byte id) => inner.GetFeature(id);
        public void Write(byte id, ReadOnlySpan<byte> p, int t = 500) => inner.Write(id, p, t);
        public byte[]? Read(int t = 500) => inner.Read(t);
        public byte[] GetInputReport(byte id) => inner.GetInputReport(id);
        public void FlushQueue() { }
    }

    /// <summary>A Viper on transaction id 3F with these values; other ids get no answer (04).</summary>
    static Func<byte[], int, byte[]> Mouse(Dictionary<(byte, byte), byte[]> values, byte tid = 0x3F) => (req, _) =>
        req[2] != tid ? Reply(req, RazerStatus.NoAnswer)
        : values.TryGetValue((req[7], req[8]), out var a) ? Reply(req, RazerStatus.Ok, a) : Reply(req, RazerStatus.Unsupported);

    static Dictionary<(byte, byte), byte[]> ViperValues() => new()
    {
        [(0x00, 0x81)] = new byte[] { 1, 4 },
        [(0x07, 0x80)] = new byte[] { 0, 0xC0 },  // 192/255 → 75 %
        [(0x07, 0x84)] = new byte[] { 0, 1 },
        [(0x04, 0x85)] = new byte[] { 0, 0x03, 0x20, 0x03, 0x20 }, // 800×800
        [(0x00, 0x85)] = new byte[] { 1 },
        [(0x07, 0x83)] = new byte[] { 0x01, 0x2C }, // 300 s
        [(0x07, 0x81)] = new byte[] { 0x4D, 0 },    // 30 %
    };

    [Fact]
    public void RazerRequestHasTheHeaderAndXorChecksum()
    {
        var r = RazerCodec.Request(0x3F, RazerCodec.Battery);
        Assert.Equal(90, r.Length);
        Assert.Equal(new byte[] { 0x00, 0x3F, 0, 0, 0, 0x02, 0x07, 0x80 }, r[..8]);
        Assert.Equal(0x02 ^ 0x07 ^ 0x80, r[88]);
        Assert.Equal(0, r[89]);
        Assert.Equal((byte)(0x07 ^ 0x04 ^ 0x05 ^ 0x01 ^ 0x03 ^ 0x20 ^ 0x03 ^ 0x20), RazerCodec.Request(0x3F, RazerCodec.DpiSet, RazerCodec.EncodeDpi(1, 800, 800))[88]);
    }

    [Theory]
    [InlineData(255, 100)] [InlineData(0, 0)] [InlineData(0xC0, 75)] [InlineData(0x4D, 30)] [InlineData(0xD9, 85)]
    public void RazerBatteryScale(byte level, int percent)
    {
        Assert.Equal(percent, RazerCodec.Percent(level));
        Assert.Equal(percent, RazerCodec.Percent(RazerCodec.Level(percent))); // a written threshold reads back the same
    }

    [Fact]
    public void RazerStagesKeepEveryByteButTheActiveOne()
    {
        var table = new byte[0x26];
        table[0] = 1; table[1] = 2; table[2] = 3;
        for (var i = 0; i < 3; i++) { var o = 3 + i * 7; table[o] = (byte)i; table[o + 1] = 0x03; table[o + 2] = (byte)(0x20 + i); table[o + 3] = 0x03; table[o + 4] = (byte)(0x20 + i); table[o + 5] = 0xAB; }
        var (active, stages) = RazerCodec.DecodeStages(table);
        Assert.Equal(2, active);
        Assert.Equal(new DpiStage(0x321, 0x321), stages[1]);
        var copy = RazerCodec.WithActiveStage(table, 3);
        Assert.Equal(3, copy[1]);
        copy[1] = 2;
        Assert.Equal(table, copy); // unknown bytes (0xAB) kept as read
        Assert.Throws<ArgumentOutOfRangeException>(() => RazerCodec.WithActiveStage(table, 4));
    }

    [Fact]
    public void RazerPollingAndSleepCodecs()
    {
        Assert.Equal(500, RazerCodec.DecodePolling(new byte[] { 2 }));
        Assert.Equal(4000, RazerCodec.DecodePollingExtended(new byte[] { 0, 2 }));
        Assert.Equal(8, RazerCodec.Divisor(1000, 125));
        Assert.Throws<ArgumentOutOfRangeException>(() => RazerCodec.Divisor(1000, 300));
        Assert.Equal(new byte[] { 0x01, 0x2C }, RazerCodec.EncodeSleep(300));
        Assert.Equal("1.4", RazerCodec.DecodeFirmware(new byte[] { 1, 4 }));
    }

    [Fact]
    public void RazerReadsTheViperOnItsCatalogId()
    {
        var d = Driver(Mouse(ViperValues()), out var sent);
        var s = d.Read();
        Assert.Equal((byte)0x3F, d.TransactionId);
        Assert.Equal(new PeripheralBattery(75, true), s.Battery);
        Assert.Equal((800, 800), (s.Dpi!.X, s.Dpi.Y));
        Assert.Equal(1000, s.Polling!.Hz); // extended unsupported → legacy
        Assert.Equal((300, 30, "1.4"), (s.SleepSeconds!.Value, s.LowBatteryPercent!.Value, s.Firmware));
        Assert.All(sent, r => Assert.True((r[8] & 0x80) != 0, "only getters while reading"));
        Assert.Equal(RazerCodec.NoStore, sent.First(r => r[7] == 0x04 && r[8] == 0x85)[9]); // the Viper reads its DPI with the no-store byte
    }

    [Fact]
    public void RazerFallsBackToAnotherIdWithGettersOnly()
    {
        var d = Driver(Mouse(ViperValues(), tid: 0xFF), out var sent);
        Assert.Equal(75, d.Read().Battery!.Percent);
        Assert.Equal((byte)0xFF, d.TransactionId);
        Assert.All(sent, r => Assert.True((r[8] & 0x80) != 0));
    }

    [Fact]
    public void RazerAsleepShowsAsleepAndWritesNothing()
    {
        var d = Driver((req, _) => Reply(req, RazerStatus.NoAnswer), out var sent);
        var s = d.Read();
        Assert.True(s.Battery!.Asleep);
        Assert.Empty(d.Writable);
        Assert.Throws<PeripheralException>(() => d.SetPollingRate(500));
        Assert.All(sent, r => Assert.True((r[8] & 0x80) != 0));
    }

    [Fact]
    public void RazerBusyThenOk()
    {
        var values = ViperValues();
        var d = Driver((req, n) => n < 2 ? Reply(req, RazerStatus.Busy) : Mouse(values)(req, n), out _);
        Assert.Equal(75, d.ReadBattery()!.Percent);
    }

    [Fact]
    public void RazerStaleReplyToAGetterIsAskedAgain()
    {
        var values = ViperValues();
        var stale = 0;
        var d = Driver((req, n) =>
        {
            if (req[7] == 0x07 && req[8] == 0x80 && stale++ == 0) { var other = (byte[])req.Clone(); other[8] = 0x84; return Reply(other, RazerStatus.Ok, 0, 1); }
            return Mouse(values)(req, n);
        }, out var sent);
        Assert.Equal(75, d.ReadBattery()!.Percent);
        Assert.Equal(2, sent.Count(r => r[7] == 0x07 && r[8] == 0x80));
    }

    [Fact]
    public void RazerSetterIsSentOnceAndReadBack()
    {
        var values = ViperValues();
        var staleSet = false;
        var d = Driver((req, n) =>
        {
            if (req[7] == 0x00 && req[8] == 0x05) // legacy polling set: applies it, answers as configured
            {
                values[(0x00, 0x85)] = new[] { req[9] };
                if (staleSet) { var other = (byte[])req.Clone(); other[8] = 0x85; return Reply(other, RazerStatus.Ok); }
                return Reply(req, RazerStatus.Ok);
            }
            return Mouse(values)(req, n);
        }, out var sent);
        d.Read();
        Assert.Contains(PeripheralSetting.PollingRate, d.Writable);
        Assert.Equal(500, d.SetPollingRate(500).Polling!.Hz);
        Assert.Equal(1, sent.Count(r => r[8] == 0x05));

        staleSet = true; // a lost reply: the change isn't sent again
        Assert.Throws<PeripheralException>(() => d.SetPollingRate(125));
        Assert.Equal(2, sent.Count(r => r[8] == 0x05));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.SetPollingRate(300));
    }

    [Fact]
    public void RazerReadBackMismatchReportsWhatTheMouseHas()
    {
        var values = ViperValues();
        var d = Driver((req, n) => req[7] == 0x07 && req[8] == 0x03 ? Reply(req, RazerStatus.Ok) : Mouse(values)(req, n), out _); // takes it, keeps 300
        d.Read();
        var e = Assert.Throws<ReadBackException>(() => d.SetSleep(600));
        Assert.Equal(300, e.State.SleepSeconds);
        Assert.Throws<ArgumentOutOfRangeException>(() => d.SetSleep(10));
    }

    /// <summary>
    /// A light on <paramref name="led"/> with separate stored and live values (as the Viper Ultimate and the Mouse Dock answer): brightness
    /// 0F/84-0F/04 and the effect 0F/82-0F/02, each by its storage byte. Other commands go to <paramref name="other"/>.
    /// </summary>
    static Func<byte[], int, byte[]> Light(byte led, Dictionary<byte, byte> level, Dictionary<byte, byte[]> effect, Func<byte[], int, byte[]> other) => (req, n) =>
    {
        const int a = 1 + RazerCodec.ArgsOffset;
        if (req[7] != 0x0F || req[a + 1] != led) return other(req, n);
        var storage = req[a];
        switch (req[8])
        {
            case 0x04: level[storage] = req[a + 2]; return Reply(req, RazerStatus.Ok);
            case 0x84: return Reply(req, RazerStatus.Ok, storage, led, level[storage]);
            case 0x02: effect[storage] = req[(a + 2)..(a + req[6])]; return Reply(req, RazerStatus.Ok);
            case 0x82: return Reply(req, RazerStatus.Ok, new byte[] { 0, led }.Concat(effect[storage]).ToArray());
            default: return Reply(req, RazerStatus.Unsupported);
        }
    };

    static Dictionary<byte, byte[]> Spectrum() => new() { [RazerCodec.Stored] = new byte[] { 3 }, [RazerCodec.NoStore] = new byte[] { 3 } };

    [Fact]
    public void MouseDockReadsAndWritesOnlyItsRingLight()
    {
        var level = new Dictionary<byte, byte> { [RazerCodec.Stored] = 0, [RazerCodec.NoStore] = 0 };
        var effect = Spectrum();
        var d = Driver(Light(0x05, level, effect, (req, _) => (req[7], req[8]) switch
        {
            (0x00, 0x81) => Reply(req, RazerStatus.Ok, 1, 2),
            (0x00, 0x82) => Reply(req, RazerStatus.Ok),
            _ => Reply(req, RazerStatus.Unsupported),
        }), out var sent, 0x007E);
        var s = d.Read();
        Assert.Equal("1.2", s.Firmware);
        Assert.Equal(0, s.Brightness);
        Assert.Equal(new PeripheralLighting(LightingEffect.Spectrum, null), s.Lighting);
        Assert.Null(s.Dpi);
        Assert.Null(d.ReadBattery());
        Assert.False(d.HasLiveDpi);
        Assert.True(d.IsDock);
        Assert.Equal(new[] { PeripheralSetting.Brightness, PeripheralSetting.Lighting }, d.Writable.Order());
        Assert.All(sent, r => Assert.Contains((r[7], r[8]), new[] { ((byte)0x00, (byte)0x81), ((byte)0x00, (byte)0x82), ((byte)0x0F, (byte)0x84), ((byte)0x0F, (byte)0x82) })); // no DPI, polling or battery
        Assert.Equal(50, d.SetBrightness(50).Brightness);
        Assert.Equal((RazerCodec.Level(50), RazerCodec.Level(50)), (level[RazerCodec.Stored], level[RazerCodec.NoStore])); // stored and live
    }

    [Fact]
    public void RazerEffectCodec()
    {
        var (command, args) = RazerCodec.EncodeEffect(RazerCodec.Stored, 0x05, LightingEffect.Static, 0xFF8000);
        Assert.Equal(new RazerCommand(0x0F, 0x02, 0x09), command);
        Assert.Equal(new byte[] { 1, 5, 1, 0, 0, 1, 0xFF, 0x80, 0x00 }, args);
        Assert.Equal(new byte[] { 0, 4, 2, 1, 0, 1, 0, 0xFF, 0 }, RazerCodec.EncodeEffect(RazerCodec.NoStore, 0x04, LightingEffect.Breathing, 0x00FF00).Args);
        Assert.Equal(new byte[] { 1, 4, 3 }, RazerCodec.EncodeEffect(RazerCodec.Stored, 0x04, LightingEffect.Spectrum, 0x123456).Args);
        Assert.Equal(new RazerCommand(0x0F, 0x02, 0x06), RazerCodec.EncodeEffect(RazerCodec.Stored, 0x04, LightingEffect.Off, 0).Command);
        // Replies as read on the hardware: static red on the dock; the Viper's live spectrum with leftover bytes after the effect.
        Assert.Equal(new PeripheralLighting(LightingEffect.Static, 0xFF0000), RazerCodec.DecodeEffect(new byte[] { 0, 5, 1, 0, 0, 1, 0xFF, 0, 0, 0, 0, 0 }));
        Assert.Equal(new PeripheralLighting(LightingEffect.Spectrum, null), RazerCodec.DecodeEffect(new byte[] { 0, 4, 3, 0, 0x28, 1, 0, 0xFF, 0, 0, 0xFF, 0 }));
        Assert.Equal(LightingEffect.Other, RazerCodec.DecodeEffect(new byte[] { 0, 4, 2, 2, 0, 2, 1, 2, 3, 4, 5, 6 }).Effect); // two-color breathing
        Assert.Equal(LightingEffect.Other, RazerCodec.DecodeEffect(new byte[] { 0, 4, 4, 1, 0x28, 0, 0, 0, 0, 0, 0, 0 }).Effect); // wave
    }

    [Fact]
    public void ViperLightingAndDpiAreWrittenStoredAndLive()
    {
        var values = ViperValues();
        var dpi = new Dictionary<byte, byte[]> { [RazerCodec.Stored] = new byte[] { 1, 0x03, 0x20, 0x03, 0x20 }, [RazerCodec.NoStore] = new byte[] { 0, 0x06, 0x40, 0x06, 0x40 } };
        var level = new Dictionary<byte, byte> { [RazerCodec.Stored] = 255, [RazerCodec.NoStore] = 0 }; // as found: 100 % stored, dark live
        var effect = Spectrum();
        var d = Driver(Light(0x04, level, effect, (req, n) =>
        {
            const int a = 1 + RazerCodec.ArgsOffset;
            if (req[7] == 0x04 && req[8] == 0x05) { dpi[req[a]] = req[a..(a + 5)]; return Reply(req, RazerStatus.Ok); }
            if (req[7] == 0x04 && req[8] == 0x85) return Reply(req, RazerStatus.Ok, dpi[req[a]]);
            return Mouse(values)(req, n);
        }), out var sent);
        var s = d.Read();
        Assert.Equal(1600, s.Dpi!.X); // the Viper shows its live DPI
        Assert.Equal(100, s.Brightness); // and its stored brightness
        Assert.Contains(PeripheralSetting.Lighting, d.Writable);

        Assert.Equal(900, d.SetDpi(900, 900).Dpi!.X);
        Assert.Equal(new[] { RazerCodec.Stored, RazerCodec.NoStore }, sent.Where(r => r[7] == 0x04 && r[8] == 0x05).Select(r => r[9]));
        Assert.Equal((900, 900), (RazerCodec.DecodeDpi(dpi[RazerCodec.Stored]).X, RazerCodec.DecodeDpi(dpi[RazerCodec.NoStore]).X));

        Assert.Equal(new PeripheralLighting(LightingEffect.Static, 0x00FF00), d.SetLighting(LightingEffect.Static, 0x00FF00).Lighting);
        Assert.Equal(effect[RazerCodec.Stored], effect[RazerCodec.NoStore]);
        // Live only (the engine's battery color): the stored effect stays as it was
        Assert.Equal(new PeripheralLighting(LightingEffect.Static, 0x00FF00), d.SetLightingLive(LightingEffect.Breathing, 0x0000FF).Lighting);
        Assert.Equal(new PeripheralLighting(LightingEffect.Breathing, 0x0000FF), RazerCodec.DecodeEffect(new byte[] { 0, 4 }.Concat(effect[RazerCodec.NoStore]).ToArray()));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.SetLighting(LightingEffect.Other, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.SetLighting(LightingEffect.Static, 0x1000000));

        Assert.Equal(40, d.SetBrightness(40).Brightness);
        Assert.Equal(level[RazerCodec.Stored], level[RazerCodec.NoStore]); // the live light changes too

        // A device that takes the stored brightness but keeps its live one: reported, not hidden
        var light = Light(0x04, level, effect, Mouse(values));
        var stuck = Driver((req, n) => req[7] == 0x0F && req[8] == 0x04 && req[9] == RazerCodec.NoStore ? Reply(req, RazerStatus.Ok) : light(req, n), out _);
        stuck.Read();
        Assert.Throws<ReadBackException>(() => stuck.SetBrightness(70));
    }

    [Theory]
    [InlineData(0, 0xFF0000)] [InlineData(50, 0xFFFF00)] [InlineData(100, 0x00FF00)] [InlineData(25, 0xFF7F00)]
    public void DockBatteryColorGoesFromRedToGreen(int percent, int color) => Assert.Equal(color, DockBatteryColor.For(percent));

    // ---------- registry ----------
    [Fact]
    public void RegistryClaimsOnlyExactProductsAndShapes()
    {
        PeripheralDriver.Opener = (_, _) => throw new InvalidOperationException("claiming never opens a device");
        Assert.IsType<RazerDriver>(PeripheralRegistry.Claim(Viper()));
        Assert.IsType<RazerDriver>(PeripheralRegistry.Claim(Viper(0x007A)));
        Assert.Equal(PeripheralKind.Other, Assert.IsType<RazerDriver>(PeripheralRegistry.Claim(Viper(0x007E))).Kind); // the Mouse Dock: light only
        Assert.Null(PeripheralRegistry.Claim(Viper(0x00A4))); // the Mouse Dock Pro
        Assert.Null(PeripheralRegistry.Claim(new HidDevice("k", new[] { Col(0x1532, 0x007B, "mi_00", 1, 2, 8, 0, 0) }))); // no 91-byte collection
        var keyboard = new HidDevice("kb", new[]
        {
            Col(0x3151, 0x502D, "mi_00", 1, 6, 9, 0, 0), Col(0x3151, 0x502D, "mi_01&col01", 0xFFFF, 1, 65, 65, 65),
            Col(0x3151, 0x502D, "mi_02", ViaCodec.UsagePage, ViaCodec.Usage, 33, 33, 0), // even a VIA-shaped collection
        });
        Assert.Null(PeripheralRegistry.Claim(keyboard));
        Assert.IsType<ViaDriver>(PeripheralRegistry.Claim(new HidDevice("via", new[] { Col(0xFEED, 0x0001, "mi_01", ViaCodec.UsagePage, ViaCodec.Usage, 33, 33, 0) })));
        Assert.Null(PeripheralRegistry.Claim(new HidDevice("u", new[] { Col(0x1234, 0x5678, "mi_00", 0xFF00, 1, 65, 65, 65) })));
    }

    [Fact]
    public void OdmCatalogsStayDisjoint()
    {
        var all = OdmBatteryDriver.Feature64Models.Concat(OdmBatteryDriver.Report8Models).Select(m => (m.VendorId, m.ProductId))
            .Concat(RazerCatalog.Models.Select(m => (RazerCatalog.VendorId, m.ProductId)))
            .Concat(VendorBatteryDriver.Models.SelectMany(m => m.ProductIds.Select(p => (m.VendorId, p)))).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.DoesNotContain((ushort)0x3151, all.Select(a => a.Item1));
    }

    // ---------- HID++ ----------
    [Fact]
    public void HidppMatchesRepliesAndErrors()
    {
        Assert.Equal(new byte[] { 0xFF, 0x05, 0x1B, 0x22 }, HidppCodec.Request(0xFF, 0x05, 1, new byte[] { 0x22 }));
        var reply = new byte[] { 0x11, 0xFF, 0x05, 0x1B, 0x40, 0x0C, 0x02 };
        Assert.Equal((HidppMatch.Reply, (byte)0), (HidppCodec.Match(reply, 0xFF, 5, 1).Kind, HidppCodec.Match(reply, 0xFF, 5, 1).Code));
        Assert.Equal(HidppMatch.Other, HidppCodec.Match(reply, 0xFF, 5, 2).Kind); // another function
        Assert.Equal(HidppMatch.Other, HidppCodec.Match(new byte[] { 0x11, 0xFF, 0x05, 0x10, 0, 0, 0 }, 0xFF, 5, 1).Kind); // software id 0: a notification
        var error = HidppCodec.Match(new byte[] { 0x11, 0x01, 0xFF, 0x05, 0x1B, 0x07, 0 }, 1, 5, 1);
        Assert.Equal((HidppMatch.Error, (byte)7), (error.Kind, error.Code));
        Assert.Equal(HidppMatch.Error, HidppCodec.Match(new byte[] { 0x10, 0x01, 0x8F, 0x00, 0x1B, 0x01, 0 }, 1, 0, 1).Kind); // HID++ 1.0
    }

    [Fact]
    public void HidppBatteryAndSettingDecoders()
    {
        Assert.Equal(new PeripheralBattery(64, true), HidppCodec.Unified(new byte[] { 64, 4, 1 }));
        Assert.Equal(new PeripheralBattery(null, false, Level: "Low"), HidppCodec.Unified(new byte[] { 0, 2, 0 }));
        Assert.Equal(new PeripheralBattery(100, true), HidppCodec.Status(new byte[] { 0, 0, 3 }));
        Assert.Equal(new PeripheralBattery(50, true), HidppCodec.Voltage(new byte[] { 0x0E, 0xE3, 0x80 })); // 3811 mV
        Assert.Equal(100, HidppCodec.LiIonPercent(4300));
        Assert.Equal(0, HidppCodec.LiIonPercent(3000));
        Assert.Equal((100, 25600, 50), HidppCodec.DpiRange(new byte[] { 0x00, 0x64, 0xE0, 0x32, 0x64, 0x00, 0, 0 }));
        Assert.Equal((400, 1600, 400), HidppCodec.DpiRange(new byte[] { 0x01, 0x90, 0x03, 0x20, 0x06, 0x40, 0, 0 }));
        Assert.Equal(new[] { 125, 250, 500, 1000 }, HidppCodec.Rates(0x8B));
        Assert.Equal("RQM 64.01.B0017", HidppCodec.Firmware(new byte[] { 0, (byte)'R', (byte)'Q', (byte)'M', 0x64, 0x01, 0x00, 0x17 }));
    }

    // ---------- ODM families ----------
    [Fact]
    public void Feature64BatteryRequestAndReply()
    {
        var p = Feature64Codec.Request(Feature64Codec.Battery);
        Assert.Equal(new byte[] { 0, 0, 2, 2, 0, 0x83 }, p[..6]);
        var reply = new byte[65];
        reply[1] = 0xA1; reply[6] = 0x83; reply[7] = 1; reply[8] = 57;
        Assert.Equal((byte)0xA1, Feature64Codec.Status(reply, 0x83));
        Assert.Equal(new PeripheralBattery(57, true), Feature64Codec.DecodeBattery(reply));
        reply[1] = 0xA4;
        Assert.True(Feature64Codec.DecodeBattery(reply).Asleep);
        reply[6] = 0x84;
        Assert.Null(Feature64Codec.Status(reply, 0x83));
    }

    [Fact]
    public void Report8ChecksumAndBattery()
    {
        var req = Report8Codec.Request(Report8Codec.Battery);
        Assert.Equal((byte)(0x55 - 0x08 - 0x04), req[15]);
        var reply = new byte[17];
        reply[0] = 0x08; reply[1] = 0x04; reply[6] = 88; reply[7] = 0; reply[8] = 0x0F; reply[9] = 0xA0;
        reply[16] = Report8Codec.Checksum(reply.AsSpan(1));
        Assert.True(Report8Codec.Answers(reply, 0x04));
        Assert.Equal(new PeripheralBattery(88, false), Report8Codec.DecodeBattery(reply));
        reply[16]++;
        Assert.False(Report8Codec.Answers(reply, 0x04));
    }

    // ---------- battery-only readers ----------
    [Fact]
    public void VendorBatteryDecoders()
    {
        Assert.Equal(new PeripheralBattery(60, true), VendorBatteryCodec.SteelSeriesAerox(new byte[] { 0, 0x80 | 13 }));
        Assert.True(VendorBatteryCodec.SteelSeriesAerox(new byte[] { 0, 0 }).Asleep);
        Assert.Equal(new PeripheralBattery(50, false), VendorBatteryCodec.NovaProWireless(new byte[] { 6, 0xB0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0, 0, 3 }));
        Assert.Equal(new PeripheralBattery(80, true), VendorBatteryCodec.CorsairVoid(new byte[] { 0x64, 0, 0x80 | 80, 0, 4 }));
        Assert.Equal(73, VendorBatteryCodec.BragiPercent(VendorBatteryCodec.BragiValue(new byte[] { 0, 0, 2, 0, 0xD9, 0x02, 0, 0 })!.Value)); // 729
        Assert.Null(VendorBatteryCodec.BragiValue(new byte[] { 0, 0, 2, 5, 0, 0, 0, 0 }));
    }

    [Fact]
    public void PlayStationBatteryFromInputReports()
    {
        var ds = new byte[64]; ds[0] = 0x01; ds[53] = 0x14; // level 4, charging
        Assert.Equal(new PeripheralBattery(45, true), PlayStationCodec.Decode(0x0CE6, ds));
        ds[53] = 0x2A;
        Assert.Equal(new PeripheralBattery(100, true), PlayStationCodec.Decode(0x0CE6, ds));
        var ds4 = new byte[64]; ds4[0] = 0x01; ds4[30] = 0x07; // level 7 on battery
        Assert.Equal(new PeripheralBattery(75, false), PlayStationCodec.Decode(0x09CC, ds4));
        ds4[0] = 0x05;
        Assert.Null(PlayStationCodec.Decode(0x09CC, ds4));
    }

    [Fact]
    public void HeadsetControlJson()
    {
        var list = HeadsetControl.Parse("""
            {"devices":[{"device":"Arctis Nova 7","id_vendor":"0x1038","id_product":"0x2202","battery":{"status":"BATTERY_CHARGING","level":64}},
                        {"device":"G535","id_vendor":1133,"id_product":2756,"battery":{"status":"BATTERY_UNAVAILABLE","level":-1}},
                        {"device":"broken","id_vendor":"zz","id_product":"0x1"}]}
            """);
        Assert.Equal(2, list.Count);
        Assert.Equal(new HeadsetControlDevice(0x1038, 0x2202, "Arctis Nova 7", new PeripheralBattery(64, true)), list[0]);
        Assert.True(list[1].Battery.Asleep);
        Assert.Empty(HeadsetControl.Parse("not json"));
    }

    [Fact]
    public void ViaReplies()
    {
        var r = new byte[33]; r[1] = 0x01; r[2] = 0x00; r[3] = 0x0C;
        Assert.True(ViaCodec.Answers(r, ViaCodec.ProtocolVersion));
        Assert.Equal(12, ViaCodec.Version(r));
        Assert.False(ViaCodec.Answers(r, ViaCodec.LayerCount));
    }
}
