#pragma once
#include <windows.h>
#include <cstdint>
#include <cstddef>
#include <string_view>

namespace live {
enum class Kind : std::uint32_t { Hello = 1, Partial = 2, Stable = 3, Ack = 4, Ping = 5 };
inline constexpr unsigned max_text = 1024;
struct Packet {
    std::uint32_t magic = 0x36534956, version = 1;
    Kind kind = Kind::Hello;
    std::uint32_t length = 0;
    GUID session{};
    std::uint64_t sequence = 0, segment = 0, window = 0;
    wchar_t text[max_text]{};
};
static_assert(sizeof(wchar_t) == 2 && sizeof(Packet) == 2104);
static_assert(offsetof(Packet, text) == 56 && offsetof(Packet, sequence) == 32);

inline bool Valid(const Packet& p) {
    if (p.magic != 0x36534956 || p.version != 1 || p.session == GUID_NULL || !p.window ||
        p.length > max_text || p.kind < Kind::Hello || p.kind > Kind::Ping) return false;
    const bool text = p.kind == Kind::Partial || p.kind == Kind::Stable;
    if ((text && (!p.length || !p.segment)) || (!text && p.length) ||
        (p.kind == Kind::Hello && (p.sequence || p.segment)) ||
        (p.kind != Kind::Hello && !p.sequence) || (p.kind == Kind::Ping && p.segment)) return false;
    for (unsigned i = 0; i < p.length; ++i) {
        const auto c = static_cast<unsigned>(p.text[i]);
        if (!c) return false;
        if (c >= 0xd800 && c <= 0xdbff) {
            if (++i >= p.length || p.text[i] < 0xdc00 || p.text[i] > 0xdfff) return false;
        } else if (c >= 0xdc00 && c <= 0xdfff) return false;
    }
    return true;
}

struct Sequence {
    GUID session{};
    std::uint64_t window = 0, last = 0, next_segment = 1;
    bool Accept(const Packet& p) {
        if (!Valid(p) || p.session != session || p.window != window || p.sequence != last + 1 ||
            (p.kind != Kind::Ping && p.kind != Kind::Partial && p.kind != Kind::Stable) ||
            (p.kind != Kind::Ping && p.segment != next_segment)) return false;
        last = p.sequence;
        return true;
    }
    void Applied(const Packet& p) { if (p.kind == Kind::Stable) ++next_segment; }
};
}
