using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KosGovernor.Views;

/// <summary>
/// File icons through the shell (SHGetFileInfo), read off the UI thread and stored by path.
/// Only reads the icon; never runs the file.
/// </summary>
public static class IconCache
{
    const uint ShgfiIcon = 0x100, ShgfiLargeIcon = 0x0, ShgfiSmallIcon = 0x1, ShgfiUseFileAttributes = 0x10;

    static readonly ConcurrentDictionary<(string, bool), Task<ImageSource?>> Cache = new();
    static readonly SemaphoreSlim Gate = new(4);

    /// <summary>Generic program icon (svchost, processes without a readable path).</summary>
    public static Task<ImageSource?> GenericAsync() => GetAsync("*.exe", large: false);

    public static Task<ImageSource?> GetAsync(string? path, bool large)
    {
        if (string.IsNullOrWhiteSpace(path)) return Task.FromResult<ImageSource?>(null);
        return Cache.GetOrAdd((path.ToLowerInvariant(), large), _ => Task.Run(async () =>
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try { return Load(path, large) ?? (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Load("x.exe", large, generic: true) : null); }
            finally { Gate.Release(); }
        }));
    }

    // .exe without its own icon: SHGetFileInfo on a thread-pool (MTA) thread fails; the generic one comes from the file type.
    static ImageSource? Load(string path, bool large, bool generic = false)
    {
        if (!generic && !File.Exists(path)) return null;
        var info = new ShFileInfo();
        if (SHGetFileInfo(path, generic ? 0x80u : 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon | (large ? ShgfiLargeIcon : ShgfiSmallIcon) | (generic ? ShgfiUseFileAttributes : 0)) == IntPtr.Zero
            || info.hIcon == IntPtr.Zero) return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze(); // used on the UI thread
            return source;
        }
        catch { return null; }
        finally { DestroyIcon(info.hIcon); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);
}
