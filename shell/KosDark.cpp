// KOS Shell's dark mode for classic Windows tools (Registry Editor, management consoles). KOS Engine loads this DLL into such an app
// when its window opens and the toolbox switch for it is on: a WH_CALLWNDPROC hook on that window's thread only (DarkHook), removed
// right after one message. In the app the DLL checks its exe against its own fixed list, then:
// - the process's app mode (uxtheme's undocumented ordinals): dark popup and context menus; dark title bars (DWM attribute 20);
// - each window by its class, as it's created (a WinEvent hook inside this process only): tree and list views, headers, status bars,
//   edits, combo and list boxes, buttons, scroll bars, rebars, the MDI client, dialogs (WM_CTLCOLOR*) and the menu bar (the
//   undocumented UAH messages).
// Nothing else: no API is hooked and no other process is touched. The DLL stays loaded until the app exits (its windows use it).
#include <windows.h>
#include <commctrl.h>
#include <uxtheme.h>
#include <dwmapi.h>
#include <atomic>
#include <initializer_list>

namespace
{
    constexpr COLORREF Back = RGB(32, 32, 32), Field = RGB(45, 45, 45), Hot = RGB(65, 65, 65), Text = RGB(240, 240, 240), Dim = RGB(150, 150, 150), Lines = RGB(90, 90, 90);
    constexpr UINT_PTR FrameId = 0x4B4F5301, StatusId = 0x4B4F5302, EraseId = 0x4B4F5303, ListId = 0x4B4F5304;
    constexpr const wchar_t* Mark = L"KosShell.Dark";
    HBRUSH g_back, g_field, g_hot;
    std::atomic<bool> g_started{ false };
    using AllowDarkFn = BOOL(WINAPI*)(HWND, BOOL);
    AllowDarkFn g_allowDark;

    // Sent to a window with a menu bar so it can draw the bar itself (undocumented; the layout used by every dark-menu-bar implementation).
    constexpr UINT WmUahDrawMenu = 0x91, WmUahDrawMenuItem = 0x92;
    struct UahMenu { HMENU Menu; HDC Dc; DWORD Flags; };
    union UahMenuItemMetrics { struct { DWORD cx, cy; } Bar[2]; struct { DWORD cx, cy; } Popup[4]; };
    struct UahMenuPopupMetrics { DWORD Cx[4]; DWORD UpdateMaxWidths : 2; };
    struct UahMenuItem { int Position; UahMenuItemMetrics Metrics; UahMenuPopupMetrics PopupMetrics; };
    struct UahDrawMenuItem { DRAWITEMSTRUCT Draw; UahMenu Menu; UahMenuItem Item; };

    bool Is(const wchar_t* cls, const wchar_t* name) { return CompareStringOrdinal(cls, -1, name, -1, TRUE) == CSTR_EQUAL; }

    /// This process is one of the apps (KOS Engine checked it too: hooked into anything else, the DLL does nothing).
    bool Allowed()
    {
        wchar_t exe[MAX_PATH], path[MAX_PATH];
        const auto length = GetModuleFileNameW(nullptr, exe, MAX_PATH);
        if (length == 0 || length >= MAX_PATH) return false;
        for (auto name : { L"\\regedit.exe", L"\\System32\\mmc.exe" })
        {
            const auto windows = GetSystemWindowsDirectoryW(path, MAX_PATH);
            if (windows != 0 && windows < MAX_PATH && wcscat_s(path, name) == 0 && CompareStringOrdinal(exe, -1, path, -1, TRUE) == CSTR_EQUAL) return true;
        }
        return false;
    }

    bool IsToolbar(HWND h)
    {
        wchar_t cls[32] = {};
        return h && GetClassNameW(h, cls, 32) && Is(cls, TOOLBARCLASSNAMEW);
    }

    /// A toolbar without images (the consoles' menu bar): its buttons are drawn here, light on dark, whatever the app drew.
    bool TextOnly(HWND toolbar) { return SendMessageW(toolbar, TB_GETIMAGELIST, 0, 0) == 0; }

    LRESULT DrawTextButton(NMTBCUSTOMDRAW& draw)
    {
        const auto toolbar = draw.nmcd.hdr.hwndFrom;
        wchar_t text[128] = {};
        const auto length = SendMessageW(toolbar, TB_GETBUTTONTEXTW, draw.nmcd.dwItemSpec, 0);
        if (length > 0 && length < 128) SendMessageW(toolbar, TB_GETBUTTONTEXTW, draw.nmcd.dwItemSpec, reinterpret_cast<LPARAM>(text));
        const auto state = draw.nmcd.uItemState;
        FillRect(draw.nmcd.hdc, &draw.nmcd.rc, state & (CDIS_HOT | CDIS_SELECTED | CDIS_CHECKED | CDIS_MARKED) ? g_hot : g_back);
        auto font = SelectObject(draw.nmcd.hdc, reinterpret_cast<HGDIOBJ>(SendMessageW(toolbar, WM_GETFONT, 0, 0)));
        SetBkMode(draw.nmcd.hdc, TRANSPARENT);
        SetTextColor(draw.nmcd.hdc, state & CDIS_DISABLED ? Dim : Text);
        DrawTextW(draw.nmcd.hdc, text, -1, &draw.nmcd.rc, DT_CENTER | DT_VCENTER | DT_SINGLELINE | (state & CDIS_SHOWKEYBOARDCUES ? 0 : DT_HIDEPREFIX));
        SelectObject(draw.nmcd.hdc, font);
        return CDRF_SKIPDEFAULT;
    }

    void Fill(HDC dc, HWND h, HBRUSH brush)
    {
        RECT rc;
        GetClientRect(h, &rc);
        FillRect(dc, &rc, brush);
    }

    /// Top-level windows, dialogs and the parents of plain controls: control colors, background, menu bar.
    LRESULT CALLBACK FrameProc(HWND h, UINT msg, WPARAM w, LPARAM l, UINT_PTR, DWORD_PTR)
    {
        switch (msg)
        {
        case WM_CTLCOLORDLG: case WM_CTLCOLORSTATIC: case WM_CTLCOLORBTN:
            SetTextColor(reinterpret_cast<HDC>(w), IsWindowEnabled(reinterpret_cast<HWND>(l)) ? Text : Dim);
            SetBkColor(reinterpret_cast<HDC>(w), Back);
            return reinterpret_cast<LRESULT>(g_back);
        case WM_CTLCOLOREDIT: case WM_CTLCOLORLISTBOX:
            SetTextColor(reinterpret_cast<HDC>(w), Text);
            SetBkColor(reinterpret_cast<HDC>(w), Field);
            return reinterpret_cast<LRESULT>(g_field);
        case WM_ERASEBKGND:
            Fill(reinterpret_cast<HDC>(w), h, g_back);
            return 1;
        case WM_NOTIFY:
            // Toolbar text (the consoles' menu bar is a toolbar): the app's own custom draw first, then light text over it.
            if (auto header = reinterpret_cast<NMHDR*>(l); header->code == NM_CUSTOMDRAW && IsToolbar(header->hwndFrom))
            {
                const auto result = DefSubclassProc(h, msg, w, l);
                auto draw = reinterpret_cast<NMTBCUSTOMDRAW*>(l);
                if (draw->nmcd.dwDrawStage == CDDS_PREPAINT) return result | CDRF_NOTIFYITEMDRAW;
                if (draw->nmcd.dwDrawStage != CDDS_ITEMPREPAINT) return result;
                if (TextOnly(header->hwndFrom)) return DrawTextButton(*draw);
                if (result & CDRF_SKIPDEFAULT) return result;
                draw->clrText = Text;
                draw->clrTextHighlight = Text;
                draw->clrBtnFace = Back;
                draw->clrHighlightHotTrack = Hot;
                return result | TBCDRF_USECDCOLORS | TBCDRF_HILITEHOTTRACK;
            }
            break;
        case WmUahDrawMenu:
        {
            MENUBARINFO bar{ sizeof bar };
            RECT window;
            if (!GetMenuBarInfo(h, OBJID_MENU, 0, &bar) || !GetWindowRect(h, &window)) break;
            OffsetRect(&bar.rcBar, -window.left, -window.top);
            FillRect(reinterpret_cast<UahMenu*>(l)->Dc, &bar.rcBar, g_back);
            return TRUE;
        }
        case WmUahDrawMenuItem:
        {
            auto item = reinterpret_cast<UahDrawMenuItem*>(l);
            wchar_t text[256] = {};
            MENUITEMINFOW info{ sizeof info };
            info.fMask = MIIM_STRING;
            info.dwTypeData = text;
            info.cch = 255;
            GetMenuItemInfoW(item->Menu.Menu, item->Item.Position, TRUE, &info);
            const auto state = item->Draw.itemState;
            FillRect(item->Menu.Dc, &item->Draw.rcItem, state & (ODS_HOTLIGHT | ODS_SELECTED) ? g_hot : g_back);
            SetBkMode(item->Menu.Dc, TRANSPARENT);
            SetTextColor(item->Menu.Dc, state & (ODS_GRAYED | ODS_DISABLED) ? Dim : Text);
            DrawTextW(item->Menu.Dc, text, -1, &item->Draw.rcItem, DT_CENTER | DT_SINGLELINE | DT_VCENTER | (state & ODS_NOACCEL ? DT_HIDEPREFIX : 0));
            return TRUE;
        }
        case WM_NCPAINT: case WM_NCACTIVATE:
        {
            const auto result = DefSubclassProc(h, msg, w, l);
            // The light line Windows draws between the menu bar and the client area.
            MENUBARINFO bar{ sizeof bar };
            if (GetMenu(h) && GetMenuBarInfo(h, OBJID_MENU, 0, &bar))
            {
                RECT client, window;
                GetClientRect(h, &client);
                MapWindowPoints(h, nullptr, reinterpret_cast<POINT*>(&client), 2);
                GetWindowRect(h, &window);
                OffsetRect(&client, -window.left, -window.top);
                RECT line = client;
                line.bottom = line.top;
                line.top--;
                if (auto dc = GetWindowDC(h)) { FillRect(dc, &line, g_back); ReleaseDC(h, dc); }
            }
            return result;
        }
        case WM_NCDESTROY:
            RemoveWindowSubclass(h, FrameProc, FrameId);
            break;
        }
        return DefSubclassProc(h, msg, w, l);
    }

    /// The status bar draws its text with the system's dark text color: painted here, unless a part is owner-drawn (left as it is).
    LRESULT CALLBACK StatusProc(HWND h, UINT msg, WPARAM w, LPARAM l, UINT_PTR, DWORD_PTR)
    {
        if (msg == WM_PAINT || msg == WM_ERASEBKGND)
        {
            const bool simple = SendMessageW(h, SB_ISSIMPLE, 0, 0) != 0;
            const int parts = simple ? 1 : static_cast<int>(SendMessageW(h, SB_GETPARTS, 0, 0));
            bool ownerDrawn = false;
            for (int i = 0; i < parts && !ownerDrawn; i++)
                ownerDrawn = (HIWORD(SendMessageW(h, SB_GETTEXTLENGTHW, simple ? SB_SIMPLEID : i, 0)) & SBT_OWNERDRAW) != 0;
            if (!ownerDrawn)
            {
                if (msg == WM_ERASEBKGND) return 1;
                PAINTSTRUCT paint;
                auto dc = BeginPaint(h, &paint);
                Fill(dc, h, g_back);
                auto font = SelectObject(dc, reinterpret_cast<HGDIOBJ>(SendMessageW(h, WM_GETFONT, 0, 0)));
                SetBkMode(dc, TRANSPARENT);
                SetTextColor(dc, Text);
                for (int i = 0; i < parts; i++)
                {
                    const WPARAM id = simple ? SB_SIMPLEID : i;
                    RECT rc;
                    if (simple) GetClientRect(h, &rc);
                    else if (!SendMessageW(h, SB_GETRECT, id, reinterpret_cast<LPARAM>(&rc))) continue;
                    wchar_t text[512] = {};
                    if (LOWORD(SendMessageW(h, SB_GETTEXTLENGTHW, id, 0)) < 512) SendMessageW(h, SB_GETTEXTW, id, reinterpret_cast<LPARAM>(text));
                    rc.left += 4;
                    rc.right -= 4;
                    DrawTextW(dc, text, -1, &rc, DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS | DT_NOPREFIX | (text[0] == L'\t' ? DT_CENTER : 0));
                }
                SelectObject(dc, font);
                EndPaint(h, &paint);
                return 0;
            }
        }
        else if (msg == WM_NCDESTROY) RemoveWindowSubclass(h, StatusProc, StatusId);
        return DefSubclassProc(h, msg, w, l);
    }

    /// MDI client (the consoles' background).
    LRESULT CALLBACK EraseProc(HWND h, UINT msg, WPARAM w, LPARAM l, UINT_PTR, DWORD_PTR)
    {
        if (msg == WM_ERASEBKGND) { Fill(reinterpret_cast<HDC>(w), h, g_back); return 1; }
        if (msg == WM_NCDESTROY) RemoveWindowSubclass(h, EraseProc, EraseId);
        return DefSubclassProc(h, msg, w, l);
    }

    /// A list view's header text: its dark theme keeps the text black, so it's set in the header's custom draw (sent to the list view).
    LRESULT CALLBACK ListProc(HWND h, UINT msg, WPARAM w, LPARAM l, UINT_PTR, DWORD_PTR)
    {
        if (msg == WM_NOTIFY)
            if (auto header = reinterpret_cast<NMHDR*>(l); header->code == NM_CUSTOMDRAW && header->hwndFrom == ListView_GetHeader(h))
            {
                auto draw = reinterpret_cast<NMCUSTOMDRAW*>(l);
                if (draw->dwDrawStage == CDDS_PREPAINT) return CDRF_NOTIFYITEMDRAW;
                if (draw->dwDrawStage == CDDS_ITEMPREPAINT) { SetTextColor(draw->hdc, Text); return CDRF_DODEFAULT; }
            }
        if (msg == WM_NCDESTROY) RemoveWindowSubclass(h, ListProc, ListId);
        return DefSubclassProc(h, msg, w, l);
    }

    void ParentColors(HWND h)
    {
        if (auto parent = GetParent(h)) SetWindowSubclass(parent, FrameProc, FrameId, 0);
    }

    /// Once per window, on its own thread (subclassing works only there).
    void Darken(HWND h)
    {
        if (!IsWindow(h) || GetPropW(h, Mark)) return;
        SetPropW(h, Mark, reinterpret_cast<HANDLE>(1));
        wchar_t cls[64] = {};
        GetClassNameW(h, cls, 64);
        const bool top = GetAncestor(h, GA_ROOT) == h;
        if (g_allowDark) g_allowDark(h, TRUE);
        if (top || Is(cls, L"#32770"))
        {
            SetWindowSubclass(h, FrameProc, FrameId, 0);
            if (top)
            {
                BOOL dark = TRUE;
                DwmSetWindowAttribute(h, 20, &dark, sizeof dark); // DWMWA_USE_IMMERSIVE_DARK_MODE: the title bar
                SetWindowPos(h, nullptr, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
        }
        else if (Is(cls, WC_TREEVIEWW))
        {
            SetWindowTheme(h, L"DarkMode_Explorer", nullptr);
            TreeView_SetBkColor(h, Back);
            TreeView_SetTextColor(h, Text);
            TreeView_SetLineColor(h, Lines);
        }
        else if (Is(cls, WC_LISTVIEWW))
        {
            SetWindowTheme(h, L"DarkMode_Explorer", nullptr);
            ListView_SetBkColor(h, Back);
            ListView_SetTextBkColor(h, Back);
            ListView_SetTextColor(h, Text);
            SetWindowSubclass(h, ListProc, ListId, 0);
            if (auto header = ListView_GetHeader(h)) Darken(header);
        }
        else if (Is(cls, WC_HEADERW)) SetWindowTheme(h, L"DarkMode_ItemsView", nullptr);
        else if (Is(cls, STATUSCLASSNAMEW)) SetWindowSubclass(h, StatusProc, StatusId, 0);
        else if (Is(cls, L"MDIClient")) SetWindowSubclass(h, EraseProc, EraseId, 0);
        else if (Is(cls, REBARCLASSNAMEW))
        {
            SetWindowTheme(h, L"", L""); // the rebar's colors apply only unthemed
            SendMessageW(h, RB_SETBKCOLOR, 0, Back);
            SendMessageW(h, RB_SETTEXTCOLOR, 0, Text);
        }
        else if (Is(cls, WC_EDITW) || Is(cls, WC_COMBOBOXW) || Is(cls, WC_LISTBOXW) || Is(cls, L"ComboLBox"))
        {
            SetWindowTheme(h, L"DarkMode_CFD", nullptr);
            ParentColors(h);
        }
        else if (Is(cls, WC_BUTTONW))
        {
            const auto type = GetWindowLongW(h, GWL_STYLE) & BS_TYPEMASK;
            const bool push = type == BS_PUSHBUTTON || type == BS_DEFPUSHBUTTON || type == BS_SPLITBUTTON || type == BS_DEFSPLITBUTTON;
            // Push buttons have a dark theme; check boxes, radio buttons and group boxes take WM_CTLCOLOR* text colors only unthemed.
            if (push) SetWindowTheme(h, L"DarkMode_Explorer", nullptr);
            else SetWindowTheme(h, L"", L"");
            ParentColors(h);
        }
        else if (Is(cls, TOOLBARCLASSNAMEW))
        {
            // Its notifications go to the window TB_SETPARENT named, which may not be its parent: read (set and put back) and subclass it.
            const auto target = reinterpret_cast<HWND>(SendMessageW(h, TB_SETPARENT, reinterpret_cast<WPARAM>(GetParent(h)), 0));
            if (target)
            {
                SendMessageW(h, TB_SETPARENT, reinterpret_cast<WPARAM>(target), 0);
                if (GetWindowThreadProcessId(target, nullptr) == GetCurrentThreadId()) SetWindowSubclass(target, FrameProc, FrameId, 0);
            }
        }
        else if (Is(cls, WC_STATICW)) ParentColors(h);
        else if (Is(cls, WC_SCROLLBARW)) SetWindowTheme(h, L"DarkMode_Explorer", nullptr);
        else return;
        InvalidateRect(h, nullptr, TRUE);
    }

    void CALLBACK OnWindowEvent(HWINEVENTHOOK, DWORD event, HWND h, LONG object, LONG child, DWORD, DWORD)
    {
        if (object != OBJID_WINDOW || child != CHILDID_SELF || !h) return;
        if (event == EVENT_OBJECT_DESTROY) RemovePropW(h, Mark);
        else Darken(h);
    }

    BOOL CALLBACK DarkenChild(HWND h, LPARAM) { Darken(h); return TRUE; }

    BOOL CALLBACK DarkenOpen(HWND h, LPARAM)
    {
        DWORD pid = 0;
        if (GetWindowThreadProcessId(h, &pid) == GetCurrentThreadId() && pid == GetCurrentProcessId())
        {
            Darken(h);
            EnumChildWindows(h, DarkenChild, 0);
            RedrawWindow(h, nullptr, nullptr, RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN);
        }
        return TRUE;
    }

    void Start()
    {
        if (g_started.exchange(true) || !Allowed()) return;
        // The app's windows keep using this code until they close: never unloaded (KOS Engine removes its hook right away).
        HMODULE self;
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN, reinterpret_cast<LPCWSTR>(&Start), &self)) return;
        g_back = CreateSolidBrush(Back);
        g_field = CreateSolidBrush(Field);
        g_hot = CreateSolidBrush(Hot);
        // uxtheme by ordinal (Windows 10 1903 and later): 135 SetPreferredAppMode, 104 RefreshImmersiveColorPolicyState,
        // 136 FlushMenuThemes, 133 AllowDarkModeForWindow.
        if (auto ux = LoadLibraryExW(L"uxtheme.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32))
        {
            using ModeFn = int(WINAPI*)(int);
            using VoidFn = void(WINAPI*)();
            g_allowDark = reinterpret_cast<AllowDarkFn>(GetProcAddress(ux, MAKEINTRESOURCEA(133)));
            if (auto setMode = reinterpret_cast<ModeFn>(GetProcAddress(ux, MAKEINTRESOURCEA(135)))) setMode(2); // ForceDark
            if (auto refresh = reinterpret_cast<VoidFn>(GetProcAddress(ux, MAKEINTRESOURCEA(104)))) refresh();
            if (auto flush = reinterpret_cast<VoidFn>(GetProcAddress(ux, MAKEINTRESOURCEA(136)))) flush();
        }
        // New windows of this process, on whichever thread creates them (in context: the callback runs on that thread).
        SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW, self, OnWindowEvent, GetCurrentProcessId(), 0, WINEVENT_INCONTEXT);
        // The windows already open on this thread (the one KOS Engine hooked: the app's UI thread).
        EnumWindows(DarkenOpen, 0);
    }
}

/// KOS Engine's thread hook: the first message starts dark mode in this process (once).
extern "C" LRESULT CALLBACK DarkHook(int code, WPARAM w, LPARAM l)
{
    if (code == HC_ACTION) Start();
    return CallNextHookEx(nullptr, code, w, l);
}
