using KosToolbox.Core.Peripherals;
using KosToolbox.Core.Peripherals.Hid;
using KosToolbox.Core.Peripherals.Royuan;
using Xunit;

namespace KosToolbox.Core.Tests;

[Collection("PeripheralDriver statics")]
public class RoyuanTests
{
    /// <summary>A Win32 buffer (report id 0) from the payload hex an R82HE answered (firmware 5.05, 2026-11-17 probe).</summary>
    static byte[] Buf(string hex)
    {
        var b = new byte[65];
        Convert.FromHexString(hex.Replace(" ", "")).CopyTo(b, 1);
        return b;
    }

    // Raw replies from the R82HE on this PC.
    const string IdentifyR82 = "8F 1C 0B 00 00 00 00 05 05";
    const string Polling1000 = "83 00 03 00 00 00 00 7C";
    const string LightPicture = "87 0D 04 00 00 00 C8 C8"; // mode 13 (per-key picture), wire speed 4, brightness 0
    const string Profile1 = "84 00 00 00 00 00 00 7B";
    // Sub-layer 0 of profile 1 on the R82HE (8 pages of 64 bytes), read 2026-11-17.
    const string R82Keymap =
        "000029000000350000002B00000039000000E1000000E00000003A0000001E000000140000000400000000000000E30000003B0000001F0000001A0000001600" +
        "00001D000000E20000003C0000002000000008000000070000001B000000000000003D00000021000000150000000900000006000000000000003E0000002200" +
        "0000170000000A00000019000000000000003F000000230000001C0000000B000000050000002C0000004000000024000000180000000D000000110000000000" +
        "000041000000250000000C0000000E00000010000000000000004200000026000000120000000F00000036000A01000000004300000027000000130000003300" +
        "000037000000E4000000440000002D0000002F000000340000003800000000000000450000002E0000003000000000000000E500000050000000000000002A00" +
        "0000310000002800000052000000510000004C0000004A0000004B0000004E000000000000004F00000000000300E2000300E9000300EA000D0201000D020200" +
        "0D0301000D0302000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";

    const string ActuationPage0 = "90 01 90 01 90 01 90 01 90 01 90 01 90 01 90 01 22 01 22 01 00 00 90 01";

    [Fact]
    public void Bit7ChecksumMatchesThePollingRequest()
    {
        var p = RoyuanCodec.Request(new[] { RoyuanCodec.GetPolling });
        Assert.Equal(64, p.Length);
        Assert.Equal(new byte[] { 0x83, 0, 0, 0, 0, 0, 0, 0x7C }, p[..8]);
        Assert.All(p[8..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void LightingUsesBit8AndSendsWhiteAsFafafa()
    {
        var p = RoyuanCodec.EncodeLight(new RoyuanLight(1, 2, 4, 7, 0xFFFFFF));
        Assert.Equal(new byte[] { 0x07, 1, 2, 4, 7, 0xFA, 0xFA, 0xFA }, p[..8]);
        Assert.Equal(RoyuanCodec.Checksum(p.AsSpan(0, 8)), p[8]);
        Assert.Equal(0xFF - ((0x07 + 1 + 2 + 4 + 7 + 3 * 0xFA) & 0xFF), p[8]);
    }

    [Fact]
    public void OpcodesOutsideTheAllowListAreRefused()
    {
        foreach (var op in new byte[] { 0xAC, 0x7F, 0x1C, 0x1E, 0x8C, 0x01, 0x30, 0xBA, 0xF8, 0x05, 0x09, 0x13, 0x15, 0x16, 0x89 })
            Assert.Throws<ArgumentException>(() => RoyuanCodec.Request(new[] { op }));
    }

    [Fact]
    public void IdentifyDecodesIdAndVersion()
    {
        var r = Buf(IdentifyR82);
        Assert.True(RoyuanCodec.Answers(r, RoyuanCodec.Identify));
        Assert.Equal(2844u, RoyuanCodec.DeviceId(r));
        Assert.Equal("5.05", RoyuanCodec.VersionText(RoyuanCodec.Version(r)));
        Assert.Equal(0.005, RoyuanCodec.TravelUnitMm(0x0505));
        Assert.Equal(0.01, RoyuanCodec.TravelUnitMm(0x0309));
        Assert.Equal(0.1, RoyuanCodec.TravelUnitMm(0x0209));
    }

    [Fact]
    public void PollingRoundTrips()
    {
        Assert.Equal(1000, RoyuanCodec.DecodePolling(Buf(Polling1000)));
        foreach (var hz in RoyuanCodec.PollingRates)
        {
            var p = RoyuanCodec.EncodePolling(hz);
            Assert.Equal(RoyuanCodec.SetPolling, p[0]);
            var reply = new byte[65];
            reply[1] = RoyuanCodec.GetPolling;
            reply[3] = p[2];
            Assert.Equal(hz, RoyuanCodec.DecodePolling(reply));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => RoyuanCodec.EncodePolling(1500));
    }

    [Fact]
    public void LightingRoundTripsAndMapsModes()
    {
        var picture = RoyuanCodec.DecodeLight(Buf(LightPicture));
        Assert.Equal(new RoyuanLight(13, 4, 0, 0, 0x00C8C8), picture);
        Assert.Equal(LightingEffect.Other, RoyuanCodec.ToLighting(picture).Effect);

        var stat = new RoyuanLight(1, 2, 3, RoyuanCodec.FixedColor, 0x123456);
        var sent = RoyuanCodec.EncodeLight(stat);
        var reply = new byte[65];
        sent.AsSpan(0, 8).CopyTo(reply.AsSpan(1));
        reply[1] = RoyuanCodec.GetLight;
        Assert.Equal(stat, RoyuanCodec.DecodeLight(reply));
        Assert.Equal(new PeripheralLighting(LightingEffect.Static, 0x123456), RoyuanCodec.ToLighting(stat));
        Assert.Equal(new PeripheralLighting(LightingEffect.Breathing, 0x00FF00), RoyuanCodec.ToLighting(stat with { Mode = 2, Flags = 3 })); // preset green
        Assert.Equal(new PeripheralLighting(LightingEffect.Spectrum, null), RoyuanCodec.ToLighting(stat with { Mode = 3, Flags = RoyuanCodec.Rainbow }));
        Assert.Equal(LightingEffect.Other, RoyuanCodec.ToLighting(stat with { Mode = 1, Flags = RoyuanCodec.Rainbow }).Effect);
        Assert.Equal(0xFFFFFF, RoyuanCodec.ToLighting(stat with { Color = 0xFAFAFA }).Color);
    }

    [Fact]
    public void ColumnPagesDecodeAndWritePagesCarryLastOnlyAtTheEnd()
    {
        var values = new ushort[RoyuanCodec.Slots];
        RoyuanCodec.DecodeU16Page(Buf(ActuationPage0), 0, values);
        Assert.Equal(new ushort[] { 400, 400, 400, 400, 400, 400, 400, 400, 290, 290, 0, 400 }, values[..12]);

        var all = Enumerable.Range(0, RoyuanCodec.Slots).Select(i => (ushort)(1000 + i)).ToArray();
        var pages = RoyuanCodec.ColumnWriteU16(0, all);
        Assert.Equal(5, pages.Count);
        for (var p = 0; p < 5; p++)
        {
            Assert.Equal(new byte[] { RoyuanCodec.SetColumn, 0, 1, (byte)p, (byte)(p == 4 ? 1 : 0) }, pages[p][..5]);
            Assert.Equal(RoyuanCodec.Checksum(pages[p].AsSpan(0, 7)), pages[p][7]);
        }
        Assert.Equal(1000 + 28, pages[1][8] | pages[1][9] << 8);
        Assert.Equal(1000 + 127, pages[4][8 + 30] | pages[4][8 + 31] << 8);
        Assert.All(pages[4][(8 + 32)..], b => Assert.Equal(0, b));

        var u8 = RoyuanCodec.ColumnWriteU8(7, Enumerable.Range(0, 128).Select(i => (byte)i).ToArray());
        Assert.Equal(3, u8.Count);
        Assert.Equal(new byte[] { 0, 0, 1 }, u8.Select(p => p[4]).ToArray());
        Assert.Equal(112, u8[2][8]);
    }

    // ---------- driver ----------
    static HidCollection Col(string hw, ushort page, ushort usage, int input, int feature) =>
        new($@"\\?\hid#vid_3151&pid_502d&{hw}#7&1&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}", 0x3151, 0x502D, 0x0100, page, usage,
            input, 0, feature, "R82HE", Guid.Empty, false);

    static HidDevice R82 => new("r82", new[] { Col("mi_00", 1, 6, 9, 0), Col("mi_01&col05", 0xFFFF, 1, 32, 0), Col("mi_02", 0xFFFF, 2, 0, 65) });

    /// <summary>A board with these stored values; replies are written into the request buffer, as the R82HE does.</summary>
    sealed class Board(uint id = 2844)
    {
        public byte Polling = 3, Profile, Mode = 13, Speed = 4, Brightness, Flags;
        public int Color = 0x00C8C8;
        public bool IgnoreWrites;
        /// <summary>After a commit, this many GETs come back as the bare request and SETs are dropped (the R82HE storing to flash).</summary>
        public int BusyAfterCommit;
        int _busy;
        public List<byte[]> Sent { get; } = new();
        /// <summary>Columns by sub-op (0..3, 6 u16; 7 u8), like the R82HE here: keys at slots 0..79 (slot 8 differs), 9 and 21 snap.</summary>
        public readonly Dictionary<byte, int[]> Columns = new()
        {
            [0] = Keys(400), [1] = Keys(400), [2] = new int[128], [3] = new int[128], [6] = Keys(60), [7] = new int[128],
            [4] = new int[128], [5] = new int[128], [9] = Enumerable.Repeat(255, 128).ToArray(), [10] = new int[512],
        };
        /// <summary>Sub-layers 0..3, 512 bytes each; sub-layer 0 is the R82HE's own keymap.</summary>
        public readonly byte[][] Layers = { Convert.FromHexString(R82Keymap), new byte[512], new byte[512], new byte[512] };
        public readonly byte[] Fn = new byte[512];
        public readonly byte[][] Macros = Enumerable.Range(0, 50).Select(_ => new byte[256]).ToArray();
        static bool Wide(int subop) => subop is 0 or 1 or 2 or 3 or 4 or 6;

        static int[] Keys(int v) => Enumerable.Range(0, 128).Select(i => i < 80 ? v : 0).ToArray();

        public Board Customized()
        {
            Columns[0][8] = Columns[1][8] = 290;
            Columns[7][9] = Columns[7][21] = 7;
            Columns[9][9] = 21;
            Columns[9][21] = 9;
            return this;
        }

        public byte[] Reply(byte[] req, int _)
        {
            var r = (byte[])req.Clone();
            if (_busy > 0) { _busy--; return r; }
            switch (req[1])
            {
                case 0x8F: BitConverter.GetBytes(id).CopyTo(r, 2); r[8] = 5; r[9] = 5; break;
                case 0x83: r[3] = Polling; break;
                case 0x84: r[2] = Profile; break;
                case 0x87: r[2] = Mode; r[3] = Speed; r[4] = Brightness; r[5] = Flags; r[6] = (byte)(Color >> 16); r[7] = (byte)(Color >> 8); r[8] = (byte)Color; break;
                case 0x90: Fn.AsSpan(req[5] * 64, 64).CopyTo(r.AsSpan(1)); break;
                case 0x8B: Macros[req[2]].AsSpan(req[3] * 64, 64).CopyTo(r.AsSpan(1)); break;
                case 0x8A when req[5] < 4:
                    Layers[req[5]].AsSpan(req[4] * 64, 64).CopyTo(r.AsSpan(1));
                    break;
                case 0xE5 when Columns.TryGetValue(req[2], out var col):
                    var page = req[4];
                    if (!Wide(req[2])) for (var i = 0; i < 64; i++) r[1 + i] = (byte)col[page * 64 + i];
                    else for (var i = 0; i < 32; i++) { r[1 + 2 * i] = (byte)col[page * 32 + i]; r[2 + 2 * i] = (byte)(col[page * 32 + i] >> 8); }
                    break;
            }
            return r;
        }

        /// <summary>A SET: writes take effect when sent (no GET follows them).</summary>
        public void Apply(byte[] req)
        {
            Sent.Add(req);
            if (IgnoreWrites || _busy > 0) return;
            if (req[1] is 0x03 or 0x04 or 0x07 or 0x0A or 0x10 || req[1] is 0x65 or 0x0B && req[5] == 1) _busy = BusyAfterCommit;
            switch (req[1])
            {
                case 0x03: Polling = req[3]; break;
                case 0x04: Profile = req[2]; break;
                case 0x07: (Mode, Speed, Brightness, Flags, Color) = (req[2], req[3], req[4], req[5], req[6] << 16 | req[7] << 8 | req[8]); break;
                case 0x65 when req[3] == 1 && req[2] == 9: break; // a whole partner column is rebuilt from the board's partner store (R82HE)
                case 0x65 when req[3] == 1 && Columns.TryGetValue(req[2], out var col): // whole-column page at [9] (payload 8)
                    if (!Wide(req[2])) for (var i = 0; i < 56 && req[4] * 56 + i < 128; i++) col[req[4] * 56 + i] = req[9 + i];
                    else for (var i = 0; i < 28 && req[4] * 28 + i < 128; i++) col[req[4] * 28 + i] = req[9 + 2 * i] | req[10 + 2 * i] << 8;
                    break;
                case 0x65 when req[3] == 0 && req[2] == 8: // one slot's DKS actions, into the sub-op 10 blocks
                    for (var l = 0; l < 4; l++) Columns[10][l * 128 + req[4]] = req[9 + l];
                    break;
                case 0x65 when req[3] == 0 && Columns.TryGetValue(req[2], out var one): // one slot, value at [9]
                    one[req[4]] = Wide(req[2]) ? req[9] | req[10] << 8 : req[9];
                    break;
                case 0x10: req.AsSpan(9, 4).CopyTo(Fn.AsSpan(req[4] * 4)); break;
                case 0x0B: // a macro page: [0B, slot, page, 56, last] + 56 bytes at [9]
                    for (var i = 0; i < 56 && req[3] * 56 + i < 256; i++) Macros[req[2]][req[3] * 56 + i] = req[9 + i];
                    break;
                case 0x0A: // one keymap entry: [0A, profile, slot, 0, 0, 1, sub-layer] + entry at [9]
                    req.AsSpan(9, 4).CopyTo(Layers[req[7]].AsSpan(req[3] * 4));
                    break;
            }
        }
    }

    static RoyuanDriver Driver(Board board)
    {
        PeripheralDriver.Delay = _ => { };
        PeripheralDriver.Opener = (c, _) => new Recording(new FakeTransport(c, board.Reply), board.Apply);
        return RoyuanDriver.Claim(R82)!;
    }

    sealed class Recording(FakeTransport inner, Action<byte[]> sent) : IHidTransport
    {
        public HidCollection Collection => inner.Collection;
        public void SetFeature(byte id, ReadOnlySpan<byte> p) { inner.SetFeature(id, p); sent(inner.Sent[^1]); }
        public byte[] GetFeature(byte id) => inner.GetFeature(id);
        public void Write(byte id, ReadOnlySpan<byte> p, int t = 500) => inner.Write(id, p, t);
        public byte[]? Read(int t = 500) => inner.Read(t);
        public byte[] GetInputReport(byte id) => inner.GetInputReport(id);
        public void FlushQueue() { }
    }

    [Fact]
    public void ReadsTheSwitchesAsMostKeysHaveThem()
    {
        var m = Driver(new Board().Customized()).Read().Magnetic!;
        Assert.Equal(new PeripheralMagnetic(2.0, false, 0, 0, 0.3, Uniform: false, StepMm: 0.005), m);
        Assert.True(Driver(new Board()).Read().Magnetic!.Uniform);
    }

    [Fact]
    public void SwitchTravelGoesToEveryKeyAndKeepsKinds()
    {
        var board = new Board().Customized();
        var driver = Driver(board);
        var backup = driver.Read() is { Magnetic: not null } ? driver.Columns! : throw new Exception("no columns");
        var count = board.Sent.Count;

        var s = driver.SetMagnetic(new PeripheralMagnetic(1.5, true, 0.1, 0.15, 0.2));
        Assert.Equal(new PeripheralMagnetic(1.5, true, 0.1, 0.15, 0.2, Uniform: true, StepMm: 0.005), s.Magnetic);
        Assert.Equal(300, board.Columns[0][8]);
        Assert.Equal(300, board.Columns[1][8]);   // release point follows actuation
        Assert.Equal(0, board.Columns[0][100]);   // no key there: untouched
        Assert.Equal(0x87, board.Columns[7][9]);  // snap kept, rapid trigger set
        Assert.Equal(0x80, board.Columns[7][0]);
        Assert.Equal(0, board.Columns[7][100]);
        var writes = board.Sent.Skip(count).Where(p => p[1] == 0x65).ToList();
        Assert.Equal(5 * 5 + 3, writes.Count);    // five u16 columns of 5 pages, the mode column of 3
        Assert.Equal(6, writes.Count(p => p[5] == 1)); // last (flash) once a column

        driver.WriteColumns(backup);
        Assert.Equal(290, board.Columns[0][8]);
        Assert.Equal(7, board.Columns[7][9]);
        Assert.Equal(0, board.Columns[2][0]);
        Assert.False(driver.Read().Magnetic!.Uniform);
    }

    [Fact]
    public void GamemodeRapidTriggerKeepsEachKeysActuationAndKind()
    {
        var board = new Board().Customized();
        var driver = Driver(board);
        driver.Read();
        var actuation = board.Columns[0][8];
        var count = board.Sent.Count;

        driver.SetRapidTrigger(true, 0.1);
        Assert.Equal(actuation, board.Columns[0][8]);  // the user's per-key actuation stays
        Assert.Equal(0x87, board.Columns[7][9]);       // snap kept, rapid trigger set
        Assert.Equal(20, board.Columns[2][8]);         // both steps 0.1 mm (0.005 mm unit)
        Assert.Equal(20, board.Columns[3][8]);
        Assert.Equal(0, board.Columns[7][100]);        // no key there: untouched
        Assert.DoesNotContain(board.Sent.Skip(count), p => p[1] == 0x65 && p[2] == 0); // the actuation column isn't sent

        var sent = board.Sent.Count;
        driver.SetRapidTrigger(true, 0.1);             // already so: nothing written
        Assert.DoesNotContain(board.Sent.Skip(sent), p => p[1] == 0x65);
        driver.SetRapidTrigger(false, 0);
        Assert.Equal(7, board.Columns[7][9]);
        Assert.Throws<ArgumentOutOfRangeException>(() => driver.SetRapidTrigger(true, 3));
    }

    [Fact]
    public void GamemodeStateKeepsTheKeyboardsColumnsAndLight()
    {
        var driver = Driver(new Board().Customized());
        driver.Read();
        var state = new GamemodePeripheralState { Before = { new GamemodePeripheralEntry { KeyboardSwitches = driver.Columns, KeyboardLight = driver.StoredLight } } };
        var json = System.Text.Json.JsonSerializer.Serialize(state, KosToolbox.Core.Persistence.KosJson.Default.GamemodePeripheralState);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, KosToolbox.Core.Persistence.KosJson.Default.GamemodePeripheralState)!.Before[0];
        Assert.Equal(driver.StoredLight, back.KeyboardLight);
        Assert.Equal(driver.Columns!.Actuation, back.KeyboardSwitches!.Actuation);
        Assert.Equal(driver.Columns.Mode, back.KeyboardSwitches.Mode);
        Assert.Equal(driver.Columns.DksActions, back.KeyboardSwitches.DksActions);
        Assert.DoesNotContain("\"Keys\"", json);
    }

    [Fact]
    public void EachCommitWaitsForTheBoardToComeBack()
    {
        var board = new Board { BusyAfterCommit = 6 }.Customized();
        var driver = Driver(board);
        driver.Read();
        var s = driver.SetMagnetic(new PeripheralMagnetic(1.9, false, 0, 0, 0.3));
        Assert.Equal(1.9, s.Magnetic!.ActuationMm);
        Assert.Equal(380, board.Columns[1][8]); // the second column wasn't sent into the busy board
        Assert.True(board.Sent.Count(p => p[1] == RoyuanCodec.Identify) > 2);

        board.BusyAfterCommit = 10_000;
        Assert.Throws<PeripheralException>(() => driver.SetPollingRate(8000));
        Assert.Single(board.Sent, p => p[1] == RoyuanCodec.SetPolling);
    }

    [Fact]
    public void SwitchTravelIsCheckedBeforeAnythingIsSent()
    {
        var board = new Board();
        var driver = Driver(board);
        driver.Read();
        var count = board.Sent.Count;
        Assert.Throws<ArgumentOutOfRangeException>(() => driver.SetMagnetic(new PeripheralMagnetic(3.5, false, 0, 0, 0.3)));
        Assert.Throws<ArgumentOutOfRangeException>(() => driver.SetMagnetic(new PeripheralMagnetic(2.0, true, 0, 0.1, 0.3)));
        Assert.Throws<ArgumentOutOfRangeException>(() => driver.SetMagnetic(new PeripheralMagnetic(2.0, false, 0, 0, 1.5)));
        Assert.Equal(count, board.Sent.Count);

        var s = driver.SetMagnetic(new PeripheralMagnetic(2.0, false, 0, 0, 0.3)); // what it has: nothing to send
        Assert.DoesNotContain(board.Sent.Skip(count), p => p[1] == 0x65);
        Assert.Equal(2.0, s.Magnetic!.ActuationMm);

        board.IgnoreWrites = true;
        var e = Assert.Throws<ReadBackException>(() => driver.SetMagnetic(new PeripheralMagnetic(1.0, false, 0, 0, 0.3)));
        Assert.Contains("actuation", e.Message);
        Assert.Equal(2.0, e.State.Magnetic!.ActuationMm);
    }

    /// <summary>The travel collection: hands out queued input reports; records that it was disposed.</summary>
    sealed class TravelInput(HidCollection c, Queue<byte[]> reports) : IHidTransport, IDisposable
    {
        public bool Disposed;
        public HidCollection Collection { get; } = c;
        public void SetFeature(byte id, ReadOnlySpan<byte> p) => throw new NotSupportedException();
        public byte[] GetFeature(byte id) => throw new NotSupportedException();
        public void Write(byte id, ReadOnlySpan<byte> p, int t = 500) => throw new NotSupportedException();
        public byte[]? Read(int t = 500) => reports.Count > 0 ? reports.Dequeue() : null;
        public byte[] GetInputReport(byte id) => throw new NotSupportedException();
        public void FlushQueue() { }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void KeymapDecodesTheR82AndTheLayoutCoversItsKeys()
    {
        var keys = new RoyuanKey[RoyuanCodec.Slots];
        var bytes = Convert.FromHexString(R82Keymap);
        for (var page = 0; page < 8; page++) RoyuanCodec.DecodeKeymapPage(Buf(Convert.ToHexString(bytes, page * 64, 64)), page, keys);
        Assert.Equal("Esc", keys[0].Label);
        Assert.Equal("A", keys[9].Label);
        Assert.Equal("D", keys[21].Label);
        Assert.Equal("Space", keys[41].Label);
        Assert.Equal("Fn", keys[59].Label);
        Assert.Equal("Mute", keys[91].Label);
        Assert.Equal("Vol+", keys[92].Label);
        Assert.True(keys[10].IsEmpty);

        var layout = RoyuanCatalog.Find(0x3151, 0x502D, 2844)!.Layout!;
        Assert.Equal(80, layout.Count);
        Assert.Equal(80, layout.Select(k => k.Slot).Distinct().Count());
        Assert.All(layout, k => Assert.False(keys[k.Slot].IsEmpty));
        Assert.All(layout, k => Assert.Equal(k.Slot % 6, k.Row)); // the matrix is column-major: slot = column × 6 + row
        Assert.All(layout.GroupBy(k => k.Row), row => Assert.True(row.Max(k => k.X + k.Width) <= 16));
        // every key with a usage on rows 0..5 of columns 0..14 is on the layout (the rest are the knob's entries)
        var mapped = Enumerable.Range(0, 90).Where(s => !keys[s].IsEmpty).ToHashSet();
        Assert.True(mapped.SetEquals(layout.Select(k => k.Slot)));
    }

    [Fact]
    public void KeyTravelChangesOnlyTheChosenKeys()
    {
        var board = new Board().Customized();
        var driver = Driver(board);
        driver.Read();
        Assert.Equal("A", driver.Keymap![9].Label);
        Assert.Equal(new PeripheralMagnetic(1.45, false, 0, 0, 0.3, Uniform: true, StepMm: 0.005), driver.KeyTravel(new[] { 8 }));
        Assert.False(driver.KeyTravel(new[] { 8, 9 })!.Uniform);

        driver.SetKeyTravel(new[] { 9, 21 }, new PeripheralMagnetic(1.2, true, 0.05, 0.05, 0.3));
        Assert.Equal(240, board.Columns[0][9]);
        Assert.Equal(240, board.Columns[0][21]);
        Assert.Equal(0x87, board.Columns[7][21]);
        Assert.Equal(400, board.Columns[0][0]);  // the other keys are untouched
        Assert.Equal(0, board.Columns[7][0]);
        Assert.Equal(290, board.Columns[0][8]);
        Assert.Equal(0, board.Columns[2][0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => driver.SetKeyTravel(Array.Empty<int>(), new PeripheralMagnetic(1.2, false, 0, 0, 0.3)));
    }

    static RoyuanKey Usage(byte u) => new(0, 0, u, 0);
    static readonly RoyuanKey None = default;

    [Fact]
    public void KeyModesWriteTheirColumnsAndSubLayers()
    {
        var board = new Board().Customized();
        var driver = Driver(board);
        driver.Read();
        var a = driver.KeyMode(9)!;
        Assert.Equal((byte)7, a.Kind);
        Assert.Equal(21, a.Partner);
        Assert.Equal("A", a.Layers[0].Label);

        // snap S (15) with F (27)
        var before = board.Sent.Count;
        driver.SetKeyMode(15, a with { Partner = 27, Layers = new[] { Usage(0x16), None, None, None } });
        Assert.Equal(new[] { 15, 27 }, board.Sent.Skip(before).Where(p => p[1] == 0x65 && p[2] == 9).Select(p => (int)p[4]).ToArray()); // partners one slot at a time
        Assert.Equal((7, 27), (board.Columns[7][15], board.Columns[9][15]));
        Assert.Equal((7, 15), (board.Columns[7][27], board.Columns[9][27]));

        // A back to plain: its partner D leaves the pair too
        driver.SetKeyMode(9, a with { Kind = 0, Partner = null });
        Assert.Equal((0, 255), (board.Columns[7][9], board.Columns[9][9]));
        Assert.Equal((0, 255), (board.Columns[7][21], board.Columns[9][21]));

        // Caps: tap Esc, hold Ctrl after 200 ms
        driver.SetKeyMode(3, new RoyuanKeyMode(3, null, 200, 0, new byte[4], new[] { Usage(0xE0), Usage(0x29), None, None }));
        Assert.Equal(3, board.Columns[7][3]);
        Assert.Equal(20, board.Columns[5][3]);
        Assert.Equal(new byte[] { 0, 0, 0xE0, 0 }, board.Layers[0][12..16]);
        Assert.Equal(new byte[] { 0, 0, 0x29, 0 }, board.Layers[1][12..16]);
        Assert.Equal("Esc", driver.KeyMode(3)!.Layers[1].Label);

        // Space: dynamic keystroke, first point 0.5 mm
        driver.SetKeyMode(41, new RoyuanKeyMode(2, null, 0, 0.5, new byte[] { 0x0B, 0x04, 0, 0 }, new[] { Usage(0x2C), Usage(0x04), None, None }));
        Assert.Equal(2, board.Columns[7][41]);
        Assert.Equal(100, board.Columns[4][41]);
        Assert.Equal(0x0B, board.Columns[10][41]);
        Assert.Equal(0x04, board.Columns[10][128 + 41]);
        Assert.Single(board.Sent, p => p[1] == 0x65 && p[2] == 8 && p[4] == 41);

        Assert.Throws<ArgumentOutOfRangeException>(() => driver.SetKeyMode(15, a with { Partner = 15 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => driver.SetKeyMode(15, a with { Partner = 100 })); // no key there
        Assert.Throws<ArgumentOutOfRangeException>(() => driver.SetKeyMode(3, driver.KeyMode(3)! with { HoldMs = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => driver.SetKeyMode(100, a with { Kind = 0 }));
        Assert.Throws<ArgumentException>(() => RoyuanCodec.DksActionsWrite(1, new byte[3]));
    }

    [Fact]
    public void MacrosEncodeAndRoundTrip()
    {
        var m = new RoyuanMacro(1, new RoyuanMacroStep[]
        {
            new(0x04, false, true, 20), new(0x04, false, false, 0), new(0x00, true, true, 300), new(0x00, true, false, 127),
        });
        var blob = m.Encode();
        Assert.Equal(new byte[] { 1, 0, 0x04, 0x80 | 20, 0x04, 0x00, 0, 0, 0xF0, 0x80, 0x2C, 0x01, 0xF0, 127, 0 }, blob[..15]);
        Assert.Equal(m.Steps, RoyuanMacro.Decode(blob).Steps);
        Assert.Equal(1, RoyuanMacro.Decode(blob).Repeat);

        var pages = RoyuanMacro.Pages(7, blob);
        Assert.Single(pages);
        Assert.Equal(new byte[] { 0x0B, 7, 0, 56, 1 }, pages[0][..5]);
        var long_ = new RoyuanMacro(0, Enumerable.Range(0, 120).Select(i => new RoyuanMacroStep(0x04, false, i % 2 == 0, 10)).ToList());
        Assert.Equal(new byte[] { 0, 0, 0, 0, 1 }, RoyuanMacro.Pages(0, long_.Encode()).Select(p => p[4]).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => new RoyuanMacro(0, Enumerable.Range(0, 130).Select(_ => new RoyuanMacroStep(0x04, false, true, 10)).ToList()).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => new RoyuanMacro(0, new[] { new RoyuanMacroStep(0x02, false, true, 10) }).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => new RoyuanMacro(0, new[] { new RoyuanMacroStep(5, true, true, 10) }).Encode());
    }

    [Fact]
    public void RemapFnLayerAndMacrosAreReadBack()
    {
        var board = new Board();
        var driver = Driver(board);
        driver.Read();
        var model = RoyuanCatalog.Find(0x3151, 0x502D, 2844)!;
        Assert.Equal("A", model.Default(9)!.Value.Label);
        Assert.Equal("Fn", model.Default(59)!.Value.Label);

        driver.SetKey(57, 0, new RoyuanKey(0, 0, 0x2C, 0)); // L → Space
        Assert.Equal("Space", driver.Keymap![57].Label);
        driver.SetKey(57, 0, model.Default(57)!.Value);
        Assert.Equal("L", driver.Keymap![57].Label);

        driver.SetFnKey(9, new RoyuanKey(0, 0, 0x50, 0)); // Fn+A → Left
        Assert.Equal("←", driver.FnLayer![9].Label);

        // the R82HE's macro write doesn't store (checked on the board): refused before anything is sent; reads still work
        var macro = new RoyuanMacro(1, new RoyuanMacroStep[] { new(0x0B, false, true, 15), new(0x0B, false, false, 15) });
        board.Macros[3] = macro.Encode();
        Assert.Equal(macro.Steps, driver.ReadMacro(3)!.Steps);
        Assert.False(driver.MacroWrites);
        Assert.Throws<PeripheralException>(() => driver.SetMacro(3, macro));
        Assert.DoesNotContain(board.Sent, p => p[1] == 0x0B);
        driver.SetKey(72, 0, new RoyuanKey(9, 0, 3, 0)); // F12 runs macro 4
        Assert.Equal("M4", driver.Keymap![72].Label);
    }

    [Fact]
    public void PictureUploadIsPagedPacedAndShownInModeThirteen()
    {
        var colors = Enumerable.Range(0, RoyuanCodec.PictureKeys).Select(i => i == 9 ? 0xFF0000 : 0x000010).ToArray();
        var pages = RoyuanCodec.PicturePages(0, colors);
        Assert.Equal(7, pages.Count);
        Assert.Equal(new byte[] { 0x0C, 0, 0xFF, 0, 56, 0 }, pages[0][..6]);
        Assert.Equal(new byte[] { 0x0C, 0, 0xFF, 6, 42, 1 }, pages[6][..6]);
        Assert.Equal(new byte[] { 0xFF, 0, 0 }, pages[0][(8 + 27)..(8 + 30)]); // slot 9 at bytes 27..29
        Assert.Throws<ArgumentException>(() => RoyuanCodec.PicturePages(0, colors[..125]));

        var board = new Board { Mode = 1, Flags = 7 };
        var driver = Driver(board);
        driver.Read();
        var s = driver.SendPicture(colors);
        Assert.Equal(7, board.Sent.Count(p => p[1] == 0x0C));
        Assert.Equal(13, board.Mode); // switched to the picture, brightness and speed kept
        Assert.Equal(LightingEffect.Other, s.Lighting!.Effect);
        Assert.Throws<PeripheralException>(() => driver.SendPicture(colors)); // within 10 s of the last upload
        Assert.Equal(7, board.Sent.Count(p => p[1] == 0x0C));
    }

    [Fact]
    public void TravelStreamTurnsOnReadsAndTurnsOff()
    {
        var board = new Board();
        var driver = Driver(board);
        driver.Read();
        var reports = new Queue<byte[]>(new[]
        {
            new byte[] { 1, 0, 0, 0, 0 },                      // another report id: skipped
            new byte[] { 5, 0x1B, 0x7C, 0x01, 9, 0, 0, 0 },    // slot 9 at 380 counts
            new byte[] { 5, 0x1B, 0, 0, 9, 0, 0, 0 },          // released
        });
        TravelInput? input = null;
        var feature = PeripheralDriver.Opener;
        PeripheralDriver.Opener = (c, rw) => rw ? input = new TravelInput(c, reports) : feature(c, rw);

        var stream = driver.StartTravelStream();
        Assert.Equal(0xFFFF, input!.Collection.UsagePage);
        Assert.Equal(1, input.Collection.Usage);
        Assert.Equal(new byte[] { 0x1B, 1 }, board.Sent[^1][1..3]);
        Assert.Equal((9, 1.9), stream.Next(100));
        Assert.Equal((9, 0.0), stream.Next(100));
        Assert.Null(stream.Next(10));
        stream.Dispose();
        stream.Dispose();
        Assert.Equal(new byte[] { 0x1B, 0 }, board.Sent[^1][1..3]);
        Assert.Single(board.Sent, p => p[1] == 0x1B && p[2] == 0);
        Assert.True(input.Disposed);
    }

    [Fact]
    public void ClaimSendsNothingAndTheRegistryPrefersItOverVia()
    {
        var board = new Board();
        var driver = Driver(board);
        Assert.Empty(board.Sent);
        Assert.Equal(PeripheralKind.Keyboard, driver.Kind);
        Assert.IsType<RoyuanDriver>(PeripheralRegistry.Claim(R82));
        Assert.Null(driver.ReadBattery());
        Assert.Empty(board.Sent);
        Assert.Null(RoyuanDriver.Claim(new HidDevice("x", new[] { Col("mi_02", 0xFFFF, 2, 0, 33) })));
    }

    [Fact]
    public void ReadsTheBoard()
    {
        var board = new Board();
        var s = Driver(board).Read();
        Assert.Equal("Attack Shark R82HE", s.Name);
        Assert.Equal("5.05", s.Firmware);
        Assert.Equal(1000, s.Polling!.Hz);
        Assert.Equal(0, s.Brightness);
        Assert.Equal(LightingEffect.Other, s.Lighting!.Effect);
        Assert.Equal(1, s.Profile);
        Assert.Equal(4, s.Profiles);
        Assert.Equal(0x8F, board.Sent[0][1]);
        Assert.Null(s.Battery);
    }

    [Fact]
    public void AnotherBoardGetsIdentifyAndNothingElse()
    {
        var board = new Board(id: 1466);
        var driver = Driver(board);
        var s = driver.Read();
        driver.Read();
        Assert.Contains("1466", s.Protocol);
        Assert.Single(board.Sent);
        Assert.Empty(driver.Writable);
        Assert.Throws<PeripheralException>(() => driver.SetPollingRate(1000));
        Assert.Single(board.Sent);
    }

    [Fact]
    public void AReplyWithoutTheEchoIsAFailure()
    {
        var board = new Board();
        PeripheralDriver.Delay = _ => { };
        var stale = Convert.FromHexString("00" + Polling1000.Replace(" ", "") + new string('0', 112));
        PeripheralDriver.Opener = (c, _) => new FakeTransport(c, (req, _) => req[1] == 0x8F ? board.Reply(req, 0) : stale);
        var s = RoyuanDriver.Claim(R82)!.Read();
        Assert.Equal(1000, s.Polling!.Hz); // the 83 request is answered by an 83 reply
        Assert.Null(s.Lighting);           // the 87 request got the previous (83) reply: not an answer
        Assert.Null(s.Profile);
    }

    [Fact]
    public void WritesAreReadBack()
    {
        var board = new Board();
        var driver = Driver(board);
        driver.Read();
        Assert.Contains(PeripheralSetting.Profile, driver.Writable);

        Assert.Equal(8000, driver.SetPollingRate(8000).Polling!.Hz);
        Assert.Equal(75, driver.SetBrightness(75).Brightness);
        Assert.Equal(13, board.Mode); // the brightness alone changes: the per-key picture mode stays
        Assert.Equal(new PeripheralLighting(LightingEffect.Static, 0xFFFFFF), driver.SetLighting(LightingEffect.Static, 0xFFFFFF).Lighting);
        Assert.Equal(0xFAFAFA, board.Color);
        Assert.Equal(3, driver.SetProfile(3).Profile);
        Assert.Equal(50, driver.SetBrightness(60).Brightness); // rounded to the board's step
        Assert.Equal(75, driver.SetBrightness(75).Brightness);
        Assert.Throws<ArgumentOutOfRangeException>(() => driver.SetBrightness(101));
        Assert.Throws<ArgumentOutOfRangeException>(() => driver.SetProfile(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => driver.SetLighting(LightingEffect.Off, 0));

        board.IgnoreWrites = true;
        var count = board.Sent.Count;
        var e = Assert.Throws<ReadBackException>(() => driver.SetPollingRate(1000));
        Assert.Equal(8000, e.State.Polling!.Hz);
        Assert.Single(board.Sent.Skip(count), p => p[1] == RoyuanCodec.SetPolling); // sent once
    }
}
