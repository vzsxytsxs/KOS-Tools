using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using KosToolbox.Core.Execution;
using KosToolbox.Core.SystemInfo;

namespace KosToolbox.Core.Tools;

/// <summary>A module's SPD as read by CPU-Z: properties, JEDEC/XMP/EXPO tables and the register dump.</summary>
public sealed record CpuzDimm(string Title, IReadOnlyList<InfoRow> Rows, string? Registers)
{
    /// <summary>SPD bytes, from the register dump (empty without a dump).</summary>
    public byte[] Spd => Registers is null ? Array.Empty<byte>()
        : Registers.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).SelectMany(l => l.Split(':').Skip(1).FirstOrDefault()?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>())
            .Select(h => Convert.ToByte(h, 16)).ToArray();
}

/// <summary>Current memory controller timings and each module's SPD, from the CPU-Z report.</summary>
public sealed record CpuzMemory(IReadOnlyList<InfoRow> Timings, IReadOnlyList<CpuzDimm> Dimms);

/// <summary>
/// Timings and SPD need access to the SMBus and the memory controller registers, which Windows only gives to a kernel
/// driver. The toolbox installs no driver: it uses the CPU-Z from the KOS tools folder (which loads its own signed driver) in
/// report mode (-txt, no window) and reads the "Chipset" and "Memory SPD" sections.
/// </summary>
public static class Cpuz
{
    public static string? Exe()
    {
        var root = ToolDiscovery.Root;
        try
        {
            if (!Directory.Exists(root)) return null;
            var direct = Path.Combine(root, "cpuz_x64.exe");
            if (File.Exists(direct)) return direct;
            // The folder carries the version (tools\CPUZ_3.01): only one level down.
            return Directory.EnumerateDirectories(root).Select(d => Path.Combine(d, "cpuz_x64.exe")).Where(File.Exists)
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static async Task<CpuzMemory> ReadMemoryAsync(CancellationToken ct)
    {
        var exe = Exe() ?? throw new FileNotFoundException($"CPU-Z (cpuz_x64.exe) wasn't found in {ToolDiscovery.Root}.");
        // When elevated, the toolbox would run CPU-Z as administrator: only if the file is owned by SYSTEM/Administrators.
        if (Elevation.IsAdmin && ToolDiscovery.OwnerProblem(exe) is { } problem) throw new InvalidOperationException(problem);
        // CPU-Z doesn't accept a quoted path and writes a folderless name next to its own exe: the report goes there
        // and is deleted after being read.
        const string name = "kos-memory-report";
        var file = Path.Combine(Path.GetDirectoryName(exe)!, name + ".txt");
        try
        {
            File.Delete(file);
            var psi = new ProcessStartInfo(exe, "-txt=" + name)
            {
                UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(exe)!,
            };
            if (!Elevation.IsAdmin) psi.Verb = "runas"; // CPU-Z needs administrator for its driver
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("CPU-Z didn't start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { p.Kill(); } catch { }
                throw new TimeoutException("CPU-Z didn't finish its report in 2 minutes.");
            }
            if (!File.Exists(file)) throw new IOException("CPU-Z didn't write its report.");
            return Parse(await File.ReadAllLinesAsync(file, ct));
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    internal static CpuzMemory Parse(IReadOnlyList<string> lines)
    {
        // Chipset: from "Memory Type" onward are the memory and the current timings (before that come north/south bridge and PCIe).
        var timings = new List<InfoRow>();
        double? mhz = null;
        var memory = false;
        foreach (var line in Section(lines, "Chipset"))
        {
            if (Row(line) is not { } r) continue;
            memory |= r.Key == "Memory Type";
            if (!memory || r.Key == "Host Bridge") continue;
            timings.Add(new InfoRow(r.Key, r.Value));
            if (r.Key == "Memory Frequency" && Number(r.Value) is { } f)
            {
                mhz = f;
                timings.Add(new InfoRow("Data rate", $"{f * 2:0} MT/s"));
            }
            if (r.Key.StartsWith("CAS# latency") && Number(r.Value) is { } cl && mhz is > 0)
                timings.Add(new InfoRow("CAS latency in ns", $"{cl / mhz.Value * 1000:0.00} ns"));
        }

        // Memory SPD: first a "DIMM #" properties block per module; then a "DIMM #" + "SPD registers" block per module.
        var dimms = new List<(string Number, List<InfoRow> Rows)>();
        var dumps = new List<string>();
        List<InfoRow>? rows = null;
        StringBuilder? registers = null;
        void EndDump() { if (registers is not null) dumps.Add(registers.ToString().TrimEnd()); registers = null; }
        var spd = Section(lines, "Memory SPD").ToList();
        for (var i = 0; i < spd.Count; i++)
        {
            if (Row(spd[i]) is not { } r) continue;
            if (!r.Indented && r.Key == "DIMM #")
            {
                EndDump();
                rows = null;
                if (i + 1 < spd.Count && Row(spd[i + 1]) is { Key: "SPD registers" }) { registers = new StringBuilder(); i++; }
                else dimms.Add((r.Value, rows = new List<InfoRow>()));
                continue;
            }
            if (registers is not null)
            {
                // "\t00\t23 11 0C ..." (the "00 01 02 ... 0F" header comes in a single column and is left out)
                var parts = spd[i].Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && parts[0].Length <= 3) registers.AppendLine($"{parts[0].PadLeft(3, '0')}: {parts[1]}");
                continue;
            }
            rows?.Add(new InfoRow(r.Key, r.Value.Length > 0 ? r.Value : "—"));
        }
        EndDump();

        return new CpuzMemory(timings, dimms.Select((d, i) => new CpuzDimm(
            string.Join(" · ", new[] { $"DIMM {d.Number}", Value(d.Rows, "Size"), Value(d.Rows, "Max bandwidth") }.Where(s => s is not null)),
            d.Rows, i < dumps.Count ? dumps[i] : null)).ToList());
    }

    static string? Value(List<InfoRow> rows, string key) => rows.FirstOrDefault(r => r.Label == key)?.Value is { } v and not "—" ? v : null;

    /// <summary>Lines of a section: the title is followed by a dashes line; the section runs to the next title.</summary>
    static IEnumerable<string> Section(IReadOnlyList<string> lines, string title)
    {
        static bool IsTitle(IReadOnlyList<string> l, int i) => i + 1 < l.Count && l[i].Trim().Length > 0 && l[i + 1].StartsWith("-----");
        for (var i = 0; i < lines.Count; i++)
        {
            if (!IsTitle(lines, i) || lines[i].Trim() != title) continue;
            for (var j = i + 2; j < lines.Count && !IsTitle(lines, j); j++) yield return lines[j];
            yield break;
        }
    }

    /// <summary>"Key\t\t\tValue" (with a leading tab on lines inside a block).</summary>
    static (bool Indented, string Key, string Value)? Row(string line)
    {
        var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : (line.StartsWith('\t'), parts[0], string.Join(" ", parts.Skip(1)));
    }

    static readonly Regex Leading = new(@"^\d+(?:[.,]\d+)?");

    static double? Number(string text) => Leading.Match(text.Trim()) is { Success: true } m
        && double.TryParse(m.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}
