using KosToolbox.Core.Devices;
using KosToolbox.Core.Execution;
using Xunit;

namespace KosToolbox.Core.Tests;

public class IrqLayoutTests
{
    // ---------- read-back parsing ----------
    static byte[] IrqDes(uint allocNum, ulong affinity)
    {
        var b = new byte[36];
        BitConverter.GetBytes(0x0Cu).CopyTo(b, 4);
        BitConverter.GetBytes(allocNum).CopyTo(b, 12);
        BitConverter.GetBytes(affinity).CopyTo(b, 16);
        return b;
    }

    [Fact]
    public void MsiMessagesAreCountedAndAffinitiesJoined()
    {
        var s = IrqResources.Parse(new[] { IrqDes(0xFFFFFFFD, 1), IrqDes(0xFFFFFFFC, 2), IrqDes(0xFFFFFFFB, 4) });
        Assert.Equal(3, s.Count);
        Assert.True(s.MessageSignaled);
        Assert.Empty(s.Lines);
        Assert.Equal(7UL, s.Affinity);
    }

    [Fact]
    public void LineInterruptKeepsItsIrqNumber()
    {
        var s = IrqResources.Parse(new[] { IrqDes(16, 0xFFFFFFFF), new byte[10] }); // a short descriptor is skipped
        Assert.Equal(1, s.Count);
        Assert.False(s.MessageSignaled);
        Assert.Equal(new uint[] { 16 }, s.Lines);
        Assert.Equal(0xFFFFFFFFUL, s.Affinity);
        Assert.Equal(0, IrqResources.Parse(Array.Empty<byte[]>()).Count);
    }

    [Fact]
    public void TranslatedListGivesTheProcessorsInUse()
    {
        // A GPU's translated resources (3 memory ranges, an I/O port, device-private entries, then MSI on CPU 5), as the registry has them.
        var gpu = Convert.FromHexString("010000000500000001000000010001000900000003018000000000DE000000000000000100000000810100000100000000000000000000000000000003018400000000E02F0000000000001000000000810100000100000001000000000000000000000003018400000000F02F000000000000020000000081010000010000000300000000000000000000000101310100E000000000000080000000000000008101000001000000050000000000000000000000020103000B000000B10000002000000000000000");
        Assert.Equal(0x20UL, IrqResources.TranslatedAffinity(gpu));
        var noIrq = Convert.FromHexString("010000000500000001000000010001000100000003018000000000DE0000000000000001");
        Assert.Null(IrqResources.TranslatedAffinity(noIrq));
        Assert.Null(IrqResources.TranslatedAffinity(gpu[..60])); // truncated
        Assert.Null(IrqResources.TranslatedAffinity(Array.Empty<byte>()));
    }

    [Fact]
    public void PendingWhenTheRegistryAsksForSomethingElse()
    {
        const ulong all = 0x3F;
        var msi = new IrqState(1, true, Array.Empty<uint>(), 0x20);
        var line = new IrqState(1, false, new uint[] { 16 }, 0x3F);
        Assert.False(IrqResources.Pending(new InterruptConfig { Msi = 1, Policy = 4, Mask = 0x30 }, msi, 3, all)); // CPU 5 is inside 4-5
        Assert.True(IrqResources.Pending(new InterruptConfig { Policy = 4, Mask = 0x02 }, msi, 3, all));
        Assert.True(IrqResources.Pending(new InterruptConfig { Msi = 1 }, line, 3, all));
        Assert.False(IrqResources.Pending(new InterruptConfig { Msi = 1 }, line, 1, all));                   // no MSI support: not compared
        Assert.True(IrqResources.Pending(new InterruptConfig { Msi = 0 }, msi, 3, all));
        Assert.False(IrqResources.Pending(new InterruptConfig { Msi = 1, Policy = 4, Mask = 2 }, null, 3, all)); // no interrupts: blank
        Assert.False(IrqResources.Pending(new InterruptConfig(), line, 3, all));
        Assert.False(IrqResources.Pending(new InterruptConfig { Policy = 4, Mask = 2 }, msi with { Affinity = 0 }, 3, all)); // processors not known
        Assert.Equal("MSI ×1", Text.Txt.IrqInEffect(msi with { Affinity = 0 }, false, all));
        Assert.Equal("Line IRQ 16 · all CPUs · pending restart", Text.Txt.IrqInEffect(line, true, all));
        Assert.Equal("MSI ×1 · CPU 5", Text.Txt.IrqInEffect(msi, false, all));
    }

    // ---------- topologies ----------
    /// <summary>Cores of <paramref name="smt"/> logical processors each; caches and classes per core.</summary>
    static List<CpuCore> Topo(int cores, int smt = 1, Func<int, int>? cache = null, Func<int, int>? eff = null, Func<int, long>? l3 = null, Func<int, int>? sched = null) =>
        Enumerable.Range(0, cores).Select(i => new CpuCore(i * smt, ((1UL << smt) - 1) << (i * smt), eff?.Invoke(i) ?? 0, sched?.Invoke(i) ?? 0,
            cache?.Invoke(i) ?? 0, l3?.Invoke(i) ?? 32 << 20)).ToList();

    static IrqDevice Dev(string name, IrqRole role, int messages = 1, InterruptConfig? config = null) =>
        new(name, name, role, 3, config ?? new InterruptConfig { InstanceId = name }, new IrqState(messages, true, Array.Empty<uint>(), 1));

    static readonly IrqDevice[] TypicalPc =
    {
        Dev("gpu", IrqRole.Gpu), Dev("hda1", IrqRole.Audio), Dev("xhci", IrqRole.InputUsb), Dev("nic", IrqRole.Network),
        Dev("hda2", IrqRole.Audio), Dev("gpu-usb", IrqRole.Usb), Dev("nvme", IrqRole.Other, 7),
    };

    static Dictionary<string, ulong> Layout(IEnumerable<IrqDevice> devices, List<CpuCore> topo) =>
        IrqLayout.Suggest(devices.ToList(), topo).ToDictionary(p => p.Device.InstanceId, p => p.Mask);

    [Fact]
    public void SixCoresWithoutSmt()
    {
        var l = Layout(TypicalPc, Topo(6));
        Assert.Equal(0b10UL, l["xhci"]);
        Assert.Equal(0b100UL, l["nic"]);
        Assert.Equal(0b1000UL, l["hda1"]);  // audio and the other USB controllers share one core
        Assert.Equal(0b1000UL, l["hda2"]);
        Assert.Equal(0b1000UL, l["gpu-usb"]);
        Assert.Equal(0b110000UL, l["gpu"]); // min(max(2, 1), 5 / 2) = 2 cores
        Assert.False(l.ContainsKey("nvme"));
    }

    [Fact]
    public void SmtSiblingsAreNeverUsed()
    {
        var l = Layout(TypicalPc, Topo(8, smt: 2));
        Assert.Equal(1UL << 2, l["xhci"]);
        Assert.Equal(1UL << 4, l["nic"]);
        Assert.Equal(1UL << 6, l["hda1"]);
        Assert.Equal(1UL << 8 | 1UL << 10, l["gpu"]);
        Assert.All(l.Values, m => Assert.Equal(0UL, m & 0xAAAAUL)); // no odd (second) logical processor
        Assert.All(l.Values, m => Assert.Equal(0UL, m & 3));        // nothing on CPU 0's core
    }

    [Fact]
    public void GpuGetsMoreCoresForMoreMessagesUpToHalfThePool()
    {
        var l = Layout(new[] { Dev("gpu", IrqRole.Gpu, messages: 8) }, Topo(8, smt: 2)); // pool of 7: at most 3
        Assert.Equal(1UL << 2 | 1UL << 4 | 1UL << 6, l["gpu"]);
    }

    [Fact]
    public void VCacheCcdIsLeftToGames()
    {
        // CCD 0 (cores 0-3) has the 3D V-Cache: interrupts go to CCD 1.
        var topo = Topo(8, smt: 2, cache: i => i / 4, l3: i => i < 4 ? 96 << 20 : 32 << 20);
        Assert.Equal(0, IrqLayout.GameCache(topo));
        Assert.Equal(new[] { 8, 10, 12, 14 }, IrqLayout.Pool(topo).Select(c => c.FirstCpu));
        var l = Layout(TypicalPc, topo);
        Assert.Equal(1UL << 8, l["xhci"]);
        Assert.Equal(1UL << 10, l["nic"]);
        Assert.Equal(1UL << 12, l["hda1"]);
        Assert.Equal(1UL << 14 | 1UL << 8, l["gpu"]); // second pick: every core used once, pool order

        // V-Cache on CCD 1: the other CCD without CPU 0's core.
        var second = Topo(8, smt: 2, cache: i => i / 4, l3: i => i >= 4 ? 96 << 20 : 32 << 20);
        Assert.Equal(1, IrqLayout.GameCache(second));
        Assert.Equal(new[] { 2, 4, 6 }, IrqLayout.Pool(second).Select(c => c.FirstCpu));
    }

    [Fact]
    public void EqualCcdsAreDecidedBySchedulingClass()
    {
        var topo = Topo(8, smt: 2, cache: i => i / 4, sched: i => i == 5 ? 9 : 1);
        Assert.Equal(1, IrqLayout.GameCache(topo));
        Assert.Equal(new[] { 2, 4, 6 }, IrqLayout.Pool(topo).Select(c => c.FirstCpu));
        Assert.Null(IrqLayout.GameCache(Topo(8, smt: 2)));
    }

    [Fact]
    public void HybridUsesPCoresOnly()
    {
        // 6 P-cores with HT (0-11), 8 E-cores (12-19), and LP E-cores on their own cache: still a single game domain.
        var topo = Enumerable.Range(0, 6).Select(i => new CpuCore(i * 2, 3UL << (i * 2), 1, 0, 0, 30 << 20))
            .Concat(Enumerable.Range(0, 8).Select(i => new CpuCore(12 + i, 1UL << (12 + i), 0, 0, i < 6 ? 0 : 1, 30 << 20))).ToList();
        Assert.Null(IrqLayout.GameCache(topo));
        Assert.Equal(new[] { 2, 4, 6, 8, 10 }, IrqLayout.Pool(topo).Select(c => c.FirstCpu));
        Assert.All(Layout(TypicalPc, topo).Values, m => Assert.Equal(0UL, m & ~0x554UL));
    }

    [Fact]
    public void PinnedInsidePoolIsKeptAndCounted()
    {
        var nic = Dev("nic", IrqRole.Network, config: new InterruptConfig { InstanceId = "nic", Policy = 4, Mask = 0b1000 });
        var devices = TypicalPc.Where(d => d.InstanceId != "nic").Append(nic).ToList();
        var picks = IrqLayout.Suggest(devices, Topo(6));
        Assert.True(picks.Single(p => p.Device.InstanceId == "nic").Kept);
        var l = picks.ToDictionary(p => p.Device.InstanceId, p => p.Mask);
        Assert.Equal(0b1000UL, l["nic"]);
        Assert.Equal(0b10UL, l["xhci"]);
        Assert.Equal(0b100UL, l["hda1"]);
        Assert.Equal(0b110000UL, l["gpu"]);

        // Pinned to CPU 0: outside the pool, so it gets a new core.
        var onZero = Dev("nic", IrqRole.Network, config: new InterruptConfig { InstanceId = "nic", Policy = 4, Mask = 1 });
        var moved = IrqLayout.Suggest(TypicalPc.Where(d => d.InstanceId != "nic").Append(onZero).ToList(), Topo(6));
        Assert.False(moved.Single(p => p.Device.InstanceId == "nic").Kept);
        Assert.Equal(0b100UL, moved.Single(p => p.Device.InstanceId == "nic").Mask);
    }

    [Fact]
    public void TinyPools()
    {
        var two = Layout(TypicalPc, Topo(2));
        Assert.All(two.Values, m => Assert.Equal(0b10UL, m)); // everything on the one core left
        Assert.Empty(IrqLayout.Suggest(TypicalPc, Topo(1)));
        Assert.Empty(IrqLayout.Suggest(TypicalPc, Topo(1, smt: 2)));
    }

    [Fact]
    public void RolesFromClassAndPciClassCode()
    {
        PnpDevice D(string cls, uint? bc = null) => new(@"PCI\X", "x", cls, true) { PciBaseClass = bc };
        Assert.Equal(IrqRole.Gpu, IrqLayout.RoleOf(D("Display", 3), false));
        Assert.Equal(IrqRole.InputUsb, IrqLayout.RoleOf(D("USB", 0x0C), true));
        Assert.Equal(IrqRole.Usb, IrqLayout.RoleOf(D("USB", 0x0C), false));
        Assert.Equal(IrqRole.Network, IrqLayout.RoleOf(D("Net", 2), false));
        Assert.Equal(IrqRole.Audio, IrqLayout.RoleOf(D("System", 4), false)); // HD Audio controller
        Assert.Equal(IrqRole.Audio, IrqLayout.RoleOf(D("MEDIA"), false));
        Assert.Equal(IrqRole.Other, IrqLayout.RoleOf(D("SCSIAdapter", 1), false));
    }

    // ---------- advice ----------
    [Fact]
    public void AdviceFindings()
    {
        const ulong all = 0xFF;
        IrqDevice Line(string name, IrqRole role, uint irq) => new(name, name, role, 3, new InterruptConfig { InstanceId = name }, new IrqState(1, false, new[] { irq }, all));
        IrqDevice Pinned(string name, IrqRole role, ulong mask, ulong inUse) =>
            new(name, name, role, 3, new InterruptConfig { InstanceId = name, Policy = 4, Mask = mask }, new IrqState(1, true, Array.Empty<uint>(), inUse));

        var found = IrqAdvice.Find(new[]
        {
            Line("HDA", IrqRole.Audio, 16), Line("SMBus", IrqRole.Other, 16) with { Support = 1 },
            Pinned("GPU", IrqRole.Gpu, 0b1100, 0b100), Pinned("xHCI", IrqRole.InputUsb, 0b110, 0b10), Pinned("NIC", IrqRole.Network, 0b10, 0b10),
        }, core0Mask: 0b1, all);

        Assert.Contains(found, f => f.StartsWith("HDA: runs on a line-based"));
        Assert.DoesNotContain(found, f => f.StartsWith("SMBus: runs on")); // no MSI support
        Assert.Contains(found, f => f.Contains("HDA and SMBus share line IRQ 16"));
        Assert.Contains(found, f => f.StartsWith("NIC and xHCI take interrupts on the same CPU(s) 1"));
        Assert.DoesNotContain(found, f => f.StartsWith("GPU and xHCI")); // in effect: CPU 2 against CPU 1
        Assert.DoesNotContain(found, f => f.Contains("CPU 0's core"));

        var onCore0 = IrqAdvice.Find(new[] { Pinned("xHCI", IrqRole.InputUsb, 0b1, 0b1) }, 0b1, all);
        Assert.Contains(onCore0, f => f.Contains("CPU 0's core"));
        Assert.Empty(IrqAdvice.Find(new[] { Pinned("xHCI", IrqRole.InputUsb, 0b10, 0b10) }, 0b1, all));
    }

    // ---------- elevated batch ----------
    [Fact]
    public void BatchIsValidatedLikeSingleChanges()
    {
        Assert.ThrowsAny<Exception>(() => new ElevatedJob { InterruptBatch = new InterruptBatch() }.Validate()); // empty
        Assert.ThrowsAny<Exception>(() => new ElevatedJob
        {
            InterruptBatch = new InterruptBatch { Configs = { new InterruptConfig { InstanceId = @"PCI\VEN_FFFF&DEV_FFFF\0", Msi = 1 } } },
        }.Validate()); // not connected
        Assert.ThrowsAny<Exception>(() => new ElevatedJob { InterruptBatch = new InterruptBatch { Restart = { @"PCI\VEN_FFFF&DEV_FFFF\0" } } }.Validate());
        Assert.ThrowsAny<Exception>(() => new ElevatedJob
        {
            InterruptBatch = new InterruptBatch { Restart = { @"PCI\A\1" } }, RemoveDevices = { @"PCI\A\1" },
        }.Validate()); // two kinds
    }

    [Fact]
    public void OnlyUsbAndNetworkAreRestarted()
    {
        var pci = new List<PnpDevice>
        {
            new(@"PCI\VEN_1&DEV_1\1", "xHCI", "USB", true), new(@"PCI\VEN_1&DEV_2\1", "NIC", "Net", true),
            new(@"PCI\VEN_1&DEV_3\1", "GPU", "Display", true), new(@"PCI\VEN_1&DEV_4\1", "HDA", "System", true), new(@"PCI\VEN_1&DEV_5\1", "NVMe", "SCSIAdapter", true),
        };
        DeviceRestart.Validate(@"PCI\VEN_1&DEV_1\1", pci);
        DeviceRestart.Validate(@"pci\ven_1&dev_2\1", pci);
        Assert.ThrowsAny<Exception>(() => DeviceRestart.Validate(@"PCI\VEN_1&DEV_3\1", pci));
        Assert.ThrowsAny<Exception>(() => DeviceRestart.Validate(@"PCI\VEN_1&DEV_4\1", pci));
        Assert.ThrowsAny<Exception>(() => DeviceRestart.Validate(@"PCI\VEN_1&DEV_5\1", pci));
        Assert.ThrowsAny<Exception>(() => DeviceRestart.Validate(@"USB\VID_1&PID_1\1", pci));
        Assert.ThrowsAny<Exception>(() => DeviceRestart.Validate(@"PCI\VEN_1&DEV_9\1", pci)); // not connected
    }
}
