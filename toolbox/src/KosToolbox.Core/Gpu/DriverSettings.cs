using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

namespace KosToolbox.Core.Gpu;

/// <summary>A named value of a driver setting.</summary>
public sealed record DrsOption(uint Value, string Label);

/// <summary>
/// A driver setting as NVIDIA Profile Inspector shows it: name, group, description and known values.
/// Curated = is in the Profile Inspector list; Hidden = only exists in the reference (not documented by the driver).
/// </summary>
public sealed record DrsMeta(uint Id, string Name, string Group, string Description, IReadOnlyList<DrsOption> Options,
    uint? Default, bool Curated, bool Hidden, bool IsString);

/// <summary>Session opened on the driver's profile database (NVAPI DRS).</summary>
public sealed class DrsSession : IDisposable
{
    internal DrsSession(IntPtr handle) => Handle = handle;
    internal IntPtr Handle { get; private set; }
    public void Dispose() { Nvapi.DrsClose(Handle); Handle = IntPtr.Zero; }
}

/// <summary>Driver profile (the global one or a game/app one).</summary>
public sealed record DrsProfile(IntPtr Handle, string Name, bool Predefined, bool IsGlobal)
{
    public override string ToString() => IsGlobal ? "Global driver profile" : Name;
}

/// <summary>
/// Settings of the NVIDIA driver (DRS profiles), in the style of NVIDIA Profile Inspector. The names, groups, descriptions and
/// values come from the Profile Inspector files that KOS installs (the curated list embedded in the exe and Reference.xml) and
/// from the driver itself; reading and writing go through NVAPI.
/// </summary>
public static class DriverSettings
{
    public static DrsSession Open() => new(Nvapi.DrsOpen());

    public static List<DrsProfile> Profiles(DrsSession s)
    {
        var global = Nvapi.DrsGlobalProfile(s.Handle);
        var globalName = Nvapi.DrsProfileName(s.Handle, global);
        var list = Nvapi.DrsProfiles(s.Handle).Where(p => p.Name != globalName)
            .Select(p => new DrsProfile(p.Handle, p.Name, p.Predefined, false)).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        list.Insert(0, new DrsProfile(global, globalName, true, true));
        return list;
    }

    /// <summary>Settings stored in the profile itself (Location 0).</summary>
    public static Dictionary<uint, DrsStored> Stored(DrsSession s, DrsProfile p) =>
        Nvapi.DrsEnum(s.Handle, p.Handle).GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());

    /// <summary>Writes to the session (takes effect on Save); says whether the driver refused (it lacks the setting, or it is not valid in this profile).</summary>
    public static DrsWrite Set(DrsSession s, DrsProfile p, uint id, bool isString, uint dword, string? text) =>
        Nvapi.DrsSet(s.Handle, p.Handle, id, isString ? DrsType.WString : DrsType.Dword, dword, text);

    /// <summary>
    /// What the installed driver refuses when writing to the global profile: NotInDriver (NVAPI_SETTING_NOT_FOUND: features of
    /// other cards, such as RTX HDR, or that this driver version/installation lacks) and NotOnGlobal (NVAPI_NOT_SUPPORTED:
    /// only valid on game profiles, such as Ansel). Profile Inspector writes the same way and gets the same errors.
    /// Tested in a separate session, discarded without saving: nothing reaches the driver.
    /// </summary>
    public static (HashSet<uint> NotInDriver, HashSet<uint> NotOnGlobal) Unsupported(IEnumerable<DrsMeta> catalog)
    {
        using var scratch = Open();
        var global = Nvapi.DrsGlobalProfile(scratch.Handle);
        var (notInDriver, notOnGlobal) = (new HashSet<uint>(), new HashSet<uint>());
        foreach (var m in catalog)
        {
            try
            {
                var r = Nvapi.DrsSet(scratch.Handle, global, m.Id, m.IsString ? DrsType.WString : DrsType.Dword, m.Default ?? 0, "");
                if (r == DrsWrite.NotInDriver) notInDriver.Add(m.Id);
                else if (r == DrsWrite.NotOnThisProfile) notOnGlobal.Add(m.Id);
            }
            catch (InvalidOperationException) { /* another error: cannot tell, stays editable */ }
        }
        return (notInDriver, notOnGlobal);
    }

    public static void RestoreDefault(DrsSession s, DrsProfile p, uint id) => Nvapi.DrsRestoreDefault(s.Handle, p.Handle, id);

    /// <summary>
    /// The profile the driver uses for this program: the one that already names its file (a game profile of the driver, or one made
    /// before), else a new profile named after the file with the program in it, saved. Created = it is new.
    /// </summary>
    public static (string Profile, bool Created) ProfileForProgram(DrsSession s, string exePath)
    {
        var file = Path.GetFileName(exePath);
        if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose a program (.exe).", nameof(exePath));
        if (Nvapi.DrsFindApplication(s.Handle, file) is { } found) return (found.ProfileName, false);
        Nvapi.DrsCreateProfileFor(s.Handle, file, file);
        Nvapi.DrsSave(s.Handle);
        return (file, true);
    }

    public static List<string> Programs(DrsSession s, DrsProfile p) => p.IsGlobal ? new() : Nvapi.DrsApplications(s.Handle, p.Handle);

    /// <summary>Deletes a profile made by the user (never one of the driver's own) and saves.</summary>
    public static void DeleteProfile(DrsSession s, DrsProfile p)
    {
        if (p.IsGlobal || p.Predefined) throw new InvalidOperationException("Only profiles made by the user can be deleted.");
        Nvapi.DrsDeleteProfileHandle(s.Handle, p.Handle);
        Nvapi.DrsSave(s.Handle);
    }
    public static void Save(DrsSession s) => Nvapi.DrsSave(s.Handle);
    public static void RestoreAll(DrsSession s) => Nvapi.DrsRestoreAll(s.Handle);

    // ---------- catalog ----------

    sealed record Raw(uint Id, string? Name, string? Group, string? Description, List<DrsOption> Options, uint? Default, bool Hidden, bool IsString);

    /// <summary>
    /// Catalog: curated Profile Inspector list + Reference.xml + settings documented by the driver. Without
    /// Profile Inspector, only what the driver documents (its names and values).
    /// </summary>
    public static List<DrsMeta> Catalog(string profileInspectorExe)
    {
        var curated = File.Exists(profileInspectorExe) ? Parse(EmbeddedXml(profileInspectorExe, "CustomSettingNames.xml")) : new();
        var referencePath = Path.Combine(Path.GetDirectoryName(profileInspectorExe) ?? "", "Reference.xml");
        var reference = File.Exists(referencePath) ? Parse(XDocument.Load(referencePath)) : new();
        var driverIds = new HashSet<uint>();
        try { driverIds.UnionWith(Nvapi.DrsAvailableIds()); } catch { /* driver without the list: what the files have remains */ }

        var ids = curated.Keys.Union(driverIds).Union(reference.Keys);
        var list = new List<DrsMeta>();
        foreach (var id in ids)
        {
            curated.TryGetValue(id, out var c);
            reference.TryGetValue(id, out var r);
            var inDriver = driverIds.Contains(id);
            var name = c?.Name ?? r?.Name ?? (inDriver ? Nvapi.DrsSettingName(id) : null) ?? $"0x{id:X8}";
            var options = c?.Options is { Count: > 0 } co ? co : r?.Options is { Count: > 0 } ro ? ro : new List<DrsOption>();
            uint? def = c?.Default ?? r?.Default;
            if (options.Count == 0 && inDriver && Nvapi.DrsAvailableValues(id) is { } dv)
            {
                options = dv.Values.Select(v => new DrsOption(v, $"0x{v:X8}")).ToList();
                def ??= dv.Default;
            }
            var hidden = c is null && (!inDriver || r?.Hidden == true);
            list.Add(new DrsMeta(id, name, c?.Group ?? r?.Group ?? "Other", Clean(c?.Description ?? r?.Description ?? ""), options, def,
                c is not null, hidden, c?.IsString == true || r?.IsString == true));
        }
        return list.OrderBy(m => m.Group, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static string Clean(string description) =>
        description.Replace("${DlssVersion}", "installed version").Replace("\\r\\n", " ").Replace("\r\n", " ").Replace('\n', ' ').Trim();

    static uint? Hex(string? s) =>
        s is not null && s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && uint.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;

    static Dictionary<uint, Raw> Parse(XDocument? doc)
    {
        var map = new Dictionary<uint, Raw>();
        if (doc is null) return map;
        foreach (var s in doc.Descendants("CustomSetting"))
        {
            if (Hex((string?)s.Element("HexSettingID")) is not { } id) continue;
            var options = s.Element("SettingValues")?.Elements("CustomSettingValue")
                .Select(v => Hex((string?)v.Element("HexValue")) is { } value ? new DrsOption(value, ((string?)v.Element("UserfriendlyName") ?? "").Trim()) : null)
                .OfType<DrsOption>().ToList() ?? new();
            var fallback = (string?)s.Element("FallbackType");
            map[id] = new Raw(id, ((string?)s.Element("UserfriendlyName"))?.Trim(), (string?)s.Element("GroupName"), (string?)s.Element("Description"),
                options, Hex((string?)s.Element("OverrideDefault")), string.Equals((string?)s.Element("Hidden"), "true", StringComparison.OrdinalIgnoreCase),
                string.Equals(fallback, "STRING", StringComparison.OrdinalIgnoreCase));
        }
        return map;
    }

    /// <summary>XML embedded in the Profile Inspector exe (.NET), read as data (nothing from the exe is executed).</summary>
    static XDocument? EmbeddedXml(string exe, string suffix)
    {
        try
        {
            using var fs = File.OpenRead(exe);
            using var pe = new PEReader(fs);
            if (pe.PEHeaders.CorHeader is not { } cor) return null;
            var md = pe.GetMetadataReader();
            var block = pe.GetSectionData(cor.ResourcesDirectory.RelativeVirtualAddress);
            foreach (var h in md.ManifestResources)
            {
                var r = md.GetManifestResource(h);
                if (!md.GetString(r.Name).EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || !r.Implementation.IsNil) continue;
                var reader = block.GetReader((int)r.Offset, block.Length - (int)r.Offset);
                var length = reader.ReadInt32();
                using var ms = new MemoryStream(reader.ReadBytes(length));
                return XDocument.Load(ms);
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or System.Xml.XmlException or InvalidOperationException) { }
        return null;
    }

    // ---------- .nip (Profile Inspector format) ----------

    public sealed record NipSetting(uint Id, string Name, bool IsString, uint Dword, string? Text);

    public static void ExportNip(string path, string profileName, IEnumerable<NipSetting> settings)
    {
        var doc = new XDocument(new XDeclaration("1.0", "utf-16", null),
            new XElement("ArrayOfProfile",
                new XElement("Profile",
                    new XElement("ProfileName", profileName),
                    new XElement("Executeables"),
                    new XElement("Settings", settings.Select(s => new XElement("ProfileSetting",
                        new XElement("SettingNameInfo", s.Name),
                        new XElement("SettingID", s.Id),
                        new XElement("SettingValue", s.IsString ? s.Text ?? "" : s.Dword.ToString(CultureInfo.InvariantCulture)),
                        new XElement("ValueType", s.IsString ? "String" : "Dword")))))));
        using var writer = new StreamWriter(path, false, System.Text.Encoding.Unicode);
        doc.Save(writer);
    }

    /// <summary>
    /// Imports a .nip like Profile Inspector's "Replace", without asking: first saves each affected profile to
    /// <paramref name="backupDir"/> (.nip), resets its settings to NVIDIA's default and writes the file's.
    /// Returns how many were written and the ones the driver refused.
    /// </summary>
    public static (int Applied, List<string> Refused, List<string> Backups) ReplaceFromNip(string path, string backupDir)
    {
        var nip = ImportNip(path);
        var refused = new List<string>();
        var backups = new List<string>();
        var applied = 0;
        using var s = Open();
        var profiles = Profiles(s);
        foreach (var (name, settings) in nip)
        {
            var p = profiles.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"The driver has no profile named \"{name}\".");
            var stored = Stored(s, p);
            Directory.CreateDirectory(backupDir);
            var backup = Path.Combine(backupDir, $"{string.Concat(name.Split(Path.GetInvalidFileNameChars()))}-{DateTime.Now:yyyyMMdd-HHmmss}.nip");
            ExportNip(backup, p.Name, stored.Values.Where(v => v.Type is DrsType.Dword or DrsType.String or DrsType.WString)
                .Select(v => new NipSetting(v.Id, Nvapi.DrsSettingName(v.Id) ?? $"0x{v.Id:X8}", v.Type != DrsType.Dword, v.Dword, v.Text)));
            backups.Add(backup);
            foreach (var id in stored.Keys) RestoreDefault(s, p, id);
            foreach (var x in settings)
                if (Set(s, p, x.Id, x.IsString, x.Dword, x.Text) == DrsWrite.Ok) applied++;
                else refused.Add(x.Name.Length > 0 ? x.Name : $"0x{x.Id:X8}");
        }
        Save(s);
        return (applied, refused, backups);
    }

    /// <summary>Profiles of a .nip: name and settings (DWORD and text; binary ones are ignored).</summary>
    public static List<(string Profile, List<NipSetting> Settings)> ImportNip(string path)
    {
        var doc = XDocument.Load(path);
        return doc.Descendants("Profile").Select(p => ((string?)p.Element("ProfileName") ?? "",
            p.Descendants("ProfileSetting").Select(s =>
            {
                var type = (string?)s.Element("ValueType") ?? "Dword";
                if (!uint.TryParse((string?)s.Element("SettingID"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return null;
                var value = (string?)s.Element("SettingValue") ?? "";
                if (type.Equals("Dword", StringComparison.OrdinalIgnoreCase))
                    return uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dw) ? new NipSetting(id, (string?)s.Element("SettingNameInfo") ?? "", false, dw, null) : null;
                return type.Contains("String", StringComparison.OrdinalIgnoreCase) ? new NipSetting(id, (string?)s.Element("SettingNameInfo") ?? "", true, 0, value) : null;
            }).OfType<NipSetting>().ToList())).ToList();
    }
}
