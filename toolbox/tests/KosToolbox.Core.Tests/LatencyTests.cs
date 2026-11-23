using System.Runtime.InteropServices;
using System.Text;
using KosToolbox.Core.Latency;
using Xunit;

namespace KosToolbox.Core.Tests;

public class LatencyTests
{
    const long Frequency = 10_000_000; // 1 tick = 0.1 µs

    static readonly KernelModules Modules = new(new (ulong, uint, string)[]
    {
        (0xFFFFF800_00100000, 0x1000, "nvlddmkm.sys"), (0xFFFFF800_00000000, 0x1000, "ntoskrnl.exe"), (0, 0x1000, "hidden.sys"),
    });

    static byte[] Routine(long start, ulong routine) => BitConverter.GetBytes(start).Concat(BitConverter.GetBytes(routine)).ToArray();

    [Fact]
    public void ModulesFoundByAddress()
    {
        Assert.Equal(2, Modules.Count); // a module without an address is left out
        Assert.Equal("ntoskrnl.exe", Modules.Find(0xFFFFF800_00000FFF));
        Assert.Equal("nvlddmkm.sys", Modules.Find(0xFFFFF800_00100010));
        Assert.Equal(KernelModules.Unknown, Modules.Find(0xFFFFF800_00001000));
        Assert.Equal(KernelModules.Unknown, Modules.Find(1));
    }

    [Fact]
    public void ModuleListParsed()
    {
        var data = new byte[8 + 2 * 296];
        BitConverter.GetBytes(2u).CopyTo(data, 0);
        void Entry(int i, ulong image, uint size, string path, ushort nameAt)
        {
            var at = 8 + i * 296;
            BitConverter.GetBytes(image).CopyTo(data, at + 16);
            BitConverter.GetBytes(size).CopyTo(data, at + 24);
            BitConverter.GetBytes(nameAt).CopyTo(data, at + 38);
            Encoding.ASCII.GetBytes(path).CopyTo(data, at + 40);
        }
        Entry(0, 0xFFFFF800_00000000, 0x9000, @"\SystemRoot\system32\ntoskrnl.exe", 21);
        Entry(1, 0xFFFFF800_10000000, 0x200, @"\SystemRoot\System32\drivers\USBXHCI.SYS", 29);
        var list = KernelModules.Parse(data);
        Assert.Equal(new (ulong, uint, string)[] { (0xFFFFF800_00000000, 0x9000, "ntoskrnl.exe"), (0xFFFFF800_10000000, 0x200, "USBXHCI.SYS") }, list);
    }

    [Fact]
    public void DpcIsrFaultsAndTimerAddedUp()
    {
        var c = new LatencyCollector(Modules, Frequency);
        int Pid(uint tid) => tid == 77 ? 1234 : 99;
        c.Add(LatencyCollector.PerfInfo, LatencyCollector.Dpc, 0, 1000, Routine(500, 0xFFFFF800_00100020), Pid);       // 50 µs
        c.Add(LatencyCollector.PerfInfo, LatencyCollector.TimerDpc, 1, 30000, Routine(10000, 0xFFFFF800_00100020), Pid); // 2,000 µs
        c.Add(LatencyCollector.PerfInfo, LatencyCollector.Isr, 1, 2000, Routine(1900, 0xFFFFF800_00000010), Pid);       // 10 µs
        c.Add(LatencyCollector.PerfInfo, LatencyCollector.Dpc, 0, 100, Routine(200, 0xFFFFF800_00000010), Pid);         // ends before it starts: ignored
        c.Add(Guid.NewGuid(), LatencyCollector.Dpc, 0, 1000, Routine(500, 0xFFFFF800_00000010), Pid);                    // other provider: ignored
        var fault = new byte[40];
        BitConverter.GetBytes(77u).CopyTo(fault, 32);
        c.Add(LatencyCollector.PageFault, LatencyCollector.HardFault, 0, 0, fault, Pid);
        c.Add(LatencyCollector.PageFault, LatencyCollector.HardFault, 0, 0, fault, Pid);
        BitConverter.GetBytes(5u).CopyTo(fault, 32);
        c.Add(LatencyCollector.PageFault, LatencyCollector.HardFault, 0, 0, fault, Pid);
        c.Timer(1000); // 100 µs
        c.Timer(3000); // 300 µs

        var r = c.Report(TimeSpan.FromSeconds(5), pid => pid == 1234 ? "game" : "other");
        Assert.Equal(new[] { "nvlddmkm.sys", "ntoskrnl.exe" }, r.Drivers.Select(d => d.Name));
        var nv = r.Drivers[0];
        Assert.Equal((2L, 2000.0, 2.05), (nv.Dpc.Count, nv.Dpc.MaxUs, nv.Dpc.TotalMs));
        Assert.Equal(0, nv.Isr.Count);
        Assert.Equal((1L, 10.0), (r.Drivers[1].Isr.Count, r.Drivers[1].Isr.MaxUs));
        // Which processors each driver's routines ran on: nvlddmkm one DPC on CPU 0 and one on CPU 1, ntoskrnl its ISR on CPU 1.
        Assert.Equal(new[] { new CpuShare(0, 1), new CpuShare(1, 1) }, nv.OnCpus);
        Assert.Equal(new[] { new CpuShare(1, 1) }, r.Drivers[1].OnCpus);
        Assert.Null(r.Cpus[0].OnCpus);
        Assert.Equal(new[] { "CPU 0", "CPU 1" }, r.Cpus.Select(x => x.Name));
        Assert.Equal((1L, 1L, 2000.0), (r.Cpus[1].Dpc.Count, r.Cpus[1].Isr.Count, r.Cpus[1].MaxUs));
        Assert.Equal(3, r.HardFaults);
        Assert.Equal(new[] { new FaultRow("game", 2), new FaultRow("other", 1) }, r.Faults);
        Assert.Equal((2L, 300.0, 200.0), (r.TimerSamples, r.TimerMaxUs, r.TimerAverageUs));
    }

    [Fact]
    public void TraceStructsMatchWindowsLayout()
    {
        Assert.Equal(120, Marshal.SizeOf<LatencyTrace.TraceProperties>());
        Assert.Equal(448, Marshal.SizeOf<LatencyTrace.EventTraceLogfile>());
        Assert.Equal(48, (int)Marshal.OffsetOf<LatencyTrace.TraceProperties>(nameof(LatencyTrace.TraceProperties.BufferSizeKb)));
        Assert.Equal(104, (int)Marshal.OffsetOf<LatencyTrace.TraceProperties>(nameof(LatencyTrace.TraceProperties.LoggerThreadId)));
    }
}
