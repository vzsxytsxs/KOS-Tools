using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Interop;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Setup;
using KosToolbox.Core.Text;
using Microsoft.Win32;

namespace KosToolbox.Core.Debloat;

/// <summary>Discord channel: per-user install in %LocalAppData% and settings in %AppData%.</summary>
public sealed record DiscordInstall(string Id, string Name, string RunValue, string Root, string Data, string Desktop);

/// <summary>Discord's debloat functions, each on its own. Discord's updater brings removed modules back with an update: the option reads as not applied again.</summary>
public static class DiscordDebloat
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string OpenAsarUrl = "https://github.com/GooseMod/OpenAsar/releases/download/nightly/app.asar";
    const string VencordCliUrl = "https://github.com/Vencord/Installer/releases/latest/download/VencordInstallerCli.exe";
    static readonly string[] OptionalModules = { "discord_cloudsync", "discord_dispatch", "discord_erlpack", "discord_game_utils", "discord_media", "discord_spellcheck" };
    static readonly string[] UnusedFiles = { "debug.log", "Microsoft.Gaming.XboxApp.XboxNetwork.winmd", "discord_wer.dll", "discord_game_sdk_x64.dll", "discord_game_sdk_x86.dll" };

    internal static readonly string[] PresetKeepKrisp = { "modules", "files", "cache", "autostart", "locales", "openasar", "hwaccel", "shortcut" };
    internal static readonly string[] PresetNoKrisp = PresetKeepKrisp.Append("krisp").ToArray();

    /// <summary>Argument of the startup entry: the toolbox runs the pass below instead of opening its window.</summary>
    public const string LogonArgument = "--discord-debloat";
    /// <summary>What a Discord update brings back, removed at every sign-in; autostart too, as an update can restore Discord's own entry.</summary>
    static readonly string[] LogonAlways = { "modules", "files", "cache", "locales", "autostart" };
    /// <summary>Removed at sign-in only if the user removed them: keeping Krisp or the overlay is a choice.</summary>
    static readonly string[] LogonIfRemoved = { "rpc", "krisp", "overlay" };
    static readonly TimeSpan LogonDelay = TimeSpan.FromSeconds(15);
    // The script's flags without --no-sandbox and --disable-web-security (Discord shows content other people send) and --disable-updater (nothing in the client reads it).
    const string LogonFlags = "--start-minimized --disable-animations --disable-extensions --disable-logging --disable-software-rasterizer --enable-gpu-rasterization --enable-zero-copy --ignore-gpu-blocklist";

    static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public static DiscordInstall Stable => new("discord", "Discord", "Discord", Path.Combine(Local, "Discord"), Path.Combine(Roaming, "discord"), Desktop);
    public static DiscordInstall Ptb => new("discord-ptb", "Discord PTB", "Discord PTB", Path.Combine(Local, "DiscordPTB"), Path.Combine(Roaming, "discordptb"), Desktop);
    public static DiscordInstall Canary => new("discord-canary", "Discord Canary", "Discord Canary", Path.Combine(Local, "DiscordCanary"), Path.Combine(Roaming, "discordcanary"), Desktop);
    public static DiscordInstall Development => new("discord-development", "Discord Development", "Discord Development", Path.Combine(Local, "DiscordDevelopment"), Path.Combine(Roaming, "discorddevelopment"), Desktop);
    /// <summary>The official channels; client mods (Vesktop, Equibop, Legcord) aren't debloated.</summary>
    public static DiscordInstall[] Channels => new[] { Stable, Ptb, Canary, Development };

    public static DebloatApp App(DiscordInstall d)
    {
        var exeName = Path.GetFileName(d.Root) + ".exe";
        return new DebloatApp
        {
            Id = d.Id, Name = d.Name, Icon = "discord", Processes = new[] { Path.GetFileNameWithoutExtension(exeName) },
            Executable = () => Latest(d, exeName) is { } app ? Path.Combine(app, exeName) : null,
            Options = Options(d, exeName),
        };
    }

    /// <summary>The presets first, then each function on its own.</summary>
    internal static IReadOnlyList<DebloatOption> Options(DiscordInstall d, string exeName)
    {
        var functions = Functions(d, exeName);
        DebloatOption Preset(string id, string name, string info, IEnumerable<string> ids) =>
            DebloatCatalog.Preset(id, name, info, ids.Select(i => functions.Single(f => f.Id == i)).ToList());
        return new[]
        {
            Preset("preset-no-krisp", Txt.DiscordPresetNoKrisp, Txt.DiscordPresetNoKrispInfo, PresetNoKrisp),
            Preset("preset-keep-krisp", Txt.DiscordPresetKeepKrisp, Txt.DiscordPresetKeepKrispInfo, PresetKeepKrisp),
        }.Concat(functions).ToList();
    }

    static IReadOnlyList<DebloatOption> Functions(DiscordInstall d, string exeName) => new[]
    {
        Modules(d, exeName, "rpc", Txt.DiscordRpc, Txt.DiscordRpcInfo, "discord_rpc"),
        Modules(d, exeName, "krisp", Txt.DiscordKrisp, Txt.DiscordKrispInfo, "discord_krisp"),
        Modules(d, exeName, "overlay", Txt.DiscordOverlay, Txt.DiscordOverlayInfo, "discord_overlay2", "discord_desktop_overlay", "discord_hook"),
        Modules(d, exeName, "modules", Txt.DiscordModules, Txt.DiscordModulesInfo, OptionalModules),
        new DebloatOption
        {
            Id = "locales", Name = Txt.DebloatLocales, Description = Txt.DebloatLocalesInfo, ClosesApp = true,
            Read = () => DebloatFiles.StateOfRemoval(ExtraLocales(d).Count()),
            Apply = _ => Sync(() => DebloatFiles.Delete(ExtraLocales(d))),
        },
        new DebloatOption
        {
            Id = "files", Name = Txt.DiscordFiles, Description = Txt.DiscordFilesInfo, ClosesApp = true,
            Read = () => DebloatFiles.StateOfRemoval(Unused(d).Count()),
            Apply = _ => Sync(() => DebloatFiles.Delete(Unused(d))),
        },
        new DebloatOption
        {
            Id = "cache", Name = Txt.DiscordCache, Description = Txt.DiscordCacheInfo, ClosesApp = true,
            Apply = _ => Sync(() => DebloatFiles.Delete(Cache(d))),
        },
        new DebloatOption
        {
            Id = "autostart", Name = Txt.DebloatAutostart, Description = Txt.DiscordAutostartInfo,
            Read = () => HasRunValue(d.RunValue) || Setting(d, "OPEN_ON_STARTUP") is not false ? DebloatState.NotApplied : DebloatState.Applied,
            Apply = _ => Sync(() =>
            {
                using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)) run?.DeleteValue(d.RunValue, throwOnMissingValue: false);
                WriteSettings(d, s => s["OPEN_ON_STARTUP"] = false);
            }),
        },
        new DebloatOption
        {
            Id = "hwaccel", Name = Txt.DiscordHwAccel, Description = Txt.DiscordHwAccelInfo, ClosesApp = true,
            Read = () => Setting(d, "enableHardwareAcceleration") is false ? DebloatState.Applied : DebloatState.NotApplied,
            Apply = _ => Sync(() => WriteSettings(d, s => s["enableHardwareAcceleration"] = false)),
            Undo = _ => Sync(() => WriteSettings(d, s => s["enableHardwareAcceleration"] = true)),
        },
        new DebloatOption
        {
            Id = "openasar", Name = Txt.DiscordOpenAsar, Description = Txt.DiscordOpenAsarInfo, ClosesApp = true, Warning = Txt.DiscordClientModWarning,
            Read = () => Asar(d, exeName) is { } asar && IsOpenAsar(asar) ? DebloatState.Applied : DebloatState.NotApplied,
            Apply = ct => InstallOpenAsarAsync(d, exeName, ct),
            Undo = _ => Sync(() => RestoreAsar(d, exeName)),
        },
        new DebloatOption
        {
            Id = "vencord", Name = Txt.DiscordVencord, Description = Txt.DiscordVencordInfo, ClosesApp = true, Warning = Txt.DiscordClientModWarning,
            Read = () => Latest(d, exeName) is { } app && File.Exists(Path.Combine(app, "resources", "_app.asar")) ? DebloatState.Applied : DebloatState.NotApplied,
            Apply = ct => VencordAsync(d, "-install", ct),
            Undo = ct => VencordAsync(d, "-uninstall", ct),
        },
        new DebloatOption
        {
            Id = "shortcut", Name = Txt.DiscordShortcut, Description = Txt.DiscordShortcutInfo,
            Read = () => ShortcutTarget(d) is { } t && Latest(d, exeName) is { } app && t.Equals(Path.Combine(app, exeName), StringComparison.OrdinalIgnoreCase) ? DebloatState.Applied : DebloatState.NotApplied,
            Apply = _ => Sync(() =>
            {
                var app = Latest(d, exeName) ?? throw new InvalidOperationException(Txt.DebloatNotFound(d.Name));
                var exe = Path.Combine(app, exeName);
                ShellLink.Create(ShortcutPath(d), exe, app, exe);
            }),
            Undo = _ => Sync(() => File.Delete(ShortcutPath(d))),
        },
        new DebloatOption
        {
            Id = "startup", Name = Txt.DiscordStartup, Description = Txt.DiscordStartupInfo,
            Read = () => RunValue(StartupValue(d)) is not { } value ? DebloatState.NotApplied : IsOwnEntry(value) ? DebloatState.Applied : DebloatState.Partial,
            Apply = _ => Sync(() =>
            {
                // With UAC off this runs as administrator at every sign-in, like the GPU's sign-in task.
                if (!Elevation.InProtectedFolder) throw new InvalidOperationException(Txt.DiscordStartupNeedsProgramFiles);
                WriteStartup(d, exeName);
            }),
            Undo = _ => Sync(() =>
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                run?.DeleteValue(StartupValue(d), throwOnMissingValue: false);
            }),
        },
    };

    static Task Sync(Action action) { action(); return Task.CompletedTask; }

    /// <summary>Newest app-* folder that really has the executable (several versions stay installed side by side).</summary>
    internal static string? Latest(DiscordInstall d, string exeName) =>
        AppFolders(d).Where(a => File.Exists(Path.Combine(a, exeName)))
            .OrderByDescending(a => Version.TryParse(Path.GetFileName(a)[4..], out var v) ? v : new Version(0, 0))
            .ThenByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault();

    static IEnumerable<string> AppFolders(DiscordInstall d) => DebloatFiles.Folders(d.Root, "app-*");

    // ---------- modules ----------

    static DebloatOption Modules(DiscordInstall d, string exeName, string id, string name, string info, params string[] modules) => new()
    {
        Id = id, Name = name, Description = info, ClosesApp = true,
        Read = () => DebloatFiles.StateOfRemoval(ModuleFolders(d, modules).Count()),
        Apply = _ => Sync(() =>
        {
            DebloatFiles.Delete(ModuleFolders(d, modules));
            // Removed after the startup pass was turned on: it now removes this one too. Added, never read back from disk:
            // after an update a module the user removed is there again until the next pass.
            if (LogonIfRemoved.Contains(id) && RunValue(StartupValue(d)) is { } entry && IsOwnEntry(entry)) SetStartup(d, ListedRemoved(entry).Append(id).Distinct());
        }),
    };

    /// <summary>modules\discord_x-1 in every installed version.</summary>
    internal static IEnumerable<string> ModuleFolders(DiscordInstall d, params string[] modules) =>
        from app in AppFolders(d)
        from module in modules
        from folder in DebloatFiles.Folders(Path.Combine(app, "modules"), module + "*")
        let name = Path.GetFileName(folder)
        where name.Equals(module, StringComparison.OrdinalIgnoreCase) || name.StartsWith(module + "-", StringComparison.OrdinalIgnoreCase)
        select folder;

    internal static IEnumerable<string> ExtraLocales(DiscordInstall d) =>
        AppFolders(d).SelectMany(app => DebloatFiles.Files(Path.Combine(app, "locales"), "*.pak"))
            .Where(f => !Path.GetFileName(f).Equals("en-US.pak", StringComparison.OrdinalIgnoreCase));

    internal static IEnumerable<string> Unused(DiscordInstall d)
    {
        if (!DebloatFiles.IsFolder(d.Root)) yield break;
        foreach (var f in new[] { "Discord_updater_rCURRENT.log", "SquirrelSetup.log" }.Select(n => Path.Combine(d.Root, n)).Where(File.Exists)) yield return f;
        foreach (var f in new[] { "download", "Packages" }.Select(n => Path.Combine(d.Root, n)).Where(DebloatFiles.IsFolder)) yield return f;
        foreach (var app in AppFolders(d))
            foreach (var name in UnusedFiles)
                foreach (var f in DebloatFiles.Files(app, name)) yield return f;
    }

    internal static IEnumerable<string> Cache(DiscordInstall d)
    {
        if (!DebloatFiles.IsFolder(d.Data)) yield break;
        foreach (var folder in new[] { "logs", "crash_reports", "Cache", @"Cache\Cache_Data", @"Code Cache\js", @"Code Cache\wasm", "GPUCache", "Sessions" })
        {
            // Files only checks the last part of the path: a link at "Code Cache" would send the delete elsewhere.
            if (DebloatFiles.IsLink(Path.Combine(d.Data, folder.Split('\\')[0]))) continue;
            foreach (var f in DebloatFiles.Files(Path.Combine(d.Data, folder), "*")) yield return f;
        }
    }

    // ---------- settings.json ----------

    static bool HasRunValue(string name) => RunValue(name) is not null;

    static string? RunValue(string name)
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        return run?.GetValue(name) as string;
    }

    static string SettingsPath(DiscordInstall d) => Path.Combine(d.Data, "settings.json");

    /// <summary>Null when the file or the value doesn't exist or can't be read.</summary>
    internal static bool? Setting(DiscordInstall d, string name)
    {
        try { return File.Exists(SettingsPath(d)) && JsonNode.Parse(File.ReadAllText(SettingsPath(d)))?[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Keeps every other setting. An unreadable file is refused instead of being replaced (it holds the user's settings).</summary>
    internal static void WriteSettings(DiscordInstall d, Action<JsonObject> change)
    {
        Directory.CreateDirectory(d.Data);
        var path = SettingsPath(d);
        JsonObject settings;
        try { settings = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new JsonException("not an object") : new JsonObject(); }
        catch (JsonException ex) { throw new InvalidOperationException(Txt.DiscordSettingsUnreadable(path, ex.Message)); }
        change(settings);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    // ---------- OpenAsar ----------

    /// <summary>With Vencord installed, Discord's real asar is _app.asar (app.asar is Vencord's loader).</summary>
    internal static string? Asar(DiscordInstall d, string exeName)
    {
        if (Latest(d, exeName) is not { } app) return null;
        var patched = Path.Combine(app, "resources", "_app.asar");
        return File.Exists(patched) ? patched : Path.Combine(app, "resources", "app.asar");
    }

    /// <summary>OpenAsar's asar is small and holds its code as plain text; Discord's is many megabytes.</summary>
    internal static bool IsOpenAsar(string asar)
    {
        try { return new FileInfo(asar) is { Exists: true, Length: < 5_000_000 } && Encoding.Latin1.GetString(File.ReadAllBytes(asar)).Contains("OpenAsar", StringComparison.Ordinal); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    static async Task InstallOpenAsarAsync(DiscordInstall d, string exeName, CancellationToken ct)
    {
        var asar = Asar(d, exeName) ?? throw new InvalidOperationException(Txt.DebloatNotFound(d.Name));
        var bytes = await Downloader.Http.GetByteArrayAsync(OpenAsarUrl, ct);
        var tmp = asar + ".new";
        await File.WriteAllBytesAsync(tmp, bytes, ct);
        if (!IsOpenAsar(tmp)) { File.Delete(tmp); throw new InvalidOperationException(Txt.DiscordOpenAsarInvalid); }
        // The original comes back with Undo.
        if (File.Exists(asar) && !IsOpenAsar(asar)) File.Copy(asar, asar + ".stock", overwrite: true);
        File.Move(tmp, asar, overwrite: true);
        WriteSettings(d, s =>
        {
            // Merged: the block also holds the user's own OpenAsar settings (custom CSS).
            if (s["openasar"] is not JsonObject openAsar) s["openasar"] = openAsar = new JsonObject();
            openAsar["setup"] = true;
            openAsar["quickstart"] = true;
            openAsar["cmdPreset"] = "battery";
            // Otherwise it replaces itself from the moving nightly tag at every start, with no signature, as Discord's main process.
            openAsar["autoupdate"] = false;
        });
    }

    static void RestoreAsar(DiscordInstall d, string exeName)
    {
        var asar = Asar(d, exeName) ?? throw new InvalidOperationException(Txt.DebloatNotFound(d.Name));
        if (!File.Exists(asar + ".stock")) throw new InvalidOperationException(Txt.DiscordNoStockAsar);
        File.Move(asar + ".stock", asar, overwrite: true);
    }

    // ---------- Vencord ----------

    /// <summary>Vencord's own installer in command-line mode. It has no signature; it comes from the project's latest release.</summary>
    static async Task VencordAsync(DiscordInstall d, string action, CancellationToken ct)
    {
        var (cli, _) = await Downloader.DownloadAsync(VencordCliUrl, "VencordInstallerCli.exe", Downloader.Folder, new Progress<DownloadProgress>(), ct);
        try
        {
            // -location, not -branch: the installer's -branch knows stable, ptb and canary only.
            var r = await ProcessRunner.RunAsync(cli, $"{action} -location \"{d.Root}\"", ct: ct);
            if (r.ExitCode != 0) throw new InvalidOperationException(Txt.DiscordVencordFailed(r.ExitCode, r.Output));
        }
        finally { File.Delete(cli); }
    }

    // ---------- shortcut ----------

    static string ShortcutPath(DiscordInstall d) => Path.Combine(d.Desktop, $"{d.Name} Debloated.lnk");

    static string? ShortcutTarget(DiscordInstall d)
    {
        try { return File.Exists(ShortcutPath(d)) ? ShellLink.Read(ShortcutPath(d)).Target : null; }
        catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException) { return null; }
    }

    // ---------- pass at Windows startup ----------

    static string StartupValue(DiscordInstall d) => $"KOS {d.RunValue} debloat";

    internal static string StartupCommand(string exe, DiscordInstall d, IEnumerable<string> removed)
    {
        var list = string.Join(',', removed);
        return $"\"{exe}\" {LogonArgument} {d.Id}" + (list.Length > 0 ? " " + list : "");
    }

    static bool IsOwnEntry(string command) => command.StartsWith($"\"{Environment.ProcessPath}\"", StringComparison.OrdinalIgnoreCase);

    /// <summary>The last word of the command, when it is the list of removed modules.</summary>
    internal static IEnumerable<string> ListedRemoved(string command) => command.Split(' ')[^1].Split(',').Where(LogonIfRemoved.Contains);

    /// <summary>
    /// A startup entry of the user, not a task: everything it touches is the user's, so it needs no elevation of its own. The modules the
    /// user chose to remove go in the command, because after an update nothing on disk says they were removed. Before Discord's first
    /// start no module is on disk, which would read as all of them removed.
    /// </summary>
    internal static void WriteStartup(DiscordInstall d, string exeName)
    {
        var functions = Functions(d, exeName);
        var removed = ModuleFolders(d, "discord_desktop_core").Any()
            ? LogonIfRemoved.Where(id => functions.Single(f => f.Id == id).Read() == DebloatState.Applied)
            : Enumerable.Empty<string>();
        SetStartup(d, RunValue(StartupValue(d)) is { } old ? removed.Union(ListedRemoved(old)) : removed);
    }

    static void SetStartup(DiscordInstall d, IEnumerable<string> removed)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("The toolbox executable path is unknown.");
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        run.SetValue(StartupValue(d), StartupCommand(exe, d, removed));
    }

    /// <summary>
    /// The toolbox started hidden by the startup entry: no window, no WPF. Removes what a Discord update brought back and opens Discord
    /// minimized to the tray. Discord opens even if the cleaning fails, since this entry replaces its own startup entry.
    /// </summary>
    public static async Task<int> RunAtLogonAsync(string id, string removed)
    {
        if (Channels.FirstOrDefault(c => c.Id == id) is not { } d)
        {
            Log.Error($"Discord startup pass: unknown channel {id}.");
            return ElevatedJob.ExitRefused;
        }
        // First, and it ends the run: Discord's folder is the user's, so starting it from a toolbox elevated by UAC would run whatever is there as administrator.
        try { DebloatCatalog.RefuseElevatedUserChange(); }
        catch (InvalidOperationException ex)
        {
            Log.Error($"Discord startup pass: {ex.Message}");
            return ElevatedJob.ExitRefused;
        }
        await Task.Delay(LogonDelay);
        var exeName = Path.GetFileName(d.Root) + ".exe";
        // Uninstalled since the entry was made: nothing to clean, and the settings folder shouldn't come back.
        if (Latest(d, exeName) is not { } app)
        {
            Log.Warn($"Discord startup pass: {d.Name} wasn't found.");
            return ElevatedJob.ExitFailed;
        }
        try
        {
            await DebloatCatalog.CloseAsync(App(d), CancellationToken.None);
            await ApplyLogonPassAsync(d, exeName, removed);
            Log.Info($"Discord startup pass: {d.Id} cleaned.");
        }
        catch (Exception ex) { Log.Error($"Discord startup pass: cleaning {d.Id} failed", ex); }
        // No window to show an error in: a failure only goes to the log.
        try { Process.Start(new ProcessStartInfo(Path.Combine(app, exeName), LogonFlags) { UseShellExecute = false, WorkingDirectory = app })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error($"Discord startup pass: opening {d.Id} failed", ex);
            return ElevatedJob.ExitFailed;
        }
        return ElevatedJob.ExitOk;
    }

    /// <summary>The functions of that pass; a name in <paramref name="removed"/> outside the list that can be repeated is ignored.</summary>
    internal static async Task ApplyLogonPassAsync(DiscordInstall d, string exeName, string removed)
    {
        var functions = Functions(d, exeName);
        foreach (var id in LogonAlways.Concat(removed.Split(',').Where(LogonIfRemoved.Contains)))
            await functions.Single(f => f.Id == id).Apply(CancellationToken.None);
        // An update moves Discord to a new folder: the direct shortcut, if there is one, follows it.
        if (File.Exists(ShortcutPath(d))) await functions.Single(f => f.Id == "shortcut").Apply(CancellationToken.None);
    }
}
