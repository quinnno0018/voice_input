#pragma once
#include "live_protocol.h"
#include <sddl.h>
#include <atomic>
#include <string>
#include <vector>

namespace live {
class Handle {
    HANDLE value_ = nullptr;
public:
    explicit Handle(HANDLE value = nullptr) : value_(value) {}
    ~Handle() { if (value_ && value_ != INVALID_HANDLE_VALUE) CloseHandle(value_); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    HANDLE get() const { return value_; }
};

inline std::vector<BYTE> UserToken(HANDLE process) {
    HANDLE raw = nullptr;
    if (!OpenProcessToken(process, TOKEN_QUERY, &raw)) return {};
    Handle token(raw);
    DWORD size = 0;
    GetTokenInformation(raw, TokenUser, nullptr, 0, &size);
    std::vector<BYTE> bytes(size);
    if (!size || !GetTokenInformation(raw, TokenUser, bytes.data(), size, &size)) return {};
    return bytes;
}

inline std::wstring PipeName() {
    auto user = UserToken(GetCurrentProcess());
    LPWSTR sid = nullptr;
    if (user.empty() || !ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(user.data())->User.Sid, &sid))
        return {};
    std::wstring name = L"\\\\.\\pipe\\VoiceInput.TsfAsr.v1.";
    name += sid;
    LocalFree(sid);
    return name;
}

inline bool SameServerUser(HANDLE pipe) {
    ULONG pid = 0;
    if (!GetNamedPipeServerProcessId(pipe, &pid)) return false;
    Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid));
    if (!process.get()) return false;
    auto self = UserToken(GetCurrentProcess()), peer = UserToken(process.get());
    return !self.empty() && !peer.empty() && EqualSid(
        reinterpret_cast<TOKEN_USER*>(self.data())->User.Sid,
        reinterpret_cast<TOKEN_USER*>(peer.data())->User.Sid);
}

class Client {
    struct Shared {
        std::atomic<long> refs{2};
        Handle stop{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
        Handle ack{CreateEventW(nullptr, FALSE, FALSE, nullptr)};
        SRWLOCK lock = SRWLOCK_INIT;
        Packet hello, pending;
        bool has_packet = false, taken = false;
        std::atomic<bool> dead{false};
        HMODULE module = nullptr;
        void Release() { if (--refs == 0) delete this; }
    };
    Shared* shared_ = nullptr;

    static bool Transfer(HANDLE pipe, HANDLE stop, Packet& packet, bool write) {
        Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        if (!event.get()) return false;
        auto* bytes = reinterpret_cast<BYTE*>(&packet);
        DWORD offset = 0;
        while (offset < sizeof(Packet)) {
            if (WaitForSingleObject(stop, 0) == WAIT_OBJECT_0) return false;
            OVERLAPPED io{};
            io.hEvent = event.get();
            ResetEvent(event.get());
            DWORD count = 0;
            const BOOL ok = write ? WriteFile(pipe, bytes + offset, sizeof(Packet) - offset, &count, &io)
                : ReadFile(pipe, bytes + offset, sizeof(Packet) - offset, &count, &io);
            if (!ok) {
                if (GetLastError() != ERROR_IO_PENDING) return false;
                HANDLE waits[] = {stop, event.get()};
                const auto wait = WaitForMultipleObjects(2, waits, FALSE, 5000);
                if (wait != WAIT_OBJECT_0 + 1) {
                    CancelIoEx(pipe, &io);
                    // Only the worker waits; stack OVERLAPPED storage must outlive cancellation.
                    GetOverlappedResult(pipe, &io, &count, TRUE);
                    return false;
                }
                if (!GetOverlappedResult(pipe, &io, &count, FALSE)) return false;
            }
            if (!count) return false;
            offset += count;
        }
        return true;
    }

    static void Run(Shared* s) {
        const auto name = PipeName();
        if (name.empty()) return;
        Handle pipe(CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr));
        if (pipe.get() == INVALID_HANDLE_VALUE || !SameServerUser(pipe.get()) ||
            !Transfer(pipe.get(), s->stop.get(), s->hello, true)) return;
        Sequence order{s->hello.session, s->hello.window};
        for (;;) {
            Packet packet;
            if (!Transfer(pipe.get(), s->stop.get(), packet, false) || !order.Accept(packet)) return;
            if (packet.kind != Kind::Ping) {
                AcquireSRWLockExclusive(&s->lock);
                s->pending = packet;
                s->has_packet = true;
                s->taken = false;
                ReleaseSRWLockExclusive(&s->lock);
                HANDLE waits[] = {s->stop.get(), s->ack.get()};
                const auto deadline = GetTickCount64() + 5000;
                for (;;) {
                    const auto wait = WaitForMultipleObjects(2, waits, FALSE, 20);
                    if (wait == WAIT_OBJECT_0 + 1) break;
                    if (wait != WAIT_TIMEOUT || GetTickCount64() >= deadline ||
                        !PeekNamedPipe(pipe.get(), nullptr, 0, nullptr, nullptr, nullptr)) return;
                }
                AcquireSRWLockExclusive(&s->lock);
                s->has_packet = false;
                ReleaseSRWLockExclusive(&s->lock);
                order.Applied(packet);
            }
            packet.kind = Kind::Ack;
            packet.length = 0;
            ZeroMemory(packet.text, sizeof(packet.text));
            if (!Transfer(pipe.get(), s->stop.get(), packet, true)) return;
        }
    }

    static DWORD WINAPI Worker(void* data) {
        auto* s = static_cast<Shared*>(data);
        try { Run(s); } catch (...) { /* Fail closed; never unwind into a host process. */ }
        s->dead = true;
        const auto module = s->module;
        s->Release();
        --probe_objects;
        if (module) FreeLibraryAndExitThread(module, 0);
        return 0;
    }

public:
    Client() = default;
    Client(const Client&) = delete;
    Client& operator=(const Client&) = delete;
    ~Client() { Close(); }
    bool Open(HWND target) {
        Close();
        auto* s = new(std::nothrow) Shared();
        if (!s) return false;
        s->hello.window = reinterpret_cast<std::uint64_t>(target);
        if (!s->stop.get() || !s->ack.get() || FAILED(CoCreateGuid(&s->hello.session))) {
            s->Release(); s->Release(); return false;
        }
#ifndef TSF_PROBE_TEST
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
                reinterpret_cast<LPCWSTR>(&Worker), &s->module)) {
            s->Release(); s->Release(); return false;
        }
#endif
        ++probe_objects;
        HANDLE thread = CreateThread(nullptr, 0, Worker, s, 0, nullptr);
        if (!thread) {
            --probe_objects;
            if (s->module) FreeLibrary(s->module);
            s->Release(); s->Release(); return false;
        }
        CloseHandle(thread);
        shared_ = s;
        return true;
    }
    void Close() {
        if (!shared_) return;
        SetEvent(shared_->stop.get());
        shared_->Release();
        shared_ = nullptr;
    }
    bool Dead() const { return shared_ && shared_->dead; }
    bool Take(Packet& packet) {
        if (!shared_) return false;
        AcquireSRWLockExclusive(&shared_->lock);
        const bool available = shared_->has_packet && !shared_->taken;
        if (available) { packet = shared_->pending; shared_->taken = true; }
        ReleaseSRWLockExclusive(&shared_->lock);
        return available;
    }
    void Acknowledge() { if (shared_) SetEvent(shared_->ack.get()); }
};
}
