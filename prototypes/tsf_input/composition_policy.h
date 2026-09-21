#pragma once
#include <cstdint>
#include <string_view>

namespace probe {
// Escaped Chinese text keeps compiler source encoding irrelevant.
inline constexpr std::wstring_view frames[] = {
    L"\u660e", L"\u660e\u5929", L"\u660e\u5929\u4e0b",
    L"\u660e\u5929\u4e0b\u5348", L"\u660e\u5929\u4e0b\u5348\u4e09",
    L"\u660e\u5929\u4e0b\u5348\u4e09\u70b9",
    L"\u660e\u5929\u4e0b\u5348\u56db\u70b9",
    L"\u660e\u5929\u4e0b\u5348\u56db\u70b9\u68c0\u67e5",
    L"\u660e\u5929\u4e0b\u5348\u56db\u70b9\u68c0\u67e5 Web",
    L"\u660e\u5929\u4e0b\u5348\u56db\u70b9\u68c0\u67e5 WebSocket",
    L"\u660e\u5929\u4e0b\u5348\u56db\u70b9\u68c0\u67e5 WebSocket con",
    L"\u660e\u5929\u4e0b\u5348\u56db\u70b9\u68c0\u67e5 WebSocket connection",
    L"\u660e\u5929\u4e0b\u5348\u56db\u70b9\u68c0\u67e5 WebSocket connection\u3002"
};
inline constexpr unsigned frame_count = sizeof(frames) / sizeof(frames[0]);
inline constexpr unsigned frame_interval_ms = 350;

struct Playback {
    std::uint64_t generation = 0;
    unsigned index = 0;
    bool running = false;
    void Start() { ++generation; index = 0; running = true; }
    void Stop() { ++generation; running = false; }
    bool Accepts(std::uint64_t ticket) const { return running && ticket == generation; }
    bool Final() const { return index + 1 == frame_count; }
    void Applied() { if (Final()) Stop(); else ++index; }
};

inline bool CanReplace(bool same_target, bool caret_at_end,
                       std::wstring_view owned, std::wstring_view observed) {
    return same_target && caret_at_end && owned == observed;
}
}
