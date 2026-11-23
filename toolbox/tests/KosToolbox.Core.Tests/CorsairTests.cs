using System.ComponentModel;
using KosToolbox.Core.Peripherals;
using KosToolbox.Core.Peripherals.Batteries;
using KosToolbox.Core.Peripherals.Corsair;
using KosToolbox.Core.Peripherals.Hid;
using KosToolbox.Core.Peripherals.Odm;
using Xunit;

namespace KosToolbox.Core.Tests;

/// <summary>
/// The friend's Corsair devices as the read-only probe saw them (2026-11-23): a SABRE v2 PRO behind its 2B2A receiver (report 8), a K65
/// RGB Mini (Bragi, unnumbered 1025-byte reports) and a Virtuoso behind the Slipstream receiver 0A42 (Bragi, numbered 64-byte reports).
/// The simulated devices answer with the probe's own bytes; writes change their memory so the read-back can be checked.
/// </summary>
[Collection("PeripheralDriver statics")]
public class CorsairTests
{
    static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    static HidCollection Col(ushort pid, string hw, ushort page, ushort usage, int input, int output, int feature = 0) =>
        new($@"\\?\hid#vid_1b1c&pid_{pid:x4}&{hw}#8&1&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}", 0x1B1C, pid, 0x0200, page, usage, input, output, feature, "Corsair", Guid.NewGuid(), false);

    static HidDevice SabreReceiver() => new("sabre", new[]
    {
        Col(0x2B2A, "mi_01&col01", 0xFF05, 0, 8, 0), Col(0x2B2A, "mi_01&col05", 0xFF02, 2, 17, 17), Col(0x2B2A, "mi_01&col06", 0xFF04, 2, 0, 0, 8),
        Col(0x2B2A, "mi_01&col07", 0xFF06, 2, 49, 49), Col(0x2B2A, "mi_02", 1, 6, 9, 2),
    });

    static HidDevice K65() => new("k65", new[]
    {
        Col(0x1BAF, "mi_00&col01", 1, 6, 16, 2), Col(0x1BAF, "mi_01", 0xFF42, 1, 1025, 1025), Col(0x1BAF, "mi_02", 0xFF42, 2, 65, 0),
    });

    static HidDevice Slipstream() => new("slipstream", new[]
    {
        Col(0x0A42, "mi_03&col01", 1, 6, 15, 2), Col(0x0A42, "mi_03&col05", 0xFF42, 1, 64, 64), Col(0x0A42, "mi_03&col06", 0xFF42, 2, 64, 0),
        Col(0x0A42, "mi_03&col07", 0xFF58, 1, 64, 64), Col(0x0A42, "mi_04", 1, 2, 16, 0),
    });

    /// <summary>A transport that hands each written report to a device simulation and returns its replies.</summary>
    sealed class SimTransport(HidCollection collection, Action<byte, byte[], Queue<byte[]>> onWrite) : IHidTransport
    {
        readonly Queue<byte[]> _replies = new();
        public HidCollection Collection { get; } = collection;
        public void SetFeature(byte reportId, ReadOnlySpan<byte> payload) => throw new NotSupportedException();
        public byte[] GetFeature(byte reportId) => throw new NotSupportedException();
        public byte[] GetInputReport(byte reportId) => throw new NotSupportedException();
        public void FlushQueue() => _replies.Clear();
        public void Write(byte reportId, ReadOnlySpan<byte> payload, int timeoutMs = 500) => onWrite(reportId, HidBuffer.Build(reportId, payload, Collection.OutputLength), _replies);
        public byte[]? Read(int timeoutMs = 500) => _replies.Count > 0 ? _replies.Dequeue() : null;
    }

    // ---------- SABRE v2 PRO (report 8) ----------

    // Memory of the friend's mouse (cmd 08 replies, 10 bytes per chunk), address → data.
    static readonly (int At, string Data)[] Dump =
    {
        (0x000, "01 54 03 52 01 54 00 55 00 55"), (0x00A, "00 55 0D 0D 00 3B 0F 0F 00 37"), (0x014, "C7 C7 00 C7 1F 1F 00 17 27 27"),
        (0x01E, "00 07 27 27 00 07 27 27 00 07"), (0x028, "27 27 00 07 FF 00 00 56 00 00"), (0x032, "FF 56 00 FF 00 56 FF FF 00 57"),
        (0x03C, "FF 00 FF 57 FF 00 FF 57 FF 00"), (0x046, "FF 57 FF 00 FF 57 02 53 80 D5"), (0x050, "03 52 00 55 FF 00 FF 57 00 55"),
        (0x05A, "80 D5 03 52 00 55 01 01 00 53"), (0x064, "01 02 00 52 01 04 00 50 01 08"), (0x06E, "00 4C 01 10 00 44 02 01 00 52"),
        (0x078, "02 02 00 51 02 03 00 50 07 00"), (0x082, "00 4E 08 04 00 49 04 0A 03 44"), (0x08C, "00 00 00 55 00 00 00 55 00 00"),
        (0x096, "00 55 00 00 00 55 00 00 00 55"), (0x0A0, "01 FF 00 FF 07 09 46 00 55 08"), (0x0AA, "4D 01 54 06 4F 00 55 00 55 00"),
        (0x0B4, "55 00 55 06 4F 00 55 0A 4B FF"), (0x0D2, "FF FF FF FF FF FF FF FF FF 2D"), (0x0DC, "0F 30 E9 01 54 FF FF FF FF FF"),
    };

    // The probe's own replies (Win32 buffers, id first).
    const string BatteryReply = "08 04 00 00 00 02 28 00 0E C3 00 00 00 00 00 00 4E";
    const string MouseFirmwareReply = "08 12 00 00 00 02 02 24 00 00 00 00 00 00 00 00 13";
    const string ReceiverFirmwareReply = "08 1D 00 00 00 02 02 21 00 00 00 00 00 00 00 00 0B";

    /// <summary>The mouse: answers 04, 12, 1D, 08 (read) and 07 (write) like the probe saw it.</summary>
    sealed class SabreSim
    {
        public readonly byte[] Memory = Enumerable.Repeat((byte)0xFF, 0x200).ToArray();
        public readonly List<byte[]> Writes = new();
        public bool IgnoreWrites;
        public bool NoAck;       // the write lands but is never acknowledged (the ack shape of 07 is unproven)
        public bool Silent;

        public SabreSim() { foreach (var (at, data) in Dump) Hex(data).CopyTo(Memory, at); }

        static byte[] Reply(byte cmd, byte status, int addr, byte[] data)
        {
            var r = new byte[17];
            r[0] = 8; r[1] = cmd; r[2] = status; r[3] = (byte)(addr >> 8); r[4] = (byte)addr; r[5] = (byte)data.Length;
            data.CopyTo(r, 6);
            r[16] = Report8Codec.Checksum(r.AsSpan(1, 16));
            return r;
        }

        public void OnWrite(byte id, byte[] buffer, Queue<byte[]> replies)
        {
            Assert.Equal(8, id);
            Assert.Equal(Report8Codec.Checksum(buffer.AsSpan(1, 16)), buffer[16]); // every request carries a valid checksum
            if (Silent) return;
            var cmd = buffer[1];
            var addr = buffer[3] << 8 | buffer[4];
            var len = buffer[5];
            switch (cmd)
            {
                case 0x04: replies.Enqueue(Hex(BatteryReply)); break;
                case 0x12: replies.Enqueue(Hex(MouseFirmwareReply)); break;
                case 0x1D: replies.Enqueue(Hex(ReceiverFirmwareReply)); break;
                case 0x08: replies.Enqueue(Reply(cmd, 0, addr, Memory.AsSpan(addr, len).ToArray())); break;
                case 0x07:
                    Writes.Add(buffer);
                    if (!IgnoreWrites) buffer.AsSpan(6, len).CopyTo(Memory.AsSpan(addr));
                    if (!NoAck) replies.Enqueue(Reply(cmd, 0, addr, buffer.AsSpan(6, len).ToArray()));
                    break;
                default: throw new InvalidOperationException($"The driver sent command {cmd:X2}, which it must never send.");
            }
        }
    }

    static SabreDriver Sabre(SabreSim sim)
    {
        PeripheralDriver.Delay = _ => { };
        PeripheralDriver.Opener = (c, _) => new SimTransport(c, sim.OnWrite);
        return SabreDriver.Claim(SabreReceiver())!;
    }

    [Fact]
    public void TheProbesRepliesDecode()
    {
        Assert.Equal(new PeripheralBattery(40, false), SabreCodec.DecodeBattery(Hex(BatteryReply)));
        Assert.Equal("2.24", SabreCodec.DecodeFirmware(Hex(MouseFirmwareReply), SabreCodec.MouseFirmware));
        Assert.Equal("2.21", SabreCodec.DecodeFirmware(Hex(ReceiverFirmwareReply), SabreCodec.ReceiverFirmware));
        Assert.Null(SabreCodec.DecodeFirmware(Hex(MouseFirmwareReply), SabreCodec.ReceiverFirmware));
        var bad = Hex(BatteryReply);
        bad[16] ^= 1;
        Assert.Null(SabreCodec.DecodeBattery(bad));
        // The request checksum is the codec's 0x55 − Σ with the report id counted: 08+08+0A = 1A → 3B.
        Assert.Equal(0x3B, SabreCodec.ReadRequest(0x00, 0x0A)[15]);
    }

    [Fact]
    public void TheFriendsMemoryDecodesToWhatICueShowed()
    {
        var sim = new SabreSim();
        var s = SabreCodec.Decode(sim.Memory[..SabreCodec.ImageLength]);
        Assert.Equal(1000, s.PollingHz);                                   // iCUE: 1000 Hz
        Assert.Equal(3, s.StageCount);
        Assert.Equal(1, s.ActiveStage);
        Assert.Equal(new[] { 700, 800, 10000 }, s.StageDpi);
        Assert.Equal(SabreLift.Low, s.LiftOff);                            // "Baixo"
        Assert.True(s.MotionSync);                                         // on
        Assert.False(s.AngleSnap);                                         // off
        Assert.False(s.Ripple);                                            // off
        Assert.Equal(60, s.SleepSeconds);                                  // "1 Minuto"
        Assert.True(s.SurfaceCalibration);                                 // a surface was chosen, lift-off is greyed out
        Assert.Equal(new[] { 0xFF0000, 0x0000FF, 0x00FF00 }, s.StageColors);
        Assert.Equal(new SabreAction?[] { SabreAction.Left, SabreAction.Right, SabreAction.Middle, SabreAction.Back, SabreAction.Forward }, s.Buttons);
    }

    [Fact]
    public void ADamagedFieldIsNotDecodedAndEveryStageChecksumHolds()
    {
        var sim = new SabreSim();
        sim.Memory[0x0B] ^= 1; // lift-off checksum
        sim.Memory[0x11] ^= 1; // a DPI stage
        var s = SabreCodec.Decode(sim.Memory[..SabreCodec.ImageLength]);
        Assert.Null(s.LiftOff);
        Assert.Null(s.StageDpi);
        Assert.Equal(1000, s.PollingHz);
        foreach (var dpi in new[] { 100, 400, 800, 1600, 10000, 12800 })
        {
            var block = SabreCodec.EncodeStage(dpi);
            Assert.True(SabreCodec.BlockValid(block, 0, 4));
            Assert.Equal(dpi, SabreCodec.DecodeStage(block, 0));
        }
        // Higher stages use the page byte 44 / 88 (read as 256 / 512 more steps); only decoded, never written.
        Assert.Equal(15050, SabreCodec.DecodeStage(new byte[] { 0x2C, 0x2C, 0x44, 0xB9 }, 0));
        Assert.Null(SabreCodec.DecodeStage(new byte[] { 0x2C, 0x2C, 0x45, 0xB8 }, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SabreCodec.EncodeStage(12850));
        Assert.Throws<ArgumentOutOfRangeException>(() => SabreCodec.EncodeStage(75));
    }

    [Fact]
    public void TheSabreIsClaimedByItsReportCollectionOnly()
    {
        PeripheralDriver.Delay = _ => { };
        Assert.IsType<SabreDriver>(PeripheralRegistry.Claim(SabreReceiver())); // not the battery-only ODM reader
        var noCommands = new HidDevice("x", SabreReceiver().Collections.Where(c => c.UsagePage != 0xFF02).ToList());
        Assert.Null(SabreDriver.Claim(noCommands));
    }

    [Fact]
    public void TheSabreReadsEverythingTheProbeSaw()
    {
        var sim = new SabreSim();
        var d = Sabre(sim);
        Assert.False(d.Tested);
        Assert.True(d.StoredOnly);
        Assert.Empty(d.Writable);
        var s = d.Read();
        Assert.Equal(new PeripheralBattery(40, false), s.Battery);
        Assert.Equal("2.24 (receiver 2.21)", s.Firmware);
        Assert.Equal(800, s.Dpi!.X);
        Assert.Equal(2, s.Dpi.ActiveStage);
        Assert.Equal(new[] { 700, 800, 10000 }, s.Dpi.Stages!.Select(x => x.X));
        Assert.Equal(1000, s.Polling!.Hz);
        Assert.Equal(new[] { 1000, 2000, 4000, 8000 }, s.Polling.Supported);
        Assert.Equal(60, s.SleepSeconds);
        Assert.True(d.Writable.SetEquals(new[] { PeripheralSetting.Dpi, PeripheralSetting.ActiveStage, PeripheralSetting.PollingRate, PeripheralSetting.Sleep }));
        Assert.Equal(new SabreExtras(SabreLift.Low, true, true, false, false, d.Extras!.Buttons, d.Extras.StageColors), d.Extras);
        Assert.Empty(sim.Writes);
        Assert.Equal(new PeripheralBattery(40, false), d.ReadBattery());
        Assert.Equal(PeripheralConnection.Receiver, d.Connection);
    }

    [Fact]
    public void ASilentMouseReadsAsAsleepAndIsNeverWritten()
    {
        var sim = new SabreSim { Silent = true };
        var d = Sabre(sim);
        var s = d.Read();
        Assert.True(s.Battery!.Asleep);
        Assert.Null(s.Dpi);
        Assert.Empty(d.Writable);
        Assert.Throws<ArgumentOutOfRangeException>(() => d.SetDpi(800, 400));
        sim.Silent = false;
        d.Read();
        sim.Silent = true;
        Assert.Throws<PeripheralAsleepException>(() => d.SetPollingRate(2000));
        Assert.Empty(sim.Writes);
    }

    [Fact]
    public void WritesGoToTheRightFieldWithTheirChecksumAndAreReadBack()
    {
        var sim = new SabreSim();
        var d = Sabre(sim);
        d.Read();

        var s = d.SetDpi(1600, 1600); // the active stage is 2 (address 10)
        Assert.Equal("07 00 00 10 04 1F 1F 00 17".Replace(" ", ""), Convert.ToHexString(sim.Writes[0][1..10]));
        Assert.Equal(1600, s.Dpi!.X);
        Assert.Equal(1600, s.Dpi.Stages![1].X);
        Assert.Equal(700, s.Dpi.Stages[0].X); // the others stay

        s = d.SetPollingRate(8000);
        Assert.Equal(new byte[] { 0x40, 0x15 }, sim.Memory[0..2]);
        Assert.Equal(8000, s.Polling!.Hz);

        s = d.SetSleep(300);
        Assert.Equal(new byte[] { 30, 0x37 }, sim.Memory[0xAD..0xAF]);
        Assert.Equal(300, s.SleepSeconds);

        s = d.SetActiveStage(3);
        Assert.Equal(new byte[] { 2, 0x53 }, sim.Memory[4..6]);
        Assert.Equal(3, s.Dpi!.ActiveStage);

        d.SetMotionSync(false);
        d.SetAngleSnap(true);
        d.SetRipple(true);
        Assert.Equal(new byte[] { 0, 0x55 }, sim.Memory[0xAB..0xAD]);
        Assert.Equal(new byte[] { 1, 0x54 }, sim.Memory[0xAF..0xB1]);
        Assert.Equal(new byte[] { 1, 0x54 }, sim.Memory[0xB1..0xB3]);
        Assert.Equal(new bool?[] { false, true, true }, new[] { d.Extras!.MotionSync, d.Extras.AngleSnap, d.Extras.Ripple });

        d.SetButton(5, SabreAction.DpiCycle);
        Assert.Equal(new byte[] { 2, 1, 0, 0x52 }, sim.Memory[0x70..0x74]);
        Assert.Equal(SabreAction.DpiCycle, d.Extras!.Buttons![4]);
        Assert.Equal(new byte[] { 1, 1, 0, 0x53 }, sim.Memory[0x60..0x64]); // the left click is untouched
        Assert.All(sim.Writes, w => Assert.Equal(7, w[1]));
    }

    [Fact]
    public void RefusalsSendNothing()
    {
        var sim = new SabreSim();
        var d = Sabre(sim);
        d.Read();
        Assert.Throws<ArgumentOutOfRangeException>(() => d.SetDpi(12850, 12850));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.SetDpi(825, 825));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.SetPollingRate(500));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.SetSleep(120));       // not one of the hub's times
        Assert.Throws<ArgumentOutOfRangeException>(() => d.SetActiveStage(4));   // the mouse has 3 stages
        Assert.Throws<ArgumentOutOfRangeException>(() => d.SetButton(6, SabreAction.Left));
        Assert.Throws<PeripheralException>(() => d.SetButton(1, SabreAction.Disabled)); // the only left click
        Assert.Throws<PeripheralException>(() => d.SetLiftOff(SabreLift.High));         // surface calibration is on
        Assert.Empty(sim.Writes);
        sim.Memory[0xDF] = 0; sim.Memory[0xE0] = 0x55; // calibration off: lift-off is allowed
        d.Read();
        Assert.Equal(SabreLift.High, d.SetLiftOff(SabreLift.High) is not null ? d.Extras!.LiftOff : null);
        Assert.Equal(new byte[] { 4, 0x51 }, sim.Memory[0x0A..0x0C]);
    }

    [Fact]
    public void AFieldThatDoesNotValidateIsNotWritten()
    {
        var sim = new SabreSim();
        var d = Sabre(sim);
        d.Read();
        sim.Memory[0xAC] ^= 1; // motion sync's checksum is damaged since the read
        Assert.Throws<PeripheralException>(() => d.SetMotionSync(false));
        Assert.Empty(sim.Writes);
    }

    [Fact]
    public void AWriteTheMouseDoesNotKeepIsReportedFromTheReadBack()
    {
        var sim = new SabreSim();
        var d = Sabre(sim);
        d.Read();
        sim.IgnoreWrites = true;
        var ex = Assert.Throws<ReadBackException>(() => d.SetPollingRate(4000));
        Assert.Equal(1000, ex.State.Polling!.Hz); // what the mouse has, not what was asked
        Assert.Single(sim.Writes);
    }

    // ---------- Bragi (K65 RGB Mini, Virtuoso) ----------

    /// <summary>Bragi property store per target, with the probe's replies; unknown properties answer status 05.</summary>
    sealed class BragiSim
    {
        public readonly Dictionary<byte, Dictionary<ushort, uint>> Props = new();
        public readonly List<(byte Target, ushort Property, uint Value)> Sets = new();
        public bool RequireSoftware;
        public bool PrefixedForm;           // a 64-byte unnumbered collection: id 0 with 02 in front
        public int DropAfterPolling;        // transport operations that fail after a polling SET (the device restarts)
        public int Dropped;
        public bool IgnoreSets;
        public bool DropSetReply;           // a SET lands but its reply never arrives

        public SimTransport Open(HidCollection c)
        {
            var numbered = c.OutputLength != 1025;
            return new SimTransport(c, (id, buffer, replies) =>
            {
                if (Dropped > 0) { Dropped--; throw new Win32Exception(31); }
                var p = buffer.AsSpan(1);
                if (numbered && !PrefixedForm) { if (id != 2) throw new Win32Exception(87); }
                else if (PrefixedForm) { if (id != 0) throw new Win32Exception(87); p = p[1..]; Assert.Equal(2, buffer[1]); }
                else Assert.Equal(0, id);
                var target = p[0];
                var op = p[1];
                var property = (ushort)(p[2] | p[3] << 8);
                var value = (uint)(p[4] | p[5] << 8 | p[6] << 16 | p[7] << 24);
                Assert.True(op is 1 or 2, $"opcode {op:X2} must never be sent");
                byte status;
                var props = Props[target];
                if (op == 2) status = props.ContainsKey(property) ? (byte)0 : (byte)5;
                else
                {
                    Sets.Add((target, property, value));
                    Assert.True(property != 3 || value is 1 or 2, "mode 3 and 5 are bootloaders");
                    if (!props.ContainsKey(property)) status = 5;
                    else if (RequireSoftware && property != 3 && props[3] != 2) status = 9;
                    else { status = 0; if (!IgnoreSets) props[property] = value; if (property == 1 && DropAfterPolling > 0) Dropped = DropAfterPolling; }
                }
                var r = new byte[c.InputLength];
                r[0] = (byte)(numbered ? 1 : 0);
                r[1] = (byte)(target == 8 ? 0 : 1);
                r[2] = op;
                r[3] = status;
                var v = status == 0 && op == 2 ? props[property] : 0;
                r[4] = (byte)v; r[5] = (byte)(v >> 8); r[6] = (byte)(v >> 16); r[7] = (byte)(v >> 24);
                if (!(DropSetReply && op == 1)) replies.Enqueue(r);
            });
        }
    }

    static BragiSim K65Sim() => new()
    {
        Props = { [8] = new() { [3] = 1, [1] = 7, [0x96] = 7, [2] = 1000, [0x41] = 1, [0x13] = 0x301200, [0x14] = 0x110700, [0x11] = 0x1B1C, [0x12] = 0x1BAF,
            [0x45] = 0, [0x4A] = 0, [0x63] = 2, [0x9B] = 2, [0x9C] = 2, [0x6E] = 0 } },
    };

    // The Virtuoso behind receiver 0A42: target 09 is the headset, target 08 the receiver (the probe's replies).
    static BragiSim VirtuosoSim() => new()
    {
        Props =
        {
            [9] = new() { [3] = 1, [2] = 0, [0x13] = 0x460305, [0x11] = 0x1B1C, [0x12] = 0x0A41, [0x0F] = 0x02C6, [0x10] = 2, [0x0D] = 1, [0x0E] = 300000 },
            [8] = new() { [3] = 1, [1] = 4, [2] = 0, [0x13] = 0x820905, [0x11] = 0x1B1C, [0x12] = 0x0A42, [0x36] = 2 },
            [10] = new(), [11] = new(),
        },
    };

    static BragiDriver Bragi(BragiSim sim, HidDevice device)
    {
        PeripheralDriver.Delay = _ => { };
        PeripheralDriver.Opener = (c, _) => sim.Open(c);
        return BragiDriver.Claim(device)!;
    }

    [Fact]
    public void TheK65ReadsWhatICueShowed()
    {
        var d = Bragi(K65Sim(), K65());
        Assert.Equal(PeripheralKind.Keyboard, d.Kind);
        Assert.False(d.Tested);
        var s = d.Read();
        Assert.Equal("0.18.48", s.Firmware);                                // iCUE: v. 0.18.48
        Assert.Equal(8000, s.Polling!.Hz);                                  // iCUE: 8000 Hz
        Assert.Equal(new[] { 125, 250, 500, 1000, 2000, 4000, 8000 }, s.Polling.Supported);
        Assert.Equal(100, s.Brightness);                                    // the slider is at the top
        Assert.Null(s.Battery);
        Assert.Null(s.SleepSeconds);                                        // 0D/0E answer 05: no auto-off
        Assert.False(d.WinLockOn);
        Assert.True(d.Writable.SetEquals(new[] { PeripheralSetting.PollingRate, PeripheralSetting.Brightness }));
    }

    [Fact]
    public void TheK65WritesInHardwareModeWithoutTouchingTheMode()
    {
        var sim = K65Sim();
        var d = Bragi(sim, K65());
        d.Read();
        var s = d.SetBrightness(50);
        Assert.Equal(50, s.Brightness);
        Assert.Equal(new[] { ((byte)8, (ushort)2, 500u) }, sim.Sets);
        s = d.SetPollingRate(1000);
        Assert.Equal(1000, s.Polling!.Hz);
        Assert.DoesNotContain(sim.Sets, x => x.Property == 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => d.SetBrightness(101));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.SetPollingRate(750));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.SetSleep(300)); // keyboard: no auto-off
    }

    [Fact]
    public void SoftwareModeIsEnteredOnlyWhenRefusedAndAlwaysLeft()
    {
        var sim = K65Sim();
        sim.RequireSoftware = true;
        var d = Bragi(sim, K65());
        d.Read();
        var s = d.SetPollingRate(1000);
        Assert.Equal(1000, s.Polling!.Hz);
        Assert.Equal(new (byte, ushort, uint)[] { (8, 1, 4), (8, 3, 2), (8, 1, 4), (8, 3, 1) }, sim.Sets);
        Assert.Equal(1u, sim.Props[8][3]); // back in hardware mode

        // A refusal that isn't "software mode needed" is reported, not worked around.
        sim.Props[8][0x45] = 0;
        sim.IgnoreSets = true;
        Assert.Throws<ReadBackException>(() => d.SetBrightness(10));
        Assert.Equal(1u, sim.Props[8][3]);
    }

    [Fact]
    public void ABootloaderModeAndTheDestructiveOpcodesCannotBeBuilt()
    {
        var t = new SimTransport(K65().Collections[1], (_, _, _) => throw new InvalidOperationException("nothing may be sent"));
        var link = new BragiLink(t);
        Assert.Throws<ArgumentOutOfRangeException>(() => link.SetProperty(8, BragiLink.ModeProperty, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => link.SetProperty(8, BragiLink.ModeProperty, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => link.SetProperty(8, BragiLink.ModeProperty, 0));
        foreach (byte op in new byte[] { 0x03, 0x05, 0x06, 0x07, 0x09, 0x0B, 0x0C, 0x0D, 0x0F, 0x12, 0x1B })
            Assert.Throws<ArgumentOutOfRangeException>(() => BragiLink.Payload(8, op, 1));
        Assert.Equal(new byte[] { 8, 2, 0x13, 0, 0, 0, 0, 0 }, BragiLink.Payload(8, BragiLink.Get, 0x13));
        Assert.Equal(new byte[] { 8, 1, 0x02, 0, 0xF4, 0x01, 0, 0 }, BragiLink.Payload(8, BragiLink.Set, 0x02, 500));
    }

    [Fact]
    public void WinLockSelectsTheWindowsKeyWhenNoneIsSelected()
    {
        var sim = K65Sim();
        var d = Bragi(sim, K65());
        d.Read();
        d.SetWinLock(true);
        Assert.Equal(new (byte, ushort, uint)[] { (8, 0x4A, 1), (8, 0x45, 1) }, sim.Sets);
        Assert.True(d.WinLockOn);
        sim.Sets.Clear();
        d.SetWinLock(false);
        Assert.Equal(new (byte, ushort, uint)[] { (8, 0x45, 0) }, sim.Sets);
        Assert.False(d.WinLockOn);
    }

    [Fact]
    public void ASetWhoseReplyNeverComesIsReadBackNotCalledLostAndTheModeIsLeft()
    {
        var sim = K65Sim();
        sim.DropSetReply = true;
        var d = Bragi(sim, K65());
        d.Read();
        var s = d.SetPollingRate(2000);
        Assert.Equal(2000, s.Polling!.Hz);       // from the read-back
        Assert.Equal(1u, sim.Props[8][3]);       // the mode was never touched
        Assert.DoesNotContain(sim.Sets, x => x.Property == 3 && x.Value is not (1 or 2));
        sim.IgnoreSets = true;                   // and when the value didn't land, the read-back says so
        Assert.Throws<ReadBackException>(() => d.SetBrightness(10));
    }

    [Fact]
    public void TheSabreWriteIsSentOnceEvenWhenNoAckComes()
    {
        var sim = new SabreSim { NoAck = true };
        var d = Sabre(sim);
        d.Read();
        var s = d.SetPollingRate(4000);
        Assert.Single(sim.Writes);               // a setter is never sent twice
        Assert.Equal(4000, s.Polling!.Hz);       // confirmed by the read-back alone
    }

    [Fact]
    public void TheNumberedFormIsKeptWhenAWriteFailsForAnotherReason()
    {
        var calls = new List<byte>();
        var link = new BragiLink(new SimTransport(Slipstream().Collections[1], (id, _, _) => { calls.Add(id); throw new Win32Exception(31); }));
        Assert.Throws<Win32Exception>(() => link.GetProperty(9, 0x12));
        Assert.Throws<Win32Exception>(() => link.GetProperty(9, 0x12));
        Assert.Equal(new byte[] { 2, 2 }, calls); // never fell back to id 0
    }

    [Fact]
    public void ARestartAfterThePollingChangeIsWaitedForAndReadBack()
    {
        var sim = K65Sim();
        sim.DropAfterPolling = 2; // the next two transport operations fail, as when the keyboard re-enumerates
        var d = Bragi(sim, K65());
        d.Read();
        var s = d.SetPollingRate(2000);
        Assert.Equal(2000, s.Polling!.Hz);
    }

    [Fact]
    public void TheVirtuosoIsReadBehindItsReceiver()
    {
        var d = Bragi(VirtuosoSim(), Slipstream());
        Assert.IsType<BragiDriver>(PeripheralRegistry.Claim(Slipstream()));
        Assert.Equal(PeripheralKind.Headset, d.Kind);
        Assert.Equal(PeripheralConnection.Receiver, d.Connection);
        var s = d.Read();
        Assert.Equal("5.3.70", s.Firmware);                                 // iCUE: v. 5.3.70
        Assert.Equal(new PeripheralBattery(71, false), s.Battery);          // 0F = 710, 10 = 2
        Assert.Equal(300, s.SleepSeconds);                                  // iCUE: 5 min
        Assert.Equal(0, s.Brightness);
        Assert.Null(s.Polling);                                             // 01 isn't answered on the headset
        Assert.True(d.Writable.SetEquals(new[] { PeripheralSetting.Brightness, PeripheralSetting.Sleep }));
        Assert.Equal(new PeripheralBattery(71, false), d.ReadBattery());
    }

    [Fact]
    public void TheVirtuosoSleepAndBrightnessAreWrittenToTheHeadsetAndReadBack()
    {
        var sim = VirtuosoSim();
        var d = Bragi(sim, Slipstream());
        d.Read();
        var s = d.SetSleep(600);
        Assert.Equal(600, s.SleepSeconds);
        Assert.Equal(new (byte, ushort, uint)[] { (9, 0x0D, 1), (9, 0x0E, 600000) }, sim.Sets);
        s = d.SetBrightness(40);
        Assert.Equal(40, s.Brightness);
        Assert.All(sim.Sets, x => Assert.Equal(9, x.Target)); // never the receiver itself
        Assert.Throws<ArgumentOutOfRangeException>(() => d.SetSleep(30));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.SetSleep(91 * 60));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.SetPollingRate(1000));
    }

    [Fact]
    public void AHeadsetThatIsOffReadsAsAsleep()
    {
        var sim = VirtuosoSim();
        sim.Props[9].Clear(); // the receiver relays nothing for it
        var d = Bragi(sim, Slipstream());
        Assert.True(d.Read().Battery!.Asleep);
        Assert.True(d.ReadBattery()!.Asleep);
        Assert.Empty(d.Writable);
        Assert.Throws<PeripheralAsleepException>(() => d.SetBrightness(10));
        Assert.Empty(sim.Sets);
    }

    [Fact]
    public void TheNumberedReceiverReportWorksInTheBatteryReaderAndAnUnnumberedOneStillFallsBack()
    {
        var sim = VirtuosoSim();
        PeripheralDriver.Delay = _ => { };
        PeripheralDriver.Opener = (c, _) => sim.Open(c);
        var reader = VendorBatteryDriver.Claim(Slipstream())!;
        Assert.Equal(new PeripheralBattery(71, false), reader.ReadBattery());

        // A 64-byte collection that isn't numbered: Windows refuses report 02 (error 87) and the id-0 form with 02 in front is used.
        var plain = VirtuosoSim();
        plain.PrefixedForm = true;
        var link = new BragiLink(plain.Open(Slipstream().Collections[1]));
        Assert.Equal(0x0A41u, link.GetProperty(9, 0x12)!.Value.Value);
        Assert.Equal(0x0A41u, link.GetProperty(9, 0x12)!.Value.Value);
    }

    [Fact]
    public void TheCorsairPidsAreClaimedByExactCollectionOnly()
    {
        PeripheralDriver.Delay = _ => { };
        Assert.IsType<BragiDriver>(PeripheralRegistry.Claim(K65()));
        // The K65's second vendor collection (FF42/0002, input only) is not the control one; without MI_01 nothing is claimed by the Bragi driver.
        Assert.Null(BragiDriver.Claim(new HidDevice("k", K65().Collections.Where(c => c.OutputLength != 1025).ToList())));
        // 1B1C:1BF0 (a Bluetooth HID device without a vendor collection) is left alone.
        var unknown = new HidDevice("bt", new[] { Col(0x1BF0, "col01", 1, 2, 5, 0, 2) });
        Assert.Null(SabreDriver.Claim(unknown));
        Assert.Null(BragiDriver.Claim(unknown));
    }
}
