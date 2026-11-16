using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace KosToolbox.Core.Interop;

/// <summary>Contents of a .lnk shortcut. Reading the shortcut runs nothing.</summary>
public sealed record ShortcutInfo(string? Target, string? Arguments, string? WorkingDirectory, string? Description, string? IconLocation);

public static class ShellLink
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class CShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idl);
        void SetIDList(IntPtr idl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    /// <summary>Creates or replaces a .lnk that opens <paramref name="target"/> without arguments.</summary>
    public static void Create(string path, string target, string workingDirectory, string icon)
    {
        var link = (IShellLinkW)new CShellLink();
        try
        {
            link.SetPath(target);
            link.SetWorkingDirectory(workingDirectory);
            link.SetIconLocation(icon, 0);
            ((IPersistFile)link).Save(path, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    /// <summary>Reads target, arguments and working folder. Does not call Resolve (which may search for the target and modify the shortcut).</summary>
    public static ShortcutInfo Read(string path)
    {
        var link = (IShellLinkW)new CShellLink();
        try
        {
            ((IPersistFile)link).Load(path, 0 /*STGM_READ*/);
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            var target = sb.ToString();
            sb.Clear(); link.GetArguments(sb, sb.Capacity); var args = sb.ToString();
            sb.Clear(); link.GetWorkingDirectory(sb, sb.Capacity); var dir = sb.ToString();
            sb.Clear(); link.GetDescription(sb, sb.Capacity); var desc = sb.ToString();
            sb.Clear(); link.GetIconLocation(sb, sb.Capacity, out var index); var icon = sb.ToString();
            return new ShortcutInfo(Empty(target), Empty(args), Empty(dir), Empty(desc), icon.Length == 0 ? null : $"{icon},{index}");
        }
        finally { Marshal.FinalReleaseComObject(link); }

        static string? Empty(string s) => s.Length == 0 ? null : s;
    }
}
