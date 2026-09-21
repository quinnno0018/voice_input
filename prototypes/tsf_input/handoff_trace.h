#pragma once
#include "probe.h"
#include <cstdint>

namespace probe {
enum class HandoffStage {
    TestEnter, TestExit, KeyEnter, KeyExit, QueueInput,
    RequestFrame, RequestFinish, RequestReturn, SessionResult, EditEnter,
    EndBefore, EndAfter, HostTermination, RequestKeyboardFinish,
    HostReaffirmBefore, HostReaffirmAfter
};

#ifdef TSF_PROBE_HANDOFF_TRACE
// Fixed numeric records only: never retain a virtual key, character, or text.
struct HandoffTrace {
    static constexpr unsigned capacity = 128;
    struct Entry {
        HandoffStage stage;
        LONGLONG qpc;
        std::uint64_t generation;
        unsigned frame;
        bool running, pending, composing;
        HRESULT hr;
        LONG selection_start, selection_length, composition_start, composition_length;
    };
    Entry entries[capacity]{};
    unsigned count = 0, dropped = 0;
    bool active = false;
    LONGLONG deadline = 0;
    void Reset() { count = dropped = 0; deadline = 0; active = true; }
    void LimitTail() {
        if (!active || deadline) return;
        LARGE_INTEGER now{}, frequency{};
        QueryPerformanceCounter(&now);
        QueryPerformanceFrequency(&frequency);
        deadline = now.QuadPart + frequency.QuadPart / 2;
    }
    void Add(Entry entry) {
        if (!active) return;
        LARGE_INTEGER now{};
        QueryPerformanceCounter(&now);
        if (deadline && now.QuadPart > deadline) return;
        if (count == capacity) { ++dropped; return; }
        entry.qpc = now.QuadPart;
        entries[count++] = entry;
    }
    void Flush() {
        if (!active) return;
        active = false;
        LARGE_INTEGER frequency{};
        QueryPerformanceFrequency(&frequency);
        wchar_t line[256]{};
        swprintf_s(line, L"handoff-v1 qpc_hz=%lld tail_qpc=%lld count=%u dropped=%u",
            frequency.QuadPart, deadline, count, dropped);
        Trace(line);
        static constexpr const wchar_t* names[] = {
            L"test-enter", L"test-exit", L"key-enter", L"key-exit", L"queue-input",
            L"request-frame", L"request-finish", L"request-return", L"session-result", L"edit-enter",
            L"end-before", L"end-after", L"host-termination", L"request-keyboard-finish",
            L"host-reaffirm-before", L"host-reaffirm-after"
        };
        for (unsigned i = 0; i < count; ++i) {
            const auto& e = entries[i];
            swprintf_s(line, L"handoff i=%u qpc=%lld stage=%ls gen=%llu frame=%u r=%d p=%d c=%d sel=%ld,%ld comp=%ld,%ld",
                i, e.qpc, names[static_cast<unsigned>(e.stage)], e.generation, e.frame,
                e.running, e.pending, e.composing, e.selection_start, e.selection_length,
                e.composition_start, e.composition_length);
            Trace(line, e.hr);
        }
    }
};
#endif
}
