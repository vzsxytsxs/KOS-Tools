using KosToolbox.Core.Execution;

namespace KosToolbox.Core.Settings;

/// <summary>System CFG state; writing is left to the reviewed KOS script.</summary>
public sealed class CfgSetting : ISetting
{
    public string Key => "mitigation:system-cfg";
    public string Display => "System Control Flow Guard (CFG)";
    public bool MachineWide => true;
    public string? Unavailable() => null;

    public string? Read()
    {
        var exe = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        var result = ProcessRunner.Run(exe, "-NoProfile -NonInteractive -Command \"(Get-ProcessMitigation -System).CFG.Enable.ToString()\"");
        if (result.ExitCode != 0) throw new InvalidOperationException($"Could not read system CFG: {result.Output.Trim()}");
        return Parse(result.Output);
    }

    internal static string Parse(string output) => output.Trim().ToUpperInvariant() switch
    {
        "OFF" => "off",
        "ON" or "NOTSET" => "on", // without an override, Windows applies its default
        _ => throw new InvalidOperationException($"Unknown system CFG state: {output.Trim()}"),
    };

    public void Write(string? value) => throw new NotSupportedException("CFG is changed by the reviewed KOS script.");
}
