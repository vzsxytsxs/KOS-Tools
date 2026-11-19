using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using static KosGovernor.Core.NativeUi;

namespace KosGovernor.Core;

/// <summary>
/// KOS Firewall's question in the corner above the tray: a blocked program, Allow or Block; closing it leaves the program
/// blocked and it's asked again later. Plain Windows API like the status window, on the tray's thread. One question at a time,
/// the others wait in line. It never takes the focus, so a game in the foreground keeps it.
/// </summary>
internal sealed class FirewallPopup(Action<FirewallAsk, bool?> answer)
{
    // Logical pixels at 96 DPI
    const int Width = 420, Height = 172, Pad = 18, ButtonWidth = 104, ButtonHeight = 32, IconSize = 32;
    const uint Background = 0x00141414, Text = 0x00F5F5F5, Secondary = 0x00A0A0A0, Border = 0x00333333, Hover = 0x00262626, Dark = 0x000D0D0D;
    const string ClassName = "KosFirewallPopup";
    const int HitAllow = 0, HitBlock = 1, HitClose = 2;

    readonly Queue<FirewallAsk> _waiting = new();
    WndProc? _proc;
    bool _registered, _tracking;
    IntPtr _hwnd, _icon;
    FirewallAsk? _current;
    int _hover = -1;

    /// <summary>Shows the question now, or after the ones before it (tray thread).</summary>
    public void Add(FirewallAsk ask)
    {
        if (_current is not null)
        {
            _waiting.Enqueue(ask);
            InvalidateRect(_hwnd, IntPtr.Zero, false);
            return;
        }
        Open(ask);
    }

    public void Close()
    {
        _waiting.Clear();
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
    }

    void Open(FirewallAsk ask)
    {
        var instance = GetModuleHandle(null);
        if (!_registered)
        {
            _proc = Proc; // kept: Windows calls it while the class exists
            var cls = new WndClassEx
            {
                cbSize = Marshal.SizeOf<WndClassEx>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), hInstance = instance,
                lpszClassName = ClassName, hCursor = LoadCursor(IntPtr.Zero, (IntPtr)32512),
            };
            RegisterClassEx(ref cls);
            _registered = true;
        }
        const uint exStyle = 0x00000008 | 0x00000080 | 0x08000000; // WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
        _hwnd = CreateWindowEx(exStyle, ClassName, "KOS Firewall", 0x80000000, 0, 0, Width, Height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero); // WS_POPUP
        if (_hwnd == IntPtr.Zero) { Log.Warn("KOS Firewall: the popup window couldn't be created."); answer(ask, null); return; }
        var round = 2; // DWMWCP_ROUND (Windows 11; ignored before)
        DwmSetWindowAttribute(_hwnd, 33, ref round, sizeof(int));
        Show(ask);
        Place();
        ShowWindow(_hwnd, 4); // SW_SHOWNOACTIVATE
    }

    void Show(FirewallAsk ask)
    {
        _current = ask;
        _hover = -1;
        if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
        try { if (ExtractIconEx(ask.Path, 0, out var large, IntPtr.Zero, 1) > 0) _icon = large; }
        catch { _icon = IntPtr.Zero; }
        InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    void Place()
    {
        var dpi = (int)GetDpiForWindow(_hwnd);
        var work = new Rect();
        SystemParametersInfo(0x30, 0, ref work, 0); // SPI_GETWORKAREA
        int w = Width * dpi / 96, h = Height * dpi / 96, margin = 12 * dpi / 96;
        SetWindowPos(_hwnd, (IntPtr)(-1), work.Right - w - margin, work.Bottom - h - margin, w, h, 0x0010 | 0x0040); // HWND_TOPMOST; NOACTIVATE | SHOWWINDOW
    }

    void Choose(int hit)
    {
        if (_current is not { } ask) return;
        answer(ask, hit == HitAllow ? true : hit == HitBlock ? false : null);
        if (_waiting.TryDequeue(out var next)) Show(next);
        else
        {
            _current = null; // answered: closing the window isn't a "later"
            DestroyWindow(_hwnd);
        }
    }

    static (int Left, int Top, int Right, int Bottom) HitRect(int hit) => hit switch
    {
        HitAllow => (Width - Pad - 2 * ButtonWidth - 8, Height - Pad - ButtonHeight, Width - Pad - ButtonWidth - 8, Height - Pad),
        HitBlock => (Width - Pad - ButtonWidth, Height - Pad - ButtonHeight, Width - Pad, Height - Pad),
        _ => (Width - 40, 8, Width - 8, 40),
    };

    int Hit(IntPtr l)
    {
        var dpi = (int)GetDpiForWindow(_hwnd);
        int x = (short)(l.ToInt64() & 0xFFFF) * 96 / dpi, y = (short)((l.ToInt64() >> 16) & 0xFFFF) * 96 / dpi;
        foreach (var hit in new[] { HitAllow, HitBlock, HitClose })
        {
            var r = HitRect(hit);
            if (x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom) return hit;
        }
        return -1;
    }

    IntPtr Proc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        switch (msg)
        {
            case 0x000F: // WM_PAINT
                {
                    var hdc = BeginPaint(hwnd, out var ps);
                    GetClientRect(hwnd, out var r);
                    var mem = CreateCompatibleDC(hdc);
                    var bitmap = CreateCompatibleBitmap(hdc, r.Right, r.Bottom);
                    var old = SelectObject(mem, bitmap);
                    if (_current is { } ask) Paint(mem, r.Right, r.Bottom, (int)GetDpiForWindow(hwnd), ask, _waiting.Count, _hover, _icon);
                    BitBlt(hdc, 0, 0, r.Right, r.Bottom, mem, 0, 0, 0x00CC0020); // SRCCOPY
                    SelectObject(mem, old);
                    DeleteObject(bitmap);
                    DeleteDC(mem);
                    EndPaint(hwnd, ref ps);
                }
                return IntPtr.Zero;
            case 0x0014: // WM_ERASEBKGND
                return (IntPtr)1;
            case 0x0021: // WM_MOUSEACTIVATE: clicks don't activate it
                return (IntPtr)3; // MA_NOACTIVATE
            case 0x0200: // WM_MOUSEMOVE
                {
                    if (!_tracking)
                    {
                        var track = new TrackMouse { cbSize = Marshal.SizeOf<TrackMouse>(), dwFlags = 2, hwndTrack = hwnd }; // TME_LEAVE
                        _tracking = TrackMouseEvent(ref track);
                    }
                    var hit = Hit(l);
                    if (hit != _hover) { _hover = hit; InvalidateRect(hwnd, IntPtr.Zero, false); }
                }
                return IntPtr.Zero;
            case 0x02A3: // WM_MOUSELEAVE
                _tracking = false;
                if (_hover != -1) { _hover = -1; InvalidateRect(hwnd, IntPtr.Zero, false); }
                return IntPtr.Zero;
            case 0x0020 when (l.ToInt64() & 0xFFFF) == 1: // WM_SETCURSOR over the client area
                SetCursor(LoadCursor(IntPtr.Zero, (IntPtr)(_hover >= 0 ? 32649 : 32512))); // IDC_HAND / IDC_ARROW
                return (IntPtr)1;
            case 0x0202: // WM_LBUTTONUP
                if (Hit(l) is var chosen and >= 0) Choose(chosen);
                return IntPtr.Zero;
            case 0x02E0: // WM_DPICHANGED
                Place();
                return IntPtr.Zero;
            case 0x0002: // WM_DESTROY
                if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
                // Closed from outside (the engine stopping): what was still open or waiting is asked again next time.
                if (_current is { } open) answer(open, null);
                foreach (var waiting in _waiting) answer(waiting, null);
                _waiting.Clear();
                _current = null;
                _hwnd = IntPtr.Zero;
                _tracking = false;
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, w, l);
    }

    static string Protocol(byte p) => p switch
    {
        6 => "TCP", 17 => "UDP", 1 => "ICMP", 58 => "ICMPv6", 0 => "", _ => "protocol " + p.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>"1.1.1.1:443 (TCP)", "[2606:4700::1111]:443 (TCP)".</summary>
    internal static string Target(FirewallAsk a)
    {
        var address = a.Remote is null ? "an unknown address" : a.Remote.AddressFamily == AddressFamily.InterNetworkV6 && a.RemotePort > 0 ? $"[{a.Remote}]" : a.Remote.ToString();
        var port = a.RemotePort > 0 && a.Remote is not null ? ":" + a.RemotePort.ToString(CultureInfo.InvariantCulture) : "";
        var protocol = Protocol(a.Protocol);
        return address + port + (protocol.Length > 0 ? $" ({protocol})" : "");
    }

    /// <summary>Draws the whole popup (also used to render it to an image without showing it).</summary>
    internal static void Paint(IntPtr hdc, int width, int height, int dpi, FirewallAsk ask, int waiting, int hover, IntPtr icon)
    {
        int S(int v) => v * dpi / 96;
        IntPtr Font(int size, int weight) => CreateFont(-S(size), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        IntPtr title = Font(15, 600), body = Font(13, 400), strong = Font(13, 600), small = Font(11, 400), label = Font(11, 600);
        var background = CreateSolidBrush(Background);
        var all = new Rect { Right = width, Bottom = height };
        FillRect(hdc, ref all, background);
        DeleteObject(background);
        SetBkMode(hdc, 1); // TRANSPARENT

        void Draw(string text, IntPtr font, uint color, int x, int y, int w, int h, uint format = 0x0020 | 0x0004 | 0x8000) // SINGLELINE | VCENTER | END_ELLIPSIS
        {
            var old = SelectObject(hdc, font);
            SetTextColor(hdc, color);
            var r = new Rect { Left = S(x), Top = S(y), Right = S(x + w), Bottom = S(y + h) };
            DrawText(hdc, text, text.Length, ref r, format | 0x0800); // NOPREFIX
            SelectObject(hdc, old);
        }
        void Box((int Left, int Top, int Right, int Bottom) r, uint fill, uint outline)
        {
            var pen = CreatePen(0, 1, outline);
            var brush = CreateSolidBrush(fill);
            var oldPen = SelectObject(hdc, pen);
            var oldBrush = SelectObject(hdc, brush);
            RoundRect(hdc, S(r.Left), S(r.Top), S(r.Right), S(r.Bottom), S(8), S(8));
            SelectObject(hdc, oldPen);
            SelectObject(hdc, oldBrush);
            DeleteObject(pen);
            DeleteObject(brush);
        }

        // Frame
        var frame = CreatePen(0, 1, Border);
        var oldFrame = SelectObject(hdc, frame);
        var hollow = SelectObject(hdc, GetStockObject(5)); // NULL_BRUSH
        RoundRect(hdc, 0, 0, width, height, S(16), S(16));
        SelectObject(hdc, oldFrame);
        SelectObject(hdc, hollow);
        DeleteObject(frame);

        Draw("KOS FIREWALL", label, Secondary, Pad, 12, 160, 24);
        if (waiting > 0) Draw($"{waiting} more waiting", small, Secondary, Width - 200, 12, 152, 24, 0x0020 | 0x0004 | 0x0002); // right aligned
        // Close: a cross, with a hover square
        var close = HitRect(HitClose);
        if (hover == HitClose) Box(close, Hover, Hover);
        var cross = CreatePen(0, Math.Max(1, S(1)), hover == HitClose ? Text : Secondary);
        var oldCross = SelectObject(hdc, cross);
        int cx = S((close.Left + close.Right) / 2), cy = S((close.Top + close.Bottom) / 2), arm = S(5);
        MoveToEx(hdc, cx - arm, cy - arm, IntPtr.Zero); LineTo(hdc, cx + arm + 1, cy + arm + 1);
        MoveToEx(hdc, cx + arm, cy - arm, IntPtr.Zero); LineTo(hdc, cx - arm - 1, cy + arm + 1);
        SelectObject(hdc, oldCross);
        DeleteObject(cross);

        var textLeft = Pad;
        if (icon != IntPtr.Zero)
        {
            DrawIconEx(hdc, S(Pad), S(46), icon, S(IconSize), S(IconSize), 0, IntPtr.Zero, 3); // DI_NORMAL
            textLeft = Pad + IconSize + 12;
        }
        Draw(ask.Name, title, Text, textLeft, 40, Width - Pad - textLeft, 24);
        Draw(ask.Path, small, Secondary, textLeft, 64, Width - Pad - textLeft, 20, 0x0020 | 0x0004 | 0x4000); // PATH_ELLIPSIS
        Draw("Blocked a connection to " + Target(ask), body, Secondary, Pad, 92, Width - 2 * Pad, 24);

        var allow = HitRect(HitAllow);
        Box(allow, hover == HitAllow ? 0x00FFFFFF : Text, Text);
        Draw("Allow", strong, Dark, allow.Left, allow.Top, allow.Right - allow.Left, allow.Bottom - allow.Top, 0x0020 | 0x0004 | 0x0001); // centered
        var block = HitRect(HitBlock);
        Box(block, hover == HitBlock ? Hover : Background, Border);
        Draw("Block", strong, Text, block.Left, block.Top, block.Right - block.Left, block.Bottom - block.Top, 0x0020 | 0x0004 | 0x0001);
        Draw("Closing keeps it blocked.", small, Secondary, Pad, allow.Top, allow.Left - Pad - 8, ButtonHeight);
        foreach (var f in new[] { title, body, strong, small, label }) DeleteObject(f);
    }

    /// <summary>The client size in pixels (rendering to an image).</summary>
    internal static (int Width, int Height) ClientSize(int dpi) => (Width * dpi / 96, Height * dpi / 96);
}
