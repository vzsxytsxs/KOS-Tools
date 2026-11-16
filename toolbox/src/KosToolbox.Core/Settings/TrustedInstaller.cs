using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using Microsoft.Win32;

namespace KosToolbox.Core.Settings;

/// <summary>
/// Fallback for protected keys, same as the KOS scripts: reg.exe run through %windir%\PowerRun.exe.
/// The arguments are built from the catalog (path, name, type, value), never from free text.
/// </summary>
static class TrustedInstaller
{
    static string PowerRun => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "PowerRun.exe");
    public static bool Available => Elevation.IsAdmin && File.Exists(PowerRun);

    public static void RegWrite(RegistrySetting s, string? value)
    {
        var reg = Path.Combine(Environment.SystemDirectory, "reg.exe");
        var key = $@"HKLM\{s.Path}";
        var args = value is null
            ? $"/SW:0 {ProcessRunner.Quote(reg)} delete {ProcessRunner.Quote(key)} /v {ProcessRunner.Quote(s.Name)} /f"
            : $"/SW:0 {ProcessRunner.Quote(reg)} add {ProcessRunner.Quote(key)} /v {ProcessRunner.Quote(s.Name)} /t {RegType(s.Kind)} /d {ProcessRunner.Quote(value)} /f";
        Log.Info($"Access denied on {s.Display}; writing through PowerRun (TrustedInstaller).");
        ProcessRunner.Run(PowerRun, args);
        // PowerRun may return before reg.exe finishes; waits for the value to appear (like the scripts' checkStart).
        for (var i = 0; i < 10; i++)
        {
            if (s.ValueEquals(s.Read(), value)) return;
            Thread.Sleep(500);
        }
        throw new UnauthorizedAccessException($"Couldn't write {s.Display}, not even as TrustedInstaller.");
    }

    /// <summary>Writes a binary value under HKLM (a key only SYSTEM may write, such as the NVIDIA driver's display database).</summary>
    public static void RegWriteBinary(string path, string name, byte[] value)
    {
        var reg = Path.Combine(Environment.SystemDirectory, "reg.exe");
        Log.Info($@"Writing HKLM\{path}\{name} through PowerRun (TrustedInstaller).");
        ProcessRunner.Run(PowerRun, $@"/SW:0 {ProcessRunner.Quote(reg)} add {ProcessRunner.Quote($@"HKLM\{path}")} /v {ProcessRunner.Quote(name)} /t REG_BINARY /d {Convert.ToHexString(value)} /f");
        for (var i = 0; i < 10; i++)
        {
            using (var key = Registry.LocalMachine.OpenSubKey(path)) if (key?.GetValue(name) is byte[] now && now.AsSpan().SequenceEqual(value)) return;
            Thread.Sleep(500);
        }
        throw new UnauthorizedAccessException($@"Couldn't write HKLM\{path}\{name}, not even as TrustedInstaller.");
    }

    /// <summary>Deletes HKLM\<paramref name="path"/> and everything under it (a key the KOS deletes and Windows put back).</summary>
    public static void RegDeleteKey(string path)
    {
        var reg = Path.Combine(Environment.SystemDirectory, "reg.exe");
        Log.Info($@"Access denied deleting HKLM\{path}; deleting through PowerRun (TrustedInstaller).");
        ProcessRunner.Run(PowerRun, $@"/SW:0 {ProcessRunner.Quote(reg)} delete {ProcessRunner.Quote($@"HKLM\{path}")} /f");
        for (var i = 0; i < 10; i++)
        {
            using (var key = Registry.LocalMachine.OpenSubKey(path)) if (key is null) return;
            Thread.Sleep(500);
        }
        throw new UnauthorizedAccessException($@"Couldn't delete HKLM\{path}, not even as TrustedInstaller.");
    }

    /// <summary>
    /// Deletes a system file or folder TrustedInstaller owns, as the KOS image build does (del /f, rmdir /s /q: a link inside is
    /// removed, not followed). The caller checks that no link is on the path.
    /// </summary>
    public static void DeletePath(string path, bool folder)
    {
        // cmd reads % ! & | < > ^ even inside quotes or without spaces: such a path is never put on its command line.
        if (path.IndexOfAny(new[] { '"', '%', '!', '&', '|', '<', '>', '^' }) >= 0) throw new InvalidOperationException($"Unexpected path: {path}");
        // PowerRun is 32-bit but starts System32's 64-bit cmd (checked: its child from System32 is 64-bit), so no SysWOW64 redirect.
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        Log.Info($"Deleting {path} through PowerRun (TrustedInstaller).");
        ProcessRunner.Run(PowerRun, $"/SW:0 {ProcessRunner.Quote(cmd)} /d /c {(folder ? "rmdir /s /q" : "del /f /q")} \"{path}\"");
        // PowerRun may return before cmd finishes; a big folder (Edge) takes a while.
        for (var i = 0; i < (folder ? 240 : 20); i++)
        {
            if (folder ? !Directory.Exists(path) : !File.Exists(path)) return;
            Thread.Sleep(500);
        }
        throw new IOException($"Couldn't delete {path}, not even as TrustedInstaller (in use?).");
    }

    static string RegType(RegistryValueKind k) => k switch
    {
        RegistryValueKind.DWord => "REG_DWORD",
        RegistryValueKind.QWord => "REG_QWORD",
        RegistryValueKind.ExpandString => "REG_EXPAND_SZ",
        RegistryValueKind.Binary => "REG_BINARY",
        RegistryValueKind.MultiString => "REG_MULTI_SZ",
        _ => "REG_SZ",
    };
}
