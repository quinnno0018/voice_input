#pragma once
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <msctf.h>
#include <atomic>
#include <utility>
#include <cwchar>

// Experiment-only identities; never reuse the production client's identity.
inline constexpr GUID probe_clsid =
    {0x876623a8,0x16ef,0x4834,{0xa5,0x25,0x7a,0xb1,0x7e,0xa0,0x9c,0x83}};
inline constexpr GUID probe_profile =
    {0xdd7e2e9d,0x44f2,0x4d12,{0xa9,0x5d,0x17,0x28,0x23,0x8c,0x20,0x6f}};
inline constexpr GUID probe_key =
    {0x78f66d68,0x5360,0x4926,{0xba,0x1f,0x8b,0xea,0x2b,0x0f,0x43,0xac}};
inline constexpr wchar_t probe_name[] = L"Voice Input TSF Probe";
inline constexpr LANGID probe_language = 0x0804;
inline constexpr TF_PRESERVEDKEY probe_start_key{'J', TF_MOD_CONTROL | TF_MOD_ALT};
extern HINSTANCE probe_module;
extern std::atomic<long> probe_objects;

template<class T> class ComPtr {
    T* value_ = nullptr;
public:
    ComPtr() = default;
    explicit ComPtr(T* value) : value_(value) { if (value_) value_->AddRef(); }
    ComPtr(const ComPtr& other) : ComPtr(other.value_) {}
    ComPtr(ComPtr&& other) noexcept : value_(std::exchange(other.value_, nullptr)) {}
    ~ComPtr() { reset(); }
    ComPtr& operator=(ComPtr other) noexcept { std::swap(value_, other.value_); return *this; }
    T* get() const { return value_; }
    T* operator->() const { return value_; }
    explicit operator bool() const { return value_ != nullptr; }
    void reset() { auto* old = std::exchange(value_, nullptr); if (old) old->Release(); }
    T** put() { reset(); return &value_; }
};

inline bool SameObject(IUnknown* left, IUnknown* right) {
    if (!left || !right) return left == right;
    ComPtr<IUnknown> a, b;
    return SUCCEEDED(left->QueryInterface(IID_IUnknown, reinterpret_cast<void**>(a.put()))) &&
        SUCCEEDED(right->QueryInterface(IID_IUnknown, reinterpret_cast<void**>(b.put()))) &&
        a.get() == b.get();
}

inline void Trace(const wchar_t* action, HRESULT hr = S_OK) {
#ifdef TSF_PROBE_HANDOFF_TRACE
    wchar_t line[512]{};
#else
    wchar_t line[256]{};
#endif
    swprintf_s(line, L"[VoiceInput.TSF] pid=%lu tid=%lu tick=%llu %ls hr=0x%08lx\n",
        GetCurrentProcessId(), GetCurrentThreadId(), GetTickCount64(), action,
        static_cast<unsigned long>(hr));
    OutputDebugStringW(line);
    wchar_t path[MAX_PATH]{};
    const DWORD length = GetTempPathW(MAX_PATH, path);
    if (!length || length + 48 >= MAX_PATH) return;
    swprintf_s(path + length, MAX_PATH - length, L"voice-input-tsf-%lu.log", GetCurrentProcessId());
    HANDLE file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    char bytes[sizeof(line) * 2]{};
    const int count = WideCharToMultiByte(CP_UTF8, 0, line, -1, bytes, sizeof(bytes), nullptr, nullptr);
    DWORD written = 0;
    if (count > 1) WriteFile(file, bytes, static_cast<DWORD>(count - 1), &written, nullptr);
    CloseHandle(file);
}
