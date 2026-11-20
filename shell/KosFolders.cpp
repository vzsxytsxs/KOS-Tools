// KOS Shell's fix for File Explorer's blank between folders: each folder opened shows an empty view ("Working on it...") for a
// moment, which pressing F11 twice makes go away until the tab is closed.
// Why F11 helps (found with Microsoft's public symbols for ExplorerFrame.dll): CShellBrowser::_OnConnectionCreated holds redraw on
// the tab (WM_SETREDRAW on ShellTabWindowClass) while it swaps the old folder's view for the new one, but only when
// _IsRibbonVisible() differs from _ShouldShowRibbon(). Leaving full screen recreates the frame's command bar, the tab's ribbon proxy
// goes stale and answers "visible" from then on, so every navigation in that tab is held; a new tab gets a fresh proxy.
// This does the same for every tab, with public APIs only (no offsets, nothing patched): when a tab whose current view is known
// creates the next SHELLDLL_DefView, redraw is held on the tab; a message posted when the old view is destroyed (Explorer's swap; the
// rest of that navigation runs before it) releases it, 500 ms at most, with the RedrawWindow flags Explorer uses. The screen keeps
// the old folder until the new one is in place. Views are subclassed only to see them destroyed: each new one, and the views a tab
// had before this DLL loaded, the first time that tab creates one.
// KOS Engine loads this DLL into File Explorer when the switch is on and a folder window is shown: a WH_CALLWNDPROC hook on that
// window's thread only (FolderHook), removed right after one message. The switch is read again before each hold, so off works at once.
#include <windows.h>
#include <commctrl.h>
#include <atomic>

namespace
{
    constexpr UINT_PTR TabId = 0x4B4F5310, ViewId = 0x4B4F5311, TimerId = 0x4B4F5310;
    constexpr const wchar_t* Held = L"KosShell.FolderHeld";
    constexpr const wchar_t* Views = L"KosShell.FolderViews"; // the tab's watched views still alive, plus one once the tab is subclassed
    std::atomic<bool> g_started{ false };
    UINT g_release;

    bool Is(HWND h, const wchar_t* name)
    {
        wchar_t cls[32] = {};
        return GetClassNameW(h, cls, 32) && CompareStringOrdinal(cls, -1, name, -1, FALSE) == CSTR_EQUAL;
    }

    INT_PTR Count(HWND tab) { return reinterpret_cast<INT_PTR>(GetPropW(tab, Views)); }
    void SetCount(HWND tab, INT_PTR n) { SetPropW(tab, Views, reinterpret_cast<HANDLE>(n)); }

    bool SwitchOn()
    {
        DWORD value = 0, size = sizeof value;
        return RegGetValueW(HKEY_CURRENT_USER, L"Software\\TheKOS\\Shell", L"NoBlankFolders", RRF_RT_REG_DWORD, nullptr, &value, &size) == ERROR_SUCCESS
            && value == 1;
    }

    // Another tab chosen during a hold needs nothing here: Explorer keeps every tab visible and puts the active one on top (z-order),
    // so turning redraw back on never shows a tab it had hidden (checked on 26H2 with two tabs and Ctrl+Tab during the hold).
    void Release(HWND tab)
    {
        KillTimer(tab, TimerId);
        if (!RemovePropW(tab, Held)) return;
        SendMessageW(tab, WM_SETREDRAW, TRUE, 0);
        RedrawWindow(tab, nullptr, nullptr, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_FRAME);
    }

    LRESULT CALLBACK TabProc(HWND h, UINT msg, WPARAM w, LPARAM l, UINT_PTR, DWORD_PTR)
    {
        if (msg == g_release || (msg == WM_TIMER && w == TimerId)) { Release(h); return 0; }
        if (msg == WM_NCDESTROY) { Release(h); RemovePropW(h, Views); RemoveWindowSubclass(h, TabProc, TabId); }
        return DefSubclassProc(h, msg, w, l);
    }

    LRESULT CALLBACK ViewProc(HWND h, UINT msg, WPARAM w, LPARAM l, UINT_PTR, DWORD_PTR ref)
    {
        if (msg == WM_NCDESTROY)
        {
            const auto tab = reinterpret_cast<HWND>(ref);
            RemoveWindowSubclass(h, ViewProc, ViewId);
            if (IsWindow(tab) && Count(tab) > 1)
            {
                SetCount(tab, Count(tab) - 1);
                if (GetPropW(tab, Held)) PostMessageW(tab, g_release, 0, 0);
            }
        }
        return DefSubclassProc(h, msg, w, l);
    }

    bool Watch(HWND view, HWND tab)
    {
        if (!SetWindowSubclass(view, ViewProc, ViewId, reinterpret_cast<DWORD_PTR>(tab))) return false;
        SetCount(tab, Count(tab) + 1);
        return true;
    }

    struct Existing { HWND Tab, New; };
    BOOL CALLBACK WatchExisting(HWND h, LPARAM l)
    {
        const auto e = reinterpret_cast<Existing*>(l);
        if (h != e->New && GetWindowThreadProcessId(h, nullptr) == GetCurrentThreadId() && Is(h, L"SHELLDLL_DefView")) Watch(h, e->Tab);
        return TRUE;
    }

    /// In context: on the thread creating the view, before the old one goes.
    void CALLBACK OnCreate(HWINEVENTHOOK, DWORD, HWND view, LONG object, LONG child, DWORD, DWORD)
    {
        if (object != OBJID_WINDOW || child != CHILDID_SELF || !view || !Is(view, L"SHELLDLL_DefView")) return;
        HWND tab = GetParent(view);
        while (tab && !Is(tab, L"ShellTabWindowClass")) tab = GetParent(tab);
        if (!tab || GetWindowThreadProcessId(tab, nullptr) != GetCurrentThreadId()) return;
        if (Count(tab) == 0) // first sight of this tab: its own subclass, then the views it had already
        {
            if (!SetWindowSubclass(tab, TabProc, TabId, 0)) return;
            SetCount(tab, 1);
            Existing e{ tab, view };
            EnumChildWindows(tab, WatchExisting, reinterpret_cast<LPARAM>(&e));
        }
        const bool swapping = Count(tab) > 1; // a watched view is there to be swapped out, so the hold has its release
        if (!Watch(view, tab) || !swapping || !IsWindowVisible(tab) || !SwitchOn()) return;
        if (!GetPropW(tab, Held))
        {
            if (!SetPropW(tab, Held, reinterpret_cast<HANDLE>(1))) return; // no mark, no release: never held
            SendMessageW(tab, WM_SETREDRAW, FALSE, 0);
        }
        SetTimer(tab, TimerId, 500, nullptr);
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
        // Explorer's tabs keep using this code: never unloaded (KOS Engine removes its hook right away). Pinned before any subclass.
        HMODULE self;
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN, reinterpret_cast<LPCWSTR>(&Start), &self)) return;
        g_release = RegisterWindowMessageW(L"KosShell.FolderRelease");
        if (!g_release) return;
        // Every view created in this process, on the thread creating it. The hook belongs to a thread of its own that never ends
        // (Windows removes a hook when its thread exits; the hooked folder window's thread ends with that window).
        if (const auto owner = CreateThread(nullptr, 0, [](void*) -> DWORD
            {
                HMODULE module;
                if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, reinterpret_cast<LPCWSTR>(&OnCreate), &module)
                    && SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_CREATE, module, OnCreate, GetCurrentProcessId(), 0, WINEVENT_INCONTEXT))
                    Sleep(INFINITE);
                return 0;
            }, nullptr, 0, nullptr))
            CloseHandle(owner);
    }
}

/// KOS Engine's thread hook: its WM_NULL starts the folder fix in this process (once), not a message Explorer sends itself.
extern "C" LRESULT CALLBACK FolderHook(int code, WPARAM w, LPARAM l)
{
    if (code == HC_ACTION && reinterpret_cast<const CWPSTRUCT*>(l)->message == WM_NULL) Start();
    return CallNextHookEx(nullptr, code, w, l);
}
