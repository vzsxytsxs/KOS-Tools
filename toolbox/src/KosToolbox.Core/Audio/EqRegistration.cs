using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.Interop;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Tools;
using Microsoft.Win32;

namespace KosToolbox.Core.Audio;

/// <summary>A registry value as text: strings as they are, multi-strings joined by '\n', numbers in decimal, binary in Base64.</summary>
public sealed class FxValue
{
    public RegistryValueKind Kind { get; set; }
    public string Text { get; set; } = "";

    public static FxValue String(string text) => new() { Kind = RegistryValueKind.String, Text = text };
    public static FxValue Multi(IEnumerable<string> items) => new() { Kind = RegistryValueKind.MultiString, Text = string.Join('\n', items) };

    [JsonIgnore]
    public string[] Items => Kind == RegistryValueKind.MultiString ? Text.Split('\n', StringSplitOptions.RemoveEmptyEntries) : [Text];

    public static bool Same(FxValue? a, FxValue? b) =>
        a is null || b is null ? a is null && b is null : a.Kind == b.Kind && string.Equals(a.Text, b.Text, StringComparison.OrdinalIgnoreCase);

    public static FxValue? Read(RegistryKey key, string name)
    {
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) return null;
        var kind = key.GetValueKind(name);
        return new FxValue
        {
            Kind = kind,
            Text = value switch
            {
                string s => s, string[] a => string.Join('\n', a), byte[] b => Convert.ToBase64String(b),
                int i => i.ToString(CultureInfo.InvariantCulture), long l => l.ToString(CultureInfo.InvariantCulture), _ => value.ToString() ?? "",
            },
        };
    }

    public void Write(RegistryKey key, string name) => key.SetValue(name, Kind switch
    {
        RegistryValueKind.MultiString => Items,
        RegistryValueKind.DWord => int.Parse(Text, CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => long.Parse(Text, CultureInfo.InvariantCulture),
        RegistryValueKind.Binary => Convert.FromBase64String(Text),
        _ => (object)Text,
    }, Kind);

    public override string ToString() => Kind == RegistryValueKind.MultiString ? string.Join(" | ", Items) : Text;
}

/// <summary>One value of an endpoint's FxProperties: what it was (null = absent) and what KOS wrote (null = deleted).</summary>
public sealed class EqFxChange
{
    public string Name { get; set; } = "";
    public FxValue? Before { get; set; }
    public FxValue? After { get; set; }
}

/// <summary>What turning KOS Equalizer on changes on an endpoint, or why it can't (<see cref="Refused"/>).</summary>
public sealed record EqFxPlan(IReadOnlyList<EqFxChange> Changes, bool ReplacesOther, string? Refused);

/// <summary>The journal of what KOS Equalizer changed, to undo exactly that: %ProgramData%\TheKOS\Audio\registration.json.</summary>
public sealed class EqJournal
{
    public List<EqEndpointRecord> Endpoints { get; set; } = new();
    /// <summary>DisableProtectedAudioDG as it was before KOS set it to 1 (null: KOS didn't change it).</summary>
    public FxValue? ProtectedAudioBefore { get; set; }
    public bool ProtectedAudioSet { get; set; }
    /// <summary>KOS created the COM class and APO registration (removed with the last endpoint).</summary>
    public bool RegisteredClass { get; set; }
}

public sealed class EqEndpointRecord
{
    public string EndpointId { get; set; } = "";
    public string Name { get; set; } = "";
    public List<EqFxChange> Changes { get; set; } = new();
    /// <summary>The other equalizer was on this endpoint and was replaced (turning off puts it back).</summary>
    public bool ReplacedOther { get; set; }
    public DateTime Activated { get; set; }
}

/// <summary>KOS Equalizer on one endpoint, as read now.</summary>
public enum EqDeviceState { Off, On, OtherEqualizer, ChangedSince, Unsupported }

public sealed record EqStatus(EqDeviceState State, string? Detail, bool ReplacedOther, bool? Loaded);

/// <summary>
/// Turns KOS Equalizer's audio processing object on and off per endpoint: the COM class and APO registration (once), the
/// endpoint's effect values (FxProperties) and DisableProtectedAudioDG (unsigned APOs only load with it), then the audio service
/// restarts. Every value is journaled before it's written and put back on turning off only where it's still as KOS left it.
/// Where the other equalizer sits on the endpoint, KOS replaces it: its effects come out and what it wrapped goes back (from
/// its own record of the endpoint), KOS Equalizer takes the endpoint-effect slot; turning off restores it. An endpoint whose
/// driver has its own endpoint effect is refused (no stacking yet). Everything needs administrator.
/// </summary>
public static class EqRegistration
{
    public const string ClsidText = "{610C4861-EB61-417D-A653-45B03D493123}";
    const string IidApo = "{FD7F2B29-24D0-4B5C-B177-592C39F9CA10}"; // IAudioProcessingObject
    const string Fx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d}", Modes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b}";
    public const string ModeDefault = "{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}"; // AUDIO_SIGNALPROCESSINGMODE_DEFAULT
    /// <summary>The other equalizer's stream/mode effect and endpoint effect.</summary>
    public static readonly string[] OtherEqualizer = ["{EACD2258-FCAC-4FF4-B36D-419E924A6D79}", "{EC1CC9CE-FAED-4822-828A-82A81A6F018F}"];
    public const string OtherEqualizerName = "Equalizer APO";
    const string OtherChildKey = @"SOFTWARE\EqualizerAPO\Child APOs";
    const string Endpoints = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio";
    const string AudioKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Audio", ProtectedAudio = "DisableProtectedAudioDG";
    public const string DllName = "KosEqApo.dll";

    public static string FxKey(int pid) => $"{Fx},{pid}";
    public static string ModesKey(int pid) => $"{Modes},{pid}";
    public static string JournalPath => Path.Combine(EqSettingsFile.AudioDirectory, "registration.json");

    static bool IsOther(string? clsid) => clsid is not null && OtherEqualizer.Contains(clsid.Trim(), StringComparer.OrdinalIgnoreCase);
    static bool IsOurs(string? clsid) => string.Equals(clsid?.Trim(), ClsidText, StringComparison.OrdinalIgnoreCase);
    static bool IsClsid(string? text) => text is { Length: 38 } && text[0] == '{' && Guid.TryParse(text, out _);

    // ---------- the plan (pure: tested) ----------

    /// <summary>
    /// What turning KOS Equalizer on changes on an endpoint with these FxProperties values. <paramref name="otherChild"/> is the
    /// other equalizer's record of what it wrapped on this endpoint (value name → CLSID, or "!VALUE" for none), null if none.
    /// </summary>
    public static EqFxPlan Plan(IReadOnlyDictionary<string, FxValue> current, IReadOnlyDictionary<string, string>? otherChild)
    {
        var changes = new List<EqFxChange>();
        var replaces = false;
        FxValue? Get(string name) => current.TryGetValue(name, out var v) ? v : null;
        void Change(string name, FxValue? after)
        {
            var before = Get(name);
            if (!FxValue.Same(before, after)) changes.Add(new EqFxChange { Name = name, Before = before, After = after });
        }

        // The other equalizer's single-effect slots: what it wrapped goes back (its record), or the slot is emptied.
        foreach (var pid in new[] { 1, 2, 5, 6 })
            if (Get(FxKey(pid)) is { Kind: RegistryValueKind.String } v && IsOther(v.Text))
            {
                replaces = true;
                var original = otherChild?.GetValueOrDefault(FxKey(pid));
                Change(FxKey(pid), IsClsid(original) && !IsOther(original) ? FxValue.String(original!) : null);
            }
        // Composite slots (several effects): the other equalizer's entries come out.
        foreach (var pid in new[] { 13, 14, 15 })
            if (Get(FxKey(pid)) is { Kind: RegistryValueKind.MultiString } v && v.Items.Any(IsOther))
            {
                replaces = true;
                var rest = v.Items.Where(i => !IsOther(i)).ToList();
                Change(FxKey(pid), rest.Count == 0 ? null : FxValue.Multi(rest));
            }

        // The endpoint-effect slot: KOS Equalizer, unless the driver has an effect of its own there.
        var efx = Get(FxKey(7));
        if (efx is not null && !IsOurs(efx.Text) && !IsOther(efx.Text))
            return new EqFxPlan([], replaces, $"its driver has an endpoint effect of its own ({efx}); KOS Equalizer doesn't stack with it yet.");
        if (efx is not null && IsOther(efx.Text)) replaces = true;
        var composite = changes.FirstOrDefault(c => c.Name == FxKey(15)) is { } c15 ? c15.After : Get(FxKey(15));
        if (composite is not null && composite.Items.Any(i => !IsOurs(i)))
            return new EqFxPlan([], replaces, $"its driver has endpoint effects of its own ({composite}); KOS Equalizer doesn't stack with them yet.");
        Change(FxKey(7), FxValue.String(ClsidText));
        if (Get(ModesKey(7)) is null) Change(ModesKey(7), FxValue.Multi([ModeDefault]));
        return new EqFxPlan(changes, replaces, null);
    }

    /// <summary>
    /// What turning off writes back: each journaled value returns to what it was, only where it still holds what KOS wrote.
    /// <paramref name="changedSince"/> gets the names something else changed meanwhile (left as they are).
    /// </summary>
    public static List<EqFxChange> Undo(IEnumerable<EqFxChange> journal, IReadOnlyDictionary<string, FxValue> current, List<string> changedSince)
    {
        var undo = new List<EqFxChange>();
        foreach (var c in journal)
        {
            var now = current.TryGetValue(c.Name, out var v) ? v : null;
            if (FxValue.Same(now, c.After)) undo.Add(new EqFxChange { Name = c.Name, Before = c.After, After = c.Before });
            else if (!FxValue.Same(now, c.Before)) changedSince.Add(c.Name); // still as it was (never written): nothing to undo
        }
        return undo;
    }

    // ---------- the system ----------

    /// <summary>"{0.0.0.00000000}.{guid}" → Render/Capture and "{guid}" (its key under MMDevices\Audio).</summary>
    public static (string Flow, string Guid) Endpoint(string endpointId)
    {
        var guid = EqSettingsFile.FileName(endpointId);
        if (guid.Length != 36 || !System.Guid.TryParse(guid, out _)) throw new ArgumentException($"Not an audio endpoint id: {endpointId}", nameof(endpointId));
        var flow = endpointId.StartsWith("{0.0.1.", StringComparison.Ordinal) ? "Capture" : "Render";
        return (flow, "{" + guid + "}");
    }

    static string FxPath(string endpointId) { var (flow, guid) = Endpoint(endpointId); return $@"{Endpoints}\{flow}\{guid}\FxProperties"; }

    static RegistryKey Hklm => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    /// <summary>The endpoint's FxProperties values; null when the endpoint has no such key (no effects at all).</summary>
    public static Dictionary<string, FxValue>? ReadFx(string endpointId)
    {
        using var root = Hklm;
        using var key = root.OpenSubKey(FxPath(endpointId));
        if (key is null) return null;
        var values = new Dictionary<string, FxValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in key.GetValueNames())
            if (FxValue.Read(key, name) is { } v) values[name] = v;
        return values;
    }

    static Dictionary<string, string>? ReadOtherChild(string endpointId)
    {
        using var root = Hklm;
        using var key = root.OpenSubKey($@"{OtherChildKey}\{Endpoint(endpointId).Guid}");
        if (key is null) return null;
        return key.GetValueNames().Where(n => key.GetValue(n) is string).ToDictionary(n => n, n => (string)key.GetValue(n)!, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The journal; a journal that can't be trusted or read throws (an empty one would forget what to undo).</summary>
    public static EqJournal LoadJournal()
    {
        if (!File.Exists(JournalPath)) return new EqJournal();
        if ((GpuTuner.FolderProblem(AppPaths.TheKosRoot, EqSettingsFile.AudioDirectory) ?? ToolDiscovery.OwnerProblem(JournalPath)) is { } problem)
            throw new InvalidOperationException(problem);
        return JsonSerializer.Deserialize(File.ReadAllText(JournalPath), KosJson.Default.EqJournal) ?? throw new InvalidDataException($"{JournalPath} is empty.");
    }

    static void SaveJournal(EqJournal journal)
    {
        GpuTuner.PrepareDirectory(EqSettingsFile.AudioDirectory);
        File.Delete(JournalPath + ".tmp");
        JsonFile.Save(JournalPath, journal, KosJson.Default.EqJournal);
    }

    /// <summary>The installed DLL: %ProgramFiles%\KOS\equalizer\KosEqApo.dll.</summary>
    public static string InstalledDll => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "KOS", "equalizer", DllName);

    /// <summary>KOS Equalizer on this endpoint as read now; <see cref="EqStatus.Loaded"/> is whether the audio engine has the DLL loaded (null: not running).</summary>
    public static EqStatus Status(string endpointId)
    {
        var journal = LoadJournal();
        var record = journal.Endpoints.FirstOrDefault(e => string.Equals(e.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase));
        var fx = ReadFx(endpointId);
        if (fx is null) return new EqStatus(EqDeviceState.Unsupported, "This device has no audio effects key.", false, null);
        var ours = fx.TryGetValue(FxKey(7), out var efx) && IsOurs(efx.Text);
        if (record is not null && ours) return new EqStatus(EqDeviceState.On, null, record.ReplacedOther, Loaded());
        if (record is not null) return new EqStatus(EqDeviceState.ChangedSince, "The device's effects were changed since (a driver update?): turn it on again.", record.ReplacedOther, null);
        var plan = Plan(fx, null);
        if (plan.Refused is { } refused) return new EqStatus(EqDeviceState.Unsupported, "This device " + refused, false, null);
        return new EqStatus(plan.ReplacesOther ? EqDeviceState.OtherEqualizer : EqDeviceState.Off, null, false, null);
    }

    /// <summary>Whether the audio engine (audiodg) has the DLL loaded now; null when it isn't running or can't be read.</summary>
    public static bool? Loaded()
    {
        try
        {
            var engines = Process.GetProcessesByName("audiodg");
            if (engines.Length == 0) return null;
            foreach (var p in engines)
                using (p)
                    if (p.Modules.Cast<ProcessModule>().Any(m => string.Equals(Path.GetFileName(m.FileName), DllName, StringComparison.OrdinalIgnoreCase))) return true;
            return false;
        }
        catch { return null; }
    }

    /// <summary>
    /// Turns KOS Equalizer on for an endpoint: registers the DLL (<paramref name="dll"/>, which must be the installed copy under
    /// Program Files owned by SYSTEM/Administrators), sets DisableProtectedAudioDG, writes the endpoint's effect values (journaled
    /// first), writes a flat settings file if there's none, restarts the audio service and reads everything back.
    /// </summary>
    public static EqEndpointRecord Activate(string endpointId, string name, string dll)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(dll).StartsWith(programFiles, StringComparison.OrdinalIgnoreCase) || !File.Exists(dll))
            throw new InvalidOperationException($"KOS Equalizer's audio effect ({DllName}) must be installed under {programFiles} first.");
        if (ToolDiscovery.OwnerProblem(dll) is { } owner) throw new InvalidOperationException(owner);
        var journal = LoadJournal();
        if (journal.Endpoints.Any(e => string.Equals(e.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("KOS Equalizer is already on for this device.");
        var fx = ReadFx(endpointId) ?? throw new InvalidOperationException("This device has no audio effects key, so an effect can't be added to it.");
        var plan = Plan(fx, ReadOtherChild(endpointId));
        if (plan.Refused is { } refused) throw new InvalidOperationException("This device " + refused);

        var record = new EqEndpointRecord { EndpointId = endpointId, Name = name, Changes = plan.Changes.ToList(), ReplacedOther = plan.ReplacesOther, Activated = DateTime.UtcNow };
        journal.Endpoints.Add(record);
        using var root = Hklm;
        FxValue? protectedNow;
        using (var audio = root.OpenSubKey(AudioKey)) protectedNow = audio is null ? null : FxValue.Read(audio, ProtectedAudio);
        var setProtected = protectedNow?.Text != "1";
        if (setProtected && !journal.ProtectedAudioSet) { journal.ProtectedAudioBefore = protectedNow; journal.ProtectedAudioSet = true; }
        var register = !ClassRegistered(dll);
        if (register) journal.RegisteredClass = true;
        SaveJournal(journal); // before any write: a crash halfway still leaves the way back

        try
        {
            if (register) RegisterClass(root, dll);
            if (setProtected)
                using (var audio = root.CreateSubKey(AudioKey)) audio.SetValue(ProtectedAudio, 1, RegistryValueKind.DWord);
            WriteFx(root, endpointId, plan.Changes);
            if (!File.Exists(EqSettingsFile.PathFor(endpointId))) EqSettingsFile.Write(endpointId, null);
            Log.Info($"KOS Equalizer on for {name} ({endpointId}): {string.Join("; ", plan.Changes.Select(c => $"{c.Name} {c.Before?.ToString() ?? "absent"} -> {c.After?.ToString() ?? "absent"}"))}");
            var back = ReadFx(endpointId) ?? new();
            var wrong = plan.Changes.Where(c => !FxValue.Same(back.GetValueOrDefault(c.Name), c.After)).Select(c => c.Name).ToList();
            if (wrong.Count > 0) throw new InvalidOperationException($"The device's effects don't read back as written ({string.Join(", ", wrong)}).");
        }
        catch (Exception ex)
        {
            // Halfway on is worse than off: put back what was written (the journal knows), then report the first failure.
            Log.Error($"KOS Equalizer: turning on {name} failed; undoing", ex);
            try { Deactivate(endpointId); } catch (Exception undo) { Log.Error("KOS Equalizer: the undo failed too", undo); }
            throw;
        }
        RestartAudio();
        return record;
    }

    /// <summary>
    /// Turns KOS Equalizer off for an endpoint: its values go back where they're still as KOS left them (the names changed since
    /// are returned), and with the last endpoint the registration and DisableProtectedAudioDG go back too; the audio service restarts.
    /// </summary>
    public static List<string> Deactivate(string endpointId)
    {
        var journal = LoadJournal();
        var record = journal.Endpoints.FirstOrDefault(e => string.Equals(e.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("KOS Equalizer isn't on for this device.");
        var changedSince = new List<string>();
        using var root = Hklm;
        if (ReadFx(endpointId) is { } fx) WriteFx(root, endpointId, Undo(record.Changes, fx, changedSince));
        else changedSince.AddRange(record.Changes.Select(c => c.Name)); // the device's key is gone: nothing left to put back
        journal.Endpoints.Remove(record);
        if (journal.Endpoints.Count == 0)
        {
            if (journal.ProtectedAudioSet)
                using (var audio = root.CreateSubKey(AudioKey))
                    if (FxValue.Read(audio, ProtectedAudio)?.Text == "1")
                    {
                        if (journal.ProtectedAudioBefore is { } before) before.Write(audio, ProtectedAudio);
                        else audio.DeleteValue(ProtectedAudio, false);
                    }
            if (journal.RegisteredClass) UnregisterClass(root);
            journal = new EqJournal();
        }
        SaveJournal(journal);
        Log.Info($"KOS Equalizer off for {record.Name} ({endpointId}); left as found: {(changedSince.Count == 0 ? "none" : string.Join(", ", changedSince))}");
        RestartAudio();
        return changedSince;
    }

    static void WriteFx(RegistryKey root, string endpointId, IEnumerable<EqFxChange> changes)
    {
        // Administrators may only set values on an endpoint's FxProperties: ask for exactly that.
        using var key = root.OpenSubKey(FxPath(endpointId), RegistryKeyPermissionCheck.ReadWriteSubTree,
            System.Security.AccessControl.RegistryRights.SetValue | System.Security.AccessControl.RegistryRights.QueryValues)
            ?? throw new InvalidOperationException("This device's audio effects key is gone.");
        foreach (var c in changes)
            if (c.After is { } after) after.Write(key, c.Name);
            else key.DeleteValue(c.Name, false);
    }

    static bool ClassRegistered(string dll)
    {
        using var root = Hklm;
        using var server = root.OpenSubKey($@"SOFTWARE\Classes\CLSID\{ClsidText}\InprocServer32");
        using var apo = root.OpenSubKey($@"SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\{ClsidText}");
        return apo is not null && string.Equals(server?.GetValue(null) as string, dll, StringComparison.OrdinalIgnoreCase);
    }

    static void RegisterClass(RegistryKey root, string dll)
    {
        using (var cls = root.CreateSubKey($@"SOFTWARE\Classes\CLSID\{ClsidText}")) cls.SetValue(null, "KOS Equalizer");
        using (var server = root.CreateSubKey($@"SOFTWARE\Classes\CLSID\{ClsidText}\InprocServer32"))
        {
            server.SetValue(null, dll);
            server.SetValue("ThreadingModel", "Both");
        }
        using var apo = root.CreateSubKey($@"SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\{ClsidText}");
        apo.SetValue("FriendlyName", "KOS Equalizer");
        apo.SetValue("Copyright", "KOS");
        foreach (var (n, v) in new (string, int)[] { ("MajorVersion", 1), ("MinorVersion", 0), ("Flags", 0xF), ("MinInputConnections", 1), ("MaxInputConnections", 1),
                     ("MinOutputConnections", 1), ("MaxOutputConnections", 1), ("MaxInstances", -1), ("NumAPOInterfaces", 1) })
            apo.SetValue(n, v, RegistryValueKind.DWord);
        apo.SetValue("APOInterface0", IidApo);
    }

    static void UnregisterClass(RegistryKey root)
    {
        root.DeleteSubKeyTree($@"SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\{ClsidText}", false);
        root.DeleteSubKeyTree($@"SOFTWARE\Classes\CLSID\{ClsidText}", false);
    }

    /// <summary>The audio service restarts so the audio engine loads the endpoints' effects again (sound stops for a moment).</summary>
    static void RestartAudio() => ServiceControl.Restart("Audiosrv", TimeSpan.FromSeconds(20));
}
