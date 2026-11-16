namespace KosToolbox.Core.Text;

public static partial class Txt
{
    public const string PageDebloat = "Debloat", PageDebloatSubtitle = "Telemetry, ads and extras of the installed apps, read from each app on every visit.";
    public const string DebloatIntro = "Ticking a box runs that function right away; unticking undoes it, when it can be undone. App updates can bring things back: the boxes are read from the app every time. Hover a function to see what it does.";
    public const string DebloatPresets = "Presets", DebloatFunctions = "Functions";
    public const string DebloatNoApps = "None of the supported apps is installed: Discord, Spotify, Chrome, Brave, Firefox, LibreWolf, Thorium or OBS Studio.";
    public const string DebloatApplying = "Applying…", DebloatUndoing = "Undoing…";
    public const string DebloatDone = "Done.", DebloatConfirmed = "Applied and confirmed.", DebloatUndone = "Undone and confirmed.";
    public const string DebloatNotConfirmed = "The change ran, but reading it back doesn't show it applied.";
    public const string DebloatClosesApp = "Closes the app first.";
    public static string DebloatHeader(string name, string? version) => string.IsNullOrWhiteSpace(version) ? name : $"{name} · {version}";
    public static string DebloatNotFound(string name) => $"{name} wasn't found.";
    public const string DebloatOpenUnelevated = "This option changes your own user's files, so it doesn't run as administrator. Close the toolbox and open it normally.";
    public static string DebloatStillOpen(string name) => $"{name} is still open. Close it and try again.";
    public static string DebloatIsLink(string path) => $"{path} is a link; it was left alone.";

    public const string DebloatLocales = "Keep only the English language", DebloatLocalesInfo = "Deletes the other language files from the app.";
    public const string DebloatAutostart = "Don't start with Windows";

    // ---------- Discord ----------
    public const string DiscordRpc = "Remove Rich Presence (RPC)", DiscordRpcInfo = "Deletes the discord_rpc module: games stop showing up as your activity.";
    public const string DiscordKrisp = "Remove noise suppression (Krisp)", DiscordKrispInfo = "Deletes the discord_krisp module.";
    public const string DiscordOverlay = "Remove the in-game overlay", DiscordOverlayInfo = "Deletes the discord_overlay2, discord_desktop_overlay and discord_hook modules.";
    public const string DiscordModules = "Remove other optional modules", DiscordModulesInfo = "Deletes cloud sync, dispatch, erlpack, game utils, media and spellcheck.";
    public const string DiscordFiles = "Remove unused files", DiscordFilesInfo = "Deletes the update packages and logs, the game SDK, the crash reporter DLL and the Xbox network file.";
    public const string DiscordCache = "Clear caches and logs", DiscordCacheInfo = "Deletes logs, crash reports and the caches in %AppData%. Discord rebuilds them.";
    public const string DiscordAutostartInfo = "Removes the startup entry and turns off Open on startup in Discord's settings.";
    public const string DiscordHwAccel = "Turn off hardware acceleration", DiscordHwAccelInfo = "Sets the option in settings.json; the other settings stay.";
    public const string DiscordOpenAsar = "Install OpenAsar", DiscordOpenAsarInfo = "Replaces Discord's app.asar with OpenAsar (latest nightly from its GitHub), with its self-update off: running this again gets the newest. Undo brings the original back.";
    public const string DiscordVencord = "Install Vencord", DiscordVencordInfo = "Runs Vencord's own installer from its latest GitHub release.";
    public const string DiscordShortcut = "Desktop shortcut without the updater", DiscordShortcutInfo = "Creates a shortcut straight to Discord.exe, so opening it doesn't run the updater. It stops working when Discord updates to a new folder.";
    public const string DiscordStartup = "Debloat at startup, hidden", DiscordStartupInfo = "At every sign-in, with no window: removes what a Discord update brought back (optional modules, unused files, caches, other languages, plus RPC, Krisp or the overlay if you removed them) and opens Discord minimized to the tray.";
    public const string DiscordStartupNeedsProgramFiles = "This option runs the toolbox at every sign-in, so it needs the toolbox installed in Program Files.";
    public const string DiscordPresetNoKrisp = "Debloat, remove noise suppression", DiscordPresetNoKrispInfo = "Optional modules with Krisp, unused files, caches, autostart, English only, OpenAsar, hardware acceleration off and the desktop shortcut.";
    public const string DiscordPresetKeepKrisp = "Debloat, keep noise suppression", DiscordPresetKeepKrispInfo = "Optional modules, unused files, caches, autostart, English only, OpenAsar, hardware acceleration off and the desktop shortcut. Krisp stays.";
    public const string DiscordClientModWarning ="Modified Discord clients are against Discord's terms of service and a Discord update can undo them.";
    public const string DiscordOpenAsarInvalid = "The downloaded file isn't OpenAsar; Discord wasn't changed.";
    public const string DiscordNoStockAsar = "The original app.asar wasn't saved here. Reinstall Discord to get it back.";
    public static string DiscordSettingsUnreadable(string path, string error) => $"{path} can't be read, so it wasn't changed: {error}";
    public static string DiscordVencordFailed(int code, string output) => $"Vencord's installer ended with code {code}: {output.Trim()}";

    // ---------- Spotify ----------
    public const string SpotifyUpdates = "Block updates", SpotifyUpdatesInfo = "Empties the folder where Spotify downloads updates and denies access to it.";
    public const string SpotifyUpdatesWarning = "Spotify won't update anymore, including security fixes, until you undo this.";
    public const string SpotifyAutostartInfo = "Removes Spotify's startup entry.";
    public const string SpotifyHelpers = "Remove the migrator and startup task", SpotifyHelpersInfo = "Deletes SpotifyMigrator.exe and SpotifyStartupTask.exe.";
    public const string SpotifyRuntime = "Remove unused runtime files", SpotifyRuntimeInfo = "Deletes the crash reporter, the Direct3D and Vulkan runtime files and the extra resource packs.";

    // ---------- browsers ----------
    public const string BrowserUpdater = "Remove the updater", BrowserUpdaterInfo = "Deletes the update services, scheduled tasks and folders.";
    public const string BrowserUserUpdate = "Remove the update folder of your user", BrowserUserUpdateInfo = @"Deletes %LocalAppData%\BraveSoftware\Update.";
    public const string BrowserFiles = "Remove unused files", BrowserFilesInfo = "Deletes crash reporters, helpers and runtime files the browser doesn't need. A browser update brings them back.";
    public const string BrowserNoUpdatesWarning = "The browser no longer updates by itself, including security fixes. Reinstall it to update.";
    public static string PolicyDescription(string info, int count) => $"{info} {count} {(count == 1 ? "policy" : "policies")}; takes effect when the browser restarts.";
    public const string PolicyAi = "Turn off AI features", PolicyAiInfo = "Generative AI, writing help, Lens and AI chat.";
    public const string PolicyTelemetry = "Turn off telemetry and reporting", PolicyTelemetryInfo = "Usage reports, surveys, field trials and reporting to the cloud.";
    public const string PolicyAds = "Turn off ads, promotions and extras", PolicyAdsInfo = "Privacy Sandbox, promotions, shopping, cast and the browser's own extras.";
    public const string PolicyPerformance = "Background and network behavior", PolicyPerformanceInfo = "No running in the background or prediction; sets efficiency modes and DNS.";
    public const string PolicyStartup = "Search, home page and startup", PolicyStartupInfo = "Sets Google as the search engine and home page, and forces safe search.";
    public const string PolicyPermissions = "Site permission defaults", PolicyPermissionsInfo = "Location, notifications, USB, Bluetooth and similar.";
    public const string PolicyExtensions = "Extensions", PolicyExtensionsInfo = "Allows Manifest V2 and, in Chrome, force-installs uBlock Origin (with its settings), Return YouTube Dislike, Ruffle, I don't care about cookies, and the store ids dcmllfkiihingappljlkffafnlhdpbai and oobhedhmcdceknhbgjaeikeoaphaajlf. Force-installed extensions can't be removed by hand; Undo removes them.";
    public const string PolicyUpdate = "Update policies", PolicyUpdateInfo = "Limits Google Update and moves Chrome to the extended channel.";
    public const string PolicySecurity = "Turn off security protections", PolicySecurityInfo = "Safe Browsing, password leak checks, download restrictions, HTTPS upgrades and sandbox hardening.";
    public const string PolicySecurityWarning = "The browser no longer warns about dangerous sites and downloads, and its sandbox is weaker.";
    public const string PolicyOther = "Other browser behavior", PolicyOtherInfo = "Guest mode, printing, spellcheck, the address bar and compatibility switches.";

    // ---------- OBS ----------
    public const string ObsProfileTitle = "Create a profile", ObsProfileInfo = "Streaming and recording profile with replay buffer on F9. It becomes the active profile; the others stay. Closes OBS first.";
    public const string ObsResolution = "Resolution", ObsFps = "FPS", ObsEncoderLabel = "Encoder", ObsCreateProfile = "Create profile", ObsCustomFps = "Custom";
    public const string ObsEncoderNvenc = "NVIDIA NVENC", ObsEncoderAmf = "AMD AMF", ObsEncoderQsv = "Intel Quick Sync";
    public const string ObsInvalidProfile = "That resolution or frame rate isn't valid. FPS must be a whole number from 1 to 1000.";
    public static string ObsActiveProfile(string? name) => name is null ? "No active profile found." : $"Active profile: {name}";
    public static string ObsProfileCreated(string name) => $"Profile {name} created and active.";
    public const string ObsPrefs = "Turn off auto-update and audio ducking", ObsPrefsInfo = "Sets both in OBS's global.ini.";
    public const string ObsScene = "Create a scene collection with a mic noise gate", ObsSceneInfo = "New collection named KOS with desktop audio and the mic with a noise gate. It becomes the active one; other collections stay.";
    public const string ObsTheme = "Fluent Dark (Grey) theme", ObsThemeInfo = "Downloads the theme from the OBS forum into your OBS themes folder and selects it. Unticking goes back to OBS's default theme.";
    public const string ObsThemeInvalid = "The download doesn't contain the theme; OBS wasn't changed.";
    public const string ObsReplay ="Start with Windows with the replay buffer", ObsReplayInfo = "OBS opens minimized to the tray with the replay buffer on.";
}
