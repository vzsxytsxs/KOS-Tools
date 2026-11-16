using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KosToolbox.Views;

/// <summary>
/// File icons from the shell (SHGetFileInfo), read off the UI thread and cached by path.
/// It only reads the icon; it never runs the file.
/// </summary>
public static class IconCache
{
    const uint ShgfiIcon = 0x100, ShgfiLargeIcon = 0x0, ShgfiSmallIcon = 0x1;

    static readonly ConcurrentDictionary<(string, bool), Task<ImageSource?>> Cache = new();
    static readonly SemaphoreSlim Gate = new(4);

    public static Task<ImageSource?> GetAsync(string? path, bool large)
    {
        if (string.IsNullOrWhiteSpace(path)) return Task.FromResult<ImageSource?>(null);
        return Cache.GetOrAdd((path.ToLowerInvariant(), large), _ => Task.Run(async () =>
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try { return Load(path, large); }
            finally { Gate.Release(); }
        }));
    }

    static ImageSource? Load(string path, bool large)
    {
        if (!File.Exists(path)) return null;
        var info = new ShFileInfo();
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon | (large ? ShgfiLargeIcon : ShgfiSmallIcon)) == IntPtr.Zero
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
