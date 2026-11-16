using System.Text.Json;
using System.Runtime.Intrinsics.X86;
using System.Text.RegularExpressions;
using KosToolbox.Core.Apps;

namespace KosToolbox.Core.Setup;

/// <summary>
/// A program on the Setup page. <see cref="Resolve"/> returns the link to the current installer (always from the vendor or from
/// what the vendor publishes); <see cref="Detect"/> recognizes the installation by its name in "Programs and Features".
/// </summary>
public sealed record SetupApp(string Id, string Name, string Category, string Description)
{
    public Func<HttpClient, CancellationToken, Task<string>>? Resolve { get; init; }
    /// <summary>Newest released version (GitHub release or winget manifest), for Install › Updates; null = the source has none to compare.</summary>
    public Func<HttpClient, CancellationToken, Task<string>>? Latest { get; init; }
    /// <summary>Chocolatey package: when set, the program installs through Chocolatey (installed on first use) instead of a download.</summary>
    public string? Choco { get; init; }
    /// <summary>File name when the server doesn't provide one (link without a file name).</summary>
    public string FileName { get; init; } = "setup.exe";
    /// <summary>Case-insensitive expressions for the name in "Programs and Features".</summary>
    public string[] Detect { get; init; } = Array.Empty<string>();
    /// <summary>Installer inside the downloaded .zip (expression over the file name).</summary>
    public string? ZipEntry { get; init; }
    /// <summary>The downloaded file is already the program (no installer).</summary>
    public bool Portable { get; init; }
    /// <summary>Required Authenticode signer (certificate simple name). Portables need one: they sit in the user's Downloads, where any signed file could be dropped.</summary>
    public string? Publisher { get; init; }
    /// <summary>Only exists on the Microsoft Store: the card opens Install › Microsoft Store with this link instead of downloading.</summary>
    public string? StoreUrl { get; init; }
    /// <summary>The developer publishes the installer without a digital signature: the page explains instead of showing an error (opening still asks for confirmation).</summary>
    public bool Unsigned { get; init; }
    /// <summary>Versions chosen in a dialog (each one's Category = group in the dialog); the card itself downloads nothing.</summary>
    public SetupApp[] Variants { get; init; } = Array.Empty<SetupApp>();
    /// <summary>Only one version at a time (e.g. Spotify and SpotX touch the same installation).</summary>
    public bool SingleVariant { get; init; }
    /// <summary>Icon (Assets\Setup\{Icon}.png); default: the Id.</summary>
    public string? Icon { get; init; }
    /// <summary>Installer arguments (.ps1 scripts).</summary>
    public string[] Arguments { get; init; } = Array.Empty<string>();
    /// <summary>File that must also exist to count as installed (environment variables expanded).</summary>
    public string? DetectFile { get; init; }
    /// <summary>Expression over the installed version that must also match (e.g. the old-theme SpotX pins Spotify 1.2.13).</summary>
    public string? DetectVersion { get; init; }
}

public static class SetupCatalog
{
    const string Browsers = "Browsers", Social = "Communication and music", Launchers = "Game launchers", Tools = "Tools", Peripherals = "Peripherals",
        Versions = "Versions", ModClients = "Mod clients", SpotXGroup = "SpotX";

    static Func<HttpClient, CancellationToken, Task<string>> Direct(string url) => (_, _) => Task.FromResult(url);

    static SetupApp DiscordChannel(string channel, string label, string suffix, string? choco = null) =>
        new($"discord-{channel}", $"Discord {label}", Versions, $"Official Discord, {label.ToLowerInvariant()} channel.")
        {
            // The channels with a Chocolatey package install through it; the others download from Discord.
            Resolve = choco is null ? Direct($"https://discord.com/api/downloads/distributions/app/installers/latest?channel={channel}&platform=win&arch=x64") : null,
            FileName = $"Discord{suffix.Trim()}Setup.exe", Detect = new[] { $"^Discord{suffix}$" }, Icon = "discord", Choco = choco,
        };

    /// <summary>
    /// SpotX (github.com/SpotX-Official/SpotX): run.ps1 with the parameters of the official Install_*.bat, plus podcasts and
    /// ad sections off the home. Counts as installed when Spotify has the backup that SpotX creates when applying.
    /// </summary>
    static SetupApp SpotX(string id, string name, string version, params string[] themeArgs) =>
        new(id, name, SpotXGroup, "Spotify patched by SpotX.")
        {
            Resolve = Direct("https://raw.githubusercontent.com/SpotX-Official/SpotX/main/run.ps1"), FileName = "SpotX-run.ps1", Icon = "spotify", Unsigned = true,
            Arguments = themeArgs.Concat(new[] { "-podcasts_off", "-adsections_off", "-no_pause" }).ToArray(),
            Detect = new[] { "^Spotify$" }, DetectFile = @"%APPDATA%\Spotify\Apps\xpui.bak", DetectVersion = version,
        };

    public static IReadOnlyList<SetupApp> Apps { get; } = new SetupApp[]
    {
        new("chrome", "Google Chrome", Browsers, "Google's browser.") { Choco = "googlechrome", Detect = new[] { "^Google Chrome$" } },
        new("firefox", "Mozilla Firefox", Browsers, "Mozilla's browser.") { Choco = "firefox", Detect = new[] { "^Mozilla Firefox" } },
        new("brave", "Brave", Browsers, "Chromium browser with a built-in ad blocker.") { Choco = "brave", Detect = new[] { "^Brave$" } },
        new("edge", "Microsoft Edge", Browsers, "Microsoft's browser.") { Choco = "microsoft-edge", Detect = new[] { "^Microsoft Edge$" } },
        new("thorium", "Thorium", Browsers, "Chromium fork tuned for speed. Picks the build for this CPU (AVX-512, AVX2, AVX, SSE4 or SSE3).") { Resolve = ThoriumUrl, Latest = (h, ct) => GitHubTag(h, "gz83/thorium", ct), FileName = "thorium_mini_installer.exe", Detect = new[] { "^Thorium$" }, Unsigned = true },
        new("librewolf", "LibreWolf", Browsers, "Firefox fork focused on privacy, with uBlock Origin.") { Choco = "librewolf", Detect = new[] { "^LibreWolf" }, Unsigned = true },
        new SetupApp("ungoogled-chromium", "Ungoogled Chromium", Browsers, "Chromium without Google services and background requests.") { FileName = "ungoogled-chromium_installer_x64.exe", Detect = new[] { "^Chromium$", "^Ungoogled" }, Unsigned = true }.GitHub("ungoogled-software/ungoogled-chromium-windows", @"_installer_x64\.exe$"),
        new("chromium", "Chromium", Browsers, "The open-source browser Google Chrome is built on, without Google's branding.") { Choco = "chromium", Detect = new[] { "^Chromium$" }, Icon = "ungoogled-chromium" },

        new("discord", "Discord", Social, "Voice and text chat. Pick the release channels and mod clients to install; they can be installed side by side.")
        {
            Variants = new[]
            {
                DiscordChannel("stable", "Stable", "", "discord"), DiscordChannel("ptb", "PTB", " PTB"), DiscordChannel("canary", "Canary", " Canary", "discord-canary"), DiscordChannel("development", "Development", " Development"),
                new SetupApp("vesktop", "Vesktop", ModClients, "Discord client with Vencord built in.") { FileName = "Vesktop-Setup.exe", Detect = new[] { "^Vesktop" }, Unsigned = true }.GitHub("Vencord/Vesktop", @"^Vesktop-Setup-[\d.]+\.exe$"),
                new SetupApp("equibop", "Equibop", ModClients, "Discord client with Equicord built in.") { FileName = "Equibop-Setup.exe", Detect = new[] { "^Equibop" }, Unsigned = true }.GitHub("Equicord/Equibop", @"^Equibop-Setup-[\d.]+\.exe$"),
                new SetupApp("legcord", "Legcord", ModClients, "Lightweight Discord client.") { FileName = "Legcord-win-x64.exe", Detect = new[] { "^Legcord" }, Unsigned = true }.GitHub("Legcord/Legcord", @"^Legcord-[\d.]+-win-x64\.exe$"),
            },
        },
        new("spotify", "Spotify", Social, "Music streaming. SpotX patches the Spotify client (no ads, podcasts hidden from the home page); pick one.")
        {
            SingleVariant = true,
            Variants = new[]
            {
                new SetupApp("spotify-stable", "Spotify", Versions, "Official client.") { Resolve = Direct("https://download.scdn.co/SpotifySetup.exe"), FileName = "SpotifySetup.exe", Detect = new[] { "^Spotify$" }, Icon = "spotify" },
                SpotX("spotx-new", "SpotX (new UI)", @"^(?!1\.2\.13\.)", "-new_theme"),
                SpotX("spotx-old", "SpotX (old UI)", @"^1\.2\.13\.", "-v", "1.2.13.661.ga588f749", "-confirm_spoti_recomended_over", "-block_update_on"),
            },
        },

        new("steam", "Steam", Launchers, "Valve's store and launcher.") { Choco = "steam", Detect = new[] { "^Steam$" } },
        new("epic", "Epic Games Launcher", Launchers, "Epic Games Store and Unreal games.") { Choco = "epicgameslauncher", Detect = new[] { "^Epic Games Launcher$" } },
        new("rare", "Rare", Launchers, "Open-source Epic Games Store client (uses Legendary), an alternative to the Epic launcher.") { Choco = "rare", Detect = new[] { "^Rare( |$)" }, Unsigned = true },
        new("riot", "Riot Client", Launchers, "League of Legends, VALORANT and the other Riot games. The installer sets up the Riot Client with League of Legends; the other games install from the client.") { Resolve = Direct("https://lol.secure.dyn.riotcdn.net/channels/public/x/installer/current/live.na.exe"), FileName = "Install League of Legends.exe", Detect = new[] { "^Riot Client", "^League of Legends", "^VALORANT" } },
        new("gog", "GOG GALAXY", Launchers, "GOG's launcher.") { Choco = "goggalaxy", Detect = new[] { "^GOG GALAXY" } },
        new("rockstar", "Rockstar Games Launcher", Launchers, "GTA V, RDR2 and other Rockstar games.") { Choco = "rockstar-launcher", Detect = new[] { "^Rockstar Games Launcher" } },
        new("ubisoft", "Ubisoft Connect", Launchers, "Ubisoft's launcher.") { Choco = "ubisoft-connect", Detect = new[] { "^Ubisoft Connect" } },
        new("ea", "EA app", Launchers, "Electronic Arts' launcher.") { Choco = "ea-app", Detect = new[] { "^EA app$" } },
        new("battlenet", "Battle.net", Launchers, "Blizzard's launcher.") { Resolve = Direct("https://downloader.battle.net/download/getInstaller?os=win&installer=Battle.net-Setup.exe"), FileName = "Battle.net-Setup.exe", Detect = new[] { "^Battle\\.net" } },

        new SetupApp("afterburner", "MSI Afterburner", Tools, "GPU overclocking and the RivaTuner on-screen display.") { FileName = "MSIAfterburnerSetup.zip", ZipEntry = @"^MSIAfterburnerSetup.*\.exe$", Detect = new[] { "^MSI Afterburner" } }.Winget("g/Guru3D/Afterburner"),
        new SetupApp("capframex", "CapFrameX", Tools, "Frame-time capture and analysis with an overlay, reports and comparisons. Latency › Frames does a quick check without it.") { FileName = "CapFrameX_installer.zip", ZipEntry = @"^CapFrameXBootstrapper\.exe$", Detect = new[] { "^CapFrameX" } }.GitHub("CXWorld/CapFrameX", @"^release_[\d.]+_installer\.zip$"),
        new SetupApp("frameview", "NVIDIA FrameView", Tools, "NVIDIA's frame-time and power capture with an overlay; works with any GPU.") { FileName = "FrameViewSetup.exe", Detect = new[] { "^(NVIDIA )?FrameView" } }.Winget("n/Nvidia/FrameView"),
        new SetupApp("specialk", "Special K", Tools, "Game modding and frame pacing toolkit.") { FileName = "SpecialK.exe", Detect = new[] { "^Special K" } }.Winget("s/SpecialK/SpecialK"),
        new("libreoffice", "LibreOffice", Tools, "Free office suite (documents, spreadsheets, presentations).") { Choco = "libreoffice-fresh", Detect = new[] { "^LibreOffice" } },
        new("vscode", "Visual Studio Code", Tools, "Microsoft's code editor.") { Choco = "vscode", Detect = new[] { "^Microsoft Visual Studio Code" } },
        new("github-desktop", "GitHub Desktop", Tools, "GitHub's Git client.") { Choco = "github-desktop", Detect = new[] { "^GitHub Desktop" } },
        new("git", "Git", Tools, "Git for Windows (Git Bash included).") { Choco = "git", Detect = new[] { "^Git$", "^Git version" } },
        new("voicemeeter-potato", "Voicemeeter Potato", Tools, "VB-Audio's virtual mixer with 8 inputs.") { Choco = "voicemeeter-potato", Detect = new[] { "^Voicemeeter" } },
        new("ntlite", "NTLite", Tools, "Windows image customization.") { Choco = "ntlite-free", Detect = new[] { "^NTLite" } },
        new("notepadpp", "Notepad++", Tools, "Text and code editor.") { Choco = "notepadplusplus", Detect = new[] { @"^Notepad\+\+" } },
        new("webview2", "Microsoft Edge WebView2 Runtime", Tools, "Shows web content inside apps; many programs, installers and launchers need it. KOS removes Edge, and this brings back only the runtime.") { Choco = "webview2-runtime", Detect = new[] { "^Microsoft Edge WebView2 Runtime$" }, Icon = "edge" },
        new("dotnet", ".NET Desktop Runtime", Tools, "Runs .NET desktop apps (WPF and Windows Forms). Pick the versions your apps ask for; they install side by side.")
        {
            Icon = "dotnet",
            Variants = new[]
            {
                new SetupApp("dotnet-8", ".NET 8 Desktop Runtime", Versions, "For apps built for .NET 8.") { Choco = "dotnet-8.0-desktopruntime", Detect = new[] { @"^Microsoft Windows Desktop Runtime - 8\." }, Icon = "dotnet" },
                new SetupApp("dotnet-9", ".NET 9 Desktop Runtime", Versions, "For apps built for .NET 9.") { Choco = "dotnet-9.0-desktopruntime", Detect = new[] { @"^Microsoft Windows Desktop Runtime - 9\." }, Icon = "dotnet" },
                new SetupApp("dotnet-latest", ".NET Desktop Runtime (latest)", Versions, "The newest release, .NET 10 or later.") { Choco = "dotnet-desktopruntime", Detect = new[] { @"^Microsoft Windows Desktop Runtime - 1\d\." }, Icon = "dotnet" },
            },
        },
        new("anydesk", "AnyDesk", Tools, "Remote desktop: control another PC, or let someone help on this one.") { Choco = "anydesk.install", Detect = new[] { "^AnyDesk" } },
        new SetupApp("pawnio", "PawnIO", Tools, "Signed driver for reading hardware sensors without a vulnerable driver. With it, KOS Governor's gamemode session reports also record the CPU temperature, package power and thermal throttling.") { FileName = "PawnIO_setup.exe", Detect = new[] { "^PawnIO$" }, Publisher = "namazso.eu" }.GitHub("namazso/PawnIO.Setup", @"^PawnIO_setup\.exe$"),
        new("warp", "Cloudflare WARP", Tools, "Cloudflare's free VPN and 1.1.1.1 DNS client.") { Choco = "warp", Detect = new[] { "^Cloudflare WARP" } },

        new("ghub", "Logitech G HUB", Peripherals, "Logitech G mice, keyboards and headsets.") { Choco = "lghub", Detect = new[] { "^Logitech G HUB" } },
        new SetupApp("logitech-omm", "Logitech Onboard Memory Manager", Peripherals, "Saves settings to Logitech mice without G HUB running. Portable: the download is the program.") { FileName = "OnboardMemoryManager.exe", Portable = true, Publisher = "Logitech Inc" }.Winget("l/Logitech/OnboardMemoryManager"),
        new("synapse", "Razer Synapse", Peripherals, "Razer devices (Synapse 4).") { Resolve = Direct("https://rzr.to/synapse-4-pc-download"), FileName = "RazerSynapseInstaller.exe", Detect = new[] { "^Razer Synapse" } },
        new("icue", "Corsair iCUE", Peripherals, "Corsair devices and RGB.") { Choco = "icue", Detect = new[] { "iCUE" } },
        new("ngenuity", "HyperX NGENUITY", Peripherals, "HyperX devices. Only distributed through the Microsoft Store.") { StoreUrl = "https://apps.microsoft.com/detail/9P1TBXR6QDCX" },
    };

    /// <summary>The programs and their versions.</summary>
    public static IEnumerable<SetupApp> All => Apps.SelectMany(a => a.Variants.Prepend(a));

    public static SetupApp? Find(string id) => All.FirstOrDefault(a => a.Id == id);

    /// <summary>The "Programs and Features" entry that matches the program; null if not installed.</summary>
    public static InstalledProgram? Installed(SetupApp app, IEnumerable<InstalledProgram> programs) =>
        app.Detect.Length == 0 || (app.DetectFile is { } f && !File.Exists(Environment.ExpandEnvironmentVariables(f))) ? null
            : programs.FirstOrDefault(p => app.Detect.Any(d => Regex.IsMatch(p.Name, d, RegexOptions.IgnoreCase))
                                           && (app.DetectVersion is null || Regex.IsMatch(p.Version ?? "", app.DetectVersion)));

    // ---------- links that change with each version ----------

    /// <summary>Installer from the repository's latest GitHub release; its tag is the newest version.</summary>
    static SetupApp GitHub(this SetupApp app, string repo, string pattern) =>
        app with { Resolve = (h, ct) => GitHubAsset(h, repo, pattern, ct), Latest = (h, ct) => GitHubTag(h, repo, ct) };

    /// <summary>Installer from the newest winget manifest; its folder name is the newest version.</summary>
    static SetupApp Winget(this SetupApp app, string path) =>
        app with { Resolve = (h, ct) => WingetUrl(h, path, ct), Latest = (h, ct) => WingetVersion(h, path, ct) };

    internal static async Task<string> GitHubTag(HttpClient http, string repo, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest", ct));
        return doc.RootElement.GetProperty("tag_name").GetString() ?? throw new InvalidOperationException($"The latest release of {repo} has no tag.");
    }

    /// <summary>Latest release on GitHub: the first file whose name matches <paramref name="pattern"/>.</summary>
    internal static async Task<string> GitHubAsset(HttpClient http, string repo, string pattern, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest", ct));
        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
            if (Regex.IsMatch(a.GetProperty("name").GetString() ?? "", pattern, RegexOptions.IgnoreCase))
                return a.GetProperty("browser_download_url").GetString()!;
        throw new InvalidOperationException($"The latest release of {repo} has no installer matching {pattern}.");
    }

    /// <summary>
    /// Installer link in the newest winget manifest (public microsoft/winget-pkgs repository), which points to
    /// the vendor's site. Used where the vendor only has links with the version in the name.
    /// </summary>
    internal static async Task<string> WingetUrl(HttpClient http, string path, CancellationToken ct)
    {
        var version = await WingetVersion(http, path, ct);
        var id = string.Join('.', path.Split('/').Skip(1));
        var yaml = await http.GetStringAsync($"https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/{path}/{version}/{id}.installer.yaml", ct);
        return InstallerUrl(yaml) ?? throw new InvalidOperationException($"The winget manifest of {id} {version} has no installer link.");
    }

    static async Task<string> WingetVersion(HttpClient http, string path, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/microsoft/winget-pkgs/contents/manifests/{path}", ct));
        return NewestVersion(doc.RootElement.EnumerateArray().Where(e => e.GetProperty("type").GetString() == "dir").Select(e => e.GetProperty("name").GetString()!))
               ?? throw new InvalidOperationException($"No released version of {path} in winget-pkgs.");
    }

    /// <summary>
    /// <paramref name="available"/> is newer than <paramref name="installed"/>, comparing the numbers both have ("v1.7.2" vs "1.7.1.0",
    /// "131.0.6778.85-1.1" vs "131.0.6778.85" = same): installers often pad or drop the last parts, so only the shared ones count.
    /// </summary>
    public static bool IsNewer(string available, string installed)
    {
        long[] Parts(string v) => Regex.Matches(v, @"\d+").Select(m => long.TryParse(m.Value, out var n) ? n : 0).ToArray();
        var (a, b) = (Parts(available), Parts(installed));
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            if (a[i] != b[i]) return a[i] > b[i];
        return false;
    }

    /// <summary>
    /// Newest version folder, comparing the numbers one by one ("158.0-1" &gt; "99.0.1.2"). Names that don't start with a
    /// number (".validation", "Beta", "M138...") are left out.
    /// </summary>
    internal static string? NewestVersion(IEnumerable<string> names) =>
        names.Where(n => n.Length > 0 && char.IsAsciiDigit(n[0])).MaxBy(n => Regex.Matches(n, @"\d+").Select(m => long.Parse(m.Value)).ToArray(), VersionParts);

    static readonly Comparer<long[]> VersionParts = Comparer<long[]>.Create((a, b) =>
    {
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        return a.Length.CompareTo(b.Length);
    });

    /// <summary>
    /// Thorium for this processor's instruction set: the AVX-512 (x86-64-v4) and AVX2 (x86-64-v3) builds don't
    /// start on CPUs without those instructions.
    /// </summary>
    static Task<string> ThoriumUrl(HttpClient http, CancellationToken ct)
    {
        var build = Avx512F.IsSupported && Avx512F.VL.IsSupported && Avx512BW.IsSupported && Avx512CD.IsSupported && Avx512DQ.IsSupported ? "AVX512"
            : Avx2.IsSupported && Fma.IsSupported && Bmi1.IsSupported && Bmi2.IsSupported && Lzcnt.IsSupported ? "AVX2"
            : Avx.IsSupported ? "AVX"
            : Sse42.IsSupported && Popcnt.IsSupported ? "SSE4" : "SSE3";
        return GitHubAsset(http, "gz83/thorium", $@"^thorium_{build}_mini_installer\.exe$", ct);
    }

    /// <summary>The manifest's InstallerUrl: the x64 one; otherwise the neutral one or the first.</summary>
    internal static string? InstallerUrl(string yaml)
    {
        string? arch = null, first = null;
        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.Trim().TrimStart('-').Trim();
            if (line.StartsWith("Architecture:", StringComparison.Ordinal)) arch = line["Architecture:".Length..].Trim();
            else if (line.StartsWith("InstallerUrl:", StringComparison.Ordinal))
            {
                var url = line["InstallerUrl:".Length..].Trim();
                if (arch is "x64" or "neutral") return url;
                first ??= url;
            }
        }
        return first;
    }
}
