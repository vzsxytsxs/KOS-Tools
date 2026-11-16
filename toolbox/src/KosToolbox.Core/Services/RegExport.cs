using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace KosToolbox.Core.Services;

/// <summary>
/// .reg export (Regedit 5.00 format) of a key and its subkeys, in place of "reg.exe export".
/// Types and bytes come from RegEnumValue, so values of any type are exported exactly.
/// </summary>
public static class RegExport
{
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegEnumValueW")]
    static extern int RegEnumValue(SafeRegistryHandle key, uint index, StringBuilder name, ref uint nameLength, IntPtr reserved,
        out uint type, byte[]? data, ref uint dataLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegQueryInfoKeyW")]
    static extern int RegQueryInfoKey(SafeRegistryHandle key, IntPtr className, IntPtr classLength, IntPtr reserved, out uint subKeys,
        out uint maxSubKeyLength, out uint maxClassLength, out uint values, out uint maxValueNameLength, out uint maxValueLength,
        IntPtr securityDescriptor, IntPtr lastWriteTime);

    /// <summary>Writes the whole file. Returns how many subkeys couldn't be read (listed in comments at the top).</summary>
    public static int Export(RegistryHive hive, string path, TextWriter writer)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.OpenSubKey(path) ?? throw new InvalidOperationException($"Key not found: {path}");
        var body = new StringBuilder();
        var skipped = new List<string>();
        Write(key, $@"{HiveName(hive)}\{path}", body, skipped);
        writer.Write("Windows Registry Editor Version 5.00\r\n");
        foreach (var s in skipped) writer.Write($"; Skipped (couldn't be read): {s}\r\n");
        writer.Write(body.ToString());
        return skipped.Count;
    }

    /// <summary>The .reg line for a single value (backup before deleting the value); null if it doesn't exist.</summary>
    internal static string? ValueLine(RegistryKey key, string name)
    {
        foreach (var (n, type, data) in Values(key))
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return FormatValue(n, type, data);
        return null;
    }

    internal static string HiveName(RegistryHive hive) => hive switch
    {
        RegistryHive.LocalMachine => "HKEY_LOCAL_MACHINE",
        RegistryHive.CurrentUser => "HKEY_CURRENT_USER",
        RegistryHive.ClassesRoot => "HKEY_CLASSES_ROOT",
        RegistryHive.Users => "HKEY_USERS",
        _ => throw new ArgumentOutOfRangeException(nameof(hive)),
    };

    static void Write(RegistryKey key, string fullName, StringBuilder sb, List<string> skipped)
    {
        sb.Append("\r\n[").Append(fullName).Append("]\r\n");
        foreach (var (name, type, data) in Values(key)) sb.Append(FormatValue(name, type, data)).Append("\r\n");
        foreach (var sub in key.GetSubKeyNames())
        {
            try
            {
                using var child = key.OpenSubKey(sub);
                if (child is null) { skipped.Add($@"{fullName}\{sub}"); continue; }
                Write(child, $@"{fullName}\{sub}", sb, skipped);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                skipped.Add($@"{fullName}\{sub}");
            }
        }
    }

    static IEnumerable<(string Name, uint Type, byte[] Data)> Values(RegistryKey key)
    {
        if (RegQueryInfoKey(key.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out _, out _, out _, out var count, out var maxName, out var maxData,
                IntPtr.Zero, IntPtr.Zero) != 0) yield break;
        for (uint i = 0; i < count; i++)
        {
            var name = new StringBuilder((int)maxName + 2);
            uint nameLength = (uint)name.Capacity;
            var data = new byte[Math.Max(maxData, 1)];
            uint dataLength = (uint)data.Length;
            if (RegEnumValue(key.Handle, i, name, ref nameLength, IntPtr.Zero, out var type, data, ref dataLength) != 0) continue;
            yield return (name.ToString(0, (int)nameLength), type, data[..(int)dataLength]);
        }
    }

    /// <summary>A "name"=value line, wrapping hexadecimal data at ~80 columns like Regedit.</summary>
    internal static string FormatValue(string name, uint type, byte[] data)
    {
        var prefix = name.Length == 0 ? "@=" : $"\"{Escape(name)}\"=";
        switch (type)
        {
            case 1 /*REG_SZ*/ when IsPlainString(data, out var text):
                return prefix + "\"" + Escape(text) + "\"";
            case 4 /*REG_DWORD*/ when data.Length == 4:
                return prefix + "dword:" + BitConverter.ToUInt32(data).ToString("x8");
            case 3 /*REG_BINARY*/:
                return Hex(prefix + "hex:", data);
            default:
                return Hex(prefix + $"hex({type:x}):", data);
        }
    }

    static bool IsPlainString(byte[] data, out string text)
    {
        text = "";
        if (data.Length % 2 != 0) return false;
        var s = Encoding.Unicode.GetString(data);
        if (s.EndsWith('\0')) s = s[..^1];
        if (s.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0) return false; // Regedit wouldn't import the line
        text = s;
        return true;
    }

    static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    static string Hex(string head, byte[] data)
    {
        var sb = new StringBuilder(head);
        var col = head.Length;
        for (var i = 0; i < data.Length; i++)
        {
            var part = data[i].ToString("x2") + (i < data.Length - 1 ? "," : "");
            if (col + part.Length > 79 && i < data.Length - 1)
            {
                sb.Append("\\\r\n  ");
                col = 2;
            }
            sb.Append(part);
            col += part.Length;
        }
        return sb.ToString();
    }
}
