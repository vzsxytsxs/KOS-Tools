using System.Runtime.InteropServices;

namespace KosToolbox.Core.Gpu;

/// <summary>
/// Scaling as the NVIDIA control panel sets it, through the public NvAPI_DISP_GetDisplayConfig/SetDisplayConfig (NV_SCALING:
/// "Balanced" = the display scales, "Force GPU" = the GPU scales, plus integer scaling, which Windows' own API lacks).
/// </summary>
internal static partial class Nvapi
{
    const uint GetDisplayConfigId = 0x11ABCCF8, SetDisplayConfigId = 0x5D8CF8DE;
    // NV_DISPLAYCONFIG_PATH_INFO_V2 (48 bytes on x64), _PATH_TARGET_INFO_V2 (24), _SOURCE_MODE_INFO_V1 (32), _PATH_ADVANCED_TARGET_INFO_V1 (128).
    const int PathSize = 48, PathVersion = 2, TargetSize = 24, SourceSize = 32, DetailsSize = 128, DetailsVersion = 1;
    const int ScalingAt = 8; // NV_DISPLAYCONFIG_PATH_ADVANCED_TARGET_INFO: version, rotation, scaling
    const uint SaveToPersistence = 1;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetConfigFn(ref uint count, IntPtr paths);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SetConfigFn(uint count, IntPtr paths, uint flags);

    /// <summary>The whole display configuration in native memory (paths → targets → details, and source modes); freed on Dispose.</summary>
    sealed class DisplayConfig : IDisposable
    {
        readonly List<IntPtr> _blocks = new();
        public IntPtr Paths { get; private set; }
        public uint Count { get; private set; }

        IntPtr Alloc(int size)
        {
            var p = Marshal.AllocHGlobal(size);
            _blocks.Add(p);
            for (var i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
            return p;
        }

        public static DisplayConfig Read()
        {
            var c = new DisplayConfig();
            try
            {
                var get = Function<GetConfigFn>(GetDisplayConfigId);
                uint n = 0;
                Check(get(ref n, IntPtr.Zero), "NvAPI_DISP_GetDisplayConfig");
                if (n == 0) throw new InvalidOperationException("NVAPI reported no displays.");
                c.Paths = c.Alloc(PathSize * (int)n);
                for (var i = 0; i < n; i++) Marshal.WriteInt32(c.Paths, i * PathSize, PathSize | PathVersion << 16);
                Check(get(ref n, c.Paths), "NvAPI_DISP_GetDisplayConfig");
                // Second pass told how many targets each path has: give each one its details and the path its source mode.
                for (var i = 0; i < n; i++)
                {
                    var path = c.Paths + i * PathSize;
                    var count = Marshal.ReadInt32(path, 8);
                    var targets = c.Alloc(TargetSize * Math.Max(1, count));
                    for (var k = 0; k < count; k++)
                    {
                        var details = c.Alloc(DetailsSize);
                        Marshal.WriteInt32(details, 0, DetailsSize | DetailsVersion << 16);
                        Marshal.WriteIntPtr(targets, k * TargetSize + 8, details);
                    }
                    Marshal.WriteIntPtr(path, 16, targets);
                    Marshal.WriteIntPtr(path, 24, c.Alloc(SourceSize));
                }
                Check(get(ref n, c.Paths), "NvAPI_DISP_GetDisplayConfig");
                c.Count = n;
                return c;
            }
            catch
            {
                c.Dispose();
                throw;
            }
        }

        /// <summary>Details of the target with this display id (NV_DISPLAYCONFIG_PATH_ADVANCED_TARGET_INFO).</summary>
        public IntPtr Details(uint displayId)
        {
            for (var i = 0; i < Count; i++)
            {
                var path = Paths + i * PathSize;
                var targets = Marshal.ReadIntPtr(path, 16);
                for (var k = 0; k < Marshal.ReadInt32(path, 8); k++)
                    if ((uint)Marshal.ReadInt32(targets, k * TargetSize) == displayId)
                        return Marshal.ReadIntPtr(targets, k * TargetSize + 8);
            }
            throw new InvalidOperationException("NVAPI doesn't list this display.");
        }

        public void Dispose()
        {
            foreach (var p in _blocks) Marshal.FreeHGlobal(p);
            _blocks.Clear();
        }
    }

    /// <summary>NV_SCALING of the monitor (1-3, 5-8; 0 = default).</summary>
    public static int ReadScaling(string gdiName)
    {
        lock (Gate)
        {
            var id = DisplayId(gdiName);
            using var c = DisplayConfig.Read();
            return Marshal.ReadInt32(c.Details(id), ScalingAt);
        }
    }

    /// <summary>Applies an NV_SCALING to the monitor and saves it (the control panel's Adjust desktop size and position).</summary>
    public static void SetScaling(string gdiName, int scaling)
    {
        if (scaling is not (1 or 2 or 3 or 5 or 6 or 7 or 8)) throw new ArgumentOutOfRangeException(nameof(scaling));
        lock (Gate)
        {
            var id = DisplayId(gdiName);
            using var c = DisplayConfig.Read();
            Marshal.WriteInt32(c.Details(id), ScalingAt, scaling);
            Check(Function<SetConfigFn>(SetDisplayConfigId)(c.Count, c.Paths, SaveToPersistence), "NvAPI_DISP_SetDisplayConfig");
        }
    }
}
