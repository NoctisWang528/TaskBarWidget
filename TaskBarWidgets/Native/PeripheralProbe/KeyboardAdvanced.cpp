#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include "hidapi.h"
#include "KeyboardAdvanced.h"
#include <algorithm>
#include <array>
#include <numeric>
#include <stdexcept>
#include <utility>

namespace peripheral::keyboard_advanced
{
namespace
{
using Value = std::optional<Bytes>;
unsigned Sum(const Bytes& b, size_t first, size_t end)
{ return std::accumulate(b.begin() + first, b.begin() + end, 0u); }
unsigned U16(const Bytes& b, size_t at) { return b[at] | unsigned(b[at + 1]) << 8; }
unsigned U32(const Bytes& b, size_t at) { return U16(b, at) | U16(b, at + 2) << 16; }
bool Prefix(const Bytes& b, std::initializer_list<unsigned char> prefix)
{ return b.size() >= prefix.size() && std::equal(prefix.begin(), prefix.end(), b.begin()); }
Bytes Packet(size_t size, std::initializer_list<unsigned char> prefix)
{ Bytes b(size); std::copy(prefix.begin(), prefix.end(), b.begin()); return b; }
std::string Key(std::string s)
{
    auto first = s.find_first_not_of(" \t\r\n"), last = s.find_last_not_of(" \t\r\n");
    if (first == std::string::npos) return {};
    s = s.substr(first, last - first + 1);
    for (auto& c : s) if (c >= 'A' && c <= 'Z') c += 'a' - 'A';
    return s;
}
std::string Ascii(const wchar_t* text)
{
    std::string result;
    if (text) for (; *text; ++text) { if (*text > 127) return {}; result.push_back(char(*text)); }
    return result;
}
bool UniqueReport(const Candidate& c, Report type, unsigned report, unsigned native)
{
    unsigned matches = 0;
    for (const auto& [key, bytes] : c.layout.bytes)
        if (key.first == type && bytes + (key.second == 0 ? 1 : 0) == native) ++matches;
    return matches == 1 && native > (report == 0 ? 1u : 0u) &&
        c.layout.Has(type, report, native - (report == 0 ? 1 : 0));
}
bool Pair(const Candidate& c, unsigned report, unsigned length)
{
    const unsigned native = length + (report == 0 ? 1 : 0);
    return c.input == native && c.output == native && UniqueReport(c, Report::Input, report, native) &&
        UniqueReport(c, Report::Output, report, native);
}
std::optional<unsigned> FeatureReport(const Candidate& c)
{
    std::optional<unsigned> report;
    for (const auto& [key, length] : c.layout.bytes)
    {
        if (key.first != Report::Feature || length + (key.second == 0 ? 1 : 0) != c.feature) continue;
        if (report) return {};
        report = key.second;
    }
    return report;
}
Reading Battery(unsigned value, std::optional<bool> charging = {}, bool estimated = false)
{
    Reading r; r.percentage = int(value); r.charging = charging; r.estimated = estimated; return r;
}

// Progress precedes every blocking native call. The parent watchdog bounds a
// stuck driver; these independent limits bound responsive but unhelpful devices.
struct Session
{
    const Candidate& c;
    const std::function<void()>& progress;
    hid_device* handle = nullptr;
    ULONGLONG deadline = GetTickCount64() + 8000;
    unsigned calls = 0;
    Session(const Candidate& candidate, const std::function<void()>& update) : c(candidate), progress(update)
    { if (Step()) handle = hid_open_path(c.path.c_str()); }
    ~Session()
    {
        if (handle) { try { if (progress) progress(); } catch (...) {} hid_close(handle); }
    }
    bool Step()
    {
        if (++calls > 512 || GetTickCount64() >= deadline) return false;
        if (progress) progress();
        return true;
    }
    bool Revalidate(Layout* identityDescriptor = nullptr)
    {
        if (!handle || !Step()) return false;
        const auto* info = hid_get_device_info(handle);
        if (!info || info->vendor_id != c.vid || info->product_id != c.pid || info->release_number != c.revision ||
            info->interface_number != c.mi || info->usage_page != c.page || info->usage != c.usage ||
            !info->path || Key(info->path) != Key(c.path)) return false;
        if (c.adapter == "mchose_k99v3" && Key(Ascii(info->product_string)) != Key(c.usbProduct)) return false;
        if (!Step()) return false;
        Bytes descriptor(4096);
        const int count = hid_get_report_descriptor(handle, descriptor.data(), descriptor.size());
        if (count <= 0 || count > int(descriptor.size())) return false;
        descriptor.resize(count);
        const auto actual = ParseDescriptor(descriptor);
        if (identityDescriptor)
        {
            Candidate observed = c; observed.layout = actual;
            if (!actual.valid || !Pair(observed, 1, 64)) return false;
            *identityDescriptor = actual; return true;
        }
        return actual.valid && actual.bytes == c.layout.bytes;
    }
    bool Write(const Bytes& b, bool feature = false, bool cleanup = false)
    {
        if (!handle || b.empty()) return false;
        if (cleanup) { if (progress) progress(); }
        else if (!Step()) return false;
        const int sent = feature ? hid_send_feature_report(handle, b.data(), b.size()) : hid_write(handle, b.data(), b.size());
        return sent == int(b.size());
    }
    Value Input(unsigned size, int wait = 40)
    {
        if (!Step()) return {};
        Bytes b(size); const int count = hid_read_timeout(handle, b.data(), b.size(), wait);
        if (count < 0 || count > int(b.size())) return {};
        b.resize(count); return b;
    }
    bool Drain()
    {
        for (unsigned i = 0; i < 32; ++i)
        { auto b = Input(std::max(c.input, 65u), 1); if (!b) return false; if (b->empty()) return true; }
        return false;
    }
    Value Feature(const Bytes& packet)
    {
        if (!Write(packet, true)) return {};
        Sleep(10);
        if (!Step()) return {};
        Bytes b(packet.size()); b[0] = packet[0];
        const int count = hid_get_feature_report(handle, b.data(), b.size());
        if (count != int(b.size()) || b[0] != packet[0] || b == packet) return {};
        return b;
    }
    Value Exchange(const Bytes& packet, const std::function<Value(const Bytes&)>& decode, unsigned wait = 1000)
    {
        if (!Drain() || !Write(packet)) return {};
        const auto until = std::min(deadline, GetTickCount64() + wait);
        for (unsigned i = 0; i < 32 && GetTickCount64() < until; ++i)
        {
            auto b = Input(std::max(c.input, 65u));
            if (!b) return {};
            if (!b->empty()) if (auto payload = decode(*b)) return payload;
        }
        return {};
    }
};

Bytes DareuRequest(bool flex, unsigned target, bool power)
{ return Packet(65, {static_cast<unsigned char>(flex ? 10 : 0), static_cast<unsigned char>(target),
    static_cast<unsigned char>(power ? (flex ? 4 : 3) : (flex ? 3 : 7)),
    static_cast<unsigned char>(power ? 7 : 0), static_cast<unsigned char>(power ? 0x80 : 0xa0)}); }
Value DareuPayload(const Bytes& b, bool flex, unsigned target, bool power)
{
    if (b.size() != 65 || b[0] != (flex ? 10 : 0) || b[3] != (power ? 7 : 0) ||
        b[4] != (power ? 0x80 : 0xa0) || ((b[1] & 0xf0) != 0 && (b[1] & 0xf0) != target) ||
        (b[1] & 15) != 2 || b == DareuRequest(flex, target, power)) return {};
    return Bytes(b.begin() + 1, b.end());
}
std::optional<unsigned> DareuTarget(const Bytes& b, unsigned product)
{
    if (b.size() != 64 || b[6] > 15) return {};
    std::optional<unsigned> target;
    for (unsigned i = 0; i < b[6]; ++i)
    {
        unsigned at = 7 + i * 3;
        if (b[at] > 1) return {};
        if (b[at] == 1 && (unsigned(b[at + 1]) << 8 | b[at + 2]) == product)
        { if (target) return {}; target = (i + 1) << 4; }
    }
    return target;
}
std::optional<Reading> DareuBattery(const Bytes& b, bool flex)
{
    if (b.size() != 64 || b[6] > (flex ? 1 : 2) || b[7] > 100 || (flex && !b[7])) return {};
    return Battery(b[7], b[6] != 0, true);
}
std::optional<Reading> ReadDareu(Session& s)
{
    const bool flex = s.c.adapter == "dareu_flex87", direct = s.c.pid == 0x51;
    auto get = [&](unsigned target, bool power) -> Value
    {
        const auto packet = DareuRequest(flex, target, power);
        for (unsigned i = 0; i < 8; ++i)
        { const auto raw = s.Feature(packet); if (!raw) return {}; if (auto b = DareuPayload(*raw, flex, target, power)) return b; }
        return {};
    };
    auto identify = [&]() -> std::optional<unsigned>
    { if (direct) return 0; auto b = get(0, false); return b ? DareuTarget(*b, flex ? 0x210 : 0x51) : std::nullopt; };
    const auto target = identify(); if (!target) return {};
    std::optional<Reading> result;
    for (int i = 0; i < 2; ++i) { auto b = get(*target, true); if (!b || !(result = DareuBattery(*b, flex))) return {}; }
    if (identify() != target) return {};
    if (result && !flex) result->charging.reset(); // DB179: A98 charging is not verified.
    return result;
}

Bytes TitanRequest(unsigned command)
{
    auto b = Packet(32, {6, static_cast<unsigned char>(command), 0, 0, 24});
    const unsigned checksum = Sum(b, 1, b.size()); b[5] = checksum & 255; b[6] = checksum >> 8;
    return b;
}
Value TitanPayload(const Bytes& b, unsigned command)
{
    const auto request = TitanRequest(command);
    if (b.size() != 32 || b[0] != 6 || !std::equal(b.begin() + 1, b.begin() + 4, request.begin() + 1) || b == request) return {};
    return Bytes(b.begin() + 1, b.end());
}
std::optional<unsigned> TitanIdentity(const Bytes& b)
{
    if (b.size() != 31 || b[7] || b[8] || !U16(b, 9) || U16(b, 9) == 65535 || b[16] < 1 || b[16] > 3) return {};
    return U16(b, 9) | unsigned(b[16]) << 16;
}
std::optional<Reading> TitanBattery(const Bytes& b)
{
    if (b.size() != 31 || b[7] > 100 || b[8] > 2 || b[9] > 1) return {};
    return Battery(b[7], {}, true);
}
std::optional<Reading> ReadTitan(Session& s)
{
    auto get = [&](unsigned command) { return s.Exchange(TitanRequest(command),
        [command](const Bytes& b) { return TitanPayload(b, command); }, 800); };
    const auto online = [&] { auto b = get(0x55); return b && (*b)[7] == 1; };
    if (!online()) return {};
    auto b = get(0x12); auto identity = b ? TitanIdentity(*b) : std::nullopt; if (!identity) return {};
    std::optional<Reading> result;
    for (int i = 0; i < 2; ++i)
    {
        b = get(0x1f);
        if (!b || !(result = TitanBattery(*b))) return {};
    }
    b = get(0x12);
    if (!b || TitanIdentity(*b) != identity || !online()) return {};
    result->peer = std::to_string(*identity); return result;
}

Bytes GlwRequest(unsigned command, unsigned offset, unsigned count)
{
    auto b = Packet(65, {0, 0x55, static_cast<unsigned char>(command), 0, 0,
        static_cast<unsigned char>(count), static_cast<unsigned char>(offset), static_cast<unsigned char>(offset >> 8), 0});
    b[4] = Sum(b, 5, 9) & 255; return b;
}
Value GlwPayload(const Bytes& b, unsigned command, unsigned offset, unsigned count)
{
    if (b.size() != 64 || count > 56 || !Prefix(b, {0xaa, static_cast<unsigned char>(command)}) ||
        b[4] != count || U16(b, 5) != offset) return {};
    return Bytes(b.begin() + 8, b.begin() + 8 + count);
}
std::optional<unsigned> GlwSlot(const Bytes& b)
{
    if (b.size() != 56 || b[0] >= 4) return {};
    std::array<unsigned char, 4> order{}; std::copy_n(b.begin() + 2, 4, order.begin());
    auto sorted = order; std::sort(sorted.begin(), sorted.end());
    if (sorted != std::array<unsigned char, 4>{0, 1, 2, 3}) return b[0];
    if (b[1] > 4) return {};
    return order[b[0]];
}
std::optional<Reading> GlwBattery(const Bytes& first, const Bytes& tail)
{
    if (first.size() != 56 || tail.size() != 8 || (first[32] & 127) > 100) return {};
    return Battery(first[32] & 127, (first[32] & 128) != 0);
}
std::optional<Reading> ReadGlw(Session& s)
{
    const auto get = [&](unsigned command, unsigned offset, unsigned count) { return s.Exchange(GlwRequest(command, offset, count),
        [=](const Bytes& b) { return GlwPayload(b, command, offset, count); }, 600); };
    std::optional<Reading> result;
    for (int i = 0; i < 2; ++i)
    {
        const auto base = get(4, 0, 56); auto slot = base ? GlwSlot(*base) : std::nullopt; if (!slot) return {};
        const auto first = get(5, *slot * 64, 56), tail = get(5, *slot * 64 + 56, 8);
        const auto after = get(4, 0, 56);
        if (!first || !tail || !after || after->size() != base->size() ||
            !std::equal(base->begin(), base->begin() + 6, after->begin()) || GlwSlot(*after) != slot ||
            !(result = GlwBattery(*first, *tail))) return {};
    }
    return result;
}

struct RelayStatus { unsigned percentage, ready, idle; };
std::optional<RelayStatus> RelayPayload(const Bytes& b)
{
    if (b.size() < 9 || b[1] > 100 || b[6] < 1 || b[6] > 3) return {};
    for (unsigned at : {0u, 3u, 4u, 5u, 8u}) if (b[at] > 1) return {};
    if (b[6] == 2 || b[3] != 1 || b[8] != 0) return {};
    return RelayStatus{b[1], b[0], b[5]};
}
bool RelayModel(unsigned model, bool valkyrie)
{
    if (valkyrie) return model == 3116 || model == 3165 || model == 3907 || model == 3997;
    return model == 1326 || model == 2382;
}
std::optional<Reading> ReadRelay(Session& s)
{
    const auto report = FeatureReport(s.c); if (!report) return {};
    const auto send = [&](std::initializer_list<unsigned char> data) -> Value
    {
        auto packet = Packet(s.c.feature, {static_cast<unsigned char>(*report)});
        std::copy(data.begin(), data.end(), packet.begin() + 1);
        auto b = s.Feature(packet); if (!b) return {};
        return Bytes(b->begin() + 1, b->end());
    };
    const auto status = [&]() -> std::optional<RelayStatus>
    { auto b = send({0xf7}); return b ? RelayPayload(*b) : std::nullopt; };
    const auto idle = [&]() -> std::optional<unsigned>
    { auto value = status(); if (!value || value->ready || !value->idle) return {}; return value->percentage; };
    const auto identify = [&]() -> std::optional<unsigned>
    {
        if (!idle()) return {};
        auto packet = Packet(s.c.feature, {static_cast<unsigned char>(*report), 0xf6, 0x0a});
        if (!s.Write(packet, true)) return {};
        Sleep(10);
        packet = Packet(s.c.feature, {static_cast<unsigned char>(*report), 0x8f, 0, 0, 0, 0, 0, 0, 0x70, 0});
        if (!s.Write(packet, true)) return {};
        for (unsigned attempt = 0; attempt < 5; ++attempt)
        {
            Sleep(100); const auto value = status(); if (!value) return {};
            if (!value->ready) continue;
            auto b = send({0xfc});
            if (!b || b->size() < 9 || (*b)[0] != 0x8f ||
                std::equal(b->begin(), b->end(), packet.begin() + 1) ||
                !RelayModel(U32(*b, 1), s.c.adapter == "valkyrie_vk99_ry")) return {};
            return U32(*b, 1);
        }
        return {};
    };
    const auto identity = identify(); if (!identity) return {};
    const auto first = idle(), second = idle(); if (!first || !second) return {};
    const bool strict = s.c.adapter == "keycool_k19";
    if ((strict && first != second) || identify() != identity) return {};
    const auto final = idle(); if (!final || (strict && final != second)) return {};
    auto result = Battery(*final); result.peer = std::to_string(*identity); return result;
}

Bytes PmoRequest(const Candidate& c, unsigned command)
{
    unsigned report = 0;
    for (const auto& [key, length] : c.layout.bytes)
        if (key.first == Report::Output && length + (key.second == 0 ? 1 : 0) == c.output) report = key.second;
    return Packet(c.output, {static_cast<unsigned char>(report), 0xaa, static_cast<unsigned char>(command), 0, 0, 24});
}
Value PmoPayload(const Bytes& raw, const Bytes& request, unsigned command)
{
    const bool numbered = request[0] != 0;
    if (raw.size() != request.size() - (numbered ? 0 : 1) || (numbered && raw[0] != request[0])) return {};
    Bytes b(raw.begin() + (numbered ? 1 : 0), raw.end());
    if (std::equal(b.begin(), b.end(), request.begin() + 1) || !Prefix(b, {0xaa, static_cast<unsigned char>(command)})) return {};
    if (command != 0xd0 && (!Prefix(b, {0xaa, static_cast<unsigned char>(command), 0, 0, 24}) || b[7] != 0x55)) return {};
    return b;
}
std::optional<unsigned> PmoIdentity(const Bytes& b, bool online = true)
{
    if ((b.size() != 32 && b.size() != 64) || (online && b[13] != 4) || U16(b, 9) != 0x36b0) return {};
    const unsigned product = U16(b, 11);
    if (product != 0x3004 && product != 0x3008 && product != 0x3009 && product != 0x3012 && product != 0x302b) return {};
    return product;
}
std::optional<Reading> PmoBattery(const Bytes& b)
{
    if (b.size() < 32 || b[8] > 100 || b[9] > 1) return {};
    return Battery(b[8]); // DB179 explicitly marks charging as unverified.
}
// A transient query session must be stopped even when acknowledgement/read fails.
struct StopGuard
{
    std::function<void()> stop; bool armed = true;
    ~StopGuard() { if (armed) { try { stop(); } catch (...) {} } }
};
std::optional<Reading> ReadPmo(Session& s)
{
    std::optional<unsigned> expected;
    const auto get = [&](unsigned command) -> Value
    {
        const auto request = PmoRequest(s.c, command);
        bool changed = false;
        auto result = s.Exchange(request, [&](const Bytes& raw) -> Value
        {
            if (auto identity = PmoPayload(raw, request, 0xd0))
            { if (expected && PmoIdentity(*identity) != expected) changed = true; }
            return PmoPayload(raw, request, command);
        }, 600);
        return changed ? std::nullopt : result;
    };
    const auto identify = [&]() -> std::optional<unsigned>
    {
        auto b = get(0xd0); if (!b || (*b)[13] != 4) return {};
        if (auto identity = PmoIdentity(*b)) return identity;
        // Some receivers expose identity only in the start-session response.
        if (U16(*b, 11) != 0) return {};
        StopGuard stop{[&] { s.Write(PmoRequest(s.c, 0x11), false, true); }};
        b = get(0x10); if (!b) return {};
        const auto identity = PmoIdentity(*b, false);
        if (!identity || !get(0x11)) return {};
        stop.armed = false;
        return identity;
    };
    expected = identify(); if (!expected) return {};
    std::optional<Reading> result;
    {
        StopGuard stop{[&] { s.Write(PmoRequest(s.c, 0x11), false, true); }};
        if (!get(0x10)) return {};
        for (int i = 0; i < 2; ++i)
        {
            auto b = get(0x30);
            if (!b || !(result = PmoBattery(*b))) return {};
        }
        if (!get(0x11)) return {};
        stop.armed = false;
    }
    if (identify() != expected) return {};
    result->peer = std::to_string(*expected); return result;
}

Bytes LofreeRequest(unsigned command) { return Packet(32, {4, 0, 0, static_cast<unsigned char>(command)}); }
std::optional<unsigned> LofreeValue(const Bytes& b, unsigned command)
{
    if (b.size() < 9 || b.size() > 32 || b[0] != 4 || (b[3] != command && b[3] != 0) || b[5] || b[6]) return {};
    const auto request = LofreeRequest(command);
    if (std::equal(b.begin(), b.end(), request.begin()) || std::all_of(b.begin() + 1, b.end(), [](unsigned v) { return !v; })) return {};
    if (command == 0x1a && b[8] > 100) return {};
    return b[8];
}
std::optional<Reading> ReadLofree(Session& s)
{
    const auto get = [&](unsigned command) -> std::optional<unsigned>
    {
        StopGuard stop{[&] { s.Write(LofreeRequest(2), false, true); }};
        auto ack = s.Exchange(LofreeRequest(1), [](const Bytes& b) -> Value
        { if (b.size() < 4 || b.size() > 32 || b[0] != 4 || b[3] != 1 || b == LofreeRequest(1)) return {}; return b; }, 500);
        if (!ack) return {};
        auto b = s.Exchange(LofreeRequest(command), [command](const Bytes& raw) -> Value
        { if (!LofreeValue(raw, command)) return {}; return raw; }, 500);
        if (!b) return {};
        if (!s.Write(LofreeRequest(2), false, true)) return {};
        stop.armed = false;
        return LofreeValue(*b, command);
    };
    const auto online = get(0xaa); if (!online || !*online) return {};
    const auto first = get(0x1a), second = get(0x1a); if (!first || !second) return {};
    const auto final = get(0xaa); if (!final || !*final) return {};
    return Battery(*second);
}

struct Fragment
{
    unsigned total = 0, current = 0, sync = 0;
    Bytes payload;
};
Bytes IpiFragment(const Fragment& f, unsigned size)
{
    if (size < 20 || size > 65 || !f.total || f.total > 5 || !f.current || f.current > f.total || f.sync > 7 || f.payload.size() > 14) return {};
    auto b = Packet(size, {9, 0x66, static_cast<unsigned char>(f.total | ((f.sync & 4) << 5)),
        static_cast<unsigned char>(f.current | ((f.sync & 2) << 6)),
        static_cast<unsigned char>(f.payload.size() | ((f.sync & 1) << 7))});
    std::copy(f.payload.begin(), f.payload.end(), b.begin() + 5);
    b[5 + f.payload.size()] = Sum(b, 1, 5 + f.payload.size()) & 255;
    return b;
}
std::optional<Fragment> IpiParse(const Bytes& b, unsigned size)
{
    if (b.size() != size || size < 20 || size > 65 || !Prefix(b, {9, 0x66})) return {};
    Fragment f{unsigned(b[2] & 127), unsigned(b[3] & 127),
        unsigned((b[2] & 128) >> 5 | (b[3] & 128) >> 6 | (b[4] & 128) >> 7), {}};
    const unsigned count = b[4] & 127;
    if (!f.total || f.total > 5 || !f.current || f.current > f.total || count > 14 ||
        b[5 + count] != (Sum(b, 1, 5 + count) & 255)) return {};
    f.payload.assign(b.begin() + 5, b.begin() + 5 + count); return f;
}
struct Assembly
{
    unsigned total = 0, received = 0;
    Bytes data;
    Fragment last;
    // 0 rejects, 1 appends, 2 acknowledges an exact retransmission.
    unsigned Accept(const Fragment& f)
    {
        if (f.payload.empty() || data.size() > 63) return 0;
        if (received && f.current == received && f.total == last.total && f.sync == last.sync && f.payload == last.payload) return 2;
        if ((total && total != f.total) || f.current != received + 1 || data.size() + f.payload.size() > 63) return 0;
        total = f.total; received = f.current; last = f;
        data.insert(data.end(), f.payload.begin(), f.payload.end()); return 1;
    }
    bool Complete() const { return total && received == total; }
};
Bytes IpiRequest(unsigned command)
{
    auto b = Packet(64, {9, static_cast<unsigned char>(command), static_cast<unsigned char>(command == 0x82), 0, 1, 0,
        static_cast<unsigned char>(command == 0x82 ? 6 : 0)});
    b[63] = 255 - (Sum(b, 0, 63) & 255); return b;
}
Value IpiBusiness(const Bytes& b, unsigned command)
{
    const auto request = IpiRequest(command);
    const unsigned count = command == 0x82 ? 6 : 2;
    if (b.size() != 63 || !std::equal(b.begin(), b.begin() + 5, request.begin() + 1) ||
        std::equal(b.begin(), b.end(), request.begin() + 1) || ((9 + Sum(b, 0, b.size())) & 255) != 255 || b[5] != count) return {};
    return Bytes(b.begin() + 6, b.begin() + 6 + count);
}
unsigned IpiNextSync(unsigned& sync) { sync = (sync + 1) & 7; return sync; }
Value IpiExchange(Session& s, unsigned command, unsigned& sync)
{
    if (!s.Drain()) return {};
    const auto request = IpiRequest(command);
    unsigned txSync = 0;
    const unsigned total = unsigned((request.size() + 13) / 14);
    unsigned sent = 0; bool acknowledged = false; Assembly reply;
    const auto send = [&]()
    {
        const unsigned at = sent * 14;
        txSync = IpiNextSync(sync);
        Fragment f{total, sent + 1, txSync, Bytes(request.begin() + at, request.begin() + std::min<size_t>(at + 14, request.size()))};
        ++sent; return s.Write(IpiFragment(f, s.c.output));
    };
    if (!send()) return {};
    const auto until = std::min(s.deadline, GetTickCount64() + 2000);
    for (unsigned reads = 0; reads < 96 && GetTickCount64() < until; ++reads)
    {
        const auto raw = s.Input(s.c.input); if (!raw) return {};
        if (raw->empty() || (*raw)[0] != 9) continue;
        if (raw->size() > 6 && (*raw)[1] == 0x0a && (*raw)[5] == 2 && (*raw)[6] == 0) return {};
        const auto f = IpiParse(*raw, s.c.input); if (!f) return {};
        if (f->payload.empty())
        {
            if (f->total != total || f->current != sent || f->sync != txSync) continue;
            if (sent < total) { if (!send()) return {}; }
            else acknowledged = true;
        }
        else
        {
            if (sent != total || !reply.Accept(*f)) return {};
            if (!s.Write(IpiFragment({f->total, f->current, f->sync, {}}, s.c.output))) return {};
        }
        if (reply.Complete())
        {
            if (auto result = IpiBusiness(reply.data, command)) { if (acknowledged) return result; }
            else reply = {}; // A fully framed older command is never a battery confirmation.
        }
    }
    return {};
}
std::optional<Reading> IpiBattery(const Bytes& b)
{
    if (b.size() != 2 || !b[0] || b[0] > 100) return {};
    return Battery(b[0]); // The phase nibble has no verified charging meaning.
}
std::optional<Reading> ReadIpi(Session& s)
{
    unsigned sync = 0;
    const auto identity = [&]
    {
        const auto b = IpiExchange(s, 0x82, sync);
        return b && *b == Bytes{0x12, 0, 0, 0, 0, 3};
    };
    if (!identity()) return {};
    std::optional<Reading> result;
    for (int i = 0; i < 2; ++i)
    {
        const auto b = IpiExchange(s, 0x87, sync);
        if (!b || !(result = IpiBattery(*b))) return {};
    }
    if (!identity()) return {};
    result->peer = "120000000003"; return result;
}

bool OmniModel(unsigned receiver, unsigned product)
{
    if (receiver == 0x1ace) return product == 0x1a85 || product == 0x1b42 || product == 0x1cf1 ||
        product == 0x1ab0 || product == 0x1b7a || product == 0x1b06;
    return receiver == 0x1ad0 && (product == 0x1c12 || product == 0x1a85 || product == 0x1b42 ||
        product == 0x1c25 || product == 0x1cf1 || product == 0x1da4);
}
std::optional<unsigned> OmniIdentity(const Bytes& b, unsigned receiver)
{
    if (b.size() != 64 || !Prefix(b, {1, 0xa0}) || b == Packet(64, {1, 0xa0})) return {};
    unsigned count = 14;
    if (receiver == 0x1ad0) { if (b[2] || U16(b, 3) > 14) return {}; count = U16(b, 3); }
    std::optional<unsigned> found;
    for (unsigned i = 0; i < count; ++i)
    {
        const unsigned at = 5 + 4 * i, product = U16(b, at);
        if (!product && receiver == 0x1ace) break;
        const bool known = OmniModel(receiver, product);
        if (receiver == 0x1ad0 && (known || b[at + 2] == 2))
        { if (!known || b[at + 2] != 2 || b[at + 3] != 4) return {}; }
        if (known) { if (found) return {}; found = product; }
    }
    return found;
}
std::optional<Reading> OmniBattery(const Bytes& b, unsigned receiver, unsigned product)
{
    if (b.size() != 64 || !Prefix(b, {2, 0x12, 1}) || b == Packet(64, {2, 0x12, 1}) ||
        !OmniModel(receiver, product) || (receiver == 0x1ad0 && (b[3] || b[4])) ||
        b[5] == 0xfa || (b[5] == 0xff && b[6] == 0xaa) || b[9] > 1) return {};
    const unsigned value = b[product == 0x1b06 ? 11 : 6];
    if (value > 100) return {};
    return Battery(value); // Source explicitly marks charging as unverified.
}
bool OmniConnected(const Bytes& b)
{
    return b.size() == 64 && Prefix(b, {2, 0x12, 3, 0, 0}) && b != Packet(64, {2, 0x12, 3}) && b[5] <= 2;
}
std::optional<Reading> ReadOmni(Session& s)
{
    // Central discovery must bind/revalidate the report-1 sibling from the same
    // physical container. Never synthesize a HID path or enumerate here.
    if (s.c.physical.empty() || s.c.transportPath.empty() || Key(s.c.transportPath) == Key(s.c.path)) return {};
    Candidate peer = s.c; peer.path = s.c.transportPath; peer.page = 0xff02;
    if (peer.pid == 0x1ace && Key(peer.path).find("mi_02&col01") == std::string::npos) return {};
    Session identity(peer, s.progress); identity.deadline = s.deadline;
    Layout observed;
    if (!identity.Revalidate(&observed)) return {};
    peer.layout = std::move(observed);
    const auto identify = [&]() -> std::optional<unsigned>
    {
        const auto b = identity.Exchange(Packet(64, {1, 0xa0}), [&](const Bytes& raw) -> Value
        { if (raw.size() != 64 || !Prefix(raw, {1, 0xa0}) || raw == Packet(64, {1, 0xa0})) return {}; return raw; });
        return b ? OmniIdentity(*b, s.c.pid) : std::nullopt;
    };
    const auto model = identify(); if (!model) return {};
    const auto connected = [&]
    {
        if (s.c.pid != 0x1ad0) return true;
        const auto reply = s.Exchange(Packet(64, {2, 0x12, 3}), [](const Bytes& b) -> Value
        {
            if (b.size() != 64 || !Prefix(b, {2, 0x12, 3}) || b == Packet(64, {2, 0x12, 3})) return {};
            return b;
        });
        return reply && OmniConnected(*reply);
    };
    if (!connected()) return {};
    std::optional<Reading> result;
    for (int i = 0; i < 2; ++i)
    {
        const auto b = s.Exchange(Packet(64, {2, 0x12, 1}), [&](const Bytes& raw) -> Value
        { if (raw.size() != 64 || !Prefix(raw, {2, 0x12, 1}) || raw == Packet(64, {2, 0x12, 1})) return {}; return raw; });
        if (!b || !(result = OmniBattery(*b, s.c.pid, *model))) return {};
    }
    if (identify() != model || !connected() || !identity.Revalidate()) return {};
    result->peer = std::to_string(*model); return result;
}
}

bool KnownIdentity(unsigned vid, unsigned pid)
{
    return (vid == 0x3151 && pid == 0x5026) ||
        (vid == 0x374a && (pid == 0xa263 || pid == 0xa278 || pid == 0xa281)) ||
        (vid == 0x260d && (pid == 0x37 || pid == 0x51 || pid == 0x211)) ||
        (vid == 0x3837 && pid == 0x3033) || (vid == 0x36b0 && pid == 0x3002) ||
        (vid == 0x372e && pid == 0x106c) || (vid == 0x056d && pid == 0xc077) ||
        (vid == 0x0b05 && (pid == 0x1ace || pid == 0x1ad0)) ||
        (vid == 0x388d && (pid == 0x24 || pid == 0x25));
}
bool Admit(Candidate& c)
{
    if (!KnownIdentity(c.vid, c.pid) || !c.layout.valid || c.standard || c.pointer || c.keyboard) return false;
    std::string adapter;
    if ((c.vid == 0x3151 || c.vid == 0x374a) && c.mi == 2 && c.page == 0xffff && c.usage == 2 &&
        c.feature >= 10 && c.feature <= 65 && FeatureReport(c))
        adapter = c.vid == 0x3151 ? "keycool_k19" : "valkyrie_vk99_ry";
    if (c.vid == 0x260d && c.page == 12 && c.usage == 1 && c.feature == 65)
    {
        const bool flex = c.pid == 0x211;
        if (c.mi == (flex ? 1 : 3) && FeatureReport(c) == std::optional<unsigned>(flex ? 10 : 0))
            adapter = flex ? "dareu_flex87" : "dareu_a98";
    }
    if (c.vid == 0x3837 && c.page == 1 && c.usage == 0 && Pair(c, 0, 64))
    {
        const auto name = Key(c.usbProduct);
        if (name == "mchose k99 v3 2.4g" ||
            (c.mi == 1 && (name == "mchose g87 v2 2.4g" || name == "mchose g75 v2 2.4g")))
            adapter = "mchose_k99v3";
    }
    if (c.vid == 0x36b0 && c.mi == 1 && c.page == 0xff60 && c.usage == 0x61 &&
        (c.input == 33 || c.input == 65) && c.input == c.output)
    {
        unsigned matches = 0;
        for (const auto& [key, length] : c.layout.bytes)
            if (key.first == Report::Output && Pair(c, key.second, length)) ++matches;
        if (matches == 1) adapter = "pmo_wave75";
    }
    if (c.vid == 0x372e && c.mi == 2 && c.page == 0xff60 && c.usage == 0x61 &&
        c.input >= 20 && c.input <= 65 && c.output >= 20 && c.output <= 65 &&
        UniqueReport(c, Report::Input, 9, c.input) && UniqueReport(c, Report::Output, 9, c.output)) adapter = "ipi_aurora";
    if (c.vid == 0x056d && c.mi == 1 && Pair(c, 6, 32) &&
        ((c.page == 0xff87 && c.usage == 0x20) || (c.page == 0xff88 && c.usage == 0x21) ||
        (c.page == 0xff00 && c.usage == 1))) adapter = "titan_t87";
    if (c.vid == 0x0b05 && c.mi == 2 && c.page == 0xff00 && c.usage == 1 && Pair(c, 2, 64) &&
        (c.pid != 0x1ace || Key(c.path).find("mi_02&col02") != std::string::npos)) adapter = "rog_omni_keyboard";
    if (c.vid == 0x388d && c.page == 0xff1c && c.usage == 0x92 && Pair(c, 4, 32)) adapter = "lofree";
    if (adapter.empty()) return false;
    c.adapter = adapter; c.kind = "keyboard"; c.protocol = Protocol::KeyboardAdvanced;
    c.wired = (c.vid == 0x260d && c.pid == 0x51) || (c.vid == 0x388d && c.pid == 0x24);
    return true;
}
std::optional<Reading> Read(const Candidate& c, const std::function<void()>& progress)
{
    Candidate admitted = c;
    if (!Admit(admitted) || admitted.adapter != c.adapter || c.protocol != Protocol::KeyboardAdvanced) return {};
    Session session(c, progress);
    if (!session.Revalidate()) return {};
    std::optional<Reading> result;
    if (c.adapter == "keycool_k19" || c.adapter == "valkyrie_vk99_ry") result = ReadRelay(session);
    else if (c.adapter == "dareu_a98" || c.adapter == "dareu_flex87") result = ReadDareu(session);
    else if (c.adapter == "mchose_k99v3") result = ReadGlw(session);
    else if (c.adapter == "pmo_wave75") result = ReadPmo(session);
    else if (c.adapter == "ipi_aurora") result = ReadIpi(session);
    else if (c.adapter == "titan_t87") result = ReadTitan(session);
    else if (c.adapter == "lofree") result = ReadLofree(session);
    else if (c.adapter == "rog_omni_keyboard") result = ReadOmni(session);
    if (!result || !session.Revalidate()) return {};
    return result;
}

void OfflineSelfTests()
{
    const auto check = [](const auto& value, const char* name)
    { if (!value) throw std::runtime_error(std::string("KeyboardAdvanced: ") + name); };
    const auto fixture = [](unsigned vid, unsigned pid, int mi, unsigned page, unsigned usage,
        unsigned report, unsigned length, bool feature = false)
    {
        Candidate c; c.vid = vid; c.pid = pid; c.mi = mi; c.page = page; c.usage = usage;
        c.layout.valid = true;
        if (feature) { c.feature = length + (report == 0); c.layout.bytes[{Report::Feature, report}] = length; }
        else
        {
            c.input = c.output = length + (report == 0);
            c.layout.bytes[{Report::Input, report}] = length; c.layout.bytes[{Report::Output, report}] = length;
        }
        return c;
    };
    std::vector<Candidate> accepted;
    accepted.push_back(fixture(0x3151, 0x5026, 2, 0xffff, 2, 0, 64, true));
    for (unsigned pid : {0xa263u, 0xa278u, 0xa281u}) accepted.push_back(fixture(0x374a, pid, 2, 0xffff, 2, 0, 64, true));
    for (unsigned pid : {0x37u, 0x51u}) accepted.push_back(fixture(0x260d, pid, 3, 12, 1, 0, 64, true));
    accepted.push_back(fixture(0x260d, 0x211, 1, 12, 1, 10, 65, true));
    for (auto name : {"MCHOSE K99 V3 2.4G", "MCHOSE G75 V2 2.4G", "MCHOSE G87 V2 2.4G"})
    { auto c = fixture(0x3837, 0x3033, 1, 1, 0, 0, 64); c.usbProduct = name; accepted.push_back(c); }
    accepted.push_back(fixture(0x36b0, 0x3002, 1, 0xff60, 0x61, 0, 32));
    accepted.push_back(fixture(0x372e, 0x106c, 2, 0xff60, 0x61, 9, 20));
    accepted.push_back(fixture(0x056d, 0xc077, 1, 0xff87, 0x20, 6, 32));
    for (unsigned pid : {0x1aceu, 0x1ad0u})
    { auto c = fixture(0x0b05, pid, 2, 0xff00, 1, 2, 64); c.path = "fixture#mi_02&col02"; accepted.push_back(c); }
    for (unsigned pid : {0x24u, 0x25u}) accepted.push_back(fixture(0x388d, pid, -1, 0xff1c, 0x92, 4, 32));
    for (auto c : accepted)
    {
        check(KnownIdentity(c.vid, c.pid) && Admit(c), "full admission");
        check(c.protocol == Protocol::KeyboardAdvanced && c.kind == "keyboard" && !c.adapter.empty(), "admission contract");
        check(c.wired == ((c.vid == 0x260d && c.pid == 0x51) || (c.vid == 0x388d && c.pid == 0x24)), "direct USB connection");
        auto bad = c; bad.vid ^= 0x8000; check(!Admit(bad), "wrong VID");
        bad = c; bad.pid ^= 0x8000; check(!Admit(bad), "wrong PID");
        bad = c; bad.page = 1; bad.usage = 6; check(!Admit(bad), "keyboard input collection");
        bad = c; bad.layout.valid = false; check(!Admit(bad), "missing descriptor");
        bad = c; bad.layout.bytes.clear(); check(!Admit(bad), "missing report");
        bad = c; bad.standard = true; check(!Admit(bad), "standard report exclusion");
        bad = c; bad.pointer = true; check(!Admit(bad), "pointer collection exclusion");
        bad = c; bad.keyboard = true; check(!Admit(bad), "keyboard collection exclusion");
        bad = c; bad.usage ^= 0x40; check(!Admit(bad), "wrong usage");
        bad = c; bad.feature += 1; bad.input += 1; bad.output += 1; check(!Admit(bad), "native caps mismatch");
        if (c.vid != 0x388d && c.vid != 0x3837) { bad = c; bad.mi += 1; check(!Admit(bad), "wrong interface"); }
        // A second equally sized report is ambiguous, even with the expected ID present.
        bad = c;
        if (c.feature) bad.layout.bytes[{Report::Feature, 31}] = c.feature;
        else { bad.layout.bytes[{Report::Input, 31}] = c.input; bad.layout.bytes[{Report::Output, 31}] = c.output; }
        check(!Admit(bad), "ambiguous reports");
    }
    auto name = fixture(0x3837, 0x3033, 1, 1, 0, 0, 64);
    for (auto s : {"MCHOSE K99 V3", "MCHOSE QHW 2.4G", "MCHOSE K99 V3 2.4G extra", ""})
    { name.usbProduct = s; check(!Admit(name), "exact product-name proof"); }
    name.usbProduct = "  mchose g75 v2 2.4g\r\n"; check(Admit(name), "ASCII case/outer whitespace");
    name.mi = 2; check(!Admit(name), "G75 interface proof");
    check(!KnownIdentity(0x41e4, 0x2001), "unproven legacy branch excluded");

    Bytes relay{0, 0, 0, 1, 0, 1, 1, 0, 0};
    check(RelayPayload(relay) && RelayPayload(relay)->percentage == 0, "relay zero is present");
    relay[1] = 100; check(RelayPayload(relay), "relay hundred");
    relay[1] = 101; check(!RelayPayload(relay), "relay out of range"); relay[1] = 30;
    relay[6] = 2; check(!RelayPayload(relay), "relay boot/type exclusion"); relay[6] = 1;
    relay[3] = 0; check(!RelayPayload(relay), "relay offline"); relay[3] = 1;
    relay[8] = 1; check(!RelayPayload(relay), "relay replacement");
    check(!RelayPayload({}) && RelayModel(2382, false) && !RelayModel(3116, false) && RelayModel(3116, true), "relay model identities");

    for (bool flex : {false, true})
    {
        auto b = DareuRequest(flex, 0x10, true);
        check(!DareuPayload(b, flex, 0x10, true), "DAREU query echo");
        b[1] = 0x12; b[7] = 1; b[8] = 100;
        auto payload = DareuPayload(b, flex, 0x10, true);
        check(payload && DareuBattery(*payload, flex)->percentage == 100, "DAREU routed battery");
        check(!DareuPayload(b, flex, 0x20, true), "DAREU wrong target");
        b[1] = 0x11; check(!DareuPayload(b, flex, 0x10, true), "DAREU busy is not confirmation");
        (*payload)[7] = 0; check(bool(DareuBattery(*payload, flex)) != flex, "DAREU source-specific zero");
        (*payload)[6] = 3; check(!DareuBattery(*payload, flex), "DAREU invalid state");
    }
    Bytes pairs(64); pairs[6] = 1; pairs[7] = 1; pairs[8] = 0; pairs[9] = 0x51;
    check(DareuTarget(pairs, 0x51) == 0x10, "DAREU paired identity");
    pairs[6] = 2; pairs[10] = 1; pairs[11] = 0; pairs[12] = 0x51;
    check(!DareuTarget(pairs, 0x51), "DAREU duplicate pairing rejected");
    pairs[6] = 16; check(!DareuTarget(pairs, 0x51) && !DareuTarget({}, 0x51), "DAREU pairing bounds");

    auto titan = TitanRequest(0x1f);
    check(U16(titan, 5) == 0x1f + 24 && !TitanPayload(titan, 0x1f), "Titan checksum and echo");
    titan[8] = 100; titan[9] = 2; titan[10] = 1;
    auto tb = TitanPayload(titan, 0x1f);
    check(tb && TitanBattery(*tb)->percentage == 100 && !TitanBattery(*tb)->charging, "Titan estimated / unknown charging");
    check(!TitanPayload(titan, 0x12), "Titan wrong command");
    (*tb)[7] = 101; check(!TitanBattery(*tb), "Titan invalid value");
    check(!TitanIdentity(Bytes(31)) && !TitanBattery({}), "Titan identity/missing guards");

    const auto request = GlwRequest(5, 192, 56);
    check(request[4] == ((56 + 192) & 255) && request[1] == 0x55 && request[2] == 5, "GLW request checksum");
    auto glw = Packet(64, {0xaa, 5, 0, 0, 56, 192, 0, 0});
    check(GlwPayload(glw, 5, 192, 56) && !GlwPayload(glw, 5, 128, 56) && !GlwPayload(glw, 4, 192, 56), "GLW command/range correlation");
    Bytes base(56); base[0] = 1; base[1] = 4; base[2] = 3; base[3] = 2; base[4] = 1; base[5] = 0;
    check(GlwSlot(base) == 2, "GLW active physical slot");
    base[1] = 0; check(GlwSlot(base) == 2, "GLW zero count independent of active index");
    base[5] = 3; check(GlwSlot(base) == base[0], "GLW direct-index firmware fallback");
    Bytes config(56), tail(8); config[32] = 128;
    check(GlwBattery(config, tail)->percentage == 0 && GlwBattery(config, tail)->charging == true, "GLW zero charging");
    check(!GlwBattery(config, {}), "GLW incomplete profile");
    config[32] = 255; check(!GlwBattery(config, tail), "GLW invalid percentage");

    const auto pmoCandidate = fixture(0x36b0, 0x3002, 1, 0xff60, 0x61, 0, 32);
    const auto pmoRequest = PmoRequest(pmoCandidate, 0x30);
    Bytes pmo(pmoRequest.begin() + 1, pmoRequest.end());
    check(!PmoPayload(pmo, pmoRequest, 0x30), "PMO query echo");
    pmo[7] = 0x55; pmo[8] = 0; pmo[9] = 1;
    check(PmoPayload(pmo, pmoRequest, 0x30) && PmoBattery(pmo)->percentage == 0 && !PmoBattery(pmo)->charging, "PMO zero and unknown charging");
    pmo[9] = 2; check(!PmoBattery(pmo), "PMO invalid status");
    pmo[9] = 0xb0; pmo[10] = 0x36; pmo[11] = 9; pmo[12] = 0x30; pmo[13] = 4;
    check(PmoIdentity(pmo) == 0x3009, "PMO downstream keyboard identity");
    pmo[10] = 0; check(!PmoIdentity(pmo), "PMO foreign downstream VID");

    check(!LofreeValue(LofreeRequest(0x1a), 0x1a), "Lofree query echo");
    auto lofree = Packet(9, {4, 1, 0, 0x1a, 0, 0, 0, 0, 0});
    check(LofreeValue(lofree, 0x1a) == 0, "Lofree zero present");
    lofree[8] = 50; lofree[3] = 0; check(LofreeValue(lofree, 0x1a) == 50, "Lofree neutral reply header");
    lofree[3] = 0x19; check(!LofreeValue(lofree, 0x1a), "Lofree wrong command header"); lofree[3] = 0x1a;
    lofree[8] = 101; check(!LofreeValue(lofree, 0x1a), "Lofree invalid percentage");
    lofree[8] = 50; lofree[5] = 1; check(!LofreeValue(lofree, 0x1a), "Lofree reserved bytes");
    unsigned stopped = 0;
    { StopGuard stop{[&] { ++stopped; }}; }
    try { StopGuard stop{[&] { ++stopped; }}; throw 1; } catch (int) {}
    { StopGuard stop{[&] { ++stopped; }}; stop.armed = false; }
    check(stopped == 2, "session cleanup on failure / no duplicate successful stop");

    for (unsigned sync = 0; sync < 8; ++sync)
    {
        Fragment f{5, 1, sync, Bytes(14, 7)};
        auto raw = IpiFragment(f, 20); auto decoded = IpiParse(raw, 20);
        check(decoded && decoded->sync == sync && decoded->payload == f.payload, "IPI fragment sync/checksum roundtrip");
        raw[5] ^= 1; check(!IpiParse(raw, 20), "IPI corrupt fragment checksum");
    }
    unsigned sync = 0;
    for (unsigned sent = 1; sent <= 16; ++sent) check(IpiNextSync(sync) == (sent & 7), "IPI sync advances per transmitted fragment");
    Assembly assembly;
    Fragment one{2, 1, 0, {1, 2}}, two{2, 2, 1, {3}};
    check(assembly.Accept(two) == 0 && assembly.Accept(one) == 1 && assembly.Accept(one) == 2 && assembly.data.size() == 2, "IPI ordering and exact retransmission");
    one.payload[0] = 9; check(assembly.Accept(one) == 0, "IPI conflicting duplicate");
    check(assembly.Accept(two) == 1 && assembly.Complete() && assembly.data == Bytes{1, 2, 3}, "IPI assembled payload");
    Assembly oversized;
    for (unsigned current = 1; current <= 4; ++current) check(oversized.Accept({5, current, 0, Bytes(14, 1)}), "IPI bounded assembly");
    check(!oversized.Accept({5, 5, 0, Bytes(14, 1)}), "IPI assembly overflow");
    auto ipi = IpiRequest(0x87); Bytes business(ipi.begin() + 1, ipi.end());
    check(!IpiBusiness(business, 0x87), "IPI business echo");
    business[5] = 2; business[6] = 85; business[7] = 0x90;
    business[62] = 255 - ((9 + Sum(business, 0, 62)) & 255);
    check(IpiBusiness(business, 0x87) == Value(Bytes{85, 0x90}) && !IpiBusiness(business, 0x82), "IPI business command / checksum / payload");
    check(IpiBattery({85, 0x90})->percentage == 85 && !IpiBattery({85, 0x90})->charging &&
        !IpiBattery({0, 0}) && !IpiBattery({101, 0}) && !IpiBattery({}), "IPI online range / missing / charging unknown");
    business[20] ^= 1; check(!IpiBusiness(business, 0x87), "IPI business checksum rejection");
    check(!IpiParse({}, 20) && IpiFragment({6, 1, 0, {}}, 20).empty(), "IPI fragment bounds");

    auto omni = Packet(64, {1, 0xa0});
    check(!OmniIdentity(omni, 0x1ace), "Omni query echo");
    omni[5] = 0x06; omni[6] = 0x1b;
    check(OmniIdentity(omni, 0x1ace) == 0x1b06, "Omni legacy downstream identity");
    auto power = Packet(64, {2, 0x12, 1}); power[11] = 90;
    check(OmniBattery(power, 0x1ace, 0x1b06)->percentage == 90 && !OmniBattery(power, 0x1ace, 0x1b06)->charging, "Omni model-specific offset / charging unknown");
    power[5] = 0xff; power[6] = 0xaa; check(!OmniBattery(power, 0x1ace, 0x1b06), "Omni explicit FFAA error");
    omni = Packet(64, {1, 0xa0, 0, 1, 0, 0x12, 0x1c, 2, 4});
    check(OmniIdentity(omni, 0x1ad0) == 0x1c12, "Omni modern count and routing");
    omni[8] = 3; check(!OmniIdentity(omni, 0x1ad0), "Omni wrong notification channel");
    omni[8] = 4; omni[3] = 15; check(!OmniIdentity(omni, 0x1ad0), "Omni pair-list bounds");
    auto connection = Packet(64, {2, 0x12, 3, 0, 0, 1});
    check(OmniConnected(connection), "Omni modern connection");
    connection[5] = 255; check(!OmniConnected(connection), "Omni disconnected sentinel");
}
}
