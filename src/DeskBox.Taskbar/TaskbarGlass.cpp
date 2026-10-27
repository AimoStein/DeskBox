// DeskBox's optional Windows 11 taskbar background bridge.
// Uses the Windows SDK XAML Diagnostics API; no hooks or patched system code.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#undef GetCurrentTime
#include <ocidl.h>
#include <windows.ui.xaml.h>
#include <xamlom.h>
#include <windows.ui.xaml.hosting.desktopwindowxamlsource.h>
#undef GetCurrentTime
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.UI.Xaml.h>
#include <winrt/Windows.UI.Xaml.Media.h>
#include <winrt/Windows.UI.Xaml.Shapes.h>
#include <winrt/Windows.UI.Xaml.Hosting.h>
#include <array>
#include <string>
#include <thread>
#include <vector>
#include <algorithm>

namespace xaml = winrt::Windows::UI::Xaml;
namespace media = xaml::Media;
namespace shapes = xaml::Shapes;
namespace hosting = xaml::Hosting;

// Shared with TaskbarGlass.cs. Explicit 8-byte alignment on both sides.
struct Slot { UINT64 window; LONG desired, applied, known, reserved; };
struct State {
    UINT32 magic, version, ownerPid;
    LONG enabled;
    UINT64 heartbeat;
    LONG error, callbacks;
    UINT32 bridgeId, reserved;
    Slot slots[16];
};
static_assert(sizeof(Slot) == 24 && offsetof(State, slots) == 40);
constexpr UINT32 Magic = 0x47425444;
constexpr UINT32 BuildId() {
    UINT32 value = 2166136261u;
    for (auto c : __DATE__ " " __TIME__) value = (value ^ static_cast<unsigned char>(c)) * 16777619u;
    return value;
}
constexpr UINT32 BridgeId = BuildId();
constexpr GUID SiteId{0xcb6db63e, 0xd442, 0x4e63, {0x8b,0xaf,0x91,0x4a,0x75,0x18,0xd6,0x22}};
static HMODULE moduleHandle;

static std::wstring MappingName() {
    DWORD session{}; ProcessIdToSessionId(GetCurrentProcessId(), &session);
    return L"Local\\DeskBox.TaskbarGlass.v2." + std::to_wstring(session);
}

// The mapped view stays valid across controller restarts. No file/registry IPC.
struct Mapping {
    HANDLE handle{};
    HANDLE owner{};
    DWORD ownerId{};
    State* state{};
    State* Get() {
        if (!state) {
            handle = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, MappingName().c_str());
            if (handle) state = static_cast<State*>(MapViewOfFile(handle, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(State)));
            if (!state && handle) { CloseHandle(handle); handle = nullptr; }
        }
        return state && state->magic == Magic && state->version == 2 ? state : nullptr;
    }
    bool Alive(State const& current) {
        if (ownerId != current.ownerPid) {
            if (owner) CloseHandle(owner);
            ownerId = current.ownerPid;
            owner = OpenProcess(SYNCHRONIZE, FALSE, ownerId);
        }
        return owner && WaitForSingleObject(owner, 0) == WAIT_TIMEOUT;
    }
    ~Mapping() { if (state) UnmapViewOfFile(state); if (handle) CloseHandle(handle); if (owner) CloseHandle(owner); }
};

struct Background {
    InstanceHandle handle{};
    winrt::weak_ref<shapes::Shape> shape;
    media::Brush original{nullptr};
    media::SolidColorBrush transparent{nullptr};
    HWND taskbar{};
    bool changed{};
};

// All XAML objects below are accessed only on their own UI thread.
struct ThreadState {
    Mapping mapping;
    std::vector<winrt::weak_ref<hosting::DesktopWindowXamlSource>> sources;
    std::vector<Background> backgrounds;
    xaml::DispatcherTimer timer{nullptr};

    HWND FindTaskbar(shapes::Shape const& shape) {
        auto root = shape.XamlRoot();
        if (!root) return nullptr;
        for (auto const& weak : sources) {
            if (auto source = weak.get()) {
                if (source.Content() != root.Content()) continue;
                HWND island{};
                winrt::check_hresult(source.as<IDesktopWindowXamlSourceNative>()->get_WindowHandle(&island));
                auto window = GetAncestor(island, GA_ROOT);
                wchar_t name[100]{}; GetClassNameW(window, name, 100);
                if (wcscmp(name, L"Shell_TrayWnd") == 0 || wcscmp(name, L"Shell_SecondaryTrayWnd") == 0) return window;
            }
        }
        return nullptr;
    }

    void Tick() noexcept {
        auto state = mapping.Get();
        auto active = state && state->bridgeId == BridgeId && state->enabled &&
            GetTickCount64() - state->heartbeat < 3000 && mapping.Alive(*state);
        std::erase_if(sources, [](auto const& source) { return !source.get(); });
        for (auto it = backgrounds.begin(); it != backgrounds.end();) {
            try {
                auto shape = it->shape.get();
                if (!shape) { it = backgrounds.erase(it); continue; }
                if (!IsWindow(it->taskbar)) it->taskbar = FindTaskbar(shape);
                Slot* slot{};
                if (state && it->taskbar) {
                    for (auto& candidate : state->slots)
                        if (candidate.window == reinterpret_cast<UINT64>(it->taskbar)) { slot = &candidate; break; }
                }
                if (slot) InterlockedExchange(&slot->known, 1);
                const bool transparent = active && slot && slot->desired;
                auto current = shape.Fill();
                // Preserve a replacement system brush (theme/transparency changes).
                if (current != it->transparent) it->original = current;
                if (transparent && current) {
                    if (!it->transparent) {
                        it->transparent = media::SolidColorBrush();
                        it->transparent.Opacity(0);
                    }
                    if (current != it->transparent) shape.Fill(it->transparent);
                    it->changed = true;
                    if (slot) InterlockedExchange(&slot->applied, 1);
                } else if (!transparent && it->changed) {
                    if (current == it->transparent) shape.Fill(it->original);
                    it->changed = false;
                    if (slot) InterlockedExchange(&slot->applied, 0);
                }
            } catch (...) {
                if (state) InterlockedExchange(&state->error, winrt::to_hresult());
            }
            ++it;
        }
    }

    void Start() {
        if (timer) return;
        timer = xaml::DispatcherTimer();
        timer.Interval(std::chrono::milliseconds(100));
        timer.Tick([this](auto&&, auto&&) { Tick(); });
        timer.Start();
    }
};
static thread_local ThreadState* threadState{};

struct Site : winrt::implements<Site, IObjectWithSite, IVisualTreeServiceCallback2> {
    winrt::com_ptr<IUnknown> site;
    winrt::com_ptr<IXamlDiagnostics> diagnostics;
    HRESULT __stdcall SetSite(IUnknown* value) noexcept override {
        try {
            if (!value) return S_OK;
            if (site) return S_OK;
            site.copy_from(value);
            diagnostics = site.as<IXamlDiagnostics>();
            auto self = get_strong();
            std::thread([self] {
                try {
                    winrt::init_apartment(winrt::apartment_type::multi_threaded);
                    winrt::check_hresult(self->site.as<IVisualTreeService>()->AdviseVisualTreeChange(self.get()));
                } catch (...) {
                    Mapping mapping;
                    if (auto state = mapping.Get()) InterlockedExchange(&state->error, winrt::to_hresult());
                }
            }).detach();
            return S_OK;
        } catch (...) { return winrt::to_hresult(); }
    }
    HRESULT __stdcall GetSite(REFIID iid, void** result) noexcept override { return site ? site->QueryInterface(iid, result) : E_FAIL; }
    HRESULT __stdcall OnElementStateChanged(InstanceHandle, VisualElementState, LPCWSTR) noexcept override { return S_OK; }
    HRESULT __stdcall OnVisualTreeChange(ParentChildRelation, VisualElement element, VisualMutationType mutation) noexcept override {
        // Per the SDK contract, strings are owned by the recipient only for Add.
        if (mutation != Add) {
            if (threadState) {
                std::erase_if(threadState->backgrounds, [&](auto const& b) { return b.handle == element.Handle; });
            }
            return S_OK;
        }
        try {
            if (!threadState) threadState = new ThreadState(); // XAML runtime lifetime, never unloaded.
            auto& local = *threadState;
            if (auto state = local.mapping.Get()) InterlockedIncrement(&state->callbacks);
            const std::wstring type(element.Type ? element.Type : L"");
            const std::wstring name(element.Name ? element.Name : L"");
            if (type == L"Windows.UI.Xaml.Hosting.DesktopWindowXamlSource" ||
                name == L"BackgroundFill" || name == L"BackgroundStroke") {
                winrt::com_ptr<::IInspectable> instance;
                winrt::check_hresult(diagnostics->GetIInspectableFromHandle(element.Handle, instance.put()));
                if (auto source = instance.try_as<hosting::DesktopWindowXamlSource>()) {
                    local.sources.push_back(winrt::make_weak(source));
                } else if (auto shape = instance.try_as<shapes::Shape>()) {
                    // Restrict changes to the taskbar tree, never menus or other XAML UI.
                    auto parent = media::VisualTreeHelper::GetParent(shape);
                    while (parent && winrt::get_class_name(parent) != L"Taskbar.TaskbarFrame")
                        parent = media::VisualTreeHelper::GetParent(parent);
                    if (parent && std::none_of(local.backgrounds.begin(), local.backgrounds.end(),
                        [&](auto const& b) { return b.handle == element.Handle; })) {
                        Background background{};
                        background.handle = element.Handle;
                        background.shape = winrt::make_weak(shape);
                        local.backgrounds.push_back(std::move(background));
                        local.Start();
                    }
                }
            }
        } catch (...) { /* Ignore unrelated/unavailable XAML elements. */ }
        SysFreeString(element.SrcInfo.FileName);
        SysFreeString(element.SrcInfo.Hash);
        SysFreeString(element.Name);
        SysFreeString(element.Type);
        return S_OK;
    }
};

struct Factory : winrt::implements<Factory, IClassFactory> {
    HRESULT __stdcall CreateInstance(IUnknown* outer, REFIID iid, void** result) noexcept override {
        if (outer) return CLASS_E_NOAGGREGATION;
        try { return winrt::make<Site>()->QueryInterface(iid, result); }
        catch (...) { return winrt::to_hresult(); }
    }
    HRESULT __stdcall LockServer(BOOL) noexcept override { return S_OK; }
};

STDAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** result) {
    if (clsid != SiteId) return CLASS_E_CLASSNOTAVAILABLE;
    try { return winrt::make<Factory>()->QueryInterface(iid, result); }
    catch (...) { return winrt::to_hresult(); }
}
STDAPI DllCanUnloadNow() { return S_FALSE; }

// Called on a worker thread in DeskBox, not on Explorer's UI thread.
extern "C" __declspec(dllexport) HRESULT __stdcall AttachTaskbar(DWORD processId) noexcept {
    try {
        wchar_t path[32768]{};
        if (!GetModuleFileNameW(moduleHandle, path, 32768)) return HRESULT_FROM_WIN32(GetLastError());
        const auto xaml = LoadLibraryExW(L"Windows.UI.Xaml.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!xaml) return HRESULT_FROM_WIN32(GetLastError());
        auto initialize = reinterpret_cast<decltype(&InitializeXamlDiagnosticsEx)>(GetProcAddress(xaml, "InitializeXamlDiagnosticsEx"));
        HRESULT result = E_NOTIMPL;
        if (initialize) {
            // Each attempt uses a fresh thread: XAML diagnostics initialization is per-thread.
            for (int endpoint = 1; endpoint <= 8; ++endpoint) {
                std::thread worker([&] {
                    const auto connection = L"VisualDiagConnection" + std::to_wstring(endpoint);
                    result = initialize(connection.c_str(), processId, nullptr, path, SiteId, nullptr);
                });
                worker.join();
                if (SUCCEEDED(result)) break;
            }
        }
        FreeLibrary(xaml);
        return result;
    } catch (...) { return winrt::to_hresult(); }
}

extern "C" __declspec(dllexport) UINT32 __stdcall GetBridgeId() noexcept { return BridgeId; }

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) moduleHandle = instance;
    return TRUE;
}
