#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <objbase.h>
#include <setupapi.h>
#include <hidsdi.h>
#include <hidpi.h>
#include <devpkey.h>
#include <cfgmgr32.h>
#include <tlhelp32.h>
#include <bcrypt.h>
#include <fcntl.h>
#include <io.h>
#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <deque>
#include <iostream>
#include <memory>
#include <mutex>
#include <thread>
#include "hidapi.h"
#include "tests/HidTrace.h"
#include "Protocol.h"
#include "Special.h"
#include "ExtraKeyboard.h"
#include "KeyboardAdvanced.h"
#include "HeadsetController.h"
#include "ExtraMouse.h"
#include "../Builtins/utilities/yyjson/yyjson.h"
#include "tests/SelfTests.h"

using namespace peripheral;
namespace
{
constexpr size_t MaxLine = 1024 * 1024;
constexpr size_t MaxDevices = 256;
struct Handle
{
    HANDLE value = INVALID_HANDLE_VALUE;
    explicit Handle(HANDLE h = INVALID_HANDLE_VALUE) : value(h) {}
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
};
struct HidHandle
{
    hid_device* value = nullptr;
    explicit HidHandle(const std::string& path) : value(hid_open_path(path.c_str())) {}
    ~HidHandle() { if (value) hid_close(value); }
};
std::string Utf8(const std::wstring& text)
{
    if (text.empty()) return {};
    int n = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), int(text.size()), nullptr, 0, nullptr, nullptr);
    if (!n) return {};
    std::string result(n, 0);
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), int(text.size()), result.data(), n, nullptr, nullptr);
    return result;
}
std::wstring Wide(const std::string& text)
{
    int n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), int(text.size()), nullptr, 0);
    if (!n) return {};
    std::wstring result(n, 0);
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), int(text.size()), result.data(), n);
    return result;
}
std::string Lower(std::string s)
{
    for (auto& ch : s) if (ch >= 'A' && ch <= 'Z') ch += 'a' - 'A';
    return s;
}
std::string Now()
{
    SYSTEMTIME t{}; GetSystemTime(&t);
    char value[40]{};
    std::snprintf(value, sizeof(value), "%04u-%02u-%02uT%02u:%02u:%02u.%03uZ",
        t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
    return value;
}
using Json = std::unique_ptr<yyjson_doc, decltype(&yyjson_doc_free)>;
struct Response
{
    yyjson_mut_doc* doc = yyjson_mut_doc_new(nullptr);
    yyjson_mut_val* root = yyjson_mut_obj(doc);
    explicit Response(yyjson_val* requestId)
    {
        yyjson_mut_doc_set_root(doc, root);
        yyjson_mut_obj_add_int(doc, root, "version", 1);
        yyjson_mut_obj_add_val(doc, root, "requestId", requestId ? yyjson_val_mut_copy(doc, requestId) : yyjson_mut_null(doc));
    }
    ~Response() { yyjson_mut_doc_free(doc); }
    void String(yyjson_mut_val* object, const char* key, const std::string& value)
    { yyjson_mut_obj_add_strcpy(doc, object, key, value.c_str()); }
    bool Send()
    {
        size_t length = 0;
        char* json = yyjson_mut_write(doc, 0, &length);
        if (!json || length + 1 > MaxLine) { free(json); return false; }
        std::cout.write(json, std::streamsize(length)); std::cout.put('\n'); std::cout.flush(); free(json);
        return bool(std::cout);
    }
};
void Event(yyjson_val* id, const char* event, const std::string& identity)
{
    Response response(id); response.String(response.root, "event", event); response.String(response.root, "identity", identity);
    if (!response.Send()) ExitProcess(3);
}
struct Channel
{
    Candidate c;
    std::vector<std::pair<HIDP_REPORT_TYPE, HIDP_VALUE_CAPS>> batteries;
    bool hasBattery = false;
};
std::vector<HIDP_VALUE_CAPS> Values(PHIDP_PREPARSED_DATA data, const HIDP_CAPS& caps, HIDP_REPORT_TYPE type)
{
    USHORT count = type == HidP_Feature ? caps.NumberFeatureValueCaps : caps.NumberInputValueCaps;
    if (!count || count > 1024) return {};
    std::vector<HIDP_VALUE_CAPS> values(count);
    if (HidP_GetValueCaps(type, values.data(), &count, data) != HIDP_STATUS_SUCCESS) return {};
    values.resize(count); return values;
}
bool IsBattery(const HIDP_VALUE_CAPS& f)
{
    USAGE usage = f.UsagePage == 6 ? 0x20 : f.UsagePage == 0x85 ? 0x64 : 0;
    return usage && !f.IsAlias && f.IsAbsolute && f.ReportCount == 1 && f.BitSize >= 7 && f.BitSize <= 32 &&
        !(f.BitField & 1) && (f.BitField & 2) && !f.Units && !f.UnitsExp &&
        f.LogicalMin == 0 && f.LogicalMax == 100 &&
        (f.IsRange ? f.Range.UsageMin <= usage && f.Range.UsageMax >= usage : f.NotRange.Usage == usage);
}
std::string UsbParentSerial(const Candidate& c)
{
    // Azoth HID serial metadata contains changing trailing bytes.
    // Use the USB bus's unique instance serial, never trim or guess a prefix.
    HDEVINFO list = SetupDiCreateDeviceInfoList(nullptr, nullptr);
    if (list == INVALID_HANDLE_VALUE) return {};
    SP_DEVICE_INTERFACE_DATA iface{}; iface.cbSize = sizeof(iface);
    SP_DEVINFO_DATA info{}; info.cbSize = sizeof(info);
    DWORD required = 0; auto path = Wide(c.path);
    bool opened = SetupDiOpenDeviceInterfaceW(list, path.c_str(), 0, &iface) != FALSE;
    if (opened)
    {
        SetupDiGetDeviceInterfaceDetailW(list, &iface, nullptr, 0, &required, nullptr);
        if (required < sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W) || required > 32768) opened = false;
        else
        {
            Bytes buffer(required); auto detail = reinterpret_cast<SP_DEVICE_INTERFACE_DETAIL_DATA_W*>(buffer.data());
            detail->cbSize = sizeof(*detail);
            opened = SetupDiGetDeviceInterfaceDetailW(list, &iface, detail, required, nullptr, &info) != FALSE;
        }
    }
    SetupDiDestroyDeviceInfoList(list);
    if (!opened || !info.DevInst) return {};
    char prefix[40]{};
    std::snprintf(prefix, sizeof(prefix), "usb\\vid_%04x&pid_%04x\\", c.vid, c.pid);
    DEVINST node = info.DevInst;
    for (unsigned depth = 0; depth < 8; ++depth)
    {
        wchar_t instance[MAX_DEVICE_ID_LEN]{};
        if (CM_Get_Device_IDW(node, instance, MAX_DEVICE_ID_LEN, 0) != CR_SUCCESS) return {};
        auto id = Utf8(instance);
        if (Lower(id).rfind(prefix, 0) == 0)
        {
            DWORD caps = 0, size = sizeof(caps), type = 0;
            if (CM_Get_DevNode_Registry_PropertyW(node, CM_DRP_CAPABILITIES, &type, &caps, &size, 0) != CR_SUCCESS ||
                !(caps & CM_DEVCAP_UNIQUEID)) return {};
            auto serial = id.substr(strlen(prefix));
            if (serial.size() < 4 || serial.size() > 128 ||
                std::any_of(serial.begin(), serial.end(), [](unsigned char ch) { return ch <= 0x20 || ch >= 0x7f; })) return {};
            return serial;
        }
        DEVINST parent = 0;
        if (CM_Get_Parent(&parent, node, 0) != CR_SUCCESS) return {};
        node = parent;
    }
    return {};
}
bool Metadata(Channel& ch, bool descriptor)
{
    auto path = Wide(ch.c.path);
    if (path.empty()) return false;
    Handle handle(CreateFileW(path.c_str(), 0, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr));
    if (handle.value == INVALID_HANDLE_VALUE) return false;
    HIDD_ATTRIBUTES attributes{}; attributes.Size = sizeof(attributes);
    if (!HidD_GetAttributes(handle.value, &attributes)) return false;
    if ((ch.c.vid && ch.c.vid != attributes.VendorID) || (ch.c.pid && ch.c.pid != attributes.ProductID)) return false;
    ch.c.vid = attributes.VendorID; ch.c.pid = attributes.ProductID; ch.c.revision = attributes.VersionNumber;
    PHIDP_PREPARSED_DATA data = nullptr;
    if (!HidD_GetPreparsedData(handle.value, &data)) return false;
    HIDP_CAPS caps{};
    bool valid = HidP_GetCaps(data, &caps) == HIDP_STATUS_SUCCESS;
    if (valid)
    {
        ch.c.page = caps.UsagePage; ch.c.usage = caps.Usage;
        ch.c.input = caps.InputReportByteLength; ch.c.output = caps.OutputReportByteLength; ch.c.feature = caps.FeatureReportByteLength;
        ch.c.pointer = ch.c.page == 1 && ch.c.usage == 2;
        ch.c.keyboard = ch.c.page == 1 && ch.c.usage == 6;
        ch.c.kind = ch.c.pointer ? "mouse" : ch.c.keyboard ? "keyboard" :
            ch.c.page == 1 && (ch.c.usage == 4 || ch.c.usage == 5) ? "gamepad" :
            ch.c.page == 0x0b && ch.c.usage == 5 ? "headset" : "unknown";
        ch.batteries.clear();
        for (auto type : {HidP_Feature, HidP_Input})
        {
            unsigned length = type == HidP_Feature ? ch.c.feature : ch.c.input;
            if (!length || length > 4096) continue;
            for (const auto& f : Values(data, caps, type)) if (IsBattery(f))
            {
                // Prefer a Feature view of the same link collection when both exist.
                if (type == HidP_Input && std::any_of(ch.batteries.begin(), ch.batteries.end(), [&](const auto& old) {
                    return old.first == HidP_Feature && old.second.LinkCollection == f.LinkCollection && old.second.UsagePage == f.UsagePage;
                })) continue;
                ch.batteries.emplace_back(type, f);
            }
        }
        ch.hasBattery = !ch.batteries.empty() && ch.batteries.size() <= 8;
        ch.c.standard = ch.hasBattery && ch.c.kind != "unknown";
    }
    HidD_FreePreparsedData(data);
    if (!valid) return false;
    ch.c.serial.clear();
    ch.c.hidSerial.clear();
    ch.c.usbProduct.clear();
    wchar_t product[256]{};
    if (HidD_GetProductString(handle.value, product, sizeof(product))) ch.c.usbProduct = Utf8(product);
    wchar_t serial[256]{};
    if (HidD_GetSerialNumberString(handle.value, serial, sizeof(serial)))
    {
        auto value = Utf8(serial);
        bool meaningful = value.size() >= 4 && value.size() < 256 &&
            std::any_of(value.begin(), value.end(), [](char ch) { return ch != '0' && ch != ' ' && ch != '-'; });
        if (meaningful) ch.c.serial = value;
    }
    if (ch.c.vid == 0x0b05 && (ch.c.pid == 0x1a83 || ch.c.pid == 0x1a85))
    { ch.c.hidSerial = ch.c.serial; ch.c.serial = UsbParentSerial(ch.c); }
    if (descriptor && NeedsDescriptor(ch.c.vid, ch.c.pid))
    {
        ch.c.layout = {};
        HidHandle hid(ch.c.path);
        if (hid.value)
        {
            Bytes bytes(HID_API_MAX_REPORT_DESCRIPTOR_SIZE);
            int count = hid_get_report_descriptor(hid.value, bytes.data(), bytes.size());
            if (count > 0) { bytes.resize(count); ch.c.layout = ParseDescriptor(bytes); }
        }
    }
    return true;
}
std::wstring Property(HDEVINFO list, SP_DEVINFO_DATA& info, DWORD key)
{
    wchar_t text[2048]{}; DWORD type = 0, required = 0;
    if (!SetupDiGetDeviceRegistryPropertyW(list, &info, key, &type, reinterpret_cast<PBYTE>(text), sizeof(text) - sizeof(wchar_t), &required)) return {};
    if (type == REG_MULTI_SZ)
        for (size_t i = 0; i + 1 < required / sizeof(wchar_t); ++i) if (!text[i]) text[i] = ' ';
    return text;
}
std::string Container(HDEVINFO list, SP_DEVINFO_DATA& info)
{
    GUID id{}; DEVPROPTYPE type = 0; DWORD required = 0;
    if (!SetupDiGetDevicePropertyW(list, &info, &DEVPKEY_Device_ContainerId, &type, reinterpret_cast<PBYTE>(&id), sizeof(id), &required, 0) ||
        type != DEVPROP_TYPE_GUID || IsEqualGUID(id, GUID_NULL)) return {};
    wchar_t value[40]{};
    if (!StringFromGUID2(id, value, int(std::size(value)))) return {};
    return Lower(Utf8(value));
}
bool ParsePath(Candidate& c)
{
    auto readHex = [&](const char* key, unsigned digits, unsigned& target) {
        size_t at = c.path.find(key); if (at == std::string::npos || at + strlen(key) + digits > c.path.size()) return false;
        target = 0;
        for (unsigned i = 0; i < digits; ++i)
        {
            char ch = c.path[at + strlen(key) + i];
            int value = ch >= '0' && ch <= '9' ? ch - '0' : ch >= 'a' && ch <= 'f' ? ch - 'a' + 10 : -1;
            if (value < 0) return false; target = target * 16 + value;
        }
        return true;
    };
    unsigned mi = 0;
    if (!readHex("vid_", 4, c.vid) || !readHex("pid_", 4, c.pid)) return false;
    c.mi = readHex("&mi_", 2, mi) ? int(mi) : -1;
    return true;
}
bool Excluded(const std::set<std::string>& excluded, const Candidate& c)
{
    if (excluded.count(c.identity) || excluded.count(c.path) || excluded.count("hid-path:v1|" + c.path) ||
        excluded.count("pnp-path:v1|" + c.path)) return true;
    if (!c.transportPath.empty() && (excluded.count(c.transportPath) ||
        excluded.count("hid-path:v1|" + c.transportPath) || excluded.count("pnp-path:v1|" + c.transportPath))) return true;
    for (const auto& identity : excluded)
    {
        auto at = identity.find("|channel=");
        if (at != std::string::npos && identity.substr(at + 9) == c.path) return true;
    }
    return false;
}
std::vector<Channel> Discover(yyjson_val* requestId, const std::set<std::string>& excluded, const std::vector<Channel>& previous)
{
    Event(requestId, "progress", "pnp:hid-enumeration");
    GUID hidGuid{}; HidD_GetHidGuid(&hidGuid);
    HDEVINFO list = SetupDiGetClassDevsW(&hidGuid, nullptr, nullptr, DIGCF_DEVICEINTERFACE | DIGCF_PRESENT);
    if (list == INVALID_HANDLE_VALUE) throw std::runtime_error("hid-enumeration-failed");
    std::vector<Channel> channels;
    for (DWORD index = 0; index < 4096; ++index)
    {
        SP_DEVICE_INTERFACE_DATA iface{}; iface.cbSize = sizeof(iface);
        if (!SetupDiEnumDeviceInterfaces(list, nullptr, &hidGuid, index, &iface))
        {
            if (GetLastError() != ERROR_NO_MORE_ITEMS) { SetupDiDestroyDeviceInfoList(list); throw std::runtime_error("hid-enumeration-incomplete"); }
            break;
        }
        DWORD size = 0;
        SetupDiGetDeviceInterfaceDetailW(list, &iface, nullptr, 0, &size, nullptr);
        if (size < sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W) || size > 32768) continue;
        std::vector<unsigned char> buffer(size);
        auto detail = reinterpret_cast<SP_DEVICE_INTERFACE_DETAIL_DATA_W*>(buffer.data()); detail->cbSize = sizeof(*detail);
        SP_DEVINFO_DATA info{}; info.cbSize = sizeof(info);
        if (!SetupDiGetDeviceInterfaceDetailW(list, &iface, detail, size, nullptr, &info)) continue;
        Channel ch;
        ch.c.path = Lower(Utf8(detail->DevicePath));
        if (!ParsePath(ch.c)) continue;
        if (Excluded(excluded, ch.c)) continue;
        Event(requestId, "progress", "hid-path:v1|" + ch.c.path);
        auto compatible = Lower(Utf8(Property(list, info, SPDRP_COMPATIBLEIDS)));
        bool standardCandidate = compatible.find("hid_device_system_mouse") != std::string::npos ||
            compatible.find("hid_device_system_keyboard") != std::string::npos;
        if (!KnownIdentity(ch.c.vid, ch.c.pid) && !standardCandidate) continue;
        ch.c.physical = Container(list, info);
        if (ch.c.physical.empty()) continue; // No name/index identity fallback.
        ch.c.name = Utf8(Property(list, info, SPDRP_FRIENDLYNAME));
        if (ch.c.name.empty()) ch.c.name = Utf8(Property(list, info, SPDRP_DEVICEDESC));
        ch.c.identity = CanonicalIdentity(ch.c);
        if (!Metadata(ch, true))
        {
            auto old = std::find_if(previous.begin(), previous.end(), [&](const auto& cached) {
                return cached.c.path == ch.c.path && cached.c.physical == ch.c.physical;
            });
            if (old != previous.end()) ch.c = old->c;
            ch.c.presentUnreadable = true;
        }
        channels.push_back(std::move(ch));
        if (channels.size() > MaxDevices) { SetupDiDestroyDeviceInfoList(list); throw std::runtime_error("too-many-hid-candidates"); }
    }
    SetupDiDestroyDeviceInfoList(list);
    for (auto& main : channels)
    {
        auto& c = main.c;
        if (c.presentUnreadable || c.vid != 0x0b05 || (c.pid != 0x1ace && c.pid != 0x1ad0) || c.mi != 2 ||
            c.page != 0xff00 || c.usage != 1 || c.input != 64 || c.output != 64 ||
            !c.layout.Has(Report::Input, 2, 64) || !c.layout.Has(Report::Output, 2, 64)) continue;
        c.transportPath.clear();
        unsigned count = 0;
        for (const auto& peer : channels)
        {
            const auto& p = peer.c;
            if (p.presentUnreadable || p.physical != c.physical || p.vid != c.vid || p.pid != c.pid || p.revision != c.revision ||
                p.mi != 2 || p.page != 0xff02 || p.usage != 1 || p.input != 64 || p.output != 64 || p.path == c.path ||
                !p.layout.Has(Report::Input, 1, 64) || !p.layout.Has(Report::Output, 1, 64) ||
                (c.pid == 0x1ace && (p.path.find("mi_02&col01") == std::string::npos || c.path.find("mi_02&col02") == std::string::npos))) continue;
            c.transportPath = p.path; ++count;
        }
        if (count != 1) c.transportPath.clear();
    }
    std::vector<Candidate> candidates;
    for (auto& ch : channels)
    {
        if (ch.c.presentUnreadable) { candidates.push_back(ch.c); continue; }
        bool mouse = ch.c.pointer, keyboard = ch.c.keyboard;
        for (const auto& peer : channels) if (peer.c.physical == ch.c.physical)
        { mouse |= peer.c.pointer; keyboard |= peer.c.keyboard; }
        ch.c.pointer = mouse; ch.c.keyboard = keyboard;
        Event(requestId, "progress", "hid-path:v1|" + ch.c.path);
        AttachSerialTransport(ch.c);
        Classify(ch.c); candidates.push_back(ch.c);
    }
    SelectUnique(candidates);
    for (size_t i = 0; i < channels.size(); ++i)
    {
        if (!channels[i].c.presentUnreadable) channels[i].c = std::move(candidates[i]);
    }
    Event(requestId, "progress", "pnp:special-enumeration");
    for (auto& candidate : DiscoverSpecial(excluded, [&](const std::string& path) { Event(requestId, "progress", path); }))
    {
        if (channels.size() >= MaxDevices) throw std::runtime_error("too-many-hid-candidates");
        if (!Excluded(excluded, candidate)) { Channel ch; ch.c = std::move(candidate); channels.push_back(std::move(ch)); }
    }
    // Composite keyboard/mouse/audio input collections are not battery devices.
    // Explicit saved identities can still receive an unavailable read response.
    channels.erase(std::remove_if(channels.begin(), channels.end(), [&](const auto& ch) {
        return ch.c.protocol == Protocol::None || Excluded(excluded, ch.c);
    }), channels.end());
    return channels;
}
bool Revalidate(const Channel& channel)
{
    Channel fresh; fresh.c = channel.c; fresh.c.layout = {}; fresh.c.standard = false;
    if (!Metadata(fresh, channel.c.protocol != Protocol::Standard)) return false;
    fresh.c.pointer |= channel.c.pointer; fresh.c.keyboard |= channel.c.keyboard;
    Classify(fresh.c);
    return fresh.c.protocol == channel.c.protocol && fresh.c.adapter == channel.c.adapter && fresh.c.mi == channel.c.mi && fresh.c.page == channel.c.page &&
        fresh.c.usage == channel.c.usage && fresh.c.serial == channel.c.serial && fresh.c.revision == channel.c.revision && fresh.c.input == channel.c.input &&
        fresh.c.output == channel.c.output && fresh.c.feature == channel.c.feature && fresh.c.layout.bytes == channel.c.layout.bytes &&
        (channel.c.protocol != Protocol::Standard || (fresh.hasBattery && fresh.batteries.size() == channel.batteries.size() &&
         std::equal(fresh.batteries.begin(), fresh.batteries.end(), channel.batteries.begin(), [](const auto& a, const auto& b) {
             return a.first == b.first && a.second.ReportID == b.second.ReportID && a.second.UsagePage == b.second.UsagePage &&
                 a.second.LinkCollection == b.second.LinkCollection && a.second.BitSize == b.second.BitSize;
         })));
}
std::optional<Reading> ReadStandard(const Channel& channel)
{
    auto path = Wide(channel.c.path);
    Handle handle(CreateFileW(path.c_str(), 0, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr));
    if (handle.value == INVALID_HANDLE_VALUE) return {};
    PHIDP_PREPARSED_DATA data = nullptr;
    if (!HidD_GetPreparsedData(handle.value, &data)) return {};
    Reading result; bool success = false;
    for (const auto& [type, battery] : channel.batteries)
    {
        Bytes bytes(type == HidP_Feature ? channel.c.feature : channel.c.input, 0); bytes[0] = battery.ReportID;
        ULONG value = 0;
        USAGE usage = battery.UsagePage == 6 ? 0x20 : 0x64;
        bool read = type == HidP_Feature ? HidD_GetFeature(handle.value, bytes.data(), ULONG(bytes.size())) :
            HidD_GetInputReport(handle.value, bytes.data(), ULONG(bytes.size()));
        Reading component;
        component.role = channel.batteries.size() == 1 ? "main" : "battery " + std::to_string(result.components.size() + 1);
        if (read && bytes[0] == battery.ReportID && HidP_GetUsageValue(type, battery.UsagePage,
            battery.LinkCollection, usage, &value, data, reinterpret_cast<PCHAR>(bytes.data()), ULONG(bytes.size())) == HIDP_STATUS_SUCCESS && value <= 100)
        { component.percentage = int(value); success = true; }
        result.components.push_back(std::move(component));
    }
    HidD_FreePreparsedData(data);
    if (!success) return {};
    return result.components.size() == 1 ? result.components.front() : result;
}
bool Drain(hid_device* handle, unsigned length)
{
    Bytes bytes(std::max(length, 65u));
    for (unsigned i = 0; i < 32; ++i)
    {
        int count = hid_read_timeout(handle, bytes.data(), bytes.size(), 1);
        if (count < 0) return false;
        if (!count) return true;
    }
    return false;
}
std::optional<Reading> Exchange(hid_device* hid, const Candidate& c, unsigned field, bool send)
{
    Query q = BatteryQuery(c, field);
    if (q.packet.empty() && q.transport != Transport::Passive) return {};
    Bytes previous;
    bool dock = c.protocol == Protocol::Razer && c.vid == 0x1532 && c.pid == 0x00a4;
    bool freshnessObserved = false;
    if (dock)
    {
        previous.assign(91, 0);
        if (hid_get_feature_report(hid, previous.data(), previous.size()) != 91) return {};
    }
    if (send && !q.packet.empty())
    {
        int count = -1;
        if (q.transport == Transport::Feature) count = hid_send_feature_report(hid, q.packet.data(), q.packet.size());
        else if (q.transport == Transport::ControlOutput)
        {
            auto path = Wide(c.path);
            Handle h(CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr));
            if (h.value != INVALID_HANDLE_VALUE && HidD_SetOutputReport(h.value, q.packet.data(), ULONG(q.packet.size()))) count = int(q.packet.size());
        }
        else count = hid_write(hid, q.packet.data(), q.packet.size());
        if (count != int(q.packet.size())) return {};
    }
    ULONGLONG end = GetTickCount64() + q.waitMs;
    unsigned featureAttempts = 0;
    do
    {
        Bytes bytes(std::max(q.replyLength, 65u), 0);
        int count = 0;
        if (q.transport == Transport::Feature)
        {
            if (++featureAttempts > 4) break;
            Sleep(c.protocol == Protocol::Razer ? 100 : 30);
            bytes.resize(q.replyLength); bytes[0] = q.packet[0];
            count = hid_get_feature_report(hid, bytes.data(), bytes.size());
        }
        else count = hid_read_timeout(hid, bytes.data(), bytes.size(), 40);
        if (count < 0) return {};
        if (!count) continue;
        bytes.resize(count);
        if (dock && bytes.size() == 91)
        {
            unsigned crc = 0; for (unsigned i = 3; i < 89; ++i) crc ^= bytes[i];
            bool related = bytes[0] == 0 && bytes[2] == q.packet[2] && bytes[9] == 0 && crc == bytes[89] &&
                std::equal(bytes.begin() + 3, bytes.begin() + 9, q.packet.begin() + 3);
            freshnessObserved |= related && (bytes != previous || bytes[1] == 0 || bytes[1] == 1);
            if (!freshnessObserved) continue;
        }
        if (auto reading = Decode(c, bytes, field)) return reading;
    } while (GetTickCount64() < end);
    return {};
}
class Hidpp
{
    hid_device* hid;
    ULONGLONG deadline = GetTickCount64() + 18000;
    unsigned tag = 0;
    std::function<void()> progress;
    bool longOnly = false;
public:
    explicit Hidpp(hid_device* handle, std::function<void()> update = {}) : hid(handle), progress(std::move(update)) {}
#ifdef PERIPHERAL_HID_TRACE
    Bytes DiagnoseC547Ping() { longOnly = true; return Call(1, 0, 1, {0, 0, 0x35}); }
#endif
    Bytes Call(unsigned target, unsigned feature, unsigned function, Bytes params = {})
    {
        if (GetTickCount64() >= deadline || !Drain(hid, 20)) return {};
        if (progress) progress();
        const bool firstCall = tag == 0;
        tag = tag % 15 + 1;
        unsigned address = (function << 4) | tag;
        Bytes request{0x10, static_cast<unsigned char>(target), static_cast<unsigned char>(feature), static_cast<unsigned char>(address)};
        params.resize(3); request.insert(request.end(), params.begin(), params.end());
        if (longOnly) { request[0] = 0x11; request.resize(20); }
        if (hid_write(hid, request.data(), request.size()) != int(request.size())) return {};
        // C547's cold first ping took 1585 ms. Give it 2.5 seconds without
        // lengthening every empty-slot probe or changing reply correlation.
        unsigned wait = firstCall && feature == 0 && function == 1 ? 2500 : 350;
        ULONGLONG end = std::min(deadline, GetTickCount64() + wait);
        while (GetTickCount64() < end)
        {
            Bytes bytes(64); int count = hid_read_timeout(hid, bytes.data(), bytes.size(), 20);
            if (count < 0) return {}; bytes.resize(std::max(count, 0));
            if (HidppReplyMatches(bytes, target, feature, address)) return Bytes(bytes.begin() + 4, bytes.end());
            if (bytes.size() >= 6 && bytes[1] == target && (bytes[2] == 0x8f || bytes[2] == 0xff) &&
                bytes[3] == feature && bytes[4] == address) return {};
        }
        return {};
    }
    unsigned FeatureIndex(unsigned target, unsigned feature)
    {
        Bytes body = Call(target, 0, 0, {static_cast<unsigned char>(feature >> 8), static_cast<unsigned char>(feature)});
        return body.empty() ? 0 : body[0];
    }
    std::vector<std::pair<Candidate, std::optional<Reading>>> Read(const Candidate& receiver, bool readBattery = true)
    {
        // One worker thread owns this process-local cache. Only names confirmed
        // against the hardware unit/model key enter it; receiver PID is not a model.
        static std::map<std::string, std::string> names;
        longOnly = !receiver.layout.Has(Report::Output, 0x10, 7);
        auto results = ReadHidppUnits(receiver, [&](unsigned target, unsigned feature, unsigned function, Bytes params) {
            return Call(target, feature, function, std::move(params));
        }, readBattery, &names);
        if (results.empty() && !receiver.unit.empty())
        {
            auto cached = names.find(receiver.unit);
            if (cached != names.end())
            { auto device = receiver; device.name = cached->second; results.push_back({std::move(device), std::nullopt}); }
        }
        return results;
    }
};
std::optional<Reading> Read(Channel& channel, yyjson_val* id, std::set<std::string>& initialized)
{
    auto& c = channel.c;
    if (c.protocol == Protocol::None || c.presentUnreadable || c.identityOnly) return {};
    Event(id, "progress", c.identity);
    if (c.protocol == Protocol::OHub || c.protocol == Protocol::Gaia)
        return ReadSpecial(c, [&] { Event(id, "progress", c.identity); });
    Event(id, "progress", "hid-path:v1|" + c.path);
    if (!Revalidate(channel)) return {};
    if (IsSpecial(c.protocol)) return ReadSpecial(c, [&] { Event(id, "progress", "hid-path:v1|" + c.path); });
    if (c.protocol == Protocol::ExtraKeyboard)
        return extra_keyboard::Read(c, [&] { Event(id, "progress", "hid-path:v1|" + c.path); });
    if (c.protocol == Protocol::KeyboardAdvanced)
        return keyboard_advanced::Read(c, [&] { Event(id, "progress", "hid-path:v1|" + c.path); });
    if (c.protocol == Protocol::HeadsetController)
        return headset_controller::Read(c, [&] { Event(id, "progress", "hid-path:v1|" + c.path); }, [&] {
            return BeginInitialization(initialized, c.identity, [&] { Event(id, "initialized", c.identity); });
        });
    if (c.protocol == Protocol::ExtraMouse)
        return extra_mouse::Read(c, [&] { Event(id, "progress", "hid-path:v1|" + c.path); });
    if (c.protocol == Protocol::Standard)
    {
        auto first = ReadStandard(channel);
        return first ? ReadStandard(channel) : std::nullopt;
    }
    HidHandle hid(c.path); if (!hid.value) return {};
    if (c.protocol == Protocol::Hidpp) return {}; // Routed separately: one receiver can return multiple units.
    bool once = OnceOnly(c.protocol), send = true;
    if (once)
    {
        if (!initialized.count(c.identity) && initialized.size() >= 4096) return {};
        send = BeginInitialization(initialized, c.identity, [&] { Event(id, "initialized", c.identity); });
    }
    auto query = BatteryQuery(c);
    if (query.transport != Transport::Passive && !Drain(hid.value, c.input)) return {};
    std::optional<Reading> result;
    for (unsigned round = 0; round < 2; ++round)
    {
        Event(id, "progress", "hid-path:v1|" + c.path);
        auto next = Exchange(hid.value, c, 0, send && (!once || !round));
        if (!next) return {};
        if (next->state == "sleeping") return next;
        if (result && result->peer != next->peer) return {};
        if (c.protocol == Protocol::Sonix || c.protocol == Protocol::Pelta)
        {
            Event(id, "progress", "hid-path:v1|" + c.path);
            auto charge = Exchange(hid.value, c, 1, true);
            if (!charge || !charge->charging.has_value()) return {};
            next->charging = charge->charging;
        }
        result = next;
    }
    return result;
}
void Device(Response& response, yyjson_mut_val* devices, const Candidate& c, const std::optional<Reading>& reading, bool discovery, bool extensions = false)
{
    auto d = response.doc; auto item = yyjson_mut_obj(d); yyjson_mut_arr_append(devices, item);
    response.String(item, "identity", c.identity); response.String(item, "name", c.name.empty() ? "HID peripheral" : c.name);
    auto legacy = LegacyIdentities(c);
    if (!legacy.empty())
    {
        auto aliases = yyjson_mut_arr(d); yyjson_mut_obj_add_val(d, item, "legacyIdentities", aliases);
        for (const auto& alias : legacy) yyjson_mut_arr_add_strcpy(d, aliases, alias.c_str());
    }
    if (c.protocol == Protocol::Hidpp)
    {
        yyjson_mut_obj_add_bool(d, item, "receiverProbe", c.unit.empty());
        if (!c.unit.empty())
        {
            Candidate parent = c; parent.unit.clear();
            response.String(item, "discoveryParentIdentity", CanonicalIdentity(parent));
        }
    }
    bool wireless = (c.protocol == Protocol::Hidpp && c.pid == 0xc547) || c.adapter == "blackshark_v2_hs" ||
        c.adapter == "rog_omni_keyboard" || (c.adapter == "rog_azoth_x" && (c.pid == 0x1a85 || c.pid == 0x1c25));
    response.String(item, "kind", c.kind); response.String(item, "connection", c.wired ? "usb" : wireless ? "wireless" : "unknown");
    response.String(item, "state", reading ? reading->state : c.presentUnreadable ? "unavailable" : c.protocol == Protocol::None || c.identityOnly ? "unsupported" : "unavailable");
    response.String(item, "source", c.adapter.empty() ? ProtocolName(c.protocol) : c.adapter);
    response.String(item, "compatibility", c.protocol == Protocol::None || c.identityOnly || (c.protocol == Protocol::Hidpp && c.unit.empty()) ? "identityOnly" : c.protocol == Protocol::OHub ? "extension" : "implemented");
    yyjson_mut_obj_add_bool(d, item, "canRead", c.protocol != Protocol::None && !c.identityOnly && !c.presentUnreadable && (c.protocol != Protocol::OHub || extensions));
    auto batteries = yyjson_mut_arr(d); yyjson_mut_obj_add_val(d, item, "batteries", batteries);
    // Only successful fresh replies carry timestamps and battery values.
    auto battery = [&](const Reading& value) {
        if (!value.percentage && value.levelText.empty() && !value.charging) return;
        auto b = yyjson_mut_obj(d); yyjson_mut_arr_append(batteries, b);
        response.String(b, "role", value.role);
        yyjson_mut_obj_add_val(d, b, "percentage", value.percentage ? yyjson_mut_int(d, *value.percentage) : yyjson_mut_null(d));
        yyjson_mut_obj_add_val(d, b, "levelText", value.levelText.empty() ? yyjson_mut_null(d) : yyjson_mut_strcpy(d, value.levelText.c_str()));
        yyjson_mut_obj_add_bool(d, b, "estimated", value.estimated);
        yyjson_mut_obj_add_val(d, b, "charging", value.charging ? yyjson_mut_bool(d, *value.charging) : yyjson_mut_null(d));
    };
    if (!discovery && reading)
    {
        if (reading->components.empty()) battery(*reading);
        else for (const auto& component : reading->components) battery(component);
    }
    if (yyjson_mut_arr_size(batteries)) response.String(item, "updatedAt", Now());
    else yyjson_mut_obj_add_val(d, item, "updatedAt", yyjson_mut_null(d));
}
bool StringArray(yyjson_val* value, std::set<std::string>& result, size_t maximum)
{
    if (!value) return true;
    if (!yyjson_is_arr(value) || yyjson_arr_size(value) > maximum) return false;
    size_t index, count; yyjson_val* item;
    yyjson_arr_foreach(value, index, count, item)
    {
        if (!yyjson_is_str(item) || !yyjson_get_len(item) || yyjson_get_len(item) > 8192 ||
            strlen(yyjson_get_str(item)) != yyjson_get_len(item)) return false;
        result.emplace(yyjson_get_str(item));
    }
    return true;
}
bool ValidId(yyjson_val* id)
{
    return id && (yyjson_is_int(id) || (yyjson_is_str(id) && yyjson_get_len(id) > 0 && yyjson_get_len(id) <= 128 &&
        strlen(yyjson_get_str(id)) == yyjson_get_len(id)));
}
void Process(const std::string& line, std::vector<Channel>& channels, std::set<std::string>& initialized)
{
    Json document(yyjson_read(line.data(), line.size(), 0), yyjson_doc_free);
    auto root = document ? yyjson_doc_get_root(document.get()) : nullptr;
    auto id = yyjson_obj_get(root, "requestId");
    Response response(ValidId(id) ? id : nullptr);
    auto devices = yyjson_mut_arr(response.doc); yyjson_mut_obj_add_val(response.doc, response.root, "devices", devices);
    std::string error;
    try
    {
        auto version = yyjson_obj_get(root, "version"), operation = yyjson_obj_get(root, "operation");
        auto extensions = yyjson_obj_get(root, "vendorExtensionsEnabled");
        std::set<std::string> keys, ids, attempts, excluded;
        size_t index, count; yyjson_val *key, *value;
        bool duplicates = false;
        if (yyjson_is_obj(root)) yyjson_obj_foreach(root, index, count, key, value)
            if (!keys.insert(yyjson_get_str(key)).second) duplicates = true;
        if (!yyjson_is_obj(root) || duplicates || !yyjson_is_int(version) || yyjson_get_int(version) != 1 || !ValidId(id) ||
            !yyjson_is_str(operation) || (extensions && !yyjson_is_bool(extensions)) ||
            !StringArray(yyjson_obj_get(root, "deviceIds"), ids, MaxDevices) ||
            !StringArray(yyjson_obj_get(root, "excludedIdentities"), excluded, 4096) ||
            !StringArray(yyjson_obj_get(root, "attemptedInitializations"), attempts, 4096)) throw std::runtime_error("invalid-request");
        std::string op(yyjson_get_str(operation), yyjson_get_len(operation));
        // The extension flag never permits unknown IDs or configuration/firmware commands.
        if (op == "discover")
        {
            channels = Discover(id, excluded, channels);
            for (const auto& ch : channels) Device(response, devices, ch.c, {}, true, extensions && yyjson_get_bool(extensions));
        }
        else if (op == "read" || op == "identify")
        {
            bool identifying = op == "identify";
            if (identifying) for (const auto& identity : ids)
            {
                auto rootChannel = std::find_if(channels.begin(), channels.end(), [&](const auto& ch) { return ch.c.identity == identity; });
                if (rootChannel == channels.end() || rootChannel->c.protocol != Protocol::Hidpp || !rootChannel->c.unit.empty())
                    throw std::runtime_error("invalid-identify-target");
            }
            for (const auto& attempt : attempts) if (initialized.size() < 4096) initialized.insert(attempt);
            std::set<std::string> emitted;
            for (const auto& identity : ids)
            {
                if (excluded.count(identity)) continue;
                auto found = std::find_if(channels.begin(), channels.end(), [&](const auto& ch) { return ch.c.identity == identity; });
                std::optional<Channel> routed;
                if (found == channels.end())
                {
                    for (const auto& rootChannel : channels)
                    {
                        if (rootChannel.c.protocol != Protocol::Hidpp || rootChannel.c.presentUnreadable) continue;
                        std::string prefix = rootChannel.c.identity + "|unit=";
                        if (identity.rfind(prefix, 0) != 0) continue;
                        std::string unit = identity.substr(prefix.size());
                        if (!ValidHidppUnitIdentity(unit)) continue;
                        routed = rootChannel; routed->c.unit = unit; routed->c.identity = identity;
                        routed->c.name = "Logitech HID++ device"; break;
                    }
                }
                if (found == channels.end())
                {
                    if (!routed)
                    {
                        Candidate unknown; unknown.identity = identity; Device(response, devices, unknown, {}, false); continue;
                    }
                }
                Channel active = routed ? *routed : *found;
                if (Excluded(excluded, active.c)) continue;
                if (active.c.protocol == Protocol::Hidpp && !active.c.presentUnreadable)
                {
                    Event(id, "progress", "hid-path:v1|" + active.c.path);
                    std::vector<std::pair<Candidate, std::optional<Reading>>> results;
                    if (Revalidate(active))
                    {
                        HidHandle handle(active.c.path);
                        if (handle.value) results = Hidpp(handle.value, [&] { Event(id, "progress", "hid-path:v1|" + active.c.path); }).Read(active.c, !identifying);
                    }
                    if (results.empty()) Device(response, devices, active.c, {}, false);
                    for (const auto& result : results)
                        if (!Excluded(excluded, result.first) && emitted.insert(result.first.identity).second)
                            Device(response, devices, result.first, result.second, false);
                    continue;
                }
                if (active.c.protocol == Protocol::OHub && (!extensions || !yyjson_get_bool(extensions)))
                {
                    Device(response, devices, active.c, {}, false); continue;
                }
                auto reading = Read(active, id, initialized);
                Device(response, devices, active.c, reading, false, extensions && yyjson_get_bool(extensions));
            }
        }
        else throw std::runtime_error("unknown-operation");
    }
    catch (const std::exception& exception) { error = exception.what(); }
    response.String(response.root, "error", error);
    auto attempts = yyjson_mut_arr(response.doc); yyjson_mut_obj_add_val(response.doc, response.root, "initialized", attempts);
    for (const auto& identity : initialized) yyjson_mut_arr_add_strcpy(response.doc, attempts, identity.c_str());
    if (!response.Send()) ExitProcess(3);
}
int InspectHid()
{
    auto all = hid_enumerate(0, 0);
    for (auto item = all; item; item = item->next)
    {
        if (item->vendor_id != 0x0b05 && !(item->vendor_id == 0x046d && item->product_id == 0xc547) &&
            !(item->vendor_id == 0x1532 && item->product_id == 0x0565)) continue;
        Channel ch; ch.c.path = Lower(item->path); ParsePath(ch.c);
        bool metadata = Metadata(ch, true);
        Response r(nullptr); auto d = r.doc; auto o = r.root;
        r.String(o, "path", ch.c.path); r.String(o, "product", ch.c.usbProduct); r.String(o, "serial", ch.c.serial);
        yyjson_mut_obj_add_bool(d, o, "metadata", metadata);
        yyjson_mut_obj_add_uint(d, o, "vid", ch.c.vid); yyjson_mut_obj_add_uint(d, o, "pid", ch.c.pid);
        yyjson_mut_obj_add_int(d, o, "mi", ch.c.mi); yyjson_mut_obj_add_uint(d, o, "page", ch.c.page);
        yyjson_mut_obj_add_uint(d, o, "usage", ch.c.usage); yyjson_mut_obj_add_uint(d, o, "revision", ch.c.revision);
        yyjson_mut_obj_add_uint(d, o, "input", ch.c.input); yyjson_mut_obj_add_uint(d, o, "output", ch.c.output);
        yyjson_mut_obj_add_uint(d, o, "feature", ch.c.feature);
        HidHandle h(ch.c.path);
        if (h.value)
        {
            Bytes descriptor(HID_API_MAX_REPORT_DESCRIPTOR_SIZE);
            int n = hid_get_report_descriptor(h.value, descriptor.data(), descriptor.size());
            if (n > 0)
            {
                descriptor.resize(n); ch.c.layout = ParseDescriptor(descriptor);
                std::string hex; const char* digits = "0123456789abcdef";
                for (auto b : descriptor) { hex += digits[b >> 4]; hex += digits[b & 15]; }
                r.String(o, "descriptor", hex);
            }
        }
        auto reports = yyjson_mut_arr(d); yyjson_mut_obj_add_val(d, o, "reports", reports);
        for (const auto& [key, length] : ch.c.layout.bytes)
        {
            auto report = yyjson_mut_obj(d); yyjson_mut_arr_append(reports, report);
            r.String(report, "type", key.first == Report::Input ? "input" : key.first == Report::Output ? "output" : "feature");
            yyjson_mut_obj_add_uint(d, report, "id", key.second); yyjson_mut_obj_add_uint(d, report, "length", length);
        }
        r.Send();
    }
    hid_free_enumeration(all); return 0;
}
bool ActualParent(DWORD pid)
{
    if (!pid || pid == GetCurrentProcessId()) return false;
    Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0));
    PROCESSENTRY32W entry{}; entry.dwSize = sizeof(entry);
    if (!Process32FirstW(snapshot.value, &entry)) return false;
    do { if (entry.th32ProcessID == GetCurrentProcessId()) return entry.th32ParentProcessID == pid; }
    while (Process32NextW(snapshot.value, &entry));
    return false;
}
#ifdef PERIPHERAL_HID_TRACE
int DiagnoseC547Ping()
{
    hid_trace::enabled = true;
    for (const auto& channel : Discover(nullptr, {}, {}))
    {
        const auto& c = channel.c;
        if (c.protocol != Protocol::Hidpp || c.vid != 0x046d || c.pid != 0xc547 || !Revalidate(channel)) continue;
        HidHandle handle(c.path); if (!handle.value) return 1;
        auto start = GetTickCount64();
        auto body = Hidpp(handle.value).DiagnoseC547Ping();
        Response response(nullptr);
        yyjson_mut_obj_add_uint(response.doc, response.root, "elapsedMs", GetTickCount64() - start);
        bool valid = body.size() >= 3 && body[0] >= 2 && body[2] == 0x35;
        yyjson_mut_obj_add_bool(response.doc, response.root, "validPing", valid);
        if (valid) yyjson_mut_obj_add_uint(response.doc, response.root, "protocolMajor", body[0]);
        response.Send(); return valid ? 0 : 1;
    }
    return 1;
}
#endif
}

int wmain(int argc, wchar_t** argv)
{
    if (argc == 2 && std::wstring(argv[1]) == L"--self-test") return RunPeripheralSelfTests();
    if (argc == 2 && std::wstring(argv[1]) == L"--inspect-hid") return InspectHid();
#ifdef PERIPHERAL_HID_TRACE
    hid_trace::ConfigureFile();
    if (argc == 2 && std::wstring(argv[1]) == L"--diagnose-c547-ping") return DiagnoseC547Ping();
    if (argc == 4 && std::wstring(argv[3]) == L"--trace-hid") { hid_trace::enabled = true; --argc; }
#endif
    if (argc != 3 || std::wstring(argv[1]) != L"--parent-pid") return 2;
    wchar_t* end = nullptr; unsigned long parentPid = wcstoul(argv[2], &end, 10);
    if (!end || *end || !ActualParent(parentPid) || GetFileType(GetStdHandle(STD_INPUT_HANDLE)) != FILE_TYPE_PIPE ||
        GetFileType(GetStdHandle(STD_OUTPUT_HANDLE)) != FILE_TYPE_PIPE) return 2;
    Handle parent(OpenProcess(SYNCHRONIZE, FALSE, parentPid));
    if (!parent.value) return 2;
    _setmode(_fileno(stdin), _O_BINARY); _setmode(_fileno(stdout), _O_BINARY);
    Handle eof(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    if (!eof.value) return 2;
    std::thread([pidHandle = parent.value, eofHandle = eof.value] {
        HANDLE handles[] = {pidHandle, eofHandle};
        WaitForMultipleObjects(2, handles, FALSE, INFINITE); ExitProcess(0);
    }).detach();
    std::mutex mutex; std::condition_variable ready; std::deque<std::string> lines;
    std::thread([&] {
        std::string line; char buffer[4096]; DWORD count = 0;
        while (ReadFile(GetStdHandle(STD_INPUT_HANDLE), buffer, sizeof(buffer), &count, nullptr) && count)
            for (DWORD i = 0; i < count; ++i)
            {
                if (buffer[i] == '\n')
                {
                    if (!line.empty() && line.back() == '\r') line.pop_back();
                    { std::lock_guard<std::mutex> lock(mutex); if (lines.size() >= 1) ExitProcess(4); lines.push_back(std::move(line)); line.clear(); }
                    ready.notify_one();
                }
                else { if (line.size() + 2 > MaxLine) ExitProcess(4); line.push_back(buffer[i]); }
            }
        SetEvent(eof.value); // Also terminates a blocked metadata/driver call.
    }).detach();
    std::vector<Channel> channels; std::set<std::string> initialized;
    for (;;)
    {
        std::string line;
        { std::unique_lock<std::mutex> lock(mutex); ready.wait(lock, [&] { return !lines.empty(); }); line = std::move(lines.front()); lines.pop_front(); }
#ifdef PERIPHERAL_HID_TRACE
        hid_trace::Request(line.c_str());
#endif
        Process(line, channels, initialized);
#ifdef PERIPHERAL_HID_TRACE
        hid_trace::Flush();
#endif
    }
}
