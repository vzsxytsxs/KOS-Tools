using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;

namespace KosToolbox.Core.Apps;

/// <summary>Windows optional feature as "dism /get-features" shows it (Enabled, Disabled, Enable Pending…).</summary>
public sealed record OptionalFeature(string Name, string State)
{
    public bool IsEnabled => State.StartsWith("Enable", StringComparison.OrdinalIgnoreCase);
    public bool IsPending => State.EndsWith("Pending", StringComparison.OrdinalIgnoreCase);
}

public sealed record OptionalFeatureInfo(string DisplayName, string Description, string Restart);

/// <summary>
/// "Turn Windows features on or off" through the official dism.exe with /English (output always in English, regardless of the
/// Windows language). DISM requires administrator even to list.
/// </summary>
public static class OptionalFeatures
{
    static string Dism => Path.Combine(Environment.SystemDirectory, "dism.exe");
    const int RebootRequired = 3010;

    public static async Task<List<OptionalFeature>> ListAsync(CancellationToken ct = default)
    {
        var r = await ProcessRunner.RunAsync(Dism, "/online /english /get-features /format:table", timeout: TimeSpan.FromMinutes(5), ct: ct).ConfigureAwait(false);
        if (r.ExitCode != 0) throw new InvalidOperationException(Txt.DismFailed(r.ExitCode, r.Output));
        return ParseTable(r.Output);
    }

    /// <summary>Capabilities ("Optional features" in Settings) and their state: Installed, Not Present, Install Pending…</summary>
    public static async Task<List<OptionalFeature>> ListCapabilitiesAsync(CancellationToken ct = default)
    {
        var r = await ProcessRunner.RunAsync(Dism, "/online /english /get-capabilities /format:table", timeout: TimeSpan.FromMinutes(5), ct: ct).ConfigureAwait(false);
        if (r.ExitCode != 0) throw new InvalidOperationException(Txt.DismFailed(r.ExitCode, r.Output));
        return ParseTable(r.Output, "Capability Identity");
    }

    /// <summary>Removes an installed capability. Returns true if a restart is required.</summary>
    public static async Task<bool> RemoveCapabilityAsync(string name, CancellationToken ct = default)
    {
        Validate(name, capability: true);
        var r = await ProcessRunner.RunAsync(Dism, $"/online /english /remove-capability /capabilityname:{name} /norestart", timeout: TimeSpan.FromMinutes(30), ct: ct).ConfigureAwait(false);
        Log.Info($"dism remove-capability {name}: exit {r.ExitCode}");
        if (r.ExitCode is not (0 or RebootRequired)) throw new InvalidOperationException(Txt.DismFailed(r.ExitCode, r.Output));
        return r.ExitCode == RebootRequired;
    }

    /// <summary>"Name | State" lines after the "Feature Name | State" (or other) header.</summary>
    internal static List<OptionalFeature> ParseTable(string output, string header = "Feature Name")
    {
        var list = new List<OptionalFeature>();
        var inTable = false;
        foreach (var raw in output.Split('\n'))
        {
            var parts = raw.TrimEnd('\r').Split('|');
            if (parts.Length != 2) continue;
            var (name, state) = (parts[0].Trim(), parts[1].Trim());
            if (name.StartsWith(header, StringComparison.Ordinal)) { inTable = true; continue; }
            if (!inTable || name.Length == 0 || name.StartsWith('-')) continue;
            list.Add(new OptionalFeature(name, state));
        }
        return list.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static async Task<OptionalFeatureInfo> InfoAsync(string name, CancellationToken ct = default)
    {
        Validate(name);
        var r = await ProcessRunner.RunAsync(Dism, $"/online /english /get-featureinfo /featurename:{name}", timeout: TimeSpan.FromMinutes(2), ct: ct).ConfigureAwait(false);
        if (r.ExitCode != 0) throw new InvalidOperationException(Txt.DismFailed(r.ExitCode, r.Output));
        return ParseInfo(r.Output);
    }

    /// <summary>"Display Name : …", "Description : …" and "Restart Required : …" fields from /get-featureinfo.</summary>
    internal static OptionalFeatureInfo ParseInfo(string output)
    {
        string Field(string label) => output.Split('\n').Select(l => l.TrimEnd('\r'))
            .FirstOrDefault(l => l.StartsWith(label + " :", StringComparison.Ordinal))?[(label.Length + 2)..].Trim() ?? "";
        return new OptionalFeatureInfo(Field("Display Name"), Field("Description"), Field("Restart Required"));
    }

    /// <summary>Enables (with /all: includes the features it depends on) or disables. Returns true if a restart is required.</summary>
    public static async Task<bool> SetAsync(string name, bool enable, CancellationToken ct = default)
    {
        Validate(name);
        var args = enable ? $"/online /english /enable-feature /featurename:{name} /all /norestart" : $"/online /english /disable-feature /featurename:{name} /norestart";
        var r = await ProcessRunner.RunAsync(Dism, args, timeout: TimeSpan.FromMinutes(30), ct: ct).ConfigureAwait(false);
        Log.Info($"dism {(enable ? "enable" : "disable")} {name}: exit {r.ExitCode}");
        if (r.ExitCode is not (0 or RebootRequired)) throw new InvalidOperationException(Txt.DismFailed(r.ExitCode, r.Output));
        return r.ExitCode == RebootRequired;
    }

    /// <summary>Feature names are identifiers (letters, digits, dot, hyphen, underscore; ~ in capabilities): nothing becomes an extra argument.</summary>
    internal static void Validate(string name, bool capability = false)
    {
        if (name.Length is 0 or > 200 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' || (capability && c == '~')))
            throw new InvalidOperationException($"Invalid feature name: {name}");
    }
}
