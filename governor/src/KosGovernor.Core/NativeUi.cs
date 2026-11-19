using System.Runtime.InteropServices;

namespace KosGovernor.Core;

/// <summary>The Windows API the engine's own windows (status window, firewall popup) are made with: window class, GDI, DWM.</summary>
internal static class NativeUi
{
    internal delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WndClassEx
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PaintStruct
    {
        public IntPtr hdc;
        public int fErase;
        public Rect rcPaint;
        public int fRestore, fIncUpdate;
        public long Reserved0, Reserved1, Reserved2, Reserved3; // rgbReserved[32]
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern ushort RegisterClassEx(ref WndClassEx cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool AdjustWindowRectExForDpi(ref Rect rect, uint style, bool menu, uint exStyle, uint dpi);
    [DllImport("user32.dll")] internal static extern bool SystemParametersInfo(uint action, uint param, ref Rect rect, uint winIni);
    [DllImport("user32.dll")] internal static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] internal static extern bool KillTimer(IntPtr hwnd, IntPtr id);
    [DllImport("user32.dll")] internal static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] internal static extern IntPtr BeginPaint(IntPtr hwnd, out PaintStruct ps);
    [DllImport("user32.dll")] internal static extern bool EndPaint(IntPtr hwnd, ref PaintStruct ps);
    [DllImport("user32.dll")] internal static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] internal static extern int FillRect(IntPtr hdc, ref Rect rect, IntPtr brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int DrawText(IntPtr hdc, string text, int count, ref Rect rect, uint format);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] internal static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreatePen(int style, int width, uint color);
    [DllImport("gdi32.dll")] internal static extern IntPtr GetStockObject(int obj);
    [DllImport("gdi32.dll")] internal static extern uint SetTextColor(IntPtr hdc, uint color);
    [DllImport("gdi32.dll")] internal static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] internal static extern bool Ellipse(IntPtr hdc, int l, int t, int r, int b);
    [DllImport("gdi32.dll")] internal static extern bool MoveToEx(IntPtr hdc, int x, int y, IntPtr old);
    [DllImport("gdi32.dll")] internal static extern bool LineTo(IntPtr hdc, int x, int y);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut,
        uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitch, string face);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string? name);

    [StructLayout(LayoutKind.Sequential)]
    internal struct TrackMouse { public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }

    [DllImport("user32.dll")] internal static extern bool TrackMouseEvent(ref TrackMouse track);
    [DllImport("user32.dll")] internal static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] internal static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int w, int h, uint step, IntPtr brush, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] internal static extern uint ExtractIconEx(string file, int index, out IntPtr large, IntPtr small, uint count);
    [DllImport("gdi32.dll")] internal static extern bool RoundRect(IntPtr hdc, int l, int t, int r, int b, int w, int h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int MessageBox(IntPtr hwnd, string text, string caption, uint type);
}
