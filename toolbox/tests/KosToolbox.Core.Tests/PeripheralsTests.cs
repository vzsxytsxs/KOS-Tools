using System.Diagnostics;
using System.IO.Pipes;
using KosToolbox.Core.Peripherals;
using KosToolbox.Core.Peripherals.Hid;
using KosToolbox.Core.Text;
using Xunit;

namespace KosToolbox.Core.Tests;

public class PeripheralsTests
{
    const string HidClass = "{4d1e55b2-f16f-11cf-88cb-001111000030}";
    static readonly Guid Receiver = new("4e5e0c61-cce4-1255-be35-806e6f6e6963"), Dock = new("4e5e0c62-cce4-1255-be35-806e6f6e6963"),
        Keyboard = new("4e5e0c5e-cce4-1255-be35-806e6f6e6963");

    static HidCollection C(ushort vid, ushort pid, string hardware, string instance, ushort page, ushort usage, Guid? container, int feature = 0) =>
        new($@"\\?\hid#vid_{vid:x4}&pid_{pid:x4}&{hardware}#{instance}#{HidClass}", vid, pid, 0x0200, page, usage, 8, 0, feature, "Sample", container, false);

    // The shape of this PC's devices (read on 2026-11-17): MI_00, MI_01 with COL01-05 and MI_02, each interface under its own parent instance.
    static IEnumerable<HidCollection> Shape(ushort vid, ushort pid, Guid? container, string[] parents, (ushort Page, ushort Usage)[] usages) => new[]
    {
        C(vid, pid, "mi_00", $"{parents[0]}&0000", usages[0].Page, usages[0].Usage, container),
        C(vid, pid, "mi_01&col01", $"{parents[1]}&0000", usages[1].Page, usages[1].Usage, container),
        C(vid, pid, "mi_01&col02", $"{parents[1]}&0001", usages[2].Page, usages[2].Usage, container),
        C(vid, pid, "mi_01&col03", $"{parents[1]}&0002", usages[3].Page, usages[3].Usage, container),
        C(vid, pid, "mi_01&col04", $"{parents[1]}&0003", usages[4].Page, usages[4].Usage, container),
        C(vid, pid, "mi_01&col05", $"{parents[1]}&0004", usages[5].Page, usages[5].Usage, container, feature: 91),
        C(vid, pid, "mi_02", $"{parents[2]}&0000", usages[6].Page, usages[6].Usage, container),
    };

    static readonly (ushort, ushort)[] RazerUsages = { (1, 2), (1, 6), (0x0C, 1), (1, 0x80), (1, 0), (1, 0), (1, 6) };
    static readonly (ushort, ushort)[] KeyboardUsages = { (1, 6), (0x0C, 1), (1, 0x80), (1, 6), (1, 2), (0xFFFF, 1), (0xFFFF, 2) };
    static IEnumerable<HidCollection> ViperReceiver(Guid? container) =>
        Shape(0x1532, 0x007B, container, new[] { "8&19baef0c&0", "8&317e7b51&0", "8&da73d8f&0" }, RazerUsages);

    [Theory]
    [InlineData(@"\\?\hid#vid_1532&pid_007b&mi_01&col05#8&317e7b51&0&0004#" + HidClass, 1, 5, "8&317e7b51&0")]
    [InlineData(@"\\?\hid#vid_1532&pid_007b&mi_00#8&19baef0c&0&0000#" + HidClass, 0, -1, "8&19baef0c&0")]
    [InlineData(@"\\?\HID#VID_1532&PID_007E&MI_02#8&AE1179C&0&0000#" + HidClass, 2, -1, "8&ae1179c&0")]
    [InlineData(@"\\?\hid#vid_046d&pid_c077#7&2a1b3c4d&0&0000#" + HidClass, -1, -1, "7&2a1b3c4d&0")] // one interface, one collection
    [InlineData(@"\\?\hid#vid_1234&pid_5678&mi_0a&col1b#9&1&0&0000#" + HidClass, 10, 0x1B, "9&1&0")] // hex digits, shown back as MI_0A, COL1B
    // Bluetooth LE: no interface, the collection after the device address; only the hardware part is searched.
    [InlineData(@"\\?\hid#{00001812-0000-1000-8000-00805f9b34fb}&dev&vid_046d&pid_b023&rev_0007&d4f5470d83e4&col02#a&1b2c3d4e&0&0001#" + HidClass, -1, 2, "a&1b2c3d4e&0")]
    [InlineData(@"\\?\hid#vid_1532&pid_007b#col01&mi_01#" + HidClass, -1, -1, "col01")] // tags in the instance part don't count
    [InlineData("not a path", -1, -1, null)]
    public void PathGivesInterfaceCollectionAndParent(string path, int iface, int collection, string? parent)
    {
        Assert.Equal(iface, HidDevices.InterfaceOf(path));
        Assert.Equal(collection, HidDevices.CollectionOf(path));
        Assert.Equal(parent, HidDevices.ParentInstance(path));
    }

    [Fact]
    public void CollectionsGroupByContainerIntoThisPcsThreeDevices()
    {
        var all = ViperReceiver(Receiver)
            .Concat(Shape(0x1532, 0x007E, Dock, new[] { "8&13299e1&0", "8&22a4a3e1&0", "8&ae1179c&0" }, RazerUsages))
            .Concat(Shape(0x3151, 0x502D, Keyboard, new[] { "7&4e28660&0", "7&2475ddc9&0", "7&9ea007&0" }, KeyboardUsages))
            .Reverse(); // Windows' order isn't the device's
        var devices = HidDevices.Group(all);

        Assert.Equal(3, devices.Count);
        var viper = devices.Single(d => d.ProductId == 0x007B);
        Assert.Equal(new[] { "MI_00 —", "MI_01 COL01", "MI_01 COL02", "MI_01 COL03", "MI_01 COL04", "MI_01 COL05", "MI_02 —" },
            viper.Collections.Select(c => $"{Txt.HidInterface(c.Interface)} {Txt.HidCollectionNumber(c.Collection)}"));
        Assert.Equal(new[] { "MI_00", "MI_01 COL01", "MI_02" }, viper.Collections.Where(c => c.Collection <= 1).Select(c => Txt.HidLocation(c.Interface, c.Collection)));
        Assert.Equal(("COL02", "—"), (Txt.HidLocation(-1, 2), Txt.HidLocation(-1, -1)));
        // Every one carries a keyboard and a mouse collection: the first interface decides.
        Assert.Equal(PeripheralKind.Mouse, viper.Kind);
        Assert.Equal(PeripheralKind.Mouse, devices.Single(d => d.ProductId == 0x007E).Kind);
        Assert.Equal(PeripheralKind.Keyboard, devices.Single(d => d.VendorId == 0x3151).Kind);
    }

    [Fact]
    public void WithoutAUsableContainerEachInterfaceIsADevice()
    {
        // No container id, or the one shared by everything built into the PC: the parent instance groups one interface's collections.
        foreach (Guid? container in new Guid?[] { null, Guid.Empty, new Guid("00000000-0000-0000-ffff-ffffffffffff") })
        {
            var devices = HidDevices.Group(ViperReceiver(container));
            Assert.Equal(new[] { 1, 5, 1 }, devices.OrderBy(d => d.Collections[0].Interface).Select(d => d.Collections.Count));
        }
        // Two products in one container (a keyboard with a hub) stay two devices.
        var two = HidDevices.Group(new[] { C(0x1532, 0x007B, "mi_00", "8&1&0&0000", 1, 2, Receiver), C(0x1532, 0x007A, "mi_00", "8&2&0&0000", 1, 2, Receiver) });
        Assert.Equal(2, two.Count);
    }

    [Fact]
    public void KindComesFromTheFirstKnownUsage()
    {
        Assert.Equal(PeripheralKind.Controller, HidDevices.GuessKind(new[] { C(0x045E, 0x0B12, "mi_00", "8&1&0&0000", 1, 5, null) }));
        Assert.Equal(PeripheralKind.Headset, HidDevices.GuessKind(new[]
            { C(0x1532, 0x0527, "mi_03&col01", "8&1&0&0000", 0x0C, 1, null), C(0x1532, 0x0527, "mi_03&col02", "8&1&0&0001", 0x0B, 5, null) }));
        Assert.Equal(PeripheralKind.Other, HidDevices.GuessKind(new[] { C(0x08BB, 0x2902, "mi_03", "8&1&0&0000", 0x0C, 1, null) }));
    }

    [Fact]
    public void FidoCollectionsAreRecognisedFromTheirIds()
    {
        Assert.True(HidDevices.IsFido(new[] { @"HID\VID_1050&PID_0407&MI_01", @"HID\VID_1050&UP:F1D0_U:0001", "HID_DEVICE_UP:F1D0_U:0001", "HID_DEVICE" }));
        Assert.False(HidDevices.IsFido(new[] { @"HID\VID_1532&PID_007B&MI_01&Col05", @"HID\VID_1532&UP:0001_U:0000", "HID_DEVICE_UP:0001_U:0000", "HID_DEVICE" }));
    }

    [Fact]
    public void BufferStartsWithTheIdIsPaddedAndRefusesWhatDoesntFit()
    {
        Assert.Equal(new byte[] { 0, 1, 2, 3, 0, 0, 0, 0 }, HidBuffer.Build(0, new byte[] { 1, 2, 3 }, 8));
        Assert.Equal(new byte[] { 0x08, 0, 0, 0 }, HidBuffer.Build(0x08, default, 4));
        var razer = HidBuffer.Build(0, new byte[90], 91); // a 90-byte report fills a 91-byte collection exactly
        Assert.Equal(91, razer.Length);
        Assert.Throws<ArgumentException>(() => HidBuffer.Build(0, new byte[91], 91));
        Assert.Throws<ArgumentException>(() => HidBuffer.Build(0, default, 0)); // the collection has no report of that kind
    }

    [Fact]
    public void ReportListsTheCollectionsButNoPathOrInstance()
    {
        var path = @"\\?\hid#{00001812-0000-1000-8000-00805f9b34fb}&dev&vid_046d&pid_b023&rev_0007&d4f5470d83e4&col01#a&1b2c3d4e&0&0000#" + HidClass;
        var bluetooth = new HidCollection(path, 0x046D, 0xB023, 0x0007, 1, 2, 8, 0, 0, "Sample BT mouse", Receiver, true);
        foreach (var device in HidDevices.Group(ViperReceiver(Receiver).Append(bluetooth)))
        {
            var report = Txt.PeripheralReport(device);
            Assert.Contains(Txt.HidIds(device.VendorId, device.ProductId), report);
            Assert.Equal(device.Collections.Count, report.Split('\n').Count(l => l.StartsWith("  ")));
            foreach (var secret in new[] { "hid#", @"\\?\", "317e7b51", "19baef0c", "d4f5470d83e4", "1b2c3d4e", Receiver.ToString(), "4d1e55b2" })
                Assert.DoesNotContain(secret, report, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("MI_01  COL05  0001:0000", Txt.PeripheralReport(HidDevices.Group(ViperReceiver(Receiver))[0]));
    }

    // The overlapped path of HidHandle (timeout, CancelIoEx, GetOverlappedResult) on a named pipe: no device involved.
    [Fact]
    public async Task OverlappedTransferTimesOutThenMovesData()
    {
        var name = $"kos-hid-test-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 64, 64);
        var connected = server.WaitForConnectionAsync();
        using var client = HidNative.Open($@"\\.\pipe\{name}", HidNative.GenericRead | HidNative.GenericWrite, HidNative.FlagOverlapped);
        await connected.WaitAsync(TimeSpan.FromSeconds(5));
        var limit = TimeSpan.FromSeconds(5); // a broken pipe side fails the test instead of hanging it

        var buffer = new byte[4];
        var clock = Stopwatch.StartNew();
        Assert.Equal(-1, HidHandle.Transfer(client, buffer, write: false, timeoutMs: 100)); // nothing to read: cancelled after the timeout
        Assert.InRange(clock.ElapsedMilliseconds, 80, 5000);

        var sent = server.WriteAsync(new byte[] { 1, 2, 3, 4 }).AsTask();
        Assert.Equal(4, HidHandle.Transfer(client, buffer, write: false, timeoutMs: 2000)); // the cancelled read took nothing
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, buffer);
        await sent.WaitAsync(limit);

        var got = new byte[2];
        var received = server.ReadAsync(got).AsTask();
        Assert.Equal(2, HidHandle.Transfer(client, new byte[] { 9, 8 }, write: true, timeoutMs: 2000));
        Assert.Equal(2, await received.WaitAsync(limit));
        Assert.Equal(new byte[] { 9, 8 }, got);
    }
}
