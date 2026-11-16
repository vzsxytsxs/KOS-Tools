using Microsoft.Win32;

namespace KosToolbox.Core.Settings;

/// <summary>The three per-program settings for a game; <see cref="Path"/> is a full path, or just a file name for a QoS policy alone.</summary>
public sealed record GameWindowsSettings(string Path, bool Dscp, bool FullscreenOptimizationsOff, bool HighPerformanceGpu)
{
    /// <summary>The compatibility and GPU settings need the full path (Windows keys them by it).</summary>
    public bool HasFullPath => System.IO.Path.IsPathFullyQualified(Path);
}

/// <summary>
/// Per-program Windows settings for games, as the KOS "Auto DSCP &amp; FSE" script wrote them:
/// a QoS policy marking the program's traffic DSCP 46 (HKLM\SOFTWARE\Policies\Microsoft\Windows\QoS\&lt;name&gt;, administrator),
/// fullscreen optimizations off with the high-DPI override (AppCompatFlags\Layers, "~ DISABLEDXMAXIMIZEDWINDOWEDMODE HIGHDPIAWARE")
/// and the high-performance GPU (DirectX\UserGpuPreferences, "GpuPreference=2;"). The last two are per user and keyed by the
/// full path. Other compatibility flags and DirectX pairs in the same values are kept.
/// </summary>
public static class GameSettings
{
    const string QosKey = @"SOFTWARE\Policies\Microsoft\Windows\QoS", NlaKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\QoS";
    const string LayersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", GpuKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    internal static readonly string[] FullscreenFlags = { "DISABLEDXMAXIMIZEDWINDOWEDMODE", "HIGHDPIAWARE" };
    public const string Dscp = "46";

    /// <summary>Every program with at least one of the three, merged by path (a QoS policy by file name joins the paths with that name).</summary>
    public static List<GameWindowsSettings> List()
    {
        var paths = new Dictionary<string, (bool Dscp, bool Fse, bool Gpu)>(StringComparer.OrdinalIgnoreCase);
        using (var layers = Registry.CurrentUser.OpenSubKey(LayersKey))
            foreach (var name in layers?.GetValueNames() ?? Array.Empty<string>())
                if (HasFullscreenFlags(layers!.GetValue(name) as string)) paths[name] = (false, true, false);
        using (var gpu = Registry.CurrentUser.OpenSubKey(GpuKey))
            foreach (var name in gpu?.GetValueNames() ?? Array.Empty<string>())
                if (DxSetting.Get(gpu!.GetValue(name) as string, "GpuPreference") == "2" && Path.IsPathFullyQualified(name))
                    paths[name] = paths.TryGetValue(name, out var s) ? s with { Gpu = true } : (false, false, true);
        foreach (var app in QosApplications())
        {
            var matched = paths.Keys.Where(p => Matches(app, p)).ToList();
            if (matched.Count == 0) paths[app] = (true, false, false);
            foreach (var p in matched) paths[p] = paths[p] with { Dscp = true };
        }
        return paths.Select(p => new GameWindowsSettings(p.Key, p.Value.Dscp, p.Value.Fse, p.Value.Gpu)).OrderBy(s => Path.GetFileName(s.Path), StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static GameWindowsSettings Read(string path)
    {
        using var layers = Registry.CurrentUser.OpenSubKey(LayersKey);
        using var gpu = Registry.CurrentUser.OpenSubKey(GpuKey);
        return new GameWindowsSettings(path, QosApplications().Any(a => Matches(a, path)),
            HasFullscreenFlags(layers?.GetValue(path) as string), DxSetting.Get(gpu?.GetValue(path) as string, "GpuPreference") == "2");
    }

    /// <summary>
    /// QoS policy by file name (it survives the game moving folders); needs administrator. Turning it on also sets "Do not use NLA",
    /// without which Windows applies QoS policies only on domain networks.
    /// </summary>
    public static void SetDscp(string path, bool on)
    {
        var file = Path.GetFileName(path);
        if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"Not a program file name: {path}");
        using var qos = Registry.LocalMachine.CreateSubKey(QosKey, writable: true);
        // Only the policies KOS made: one from Group Policy or another program stays (and still reads as on).
        foreach (var name in qos.GetSubKeyNames().Where(n => n.StartsWith("KOS ", StringComparison.Ordinal)))
        {
            using var policy = qos.OpenSubKey(name);
            if (policy?.GetValue("Application Name") is string app && Matches(app, path) && policy.GetValue("DSCP Value") as string == Dscp) qos.DeleteSubKeyTree(name, false);
        }
        using var nla = Registry.LocalMachine.CreateSubKey(NlaKey, writable: true);
        if (!on)
        {
            // The last KOS policy gone: "Do not use NLA" goes back to what it was before KOS set it.
            if (!qos.GetSubKeyNames().Any(n => n.StartsWith("KOS ", StringComparison.Ordinal)) && nla.GetValue(NlaBackup) is string previous)
            {
                if (previous.Length == 0) nla.DeleteValue(NlaValue, throwOnMissingValue: false);
                else nla.SetValue(NlaValue, previous, RegistryValueKind.String);
                nla.DeleteValue(NlaBackup, throwOnMissingValue: false);
            }
            return;
        }
        using (var policy = qos.CreateSubKey($"KOS {file}", writable: true))
        {
            foreach (var (name, value) in new[] { ("Version", "1.0"), ("Application Name", file), ("Protocol", "*"), ("Local Port", "*"), ("Local IP", "*"),
                         ("Local IP Prefix Length", "*"), ("Remote Port", "*"), ("Remote IP", "*"), ("Remote IP Prefix Length", "*"), ("DSCP Value", Dscp), ("Throttle Rate", "-1") })
                policy.SetValue(name, value, RegistryValueKind.String);
        }
        if (nla.GetValue(NlaValue) as string != "1" && nla.GetValue(NlaBackup) is null)
            nla.SetValue(NlaBackup, nla.GetValue(NlaValue) as string ?? "", RegistryValueKind.String); // "" = it wasn't there
        nla.SetValue(NlaValue, "1", RegistryValueKind.String);
    }

    const string NlaValue = "Do not use NLA", NlaBackup = "KOS previous Do not use NLA";

    public static void SetFullscreenOptimizationsOff(string path, bool on)
    {
        RequireFullPath(path);
        using var key = Registry.CurrentUser.CreateSubKey(LayersKey, writable: true);
        var text = Layers(key.GetValue(path) as string, on);
        if (text is null) key.DeleteValue(path, throwOnMissingValue: false);
        else key.SetValue(path, text, RegistryValueKind.String);
    }

    public static void SetHighPerformanceGpu(string path, bool on)
    {
        RequireFullPath(path);
        using var key = Registry.CurrentUser.CreateSubKey(GpuKey, writable: true);
        var text = DxSetting.Set(key.GetValue(path) as string, "GpuPreference", on ? "2" : null);
        if (text.Length == 0) key.DeleteValue(path, throwOnMissingValue: false);
        else key.SetValue(path, text, RegistryValueKind.String);
    }

    static void RequireFullPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"Needs the program's full path: {path}");
    }

    static List<string> QosApplications()
    {
        var list = new List<string>();
        using var qos = Registry.LocalMachine.OpenSubKey(QosKey);
        foreach (var name in qos?.GetSubKeyNames() ?? Array.Empty<string>())
        {
            using var policy = qos!.OpenSubKey(name);
            if (policy?.GetValue("Application Name") is string { Length: > 0 } app && policy.GetValue("DSCP Value") as string == Dscp) list.Add(app);
        }
        return list;
    }

    /// <summary>A policy's application is a full path (that program) or a file name (any program with that name).</summary>
    internal static bool Matches(string application, string path) =>
        application.Equals(path, StringComparison.OrdinalIgnoreCase)
        || !application.Contains('\\') && application.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase);

    internal static bool HasFullscreenFlags(string? layers) =>
        layers is not null && layers.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(FullscreenFlags[0], StringComparer.OrdinalIgnoreCase);

    /// <summary>The Layers string with the two flags added or removed; other flags stay. Null = nothing left (delete the value).</summary>
    internal static string? Layers(string? current, bool on)
    {
        var flags = (current ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(f => f != "~" && !FullscreenFlags.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
        if (on) flags.AddRange(FullscreenFlags);
        return flags.Count == 0 ? null : "~ " + string.Join(" ", flags);
    }
}
