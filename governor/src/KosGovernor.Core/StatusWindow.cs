using System.Runtime.InteropServices;
using static KosGovernor.Core.NativeUi;

namespace KosGovernor.Core;

/// <summary>
/// The engine's status window: one line per task with its state, CPU and memory. Plain Windows API (the engine has no WPF), drawn
/// with GDI in the KOS colors, on the tray's thread. While it is open it turns <see cref="TaskMeter"/> on and repaints every second;
/// closed, nothing is measured.
/// </summary>
internal sealed class StatusWindow
{
    // Logical pixels at 96 DPI
    const int Width = 720, Pad = 20, RowHeight = 38, HeaderHeight = 86, ColumnsHeight = 26, FooterHeight = 64;
    const int ColState = 216, ColCpu = 548, ColMemory = 622;
    const uint Background = 0x000D0D0D, Text = 0x00F5F5F5, Secondary = 0x00A0A0A0, Hairline = 0x001F1F1F, Border = 0x00292929;
    const string ClassName = "KosEngineStatus";

    readonly Func<TaskMeter.Sample, StatusView> _build;
    readonly WndProc _proc;
    readonly IntPtr _icon;
    bool _registered;
    IntPtr _hwnd;
    TaskMeter? _meter;
    StatusView? _view;

    public StatusWindow(Func<TaskMeter.Sample, StatusView> build, IntPtr icon)
    {
        _build = build;
        _icon = icon;
        _proc = Proc; // kept: Windows calls it while the class exists
    }

    static int Height(int lines) => HeaderHeight + ColumnsHeight + lines * RowHeight + FooterHeight;

    /// <summary>Opens the window above the tray, or brings the open one to the front.</summary>
    public void Show()
    {
        if (_hwnd != IntPtr.Zero) { ShowWindow(_hwnd, 9); SetForegroundWindow(_hwnd); return; } // SW_RESTORE
        var instance = GetModuleHandle(null);
        if (!_registered)
        {
            var cls = new WndClassEx
            {
                cbSize = Marshal.SizeOf<WndClassEx>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), hInstance = instance,
                lpszClassName = ClassName, hIcon = _icon, hIconSm = _icon, hCursor = LoadCursor(IntPtr.Zero, (IntPtr)32512), // IDC_ARROW
            };
            RegisterClassEx(ref cls);
            _registered = true;
        }
        TaskMeter.On = true;
        _meter = TaskMeter.Start();
        _view = null;
        const uint style = 0x00C00000 | 0x00080000 | 0x00020000; // WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX
        _hwnd = CreateWindowEx(0, ClassName, "KOS Engine", style, 0, 0, Width, 400, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) { TaskMeter.On = false; return; }
        var dark = 1;
        DwmSetWindowAttribute(_hwnd, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        Refresh();
        Place();
        SetTimer(_hwnd, 1, 1000, IntPtr.Zero);
        ShowWindow(_hwnd, 5); // SW_SHOW
        SetForegroundWindow(_hwnd);
    }

    public void Close()
    {
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
    }

    void Refresh()
    {
        try { _view = _build(_meter!.Take()); }
        catch (Exception ex) { Log.Warn($"Status window: reading the engine's state failed: {ex.Message}"); }
    }

    /// <summary>Sized for its lines at the window's DPI, in the corner of the work area next to the tray.</summary>
    void Place()
    {
        var dpi = (int)GetDpiForWindow(_hwnd);
        var client = new Rect { Right = Width * dpi / 96, Bottom = Height(_view?.Lines.Count ?? 9) * dpi / 96 };
        AdjustWindowRectExForDpi(ref client, 0x00C00000 | 0x00080000 | 0x00020000, false, 0, (uint)dpi);
        int w = client.Right - client.Left, h = client.Bottom - client.Top;
        var work = new Rect();
        SystemParametersInfo(0x30, 0, ref work, 0); // SPI_GETWORKAREA
        var margin = 12 * dpi / 96;
        SetWindowPos(_hwnd, IntPtr.Zero, work.Right - w - margin, work.Bottom - h - margin, w, h, 0x0004 | 0x0010); // NOZORDER | NOACTIVATE
    }

    IntPtr Proc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        switch (msg)
        {
            case 0x0113: // WM_TIMER
                var lines = _view?.Lines.Count;
                Refresh();
                if (_view?.Lines.Count != lines) Place();
                InvalidateRect(hwnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            case 0x000F: // WM_PAINT
                {
                    var hdc = BeginPaint(hwnd, out var ps);
                    GetClientRect(hwnd, out var r);
                    // Drawn off screen, then copied: no flicker on the one-second repaint
                    var mem = CreateCompatibleDC(hdc);
                    var bitmap = CreateCompatibleBitmap(hdc, r.Right, r.Bottom);
                    var old = SelectObject(mem, bitmap);
                    if (_view is { } view) Paint(mem, r.Right, r.Bottom, (int)GetDpiForWindow(hwnd), view);
                    BitBlt(hdc, 0, 0, r.Right, r.Bottom, mem, 0, 0, 0x00CC0020); // SRCCOPY
                    SelectObject(mem, old);
                    DeleteObject(bitmap);
                    DeleteDC(mem);
                    EndPaint(hwnd, ref ps);
                }
                return IntPtr.Zero;
            case 0x0014: // WM_ERASEBKGND: WM_PAINT covers everything
                return (IntPtr)1;
            case 0x02E0: // WM_DPICHANGED
                Place();
                return IntPtr.Zero;
            case 0x0100 when (int)w == 0x1B: // WM_KEYDOWN, Esc
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case 0x0002: // WM_DESTROY
                KillTimer(hwnd, 1);
                TaskMeter.On = false;
                _hwnd = IntPtr.Zero;
                _meter = null;
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, w, l);
    }

    /// <summary>Draws the whole client area (also used to render the window to an image without showing it).</summary>
    internal static void Paint(IntPtr hdc, int width, int height, int dpi, StatusView view)
    {
        int S(int v) => v * dpi / 96;
        IntPtr Font(int size, int weight) => CreateFont(-S(size), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI"); // DEFAULT_CHARSET, CLEARTYPE_QUALITY
        IntPtr title = Font(20, 600), body = Font(13, 400), strong = Font(13, 600), small = Font(11, 400), label = Font(11, 600);
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
        void Line(int y, uint color)
        {
            var pen = CreatePen(0, 1, color);
            var old = SelectObject(hdc, pen);
            MoveToEx(hdc, S(Pad), S(y), IntPtr.Zero);
            LineTo(hdc, width - S(Pad), S(y));
            SelectObject(hdc, old);
            DeleteObject(pen);
        }

        Draw("KOS Engine", title, Text, Pad, 16, 300, 30);
        Draw($"CPU {EngineStatus.Cpu(view.CpuPercent)}  ·  Memory {EngineStatus.Memory(view.PrivateBytes, false)}", body, Secondary, Pad, 48, Width - 2 * Pad, 22);

        var y = HeaderHeight;
        Draw("TASK", label, Secondary, Pad + 18, y, 150, ColumnsHeight);
        Draw("STATUS", label, Secondary, ColState, y, 200, ColumnsHeight);
        Draw("CPU", label, Secondary, ColCpu, y, ColMemory - ColCpu - 8, ColumnsHeight, 0x0020 | 0x0004 | 0x0002); // right aligned
        Draw("MEMORY", label, Secondary, ColMemory, y, Width - Pad - ColMemory, ColumnsHeight, 0x0020 | 0x0004 | 0x0002);
        y += ColumnsHeight;
        Line(y, Border);

        foreach (var line in view.Lines)
        {
            // The dot: filled while the task is doing something, a ring otherwise
            var color = line.Active ? Text : Secondary;
            var pen = CreatePen(0, 1, color);
            var brush = line.Active ? CreateSolidBrush(color) : GetStockObject(5); // NULL_BRUSH
            var oldPen = SelectObject(hdc, pen);
            var oldBrush = SelectObject(hdc, brush);
            int cx = S(Pad + 5), cy = S(y + RowHeight / 2), rad = S(4);
            Ellipse(hdc, cx - rad, cy - rad, cx + rad + 1, cy + rad + 1);
            SelectObject(hdc, oldPen);
            SelectObject(hdc, oldBrush);
            DeleteObject(pen);
            if (line.Active) DeleteObject(brush);

            Draw(line.Name, strong, line.Active ? Text : Secondary, Pad + 18, y, ColState - Pad - 26, RowHeight);
            Draw(line.State, body, Secondary, ColState, y, ColCpu - ColState - 8, RowHeight);
            Draw(line.CpuPercent is { } cpu ? EngineStatus.Cpu(cpu) : "–", body, Text, ColCpu, y, ColMemory - ColCpu - 8, RowHeight, 0x0020 | 0x0004 | 0x0002);
            Draw(line.Bytes is { } bytes ? EngineStatus.Memory(bytes, line.Estimate) : "–", body, Text, ColMemory, y, Width - Pad - ColMemory, RowHeight, 0x0020 | 0x0004 | 0x0002);
            y += RowHeight;
            Line(y, Hairline);
        }

        Draw("CPU is the share of all processors, measured only while this window is open. Memory per task is an estimate (≈): what the task took when it started plus what it has taken since.",
            small, Secondary, Pad, y + 10, Width - 2 * Pad, FooterHeight - 16, 0x0010 | 0x0800); // WORDBREAK
        foreach (var f in new[] { title, body, strong, small, label }) DeleteObject(f);
    }

    /// <summary>The client size in pixels for this many lines (rendering to an image).</summary>
    internal static (int Width, int Height) ClientSize(int lines, int dpi) => (Width * dpi / 96, Height(lines) * dpi / 96);
}
