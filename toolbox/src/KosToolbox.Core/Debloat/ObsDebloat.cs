using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KosToolbox.Core.Setup;
using KosToolbox.Core.Text;
using Microsoft.Win32;

namespace KosToolbox.Core.Debloat;

public enum ObsEncoder { Nvenc, Amf, Qsv }

/// <summary>Streaming and recording profile: one folder per combination, so several can coexist and OBS switches between them.</summary>
public sealed record ObsProfile(int Height, int Fps, ObsEncoder Encoder)
{
    /// <summary>Resolutions offered, by height (16:9).</summary>
    public static readonly IReadOnlyDictionary<int, int> Resolutions = new Dictionary<int, int> { [1080] = 1920, [936] = 1664, [900] = 1600, [720] = 1280 };
    public const int MinFps = 1, MaxFps = 1000;
    const string ReplayHotkey = """{"ReplayBuffer.Save":[{"key":"OBS_KEY_F9"}]}""";

    public int Width => Resolutions[Height];
    public string Name => $"KOS {Height}p{Fps} {Encoder}";

    /// <summary>Stream bitrate in kbps: by resolution for 30 and 60 FPS, 8000 at 120; another frame rate uses the 60 FPS value.</summary>
    public int Bitrate => (Height, Fps) switch
    {
        (_, 120) => 8000,
        (1080, 30) => 3732, (1080, _) => 7465,
        (936, 30) => 2804, (936, _) => 5607,
        (900, 30) => 2592, (900, _) => 5184,
        (720, 30) => 3041, _ => 3318,
    };

    string EncoderId => Encoder switch { ObsEncoder.Nvenc => "obs_nvenc_h264_tex", ObsEncoder.Amf => "h264_texture_amf", _ => "obs_qsv11_v2" };

    public string BasicIni()
    {
        var video = Fps is 30 or 60
            ? $"FPSCommon={Fps}\n"
            : $"FPSCommon=30\nFPSType=2\nFPSNum={Fps}\nFPSDen=1\nFPSInt=30\n";
        return $"""
            [General]
            Name={Name}

            [Output]
            Mode=Advanced
            DelayEnable=false
            DelayPreserve=true
            Reconnect=true
            RetryDelay=5
            MaxRetries=25
            BindIP=default
            NewSocketLoopEnable=false
            LowLatencyEnable=false

            [AdvOut]
            ApplyServiceSettings=true
            UseRescale=false
            VodTrackIndex=2
            Encoder={EncoderId}
            RecType=Standard
            RecFormat2=fragmented_mp4
            RecUseRescale=false
            RecEncoder={EncoderId}
            FFOutputToFile=true
            FFVGOPSize=250
            FFUseRescale=false
            FFIgnoreCompat=false
            FFABitrate=160
            Track1Bitrate=160
            Track2Bitrate=160
            Track3Bitrate=160
            Track4Bitrate=160
            Track5Bitrate=160
            Track6Bitrate=160
            RecSplitFileTime=15
            RecSplitFileSize=2048
            RecRB=true
            RecRBTime=120
            RecRBSize=1024
            AudioEncoder=ffmpeg_aac
            RecAudioEncoder=ffmpeg_aac
            RecSplitFileType=Time
            RecFileNameWithoutSpace=false

            [Video]
            BaseCX={Width}
            BaseCY={Height}
            OutputCX={Width}
            OutputCY={Height}
            {video}ScaleType=bilinear
            ColorFormat=NV12
            ColorSpace=sRGB
            ColorRange=Partial
            SdrWhiteLevel=300
            HdrNominalPeakLevel=1000

            [Audio]
            MonitoringDeviceId=default
            MonitoringDeviceName=default
            SampleRate=48000
            ChannelSetup=Stereo
            MeterDecayRate=23.53

            [Hotkeys]
            ReplayBuffer={ReplayHotkey}

            """.Replace("\r\n", "\n");
    }

    public string RecordEncoderJson() => Encoder switch
    {
        ObsEncoder.Nvenc => """{"rate_control":"CQP","cqp":17,"preset":"p4","multipass":"disabled","profile":"high","lookahead":false,"adaptive_quantization":false}""",
        ObsEncoder.Amf => """{"keyint_sec":2,"rate_control":"CQP","cqp":17,"bf":0}""",
        _ => """{"target_usage":"TU7","profile":"high","keyint_sec":2,"rate_control":"CQP","cqp":17,"bframes":0}""",
    };

    public string StreamEncoderJson() => Encoder switch
    {
        ObsEncoder.Nvenc => $$"""{"rate_control":"CBR","bitrate":{{Bitrate}},"preset":"p4","tune":"hq","multipass":"disabled","profile":"main","adaptive_quantization":false,"lookahead":false}""",
        ObsEncoder.Amf => $$"""{"bitrate":{{Bitrate}},"profile":"main","preset":"balanced","keyint_sec":2,"bf":0}""",
        _ => $$"""{"bitrate":{{Bitrate}},"profile":"main","target_usage":"TU4","keyint_sec":2,"bframes":0}""",
    };
}

/// <summary>Small editor for OBS's .ini files: changes only the given key and keeps everything else.</summary>
internal static class IniFile
{
    internal static string? Get(string path, string section, string key)
    {
        if (!File.Exists(path)) return null;
        var inside = false;
        foreach (var line in File.ReadAllLines(path))
        {
            var t = line.Trim();
            if (t.StartsWith('[')) inside = t.Equals($"[{section}]", StringComparison.OrdinalIgnoreCase);
            else if (inside && t.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return t[(key.Length + 1)..];
        }
        return null;
    }

    internal static void Set(string path, string section, string key, string value)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        var start = lines.FindIndex(l => l.Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Length > 0) lines.Add("");
            lines.Add($"[{section}]");
            lines.Add($"{key}={value}");
        }
        else
        {
            var end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
            if (end < 0) end = lines.Count;
            var at = lines.FindIndex(start + 1, end - start - 1, l => l.Trim().StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
            if (at >= 0) lines[at] = $"{key}={value}";
            else
            {
                // After the section's last line with content, before the blank line that separates it from the next.
                var last = end - 1;
                while (last > start && lines[last].Trim().Length == 0) last--;
                lines.Insert(last + 1, $"{key}={value}");
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Atomic: these files hold the user's other OBS settings.
        var tmp = path + ".tmp";
        File.WriteAllLines(tmp, lines, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}

public sealed record ObsInstall(string Executable, string Data);

/// <summary>OBS Studio: profiles for streaming/recording and the settings around them, in the user's %AppData%\obs-studio.</summary>
public static class ObsDebloat
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunValue = "obs64", SceneCollection = "KOS";
    const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    const string ThemeUrl = "https://obsproject.com/forum/resources/fluent-dark-grey.1961/download", ThemeId = "fluentdarkgrey", DefaultTheme = "com.obsproject.Yami.Original";
    const string ThemeFilePrefix = "Fluent Dark (Grey)";
    const long MaxThemeBytes = 5_000_000;
    static readonly string[] ThemeFileTypes = { ".obt", ".ovt", ".qss", ".svg", ".png" };

    public static ObsInstall Default => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"obs-studio\bin\64bit\obs64.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio"));

    public static DebloatApp App(ObsInstall o) => new()
    {
        Id = "obs", Name = "OBS Studio", Icon = "obs", Processes = new[] { "obs64" }, ForceClose = false,
        Executable = () => File.Exists(o.Executable) ? o.Executable : null,
        Options = Options(o),
    };

    static string Global(ObsInstall o) => Path.Combine(o.Data, "global.ini");
    static string User(ObsInstall o) => Path.Combine(o.Data, "user.ini");
    static string SceneFile(ObsInstall o) => Path.Combine(o.Data, "basic", "scenes", SceneCollection + ".json");
    static string Themes(ObsInstall o) => Path.Combine(o.Data, "themes");
    static Task Sync(Action action) { action(); return Task.CompletedTask; }

    internal static IReadOnlyList<DebloatOption> Options(ObsInstall o) => new[]
    {
        new DebloatOption
        {
            Id = "theme", Name = Txt.ObsTheme, Description = Txt.ObsThemeInfo, ClosesApp = true,
            Read = () => IniFile.Get(User(o), "Appearance", "Theme") == ThemeId && Directory.Exists(Path.Combine(Themes(o), ThemeId)) ? DebloatState.Applied : DebloatState.NotApplied,
            Apply = async ct => InstallTheme(o, await Downloader.Http.GetByteArrayAsync(ThemeUrl, ct)),
            Undo = _ => Sync(() => IniFile.Set(User(o), "Appearance", "Theme", DefaultTheme)),
        },
        new DebloatOption
        {
            Id = "prefs", Name = Txt.ObsPrefs, Description = Txt.ObsPrefsInfo, ClosesApp = true,
            Read = () => IniFile.Get(Global(o), "General", "EnableAutoUpdates") == "false" && IniFile.Get(Global(o), "Audio", "DisableAudioDucking") == "true" ? DebloatState.Applied : DebloatState.NotApplied,
            Apply = _ => Sync(() =>
            {
                IniFile.Set(Global(o), "General", "EnableAutoUpdates", "false");
                IniFile.Set(Global(o), "Audio", "DisableAudioDucking", "true");
            }),
            Undo = _ => Sync(() =>
            {
                IniFile.Set(Global(o), "General", "EnableAutoUpdates", "true");
                IniFile.Set(Global(o), "Audio", "DisableAudioDucking", "false");
            }),
        },
        new DebloatOption
        {
            Id = "scene", Name = Txt.ObsScene, Description = Txt.ObsSceneInfo, ClosesApp = true,
            Read = () => File.Exists(SceneFile(o)) ? DebloatState.Applied : DebloatState.NotApplied,
            Apply = _ => Sync(() => CreateSceneCollection(o)),
        },
        new DebloatOption
        {
            Id = "replay", Name = Txt.ObsReplay, Description = Txt.ObsReplayInfo,
            Read = () =>
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKey);
                return run?.GetValue(RunValue) is not null ? DebloatState.Applied : DebloatState.NotApplied;
            },
            Apply = _ => Sync(() =>
            {
                using var run = Registry.CurrentUser.CreateSubKey(RunKey);
                run.SetValue(RunValue, ReplayCommand(o));
            }),
            Undo = _ => Sync(() =>
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                run?.DeleteValue(RunValue, throwOnMissingValue: false);
            }),
        },
    };

    /// <summary>
    /// The download comes from whatever the theme's author last uploaded: it is checked as a whole first, because extraction stops at a
    /// bad entry only after writing the ones before it. Only the theme's own folder and files, of the types a theme uses.
    /// </summary>
    internal static bool IsTheme(ZipArchive archive)
    {
        bool InFolder(ZipArchiveEntry e) => e.FullName.StartsWith(ThemeId + "/", StringComparison.OrdinalIgnoreCase);
        bool OwnFile(ZipArchiveEntry e) => !e.FullName.Contains('/') && e.FullName.StartsWith(ThemeFilePrefix, StringComparison.OrdinalIgnoreCase);
        bool FileType(ZipArchiveEntry e) => e.Name.Length == 0 || ThemeFileTypes.Contains(Path.GetExtension(e.Name), StringComparer.OrdinalIgnoreCase);
        return archive.Entries.Any(InFolder) && archive.Entries.Sum(e => e.Length) < MaxThemeBytes
            && archive.Entries.All(e => (InFolder(e) || OwnFile(e)) && FileType(e) && !e.FullName.Contains("..") && !e.FullName.Contains('\\'));
    }

    /// <summary>In the user's themes folder, which OBS also reads and its updates don't replace.</summary>
    internal static void InstallTheme(ObsInstall o, byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip));
        if (!IsTheme(archive)) throw new InvalidOperationException(Txt.ObsThemeInvalid);
        Directory.CreateDirectory(Themes(o));
        archive.ExtractToDirectory(Themes(o), overwriteFiles: true);
        IniFile.Set(User(o), "Appearance", "Theme", ThemeId);
    }

    /// <summary>The .sentinel folder makes OBS ask about safe mode after an unclean exit, which would stop an unattended start.</summary>
    internal static string ReplayCommand(ObsInstall o) =>
        $"cmd.exe /c del \"{Path.Combine(o.Data, ".sentinel")}\" /f /q & start \"\" /d \"{Path.GetDirectoryName(o.Executable)}\" obs64.exe --startreplaybuffer --minimize-to-tray";

    // ---------- profiles ----------

    public static string ProfileFolder(ObsInstall o, ObsProfile p) => Path.Combine(o.Data, "basic", "profiles", p.Name);

    public static string? ActiveProfile(ObsInstall o) => IniFile.Get(User(o), "Basic", "Profile");

    /// <summary>Writes the profile's folder and makes it the active one. Only this folder is replaced; other profiles stay.</summary>
    public static void CreateProfile(ObsInstall o, ObsProfile p)
    {
        if (!ObsProfile.Resolutions.ContainsKey(p.Height) || p.Fps is < ObsProfile.MinFps or > ObsProfile.MaxFps) throw new InvalidOperationException(Txt.ObsInvalidProfile);
        var folder = ProfileFolder(o, p);
        Directory.CreateDirectory(folder);
        var utf8 = new UTF8Encoding(false);
        // Creating the same profile again replaces it; changes made in OBS stay in the copy.
        var ini = Path.Combine(folder, "basic.ini");
        if (File.Exists(ini)) File.Copy(ini, ini + ".bak", overwrite: true);
        File.WriteAllText(ini, p.BasicIni(), utf8);
        File.WriteAllText(Path.Combine(folder, "recordEncoder.json"), p.RecordEncoderJson(), utf8);
        File.WriteAllText(Path.Combine(folder, "streamEncoder.json"), p.StreamEncoderJson(), utf8);
        // FirstRun: without it, OBS opens its auto-configuration wizard over the profile on a first launch.
        IniFile.Set(User(o), "General", "FirstRun", "true");
        IniFile.Set(User(o), "Basic", "Profile", p.Name);
        IniFile.Set(User(o), "Basic", "ProfileDir", p.Name);
    }

    /// <summary>The installed video card's vendor picks the encoder; NVIDIA first, then AMD, otherwise Intel Quick Sync.</summary>
    public static ObsEncoder DefaultEncoder() => EncoderFor(VideoAdapters());

    internal static ObsEncoder EncoderFor(IEnumerable<string> adapters)
    {
        var names = adapters.ToList();
        if (names.Any(n => n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || n.Contains("GeForce", StringComparison.OrdinalIgnoreCase))) return ObsEncoder.Nvenc;
        if (names.Any(n => n.Contains("AMD", StringComparison.OrdinalIgnoreCase) || n.Contains("Radeon", StringComparison.OrdinalIgnoreCase))) return ObsEncoder.Amf;
        return ObsEncoder.Qsv;
    }

    static IEnumerable<string> VideoAdapters()
    {
        using var cls = Registry.LocalMachine.OpenSubKey(DisplayClass);
        foreach (var sub in cls?.GetSubKeyNames() ?? Array.Empty<string>())
        {
            string? name = null;
            try { using var key = cls!.OpenSubKey(sub); name = key?.GetValue("DriverDesc") as string; }
            catch (System.Security.SecurityException) { } // the Properties subkey isn't readable and has no name
            if (name is not null) yield return name;
        }
    }

    // ---------- scene collection ----------

    internal static string SceneJson()
    {
        static JsonObject Audio(string name, string id, JsonArray? filters = null)
        {
            var source = new JsonObject
            {
                ["name"] = name, ["uuid"] = Guid.NewGuid().ToString(), ["id"] = id, ["versioned_id"] = id,
                ["settings"] = new JsonObject { ["device_id"] = "default" }, ["mixers"] = 255, ["volume"] = 1.0, ["balance"] = 0.5, ["enabled"] = true,
            };
            if (filters is not null) source["filters"] = filters;
            return source;
        }
        var gate = new JsonObject
        {
            ["name"] = "Noise Gate", ["uuid"] = Guid.NewGuid().ToString(), ["id"] = "noise_gate_filter", ["versioned_id"] = "noise_gate_filter",
            ["settings"] = new JsonObject(), ["enabled"] = true,
        };
        var scene = new JsonObject
        {
            ["name"] = "Scene", ["uuid"] = Guid.NewGuid().ToString(), ["id"] = "scene", ["versioned_id"] = "scene",
            ["settings"] = new JsonObject { ["id_counter"] = 0, ["custom_size"] = false, ["items"] = new JsonArray() }, ["enabled"] = true,
        };
        return new JsonObject
        {
            ["name"] = SceneCollection, ["current_scene"] = "Scene", ["current_program_scene"] = "Scene",
            ["scene_order"] = new JsonArray(new JsonObject { ["name"] = "Scene" }),
            ["sources"] = new JsonArray(scene), ["groups"] = new JsonArray(), ["transitions"] = new JsonArray(),
            ["DesktopAudioDevice1"] = Audio("Desktop Audio", "wasapi_output_capture"),
            ["AuxAudioDevice1"] = Audio("Mic/Aux", "wasapi_input_capture", new JsonArray(gate)),
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    static void CreateSceneCollection(ObsInstall o)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SceneFile(o))!);
        // An existing collection may already have the user's sources.
        if (!File.Exists(SceneFile(o))) File.WriteAllText(SceneFile(o), SceneJson(), new UTF8Encoding(false));
        IniFile.Set(User(o), "Basic", "SceneCollection", SceneCollection);
        IniFile.Set(User(o), "Basic", "SceneCollectionFile", SceneCollection);
    }
}
