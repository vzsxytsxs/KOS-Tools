// KOS Shell: makes the Windows 11 taskbar transparent. KOS Engine runs "rundll32 KosShell.dll,Inject <explorer pid>" with Explorer's
// own token; Inject asks Explorer's XAML diagnostics (InitializeXamlDiagnosticsEx) to load this DLL and create the Tap below, which
// watches the taskbar's visual tree. The taskbar's background rectangles (BackgroundFill and BackgroundStroke inside
// Taskbar.TaskbarBackground) get a transparent brush; their own brushes go back when KOS Engine turns it off. Nothing else is touched.
//
// KOS Engine talks to it through two events this DLL creates in Explorer's session: Local\KosShell.<pid>.On (manual reset: set while
// the taskbar should be transparent) and Local\KosShell.<pid>.Changed (KOS Engine sets it after changing On). The DLL stays loaded
// until Explorer exits; turned off, it only keeps watching.
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
#include <string>
#include <vector>
#include <mutex>
#include <functional>
#include <memory>
#include <atomic>

using namespace winrt::Windows::UI::Xaml;

// {E79E0435-211E-4B70-B716-739C4751D08A}
static const CLSID CLSID_KosShellTap = { 0xe79e0435, 0x211e, 0x4b70, { 0xb7, 0x16, 0x73, 0x9c, 0x47, 0x51, 0xd0, 0x8a } };

static HMODULE g_module;

namespace
{
    struct Part
    {
        winrt::weak_ref<Shapes::Rectangle> Rect;
        Media::Brush Original{ nullptr }; // touched on the element's thread only, like Saved and Watching
        DWORD Thread;
        bool Saved = false, Watching = false;
    };

    std::mutex g_lock;
    std::vector<std::shared_ptr<Part>> g_parts;
    std::atomic<bool> g_on = true; // injected only while it's wanted
    HANDLE g_onEvent, g_changedEvent;

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
    UINT g_runMessage = RegisterWindowMessageW(L"KosShell.Run");
    LRESULT CALLBACK RunHook(int code, WPARAM w, LPARAM l)
    {
        if (code == HC_ACTION)
        {
            auto msg = reinterpret_cast<CWPSTRUCT*>(l);
            if (msg->message == g_runMessage && msg->wParam == 0x4B4F53) (*reinterpret_cast<std::function<void()>*>(msg->lParam))();
        }
        return CallNextHookEx(nullptr, code, w, l);
    }

    bool RunOnThread(DWORD thread, const std::function<void()>& fn)
    {
        if (thread == GetCurrentThreadId()) { fn(); return true; }
        HWND window = nullptr;
        EnumThreadWindows(thread, [](HWND h, LPARAM p) { *reinterpret_cast<HWND*>(p) = h; return FALSE; }, reinterpret_cast<LPARAM>(&window));
        if (!window) return false;
        auto hook = SetWindowsHookExW(WH_CALLWNDPROC, RunHook, g_module, thread);
        if (!hook) return false;
        DWORD_PTR result;
        auto sent = SendMessageTimeoutW(window, g_runMessage, 0x4B4F53, reinterpret_cast<LPARAM>(&fn), SMTO_NORMAL, 5000, &result);
        UnhookWindowsHookEx(hook);
        return sent != 0;
    }

    Media::SolidColorBrush Clear() { return Media::SolidColorBrush(winrt::Windows::UI::Color{ 0, 0, 0, 0 }); }

    bool IsClear(const Media::Brush& brush)
    {
        auto solid = brush.try_as<Media::SolidColorBrush>();
        return solid && solid.Color().A == 0;
    }

    /// On the element's thread: transparent, or its own brush back. While on, a brush the taskbar sets again (theme change, visual
    /// state) becomes the one to restore and the rectangle stays transparent.
    void Apply(const std::shared_ptr<Part>& part, bool on)
    {
        auto rect = part->Rect.get();
        if (!rect) return;
        if (on)
        {
            auto fill = rect.Fill();
            if (!part->Saved || !IsClear(fill)) part->Original = fill; // the first time even an empty one, so off puts back exactly that
            part->Saved = true;
            rect.Fill(Clear());
            if (!part->Watching)
            {
                part->Watching = true;
                std::weak_ptr<Part> weak = part;
                rect.RegisterPropertyChangedCallback(Shapes::Shape::FillProperty(), [weak](const DependencyObject& sender, const DependencyProperty&)
                {
                    auto p = weak.lock();
                    if (!p || !g_on) return;
                    auto r = sender.as<Shapes::Rectangle>();
                    if (auto fill = r.Fill(); !IsClear(fill)) { p->Original = fill; r.Fill(Clear()); }
                });
            }
        }
        else if (part->Saved) rect.Fill(part->Original);
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
#endif

    void ControlLoop()
    {
        while (WaitForSingleObject(g_changedEvent, INFINITE) == WAIT_OBJECT_0)
        {
            const bool on = WaitForSingleObject(g_onEvent, 0) == WAIT_OBJECT_0;
            std::vector<DWORD> threads;
            {
                std::lock_guard guard(g_lock);
                g_on = on;
                for (auto& p : g_parts)
                    if (std::find(threads.begin(), threads.end(), p->Thread) == threads.end()) threads.push_back(p->Thread);
            }
            for (auto thread : threads)
                RunOnThread(thread, [on, thread]
                {
                    std::lock_guard guard(g_lock);
                    for (auto& p : g_parts)
                        if (p->Thread == thread)
                            try
                            {
                                Apply(p, on);
#ifdef KOS_SHELL_PROBE
                                if (auto r = p->Rect.get()) Log(std::wstring(on ? L"on" : L"off") + L": fill " + (IsClear(r.Fill()) ? L"clear" : r.Fill() ? L"own" : L"none"));
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
            if ((name != L"BackgroundFill" && name != L"BackgroundStroke") || type != L"Windows.UI.Xaml.Shapes.Rectangle") return S_OK;
#endif
            winrt::Windows::Foundation::IInspectable object;
            winrt::check_hresult(m_diagnostics->GetIInspectableFromHandle(element.Handle, reinterpret_cast<::IInspectable**>(winrt::put_abi(object))));
            auto rect = object.try_as<Shapes::Rectangle>();
#ifdef KOS_SHELL_PROBE
            std::wstring chain;
            for (auto parent = rect ? Media::VisualTreeHelper::GetParent(rect) : nullptr; parent; parent = Media::VisualTreeHelper::GetParent(parent))
                chain += L" < " + std::wstring(winrt::get_class_name(parent));
            Log(L"part " + name + chain);
            if (!rect) return S_OK;
#else
            if (!rect || !IsTaskbarBackground(rect)) return S_OK;
#endif
            std::lock_guard guard(g_lock);
            for (auto& p : g_parts)
                if (p->Rect.get() == rect) return S_OK;
            g_parts.erase(std::remove_if(g_parts.begin(), g_parts.end(), [](const std::shared_ptr<Part>& p) { return !p->Rect.get(); }), g_parts.end());
            auto part = std::make_shared<Part>(Part{ winrt::make_weak(rect), nullptr, GetCurrentThreadId() });
            if (g_on) Apply(part, true);
            g_parts.push_back(part);
            Log(L"found " + name + (g_on ? L": transparent" : L""));
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
                g_onEvent = CreateEventW(nullptr, TRUE, TRUE, (L"Local\\KosShell." + pid + L".On").c_str());
                g_changedEvent = CreateEventW(nullptr, FALSE, FALSE, (L"Local\\KosShell." + pid + L".Changed").c_str());
                if (!g_onEvent || !g_changedEvent) return HRESULT_FROM_WIN32(GetLastError());
                if (GetLastError() == ERROR_ALREADY_EXISTS) g_on = WaitForSingleObject(g_onEvent, 0) == WAIT_OBJECT_0;
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
