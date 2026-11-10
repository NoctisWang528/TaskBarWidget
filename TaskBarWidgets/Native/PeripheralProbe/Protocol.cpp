#include "Protocol.h"
#include "ExtraKeyboard.h"
#include "KeyboardAdvanced.h"
#include "HeadsetController.h"
#include "ExtraMouse.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdio>
#include <numeric>
#include <random>

namespace peripheral
{
bool Layout::Has(Report type, unsigned id, unsigned length) const
{
    auto it = bytes.find({type, id});
    return valid && it != bytes.end() && it->second == length;
}

// Parse only descriptor report geometry. No report IDs or lengths are guessed.
Layout ParseDescriptor(const Bytes& descriptor)
{
    Layout result;
    struct Globals { unsigned size = 0, count = 0, id = 0; } g;
    std::vector<Globals> stack;
    std::map<std::pair<Report, unsigned>, unsigned> bits;
    unsigned depth = 0;
    for (size_t i = 0; i < descriptor.size();)
    {
        unsigned prefix = descriptor[i++];
        if (prefix == 0xfe) return result; // Fail closed on unsupported long items.
        unsigned size = prefix & 3; if (size == 3) size = 4;
        if (size > descriptor.size() - i) return result;
        unsigned value = 0;
        for (unsigned j = 0; j < size; ++j) value |= unsigned(descriptor[i++]) << (8 * j);
        unsigned type = (prefix >> 2) & 3, tag = prefix >> 4;
        if (type == 1)
        {
            if (tag == 7) g.size = value;
            else if (tag == 9) g.count = value;
            else if (tag == 8) { if (value == 0 || value > 255) return result; g.id = value; }
            else if (tag == 10) { if (stack.size() >= 32) return result; stack.push_back(g); }
            else if (tag == 11) { if (stack.empty()) return result; g = stack.back(); stack.pop_back(); }
        }
        if (type != 0) continue;
        if (tag == 10) { if (++depth > 64) return result; }
        else if (tag == 12) { if (!depth) return result; --depth; }
        else if (tag == 8 || tag == 9 || tag == 11)
        {
            if (!depth || !g.size || !g.count || g.size > 32768 || g.count > 32768 ||
                uint64_t(g.size) * g.count > 32768) return result;
            Report report = tag == 8 ? Report::Input : tag == 9 ? Report::Output : Report::Feature;
            auto& count = bits[{report, g.id}];
            count += g.size * g.count;
            if (count > 32768) return result;
        }
    }
    if (depth || !stack.empty() || bits.empty()) return result;
    for (const auto& item : bits)
        result.bytes[item.first] = (item.second + 7) / 8 + (item.first.second != 0 ? 1 : 0);
    result.valid = true;
    return result;
}

struct Profile
{
    unsigned vid, pid;
    Protocol protocol;
    int mi;
    unsigned page, usage, transaction;
    bool wired;
    const char* name;
};
// Finite identities extracted from DB179. This is battery permission, never a brand grant.
static const Profile profiles[] = {
#include "Catalog.inc"
};
bool KnownIdentity(unsigned vid, unsigned pid)
{
    if (extra_keyboard::KnownIdentity(vid, pid)) return true;
    if (keyboard_advanced::KnownIdentity(vid, pid) || headset_controller::KnownIdentity(vid, pid) || extra_mouse::KnownIdentity(vid, pid)) return true;
    if ((vid == 0x373b && (pid == 0x11cb || pid == 0x1204)) || (vid == 0x054c && pid == 0x0e53)) return true;
    for (const auto& profile : profiles) if (profile.vid == vid && profile.pid == pid) return true;
    static const std::pair<unsigned, unsigned> metadataOnly[] = {
#include "IdentityOnly.inc"
    };
    for (const auto& identity : metadataOnly) if (identity.first == vid && identity.second == pid) return true;
    return false;
}
bool NeedsDescriptor(unsigned vid, unsigned pid)
{
    if (extra_keyboard::KnownIdentity(vid, pid)) return true;
    if (keyboard_advanced::KnownIdentity(vid, pid) || headset_controller::KnownIdentity(vid, pid) || extra_mouse::KnownIdentity(vid, pid)) return true;
    for (const auto& profile : profiles) if (profile.vid == vid && profile.pid == pid) return true;
    return false;
}

static bool Io(const Candidate& c, unsigned id, unsigned length)
{
    return c.input == length && c.output == length &&
        c.layout.Has(Report::Input, id, length) && c.layout.Has(Report::Output, id, length);
}
static bool Feature(const Candidate& c, unsigned id, unsigned descriptorLength, unsigned nativeLength)
{
    return c.feature == nativeLength && c.layout.Has(Report::Feature, id, descriptorLength);
}
Protocol Classify(Candidate& c)
{
    c.protocol = Protocol::None;
    c.adapter.clear();
    if (c.physical.empty() || c.path.empty()) return c.protocol;
    Candidate privateChannel = c;
    privateChannel.pointer = c.page == 1 && c.usage == 2;
    privateChannel.keyboard = c.page == 1 && c.usage == 6;
    if (extra_keyboard::Admit(privateChannel) || keyboard_advanced::Admit(privateChannel) ||
        headset_controller::Admit(privateChannel) || extra_mouse::Admit(privateChannel))
    {
        c = std::move(privateChannel);
        if (c.adapter == "rog_omni_keyboard" && c.transportPath.empty()) c.presentUnreadable = true;
        return c.protocol;
    }
    if (c.mi == 4 && c.vid == 0x373b && (c.pid == 0x11cb || c.pid == 0x1204) && c.page == 0x0c && c.usage == 1 && !c.transportPath.empty())
    { c.name = "ATK N9 Pro / original Ultra"; c.kind = "headset"; return c.protocol = Protocol::N9Serial; }
    if (c.mi == 3 && c.vid == 0x054c && c.pid == 0x0e53 && c.page == 0x0c && c.usage == 1 && !c.transportPath.empty())
    { c.name = "Sony INZONE H9"; c.kind = "headset"; return c.protocol = Protocol::SonyH9; }
    for (const auto& p : profiles)
    {
        if (c.mi < 0 || c.vid != p.vid || c.pid != p.pid || (p.mi >= 0 && c.mi != p.mi) ||
            c.page != p.page || c.usage != p.usage) continue;
        bool accept = false;
        switch (p.protocol)
        {
        case Protocol::Compx: accept = c.pointer && Io(c, 8, 17); break;
        case Protocol::Nordic: accept = c.pointer && Io(c, 8, 64); break;
        case Protocol::Hidpp:
            accept = ((c.layout.Has(Report::Input, 0x10, 7) && c.layout.Has(Report::Output, 0x10, 7)) ||
                (c.vid == 0x046d && c.mi == 2 &&
                 ((c.pid == 0xc54f && c.page == 0xff43 && c.usage == 0x0302) ||
                  (c.pid == 0xc547 && c.page == 0xff00 && c.usage == 2)) &&
                 c.layout.Has(Report::Output, 0x11, 20) && c.input == 20 && c.output == 20)) &&
                c.layout.Has(Report::Input, 0x11, 20) && c.input >= 20 && c.input <= 64 && c.output >= 7 && c.output <= 64; break;
        case Protocol::Razer: accept = Feature(c, 0, 90, 91); break;
        case Protocol::Rog: accept = Io(c, 3, 64) && (c.pid != 0x1ace || c.path.find("mi_02&col03") != std::string::npos); break;
        case Protocol::RogStandard:
        case Protocol::Steelseries:
            accept = c.input == 65 && c.output == 65 && c.layout.Has(Report::Input, 0, 64) &&
                c.layout.Has(Report::Output, 0, 64); break;
        case Protocol::Mhub:
            accept = (Feature(c, 0x11, 21, 21) || Feature(c, 0x11, 65, 65)) && c.pointer; break;
        case Protocol::MhubV2: accept = Io(c, 0x4d, 64); break;
        case Protocol::Sonix: accept = Feature(c, 0, 64, 65); break;
        case Protocol::Incott: accept = c.input == 8 && c.layout.Has(Report::Input, 9, 8); break;
        case Protocol::Rapoo:
            accept = (c.pointer || c.pid == 0x14a2) &&
                ((c.input == 7 && c.layout.Has(Report::Input, 0xbb, 7)) ||
                 ((c.input == 19 || c.input == 20) && c.layout.Has(Report::Input, 7, c.input))); break;
        case Protocol::X23:
            accept = c.input == 33 && c.output == 33 && c.layout.Has(Report::Input, 0, 32) &&
                c.layout.Has(Report::Output, 0, 32); break;
        case Protocol::X23Passive: accept = c.input == 32 && c.layout.Has(Report::Input, 4, 32); break;
        case Protocol::Blackshark: accept = Io(c, 2, 64); break;
        case Protocol::Xiberia: accept = Io(c, 0x55, 64); break;
        case Protocol::F87Passive: accept = c.input == 20 && c.layout.Has(Report::Input, 0x13, 20); break;
        case Protocol::DeluxPassive: accept = c.input >= 5 && c.input <= 64 && c.layout.Has(Report::Input, 3, c.input); break;
        case Protocol::Rainy75:
            accept = Io(c, 4, 64) && ((c.pid == 0x5055 && ((c.mi == 3 && c.revision == 0x114) ||
                (c.mi == 1 && c.revision == 0x111))) || (c.pid == 0x5088 && c.revision == 0x101)); break;
        case Protocol::Irok: accept = Feature(c, 0, 64, 65); break;
        case Protocol::Langtu: accept = c.input == 65 && c.output == 65 && c.layout.Has(Report::Input, 0, 64) && c.layout.Has(Report::Output, 0, 64); break;
        case Protocol::Pelta: accept = Io(c, 0xcc, 64); break;
        default: break;
        }
        if (!accept) continue;
        c.protocol = p.protocol; c.transaction = p.transaction; c.wired = p.wired;
        c.name = p.name;
        c.kind = p.protocol == Protocol::Blackshark || p.protocol == Protocol::Xiberia || p.protocol == Protocol::Pelta ? "headset" :
            p.protocol == Protocol::F87Passive || p.protocol == Protocol::Rainy75 || p.protocol == Protocol::Irok || p.protocol == Protocol::Langtu ? "keyboard" : "mouse";
        return c.protocol;
    }
    if (c.standard)
    {
        c.protocol = Protocol::Standard;
        if (c.pointer) c.kind = "mouse";
        else if (c.keyboard) c.kind = "keyboard";
    }
    return c.protocol;
}

static bool AzothSerialIdentity(const Candidate& c)
{
    return c.vid == 0x0b05 && (c.pid == 0x1a83 || c.pid == 0x1a85) &&
        c.protocol == Protocol::ExtraKeyboard && c.adapter == "rog_azoth_x" && !c.serial.empty();
}
void SelectUnique(std::vector<Candidate>& candidates)
{
    // Only the same bus-validated unique serial proves a wired/wireless pair.
    // Model names, percentage and receiver presence never prove device identity.
    for (auto& c : candidates) if (AzothSerialIdentity(c) && c.pid == 0x1a85)
    {
        unsigned wired = 0, wireless = 0;
        Candidate* wiredPeer = nullptr;
        for (auto& peer : candidates) if (AzothSerialIdentity(peer) && peer.serial == c.serial)
        { if (peer.pid == 0x1a83) { ++wired; wiredPeer = &peer; } else ++wireless; }
        if (wired == 1 && wireless == 1)
        {
            wiredPeer->confirmedLegacyIdentities = LegacyIdentities(c);
            c.protocol = Protocol::None; c.adapter.clear();
        }
    }
    std::map<std::pair<std::string, std::string>, unsigned> count;
    for (const auto& c : candidates) if (c.protocol != Protocol::None) ++count[{c.physical, c.adapter.empty() ? ProtocolName(c.protocol) : c.adapter}];
    for (auto& c : candidates)
    {
        if (c.protocol != Protocol::None && count[{c.physical, c.adapter.empty() ? ProtocolName(c.protocol) : c.adapter}] != 1) { c.protocol = Protocol::None; c.adapter.clear(); }
        c.identity = CanonicalIdentity(c);
    }
    std::map<std::string, unsigned> identities;
    for (const auto& c : candidates) ++identities[c.identity];
    for (auto& c : candidates) if (identities[c.identity] > 1)
    { c.protocol = Protocol::None; c.adapter.clear(); c.serial.clear(); c.identity = CanonicalIdentity(c); }
}
std::string CanonicalIdentity(const Candidate& c)
{
    char fields[160]{};
    std::snprintf(fields, sizeof(fields), "|vid=%04x|pid=%04x|mi=%d|usage=%04x:%04x|channel=",
        c.vid, AzothSerialIdentity(c) ? 0x1a85 : c.pid, c.mi, c.page, c.usage);
    const char* prefix = c.protocol == Protocol::Gaia ? "bt:gaia:v1|address=" : c.protocol == Protocol::OHub ? "driver:ohub:v1|container=" : "hid:v1|container=";
    if (!c.serial.empty())
    {
        std::string serial;
        constexpr char digits[] = "0123456789abcdef";
        for (unsigned char byte : c.serial) { serial += digits[byte >> 4]; serial += digits[byte & 15]; }
        return "hid:v1|serial=" + serial + fields + (c.adapter.empty() ? ProtocolName(c.protocol) : c.adapter) + (c.unit.empty() ? "" : "|unit=" + c.unit);
    }
    // A unique admitted channel needs no ephemeral collection path. Unsupported
    // collections retain the path because their MI/usage alone may be ambiguous.
    return std::string(prefix) + c.physical + fields + (c.protocol == Protocol::None ? c.path : c.adapter.empty() ? ProtocolName(c.protocol) : c.adapter) +
        (c.unit.empty() ? "" : "|unit=" + c.unit);
}
std::vector<std::string> LegacyIdentities(const Candidate& c)
{
    // Only an exact raw serial observed on this validated physical channel proves
    // its legacy key. Never infer older corrupt suffixes from a shared prefix.
    if (!AzothSerialIdentity(c)) return {};
    if (c.pid != 0x1a85 || c.hidSerial.empty() || c.hidSerial == c.serial) return c.confirmedLegacyIdentities;
    Candidate legacy = c; legacy.serial = c.hidSerial;
    return {CanonicalIdentity(legacy)};
}
const char* ProtocolName(Protocol p)
{
    switch (p)
    {
    case Protocol::Standard: return "standard-hid";
    case Protocol::Compx: return "compx-x23";
    case Protocol::Nordic: return "nordic54";
    case Protocol::Hidpp: return "hidpp20";
    case Protocol::Razer: return "razer-feature";
    case Protocol::Rog: return "rog-report3";
    case Protocol::RogStandard: return "rog-standard";
    case Protocol::Mhub: return "mhub-cache";
    case Protocol::MhubV2: return "mhub-v2";
    case Protocol::Sonix: return "sonix-fusion2";
    case Protocol::Incott: return "incott-passive";
    case Protocol::Rapoo: return "rapoo-passive";
    case Protocol::X23: return "x23-protocol3";
    case Protocol::X23Passive: return "x23-passive";
    case Protocol::Steelseries: return "aerox-d2";
    case Protocol::Blackshark: return "blackshark-v3";
    case Protocol::Xiberia: return "xiberia-t10";
    case Protocol::OHub: return "ohub-installed-driver";
    case Protocol::N9Serial: return "atk-n9-serial";
    case Protocol::SonyH9: return "sony-h9-serial";
    case Protocol::Gaia: return "gaia-rfcomm";
    case Protocol::F87Passive: return "fa09-passive";
    case Protocol::DeluxPassive: return "delux-passive";
    case Protocol::Rainy75: return "rainy75-get1a";
    case Protocol::Irok: return "irok-royuan";
    case Protocol::Langtu: return "t75-h87";
    case Protocol::Pelta: return "rog-headset-cc";
    case Protocol::ExtraKeyboard: return "keyboard-basic";
    case Protocol::KeyboardAdvanced: return "keyboard-advanced";
    case Protocol::HeadsetController: return "headset-controller";
    case Protocol::ExtraMouse: return "mouse-private";
    default: return "hid-metadata";
    }
}
bool OnceOnly(Protocol p) { return p == Protocol::Blackshark; }
bool BeginInitialization(std::set<std::string>& initialized, const std::string& identity, const std::function<void()>& notify)
{
    if (initialized.count(identity) || initialized.size() >= 4096) return false;
    initialized.insert(identity);
    notify();
    return true;
}
Query BatteryQuery(const Candidate& c, unsigned field)
{
    Query q;
    auto packet = [&](unsigned length, std::initializer_list<unsigned char> prefix) {
        q.packet.assign(length, 0); std::copy(prefix.begin(), prefix.end(), q.packet.begin()); q.replyLength = length;
    };
    q.transport = Transport::Output;
    switch (c.protocol)
    {
    case Protocol::Compx: packet(17, {8, 4}); q.packet[16] = 0x49; break;
    case Protocol::Nordic: packet(64, {8, 0x7d, 0x72, 2, 0, static_cast<unsigned char>(c.wired ? 0 : 1), 7, 1}); break;
    case Protocol::Razer:
        q.transport = Transport::Feature; packet(91, {});
        q.packet[2] = static_cast<unsigned char>(c.transaction); q.packet[6] = 2; q.packet[7] = 7; q.packet[8] = 0x80;
        for (unsigned i = 3; i < 89; ++i) q.packet[89] ^= q.packet[i]; break;
    case Protocol::Rog: packet(64, {3, 0x12, 7}); break;
    case Protocol::RogStandard: packet(65, {0, 0x12, 7}); break;
    case Protocol::Mhub:
        q.transport = Transport::Feature; q.packet.assign(21, 0xff); q.packet[0] = 0x11; q.packet[1] = 0xf9;
        q.replyLength = c.feature; break;
    case Protocol::MhubV2: packet(64, {0x4d, 1, 1, 0, 0, 9, 0, 0, 8}); break;
    case Protocol::Sonix:
    {
        q.transport = Transport::Feature; packet(65, {0, 0, 8, static_cast<unsigned char>(field ? 0x83 : 0x81), 1});
        unsigned sum = std::accumulate(q.packet.begin() + 1, q.packet.begin() + 63, 0u);
        q.packet[63] = static_cast<unsigned char>(sum); q.packet[64] = static_cast<unsigned char>(sum >> 8); break;
    }
    case Protocol::Incott: q.transport = Transport::Passive; q.replyLength = 8; q.waitMs = 1500; break;
    case Protocol::Rapoo: q.transport = Transport::Passive; q.replyLength = c.input; q.waitMs = 1500; break;
    case Protocol::X23: packet(33, {0, 0x30, 0, 1, 0}); break;
    case Protocol::X23Passive: q.transport = Transport::Passive; q.replyLength = 32; q.waitMs = 1500; break;
    case Protocol::Steelseries: packet(65, {0, 0xd2}); break;
    case Protocol::Blackshark:
        q.transport = Transport::ControlOutput; packet(64, {2, 0, 0x60});
        q.packet[6] = 4; q.packet[9] = 0x80; q.packet[10] = 0x21;
        for (unsigned i = 0; i < 62; ++i) q.packet[62] ^= q.packet[i]; q.waitMs = 3000; break;
    case Protocol::Xiberia: packet(64, {0x55, 0x10, 1}); break;
    case Protocol::N9Serial: packet(64, {2, 0, static_cast<unsigned char>(field ? 4 : 3)}); break;
    case Protocol::OHub:
        packet(128, {23,0,0,0,67,0,0,0,23,0,0,0,135,0,0,0,0,0,0,0,0,0,0,0,1}); break;
    case Protocol::Gaia: packet(8, {0xff,1,0,0,0x2e,0x50,0x22,6}); break;
    case Protocol::F87Passive: q.transport = Transport::Passive; q.replyLength = 20; q.waitMs = 1500; break;
    case Protocol::DeluxPassive: q.transport = Transport::Passive; q.replyLength = c.input; q.waitMs = 1500; break;
    case Protocol::Rainy75: packet(64,{4,0x20,0,0x1a,6,0,0,0}); break;
    case Protocol::Irok: q.transport = Transport::Feature; packet(65,{0,0xf7}); q.packet[8] = 8; break;
    case Protocol::Langtu: packet(65,{0,0xaa,0x55,0xcc,0x33,0x0e}); break;
    case Protocol::Pelta: packet(64,{0xcc,0x12,static_cast<unsigned char>(field?8:7)}); break;
    case Protocol::Hidpp: q.transport = Transport::Hidpp; break;
    case Protocol::Standard: q.transport = Transport::Standard; break;
    default: q.transport = Transport::Passive; break;
    }
    return q;
}
static bool Prefix(const Bytes& b, std::initializer_list<unsigned char> prefix)
{
    return b.size() >= prefix.size() && std::equal(prefix.begin(), prefix.end(), b.begin());
}
static unsigned U16(const Bytes& b, unsigned offset) { return b[offset] | (unsigned(b[offset + 1]) << 8); }
static Reading Sleep() { Reading r; r.state = "sleeping"; return r; }
std::optional<Reading> Decode(const Candidate& c, const Bytes& raw, unsigned field)
{
    Bytes b = raw;
    Reading r;
    auto percentage = [&](unsigned value) -> std::optional<Reading> {
        if (value > 100) return std::nullopt; r.percentage = int(value); return r;
    };
    switch (c.protocol)
    {
    case Protocol::Compx:
        if (b.size() != 17 || !Prefix(b, {8, 4}) || b[2] == 255 || b == BatteryQuery(c).packet ||
            (std::accumulate(b.begin(), b.end(), 0u) & 255) != 0x55) return {};
        r.charging = (b[7] & 1) != 0; return percentage(b[6]);
    case Protocol::Nordic:
        if (b.size() != 64 || !Prefix(b, {8, 0x72}) || b[5] != 7 || b[6] != 1) return {};
        if (b[2] != 0) return Sleep(); return percentage(b[7]);
    case Protocol::Razer:
    {
        if (b.size() != 91 || !Prefix(b, {0, 2, static_cast<unsigned char>(c.transaction), 0, 0, 0, 2, 7, 0x80})) return {};
        unsigned checksum = 0; for (unsigned i = 3; i < 89; ++i) checksum ^= b[i];
        if (checksum != b[89]) return {};
        r.percentage = int(std::lround(b[10] * 100.0 / 255)); return r; // Charging is not inferred.
    }
    case Protocol::Rog:
        if (b.size() != 64 || !Prefix(b, {3, 0x12, 7, 0, 0}) || b == BatteryQuery(c).packet || b[10] > 1 || b[5] > 100) return {};
        if (!b[5]) return Sleep(); r.charging = b[10] != 0; return percentage(b[5]);
    case Protocol::RogStandard:
        if (b.size() == 65 && b[0] == 0) b.erase(b.begin());
        if (b.size() != 64 || !Prefix(b, {0x12, 7}) || std::all_of(b.begin() + 2, b.end(), [](auto x) { return x == 0; })) return {};
        if (!b[4] && !b[9]) return Sleep(); r.charging = b[9] > 0; return percentage(b[4]);
    case Protocol::Mhub:
    {
        if (b.size() == 65 && std::all_of(b.begin() + 21, b.end(), [](auto x) { return x == 0; })) b.resize(21);
        if (b.size() != 21 || !Prefix(b, {0x11, 0xf9}) || b == BatteryQuery(c).packet) return {};
        for (size_t i = 2; i < b.size(); ++i) b[i] ^= 0xff;
        if ((U16(b, 2) != 0x3837 && U16(b, 2) != 0x5253) || b[12] > 1 || (b[10] & 7) > 2) return {};
        if ((b[10] & 7) == 1 && !(b[10] & 8)) return Sleep();
        r.charging = b[12] != 0; return percentage(b[11]);
    }
    case Protocol::MhubV2:
        if (b.size() != 64 || !Prefix(b, {0x4d}) || U16(b, 4) != 0x900 || b == BatteryQuery(c).packet || U16(b, 8) != 0x3837 ||
            !std::set<unsigned>{0x4026, 0x4028, 0x4033, 0x4034, 0x4037, 0x4038, 0x4040}.count(U16(b, 10)) || b[19] > 1 || b[20] > 100) return {};
        if (!b[20]) return Sleep(); r.peer = std::to_string(U16(b, 10)); r.charging = b[19] != 0; return percentage(b[20]);
    case Protocol::Sonix:
        if (b.size() != 65 || !Prefix(b, {0, 1, 8, static_cast<unsigned char>(field ? 0x83 : 0x81), 1}) || b[7] == 255) return {};
        if (field) { if (b[7] > 1) return {}; r.charging = b[7] != 0; return r; }
        return percentage(b[7]);
    case Protocol::Incott:
        if (b.size() != 8 || b[0] != 9) return {};
        r.charging = (b[1] & 128) != 0; return percentage(b[1] & 127);
    case Protocol::Rapoo:
        if ((b.size() == 19 || b.size() == 20) && b[0] == 7 && (b[1] & 15) <= 2 && b[7] <= 2)
        { if (!b[7]) return Sleep(); r.charging = b[7] == 2; return percentage(b[8]); }
        if (b.size() == 7 && Prefix(b, {0xbb, 0xb0}) && (b[5] == 1 || b[5] == 2))
        { r.charging = b[5] == 2; return percentage(b[6]); } return {};
    case Protocol::X23:
        if (b.size() != 32 || !Prefix(b, {0x30, 0}) ||
            !(std::equal(b.begin() + 4, b.begin() + 8, "M230") || std::equal(b.begin() + 4, b.begin() + 8, "M137")) ||
            b[12] > 1 || b[14] > 1) return {};
        if (!b[14]) return Sleep(); r.peer.assign(b.begin() + 4, b.begin() + 8); r.charging = b[12] != 0; return percentage(b[13]);
    case Protocol::X23Passive:
        if (b.size() != 32 || b[0] != 4 || b[1] > 1 || b[5] > 1) return {};
        if (!b[1]) return Sleep(); r.charging = b[5] != 0; return percentage(b[4]);
    case Protocol::Steelseries:
        if (b.size() == 65 && b[0] == 0) b.erase(b.begin());
        if (b.size() < 2 || b.size() > 64 || b[0] != 0xd2 || (b[1] & 127) > 21) return {};
        if (!(b[1] & 127)) return Sleep(); r.estimated = true; r.charging = (b[1] & 128) != 0;
        return percentage(((b[1] & 127) - 1) * 5);
    case Protocol::Blackshark:
        if (b.size() != 64 || b[0] != 2 || b[10] != 0x21 || (b[11] != 1 && b[11] != 2) || b[12] != 1) return {};
        return percentage(b[13]);
    case Protocol::Xiberia:
        if (b.size() != 64 || !Prefix(b, {0x55, 0x10, 1}) || b == BatteryQuery(c).packet || b[4] > 2) return {};
        if (!b[4]) return Sleep(); return percentage(b[7] & 127);
    case Protocol::OHub:
        if (b.size() < 2 || b.size() > 128 || b[0] != 0x87 || b[1] == 0) return {};
        return percentage(b[1]);
    case Protocol::N9Serial:
        if (b.size() != 64 || b[0] < 4 || b[0] > 63 || b[1] != 0 || b[2] != (field ? 4 : 3) || b == BatteryQuery(c,field).packet) return {};
        if (field) { if (b[4] > 1) return {}; if (!b[4]) return Sleep(); return r; }
        r.charging = (b[4] & 128) != 0; return percentage(b[4] & 127);
    case Protocol::Gaia:
        if (b.size() != 9 || !Prefix(b,{0xff,1,0,1,0x2e,0x50,0x23,6})) return {};
        return percentage(b[8]);
    case Protocol::F87Passive:
        if (b.size()!=20 || !Prefix(b,{0x13,0x0a,1,0,4,5}) || (std::accumulate(b.begin(),b.begin()+19,0u)&255)!=b[19]) return {};
        return percentage(b[6]);
    case Protocol::DeluxPassive:
    {
        const std::set<unsigned> models = c.pid==0xfa60 ? std::set<unsigned>{0x10,0x91,0x29,0x2a,0x2b} : std::set<unsigned>{0x21,0x30,0x32,0x34,0x37,0x38,0x39,0x3a};
        if(b.size()<5||b.size()>64||b[0]!=3||!models.count(b[1])||b[2]!=0x40||b[3]>1||b[4]==0||
            std::any_of(b.begin()+5,b.end(),[](auto value){return value!=0;}))return {};
        return percentage(b[4]); // Status is freshness, not a documented charging bit.
    }
    case Protocol::Rainy75:
        if(b.size()!=64||b[0]!=4||b[3]!=0x1a||b[4]!=6||b[5]!=0||b[6]!=0||b[7]>=0xfe||b[9]>2||b==BatteryQuery(c).packet)return {};
        r.charging=b[9]==1;return percentage(b[8]);
    case Protocol::Irok:
    {
        if(b.size()==65&&b[0]==0)b.erase(b.begin());
        if(b.size()!=64||std::all_of(b.begin(),b.end(),[](auto value){return value==0;}))return {};
        unsigned state=b[3],value=b[1];if(state>1){state=b[4];value=b[2];}
        if(state>1||b[0]>1||value>100)return {};if(state==1)return Sleep();return percentage(value);
    }
    case Protocol::Langtu:
        if(b.size()!=64||!Prefix(b,{0xaa,0x55,0xcc,0x33,0x0e})||b[6]==0)return {};
        if(b[6]!=1&&b[6]!=3)return {};return percentage(b[5]);
    case Protocol::Pelta:
        if(b.size()!=64||!Prefix(b,{0xcc,0x12,static_cast<unsigned char>(field?8:7)})||(b[5]==0xff&&b[6]==0xaa)||b==BatteryQuery(c,field).packet)return {};
        if(field){if(b[5]>1)return {};r.charging=b[5]!=0;return r;}return percentage(b[6]);
    default: return {};
    }
}
bool HidppReplyMatches(const Bytes& b, unsigned target, unsigned feature, unsigned address)
{
    return ((b.size() == 7 && b[0] == 0x10) || (b.size() == 20 && b[0] == 0x11)) &&
        b[1] == target && b[2] == feature && b[3] == address;
}
std::optional<Reading> DecodeHidppBattery(unsigned feature, const Bytes& b, bool precise)
{
    Reading r;
    if (feature == 0x1004 || feature == 0x1000)
    {
        if (b.size() < (feature == 0x1004 ? 4u : 3u) || b[0] > 100 || b[2] > (feature == 0x1004 ? 4 : 7)) return {};
        if (feature == 0x1000)
        { if (b[0]) r.percentage = b[0]; } // 0x1000 explicitly reserves zero for unknown.
        else if (precise) r.percentage = b[0]; // Capability bit1 makes a true 0% meaningful.
        else
        {
            switch (b[1]) { case 0:r.levelText="empty";break;case 1:r.levelText="critical";break;
                case 2:r.levelText="low";break;case 4:r.levelText="good";break;case 8:r.levelText="full";break;default:return {}; }
        }
        if (b[2] <= 3) r.charging = b[2] == 1 || b[2] == 2;
        else if (feature == 0x1000 && b[2] == 5) r.charging = true;
        return r;
    }
    if (feature != 0x1001 || b.size() < 3) return {};
    unsigned mv = (unsigned(b[0]) << 8) | b[1], flags = b[2];
    if (mv < 3000 || mv > 4500) return {};
    const std::pair<int, int> knots[] = {{3500,0},{3579,2},{3646,5},{3671,10},{3717,20},{3751,30},
        {3778,40},{3811,50},{3859,60},{3922,70},{3989,80},{4067,90},{4186,100}};
    r.percentage = mv <= 3500 ? 0 : 100;
    for (size_t i = 1; i < std::size(knots); ++i)
        if (mv > unsigned(knots[i - 1].first) && mv <= unsigned(knots[i].first))
            r.percentage = int(std::lround(knots[i - 1].second + (knots[i].second - knots[i - 1].second) *
                double(mv - knots[i - 1].first) / (knots[i].first - knots[i - 1].first)));
    r.estimated = true;
    if (!(flags & 0x44) && !((flags & 128) && (flags & 3) == 3)) r.charging = (flags & 128) && (flags & 3) == 0;
    return r;
}
bool ValidHidppUnitIdentity(const std::string& unit)
{
    if (unit.size()!=27 || unit.substr(8,7)!="|model=") return false;
    for(size_t i=0;i<unit.size();++i)if((i<8||i>=15)&&!((unit[i]>='0'&&unit[i]<='9')||(unit[i]>='a'&&unit[i]<='f')))return false;
    return unit.substr(0,8)!="00000000"&&unit.substr(0,8)!="ffffffff"&&unit.substr(15)!="000000000000";
}
std::string HidppUnitIdentity(const Bytes& b)
{
    if(b.size()<13||b[0]==0||b[0]>16)return {};
    constexpr char digits[]="0123456789abcdef";std::string unit,model;
    for(unsigned i=1;i<5;++i){unit+=digits[b[i]>>4];unit+=digits[b[i]&15];}
    for(unsigned i=7;i<13;++i){model+=digits[b[i]>>4];model+=digits[b[i]&15];}
    unit+="|model="+model;return ValidHidppUnitIdentity(unit)?unit:"";
}
std::vector<std::pair<Candidate, std::optional<Reading>>> ReadHidppUnits(const Candidate& receiver, const HidppCall& call, bool readBattery,
    std::map<std::string, std::string>* names)
{
    std::random_device entropy;
    auto request = [&](unsigned target, unsigned feature, unsigned function, Bytes params = {}) { return call(target, feature, function, std::move(params)); };
    auto featureIndex = [&](unsigned target, unsigned feature) {
        Bytes reply = request(target, 0, 0, {static_cast<unsigned char>(feature >> 8), static_cast<unsigned char>(feature)});
        return reply.empty() ? 0u : unsigned(reply[0]);
    };

        struct Target { unsigned slot, infoFeature, nameFeature; Candidate device; };
        std::vector<Target> targets;
        // A slot routes a query; only 0x0003 unit/model data grants a public identity.
        for (unsigned target : {1u, 2u, 3u, 4u, 5u, 6u, 0xffu})
        {
            unsigned char marker = 0;
            marker = static_cast<unsigned char>(entropy());
            if (!marker) marker = 1;
            Bytes ping = request(target, 0, 1, {0, 0, marker});
            if (ping.size() < 3 || ping[0] < 2 || ping[2] != marker) continue;
            unsigned index = featureIndex(target, 5); if (!index) continue;
            Bytes type = request(target, index, 2);
            if (type.empty() || (type[0] != 0 && type[0] != 3)) continue;
            unsigned infoFeature = featureIndex(target, 3); if (!infoFeature) continue;
            std::string unit = HidppUnitIdentity(request(target, infoFeature, 0));
            if (unit.empty()) continue;
            Candidate device = receiver; device.unit = unit; device.kind = type[0] == 3 ? "mouse" : "keyboard";
            device.name = type[0] == 3 ? "Logitech HID++ mouse" : "Logitech HID++ keyboard";
            device.identity = CanonicalIdentity(device);
            targets.push_back({target, infoFeature, index, std::move(device)});
        }
        std::map<std::string, unsigned> identities;
        for (const auto& target : targets) ++identities[target.device.unit];
        std::vector<std::pair<Candidate, std::optional<Reading>>> results;
        for (const auto& target : targets)
        {
            if (identities[target.device.unit] != 1 || (!receiver.unit.empty() && receiver.unit != target.device.unit)) continue;
            // Reconfirm before and after battery reads to reject pairing changes mid-request.
            if (HidppUnitIdentity(request(target.slot, target.infoFeature, 0)) != target.device.unit) continue;
            std::optional<Reading> reading;
            if (readBattery) for (unsigned feature : {0x1004u, 0x1000u, 0x1001u})
            {
                unsigned index = featureIndex(target.slot, feature); if (!index) continue;
                bool precise = true;
                if (feature == 0x1004)
                {
                    Bytes capabilities = request(target.slot, index, 0);
                    if (capabilities.size() < 2) continue;
                    precise = (capabilities[1] & 2) != 0;
                }
                auto a = DecodeHidppBattery(feature, request(target.slot, index, feature == 0x1004 ? 1 : 0), precise);
                auto b = DecodeHidppBattery(feature, request(target.slot, index, feature == 0x1004 ? 1 : 0), precise);
                if (a && b)
                {
                    reading = b;
                    if (b->percentage || !b->levelText.empty()) break;
                }
            }
            if (HidppUnitIdentity(request(target.slot, target.infoFeature, 0)) != target.device.unit) reading.reset();
            results.push_back({target.device, reading});
        }
        // Names are optional: finish every battery/identity check first. The
        // transport's existing total deadline also bounds these metadata reads.
        if (names) for (auto& result : results)
        {
            auto& device = result.first;
            auto cached = names->find(device.unit);
            if (cached != names->end()) { device.name = cached->second; continue; }
            if (readBattery && !result.second) continue;
            auto target = std::find_if(targets.begin(), targets.end(), [&](const auto& t) { return t.device.unit == device.unit; });
            auto count = request(target->slot, target->nameFeature, 0);
            if (count.empty() || !count[0] || count[0] > 64) continue;
            std::string name;
            for (unsigned chunk = 0; chunk < 4 && name.size() < count[0]; ++chunk)
            {
                auto part = request(target->slot, target->nameFeature, 1, {static_cast<unsigned char>(name.size())});
                if (part.empty()) break;
                size_t length = std::min<size_t>(part.size(), count[0] - name.size());
                name.append(part.begin(), part.begin() + length);
            }
            if (name.size() != count[0] || !std::all_of(name.begin(), name.end(), [](unsigned char c) { return c >= 0x20 && c < 0x7f; })) continue;
            // Pairing changes or a failed optional recheck discard only the name,
            // never a battery sample already confirmed above.
            if (HidppUnitIdentity(request(target->slot, target->infoFeature, 0)) != device.unit) continue;
            if (names->size() >= 256) names->erase(names->begin());
            names->emplace(device.unit, name); device.name = std::move(name);
        }
        return results;
}
Bytes SonyRequest(unsigned command, unsigned transaction)
{
    if ((command != 1 && command != 2 && command != 4) || transaction < 2 || transaction > 65535) return {};
    Bytes b{1,0,0xfc,8,0x96,0xc3,static_cast<unsigned char>(command == 1 ? 0x21 : 0x41),static_cast<unsigned char>(command),1,
        static_cast<unsigned char>(transaction),static_cast<unsigned char>(transaction >> 8)};
    b.push_back(static_cast<unsigned char>(std::accumulate(b.begin()+4,b.end(),0u))); return b;
}
std::optional<Bytes> SonyPayload(const Bytes& b, unsigned command, unsigned transaction)
{
    if (SonyRequest(command,transaction).empty() || b.size()<12 || b.size()>62 || !Prefix(b,{4,0xff}) || b[2]!=b.size()-3 ||
        b[3]!=0 || b[4]!=0x96 || b[5]!=0xc3 || b[6]!=(command==1?0x12:0x14) || b[7]!=command || b[8]!=0x10 ||
        U16(b,9)!=transaction || (std::accumulate(b.begin()+3,b.end()-1,0u)&255)!=b.back()) return {};
    return Bytes(b.begin()+11,b.end()-1);
}
}
