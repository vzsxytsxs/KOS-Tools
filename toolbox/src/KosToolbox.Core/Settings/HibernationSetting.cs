using KosToolbox.Core.Execution;
using KosToolbox.Core.Text;
using Microsoft.Win32;

namespace KosToolbox.Core.Settings;

/// <summary>
/// Hibernation: reads HibernateEnabled, which powercfg maintains, and writes with "powercfg /hibernate on|off" (which also creates
/// or deletes hiberfil.sys), like the KOS Configure Powersavings.bat. A null value = HibernateEnabled absent.
/// </summary>
public sealed class HibernationSetting : ISetting
{
    const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Power";

    public string Key => "power:hibernate";
    public string Display => @"HKLM\" + PowerKey + " → HibernateEnabled (powercfg /hibernate)";
    public bool MachineWide => true;
    public string? Unavailable() => null;

    public string? Read()
    {
        using var key = Registry.LocalMachine.OpenSubKey(PowerKey);
        return key?.GetValue("HibernateEnabled") is int v ? (v == 0 ? "0" : "1") : null;
    }

    public void Write(string? value)
    {
        var arg = value switch { "1" => "on", "0" => "off", _ => throw new ArgumentException($"Invalid hibernation value: {value}") };
        // Turning it on reserves hiberfil.sys (proportional to RAM): needs more time than the 20 s default of the reads.
        var powercfg = Path.Combine(Environment.SystemDirectory, "powercfg.exe");
        var r = ProcessRunner.Run(powercfg, "/hibernate " + arg, TimeSpan.FromMinutes(2));
        if (r.ExitCode != 0) throw new InvalidOperationException(Txt.HibernateFailed(arg, r.ExitCode, r.Output.Trim()));
        // A reduced hiberfil.sys only serves fast startup; Hibernate itself needs the full one.
        if (arg == "on") r = ProcessRunner.Run(powercfg, "/hibernate /type full", TimeSpan.FromMinutes(2));
        if (r.ExitCode != 0) throw new InvalidOperationException(Txt.HibernateFailed("/type full", r.ExitCode, r.Output.Trim()));
    }
}
