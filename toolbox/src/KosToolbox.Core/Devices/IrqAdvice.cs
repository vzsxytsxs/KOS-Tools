using KosToolbox.Core.SystemInfo;
using KosToolbox.Core.Text;

namespace KosToolbox.Core.Devices;

/// <summary>Read-only findings about the interrupts in effect; nothing here changes the system.</summary>
public static class IrqAdvice
{
    static readonly (IrqRole A, IrqRole B)[] Contending = { (IrqRole.Gpu, IrqRole.InputUsb), (IrqRole.Audio, IrqRole.Gpu), (IrqRole.Network, IrqRole.InputUsb) };

    /// <param name="core0Mask">Logical processors of the core that holds CPU 0.</param>
    public static List<string> Find(IReadOnlyList<IrqDevice> devices, ulong core0Mask, ulong allMask)
    {
        var found = new List<string>();
        foreach (var d in devices.Where(d => (d.Support & 6) is > 0 && d.Effective is { Count: > 0, MessageSignaled: false }))
            found.Add(Txt.AdviceLineBased(d.Name));

        foreach (var g in devices.Where(d => d.Effective is { Count: > 0, MessageSignaled: false })
                     .SelectMany(d => d.Effective!.Lines.Select(irq => (irq, d))).GroupBy(x => x.irq).Where(g => g.Count() > 1))
            found.Add(Txt.AdviceSharedLine(g.Key, g.Select(x => x.d.Name)));

        foreach (var (ra, rb) in Contending)
            foreach (var a in devices.Where(d => d.Role == ra && d.Pinned))
                foreach (var b in devices.Where(d => d.Role == rb && d.Pinned))
                    if ((a.Cpus(allMask) & b.Cpus(allMask)) is { } both and not 0)
                        found.Add(Txt.AdviceOverlap(a.Name, b.Name, Cpus.Format(both)));

        foreach (var d in devices.Where(d => d.Role == IrqRole.InputUsb && d.Pinned && (d.Cpus(allMask) & core0Mask) is not (null or 0)))
            found.Add(Txt.AdviceInputOnCore0(d.Name));
        return found;
    }
}
