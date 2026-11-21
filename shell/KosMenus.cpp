// KOS Shell's context menu icons: File Explorer's classic context menus (folders, items, the desktop) get a monochrome icon on the
// common commands that have none (Cut, Copy, Paste, Delete, Rename, Properties, ...), in the menu's text color.
// File Explorer draws its dark menus itself (every item MFT_OWNERDRAW, so an item bitmap is ignored): after Explorer draws an item
// (WM_DRAWITEM, seen by a WH_CALLWNDPROCRET hook on the thread showing the menu, only while its menus are open), the icon is drawn
// in the item's icon column on the menu window's DC (the item's own DC is composed elsewhere and never reaches the screen).
// Items are matched by their English text (accelerator marks, tabs and a trailing "..." ignored); anything else is left.
// KOS Engine loads this DLL into File Explorer when the switch is on and Explorer shows a menu (MenuHook, one WM_NULL); the switch is
// read again for each menu, so off works at once. Only explorer.exe; the hooks are on Explorer's own threads, never system-wide.
#include <windows.h>
#include <cwchar>
#include <map>
#include <mutex>
#include <atomic>
#include <cstdio>
#include <cstdarg>
#include <share.h>

#ifdef KOS_SHELL_PROBE
extern "C" __declspec(dllexport) BOOL WINAPI MenuSelfCheck();
#endif

namespace
{
    std::atomic<bool> g_started{ false };
    HMODULE g_self;

#ifdef KOS_SHELL_PROBE // test build: why a menu got no icons, in %TEMP%\KosShell-menus.txt
    void Trace(const wchar_t* format, ...)
    {
        wchar_t line[300], path[MAX_PATH];
        va_list args;
        va_start(args, format);
        _vsnwprintf_s(line, _TRUNCATE, format, args);
        va_end(args);
        if (!GetTempPathW(MAX_PATH, path) || wcscat_s(path, L"KosShell-menus.txt") != 0) return;
        if (FILE* f = _wfsopen(path, L"a, ccs=UTF-8", _SH_DENYNO)) { fwprintf(f, L"%lu %s\n", GetCurrentThreadId(), line); fclose(f); }
    }
#define TRACE(...) Trace(__VA_ARGS__)
#else
#define TRACE(...) ((void)0)
#endif

    bool SwitchOn()
    {
        DWORD value = 0, size = sizeof value;
        return RegGetValueW(HKEY_CURRENT_USER, L"Software\\TheKOS\\Shell", L"MenuIcons", RRF_RT_REG_DWORD, nullptr, &value, &size) == ERROR_SUCCESS && value == 1;
    }

    bool Dark()
    {
        DWORD value = 1, size = sizeof value;
        RegGetValueW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize", L"AppsUseLightTheme", RRF_RT_REG_DWORD, nullptr, &value, &size);
        return value == 0;
    }

    /// The item's text as matched: no accelerator marks (&), nothing after a tab, no trailing "..." or spaces, lower case.
    void Normalize(const wchar_t* text, wchar_t* out, size_t size)
    {
        size_t o = 0;
        for (const wchar_t* p = text; *p && *p != L'\t' && o + 1 < size; p++)
        {
            if (*p == L'&') { if (p[1] != L'&') continue; p++; }
            out[o++] = *p;
        }
        while (o > 0 && (out[o - 1] == L'.' || out[o - 1] == L' ' || out[o - 1] == 0x2026)) o--;
        out[o] = 0;
        CharLowerBuffW(out, static_cast<DWORD>(o));
    }

    // Segoe Fluent Icons / Segoe MDL2 Assets code points. Not the commands Windows already shows with an icon of its own (Display
    // settings, Personalize, Run as administrator, Open in Terminal): that icon is in Explorer's item data, so it can't be seen here.
    struct Entry { const wchar_t* Text; wchar_t Glyph; bool Prefix; };
    constexpr Entry Table[] = {
        { L"open", 0xE8E5 }, { L"open in new window", 0xE8A7 }, { L"open in new tab", 0xE8A7 }, { L"open file location", 0xE838 },
        { L"open with", 0xE7AC },
        { L"cut", 0xE8C6 }, { L"copy", 0xE8C8 }, { L"copy as path", 0xE8C8 }, { L"paste", 0xE77F }, { L"paste shortcut", 0xE71B },
        { L"delete", 0xE74D }, { L"rename", 0xE8AC }, { L"properties", 0xE946 }, { L"create shortcut", 0xE71B },
        { L"share", 0xE72D }, { L"send to", 0xE724 }, { L"refresh", 0xE72C }, { L"undo", 0xE7A7, true }, { L"redo", 0xE7A6, true },
        { L"new", 0xE710 }, { L"view", 0xE890 }, { L"sort by", 0xE8CB }, { L"group by", 0xF168 },
        { L"pin to start", 0xE718 }, { L"pin to quick access", 0xE718 }, { L"pin to taskbar", 0xE718 }, { L"unpin", 0xE77A, true },
        { L"print", 0xE749 }, { L"edit", 0xE70F }, { L"restore previous versions", 0xE81C }, { L"include in library", 0xE8F1 },
        { L"empty recycle bin", 0xE74D }, { L"select all", 0xE8B3 },
        { L"map network drive", 0xE8CE }, { L"disconnect network drive", 0xE8CD }, { L"customize this folder", 0xE771 },
    };

    wchar_t GlyphFor(const wchar_t* key)
    {
        for (const auto& e : Table)
        {
            const size_t n = wcslen(e.Text);
            if (e.Prefix ? (wcsncmp(key, e.Text, n) == 0 && (key[n] == 0 || key[n] == L' ')) : wcscmp(key, e.Text) == 0) return e.Glyph;
        }
        return 0;
    }

    std::mutex g_lock; // the font cache (menus show on several Explorer threads)
    std::map<int, HFONT> g_fonts; // by pixel size; kept for Explorer's life
    // The draw hook of this thread while one of its menus is open, and how many of its menus (and submenus) are open.
    thread_local HHOOK t_hook = nullptr;
    thread_local int t_open = 0;

    bool IsMenuWindow(HWND h)
    {
        wchar_t cls[16] = {};
        return h && GetClassNameW(h, cls, 16) && CompareStringOrdinal(cls, -1, L"#32768", -1, FALSE) == CSTR_EQUAL;
    }

    /// The icon font at that size, or null (neither icon font installed: no icons).
    HFONT Font(int px)
    {
        std::lock_guard guard(g_lock);
        if (auto it = g_fonts.find(px); it != g_fonts.end()) return it->second;
        HFONT found = nullptr;
        if (HDC dc = CreateCompatibleDC(nullptr))
        {
            for (const wchar_t* face : { L"Segoe Fluent Icons", L"Segoe MDL2 Assets" })
            {
                HFONT font = CreateFontW(-px, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY, DEFAULT_PITCH, face);
                if (!font) continue;
                HGDIOBJ old = SelectObject(dc, font);
                wchar_t got[LF_FACESIZE] = {};
                GetTextFaceW(dc, LF_FACESIZE, got);
                SelectObject(dc, old);
                if (_wcsicmp(got, face) == 0) { found = font; break; } // a substitute font would draw letters
                DeleteObject(font);
            }
            DeleteDC(dc);
        }
        g_fonts[px] = found;
        return found;
    }

    /// After Explorer drew a menu item: its icon in the icon column when the item's text is a known command.
    void Draw(const DRAWITEMSTRUCT& d)
    {
        if (d.CtlType != ODT_MENU || !d.hDC || !SwitchOn()) return;
        const auto menu = reinterpret_cast<HMENU>(d.hwndItem);
        wchar_t text[128] = {}, key[128];
        MENUITEMINFOW item{ sizeof item, MIIM_FTYPE | MIIM_STATE | MIIM_STRING | MIIM_BITMAP };
        item.dwTypeData = text;
        item.cch = 127;
        // By position, the item with this id and item data: a lookup by id alone also searches submenus and finds the first match.
        BOOL got = FALSE;
        for (int i = 0, n = GetMenuItemCount(menu); i < n && !got; i++)
        {
            item.cch = 127;
            item.fMask = MIIM_FTYPE | MIIM_STATE | MIIM_STRING | MIIM_BITMAP | MIIM_ID | MIIM_DATA;
            got = GetMenuItemInfoW(menu, i, TRUE, &item) && item.wID == d.itemID && item.dwItemData == d.itemData;
        }
        TRACE(L"draw id %u got %d type 0x%X state 0x%X bmp %p '%s' rc %ld,%ld", d.itemID, got, item.fType, item.fState, item.hbmpItem, text, d.rcItem.left, d.rcItem.top);
        if (!got || (item.fType & (MFT_SEPARATOR | MFT_RADIOCHECK)) || (item.fState & MFS_CHECKED) || item.hbmpItem) return;
        Normalize(text, key, 128);
        const wchar_t glyph = GlyphFor(key);
        if (!glyph) return;
        HWND window = WindowFromDC(d.hDC); // the menu window
        if (!IsMenuWindow(window)) return; // not another popup an extension draws
        const UINT dpi = GetDpiForWindow(window) ? GetDpiForWindow(window) : 96;
        const int px = MulDiv(16, dpi, 96);
        HFONT font = Font(px);
        if (!font) return;
        const bool dark = Dark(), grayed = (d.itemState & (ODS_GRAYED | ODS_DISABLED)) != 0;
        const COLORREF color = dark ? (grayed ? RGB(110, 110, 110) : RGB(230, 230, 230)) : (grayed ? RGB(160, 160, 160) : RGB(30, 30, 30));
        RECT box{ d.rcItem.left + MulDiv(11, dpi, 96), d.rcItem.top, 0, d.rcItem.bottom }; // Explorer's own icons start there
        box.right = box.left + px;
        // On the menu window's own DC: what's drawn on the item's DC doesn't reach the screen (measured: Explorer's dark menus
        // compose the item elsewhere).
        HDC own = GetDC(window);
        if (!own) return;
        HGDIOBJ old = SelectObject(own, font);
        SetBkMode(own, TRANSPARENT);
        SetTextColor(own, color);
        DrawTextW(own, &glyph, 1, &box, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | DT_NOCLIP);
        SelectObject(own, old);
        ReleaseDC(window, own);
        TRACE(L"  glyph %04X at %ld,%ld", glyph, box.left, box.top);
    }

    LRESULT CALLBACK AfterMessage(int code, WPARAM w, LPARAM l)
    {
        if (code == HC_ACTION)
        {
            const auto m = reinterpret_cast<const CWPRETSTRUCT*>(l);
            if (m->message == WM_DRAWITEM && m->lParam) Draw(*reinterpret_cast<const DRAWITEMSTRUCT*>(m->lParam));
#ifdef KOS_SHELL_PROBE
            else if (m->message == WM_MEASUREITEM) TRACE(L"measureitem to %p", m->hwnd);
#endif
        }
        return CallNextHookEx(nullptr, code, w, l);
    }

    /// A menu shown on this Explorer thread: the draw hook on that thread (once), then the menu redrawn so its first paint has icons.
    /// A menu shown on this Explorer thread: the draw hook on that thread while its menus are open (removed when the last one
    /// closes), and the first menu redrawn so its first paint has icons.
    void CALLBACK OnPopup(HWINEVENTHOOK, DWORD event, HWND window, LONG, LONG, DWORD, DWORD)
    {
        if (!IsMenuWindow(window)) return;
        if (event == EVENT_SYSTEM_MENUPOPUPEND)
        {
            if (t_open > 0 && --t_open == 0 && t_hook) { UnhookWindowsHookEx(t_hook); t_hook = nullptr; }
            return;
        }
        t_open++;
        if (t_hook || !SwitchOn()) return;
        t_hook = SetWindowsHookExW(WH_CALLWNDPROCRET, AfterMessage, g_self, GetCurrentThreadId());
        TRACE(L"popup %p hooked %d", window, t_hook != nullptr);
        if (t_hook) RedrawWindow(window, nullptr, nullptr, RDW_INVALIDATE | RDW_UPDATENOW);
    }

    /// Only in File Explorer (%windir%\explorer.exe), once per process.
    void Start()
    {
        if (g_started.exchange(true)) return;
        wchar_t exe[MAX_PATH], path[MAX_PATH];
        const DWORD length = GetModuleFileNameW(nullptr, exe, MAX_PATH);
        const UINT folder = GetSystemWindowsDirectoryW(path, MAX_PATH);
        if (length == 0 || length >= MAX_PATH || folder == 0 || folder >= MAX_PATH || wcscat_s(path, L"\\explorer.exe") != 0
            || CompareStringOrdinal(exe, -1, path, -1, TRUE) != CSTR_EQUAL) return;
        // Explorer's menus keep calling this code: never unloaded (KOS Engine removes its own hook right away).
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN, reinterpret_cast<LPCWSTR>(&Start), &g_self)) return;
#ifdef KOS_SHELL_PROBE
        TRACE(L"self-check %d", MenuSelfCheck());
#endif
        // Every menu shown or closed in this process, on the thread showing it. The event hook belongs to a thread of its own that never ends
        // (Windows removes a hook when its thread exits).
        if (const auto owner = CreateThread(nullptr, 0, [](void*) -> DWORD
            {
                if (SetWinEventHook(EVENT_SYSTEM_MENUPOPUPSTART, EVENT_SYSTEM_MENUPOPUPEND, g_self, OnPopup, GetCurrentProcessId(), 0, WINEVENT_INCONTEXT))
                    Sleep(INFINITE);
                return 0;
            }, nullptr, 0, nullptr))
            CloseHandle(owner);
    }
}

/// KOS Engine's thread hook: its WM_NULL starts the menu icons in this process (once), not a message Explorer sends itself.
extern "C" LRESULT CALLBACK MenuHook(int code, WPARAM w, LPARAM l)
{
    if (code == HC_ACTION && reinterpret_cast<const CWPSTRUCT*>(l)->message == WM_NULL) Start();
    return CallNextHookEx(nullptr, code, w, l);
}

#ifdef KOS_SHELL_PROBE
/// Probe build: the text matching, checked once when the DLL loads in a probe process.
extern "C" __declspec(dllexport) BOOL WINAPI MenuSelfCheck()
{
    wchar_t k[64];
    Normalize(L"Cu&t", k, 64); bool ok = wcscmp(k, L"cut") == 0 && GlyphFor(k) == 0xE8C6;
    Normalize(L"&Properties\tAlt+Enter", k, 64); ok &= wcscmp(k, L"properties") == 0;
    Normalize(L"Undo &Rename", k, 64); ok &= GlyphFor(k) == 0xE7A7;
    Normalize(L"Undone", k, 64); ok &= GlyphFor(k) == 0;
    Normalize(L"Pin to Start menu (Open-Shell)", k, 64); ok &= GlyphFor(k) == 0;
    Normalize(L"Customize this &folder...", k, 64); ok &= GlyphFor(k) == 0xE771;
    Normalize(L"R&&D...", k, 64); ok &= wcscmp(k, L"r&d") == 0;
    return ok;
}
#endif
