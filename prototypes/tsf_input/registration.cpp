#include "probe.h"
#include <string>

namespace {
std::wstring ClassKey() {
    wchar_t guid[40]{};
    StringFromGUID2(probe_clsid, guid, 40);
    return std::wstring(L"Software\\Classes\\CLSID\\") + guid;
}
HRESULT SetString(HKEY key, const wchar_t* name, const wchar_t* text) {
    return HRESULT_FROM_WIN32(RegSetValueExW(key, name, 0, REG_SZ,
        reinterpret_cast<const BYTE*>(text), static_cast<DWORD>((wcslen(text) + 1) * sizeof(wchar_t))));
}
HRESULT Profiles(ITfInputProcessorProfiles** out) {
    return CoCreateInstance(CLSID_TF_InputProcessorProfiles, nullptr, CLSCTX_INPROC_SERVER,
        IID_ITfInputProcessorProfiles, reinterpret_cast<void**>(out));
}
HRESULT Categories(ITfCategoryMgr** out) {
    return CoCreateInstance(CLSID_TF_CategoryMgr, nullptr, CLSCTX_INPROC_SERVER,
        IID_ITfCategoryMgr, reinterpret_cast<void**>(out));
}
}

extern "C" HRESULT WINAPI DllRegisterServer() {
    const HRESULT init = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(init)) return init;
    const HRESULT result = []() -> HRESULT {
        wchar_t path[32768]{};
        const DWORD length = GetModuleFileNameW(probe_module, path, 32768);
        if (!length || length >= 32768) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
        const auto key_path = ClassKey();
        HKEY key = nullptr;
        DWORD disposition = 0;
        LONG error = RegCreateKeyExW(HKEY_LOCAL_MACHINE, key_path.c_str(), 0, nullptr, 0,
            KEY_WRITE | KEY_WOW64_64KEY, nullptr, &key, &disposition);
        if (error != ERROR_SUCCESS) return HRESULT_FROM_WIN32(error);
        if (disposition != REG_CREATED_NEW_KEY) {
            RegCloseKey(key);
            return HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS);
        }
        HRESULT hr = SetString(key, nullptr, probe_name);
        HKEY server = nullptr;
        if (SUCCEEDED(hr)) {
            error = RegCreateKeyExW(key, L"InprocServer32", 0, nullptr, 0,
                KEY_WRITE | KEY_WOW64_64KEY, nullptr, &server, nullptr);
            hr = HRESULT_FROM_WIN32(error);
        }
        if (SUCCEEDED(hr)) hr = SetString(server, nullptr, path);
        if (SUCCEEDED(hr)) hr = SetString(server, L"ThreadingModel", L"Apartment");
        if (server) RegCloseKey(server);
        RegCloseKey(key);

        ComPtr<ITfInputProcessorProfiles> profiles;
        ComPtr<ITfCategoryMgr> categories;
        bool registered = false, added = false, categorized = false;
        if (SUCCEEDED(hr)) hr = Profiles(profiles.put());
        if (SUCCEEDED(hr)) { hr = profiles->Register(probe_clsid); registered = SUCCEEDED(hr); }
        if (SUCCEEDED(hr)) {
            hr = profiles->AddLanguageProfile(probe_clsid, probe_language, probe_profile,
                probe_name, static_cast<ULONG>(wcslen(probe_name)), path, length, 0);
            added = SUCCEEDED(hr);
        }
        if (SUCCEEDED(hr)) hr = Categories(categories.put());
        if (SUCCEEDED(hr)) {
            hr = categories->RegisterCategory(probe_clsid, GUID_TFCAT_TIP_KEYBOARD, probe_clsid);
            categorized = SUCCEEDED(hr);
        }
        if (SUCCEEDED(hr)) hr = profiles->EnableLanguageProfile(
            probe_clsid, probe_language, probe_profile, TRUE);
        // Roll back only entries created by this registration attempt.
        if (FAILED(hr)) {
            if (categorized) categories->UnregisterCategory(probe_clsid, GUID_TFCAT_TIP_KEYBOARD, probe_clsid);
            if (added) profiles->RemoveLanguageProfile(probe_clsid, probe_language, probe_profile);
            if (registered) profiles->Unregister(probe_clsid);
            RegDeleteTreeW(HKEY_LOCAL_MACHINE, key_path.c_str());
        }
        Trace(L"registration", hr);
        return hr;
    }();
    CoUninitialize();
    return result;
}

extern "C" HRESULT WINAPI DllUnregisterServer() {
    const HRESULT init = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(init)) return init;
    const HRESULT result = []() -> HRESULT {
        const auto key_path = ClassKey();
        wchar_t registered_path[32768]{}, module_path[32768]{};
        DWORD bytes = sizeof(registered_path);
        const LONG read = RegGetValueW(HKEY_LOCAL_MACHINE,
            (key_path + L"\\InprocServer32").c_str(), nullptr,
            RRF_RT_REG_SZ | RRF_SUBKEY_WOW6464KEY, nullptr, registered_path, &bytes);
        if (read == ERROR_FILE_NOT_FOUND) return S_OK;
        if (read != ERROR_SUCCESS) return HRESULT_FROM_WIN32(read);
        GetModuleFileNameW(probe_module, module_path, 32768);
        if (_wcsicmp(module_path, registered_path)) return E_ACCESSDENIED;
        ComPtr<ITfInputProcessorProfiles> profiles;
        ComPtr<ITfCategoryMgr> categories;
        HRESULT hr = Profiles(profiles.put());
        if (FAILED(hr)) return hr;
        hr = Categories(categories.put());
        if (FAILED(hr)) return hr;
        hr = profiles->EnableLanguageProfile(probe_clsid, probe_language, probe_profile, FALSE);
        if (FAILED(hr)) return hr;
        hr = profiles->RemoveLanguageProfile(probe_clsid, probe_language, probe_profile);
        if (FAILED(hr)) return hr;
        hr = categories->UnregisterCategory(probe_clsid, GUID_TFCAT_TIP_KEYBOARD, probe_clsid);
        if (FAILED(hr)) return hr;
        hr = profiles->Unregister(probe_clsid);
        if (FAILED(hr)) return hr;
        return HRESULT_FROM_WIN32(RegDeleteTreeW(HKEY_LOCAL_MACHINE, key_path.c_str()));
    }();
    CoUninitialize();
    return result;
}
