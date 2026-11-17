using System.Diagnostics;
using System.Text.Json;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Tools;

namespace KosToolbox.Core.Peripherals.Batteries;

/// <summary>One device HeadsetControl reported.</summary>
public sealed record HeadsetControlDevice(ushort VendorId, ushort ProductId, string Name, PeripheralBattery Battery);

/// <summary>
/// HeadsetControl (GPL-3.0, a separate program in the KOS tools folder, never linked): run as a process with no window, its JSON read
/// with JsonDocument (no reflection, AOT-safe). Used only for VID/PIDs no driver claimed.
/// </summary>
public static class HeadsetControl
{
    const int TimeoutMs = 30_000, MaxOutput = 1 << 20;

    public static string? FindExe()
    {
        try
        {
            return Directory.Exists(ToolDiscovery.Root)
                ? Directory.EnumerateFiles(ToolDiscovery.Root, "headsetcontrol.exe", SearchOption.AllDirectories).FirstOrDefault()
                : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Runs it (when present and, elevated, owned by SYSTEM/Administrators) and parses its devices; empty otherwise.</summary>
    public static List<HeadsetControlDevice> Run()
    {
        if (FindExe() is not { } exe || Elevation.IsAdmin && ToolDiscovery.OwnerProblem(exe) is not null) return new();
        var start = new ProcessStartInfo(exe, "-b -o json --timeout 1500")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        using var p = Process.Start(start);
        if (p is null) return new();
        p.ErrorDataReceived += (_, _) => { };
        p.BeginErrorReadLine();
        var output = new char[MaxOutput];
        var read = p.StandardOutput.ReadBlockAsync(output, 0, MaxOutput);
        if (!read.Wait(TimeoutMs) || !p.WaitForExit(1000))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new();
        }
        return Parse(new string(output, 0, read.Result));
    }

    /// <summary><c>devices[].{id_vendor, id_product, device, battery.{status, level}}</c>; ids as hex strings ("0x1038") or numbers.</summary>
    public static List<HeadsetControlDevice> Parse(string json)
    {
        var list = new List<HeadsetControlDevice>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array) return list;
            foreach (var d in devices.EnumerateArray())
            {
                if (Id(d, "id_vendor") is not { } vid || Id(d, "id_product") is not { } pid) continue;
                if (!d.TryGetProperty("battery", out var b) || b.ValueKind != JsonValueKind.Object) continue;
                var status = b.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                int? level = b.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out var v) && v is >= 0 and <= 100 ? v : null;
                var battery = status switch
                {
                    "BATTERY_UNAVAILABLE" => new PeripheralBattery(null, null, Asleep: true),
                    "BATTERY_CHARGING" => new PeripheralBattery(level, true),
                    "BATTERY_AVAILABLE" => new PeripheralBattery(level, false),
                    _ => null,
                };
                if (battery is null) continue;
                var name = d.TryGetProperty("device", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                list.Add(new HeadsetControlDevice(vid, pid, string.IsNullOrWhiteSpace(name) ? "Headset" : name!, battery));
            }
        }
        catch (JsonException) { }
        return list;
    }

    static ushort? Id(JsonElement d, string name)
    {
        if (!d.TryGetProperty(name, out var e)) return null;
        if (e.ValueKind == JsonValueKind.Number && e.TryGetUInt16(out var n)) return n;
        if (e.ValueKind == JsonValueKind.String && e.GetString() is { } s)
        {
            s = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
            if (ushort.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var h)) return h;
        }
        return null;
    }
}
