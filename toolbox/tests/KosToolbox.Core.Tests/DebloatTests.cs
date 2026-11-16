using System.IO.Compression;
using System.Text.Json.Nodes;
using KosToolbox.Core.Debloat;
using KosToolbox.Core.Execution;
using Microsoft.Win32;
using Xunit;

namespace KosToolbox.Core.Tests;

public sealed class DebloatTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("kos-debloat-").FullName;
    string At(params string[] parts) => Path.Combine(parts.Prepend(_root).ToArray());

    string Touch(params string[] parts)
    {
        var path = At(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    public void Dispose()
    {
        try { SpotifyDebloat.UnblockUpdates(Spotify); } catch (Exception) { }
        Directory.Delete(_root, recursive: true);
    }

    // ---------- Discord ----------

    DiscordInstall Discord => new("discord", "Discord", "KosTestDiscord", At("local"), At("roaming"), At("desktop"));

    static DebloatOption Option(IEnumerable<DebloatOption> options, string id) => options.Single(o => o.Id == id);

    [Fact]
    public void DiscordModuleOptionRemovesOnlyItsModuleInEveryVersion()
    {
        Touch("local", "app-1.0.9", "Discord.exe");
        var krisp = Touch("local", "app-1.0.9", "modules", "discord_krisp-1", "discord_krisp", "a.node");
        var krispOld = Touch("local", "app-1.0.8", "modules", "discord_krisp-2", "a.node");
        var rpc = Touch("local", "app-1.0.9", "modules", "discord_rpc-1", "a.node");
        var similar = Touch("local", "app-1.0.9", "modules", "discord_krisp_extra-1", "a.node");
        var option = Option(DiscordDebloat.Options(Discord, "Discord.exe"), "krisp");

        Assert.Equal(DebloatState.NotApplied, option.Read());
        option.Apply(default).Wait();

        Assert.Equal(DebloatState.Applied, option.Read());
        Assert.False(File.Exists(krisp));
        Assert.False(File.Exists(krispOld));
        Assert.True(File.Exists(rpc));
        Assert.True(File.Exists(similar)); // discord_krisp_extra isn't discord_krisp
    }

    [Fact]
    public void PresetRunsEveryFunctionAndReportsTheFailuresAtTheEnd()
    {
        var ran = new List<string>();
        DebloatOption Function(string id, Func<Task>? apply = null) => new()
        {
            Id = id, Name = id, Description = "",
            Apply = _ => { ran.Add(id); return apply?.Invoke() ?? Task.CompletedTask; },
        };
        var preset = DebloatCatalog.Preset("p", "P", "", new[] { Function("a"), Function("b", () => throw new IOException("no network")), Function("c") });

        var error = Assert.Throws<AggregateException>(() => preset.Apply(default).Wait());

        Assert.Equal(new[] { "a", "b", "c" }, ran);
        Assert.Contains("no network", error.InnerException!.Message);
        Assert.True(preset.IsPreset && preset.ClosesApp);
    }

    [Theory]
    [InlineData(DebloatState.Applied, DebloatState.Applied, DebloatState.OneShot)]
    [InlineData(DebloatState.NotApplied, DebloatState.NotApplied, DebloatState.OneShot)]
    [InlineData(DebloatState.Partial, DebloatState.Applied, DebloatState.NotApplied)]
    [InlineData(DebloatState.Partial, DebloatState.Partial, DebloatState.Applied)]
    public void PresetStateCombinesItsFunctions(DebloatState expected, params DebloatState[] states) =>
        Assert.Equal(expected, DebloatCatalog.CombinedState(states));

    [Fact]
    public void DiscordPresetsDifferOnlyInKrispAndComeFirst()
    {
        Assert.DoesNotContain("krisp", DiscordDebloat.PresetKeepKrisp);
        Assert.Equal(new[] { "krisp" }, DiscordDebloat.PresetNoKrisp.Except(DiscordDebloat.PresetKeepKrisp));
        var options = DiscordDebloat.Options(Discord, "Discord.exe");
        Assert.Equal(new[] { "preset-no-krisp", "preset-keep-krisp" }, options.Where(o => o.IsPreset).Select(o => o.Id));
        Assert.True(options[0].IsPreset && options[1].IsPreset);
    }

    [Fact]
    public void DiscordChannelsAreTheFourOfficialOnesWithTheirOwnFolders()
    {
        var channels = DiscordDebloat.Channels;
        Assert.Equal(new[] { "Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment" }, channels.Select(c => Path.GetFileName(c.Root)));
        Assert.Equal(4, channels.Select(c => c.Id).Distinct().Count());
        Assert.Equal(4, channels.Select(c => c.Data).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(4, DebloatCatalog.Apps.Count(a => a.Icon == "discord"));
    }

    [Fact]
    public void DiscordStartupCommandNamesTheChannelAndWhatTheUserRemoved()
    {
        Assert.Equal("\"C:\\KOS\\KosToolbox.exe\" --discord-debloat discord rpc,krisp", DiscordDebloat.StartupCommand(@"C:\KOS\KosToolbox.exe", Discord, new[] { "rpc", "krisp" }));
        Assert.Equal("\"C:\\KOS\\KosToolbox.exe\" --discord-debloat discord", DiscordDebloat.StartupCommand(@"C:\KOS\KosToolbox.exe", Discord, Array.Empty<string>()));
    }

    [Fact]
    public void DiscordStartupPassRemovesWhatAnUpdateBroughtBackButKeepsTheUsersChoices()
    {
        Touch("local", "app-1.0.9", "Discord.exe");
        var optional = Touch("local", "app-1.0.9", "modules", "discord_cloudsync-1", "a.node");
        var rpc = Touch("local", "app-1.0.9", "modules", "discord_rpc-1", "a.node");
        var krisp = Touch("local", "app-1.0.9", "modules", "discord_krisp-1", "a.node");
        var english = Touch("local", "app-1.0.9", "locales", "en-US.pak");
        var portuguese = Touch("local", "app-1.0.9", "locales", "pt-BR.pak");
        var cache = Touch("roaming", "logs", "a.log");

        // "startup" isn't a function the pass may repeat; a name outside the list is ignored.
        DiscordDebloat.ApplyLogonPassAsync(Discord, "Discord.exe", "rpc,startup,nonsense").Wait();

        Assert.False(File.Exists(optional));
        Assert.False(File.Exists(rpc));
        Assert.True(File.Exists(krisp));
        Assert.True(File.Exists(english));
        Assert.False(File.Exists(portuguese));
        Assert.False(File.Exists(cache));
        Assert.False(DiscordDebloat.Setting(Discord, "OPEN_ON_STARTUP"));
    }

    [Fact]
    public void DiscordStartupEntryFollowsWhatTheUserRemovesAndCanBeUndone()
    {
        Touch("local", "app-1.0.9", "Discord.exe");
        Touch("local", "app-1.0.9", "modules", "discord_krisp-1", "a.node");
        Touch("local", "app-1.0.9", "modules", "discord_rpc-1", "a.node");
        Touch("local", "app-1.0.9", "modules", "discord_overlay2-1", "a.node");
        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run", value = "KOS KosTestDiscord debloat";
        string? Entry() { using var key = Registry.CurrentUser.OpenSubKey(run); return key?.GetValue(value) as string; }
        var options = DiscordDebloat.Options(Discord, "Discord.exe");
        var startup = Option(options, "startup");
        try
        {
            Assert.Equal(DebloatState.NotApplied, startup.Read());
            // The test host isn't in Program Files, and an entry that runs at every sign-in needs that.
            Assert.Throws<InvalidOperationException>(() => startup.Apply(default).Wait());
            Assert.Null(Entry());

            Option(options, "rpc").Apply(default).Wait();
            // No discord_desktop_core yet: Discord hasn't downloaded its modules, so absent doesn't mean removed.
            DiscordDebloat.WriteStartup(Discord, "Discord.exe");
            Assert.EndsWith("--discord-debloat discord", Entry());

            Touch("local", "app-1.0.9", "modules", "discord_desktop_core-1", "a.node");
            DiscordDebloat.WriteStartup(Discord, "Discord.exe");
            Assert.Equal(DebloatState.Applied, startup.Read());
            Assert.EndsWith("--discord-debloat discord rpc", Entry());

            Option(options, "krisp").Apply(default).Wait();
            Assert.EndsWith("--discord-debloat discord rpc,krisp", Entry());

            // An update brought Krisp back: removing another module doesn't drop it from the list, and neither does the pass.
            Touch("local", "app-1.0.9", "modules", "discord_krisp-1", "a.node");
            Option(options, "overlay").Apply(default).Wait();
            Assert.EndsWith("--discord-debloat discord rpc,krisp,overlay", Entry());
            Touch("local", "app-1.0.9", "modules", "discord_rpc-1", "a.node");
            DiscordDebloat.ApplyLogonPassAsync(Discord, "Discord.exe", "rpc,krisp,overlay").Wait();
            Assert.EndsWith("--discord-debloat discord rpc,krisp,overlay", Entry());

            // Another copy of the toolbox made the entry: the box reads as partial, removing a module leaves it alone, and ticking keeps its list.
            using (var key = Registry.CurrentUser.CreateSubKey(run)) key.SetValue(value, "\"C:\\old\\KosToolbox.exe\" --discord-debloat discord krisp");
            Assert.Equal(DebloatState.Partial, startup.Read());
            Option(options, "rpc").Apply(default).Wait();
            Assert.StartsWith("\"C:\\old\\", Entry());
            Assert.Equal(new[] { "krisp" }, DiscordDebloat.ListedRemoved(Entry()!));
            Touch("local", "app-1.0.9", "modules", "discord_krisp-1", "a.node");
            DiscordDebloat.WriteStartup(Discord, "Discord.exe");
            Assert.EndsWith("--discord-debloat discord rpc,overlay,krisp", Entry());

            startup.Undo!(default).Wait();
            Assert.Null(Entry());
            Assert.Equal(DebloatState.NotApplied, startup.Read());
        }
        finally
        {
            using var key = Registry.CurrentUser.OpenSubKey(run, writable: true);
            key?.DeleteValue(value, throwOnMissingValue: false);
        }
    }

    [Fact]
    public void DiscordLatestIsTheNewestVersionWithTheExecutable()
    {
        Touch("local", "app-1.0.9", "Discord.exe");
        Touch("local", "app-1.0.10", "Discord.exe"); // 10 > 9, not alphabetical
        Touch("local", "app-1.0.11", "notes.txt"); // no exe
        Assert.EndsWith("app-1.0.10", DiscordDebloat.Latest(Discord, "Discord.exe"));
    }

    [Fact]
    public void DiscordLocalesKeepOnlyEnglish()
    {
        Touch("local", "app-1.0.9", "Discord.exe");
        var english = Touch("local", "app-1.0.9", "locales", "en-US.pak");
        var portuguese = Touch("local", "app-1.0.9", "locales", "pt-BR.pak");
        var option = Option(DiscordDebloat.Options(Discord, "Discord.exe"), "locales");
        option.Apply(default).Wait();
        Assert.True(File.Exists(english));
        Assert.False(File.Exists(portuguese));
        Assert.Equal(DebloatState.Applied, option.Read());
    }

    [Fact]
    public void DiscordSettingsKeepTheOtherValuesAndRefuseAnUnreadableFile()
    {
        Directory.CreateDirectory(At("roaming"));
        var path = At("roaming", "settings.json");
        File.WriteAllText(path, """{"BACKGROUND_COLOR":"#202225","enableHardwareAcceleration":true}""");
        var option = Option(DiscordDebloat.Options(Discord, "Discord.exe"), "hwaccel");

        Assert.Equal(DebloatState.NotApplied, option.Read());
        option.Apply(default).Wait();
        Assert.Equal(DebloatState.Applied, option.Read());
        var saved = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("#202225", (string?)saved["BACKGROUND_COLOR"]);
        option.Undo!(default).Wait();
        Assert.Equal(DebloatState.NotApplied, option.Read());

        File.WriteAllText(path, "{ broken");
        Assert.Throws<InvalidOperationException>(() => option.Apply(default).Wait());
        Assert.Equal("{ broken", File.ReadAllText(path)); // not replaced
    }

    [Fact]
    public void OpenAsarIsRecognizedByItsContent()
    {
        var open = At("open.asar");
        File.WriteAllText(open, "header ... OpenAsar ...");
        Assert.True(DiscordDebloat.IsOpenAsar(open));
        var stock = At("stock.asar");
        File.WriteAllText(stock, "the original Discord asar");
        Assert.False(DiscordDebloat.IsOpenAsar(stock));
        Assert.False(DiscordDebloat.IsOpenAsar(At("missing.asar")));
    }

    [Fact]
    public void WithVencordTheRealAsarIsTheRenamedOne()
    {
        Touch("local", "app-1.0.9", "Discord.exe");
        Touch("local", "app-1.0.9", "resources", "app.asar");
        Assert.EndsWith(@"resources\app.asar", DiscordDebloat.Asar(Discord, "Discord.exe"));
        Touch("local", "app-1.0.9", "resources", "_app.asar");
        Assert.EndsWith(@"resources\_app.asar", DiscordDebloat.Asar(Discord, "Discord.exe"));
    }

    // ---------- links ----------

    [Fact]
    public void DeletingNeverFollowsALink()
    {
        Touch("local", "app-1.0.9", "Discord.exe");
        var outside = Touch("outside", "keep.txt");
        var link = At("local", "app-1.0.9", "modules", "discord_rpc-1");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var junction = ProcessRunner.Run(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /J \"{link}\" \"{At("outside")}\"");
        Assert.Equal(0, junction.ExitCode);

        // Enumeration skips the link, so the option neither reports it nor touches what it points to.
        Assert.Empty(DiscordDebloat.ModuleFolders(Discord, "discord_rpc"));
        DebloatFiles.Delete(new[] { link });
        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void ALinkedFolderIsNotCleanedThroughItsParent()
    {
        var outside = Touch("outside", "logs", "keep.log");
        var data = At("roaming");
        var junction = ProcessRunner.Run(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /J \"{data}\" \"{At("outside")}\"");
        Assert.Equal(0, junction.ExitCode);
        Assert.Empty(DiscordDebloat.Cache(Discord));
        Assert.True(File.Exists(outside));
        Directory.Delete(data); // only the junction, before Dispose walks the folder
    }

    [Fact]
    public void UserChangesRefuseOnlyAnElevationThatUacGranted()
    {
        Assert.True(DebloatCatalog.RefusesUserChange(2)); // elevated from a split token
        Assert.False(DebloatCatalog.RefusesUserChange(1)); // UAC off: every program is already administrator
        Assert.False(DebloatCatalog.RefusesUserChange(3)); // unelevated
        Assert.True(DebloatCatalog.RefusesUserChange(null)); // unreadable
        Assert.NotNull(Elevation.TokenElevationType());
    }

    [Fact]
    public void OpenAsarSettingsAreMergedIntoTheExistingBlock()
    {
        Directory.CreateDirectory(At("roaming"));
        File.WriteAllText(At("roaming", "settings.json"), """{"openasar":{"css":"body{}"}}""");
        DiscordDebloat.WriteSettings(Discord, s => ((JsonObject)s["openasar"]!)["setup"] = true);
        var saved = JsonNode.Parse(File.ReadAllText(At("roaming", "settings.json")))!["openasar"]!;
        Assert.Equal("body{}", (string?)saved["css"]);
        Assert.True((bool)saved["setup"]!);
    }

    // ---------- Spotify ----------

    SpotifyInstall Spotify => new(At("spotify"), At("spotifylocal"));

    [Fact]
    public void SpotifyUpdateBlockIsReadBackAndUndone()
    {
        Assert.False(SpotifyDebloat.IsUpdateBlocked(Spotify));
        Touch("spotifylocal", "Update", "pending.bin");
        SpotifyDebloat.BlockUpdates(Spotify);
        Assert.True(SpotifyDebloat.IsUpdateBlocked(Spotify));
        SpotifyDebloat.UnblockUpdates(Spotify);
        Assert.False(SpotifyDebloat.IsUpdateBlocked(Spotify));
        Assert.False(File.Exists(At("spotifylocal", "Update", "pending.bin")));
    }

    [Fact]
    public void SpotifyRuntimeOptionKeepsWhatTheAppNeeds()
    {
        var removed = new[] { Touch("spotify", "chrome_100_percent.pak"), Touch("spotify", "d3dcompiler_47.dll"), Touch("spotify", "libEGL.dll"), Touch("spotify", "vulkan-1.dll"), Touch("spotify", "crashpad.dll") };
        var kept = new[] { Touch("spotify", "Spotify.exe"), Touch("spotify", "libcef.dll"), Touch("spotify", "chrome_elf.dll"), Touch("spotify", "v8_context_snapshot.bin") };
        var option = Option(SpotifyDebloat.Options(Spotify), "runtime");
        option.Apply(default).Wait();
        Assert.All(removed, f => Assert.False(File.Exists(f)));
        Assert.All(kept, f => Assert.True(File.Exists(f)));
        Assert.Equal(DebloatState.Applied, option.Read());
    }

    // ---------- browsers ----------

    const string TestKey = @"Software\KosToolboxTests\Policies";

    [Fact]
    public void BrowserPoliciesAreWrittenReadBackAndRemoved()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\KosToolboxTests", throwOnMissingSubKey: false);
        try
        {
            var values = new[] { new PolicyValue("x", "", "Number", 0), new PolicyValue("x", "", "Text", "google.com"), new PolicyValue("x", "Sub", "Nested", 1) };
            Assert.Equal(DebloatState.NotApplied, BrowserPolicies.Read(Registry.CurrentUser, TestKey, values));
            BrowserPolicies.Write(Registry.CurrentUser, TestKey, values.Take(1));
            Assert.Equal(DebloatState.Partial, BrowserPolicies.Read(Registry.CurrentUser, TestKey, values));
            BrowserPolicies.Write(Registry.CurrentUser, TestKey, values);
            Assert.Equal(DebloatState.Applied, BrowserPolicies.Read(Registry.CurrentUser, TestKey, values));
            BrowserPolicies.Remove(Registry.CurrentUser, TestKey, values);
            Assert.Equal(DebloatState.NotApplied, BrowserPolicies.Read(Registry.CurrentUser, TestKey, values));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(@"Software\KosToolboxTests", throwOnMissingSubKey: false); }
    }

    [Fact]
    public void BrowserPolicyDataIsWellFormed()
    {
        Assert.Equal(@"K\Sub", BrowserPolicies.KeyOf("K", new PolicyValue("x", "Sub", "N", 1)));
        Assert.Equal(@"SOFTWARE\Other", BrowserPolicies.KeyOf("K", new PolicyValue("x", @"@SOFTWARE\Other", "N", 1)));
        foreach (var table in new[] { BrowserPolicies.Chrome, BrowserPolicies.Brave, BrowserPolicies.Firefox })
        {
            Assert.NotEmpty(table);
            Assert.All(table, v => Assert.True(v.Data is string or int, $"{v.Name}: {v.Data?.GetType()}"));
            Assert.Equal(table.Length, table.Select(v => $"{v.SubKey}|{v.Name}").Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        // The options that weaken the browser are kept apart, so they can't come along with telemetry or AI.
        var chrome = BrowserPolicies.Chrome.Where(v => v.SubKey.Length == 0).ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("security", chrome["SafeBrowsingEnabled"].Group);
        Assert.Equal("security", chrome["DownloadRestrictions"].Group);
    }

    // ---------- elevated requests ----------

    [Fact]
    public void ElevatedRequestAcceptsOnlyAdminOptionsOfTheCatalog()
    {
        Assert.NotNull(DebloatCatalog.Validate(new DebloatJob { App = "chrome", Option = "policy-ai" }).Option);
        Assert.NotNull(DebloatCatalog.Validate(new DebloatJob { App = "chrome", Option = "policy-ai", Undo = true }).Option);
        Assert.Throws<InvalidOperationException>(() => DebloatCatalog.Validate(new DebloatJob { App = "nope", Option = "policy-ai" }));
        Assert.Throws<InvalidOperationException>(() => DebloatCatalog.Validate(new DebloatJob { App = "chrome", Option = @"..\..\x" }));
        Assert.Throws<InvalidOperationException>(() => DebloatCatalog.Validate(new DebloatJob { App = "discord", Option = "krisp" })); // per-user: doesn't run elevated
        Assert.Throws<InvalidOperationException>(() => DebloatCatalog.Validate(new DebloatJob { App = "chrome", Option = "files", Undo = true })); // no undo
        Assert.Throws<InvalidOperationException>(() => new ElevatedJob { Debloat = new DebloatJob { App = "chrome", Option = "policy-ai" }, TweakId = "x" }.Validate());
    }

    [Fact]
    public void OptionIdsAreUniqueInEachApp()
    {
        foreach (var app in DebloatCatalog.Apps)
            Assert.Equal(app.Options.Count, app.Options.Select(o => o.Id).Distinct().Count());
        Assert.Equal(DebloatCatalog.Apps.Count, DebloatCatalog.Apps.Select(a => a.Id).Distinct().Count());
    }

    // ---------- OBS ----------

    [Theory]
    [InlineData(1080, 30, 3732)]
    [InlineData(1080, 60, 7465)]
    [InlineData(1080, 120, 8000)]
    [InlineData(936, 60, 5607)]
    [InlineData(900, 30, 2592)]
    [InlineData(720, 60, 3318)]
    [InlineData(720, 120, 8000)]
    [InlineData(1080, 144, 7465)] // other rates use the 60 FPS value
    public void ObsBitrateFollowsResolutionAndFps(int height, int fps, int bitrate) =>
        Assert.Equal(bitrate, new ObsProfile(height, fps, ObsEncoder.Nvenc).Bitrate);

    [Fact]
    public void ObsIniUsesCommonFpsOrFractionalOnes()
    {
        var sixty = new ObsProfile(1080, 60, ObsEncoder.Nvenc).BasicIni();
        Assert.Contains("FPSCommon=60\n", sixty);
        Assert.DoesNotContain("FPSType", sixty);
        Assert.Contains("BaseCX=1920\nBaseCY=1080\nOutputCX=1920\nOutputCY=1080\n", sixty);
        Assert.Contains("Encoder=obs_nvenc_h264_tex\n", sixty);
        Assert.Contains("ReplayBuffer={\"ReplayBuffer.Save\":[{\"key\":\"OBS_KEY_F9\"}]}", sixty);
        var fractional = new ObsProfile(720, 120, ObsEncoder.Amf).BasicIni();
        Assert.Contains("FPSType=2\nFPSNum=120\nFPSDen=1\n", fractional);
        Assert.Contains("BaseCX=1280\n", fractional);
        Assert.Contains("Encoder=h264_texture_amf\n", fractional);
        Assert.Contains("Encoder=obs_qsv11_v2\n", new ObsProfile(900, 30, ObsEncoder.Qsv).BasicIni());
    }

    [Theory]
    [InlineData(ObsEncoder.Nvenc)]
    [InlineData(ObsEncoder.Amf)]
    [InlineData(ObsEncoder.Qsv)]
    public void ObsEncoderSettingsAreValidJson(ObsEncoder encoder)
    {
        var profile = new ObsProfile(1080, 60, encoder);
        Assert.Equal(7465, (int)JsonNode.Parse(profile.StreamEncoderJson())!["bitrate"]!);
        Assert.Equal("CQP", (string?)JsonNode.Parse(profile.RecordEncoderJson())!["rate_control"]);
    }

    [Theory]
    [InlineData(ObsEncoder.Nvenc, "NVIDIA GeForce RTX 3060", "Intel(R) UHD Graphics 630")]
    [InlineData(ObsEncoder.Amf, "AMD Radeon RX 6600")]
    [InlineData(ObsEncoder.Qsv, "Intel(R) UHD Graphics 630")]
    [InlineData(ObsEncoder.Qsv)]
    public void ObsEncoderDefaultsToTheInstalledCard(ObsEncoder expected, params string[] adapters) =>
        Assert.Equal(expected, ObsDebloat.EncoderFor(adapters));

    ObsInstall Obs => new(At("obs64.exe"), At("obs-studio"));

    [Fact]
    public void ObsProfileIsCreatedAsTheActiveOneAndKeepsOtherSettings()
    {
        Touch("obs-studio", "basic", "profiles", "Mine", "basic.ini");
        Directory.CreateDirectory(At("obs-studio"));
        File.WriteAllText(At("obs-studio", "user.ini"), "[General]\nPre19Defaults=false\n\n[Basic]\nProfile=Mine\nProfileDir=Mine\nSceneCollection=Old\n\n[BasicWindow]\ngeometry=abc\n");

        ObsDebloat.CreateProfile(Obs, new ObsProfile(1080, 120, ObsEncoder.Nvenc));

        Assert.Equal("KOS 1080p120 Nvenc", ObsDebloat.ActiveProfile(Obs));
        Assert.True(File.Exists(At("obs-studio", "basic", "profiles", "KOS 1080p120 Nvenc", "basic.ini")));
        Assert.True(File.Exists(At("obs-studio", "basic", "profiles", "KOS 1080p120 Nvenc", "streamEncoder.json")));
        Assert.True(File.Exists(At("obs-studio", "basic", "profiles", "Mine", "basic.ini")));
        var user = At("obs-studio", "user.ini");
        Assert.Equal("false", IniFile.Get(user, "General", "Pre19Defaults"));
        Assert.Equal("true", IniFile.Get(user, "General", "FirstRun"));
        Assert.Equal("Old", IniFile.Get(user, "Basic", "SceneCollection"));
        Assert.Equal("abc", IniFile.Get(user, "BasicWindow", "geometry"));
    }

    [Theory]
    [InlineData(1000, 60)] // not an offered resolution
    [InlineData(1080, 0)]
    [InlineData(1080, 1001)]
    public void ObsRefusesInvalidProfiles(int height, int fps) =>
        Assert.Throws<InvalidOperationException>(() => ObsDebloat.CreateProfile(Obs, new ObsProfile(height, fps, ObsEncoder.Nvenc)));

    [Fact]
    public void ObsSceneCollectionHasTheNoiseGateOnTheMic()
    {
        var scene = JsonNode.Parse(ObsDebloat.SceneJson())!;
        Assert.Equal("noise_gate_filter", (string?)scene["AuxAudioDevice1"]!["filters"]![0]!["id"]);
        Assert.Equal("wasapi_output_capture", (string?)scene["DesktopAudioDevice1"]!["id"]);
    }

    static byte[] Zip(params string[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var name in entries)
            {
                var entry = zip.CreateEntry(name);
                if (!name.EndsWith('/')) using (var writer = new StreamWriter(entry.Open())) writer.Write("x");
            }
        return stream.ToArray();
    }

    [Fact]
    public void ObsThemeIsInstalledInTheUserFolderAndSelectedAndUndoGoesBackToTheDefault()
    {
        Directory.CreateDirectory(At("obs-studio"));
        var user = At("obs-studio", "user.ini");
        File.WriteAllText(user, "[Basic]\nProfile=Mine\n");
        var theme = Option(ObsDebloat.Options(Obs), "theme");
        Assert.Equal(DebloatState.NotApplied, theme.Read());

        // The layout of the real download (FluentDarkGrey-0.4.5.zip), with its folder entries.
        ObsDebloat.InstallTheme(Obs, Zip("Fluent Dark (Grey).obt", "Fluent Dark (Grey)_default.ovt", "fluentdarkgrey/", "fluentdarkgrey/arrows/", "fluentdarkgrey/arrows/up.svg"));

        Assert.Equal(DebloatState.Applied, theme.Read());
        Assert.True(File.Exists(At("obs-studio", "themes", "fluentdarkgrey", "arrows", "up.svg")));
        Assert.True(File.Exists(At("obs-studio", "themes", "Fluent Dark (Grey).obt")));
        Assert.Equal("fluentdarkgrey", IniFile.Get(user, "Appearance", "Theme"));
        Assert.Equal("Mine", IniFile.Get(user, "Basic", "Profile"));

        theme.Undo!(default).Wait();
        Assert.Equal(DebloatState.NotApplied, theme.Read());
        Assert.Equal("com.obsproject.Yami.Original", IniFile.Get(user, "Appearance", "Theme"));
    }

    [Theory]
    [InlineData("other/style.qss")] // no theme folder
    [InlineData("fluentdarkgrey/style.qss", "../../evil.svg")] // leaves the folder
    [InlineData("fluentdarkgrey/style.qss", "dropper.exe")] // not part of a theme, and not a theme file
    [InlineData("fluentdarkgrey/style.qss", "fluentdarkgrey/dropper.exe")]
    [InlineData("fluentdarkgrey/style.qss", "My Own Theme.ovt")] // would replace another theme of the user
    public void ObsThemeRefusesAnyOtherDownloadBeforeWritingAnything(params string[] entries)
    {
        Assert.Throws<InvalidOperationException>(() => ObsDebloat.InstallTheme(Obs, Zip(entries)));
        Assert.False(Directory.Exists(At("obs-studio", "themes")));
        Assert.False(File.Exists(At("obs-studio", "user.ini")));
    }

    [Fact]
    public void DiscordCacheDoesNotFollowALinkInTheMiddleOfThePath()
    {
        var outside = Touch("outside", "js", "keep.bin");
        Directory.CreateDirectory(At("roaming"));
        var link = At("roaming", "Code Cache");
        Assert.Equal(0, ProcessRunner.Run(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /J \"{link}\" \"{At("outside")}\"").ExitCode);

        Assert.Empty(DiscordDebloat.Cache(Discord));

        Assert.True(File.Exists(outside));
        Directory.Delete(link); // only the junction, before Dispose walks the folder
    }

    [Fact]
    public void OverlayFunctionAlsoRemovesTheNewOverlayModule()
    {
        Touch("local", "app-1.0.9", "Discord.exe");
        var overlay = Touch("local", "app-1.0.9", "modules", "discord_desktop_overlay-1", "a.node");
        Option(DiscordDebloat.Options(Discord, "Discord.exe"), "overlay").Apply(default).Wait();
        Assert.False(File.Exists(overlay));
    }

    [Fact]
    public void IniKeysAreChangedInPlaceOrAddedToTheirSection()
    {
        var path = At("a.ini");
        IniFile.Set(path, "One", "A", "1");
        IniFile.Set(path, "Two", "B", "2");
        IniFile.Set(path, "One", "C", "3");
        IniFile.Set(path, "One", "A", "9");
        Assert.Equal("[One]\nA=9\nC=3\n\n[Two]\nB=2\n", File.ReadAllText(path).Replace("\r\n", "\n"));
        Assert.Null(IniFile.Get(path, "Two", "A"));
    }
}
