// KOS Shell's dark mode for classic Windows tools: Registry Editor, management consoles, classic Notepad, Control Panel applets
// (rundll32 shell32.dll,Control_RunDLL; System Properties; System Configuration, Color Management, ODBC Data Sources) and File Explorer's classic dialogs (Properties, Folder Options; not the
// Run box, which paints its own light body).
// KOS Engine loads this DLL into such an app when its window opens and the toolbox switch for it is on: a WH_CALLWNDPROC hook on
// that window's thread only (DarkHook), removed right after one message. In the app the DLL checks its exe against its own fixed
// list, then:
// - in an app: the process's app mode (uxtheme's undocumented ordinals: dark popup and context menus) and every window;
// - in File Explorer: only dialogs (#32770), each when it's shown, and the windows inside them; Explorer's own windows, its app mode
//   and the taskbar are left alone;
// - each window by its class, as it's created (a WinEvent hook inside this process only): title bar (DWM attribute 20), tree and
//   list views, headers, tabs, status bars, edits, combo and list boxes, buttons, scroll bars, rebars, toolbars, the MDI client,
//   dialogs (WM_CTLCOLOR*) and the menu bar (the undocumented UAH messages).
// Nothing else: no other process is touched, and no API is hooked except GetSysColor and GetSysColorBrush in the DarkTools apps
// (import table pointers, no code patched). The DLL stays loaded until the app exits (its windows use it).
#include <windows.h>
#include <commctrl.h>
#include <uxtheme.h>
#include <dwmapi.h>
#include <psapi.h>
#include <richedit.h>
#include <atomic>
#include <cstdio>
#include <initializer_list>

namespace
{
    constexpr COLORREF Back = RGB(32, 32, 32), Field = RGB(45, 45, 45), Hot = RGB(65, 65, 65), Text = RGB(240, 240, 240), Dim = RGB(150, 150, 150), Lines = RGB(90, 90, 90);
    constexpr UINT_PTR FrameId = 0x4B4F5301, StatusId = 0x4B4F5302, EraseId = 0x4B4F5303, ListId = 0x4B4F5304, TabId = 0x4B4F5305;
    constexpr const wchar_t* Mark = L"KosShell.Dark";
    HBRUSH g_back, g_field, g_hot, g_lines;
    std::atomic<bool> g_started{ false };
    enum class Scope { None, App, Dialogs };
    Scope g_scope = Scope::None;
    using AllowDarkFn = BOOL(WINAPI*)(HWND, BOOL);
    AllowDarkFn g_allowDark;

    // DarkTools: tools that paint parts of themselves with the system colors (Character Map's grid, a wizard's header band, a
    // drive list) get dark ones: GetSysColor and GetSysColorBrush are replaced in the import tables of the modules loaded in that
    // app (pointers only, no code patched; never put back, the app ends with them). Only in the apps of that list, never Explorer.
    bool g_colors = false;
    using SysColorFn = DWORD(WINAPI*)(int);
    using SysBrushFn = HBRUSH(WINAPI*)(int);
    SysColorFn g_sysColor;
    SysBrushFn g_sysBrush;
    HBRUSH g_brushes[COLOR_MENUBAR + 1]; // created once at the start, kept for the app's lifetime (callers never delete these)

    bool DarkColor(int index, COLORREF& color)
    {
        switch (index)
        {
        case COLOR_WINDOW: color = Field; return true;
        case COLOR_BTNFACE: case COLOR_APPWORKSPACE: case COLOR_INFOBK: color = Back; return true; // COLOR_3DFACE is BTNFACE
        case COLOR_WINDOWTEXT: case COLOR_BTNTEXT: case COLOR_INFOTEXT: color = Text; return true;
        case COLOR_GRAYTEXT: color = Dim; return true;
        case COLOR_WINDOWFRAME: case COLOR_BTNSHADOW: case COLOR_BTNHIGHLIGHT: case COLOR_3DLIGHT: color = Lines; return true;
        case COLOR_3DDKSHADOW: color = RGB(20, 20, 20); return true;
        default: return false; // selection, captions, links: as Windows has them
        }
    }

    DWORD WINAPI DarkSysColor(int index)
    {
        COLORREF color;
        return DarkColor(index, color) ? color : g_sysColor(index);
    }

    HBRUSH WINAPI DarkSysColorBrush(int index)
    {
        // A caller may delete it (harmless for a real system brush): then Windows' own.
        return index >= 0 && index <= COLOR_MENUBAR && g_brushes[index] && GetObjectType(g_brushes[index]) == OBJ_BRUSH ? g_brushes[index] : g_sysBrush(index);
    }

    void Patch(void** slot)
    {
        if (!*slot) return; // the null ending each DLL's slots
        void* to = *slot == g_sysColor ? reinterpret_cast<void*>(&DarkSysColor) : *slot == g_sysBrush ? reinterpret_cast<void*>(&DarkSysColorBrush) : nullptr;
        MEMORY_BASIC_INFORMATION page;
        if (!to || !VirtualQuery(slot, &page, sizeof page)) return;
        const DWORD writable = page.Protect & (PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY) ? PAGE_EXECUTE_READWRITE : PAGE_READWRITE;
        DWORD old;
        if (VirtualProtect(slot, sizeof *slot, writable, &old))
        {
            InterlockedExchangePointer(slot, to);
            VirtualProtect(slot, sizeof *slot, old, &old);
        }
    }

    /// One module's import address table and delay-load tables: a slot holding user32's function (by address, so API sets too).
    void PatchModule(HMODULE module)
    {
        const auto base = reinterpret_cast<BYTE*>(module);
        const auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
        if (dos->e_magic != IMAGE_DOS_SIGNATURE) return;
        const auto nt = reinterpret_cast<const IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR_MAGIC
            || nt->OptionalHeader.NumberOfRvaAndSizes <= IMAGE_DIRECTORY_ENTRY_DELAY_IMPORT) return;
        const auto& iat = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IAT];
        if (iat.VirtualAddress)
            for (auto slot = reinterpret_cast<void**>(base + iat.VirtualAddress), end = slot + iat.Size / sizeof(void*); slot < end; slot++) Patch(slot);
        // Delay-loaded imports: a slot is user32's function only once resolved, so later passes catch the ones resolved meanwhile.
        const auto& delay = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_DELAY_IMPORT];
        if (delay.VirtualAddress)
            for (auto d = reinterpret_cast<const IMAGE_DELAYLOAD_DESCRIPTOR*>(base + delay.VirtualAddress); d->DllNameRVA; d++)
            {
                if (!d->Attributes.RvaBased) continue;
                auto slot = reinterpret_cast<void**>(base + d->ImportAddressTableRVA);
                for (auto name = reinterpret_cast<const IMAGE_THUNK_DATA*>(base + d->ImportNameTableRVA); name->u1.AddressOfData; name++, slot++) Patch(slot);
            }
    }

    /// The loader can make a table read-only again between our protect and our write (a DLL loading or a delay-load resolving on
    /// another thread): our write faults and the slot is taken at the next pass. A module with malformed headers ends here too.
    void PatchHeld(HMODULE module)
    {
        __try { PatchModule(module); }
        __except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) { }
    }

    /// Every module loaded now (at the start, and again for each new top-level window: an app loads more as it goes), but this one.
    /// One pass at a time (two threads switching a slot's protection could leave it read-only under the other's write); a pass
    /// that finds one running is skipped (the next top-level window runs it again) rather than wait. Each module is held while it's
    /// read, so one unloaded meanwhile is skipped rather than read freed.
    void PatchColors()
    {
        static SRWLOCK lock = SRWLOCK_INIT;
        HMODULE modules[1024], self;
        DWORD bytes = 0;
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, reinterpret_cast<LPCWSTR>(&PatchColors), &self)) return;
        if (!TryAcquireSRWLockExclusive(&lock)) return;
        if (K32EnumProcessModules(GetCurrentProcess(), modules, sizeof modules, &bytes))
            for (DWORD i = 0; i < bytes / sizeof(HMODULE) && i < 1024; i++)
                if (HMODULE held; modules[i] != self && GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, reinterpret_cast<LPCWSTR>(modules[i]), &held))
                {
                    if (held == modules[i]) PatchHeld(held);
                    FreeLibrary(held);
                }
        ReleaseSRWLockExclusive(&lock);
    }

    /// Once, at the start: the originals and one dark brush per replaced color.
    bool StartColors()
    {
        const auto user32 = GetModuleHandleW(L"user32.dll");
        g_sysColor = user32 ? reinterpret_cast<SysColorFn>(GetProcAddress(user32, "GetSysColor")) : nullptr;
        g_sysBrush = user32 ? reinterpret_cast<SysBrushFn>(GetProcAddress(user32, "GetSysColorBrush")) : nullptr;
        if (!g_sysColor || !g_sysBrush) return false;
        for (int i = 0; i <= COLOR_MENUBAR; i++)
            if (COLORREF color; DarkColor(i, color)) g_brushes[i] = CreateSolidBrush(color);
        PatchColors();
        return true;
    }

    // Sent to a window with a menu bar so it can draw the bar itself (undocumented; the layout used by every dark-menu-bar implementation).
    constexpr UINT WmUahDrawMenu = 0x91, WmUahDrawMenuItem = 0x92;
    struct UahMenu { HMENU Menu; HDC Dc; DWORD Flags; };
    union UahMenuItemMetrics { struct { DWORD cx, cy; } Bar[2]; struct { DWORD cx, cy; } Popup[4]; };
    struct UahMenuPopupMetrics { DWORD Cx[4]; DWORD UpdateMaxWidths : 2; };
    struct UahMenuItem { int Position; UahMenuItemMetrics Metrics; UahMenuPopupMetrics PopupMetrics; };
    struct UahDrawMenuItem { DRAWITEMSTRUCT Draw; UahMenu Menu; UahMenuItem Item; };

    bool Is(const wchar_t* cls, const wchar_t* name) { return CompareStringOrdinal(cls, -1, name, -1, TRUE) == CSTR_EQUAL; }

    bool SamePath(const wchar_t* a, const wchar_t* b) { return CompareStringOrdinal(a, -1, b, -1, TRUE) == CSTR_EQUAL; }

    /// Case-insensitive "contains" (ordinal).
    bool Contains(const wchar_t* text, const wchar_t* part)
    {
        const int n = lstrlenW(text), m = lstrlenW(part);
        for (int i = 0; i + m <= n; i++)
            if (CompareStringOrdinal(text + i, m, part, m, TRUE) == CSTR_EQUAL) return true;
        return false;
    }

    /// What this process is, by its exe (KOS Engine checked it the same way: hooked into anything else, the DLL does nothing).
    Scope Allowed()
    {
        wchar_t exe[MAX_PATH], windows[MAX_PATH], path[MAX_PATH];
        const auto length = GetModuleFileNameW(nullptr, exe, MAX_PATH);
        const auto folder = GetSystemWindowsDirectoryW(windows, MAX_PATH);
        if (length == 0 || length >= MAX_PATH || folder == 0 || folder >= MAX_PATH) return Scope::None;
        auto is = [&](const wchar_t* name) { return wcscpy_s(path, windows) == 0 && wcscat_s(path, name) == 0 && SamePath(exe, path); };
        if (is(L"\\regedit.exe") || is(L"\\System32\\mmc.exe") || is(L"\\System32\\notepad.exe")) return Scope::App;
        // DarkDialogs' tools: System Configuration, Color Management, ODBC Data Sources (KOS Engine's DarkApps.Apps has the same list).
        if (is(L"\\System32\\msconfig.exe") || is(L"\\System32\\colorcpl.exe") || is(L"\\System32\\odbcad32.exe")) return Scope::App;
        // DarkTools, with the system colors replaced (KOS Engine's DarkApps.Apps has the same list).
        for (const auto tool : { L"\\System32\\charmap.exe", L"\\System32\\msinfo32.exe", L"\\System32\\hdwwiz.exe", L"\\System32\\netplwiz.exe",
                 L"\\System32\\cleanmgr.exe" })
            if (is(tool)) { g_colors = true; return Scope::App; }
#ifdef KOS_SHELL_PROBE // test build: one more System32 exe, named in the environment of the process it's loaded into
        wchar_t extra[MAX_PATH] = L"\\System32\\";
        if (GetEnvironmentVariableW(L"KOS_SHELL_PROBE_APP", extra + 10, MAX_PATH - 10) && is(extra)) return Scope::App;
#endif
        if (is(L"\\explorer.exe")) return Scope::Dialogs;
        if (is(L"\\System32\\rundll32.exe")) return Contains(GetCommandLineW(), L"shell32.dll,Control_RunDLL") ? Scope::App : Scope::None;
        // System32\SystemProperties*.exe (System Properties and its tabs).
        if (wcscpy_s(path, windows) != 0 || wcscat_s(path, L"\\System32\\SystemProperties") != 0) return Scope::None;
        const int prefix = lstrlenW(path), total = lstrlenW(exe);
        return total > prefix + 4 && CompareStringOrdinal(exe, prefix, path, prefix, TRUE) == CSTR_EQUAL && !wcschr(exe + prefix, L'\\')
            && SamePath(exe + total - 4, L".exe") ? Scope::App : Scope::None;
    }

    /// The Run box, by shell32's fixed control ids (the command combo box, "Run in separate memory space"): it paints its own white
    /// body and keeps a light title bar, so dark controls would sit on white. Left as it is.
    bool IsRunBox(HWND dialog)
    {
        wchar_t cls[16] = {};
        const auto command = GetDlgItem(dialog, 12298), separate = GetDlgItem(dialog, 12306);
        return command && separate && GetClassNameW(command, cls, 16) && Is(cls, WC_COMBOBOXW) && GetClassNameW(separate, cls, 16) && Is(cls, WC_BUTTONW);
    }

    /// In File Explorer only dialogs and what's inside them: a dialog once it's shown (before its first paint; its controls exist by
    /// then, so the Run box can be told apart), then whatever is inside a dialog that's dark.
    bool InScope(HWND h)
    {
        if (g_scope != Scope::Dialogs) return true;
        wchar_t cls[16] = {};
        const auto root = GetAncestor(h, GA_ROOT);
        if (!root || !GetClassNameW(root, cls, 16) || !Is(cls, L"#32770")) return false;
        return root == h ? IsWindowVisible(h) && !IsRunBox(h) : GetPropW(root, Mark) != nullptr;
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

    /// A user32 message box, by its shape: only its text and icon (statics 0xFFFF and 20) and buttons with a result id.
    bool IsMessageBox(HWND h)
    {
        wchar_t own[8] = {};
        if (!GetDlgItem(h, 0xFFFF) || !GetClassNameW(h, own, 8) || !Is(own, L"#32770")) return false; // cheap, on every paint
        bool shape = true;
        EnumChildWindows(h, [](HWND child, LPARAM l) -> BOOL
        {
            wchar_t cls[16] = {};
            const auto id = GetDlgCtrlID(child);
            const bool fits = GetClassNameW(child, cls, 16)
                && ((Is(cls, WC_STATICW) && (id == 0xFFFF || id == 20)) || (Is(cls, WC_BUTTONW) && id >= IDOK && id <= IDCONTINUE));
            if (!fits) *reinterpret_cast<bool*>(l) = false;
            return fits;
        }, reinterpret_cast<LPARAM>(&shape));
        return shape;
    }

    /// Top-level windows, dialogs and the parents of plain controls: control colors, background, menu bar.
    LRESULT CALLBACK FrameProc(HWND h, UINT msg, WPARAM w, LPARAM l, UINT_PTR, DWORD_PTR)
    {
        switch (msg)
        {
        case WM_PAINT:
            // A message box's button band is painted by user32 itself in its light color (no import to replace): painted over
            // after it, around the controls (its text and icon are controls too).
            if (IsMessageBox(h))
            {
                const auto result = DefSubclassProc(h, msg, w, l);
                if (const auto dc = GetDCEx(h, nullptr, DCX_CACHE | DCX_CLIPCHILDREN))
                {
                    Fill(dc, h, g_back);
                    ReleaseDC(h, dc);
                }
                return result;
            }
            break;
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

    /// Tab controls (property sheets) have no dark theme: drawn here, light text on dark, the selected tab lighter.
    LRESULT CALLBACK TabProc(HWND h, UINT msg, WPARAM w, LPARAM l, UINT_PTR, DWORD_PTR)
    {
        if (msg == WM_ERASEBKGND) return 1;
        if (msg == WM_PAINT)
        {
            PAINTSTRUCT paint;
            auto dc = BeginPaint(h, &paint);
            RECT client;
            GetClientRect(h, &client);
            FillRect(dc, &client, g_back);
            RECT body = client;
            SendMessageW(h, TCM_ADJUSTRECT, FALSE, reinterpret_cast<LPARAM>(&body));
            InflateRect(&body, 2, 2);
            FrameRect(dc, &body, g_lines);
            auto font = SelectObject(dc, reinterpret_cast<HGDIOBJ>(SendMessageW(h, WM_GETFONT, 0, 0)));
            SetBkMode(dc, TRANSPARENT);
            SetTextColor(dc, IsWindowEnabled(h) ? Text : Dim);
            const auto count = static_cast<int>(SendMessageW(h, TCM_GETITEMCOUNT, 0, 0));
            const auto selected = static_cast<int>(SendMessageW(h, TCM_GETCURSEL, 0, 0));
            for (int i = 0; i < count; i++)
            {
                RECT item;
                if (!SendMessageW(h, TCM_GETITEMRECT, i, reinterpret_cast<LPARAM>(&item))) continue;
                wchar_t text[128] = {};
                TCITEMW info{};
                info.mask = TCIF_TEXT;
                info.pszText = text;
                info.cchTextMax = 127;
                SendMessageW(h, TCM_GETITEMW, i, reinterpret_cast<LPARAM>(&info));
                FillRect(dc, &item, i == selected ? g_field : g_back);
                FrameRect(dc, &item, g_lines);
                DrawTextW(dc, text, -1, &item, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_HIDEPREFIX);
            }
            SelectObject(dc, font);
            EndPaint(h, &paint);
            return 0;
        }
        if (msg == WM_NCDESTROY) RemoveWindowSubclass(h, TabProc, TabId);
        return DefSubclassProc(h, msg, w, l);
    }

#ifdef KOS_SHELL_PROBE // test build: each step to %TEMP%\KosShell-probe.txt, opened per line so a crash keeps what came before it
    void Probe(const char* what, HWND h)
    {
        char path[MAX_PATH], cls[64] = {}, line[200];
        if (!GetTempPathA(MAX_PATH, path) || strcat_s(path, "KosShell-probe.txt") != 0) return;
        GetClassNameA(h, cls, 64);
        const int n = sprintf_s(line, "%lu/%lu %llu %s %p %s\r\n", GetCurrentProcessId(), GetCurrentThreadId(), GetTickCount64(), what, static_cast<void*>(h), cls);
        if (const auto file = CreateFileA(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, 0, nullptr); file != INVALID_HANDLE_VALUE)
        {
            DWORD written;
            if (n > 0) WriteFile(file, line, static_cast<DWORD>(n), &written, nullptr);
            CloseHandle(file);
        }
    }
#else
    void Probe(const char*, HWND) {}
#endif

    void ParentColors(HWND h)
    {
        if (auto parent = GetParent(h)) SetWindowSubclass(parent, FrameProc, FrameId, 0);
    }

    BOOL CALLBACK DarkenChild(HWND h, LPARAM);

    /// Once per window, on its own thread (subclassing works only there).
    void Darken(HWND h)
    {
        // A window shown by another thread (EVENT_OBJECT_SHOW runs there) or a child of another thread's: not marked, so its own
        // thread can still take it.
        if (!IsWindow(h) || GetWindowThreadProcessId(h, nullptr) != GetCurrentThreadId() || GetPropW(h, Mark) || !InScope(h)) return;
        SetPropW(h, Mark, reinterpret_cast<HANDLE>(1));
        Probe(IsWindowVisible(h) ? "darken (visible)" : "darken (hidden)", h);
        wchar_t cls[64] = {};
        // A task dialog's buttons and links sit in DirectUI, which draws the dialog's body light whatever its theme (text colors are
        // read when it's built): left as they are, so the dialog stays readable with only its title bar dark.
        if (const auto parent = GetParent(h); parent && GetClassNameW(parent, cls, 64) && Is(cls, L"CtrlNotifySink")) return;
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
                // In File Explorer a dialog is taken when it's shown: the controls it created before are done now.
                if (g_scope == Scope::Dialogs) EnumChildWindows(h, DarkenChild, 0);
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
        else if (Is(cls, WC_TABCONTROLW)) SetWindowSubclass(h, TabProc, TabId, 0);
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
            // A multiline edit's own scroll bars take the Explorer theme's dark ones; the other fields the dark field theme.
            const bool multiline = Is(cls, WC_EDITW) && (GetWindowLongW(h, GWL_STYLE) & ES_MULTILINE);
            SetWindowTheme(h, multiline || Is(cls, WC_LISTBOXW) || Is(cls, L"ComboLBox") ? L"DarkMode_Explorer" : L"DarkMode_CFD", nullptr);
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
                if (GetWindowThreadProcessId(target, nullptr) == GetCurrentThreadId() && InScope(target)) SetWindowSubclass(target, FrameProc, FrameId, 0);
            }
        }
        else if (g_colors && (Is(cls, L"RICHEDIT50W") || Is(cls, L"RichEdit20W")))
        {
            // A rich edit keeps its own colors (Character Map's "Characters to copy"): the background, and the text from now on.
            SendMessageW(h, EM_SETBKGNDCOLOR, 0, Field);
            CHARFORMAT2W format{};
            format.cbSize = sizeof format;
            format.dwMask = CFM_COLOR;
            format.crTextColor = Text;
            SendMessageW(h, EM_SETCHARFORMAT, SCF_DEFAULT, reinterpret_cast<LPARAM>(&format));
            SendMessageW(h, EM_SETCHARFORMAT, SCF_ALL, reinterpret_cast<LPARAM>(&format));
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
        else
        {
            if (g_colors && event == EVENT_OBJECT_CREATE && GetAncestor(h, GA_ROOT) == h) PatchColors(); // modules loaded since
            Darken(h);
        }
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
        if (g_started.exchange(true)) return;
        g_scope = Allowed();
        if (g_scope == Scope::None) return;
        // The app's windows keep using this code until they close: never unloaded (KOS Engine removes its hook right away).
        HMODULE self;
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN, reinterpret_cast<LPCWSTR>(&Start), &self)) return;
        g_back = CreateSolidBrush(Back);
        g_field = CreateSolidBrush(Field);
        g_hot = CreateSolidBrush(Hot);
        g_lines = CreateSolidBrush(Lines);
        // uxtheme by ordinal (Windows 10 1903 and later): 135 SetPreferredAppMode, 104 RefreshImmersiveColorPolicyState,
        // 136 FlushMenuThemes, 133 AllowDarkModeForWindow. File Explorer keeps its own app mode (it follows Windows' setting).
        if (auto ux = LoadLibraryExW(L"uxtheme.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32))
        {
            using ModeFn = int(WINAPI*)(int);
            using VoidFn = void(WINAPI*)();
            g_allowDark = reinterpret_cast<AllowDarkFn>(GetProcAddress(ux, MAKEINTRESOURCEA(133)));
            if (g_scope == Scope::App)
            {
                if (auto setMode = reinterpret_cast<ModeFn>(GetProcAddress(ux, MAKEINTRESOURCEA(135)))) setMode(2); // ForceDark
                if (auto refresh = reinterpret_cast<VoidFn>(GetProcAddress(ux, MAKEINTRESOURCEA(104)))) refresh();
                if (auto flush = reinterpret_cast<VoidFn>(GetProcAddress(ux, MAKEINTRESOURCEA(136)))) flush();
            }
        }
        // The system colors before anything can call PatchColors (the window hook below does, for each new top-level window).
        // Not with High Contrast on: classic apps follow it through the system colors.
        HIGHCONTRASTW contrast{ sizeof contrast };
        if (g_colors && ((SystemParametersInfoW(SPI_GETHIGHCONTRAST, sizeof contrast, &contrast, 0) && (contrast.dwFlags & HCF_HIGHCONTRASTON)) || !StartColors())) g_colors = false;
        // New windows of this process, on whichever thread creates them (in context: the callback runs on that thread). Installed
        // from a thread of its own that never ends: Windows removes a hook when its thread exits, and in File Explorer the hooked
        // thread is the first dialog's, gone when that dialog closes.
        if (const auto ready = CreateEventW(nullptr, TRUE, FALSE, nullptr))
        {
            const auto owner = CreateThread(nullptr, 0, [](void* signal) -> DWORD
            {
                HMODULE module;
                GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, reinterpret_cast<LPCWSTR>(&OnWindowEvent), &module);
                SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW, module, OnWindowEvent, GetCurrentProcessId(), 0, WINEVENT_INCONTEXT);
                SetEvent(static_cast<HANDLE>(signal));
                Sleep(INFINITE);
                return 0;
            }, ready, 0, nullptr);
            // Windows created meanwhile on other threads would be missed. Not signalled in time: the thread still holds the event.
            if (!owner || WaitForSingleObject(ready, 2000) == WAIT_OBJECT_0) CloseHandle(ready);
            if (owner) CloseHandle(owner);
        }
        // The windows already open on this thread (the one KOS Engine hooked: the app's UI thread).
        Probe("open windows", nullptr);
        EnumWindows(DarkenOpen, 0);
    }
}

/// KOS Engine's thread hook: its WM_NULL starts dark mode in this process (once). Only that one: the hook also sees every message
/// the app sends its own windows, and starting in the middle of the app's setup darkened controls it hadn't finished (the ODBC
/// administrator crashed in odbccp32.dll). A message from another thread arrives only where the app takes messages.
extern "C" LRESULT CALLBACK DarkHook(int code, WPARAM w, LPARAM l)
{
    if (code == HC_ACTION && reinterpret_cast<const CWPSTRUCT*>(l)->message == WM_NULL) Start();
    return CallNextHookEx(nullptr, code, w, l);
}
