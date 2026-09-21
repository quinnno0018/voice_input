#include "probe.h"
#include "composition_policy.h"
#include <iostream>
#include <stdexcept>
#include <string>
#include <cstring>

template<class T> T LoadExport(HMODULE dll, const char* name) {
    const auto address = GetProcAddress(dll, name);
    T function = nullptr;
    static_assert(sizeof(function) == sizeof(address), "Windows function pointer size");
    std::memcpy(&function, &address, sizeof(function));
    return function;
}

static unsigned passed = 0;
static void Check(bool condition, const char* name) {
    if (!condition) throw std::runtime_error(name);
    ++passed;
    std::cout << "[PASS] " << name << '\n';
}

int wmain(int argc, wchar_t** argv) {
    if (argc != 2) { std::cerr << "Pass the probe DLL path.\n"; return 2; }
    try {
        Check(probe_start_key.uVKey == 'J', "probe trigger uses J instead of an Fn-layer key");
        Check(probe_start_key.uModifiers == (TF_MOD_CONTROL | TF_MOD_ALT),
            "probe trigger requires Ctrl+Alt");
        probe::Playback playback;
        playback.Start();
        const auto first = playback.generation;
        Check(playback.Accepts(first), "current generation accepts updates");
        playback.Stop();
        Check(!playback.Accepts(first), "interruption rejects pending update");
        playback.Start();
        Check(!playback.Accepts(first), "restart cannot replay old updates");
        unsigned commits = 0;
        std::wstring owned, document = L"existing-prefix:";
        const auto prefix = document;
        while (playback.running) {
            Check(probe::CanReplace(true, true, owned, document.substr(prefix.size())),
                "owned range matches before update");
            owned = probe::frames[playback.index];
            document.replace(prefix.size(), document.size() - prefix.size(), owned);
            if (playback.Final()) ++commits;
            playback.Applied();
        }
        Check(commits == 1 && document == prefix + std::wstring(probe::frames[probe::frame_count - 1]),
            "one stable commit without duplication or prefix changes");
        Check(!probe::CanReplace(true, true, L"abc", L"abXc"), "user text change blocks replacement");
        Check(!probe::CanReplace(false, true, L"abc", L"abc"), "changed target blocks replacement");
        Check(!probe::CanReplace(true, false, L"abc", L"abc"), "moved caret blocks replacement");
        Check(probe::frames[5].substr(0, 4) == probe::frames[6].substr(0, 4) &&
            probe::frames[5][4] != probe::frames[6][4], "fixture includes a non-append ASR revision");

        HMODULE dll = LoadLibraryW(argv[1]);
        Check(dll != nullptr, "DLL loads without registration");
        auto getClass = LoadExport<HRESULT (WINAPI*)(REFCLSID,REFIID,void**)>(
            dll, "DllGetClassObject");
        auto canUnload = LoadExport<HRESULT (WINAPI*)()>(dll, "DllCanUnloadNow");
        Check(getClass && canUnload && GetProcAddress(dll,"DllRegisterServer") &&
            GetProcAddress(dll,"DllUnregisterServer"), "four COM exports are present");
        Check(canUnload() == S_OK, "module initially unloadable");
        ComPtr<IClassFactory> factory;
        Check(getClass(probe_clsid, IID_IClassFactory, reinterpret_cast<void**>(factory.put())) == S_OK,
            "class factory exposes expected CLSID");
        Check(canUnload() == S_FALSE, "factory pins DLL");
        ComPtr<ITfTextInputProcessor> service;
        Check(factory->CreateInstance(nullptr, IID_ITfTextInputProcessor,
            reinterpret_cast<void**>(service.put())) == S_OK, "factory creates TSF service");
        Check(service->Activate(nullptr, 0) == E_INVALIDARG, "activation rejects null manager");
        for (const GUID* iid : {&IID_ITfKeyEventSink, &IID_ITfCompositionSink,
                &IID_ITfThreadMgrEventSink, &IID_ITfThreadFocusSink, &IID_ITfTextEditSink}) {
            ComPtr<IUnknown> sink;
            Check(SUCCEEDED(service->QueryInterface(*iid, reinterpret_cast<void**>(sink.put()))) &&
                SameObject(service.get(), sink.get()), "sink has consistent COM identity");
        }
        ComPtr<IUnknown> unsupported;
        Check(service->QueryInterface(IID_IClassFactory,
            reinterpret_cast<void**>(unsupported.put())) == E_NOINTERFACE,
            "service rejects unsupported interface");
        Check(SUCCEEDED(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED)), "COM apartment initialized");
        ComPtr<ITfThreadMgr> manager;
        Check(SUCCEEDED(CoCreateInstance(CLSID_TF_ThreadMgr, nullptr, CLSCTX_INPROC_SERVER,
            IID_ITfThreadMgr, reinterpret_cast<void**>(manager.put()))), "real TSF thread manager created");
        TfClientId client = 0;
        Check(SUCCEEDED(manager->Activate(&client)), "real TSF thread manager activated locally");
        ComPtr<ITfClientId> clients;
        Check(SUCCEEDED(manager->QueryInterface(IID_ITfClientId,
            reinterpret_cast<void**>(clients.put()))), "TSF exposes client identity service");
        Check(SUCCEEDED(clients->GetClientId(probe_clsid, &client)), "probe gets a distinct TSF object identity");
        clients.reset();
        const HRESULT activated = service->Activate(manager.get(), client);
        if (FAILED(activated)) std::cerr << "Activate HRESULT=" << std::hex << activated << std::dec << '\n';
        Check(service->Deactivate() == S_OK, "service deactivation succeeds");
        Check(service->Deactivate() == S_OK, "repeated deactivation is safe");
        Check(manager->Deactivate() == S_OK, "local TSF manager deactivates");
        manager.reset();
        CoUninitialize();
        service.reset();
        factory.reset();
        Check(canUnload() == S_OK, "COM references fully released");
        Check(FreeLibrary(dll), "DLL unloads without registration");
        std::cout << passed << " checks passed before activation verdict.\n";
        Check(SUCCEEDED(activated), "service sinks and timer activate without registration");
        std::cout << passed << " checks passed. No target input or registry was modified.\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "[FAIL] " << error.what() << '\n'; return 1;
    }
}
