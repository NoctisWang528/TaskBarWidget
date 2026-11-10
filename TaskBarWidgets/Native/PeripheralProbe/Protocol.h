#pragma once
#include <cstdint>
#include <functional>
#include <map>
#include <optional>
#include <set>
#include <string>
#include <vector>

namespace peripheral
{
using Bytes = std::vector<unsigned char>;
enum class Report { Input, Output, Feature };
enum class Protocol { None, Standard, Compx, Nordic, Hidpp, Razer, Rog, RogStandard,
    Mhub, MhubV2, Sonix, Incott, Rapoo, X23, X23Passive, Steelseries, Blackshark, Xiberia,
    OHub, N9Serial, SonyH9, Gaia, F87Passive, DeluxPassive, Rainy75, Irok, Langtu, Pelta, ExtraKeyboard,
    KeyboardAdvanced, HeadsetController, ExtraMouse };
struct Layout
{
    bool valid = false;
    std::map<std::pair<Report, unsigned>, unsigned> bytes;
    bool Has(Report type, unsigned id, unsigned length) const;
};
Layout ParseDescriptor(const Bytes& descriptor);
struct Candidate
{
    unsigned vid = 0, pid = 0, revision = 0, page = 0, usage = 0;
    int mi = -1;
    unsigned input = 0, output = 0, feature = 0;
    std::string path, physical, serial, hidSerial, usbProduct, identity, name, transportPath, unit, adapter, kind = "unknown";
    std::vector<std::string> confirmedLegacyIdentities;
    bool pointer = false, keyboard = false, standard = false, presentUnreadable = false, identityOnly = false;
    Layout layout;
    Protocol protocol = Protocol::None;
    unsigned transaction = 0;
    bool wired = false;
};
struct Reading
{
    std::optional<int> percentage;
    std::optional<bool> charging;
    bool estimated = false;
    std::string state = "online";
    std::string peer;
    std::string levelText;
    std::string role = "main";
    std::vector<Reading> components;
};
enum class Transport { Output, Feature, Passive, ControlOutput, Standard, Hidpp };
struct Query
{
    Transport transport = Transport::Passive;
    Bytes packet;
    unsigned replyLength = 0;
    unsigned waitMs = 1200;
};
bool KnownIdentity(unsigned vid, unsigned pid);
bool NeedsDescriptor(unsigned vid, unsigned pid);
Protocol Classify(Candidate& candidate);
void SelectUnique(std::vector<Candidate>& candidates);
std::string CanonicalIdentity(const Candidate& candidate);
std::vector<std::string> LegacyIdentities(const Candidate& candidate);
const char* ProtocolName(Protocol protocol);
bool OnceOnly(Protocol protocol);
bool BeginInitialization(std::set<std::string>& initialized, const std::string& identity, const std::function<void()>& notify);
Query BatteryQuery(const Candidate& candidate, unsigned field = 0);
std::optional<Reading> Decode(const Candidate& candidate, const Bytes& packet, unsigned field = 0);
std::optional<Reading> DecodeHidppBattery(unsigned feature, const Bytes& body, bool precise = true);
std::string HidppUnitIdentity(const Bytes& body);
bool ValidHidppUnitIdentity(const std::string& unit);
using HidppCall = std::function<Bytes(unsigned, unsigned, unsigned, Bytes)>;
std::vector<std::pair<Candidate, std::optional<Reading>>> ReadHidppUnits(const Candidate& receiver, const HidppCall& call, bool readBattery = true,
    std::map<std::string, std::string>* names = nullptr);
bool HidppReplyMatches(const Bytes& packet, unsigned target, unsigned feature, unsigned address);
Bytes SonyRequest(unsigned command, unsigned transaction);
std::optional<Bytes> SonyPayload(const Bytes& packet, unsigned command, unsigned transaction);
}
