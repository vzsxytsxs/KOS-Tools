using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KosGovernor.Core;

/// <summary>
/// config.json in %ProgramData%\TheKOS\Governor. The engine applies the rules as administrator (including "Terminate" and
/// gamemode programs), so the folder only accepts writes from administrators: it is created protected, and an existing folder
/// that is a link or is not owned by SYSTEM/Administrators is refused, without rewriting the ACL.
/// </summary>
public static class ConfigStore
{
    public static JsonSerializerOptions Json => GovernorJson.Default.Options;

    public static string Root { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TheKOS");
    public static string Directory => Path.Combine(Root, "Governor");
    public static string FilePath => Path.Combine(Directory, "config.json");

    /// <summary>No file: default configuration. Unreadable file or suspicious folder: throws with the reason.</summary>
    public static GovernorConfig Load()
    {
        if (FolderProblem() is { } problem) throw new InvalidOperationException(problem);
        if (!File.Exists(FilePath)) return new GovernorConfig();
        if (OwnerProblem(FilePath) is { } owner) throw new InvalidOperationException(owner);
        var config = JsonSerializer.Deserialize(File.ReadAllText(FilePath), GovernorJson.Default.GovernorConfig) ?? new GovernorConfig();
        if (config.Profiles.Count == 0) config.Profiles.Add(new Profile());
        return Migrate(config);
    }

    /// <summary>
    /// Files from earlier versions: v2 changed the engine's default interval from 2 s to 3 s (a chosen value stays); v3 adds
    /// the KOS default rules for programs that have no rule yet.
    /// </summary>
    internal static GovernorConfig Migrate(GovernorConfig config)
    {
        if (config.Version < 2)
        {
            if (config.General.EngineIntervalMs == 2000) config.General.EngineIntervalMs = 3000;
            config.Version = 2;
        }
        if (config.Version < 3)
        {
            foreach (var p in config.Profiles) DefaultRules.AddMissing(p);
            config.Version = 3;
        }
        return config;
    }

    public static DateTime Stamp => File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;

    /// <summary>Validates everything, writes atomically and notifies the engine.</summary>
    public static void Save(GovernorConfig config)
    {
        if (Validation.Problems(config, CpuTopology.Current).FirstOrDefault() is { } problem) throw new InvalidOperationException(problem);
        PrepareDirectory();
        var tmp = FilePath + ".tmp";
        File.Delete(tmp); // an old .tmp could be a hard link to another file
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, GovernorJson.Default.GovernorConfig));
        File.Move(tmp, FilePath, overwrite: true);
        Names.Signal(Names.Reload);
    }

    internal static void PrepareDirectory()
    {
        System.IO.Directory.CreateDirectory(Root);
        if (FolderProblem() is { } problem) throw new InvalidOperationException(problem);
        if (System.IO.Directory.Exists(Directory)) return;
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var acl = new DirectorySecurity();
        acl.SetOwner(admins);
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(Directory).Create(acl);
        if (FolderProblem() is { } after) throw new InvalidOperationException(after);
    }

    /// <summary>Null if the root and the folder are not links and the folder (when it exists) belongs to SYSTEM or Administrators.</summary>
    public static string? FolderProblem()
    {
        foreach (var p in new[] { Root, Directory })
            if (System.IO.Directory.Exists(p) && new DirectoryInfo(p).Attributes.HasFlag(FileAttributes.ReparsePoint))
                return $"{p} is a link; the governor won't use it.";
        return System.IO.Directory.Exists(Directory) ? OwnerProblem(Directory) : null;
    }

    internal static string? OwnerProblem(string path)
    {
        try
        {
            var owner = (System.IO.Directory.Exists(path) ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl())
                .GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner is not null && (owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))) return null;
            return $"{path} isn't owned by SYSTEM or Administrators ({owner?.Value ?? "unknown"}); the governor won't use it.";
        }
        catch (Exception ex) { return $"The owner of {path} can't be read: {ex.Message}"; }
    }

    // ---------- exported profile ----------

    public sealed class ProfileFile
    {
        public Profile Profile { get; set; } = new();
        /// <summary>The masks the profile's rules use.</summary>
        public List<CpuMask> Masks { get; set; } = new();
    }

    public static string Export(GovernorConfig config, Profile profile)
    {
        var used = Validation.AllRules(profile).SelectMany(r => new[] { r.Affinity, r.CpuSets })
            .Where(s => s?.StartsWith(CpuSpec.MaskPrefix, StringComparison.Ordinal) == true).Select(s => s![CpuSpec.MaskPrefix.Length..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return JsonSerializer.Serialize(new ProfileFile { Profile = profile, Masks = config.Masks.Where(m => used.Contains(m.Name)).ToList() }, GovernorJson.Default.ProfileFile);
    }

    /// <summary>Reads an exported profile and merges it into the configuration (name made unique; missing masks are created). Validates before touching anything.</summary>
    public static Profile Import(GovernorConfig config, string json)
    {
        var file = JsonSerializer.Deserialize(json, GovernorJson.Default.ProfileFile) ?? throw new InvalidOperationException("The file is empty.");
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(config, GovernorJson.Default.GovernorConfig), GovernorJson.Default.GovernorConfig)!;
        foreach (var m in file.Masks)
        {
            var existing = copy.Masks.FirstOrDefault(x => x.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is null) copy.Masks.Add(m);
            else if (existing.Cpus != m.Cpus) throw new InvalidOperationException($"The file has a mask \"{m.Name}\" with other processors ({m.Cpus}) than yours ({existing.Cpus}). Rename one first.");
        }
        var profile = file.Profile;
        var baseName = string.IsNullOrWhiteSpace(profile.Name) ? "Imported" : profile.Name.Trim();
        profile.Name = baseName;
        for (var i = 2; copy.Profiles.Any(p => p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase)); i++) profile.Name = $"{baseName} ({i})";
        copy.Profiles.Add(profile);
        if (Validation.Problems(copy, CpuTopology.Current).FirstOrDefault() is { } problem) throw new InvalidOperationException(problem);
        config.Masks = copy.Masks;
        config.Profiles.Add(profile);
        return profile;
    }

    // ---------- old toolbox rules ----------

    sealed class ToolboxRule
    {
        public string Exe { get; set; } = "";
        public string? Priority { get; set; }
        public string? Cpus { get; set; }
    }

    /// <summary>Rules from the toolbox's Processes page (%LocalAppData%\KOS\Toolbox\process-rules.json), for the first launch.</summary>
    public static List<ProcessRule> FromToolbox(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            return (JsonSerializer.Deserialize<List<ToolboxRule>>(File.ReadAllText(path)) ?? new())
                .Where(r => r.Exe.Length > 0)
                .Select(r => new ProcessRule
                {
                    Exe = r.Exe,
                    Priority = Enum.TryParse<CpuPriority>(r.Priority, out var p) ? p : null,
                    Affinity = string.IsNullOrWhiteSpace(r.Cpus) ? null : r.Cpus,
                })
                .Where(r => !r.IsEmpty).ToList();
        }
        catch (Exception ex) { Log.Warn($"Toolbox rules in {path} weren't imported: {ex.Message}"); return new(); }
    }
}
