using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;
using Microsoft.Win32;

namespace KosToolbox.Core.Power;

public sealed record PowerPlan(Guid Id, string Name, bool IsActive);

public enum PowerValueKind { Choice, Range, ReadOnly }

public sealed record PowerChoice(uint Value, string Label)
{
    public override string ToString() => Label; // text read by the screen reader and by the ComboBox's type-to-search
}

/// <summary>Metadata and values of a power setting in a plan.</summary>
public sealed class PowerSettingInfo
{
    public required Guid Subgroup { get; init; }
    public required Guid Setting { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public bool Hidden { get; init; }
    public PowerValueKind Kind { get; init; }
    public IReadOnlyList<PowerChoice> Choices { get; init; } = Array.Empty<PowerChoice>();
    public uint Min { get; init; }
    public uint Max { get; init; }
    public uint Increment { get; init; }
    public string Units { get; init; } = "";
    /// <summary>Current value on AC (plugged in) and DC (battery); null when the read failed.</summary>
    public uint? Ac { get; init; }
    public uint? Dc { get; init; }
    public string? ReadError { get; init; }
    /// <summary>For <see cref="PowerValueKind.ReadOnly"/>: why it can't be edited.</summary>
    public string? ReadOnlyReason { get; init; }
    /// <summary>True when the English name wasn't found and the label is technical.</summary>
    public bool TechnicalName { get; init; }
    /// <summary>The plan has its own stored value (ACSettingIndex/DCSettingIndex); without it the plan's own default applies.</summary>
    public bool StoredAc { get; init; }
    public bool StoredDc { get; init; }
}

public sealed class PowerSubgroupInfo
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public bool Hidden { get; init; }
    public List<PowerSettingInfo> Settings { get; } = new();
}

public sealed class PowerWrite
{
    public Guid Scheme { get; set; }
    public Guid Subgroup { get; set; }
    public Guid Setting { get; set; }
    public bool Ac { get; set; }
    public uint Value { get; set; }
    /// <summary>Value typed by the user for a list setting: accepted outside the list (any 32-bit value).</summary>
    public bool Custom { get; set; }
}

/// <summary>An operation on a plan (copy, rename, delete, import, export, restore) or on a setting's metadata.</summary>
public sealed class PowerPlanOp
{
    public const string Duplicate = "duplicate", Rename = "rename", Delete = "delete", Import = "import", Export = "export",
        RestoreDefaults = "restore-defaults", RemoveValue = "remove-value", SetHidden = "set-hidden";
    public string Op { get; set; } = "";
    public Guid Scheme { get; set; }
    /// <summary>GUID of the created plan (Duplicate/Import), chosen beforehand so the UI can find the new plan.</summary>
    public Guid NewScheme { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Path { get; set; }
    public Guid Subgroup { get; set; }
    public Guid Setting { get; set; }
    /// <summary>RemoveValue: removes the AC value (true) or the DC value (false).</summary>
    public bool Ac { get; set; }
    /// <summary>SetHidden: sets (true) or clears ATTRIBUTE_HIDE.</summary>
    public bool Hidden { get; set; }
}

/// <summary>
/// Power plans and their settings through PowrProf (PowerEnumerate etc.), without powercfg.
/// English names and descriptions come from the fallback text stored in the registry next to the MUI resource
/// ("@%SystemRoot%\system32\powrprof.dll,-NNN,Text"); without it, the label is technical (GUID).
/// </summary>
public static class PowerPlans
{
    public static readonly Guid NoSubgroup = new("fea3413e-7e05-4911-9a71-700331f1c294");
    const uint AccessScheme = 16, AccessSubgroup = 17, AccessIndividualSetting = 18;
    const uint ErrorNoMoreItems = 259, ErrorAccessDenied = 5, MaxChoices = 256;
    const string SettingsKey = @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings";
    const string SchemesKey = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
    const string DefaultSchemesKey = @"SYSTEM\CurrentControlSet\Control\Power\User\Default\PowerSchemes";

    [DllImport("powrprof.dll", EntryPoint = "PowerEnumerate")] static extern uint EnumSchemes(IntPtr root, IntPtr scheme, IntPtr sub, uint access, uint index, byte[] buffer, ref uint size);
    [DllImport("powrprof.dll", EntryPoint = "PowerEnumerate")] static extern uint EnumSubgroups(IntPtr root, ref Guid scheme, IntPtr sub, uint access, uint index, byte[] buffer, ref uint size);
    [DllImport("powrprof.dll", EntryPoint = "PowerEnumerate")] static extern uint EnumSettings(IntPtr root, ref Guid scheme, ref Guid sub, uint access, uint index, byte[] buffer, ref uint size);
    [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] static extern uint PowerReadValueMin(IntPtr root, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerReadValueMax(IntPtr root, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerReadValueIncrement(IntPtr root, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerReadPossibleValue(IntPtr root, ref Guid sub, ref Guid setting, out uint type, uint index, byte[]? buffer, ref uint size);
    [DllImport("powrprof.dll", EntryPoint = "PowerReadSettingAttributes")] static extern uint SettingAttributes(ref Guid sub, ref Guid setting);
    [DllImport("powrprof.dll", EntryPoint = "PowerReadSettingAttributes")] static extern uint SubgroupAttributes(ref Guid sub, IntPtr setting);
    [DllImport("powrprof.dll")] static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr setting, byte[]? buffer, ref uint size);
    [DllImport("powrprof.dll")] static extern uint PowerReadDescription(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr setting, byte[]? buffer, ref uint size);
    [DllImport("powrprof.dll")] static extern uint PowerWriteFriendlyName(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr setting, byte[] buffer, uint size);
    [DllImport("powrprof.dll")] static extern uint PowerWriteDescription(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr setting, byte[] buffer, uint size);
    [DllImport("powrprof.dll")] static extern uint PowerDuplicateScheme(IntPtr root, ref Guid source, ref IntPtr destination);
    [DllImport("powrprof.dll", CharSet = CharSet.Unicode)] static extern uint PowerImportPowerScheme(IntPtr root, string path, ref IntPtr destination);
    [DllImport("powrprof.dll")] static extern uint PowerDeleteScheme(IntPtr root, ref Guid scheme);
    [DllImport("powrprof.dll")] static extern uint PowerRestoreDefaultPowerSchemes();
    [DllImport("powrprof.dll")] static extern uint PowerWriteSettingAttributes(ref Guid sub, ref Guid setting, uint attributes);
    [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);

    public static Guid ActiveScheme()
    {
        var err = PowerGetActiveScheme(IntPtr.Zero, out var ptr);
        if (err != 0) throw new InvalidOperationException($"PowerGetActiveScheme failed ({err}).");
        try { return Marshal.PtrToStructure<Guid>(ptr); }
        finally { LocalFree(ptr); }
    }

    public static void Activate(Guid scheme)
    {
        var err = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        if (err != 0) throw new InvalidOperationException($"PowerSetActiveScheme failed ({err}).");
        Log.Info($"Power plan activated: {scheme}");
    }

    /// <summary>Installed plans. A user-defined name is preserved; Windows names use the English text.</summary>
    public static List<PowerPlan> List()
    {
        var active = ActiveScheme();
        var list = new List<PowerPlan>();
        for (uint i = 0; ; i++)
        {
            var buf = new byte[16];
            uint size = 16;
            var err = EnumSchemes(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, i, buf, ref size);
            if (err == ErrorNoMoreItems) break;
            if (err != 0) throw new InvalidOperationException($"PowerEnumerate (schemes) failed ({err}).");
            var id = new Guid(buf);
            list.Add(new PowerPlan(id, SchemeName(id), id == active));
        }
        return list;
    }

    static string SchemeName(Guid id) => SchemeText(id, "FriendlyName", PowerReadFriendlyName) ?? Txt.PlanTechnicalName(id);

    /// <summary>The plan's description in English (empty when there is none).</summary>
    public static string Description(Guid id) => SchemeText(id, "Description", PowerReadDescription) ?? "";

    delegate uint SchemeTextReader(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr setting, byte[]? buffer, ref uint size);

    static string? SchemeText(Guid id, string valueName, SchemeTextReader read)
    {
        var raw = RegString($@"{SchemesKey}\{id:D}", valueName) ?? RegString($@"{DefaultSchemesKey}\{id:D}", valueName);
        if (EnglishText(raw) is { } english) return english;
        uint size = 0;
        read(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, null, ref size);
        var buf = new byte[size];
        if (size > 0 && read(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, buf, ref size) == 0)
            return Encoding.Unicode.GetString(buf).TrimEnd('\0');
        return null;
    }

    static readonly Regex Indirect = new(@"^@[^,]+,\s*-?\d+(?:,(?<text>.*))?$", RegexOptions.Singleline);

    /// <summary>
    /// English text of a registry name: the fallback text after the resource id, or the text itself
    /// when it isn't an indirect reference. Null when there is only the reference (the text would be the translated one).
    /// </summary>
    internal static string? EnglishText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!raw.StartsWith('@')) return raw.Trim();
        var m = Indirect.Match(raw);
        return m.Success && m.Groups["text"].Value.Trim() is { Length: > 0 } text ? text : null;
    }

    static string? RegString(string path, string name)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(path);
            // REG_SZ ends at the first NUL; some plans are stored with the name padded with NULs.
            return (key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string)?.Split('\0')[0];
        }
        catch { return null; }
    }

    static string SettingKey(Guid sub, Guid setting) =>
        sub == NoSubgroup ? $@"{SettingsKey}\{setting:D}" : $@"{SettingsKey}\{sub:D}\{setting:D}";

    static List<Guid> Enumerate(Func<uint, byte[], uint> call)
    {
        var list = new List<Guid>();
        for (uint i = 0; ; i++)
        {
            var buf = new byte[16];
            var err = call(i, buf);
            if (err == ErrorNoMoreItems) break;
            if (err != 0) throw new InvalidOperationException($"PowerEnumerate failed ({err}).");
            list.Add(new Guid(buf));
        }
        return list;
    }

    /// <summary>The plan's subgroups and settings, with metadata and current AC/DC values.</summary>
    public static List<PowerSubgroupInfo> Read(Guid scheme)
    {
        var subs = Enumerate((i, buf) => { uint size = 16; return EnumSubgroups(IntPtr.Zero, ref scheme, IntPtr.Zero, AccessSubgroup, i, buf, ref size); });
        if (!subs.Contains(NoSubgroup)) subs.Add(NoSubgroup);
        var result = new List<PowerSubgroupInfo>();
        foreach (var sub in subs)
        {
            var subCopy = sub;
            List<Guid> settings;
            try { settings = Enumerate((i, buf) => { uint size = 16; return EnumSettings(IntPtr.Zero, ref scheme, ref subCopy, AccessIndividualSetting, i, buf, ref size); }); }
            catch (Exception ex) { Log.Warn($"Power: settings of subgroup {sub} couldn't be listed: {ex.Message}"); continue; }
            if (settings.Count == 0) continue;
            var subName = sub == NoSubgroup ? Txt.PowerNoSubgroup : EnglishText(RegString($@"{SettingsKey}\{sub:D}", "FriendlyName")) ?? Txt.SubgroupTechnicalName(sub);
            var group = new PowerSubgroupInfo { Id = sub, Name = subName, Hidden = SubgroupHidden(sub) };
            foreach (var setting in settings) group.Settings.Add(ReadSetting(scheme, sub, setting));
            group.Settings.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            result.Add(group);
        }
        result.Sort((a, b) => a.Id == NoSubgroup ? 1 : b.Id == NoSubgroup ? -1 : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    public static PowerSettingInfo ReadSetting(Guid scheme, Guid sub, Guid setting)
    {
        var meta = Metadata(sub, setting);
        uint? ac = null, dc = null;
        string? error = null;
        try { ac = ReadValue(scheme, sub, setting, true); dc = ReadValue(scheme, sub, setting, false); }
        catch (Exception ex) { error = ex.Message; }
        return new PowerSettingInfo
        {
            Subgroup = meta.Subgroup, Setting = meta.Setting, Name = meta.Name, Description = meta.Description, Hidden = meta.Hidden,
            Kind = meta.Kind, Choices = meta.Choices, Min = meta.Min, Max = meta.Max, Increment = meta.Increment, Units = meta.Units,
            ReadOnlyReason = meta.ReadOnlyReason, TechnicalName = meta.TechnicalName, Ac = ac, Dc = dc, ReadError = error,
            StoredAc = IsStored(scheme, sub, setting, true), StoredDc = IsStored(scheme, sub, setting, false),
        };
    }

    /// <summary>Metadata (name, type, range or options) without the plan's values.</summary>
    public static PowerSettingInfo Metadata(Guid sub, Guid setting)
    {
        var key = SettingKey(sub, setting);
        var english = EnglishText(RegString(key, "FriendlyName"));
        var hidden = (SettingAttributes(ref sub, ref setting) & 1) != 0;

        var choices = new List<PowerChoice>();
        // Windows stores each option under the setting's "<index>" subkey. For range settings PowrProf
        // reports success for any index (seen on "Turn off hard disk after", maximum 0xFFFFFFFF), so the loop
        // stops when the subkey doesn't exist, and the limit prevents an endless loop.
        for (uint i = 0; i < MaxChoices && RegKeyExists($@"{key}\{i}"); i++)
        {
            var buf = new byte[4];
            uint size = 4;
            if (PowerReadPossibleValue(IntPtr.Zero, ref sub, ref setting, out var type, i, buf, ref size) != 0) break;
            if (type != 4 /*REG_DWORD*/ || size != 4) { choices.Clear(); break; }
            var value = BitConverter.ToUInt32(buf);
            var label = EnglishText(RegString($@"{key}\{i}", "FriendlyName")) ?? Txt.ChoiceTechnicalName(value);
            choices.Add(new PowerChoice(value, label));
        }

        PowerValueKind kind;
        uint min = 0, max = 0, inc = 0;
        string? readOnly = null;
        if (choices.Count > 0) kind = PowerValueKind.Choice;
        else if (PowerReadValueMin(IntPtr.Zero, ref sub, ref setting, out min) == 0
                 && PowerReadValueMax(IntPtr.Zero, ref sub, ref setting, out max) == 0 && max >= min)
        {
            kind = PowerValueKind.Range;
            if (PowerReadValueIncrement(IntPtr.Zero, ref sub, ref setting, out inc) != 0) inc = 1;
        }
        else { kind = PowerValueKind.ReadOnly; readOnly = Txt.PowerNoMetadata; }

        return new PowerSettingInfo
        {
            Subgroup = sub, Setting = setting,
            Name = english ?? Txt.SettingTechnicalName(setting),
            TechnicalName = english is null,
            Description = EnglishText(RegString(key, "Description")) ?? "",
            Hidden = hidden, Kind = kind, Choices = choices, Min = min, Max = max, Increment = inc,
            Units = EnglishText(RegString(key, "ValueUnits")) ?? "",
            ReadOnlyReason = readOnly,
        };
    }

    public static uint ReadValue(Guid scheme, Guid sub, Guid setting, bool ac)
    {
        var err = ac ? PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var v)
                     : PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out v);
        if (err != 0) throw new InvalidOperationException($"PowerRead{(ac ? "AC" : "DC")}ValueIndex failed ({err}).");
        return v;
    }

    /// <summary>Value read without throwing (comparison between plans); null when the read failed.</summary>
    public static uint? TryReadValue(Guid scheme, Guid sub, Guid setting, bool ac)
    {
        try { return ReadValue(scheme, sub, setting, ac); }
        catch { return null; }
    }

    /// <summary>
    /// Null when the metadata accepts the value; otherwise the reason. Assumes no meanings.
    /// <paramref name="custom"/>: value typed for a list setting, accepted outside the list (any 32-bit value).
    /// </summary>
    public static string? Validate(PowerSettingInfo meta, uint value, bool custom = false) => meta.Kind switch
    {
        PowerValueKind.Choice when custom => null,
        PowerValueKind.Choice =>meta.Choices.Any(c => c.Value == value) ? null : Txt.PowerNotAChoice(value),
        PowerValueKind.Range when value < meta.Min || value > meta.Max => Txt.PowerOutOfRange(meta.Min, meta.Max, meta.Units),
        PowerValueKind.Range when meta.Increment > 1 && (value - meta.Min) % meta.Increment != 0 => Txt.PowerNotAStep(meta.Increment, meta.Min),
        PowerValueKind.Range => null,
        _ => meta.ReadOnlyReason ?? Txt.PowerNoMetadata,
    };

    /// <summary>
    /// Writes values to the plan. If permission is missing and the toolbox isn't running as administrator, asks for a single UAC prompt for all of them.
    /// When the plan is the active one, re-applies it (like "powercfg /setactive") so it takes effect immediately. Doesn't activate another plan.
    /// </summary>
    public static async Task WriteAsync(IReadOnlyList<PowerWrite> writes, CancellationToken ct = default)
    {
        if (writes.Count == 0) return;
        foreach (var w in writes)
            if (Validate(Metadata(w.Subgroup, w.Setting), w.Value, w.Custom) is { } problem) throw new InvalidOperationException(problem);
        try { await Task.Run(() => Apply(writes), ct).ConfigureAwait(false); }
        catch (UnauthorizedAccessException) when (!Elevation.IsAdmin)
        {
            Log.Info("Power: access denied writing the plan; asking for elevation.");
            await ElevatedRunner.RunAsync(new ElevatedJob { Power = writes.ToList() }, ct).ConfigureAwait(false);
        }
    }

    internal static void Apply(IEnumerable<PowerWrite> writes)
    {
        var active = ActiveScheme();
        var touchedActive = false;
        foreach (var w in writes)
        {
            var scheme = w.Scheme; var sub = w.Subgroup; var setting = w.Setting;
            var err = w.Ac ? PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, w.Value)
                           : PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, w.Value);
            if (err == ErrorAccessDenied) throw new UnauthorizedAccessException(Txt.PowerAccessDenied);
            if (err != 0) throw new InvalidOperationException($"PowerWrite{(w.Ac ? "AC" : "DC")}ValueIndex failed ({err}) for {w.Setting}.");
            Log.Info($"Power: plan {w.Scheme} setting {w.Subgroup}\\{w.Setting} {(w.Ac ? "AC" : "DC")} = {w.Value}");
            touchedActive |= w.Scheme == active;
        }
        if (touchedActive) Activate(active);
    }

    /// <summary>Elevated-side validation: existing plan and values accepted by PowrProf's metadata.</summary>
    internal static void Validate(IReadOnlyList<PowerWrite> writes)
    {
        if (writes.Count == 0) throw new InvalidOperationException("No power settings in the request.");
        var plans = List().Select(p => p.Id).ToHashSet();
        foreach (var w in writes)
        {
            if (!plans.Contains(w.Scheme)) throw new InvalidOperationException($"Unknown power plan: {w.Scheme}");
            if (Validate(Metadata(w.Subgroup, w.Setting), w.Value, w.Custom) is { } problem) throw new InvalidOperationException($"{w.Setting}: {problem}");
        }
    }

    // ---------- Typed values ----------

    /// <summary>Decimal number, or hexadecimal with "0x", as an unsigned 32-bit value.</summary>
    public static bool TryParseValue(string? text, out uint value)
    {
        var t = (text ?? "").Trim();
        return t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(t[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value) && t.Length > 2
            : uint.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    // ---------- Plan and setting operations ----------

    public const int MaxNameLength = 128, MaxDescriptionLength = 512;

    // A leading "@" makes Windows (and EnglishText) treat the text as a reference to a MUI resource.
    public static string? NameProblem(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Trim() != name || name.Length > MaxNameLength || name.Any(char.IsControl) || name.StartsWith('@')
            ? Txt.PowerNameInvalid(MaxNameLength) : null;

    public static string? DescriptionProblem(string? text) =>
        text is null || text.Trim() != text || text.Length > MaxDescriptionLength || text.Any(char.IsControl) || text.StartsWith('@')
            ? Txt.PowerDescriptionInvalid(MaxDescriptionLength) : null;

    /// <summary>Network (UNC) or device path (\\?\, \\.\), refused in the elevated process.</summary>
    internal static bool IsNetworkOrDevicePath(string path) => path.Length > 1 && path[0] is '\\' or '/' && path[1] is '\\' or '/';

    /// <summary>Full path of a .pow file; on import the file must exist, on export the folder must.</summary>
    public static string? PowFileProblem(string? path, bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0 || !System.IO.Path.IsPathFullyQualified(path)
            || !string.Equals(System.IO.Path.GetExtension(path), ".pow", StringComparison.OrdinalIgnoreCase))
            return Txt.PowerPowPathInvalid;
        if (mustExist ? !File.Exists(path) : !Directory.Exists(System.IO.Path.GetDirectoryName(path))) return Txt.PowerPowPathMissing(path);
        return null;
    }

    /// <summary>Shape of the request, without reading the system: only the fields the operation uses, each valid.</summary>
    internal static void CheckShape(PowerPlanOp op)
    {
        var o = op.Op;
        if (o is not (PowerPlanOp.Duplicate or PowerPlanOp.Rename or PowerPlanOp.Delete or PowerPlanOp.Import or PowerPlanOp.Export
            or PowerPlanOp.RestoreDefaults or PowerPlanOp.RemoveValue or PowerPlanOp.SetHidden))
            throw new InvalidOperationException($"Unknown power plan operation: {o}");
        void Require(bool ok, string what) { if (!ok) throw new InvalidOperationException($"Invalid power plan request ({o}): {what}."); }
        var setting = o is PowerPlanOp.RemoveValue or PowerPlanOp.SetHidden;
        Require((op.Scheme != Guid.Empty) == (o is not (PowerPlanOp.Import or PowerPlanOp.RestoreDefaults or PowerPlanOp.SetHidden)), "plan");
        Require((op.NewScheme != Guid.Empty) == (o is PowerPlanOp.Duplicate or PowerPlanOp.Import), "new plan id");
        Require(o switch
        {
            PowerPlanOp.Duplicate => NameProblem(op.Name) is null,
            PowerPlanOp.Rename => op.Name is null || NameProblem(op.Name) is null,
            _ => op.Name is null,
        }, "name");
        Require(o is PowerPlanOp.Rename ? op.Description is null || DescriptionProblem(op.Description) is null : op.Description is null, "description");
        Require(o != PowerPlanOp.Rename || op.Name is not null || op.Description is not null, "nothing to change");
        Require(o is PowerPlanOp.Import or PowerPlanOp.Export ? PowFileProblem(op.Path, mustExist: o == PowerPlanOp.Import) is null : op.Path is null, "path");
        Require(setting ? op.Setting != Guid.Empty && op.Subgroup != Guid.Empty : op.Setting == Guid.Empty && op.Subgroup == Guid.Empty, "setting");
        Require(o == PowerPlanOp.RemoveValue || !op.Ac, "power source");
        Require(o == PowerPlanOp.SetHidden || !op.Hidden, "hidden flag");
    }

    /// <summary>Elevated-side validation: shape, existing plans, active plan not deleted, existing setting and value.</summary>
    internal static void Validate(PowerPlanOp op)
    {
        CheckShape(op);
        var plans = List();
        void Require(bool ok, string what) { if (!ok) throw new InvalidOperationException($"Invalid power plan request ({op.Op}): {what}."); }
        // Export never asks for UAC (powercfg doesn't need administrator); as administrator it would write a .pow anywhere.
        Require(op.Op != PowerPlanOp.Export, "export doesn't need elevation");
        Require(op.Path is null || !IsNetworkOrDevicePath(op.Path), "network or device path");
        if (op.Scheme != Guid.Empty) Require(plans.Any(p => p.Id == op.Scheme), "unknown plan");
        if (op.NewScheme != Guid.Empty) Require(plans.All(p => p.Id != op.NewScheme), "the new plan id is in use");
        if (op.Op == PowerPlanOp.Delete) Require(plans.Any(p => p.Id == op.Scheme && !p.IsActive), "the active plan can't be deleted");
        if (op.Setting != Guid.Empty) Require(RegKeyExists(SettingKey(op.Subgroup, op.Setting)), "unknown setting");
        if (op.Op == PowerPlanOp.RemoveValue) Require(IsStored(op.Scheme, op.Subgroup, op.Setting, op.Ac), "the plan has no own value");
    }

    /// <summary>Runs in the current process; without permission (and not as administrator) asks for a UAC prompt with the same request.</summary>
    public static async Task RunAsync(PowerPlanOp op, CancellationToken ct = default)
    {
        CheckShape(op);
        try { await Task.Run(() => Run(op), ct).ConfigureAwait(false); }
        catch (UnauthorizedAccessException) when (!Elevation.IsAdmin)
        {
            Log.Info($"Power: access denied ({op.Op}); asking for elevation.");
            await ElevatedRunner.RunAsync(new ElevatedJob { PowerPlan = op }, ct).ConfigureAwait(false);
        }
    }

    internal static void Run(PowerPlanOp op)
    {
        var scheme = op.Scheme; var sub = op.Subgroup; var setting = op.Setting;
        switch (op.Op)
        {
            case PowerPlanOp.Duplicate:
                WithNewScheme(op.NewScheme, (ref IntPtr dest) => PowerDuplicateScheme(IntPtr.Zero, ref scheme, ref dest), "PowerDuplicateScheme");
                // The copy already exists: a failure to name it must not ask for UAC and repeat the copy.
                try { WriteText(op.NewScheme, op.Name!, PowerWriteFriendlyName, "PowerWriteFriendlyName"); }
                catch (UnauthorizedAccessException ex) { throw new InvalidOperationException(ex.Message); }
                break;
            case PowerPlanOp.Rename:
                // Null field = no change: rewriting it would replace the MUI text of Windows plans with fixed text.
                if (op.Name is not null) WriteText(scheme, op.Name, PowerWriteFriendlyName, "PowerWriteFriendlyName");
                if (op.Description is not null) WriteText(scheme, op.Description, PowerWriteDescription, "PowerWriteDescription");
                break;
            case PowerPlanOp.Delete:
                if (scheme == ActiveScheme()) throw new InvalidOperationException(Txt.PowerDeleteActive);
                Check(PowerDeleteScheme(IntPtr.Zero, ref scheme), "PowerDeleteScheme");
                break;
            case PowerPlanOp.Import:
                WithNewScheme(op.NewScheme, (ref IntPtr dest) => PowerImportPowerScheme(IntPtr.Zero, op.Path!, ref dest), "PowerImportPowerScheme");
                break;
            case PowerPlanOp.Export:
                var r = ProcessRunner.Run(System.IO.Path.Combine(Environment.SystemDirectory, "powercfg.exe"), $"/export {ProcessRunner.Quote(op.Path!)} {scheme:D}");
                if (r.TimedOut || r.ExitCode != 0) throw new InvalidOperationException(Txt.PowerExportFailed(r.Output.Trim()));
                break;
            case PowerPlanOp.RestoreDefaults:
                Check(PowerRestoreDefaultPowerSchemes(), "PowerRestoreDefaultPowerSchemes");
                break;
            case PowerPlanOp.RemoveValue:
                RemoveStored(scheme, sub, setting, op.Ac);
                if (scheme == ActiveScheme()) Activate(scheme); // as after writing: the active plan starts using the default immediately
                break;
            case PowerPlanOp.SetHidden:
                var attrs = OwnAttributes(sub, setting);
                Check(PowerWriteSettingAttributes(ref sub, ref setting, op.Hidden ? attrs | 1u : attrs & ~1u), "PowerWriteSettingAttributes");
                break;
        }
        Log.Info($"Power: {op.Op} done (plan {op.Scheme}, new {op.NewScheme}, setting {op.Subgroup}\\{op.Setting}).");
    }

    /// <summary>Re-read after the operation: confirms the expected result in Windows.</summary>
    public static bool Verify(PowerPlanOp op, DateTime startedUtc)
    {
        try
        {
            var sub = op.Subgroup; var setting = op.Setting;
            return op.Op switch
            {
                PowerPlanOp.Duplicate => List().Any(p => p.Id == op.NewScheme && p.Name == op.Name),
                PowerPlanOp.Import => List().Any(p => p.Id == op.NewScheme),
                PowerPlanOp.Rename => (op.Name is null || List().Any(p => p.Id == op.Scheme && p.Name == op.Name))
                    && (op.Description is null || Description(op.Scheme) == op.Description),
                PowerPlanOp.Delete => List().All(p => p.Id != op.Scheme),
                PowerPlanOp.Export => File.Exists(op.Path) && new FileInfo(op.Path!).Length > 0 && File.GetLastWriteTimeUtc(op.Path!) >= startedUtc.AddSeconds(-2),
                // Only the factory plans remain (the ones with a key under Default\PowerSchemes), and one of them is active.
                PowerPlanOp.RestoreDefaults => List() is var plans && plans.Any(p => p.IsActive) && plans.All(p => RegKeyExists($@"{DefaultSchemesKey}\{p.Id:D}")),
                PowerPlanOp.RemoveValue => !IsStored(op.Scheme, sub, setting, op.Ac),
                PowerPlanOp.SetHidden => ((OwnAttributes(sub, setting) & 1) != 0) == op.Hidden,
                _ => false,
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"Power: verifying {op.Op} failed: {ex.Message}");
            return false;
        }
    }

    delegate uint NewSchemeCall(ref IntPtr destination);

    /// <summary>
    /// Duplicate and import with the destination GUID already chosen: the function uses the GUID pointed to when the pointer is non-null
    /// (it only allocates a new one when it is null).
    /// </summary>
    static void WithNewScheme(Guid id, NewSchemeCall call, string name)
    {
        var mem = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.StructureToPtr(id, mem, false);
            var dest = mem;
            Check(call(ref dest), name);
            if (dest != mem) { LocalFree(dest); throw new InvalidOperationException(Txt.PowerNewIdNotUsed(name)); }
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    delegate uint SchemeTextWriter(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr setting, byte[] buffer, uint size);

    static void WriteText(Guid scheme, string text, SchemeTextWriter write, string name)
    {
        var bytes = Encoding.Unicode.GetBytes(text + "\0");
        Check(write(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, bytes, (uint)bytes.Length), name);
    }

    static void Check(uint err, string call)
    {
        if (err == ErrorAccessDenied) throw new UnauthorizedAccessException(Txt.PowerAccessDenied);
        if (err != 0) throw new InvalidOperationException($"{call} failed ({err}).");
    }

    static string StoredKey(Guid scheme, Guid sub, Guid setting) => $@"{SchemesKey}\{scheme:D}\{sub:D}\{setting:D}";
    static string StoredValueName(bool ac) => ac ? "ACSettingIndex" : "DCSettingIndex";

    /// <summary>The plan stores its own value in the registry; without it PowrProf returns the plan's own default.</summary>
    public static bool IsStored(Guid scheme, Guid sub, Guid setting, bool ac)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(StoredKey(scheme, sub, setting));
            return key?.GetValue(StoredValueName(ac)) is not null;
        }
        catch { return false; }
    }

    static void RemoveStored(Guid scheme, Guid sub, Guid setting, bool ac)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(StoredKey(scheme, sub, setting), writable: true)
                ?? throw new InvalidOperationException(Txt.PowerNoOwnValue(setting));
            key.DeleteValue(StoredValueName(ac), throwOnMissingValue: true);
        }
        catch (SecurityException) { throw new UnauthorizedAccessException(Txt.PowerAccessDenied); }
    }

    /// <summary>
    /// Attributes of the setting itself ("Attributes" value in the registry, 0 when missing). PowerReadSettingAttributes
    /// returns the setting's combined with the subgroup's, which can't be rewritten on the setting.
    /// </summary>
    static uint OwnAttributes(Guid sub, Guid setting)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(SettingKey(sub, setting)) ?? throw new InvalidOperationException(Txt.PowerSettingMissing(setting));
        return key.GetValue("Attributes") is int v ? unchecked((uint)v) : 0;
    }

    /// <summary>The whole subgroup is hidden in Windows power options (hides all of its settings).</summary>
    public static bool SubgroupHidden(Guid sub) => sub != NoSubgroup && (SubgroupAttributes(ref sub, IntPtr.Zero) & 1) != 0;

    static bool RegKeyExists(string path)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(path);
            return key is not null;
        }
        catch { return false; }
    }
}
