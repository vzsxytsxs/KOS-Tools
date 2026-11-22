using KosToolbox.Core.Peripherals;
using KosToolbox.Core.Peripherals.AttackShark;
using KosToolbox.Core.Peripherals.Hid;
using Xunit;

namespace KosToolbox.Core.Tests;

[Collection("PeripheralDriver statics")]
public class AttackSharkTests
{
    static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    // The friend's X11 on its receiver (1D57:FA60, firmware 01 07), read by the probe on 2026-11-22.
    const string Version = "0B 08 01 07 00 00 00 00";
    const string Polling = "06 09 01 01 FE 00 00 00 00";
    const string Light = "05 0F 01 10 03 A8 CE 00 B9 01 03 02 46 00 00";
    const string Dpi = "04 38 01 00 00 3F 20 20 12 1C 38 4B 75 81 00 00 00 00 00 00 00 01 00 00 01 FF 00 00 00 FF 00 00 00 FF FF FF 00 00 FF FF FF 00 FF FF 40 00 FF FF FF 02 0F 5D 00 00 00 00";
    const string Buttons = "08 3B 01 02 00 00 03 00 00 04 00 00 01 00 00 01 00 00 0D 00 00 06 00 00 05 00 00 01 00 00 01 00 00 01 00 00 01 00 00 01 00 00 01 00 00 01 00 00 01 00 00 09 00 00 0A 00 00 00 3E";
    const string Heartbeat = "03 55 40 01 4B";

    [Fact]
    public void TheFriendsReportsAreValid()
    {
        foreach (var r in new[] { Version, Polling, Light, Dpi, Buttons }) Assert.True(AttackSharkCodec.Valid(Hex(r), Hex(r)[0]), r);
        var bad = Hex(Light);
        bad[12] ^= 1;
        Assert.False(AttackSharkCodec.Valid(bad, AttackSharkCodec.Light));
        Assert.False(AttackSharkCodec.Valid(Hex(Light), AttackSharkCodec.Dpi));
        // Seal recomputes what the mouse sent.
        foreach (var r in new[] { Polling, Light, Dpi, Buttons }) Assert.Equal(Hex(r), AttackSharkCodec.Seal(Hex(r)));
    }

    [Fact]
    public void DecodesTheFriendsSettings()
    {
        Assert.Equal("1.07", AttackSharkCodec.DecodeVersion(Hex(Version)));
        Assert.Equal(1000, AttackSharkCodec.DecodePolling(Hex(Polling)));
        var light = Hex(Light);
        Assert.Equal(new PeripheralLighting(LightingEffect.Static, 0xCE00B9), AttackSharkCodec.DecodeLighting(light));
        Assert.Equal(100, AttackSharkCodec.DecodeBrightness(light));
        Assert.Equal(30, AttackSharkCodec.DecodeSleepSeconds(light));       // the app showed 0.5 min
        Assert.Equal(10, AttackSharkCodec.DecodeDeepSleepMinutes(light));   // 10 min
        Assert.Equal(6, AttackSharkCodec.DecodeDebounceMs(light));          // 6 ms
        var (stages, active) = AttackSharkCodec.DecodeStages(Hex(Dpi))!.Value;
        Assert.Equal(new[] { 800, 1200, 2400, 3200, 5000, 22000 }, stages);
        Assert.Equal(1, active);
        Assert.Equal(((byte)0x55, 75, false), AttackSharkCodec.Heartbeat(Hex(Heartbeat)));
        Assert.Null(AttackSharkCodec.Heartbeat(Hex("03 55 50 00 05")));
        Assert.True(AttackSharkCodec.Ack(Hex("03 55 50 00 05"), 0x05));
        Assert.False(AttackSharkCodec.Ack(Hex("03 55 50 01 05"), 0x05));
        Assert.Null(AttackSharkCodec.Ack(Hex("03 55 50 00 06"), 0x05));
    }

    [Theory]
    [InlineData(50)] [InlineData(800)] [InlineData(1600)] [InlineData(10000)] [InlineData(10100)] [InlineData(11000)] [InlineData(12000)]
    public void StageDpiRoundTripsAndLeavesOtherStages(int dpi)
    {
        var before = Hex(Dpi);
        var after = AttackSharkCodec.WithStageDpi(before, 1, dpi);
        Assert.True(AttackSharkCodec.Valid(after, AttackSharkCodec.Dpi));
        Assert.Equal(dpi, AttackSharkCodec.DecodeStage(after, 1));
        for (var i = 0; i < 50; i++)
            if (i is not (9 or 17)) Assert.True(before[i] == after[i], $"byte {i} changed"); // stage 2's code and flag only
    }

    [Fact]
    public void TheDoubledRangeIsEncodedAsTheMouseHasIt()
    {
        // 22000 on stage 6 encodes to exactly the friend's bytes: code 81, range flag 1, bit 5 in [6] and [7].
        var dpi = Hex(Dpi);
        var undone = AttackSharkCodec.WithStageDpi(dpi, 5, 12000);
        Assert.Equal(0, undone[6]);
        Assert.Equal(0, undone[7]);
        Assert.Equal(12000, AttackSharkCodec.DecodeStage(undone, 5));
        Assert.Equal(dpi, AttackSharkCodec.WithStageDpi(undone, 5, 22000));
        foreach (var v in new[] { 12100, 15000, 20000, 20200, 21000 })
            Assert.Equal(v, AttackSharkCodec.DecodeStage(AttackSharkCodec.WithStageDpi(dpi, 0, v), 0));
        Assert.False(AttackSharkCodec.ValidDpi(20100));
        Assert.False(AttackSharkCodec.ValidDpi(22200));
        Assert.False(AttackSharkCodec.ValidDpi(825));
        Assert.False(AttackSharkCodec.ValidDpi(10150));
    }

    [Fact]
    public void SensorTogglesColorsAndPowerKeepTheirNeighbours()
    {
        var dpi = Hex(Dpi);
        var on = AttackSharkCodec.WithAngleSnap(AttackSharkCodec.WithRipple(dpi, true), true);
        Assert.True(AttackSharkCodec.DecodeRipple(on));
        Assert.True(AttackSharkCodec.DecodeAngleSnap(on));
        Assert.Equal(dpi, AttackSharkCodec.WithAngleSnap(AttackSharkCodec.WithRipple(on, false), false));
        Assert.Equal(0xFF0000, AttackSharkCodec.DecodeStageColor(dpi, 0));
        Assert.Equal(0xFF4000, AttackSharkCodec.DecodeStageColor(dpi, 6));
        var blue = AttackSharkCodec.WithStageColor(dpi, 0, 0x0000FF);
        Assert.Equal(0x0000FF, AttackSharkCodec.DecodeStageColor(blue, 0));
        Assert.True(AttackSharkCodec.Valid(blue, AttackSharkCodec.Dpi));

        var light = Hex(Light);
        var deep = AttackSharkCodec.WithDeepSleep(light, 37);
        Assert.Equal(37, AttackSharkCodec.DecodeDeepSleepMinutes(deep));
        Assert.Equal(3, deep[4] & 0x0F);  // light speed kept
        Assert.Equal(8, deep[5] & 0x0F);  // brightness kept
        Assert.Equal(light, AttackSharkCodec.WithDeepSleep(deep, 10));
        Assert.Equal(8, AttackSharkCodec.DecodeDebounceMs(AttackSharkCodec.WithDebounce(light, 8)));
        Assert.Equal(light, AttackSharkCodec.WithDebounce(light, 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => AttackSharkCodec.WithDebounce(light, 7));
    }

    [Fact]
    public void ButtonsDecodeAndALeftClickAlwaysStays()
    {
        var b = Hex(Buttons);
        Assert.Equal(ButtonAction.Left, AttackSharkCodec.DecodeButton(b, 0));
        Assert.Equal(ButtonAction.Forward, AttackSharkCodec.DecodeButton(b, 6));
        Assert.Equal(ButtonAction.Back, AttackSharkCodec.DecodeButton(b, 7));
        Assert.Equal(ButtonAction.DpiCycle, AttackSharkCodec.DecodeButton(b, 5));
        var k = AttackSharkCodec.WithButton(b, 6, ButtonAction.Key(0x0E));
        Assert.Equal(new byte[] { 0x11, 0x00, 0x0E }, k[21..24]);
        Assert.True(AttackSharkCodec.Valid(k, AttackSharkCodec.Buttons));
        Assert.Equal(b, AttackSharkCodec.WithButton(k, 6, ButtonAction.Forward));
        Assert.Throws<ArgumentException>(() => AttackSharkCodec.WithButton(b, 0, ButtonAction.Right));
    }

    [Fact]
    public void AMacroIsThreeReportsWithTheAppsLayout()
    {
        var steps = new[] { new MacroStep(0x0E, true), new MacroStep(0x0E, false), new MacroStep(0x12, true, 20), new MacroStep(0x12, false) };
        var r = AttackSharkCodec.EncodeMacro(7, steps);
        Assert.Equal(3, r.Length);
        Assert.All(r, x => Assert.Equal(64, x.Length));
        Assert.Equal(new byte[] { 0x09, 0x40, 0x07, 0x00, 0x00 }, r[0][..5]);   // id, length, slot, chunk, play mode L[3]
        Assert.Equal(1, r[0][8]);                                               // loops L[7]
        Assert.Equal(4, r[0][29]);                                              // item count L[28]
        Assert.Equal(new byte[] { 0x01, 0x0E, 0x81, 0x0E, 0x03, 0x12, 0x81, 0x12 }, r[0][30..38]);
        Assert.Equal(new byte[] { 0x09, 0x40, 0x07, 0x01 }, r[1][..4]);
        Assert.Equal(new byte[] { 0x09, 0x0C, 0x07, 0x02 }, r[2][..4]);
        var sum = 1 + 4 + 0x01 + 0x0E + 0x81 + 0x0E + 0x03 + 0x12 + 0x81 + 0x12; // L[3..128]: loops 1, count 4, the items
        Assert.Equal(new byte[] { (byte)(sum >> 8), (byte)sum }, r[2][10..12]);  // L[129..130] = chunk 2 at 4 + (129 - 123)
        Assert.Equal(0x40, AttackSharkCodec.EncodeMacro(7, steps, lastLength: 0x40)[2][1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => AttackSharkCodec.EncodeMacro(0, steps));
    }

    [Fact]
    public void LightChangesKeepTheSharedNibbles()
    {
        var light = Hex(Light);
        var dim = AttackSharkCodec.WithBrightness(light, 50);
        Assert.Equal(4, dim[5] & 0x0F);
        Assert.Equal(10, AttackSharkCodec.DecodeDeepSleepMinutes(dim));
        Assert.Equal(1, AttackSharkCodec.Level(0));
        var off = AttackSharkCodec.WithLighting(light, LightingEffect.Off, 0);
        Assert.Equal(0x00, off[3]);
        Assert.Equal(new byte[] { 0xCE, 0x00, 0xB9 }, off[6..9]);
        var green = AttackSharkCodec.WithLighting(light, LightingEffect.Breathing, 0x00FF00);
        Assert.Equal(new PeripheralLighting(LightingEffect.Breathing, 0x00FF00), AttackSharkCodec.DecodeLighting(green));
        Assert.Equal(3, AttackSharkCodec.WithLighting(light, LightingEffect.Spectrum, 0)[4] & 0x0F); // speed kept
        var sleep = AttackSharkCodec.WithSleep(light, 90);
        Assert.Equal(3, sleep[9]);
        Assert.True(AttackSharkCodec.Valid(sleep, AttackSharkCodec.Light));
        Assert.Throws<ArgumentOutOfRangeException>(() => AttackSharkCodec.WithSleep(light, 45));
        Assert.Equal(Hex("06 09 01 08 F7 00 00 00 00"), AttackSharkCodec.WithPolling(Hex(Polling), 125));
    }

    // ---------- the driver against a simulated receiver ----------

    static HidCollection Col(string hw, ushort page, ushort usage, int input, int feature) =>
        new($@"\\?\hid#vid_1d57&pid_fa60&{hw}#8&1&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}", 0x1D57, 0xFA60, 0x1111, page, usage,
            input, 0, feature, "2.4G Wireless Device", Guid.NewGuid(), false);

    static HidDevice Receiver() => new("x11", new[]
    {
        Col("mi_01", 1, 2, 8, 0), Col("mi_02&col03", 0x0A, 0, 5, 0), Col("mi_02&col04", 0x0B, 0, 0, 134),
    });

    /// <summary>The X11 as the probe saw it: heartbeats, A0 grants, reports by id; a write is stored and acknowledged.</summary>
    sealed class Mouse
    {
        public byte Model = 0x55;
        public bool Silent, RefuseWrites, IgnoreWrites;
        public int LoseWrites; // writes that vanish over the air: not stored, not acknowledged
        public readonly Dictionary<byte, byte[]> Reports = new()
        {
            [0x0B] = Hex(Version), [0x06] = Hex(Polling), [0x05] = Hex(Light), [0x04] = Hex(Dpi), [0x08] = Hex(Buttons),
        };
        public readonly Queue<byte[]> Events = new();
        public readonly List<byte[]> Writes = new();
        public byte? Requested;
    }

    sealed class View(HidCollection c, Mouse m) : IHidTransport
    {
        public HidCollection Collection => c;
        public void SetFeature(byte id, ReadOnlySpan<byte> p) => throw new InvalidOperationException("feature reports go at their own length");
        public byte[] GetFeature(byte id) => throw new InvalidOperationException("feature reports go at their own length");
        public void SetFeature(byte id, ReadOnlySpan<byte> p, int length)
        {
            Assert.Equal(0x0B, c.UsagePage);
            var report = HidBuffer.Build(id, p, length);
            if (id == AttackSharkCodec.ReadRequest) { Assert.Equal(8, length); m.Requested = p[0]; return; }
            if (id == AttackSharkCodec.Macro) { Assert.Equal(64, length); m.Writes.Add(report); m.Events.Enqueue(new byte[] { 0x03, m.Model, 0x50, 0, id }); return; }
            Assert.Equal(AttackSharkCodec.LengthOf(id), length);
            m.Writes.Add(report);
            if (m.LoseWrites > 0) { m.LoseWrites--; return; }
            if (!m.IgnoreWrites && !m.RefuseWrites) m.Reports[id] = report;
            m.Events.Enqueue(new byte[] { 0x03, m.Model, 0x50, (byte)(m.RefuseWrites ? 1 : 0), id });
        }
        public byte[] GetFeature(byte id, int length)
        {
            if (id == AttackSharkCodec.ReadRequest) return new byte[] { 0xA0, (byte)(m.Requested is null ? 0 : 1), 0, 0, 0, 0, 0, 0 };
            Assert.Equal(m.Requested, id);
            m.Requested = null; // one read per grant
            return m.Reports[id][..length];
        }
        public void Write(byte id, ReadOnlySpan<byte> p, int t = 500) => throw new NotSupportedException();
        public byte[]? Read(int t = 500)
        {
            Assert.Equal(0x0A, c.UsagePage);
            if (m.Events.TryDequeue(out var e)) return e;
            return m.Silent ? null : new byte[] { 0x03, m.Model, 0x40, 0x01, 0x4B };
        }
        public byte[] GetInputReport(byte id) => throw new NotSupportedException();
        public void FlushQueue() => m.Events.Clear();
    }

    static AttackSharkDriver Driver(Mouse m)
    {
        PeripheralDriver.Delay = _ => { };
        PeripheralDriver.Opener = (c, _) => new View(c, m);
        return AttackSharkDriver.Claim(Receiver())!;
    }

    [Fact]
    public void ReadsEverythingTheAppShows()
    {
        var m = new Mouse();
        var d = Driver(m);
        var s = d.Read();
        Assert.Equal("1.07", s.Firmware);
        Assert.Equal(new PeripheralBattery(75, false), s.Battery);
        Assert.Equal(1000, s.Polling!.Hz);
        Assert.Equal(800, s.Dpi!.X);
        Assert.Equal(6, s.Dpi.Stages!.Count);
        Assert.Equal(30, s.SleepSeconds);
        Assert.Equal(new PeripheralLighting(LightingEffect.Static, 0xCE00B9), s.Lighting);
        Assert.Contains(PeripheralSetting.Dpi, d.Writable);
        Assert.Empty(m.Writes);
    }

    [Fact]
    public void AnotherModelOnTheSameReceiverGetsNothingSent()
    {
        var m = new Mouse { Model = 0x10 }; // an R1 on the shared receiver PID
        var d = Driver(m);
        var s = d.Read();
        Assert.Equal("Attack Shark mouse", s.Name);
        Assert.Equal(75, s.Battery!.Percent);
        Assert.Null(s.Dpi);
        Assert.Null(m.Requested);
        Assert.Empty(d.Writable);
        Assert.Throws<PeripheralException>(() => d.SetPollingRate(500));
        Assert.Empty(m.Writes);
    }

    [Fact]
    public void ASilentMouseIsAsleepAndNothingIsSent()
    {
        var m = new Mouse { Silent = true };
        var s = Driver(m).Read();
        Assert.True(s.Battery!.Asleep);
        Assert.Null(m.Requested);
    }

    [Fact]
    public void AMouseThatFallsAsleepGetsNoRequests()
    {
        var m = new Mouse();
        var d = Driver(m);
        Assert.Equal("Attack Shark X11", d.Read().Name);
        m.Silent = true;
        var s = d.Read();
        Assert.True(s.Battery!.Asleep);
        Assert.Null(m.Requested);
        Assert.Null(s.Dpi);
    }

    [Fact]
    public void WritesOnceAndReadsBack()
    {
        var m = new Mouse();
        var d = Driver(m);
        d.Read();
        var s = d.SetPollingRate(500);
        Assert.Equal(500, s.Polling!.Hz);
        Assert.Single(m.Writes);
        Assert.Equal(Hex("06 09 01 02 FD 00 00 00 00"), m.Writes[0]);

        s = d.SetDpi(1600, 1600);
        Assert.Equal(1600, s.Dpi!.X);
        Assert.Equal(new[] { 1600, 1200, 2400, 3200, 5000, 22000 }, s.Dpi.Stages!.Select(x => x.X));

        s = d.SetActiveStage(3);
        Assert.Equal(2400, s.Dpi!.X);

        s = d.SetLighting(LightingEffect.Breathing, 0x00FF00);
        Assert.Equal(new PeripheralLighting(LightingEffect.Breathing, 0x00FF00), s.Lighting);
        Assert.Equal(10, AttackSharkCodec.DecodeDeepSleepMinutes(m.Reports[0x05])); // the shared nibbles survive
        s = d.SetSleep(300);
        Assert.Equal(300, s.SleepSeconds);
        Assert.Equal(6, AttackSharkCodec.DecodeDebounceMs(m.Reports[0x05]));
        Assert.Equal(5, m.Writes.Count);
    }

    [Fact]
    public void TheX11OnlySettingsWriteAndRestore()
    {
        var m = new Mouse();
        var d = Driver(m);
        d.Read();
        var x = d.Extras!;
        Assert.Equal((10, 6, false, false), (x.DeepSleepMinutes, x.DebounceMs, x.Ripple, x.AngleSnap));
        Assert.Equal(ButtonAction.Forward, x.Buttons![6]);
        var dpi = d.Raw(AttackSharkCodec.Dpi)!;
        d.SetRipple(true);
        d.SetAngleSnap(true);
        d.SetStageDpi(6, 20000);
        d.SetStageColor(1, 0x123456);
        Assert.Equal((true, true), (d.Extras!.Ripple, d.Extras.AngleSnap));
        d.Restore(dpi);
        Assert.Equal(dpi, m.Reports[0x04]);
        d.SetDeepSleep(11);
        d.SetDebounce(8);
        Assert.Equal((11, 8), (d.Extras!.DeepSleepMinutes, d.Extras.DebounceMs));
        d.SetButton(6, ButtonAction.RunMacro(7));
        Assert.Equal(new bool?[] { true, true, true }, d.WriteMacro(7, new[] { new MacroStep(0x0E, true), new MacroStep(0x0E, false) }));
        Assert.Equal(3, m.Writes.Count(w => w[0] == 0x09));
        Assert.Throws<ArgumentException>(() => d.SetButton(0, ButtonAction.Right)); // no left click left: refused before sending
        Assert.Throws<ArgumentOutOfRangeException>(() => d.SetStageDpi(7, 800));
    }

    [Fact]
    public void TheGamemodeStateKeepsTheReportsByteForByte()
    {
        // The engine (Native AOT) saves what the X11 had before gamemode through the source-generated context.
        var state = new GamemodePeripheralState { Before = { new GamemodePeripheralEntry
            { VendorId = 0x1D57, ProductId = 0xFA60, AngleSnap = true, Ripple = false, DebounceMs = 8, MouseDpiReport = Hex(Dpi), MouseLightReport = Hex(Light) } } };
        var json = System.Text.Json.JsonSerializer.Serialize(state, KosToolbox.Core.Persistence.KosJson.Default.GamemodePeripheralState);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, KosToolbox.Core.Persistence.KosJson.Default.GamemodePeripheralState)!.Before[0];
        Assert.Equal(Hex(Dpi), back.MouseDpiReport);
        Assert.Equal(Hex(Light), back.MouseLightReport);
        Assert.Equal((true, false, 8), (back.AngleSnap, back.Ripple, back.DebounceMs));
    }

    [Fact]
    public void ALostWriteIsSentAgainUntilAcknowledged()
    {
        var m = new Mouse();
        var d = Driver(m);
        d.Read();
        m.LoseWrites = 2; // the friend's X11 lost one write of stage 3 over the receiver
        Assert.Equal(2350, d.SetStageDpi(3, 2350).Dpi!.Stages![2].X);
        Assert.Equal(3, m.Writes.Count);
        Assert.All(m.Writes, w => Assert.Equal(m.Writes[0], w)); // the same report each time

        m.LoseWrites = 10;
        Assert.Throws<ReadBackException>(() => d.SetStageDpi(3, 2400));
        Assert.Equal(3 + 4, m.Writes.Count); // at most four sends, then the read-back decides
    }

    [Fact]
    public void AWriteTheMouseDidntKeepIsReported()
    {
        var m = new Mouse();
        var d = Driver(m);
        d.Read();
        m.IgnoreWrites = true;
        var ex = Assert.Throws<ReadBackException>(() => d.SetPollingRate(250));
        Assert.Equal(1000, ex.State.Polling!.Hz);
        Assert.Single(m.Writes); // sent once

        m.IgnoreWrites = false;
        m.RefuseWrites = true;
        Assert.Throws<ReadBackException>(() => d.SetBrightness(50));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.SetDpi(825, 825));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.SetDpi(800, 1600));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.SetActiveStage(7));
        Assert.Equal(2, m.Writes.Count);
    }
}
