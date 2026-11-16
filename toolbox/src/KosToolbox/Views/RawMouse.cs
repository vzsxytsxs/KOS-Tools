using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace KosToolbox.Views;

/// <summary>
/// Raw mouse reports for the main window (WM_INPUT), stamped with the performance counter inside the window procedure, before
/// WPF handles anything. Registered only while it exists and only for the foreground window (Windows lowers the raw input
/// rate of background windows). F2 is reported too, to stop a log as MouseTester does.
/// </summary>
internal sealed class RawMouse : IDisposable
{
    const int WmInput = 0x00FF, WmKeyDown = 0x0100, VkF2 = 0x71;
    const uint RidInput = 0x10000003, RidevRemove = 0x1, MouseMoveAbsolute = 0x1;
    static readonly int Header = Marshal.SizeOf<RawInputHeader>();

    readonly HwndSource _source;
    readonly IntPtr _buffer = Marshal.AllocHGlobal(64);

    /// <summary>Timestamp (Stopwatch ticks), relative X and Y counts, button flags (RI_MOUSE_*).</summary>
    public event Action<long, int, int, ushort>? Report;
    public event Action? F2;

    public RawMouse(Window window)
    {
        _source = (HwndSource)PresentationSource.FromVisual(window) ?? throw new InvalidOperationException("The window has no handle yet.");
        _source.AddHook(Hook);
        if (!Register(_source.Handle, 0))
        {
            _source.RemoveHook(Hook);
            Marshal.FreeHGlobal(_buffer);
            throw new InvalidOperationException($"Windows didn't register the raw mouse input ({Marshal.GetLastWin32Error()}).");
        }
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmInput)
        {
            var stamp = Stopwatch.GetTimestamp();
            var size = 64u;
            // RAWINPUT on x64: 24-byte header, then RAWMOUSE (usFlags, button flags at +4, lLastX at +12, lLastY at +16).
            if (GetRawInputData(lParam, RidInput, _buffer, ref size, (uint)Header) is > 0 and not uint.MaxValue && Marshal.ReadInt32(_buffer) == 0)
            {
                var flags = (ushort)Marshal.ReadInt16(_buffer, Header);
                if ((flags & MouseMoveAbsolute) == 0)
                    Report?.Invoke(stamp, Marshal.ReadInt32(_buffer, Header + 12), Marshal.ReadInt32(_buffer, Header + 16), (ushort)Marshal.ReadInt16(_buffer, Header + 4));
            }
        }
        else if (msg == WmKeyDown && wParam == VkF2) F2?.Invoke();
        return IntPtr.Zero; // WPF passes WM_INPUT on to DefWindowProc, which frees the input
    }

    static bool Register(IntPtr hwnd, uint flags)
    {
        var device = new RawInputDevice { UsagePage = 1, Usage = 2, Flags = flags, Target = hwnd }; // generic desktop › mouse
        return RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<RawInputDevice>());
    }

    public void Dispose()
    {
        Register(IntPtr.Zero, RidevRemove);
        _source.RemoveHook(Hook);
        Marshal.FreeHGlobal(_buffer);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RawInputDevice { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }

    [StructLayout(LayoutKind.Sequential)]
    struct RawInputHeader { public uint Type, Size; public IntPtr Device, WParam; }

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref uint size, uint headerSize);
}
