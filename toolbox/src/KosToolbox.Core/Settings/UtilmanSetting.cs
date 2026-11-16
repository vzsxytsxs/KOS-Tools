using System.Diagnostics;
using System.Security.Cryptography;

namespace KosToolbox.Core.Settings;

/// <summary>Recognizes the original Utilman and the identical copy of cmd.exe.</summary>
public sealed class UtilmanSetting : ISetting
{
    static string Utilman => Path.Combine(Environment.SystemDirectory, "Utilman.exe");
    static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    public string Key => "file:system32-utilman";
    public string Display => @"System32\Utilman.exe";
    public bool MachineWide => true;
    public string? Unavailable() => File.Exists(Utilman) && File.Exists(Cmd) ? null : "Utilman.exe or cmd.exe is missing.";

    public string? Read()
    {
        using (var utilman = File.OpenRead(Utilman))
        using (var cmd = File.OpenRead(Cmd))
            if (SHA256.HashData(utilman).SequenceEqual(SHA256.HashData(cmd))) return "on";
        return Parse(FileVersionInfo.GetVersionInfo(Utilman).OriginalFilename);
    }

    /// <summary>In System32 the name comes from the .mui ("utilman2.exe.mui"). A copy of cmd.exe from before a Windows
    /// update no longer has the current cmd.exe hash, but is still the prompt.</summary>
    internal static string Parse(string? originalFilename)
    {
        var name = (originalFilename ?? "").Trim().ToLowerInvariant();
        if (name.EndsWith(".mui")) name = name[..^4];
        return name switch
        {
            "utilman.exe" or "utilman2.exe" => "off",
            "cmd.exe" => "on",
            _ => throw new InvalidOperationException($"Utilman.exe is neither the original accessibility app nor a copy of cmd.exe ({originalFilename})."),
        };
    }

    public void Write(string? value) => throw new NotSupportedException("Utilman is changed by the reviewed KOS script.");
}
