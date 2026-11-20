// KOS Shell: the Windows 11 taskbar's style. KOS Engine runs "rundll32 KosShell.dll,Inject <explorer pid>" with Explorer's own
// token; Inject asks Explorer's XAML diagnostics (InitializeXamlDiagnosticsEx) to load this DLL and create the Tap below, which
// watches the taskbar's visual tree. The taskbar's background rectangles (BackgroundFill and BackgroundStroke inside
// Taskbar.TaskbarBackground) get a transparent brush, so KOS Engine's accent on the taskbar window shows through. When KOS Engine
// asks (the Look below), the buttons' and tray buttons' hover/active backgrounds and the running-app indicators get another color
// at Windows' own strength, and the hidden icons flyout another background. Their own brushes go back when KOS Engine turns it
// off. Nothing else is touched: each element is matched by its name, type and the taskbar classes just above it.
//
// KOS Engine talks to it through what this DLL creates in Explorer's session: the events Local\KosShell.<pid>.On (manual reset: set
// while a style is on) and Local\KosShell.<pid>.Changed (KOS Engine sets it after changing On or the Look), and the 16-byte mapping
// Local\KosShell.<pid>.Look. The DLL stays loaded until Explorer exits; turned off, it only keeps watching.
#include <windows.h>
#include <ocidl.h>
#include <xamlom.h>
#undef GetCurrentTime // windows.h macro; a XAML method has the same name
#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.UI.h>
#include <winrt/Windows.UI.Xaml.h>
#include <winrt/Windows.UI.Xaml.Media.h>
#include <winrt/Windows.UI.Xaml.Shapes.h>
#include <winrt/Windows.UI.Xaml.Controls.h>
#include <winrt/Windows.UI.ViewManagement.h>
#include <optional>
#include <cstring>
#include <string>
#include <vector>
#include <mutex>
#include <functional>
#include <memory>
#include <atomic>
#include <tuple>

using namespace winrt::Windows::UI::Xaml;

// {E79E0435-211E-4B70-B716-739C4751D08A}
static const CLSID CLSID_KosShellTap = { 0xe79e0435, 0x211e, 0x4b70, { 0xb7, 0x16, 0x73, 0x9c, 0x47, 0x51, 0xd0, 0x8a } };

static HMODULE g_module;

namespace
{
    enum class Kind { Background, Button, Overflow };

    struct Part
    {
        winrt::weak_ref<FrameworkElement> Element; // a Rectangle (its Fill) or a Border (its Background)
        Kind What;
        DWORD Thread;
        // Windows' latest brush and the one set here: touched on the element's thread only, like the flags.
        Media::Brush Own{ nullptr }, Mine{ nullptr };
        bool Saved = false, OwnLocal = true, Watching = false;
        bool Setting = false; // inside its own SetValue/ClearValue: the callback it raises (or another writer's) isn't answered
    };

    /// What KOS Engine wants besides the clear background. Buttons: 0xFFRRGGBB recolors the button backgrounds and indicators
    /// keeping their alpha (0: Windows' own). Overflow 1: the hidden icons flyout's background is OverflowColor (ARGB).
    struct Look { UINT32 Version, Buttons, Overflow, OverflowColor; };

    std::mutex g_lock;
    std::vector<std::shared_ptr<Part>> g_parts;
    std::atomic<bool> g_on = true; // injected only while it's wanted
    std::atomic<Look> g_look{}; // all zero (only the clear background) until KOS Engine writes one
    HANDLE g_onEvent, g_changedEvent;
    const volatile Look* g_lookView;

    void Log(const std::wstring& line)
    {
        OutputDebugStringW((L"KosShell: " + line + L"\n").c_str());
#ifdef KOS_SHELL_PROBE // test build (build.cmd probe): the log goes to %TEMP%\KosShell-probe.txt too
        wchar_t path[MAX_PATH];
        if (GetTempPathW(MAX_PATH, path) && wcscat_s(path, L"KosShell-probe.txt") == 0)
            if (auto file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, 0, nullptr); file != INVALID_HANDLE_VALUE)
            {
                const auto text = std::to_wstring(GetCurrentProcessId()) + L"/" + std::to_wstring(GetCurrentThreadId()) + L" " + line + L"\r\n";
                const auto bytes = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, nullptr, 0, nullptr, nullptr);
                std::string utf8(bytes, '\0');
                WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, utf8.data(), bytes, nullptr, nullptr);
                DWORD written;
                WriteFile(file, utf8.data(), static_cast<DWORD>(bytes - 1), &written, nullptr);
                CloseHandle(file);
            }
#endif
    }

    /// Runs fn on a thread that owns windows (the taskbar's UI thread): a WH_CALLWNDPROC hook on that thread and a message to one
    /// of its windows. XAML objects can only be touched on their own thread.
    /// The job is the DLL's own: the message only carries its number, so a late message (after a timeout) or one from another sender
    /// can't make the hook run anything else, and the shared pointer keeps the job alive while it runs.
    struct Job { WPARAM Number; std::function<void()> Work; };
    std::shared_ptr<Job> g_job;
    WPARAM g_jobNumber;
    std::mutex g_runLock; // one job at a time
    UINT g_runMessage = RegisterWindowMessageW(L"KosShell.Run");

    LRESULT CALLBACK RunHook(int code, WPARAM w, LPARAM l)
    {
        if (code == HC_ACTION)
        {
            auto msg = reinterpret_cast<CWPSTRUCT*>(l);
            if (msg->message == g_runMessage)
                if (auto job = std::atomic_load(&g_job); job && msg->wParam == job->Number) job->Work();
        }
        return CallNextHookEx(nullptr, code, w, l);
    }

    bool RunOnThread(DWORD thread, std::function<void()> work)
    {
        if (thread == GetCurrentThreadId()) { work(); return true; }
        HWND window = nullptr;
        EnumThreadWindows(thread, [](HWND h, LPARAM p) { *reinterpret_cast<HWND*>(p) = h; return FALSE; }, reinterpret_cast<LPARAM>(&window));
        if (!window) return false;
        std::lock_guard guard(g_runLock);
        auto job = std::make_shared<Job>(Job{ ++g_jobNumber, std::move(work) });
        std::atomic_store(&g_job, job);
        auto hook = SetWindowsHookExW(WH_CALLWNDPROC, RunHook, g_module, thread);
        DWORD_PTR result;
        auto sent = hook && SendMessageTimeoutW(window, g_runMessage, job->Number, 0, SMTO_NORMAL, 5000, &result);
        if (hook) UnhookWindowsHookEx(hook);
        std::atomic_store(&g_job, std::shared_ptr<Job>());
        return sent;
    }

    Media::SolidColorBrush Solid(UINT32 argb) { return Media::SolidColorBrush(winrt::Windows::UI::Color{ BYTE(argb >> 24), BYTE(argb >> 16), BYTE(argb >> 8), BYTE(argb) }); }

    DependencyProperty BrushProperty(const FrameworkElement& element)
    {
        return element.try_as<Shapes::Rectangle>() ? Shapes::Shape::FillProperty() : Controls::Border::BackgroundProperty();
    }

    /// Windows' brush as it is now, and whether it's the element's own local value (else a style or template gives it: cleared back).
    /// While the local value is still the one set here, Windows' brush came from below it and that answer stays as it was.
    void Remember(Part& part, const FrameworkElement& element, const DependencyProperty& property)
    {
        part.Own = element.GetValue(property).try_as<Media::Brush>();
        const auto local = element.ReadLocalValue(property);
        if (!part.Mine || local.try_as<Media::Brush>() != part.Mine) part.OwnLocal = local != DependencyProperty::UnsetValue();
    }

    /// Windows' neutral (gray) and accent brushes take the chosen color; any other hue (a button asking for attention) stays as it is.
    bool Recolorable(winrt::Windows::UI::Color color)
    {
        if (color.R == color.G && color.G == color.B) return true;
        using winrt::Windows::UI::ViewManagement::UIColorType;
        static const winrt::Windows::UI::ViewManagement::UISettings settings; // agile
        for (auto type : { UIColorType::Accent, UIColorType::AccentDark1, UIColorType::AccentDark2, UIColorType::AccentDark3,
                           UIColorType::AccentLight1, UIColorType::AccentLight2, UIColorType::AccentLight3 })
            if (const auto accent = settings.GetColorValue(type); accent.R == color.R && accent.G == color.G && accent.B == color.B) return true;
        return false;
    }

    /// The brush this part shows instead of Windows' own, or null for Windows' own. Always a new brush: Windows' are shared.
    Media::Brush Wanted(const Part& part)
    {
        if (!g_on) return nullptr;
        const auto look = g_look.load();
        switch (part.What)
        {
        case Kind::Background: return Solid(0);
        case Kind::Overflow: return look.Overflow ? Solid(look.OverflowColor) : nullptr;
        default:
            // Hover, pressed and active differ in alpha only: the same strength in the chosen color; a clear one stays clear.
            if (auto own = part.Own ? part.Own.try_as<Media::SolidColorBrush>() : nullptr; look.Buttons && own && own.Color().A && Recolorable(own.Color()))
                return Solid((UINT32(own.Color().A) << 24) | (look.Buttons & 0xFFFFFF));
            return nullptr;
        }
    }

    /// Sets (or, with no brush, clears) the part's value with its Setting flag up, put down again even when XAML throws.
    void Put(Part& part, const FrameworkElement& element, const DependencyProperty& property, const Media::Brush& brush, bool clear)
    {
        struct Down { bool& flag; ~Down() { flag = false; } } down{ part.Setting };
        part.Setting = true;
        if (clear) element.ClearValue(property); else element.SetValue(property, brush);
    }

    /// On the element's thread: the wanted brush, or Windows' own back. A brush Windows sets again (a theme change; a visual state:
    /// hover, pressed, active) becomes the own one and the wanted brush follows it.
    void Apply(const std::shared_ptr<Part>& part)
    {
        auto element = part->Element.get();
        if (!element) return;
        const auto property = BrushProperty(element);
        if (!part->Saved) { Remember(*part, element, property); part->Saved = true; }
        if (auto want = Wanted(*part)) { part->Mine = want; Put(*part, element, property, want, false); }
        else if (part->Mine) { part->Mine = nullptr; Put(*part, element, property, part->Own, !part->OwnLocal); }
        if (part->Watching) return;
        part->Watching = true;
        std::weak_ptr<Part> weak = part;
        element.RegisterPropertyChangedCallback(property, [weak](const DependencyObject& sender, const DependencyProperty& changed)
        {
            try // Explorer's UI thread, at hover time
            {
                auto p = weak.lock();
                if (!p || p->Setting) return; // no answer to a change made while setting: two writers never recurse
                auto e = sender.as<FrameworkElement>();
                if (p->Mine && e.GetValue(changed).try_as<Media::Brush>() == p->Mine) return; // the one set here
                Remember(*p, e, changed);
                Apply(p);
            }
            catch (...) { }
        });
    }

#ifndef KOS_SHELL_PROBE
    /// The taskbar's own background: BackgroundFill or BackgroundStroke with a Taskbar.TaskbarBackground a few levels up.
    bool IsTaskbarBackground(const DependencyObject& element)
    {
        auto parent = Media::VisualTreeHelper::GetParent(element);
        for (int i = 0; parent && i < 4; i++, parent = Media::VisualTreeHelper::GetParent(parent))
            if (winrt::get_class_name(parent) == L"Taskbar.TaskbarBackground") return true;
        return false;
    }

    std::wstring ClassOf(const DependencyObject& o) { return o ? std::wstring(winrt::get_class_name(o)) : std::wstring(); }

    /// Which part this named element is, by its name, its type and the taskbar classes just above it (BackgroundElement and
    /// BackgroundBorder are common names: Explorer has other XAML); anything else isn't touched.
    std::optional<Kind> KindOf(const std::wstring& name, const FrameworkElement& element)
    {
        const bool rect = element.try_as<Shapes::Rectangle>() != nullptr, border = element.try_as<Controls::Border>() != nullptr;
        if (rect && (name == L"BackgroundFill" || name == L"BackgroundStroke")) return IsTaskbarBackground(element) ? std::optional(Kind::Background) : std::nullopt;
        const auto parent = Media::VisualTreeHelper::GetParent(element);
        const auto p = ClassOf(parent), g = ClassOf(parent ? Media::VisualTreeHelper::GetParent(parent) : nullptr);
        if (rect && name == L"RunningIndicator" && p == L"Taskbar.TaskListLabeledButtonPanel") return Kind::Button;
        if (border && name == L"BackgroundElement" && (p == L"Taskbar.TaskListLabeledButtonPanel" || p == L"Taskbar.TaskListButtonPanel")) return Kind::Button;
        if (border && name == L"BackgroundBorder" && (g == L"SystemTray.OmniButton" || g == L"SystemTray.ChevronIconView" || g == L"SystemTray.NotifyIconView")) return Kind::Button;
        if (border && name == L"OverflowFlyoutBackgroundBorder" && g == L"SystemTray.NotificationAreaOverflow") return Kind::Overflow;
        return std::nullopt;
    }
#endif

    /// KOS Engine's Look, checked (a same-user process could write it too: only these values are taken, and only colors follow).
    Look ReadLook()
    {
        Look look{};
        if (g_lookView) std::memcpy(&look, const_cast<const Look*>(g_lookView), sizeof look);
        if (look.Version != 1 || look.Overflow > 1 || (look.Buttons && look.Buttons >> 24 != 0xFF)) return {};
        return look;
    }

    void ControlLoop()
    {
        while (WaitForSingleObject(g_changedEvent, INFINITE) == WAIT_OBJECT_0)
        {
            const bool on = WaitForSingleObject(g_onEvent, 0) == WAIT_OBJECT_0;
            std::vector<DWORD> threads;
            {
                std::lock_guard guard(g_lock);
                g_on = on;
                g_look = ReadLook();
                for (auto& p : g_parts)
                    if (std::find(threads.begin(), threads.end(), p->Thread) == threads.end()) threads.push_back(p->Thread);
            }
            for (auto thread : threads)
                RunOnThread(thread, [thread]
                {
                    std::lock_guard guard(g_lock);
                    for (auto& p : g_parts)
                        if (p->Thread == thread)
                            try
                            {
                                Apply(p);
#ifdef KOS_SHELL_PROBE
                                if (auto e = p->Element.get()) Log(std::wstring(g_on ? L"on" : L"off") + L": fill " + (p->Mine ? L"set here" : L"own"));
#endif
                            }
                            catch (...) { }
                });
            Log(on ? L"transparent" : L"own brushes back");
        }
    }

    struct Tap : winrt::implements<Tap, IObjectWithSite, IVisualTreeServiceCallback2>
    {
        winrt::com_ptr<IXamlDiagnostics> m_diagnostics;
        winrt::com_ptr<IUnknown> m_site;

        HRESULT STDMETHODCALLTYPE SetSite(IUnknown* site) override try
        {
            m_site.copy_from(site);
            if (!site) return S_OK;
            m_diagnostics = m_site.as<IXamlDiagnostics>();
            // AdviseVisualTreeChange can't be called on the thread that delivers SetSite (it waits for the UI thread): a thread of its own.
            AddRef();
            auto thread = CreateThread(nullptr, 0, [](void* p) -> DWORD
            {
                auto self = static_cast<Tap*>(p);
                auto hr = self->m_diagnostics.as<IVisualTreeService3>()->AdviseVisualTreeChange(self);
                if (FAILED(hr)) Log(L"AdviseVisualTreeChange failed: " + std::to_wstring(hr));
                self->Release();
                return 0;
            }, this, 0, nullptr);
            if (thread) CloseHandle(thread); else Release();
            return S_OK;
        }
        catch (...) { return winrt::to_hresult(); }

        HRESULT STDMETHODCALLTYPE GetSite(REFIID riid, void** site) override { return m_site ? m_site->QueryInterface(riid, site) : E_FAIL; }

        HRESULT STDMETHODCALLTYPE OnVisualTreeChange(ParentChildRelation, VisualElement element, VisualMutationType mutation) override try
        {
            if (mutation != Add || !element.Name || !element.Type) return S_OK;
            const std::wstring name = element.Name, type = element.Type;
#ifdef KOS_SHELL_PROBE // logs the tree's first elements and takes the first few named rectangles of any XAML process as its parts
            static int seen = 0;
            if (seen++ < 300) Log(L"add " + type + L" '" + name + L"'");
            if (name.size() < 9 || name.compare(name.size() - 9, 9, L"TrackRect") != 0 || type != L"Windows.UI.Xaml.Shapes.Rectangle") return S_OK; // theme brushes
            { std::lock_guard guard(g_lock); if (g_parts.size() >= 3) return S_OK; }
#else
            if (name != L"BackgroundFill" && name != L"BackgroundStroke" && name != L"RunningIndicator" && name != L"BackgroundElement"
                && name != L"BackgroundBorder" && name != L"OverflowFlyoutBackgroundBorder") return S_OK;
#endif
            winrt::Windows::Foundation::IInspectable object;
            winrt::check_hresult(m_diagnostics->GetIInspectableFromHandle(element.Handle, reinterpret_cast<::IInspectable**>(winrt::put_abi(object))));
            auto found = object.try_as<FrameworkElement>();
            if (!found) return S_OK;
#ifdef KOS_SHELL_PROBE
            std::wstring chain;
            for (auto parent = Media::VisualTreeHelper::GetParent(found); parent; parent = Media::VisualTreeHelper::GetParent(parent))
                chain += L" < " + std::wstring(winrt::get_class_name(parent));
            Log(L"part " + name + chain);
            const auto kind = Kind::Background;
#else
            const auto which = KindOf(name, found);
            if (!which) return S_OK;
            const auto kind = *which;
#endif
            std::lock_guard guard(g_lock);
            // Only this thread's parts are resolved here (another island, such as the hidden icons flyout, may have its own thread).
            const auto self = GetCurrentThreadId();
            for (auto& p : g_parts)
                if (p->Thread == self && p->Element.get() == found) return S_OK;
            g_parts.erase(std::remove_if(g_parts.begin(), g_parts.end(), [self](const std::shared_ptr<Part>& p) { return p->Thread == self && !p->Element.get(); }), g_parts.end());
            auto part = std::make_shared<Part>(Part{ winrt::make_weak(found), kind, GetCurrentThreadId() });
            Apply(part);
            g_parts.push_back(part);
            if (kind == Kind::Background) Log(L"found " + name + (g_on ? L": transparent" : L""));
            return S_OK;
        }
        catch (...) { return winrt::to_hresult(); }

        HRESULT STDMETHODCALLTYPE OnElementStateChanged(InstanceHandle, VisualElementState, LPCWSTR) override { return S_OK; }
    };

    winrt::com_ptr<Tap> g_tap; // one per Explorer: kept for the process's life

    struct Factory : winrt::implements<Factory, IClassFactory>
    {
        HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid, void** object) override try
        {
            *object = nullptr;
            if (outer) return CLASS_E_NOAGGREGATION;
            if (!g_tap)
            {
                const auto pid = std::to_wstring(GetCurrentProcessId());
                // Only this DLL creates them, once per Explorer: names that already exist are someone else's, and aren't used. On a
                // refusal the ones made here are closed again, so a later load into this Explorer can still create them.
                const auto lookName = L"Local\\KosShell." + pid + L".Look";
                HANDLE look = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(Look), lookName.c_str());
                const auto refuse = [&](const std::wstring& name, HRESULT hr)
                {
                    Log(name + L" already exists: not loading");
                    for (auto h : { &look, &g_onEvent, &g_changedEvent }) if (*h) { CloseHandle(*h); *h = nullptr; }
                    return hr;
                };
                if (!look || GetLastError() == ERROR_ALREADY_EXISTS) return refuse(lookName, look ? E_ACCESSDENIED : HRESULT_FROM_WIN32(GetLastError()));
                for (auto [handle, manual, name] : { std::tuple{ &g_onEvent, TRUE, L".On" }, std::tuple{ &g_changedEvent, FALSE, L".Changed" } })
                {
                    *handle = CreateEventW(nullptr, manual, manual, (L"Local\\KosShell." + pid + name).c_str());
                    if (!*handle || GetLastError() == ERROR_ALREADY_EXISTS) return refuse(L"Local\\KosShell." + pid + name, *handle ? E_ACCESSDENIED : HRESULT_FROM_WIN32(GetLastError()));
                }
                g_lookView = static_cast<const volatile Look*>(MapViewOfFile(look, FILE_MAP_READ, 0, 0, sizeof(Look))); // kept for Explorer's life
                // Its threads run for Explorer's life: the module is never unloaded under them.
                HMODULE pinned;
                GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN, reinterpret_cast<LPCWSTR>(&g_module), &pinned);
                auto loop = CreateThread(nullptr, 0, [](void*) -> DWORD { ControlLoop(); return 0; }, nullptr, 0, nullptr);
                if (loop) CloseHandle(loop);
                g_tap = winrt::make_self<Tap>();
            }
            return g_tap->QueryInterface(riid, object);
        }
        catch (...) { return winrt::to_hresult(); }

        HRESULT STDMETHODCALLTYPE LockServer(BOOL) override { return S_OK; }
    };
}

extern "C" HRESULT STDAPICALLTYPE DllGetClassObject(REFCLSID clsid, REFIID riid, void** object) try
{
    *object = nullptr;
    if (clsid != CLSID_KosShellTap) return CLASS_E_CLASSNOTAVAILABLE;
    return winrt::make<Factory>().as<IUnknown>()->QueryInterface(riid, object);
}
catch (...) { return winrt::to_hresult(); }

extern "C" HRESULT STDAPICALLTYPE DllCanUnloadNow() { return S_FALSE; }

/// rundll32 KosShell.dll,Inject <pid>: connects to that process's XAML diagnostics and has it load this DLL. Exits with the HRESULT.
extern "C" void CALLBACK InjectW(HWND, HINSTANCE, LPWSTR commandLine, int)
{
    const auto pid = wcstoul(commandLine, nullptr, 10);
    wchar_t path[MAX_PATH];
    HRESULT hr = E_INVALIDARG;
    if (pid != 0 && GetModuleFileNameW(g_module, path, MAX_PATH) < MAX_PATH)
    {
        hr = E_NOINTERFACE;
        if (auto xaml = LoadLibraryExW(L"Windows.UI.Xaml.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32))
            if (auto initialize = reinterpret_cast<decltype(&InitializeXamlDiagnosticsEx)>(GetProcAddress(xaml, "InitializeXamlDiagnosticsEx")))
                // Each connection name serves one client: the next one when another tool holds it.
                for (int i = 1; i <= 8; i++)
                {
                    hr = initialize((L"VisualDiagConnection" + std::to_wstring(i)).c_str(), pid, nullptr, path, CLSID_KosShellTap, nullptr);
                    if (hr != HRESULT_FROM_WIN32(ERROR_PIPE_BUSY) && hr != HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS)) break;
                }
    }
    ExitProcess(static_cast<UINT>(hr));
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, void*)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}
