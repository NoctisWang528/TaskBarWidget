#pragma once
#ifdef PERIPHERAL_HID_TRACE
#include "hidapi.h"
#include <cstdio>
#include <chrono>
#include <cstdlib>

// Opt-in diagnostic stderr only. Normal JSON IPC never contains raw HID data.
namespace peripheral::hid_trace
{
inline bool enabled = false;
inline FILE* output = stderr;
inline bool buffered = false;
inline void ConfigureFile()
{
    wchar_t* path = nullptr; size_t length = 0;
    if (_wdupenv_s(&path, &length, L"TASKBARWIDGETS_HID_TRACE_FILE") || !path) return;
    FILE* file = nullptr; _wfopen_s(&file, path, L"wb"); std::free(path);
    if (!file) return;
    output = file; buffered = true; enabled = true;
    std::setvbuf(output, nullptr, _IOFBF, 65536);
}
inline void Flush() { if (enabled) std::fflush(output); }
inline void Request(const char* line) { if (enabled) std::fprintf(output, "request %s\n", line); }
inline void Frame(const char* operation, hid_device* device, const unsigned char* bytes, size_t length, int result)
{
    if (!enabled) return;
    auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
    std::fprintf(output, "%lld %s handle=%p result=%d bytes=", static_cast<long long>(ms), operation, static_cast<void*>(device), result);
    for (size_t i = 0; i < length; ++i) std::fprintf(output, "%02x", bytes[i]);
    std::fputc('\n', output); if (!buffered) std::fflush(output);
}
inline hid_device* Open(const char* path)
{
    auto device = hid_open_path(path);
    if (enabled) { std::fprintf(output, "open handle=%p path=%s\n", static_cast<void*>(device), path); if (!buffered) std::fflush(output); }
    return device;
}
inline int Write(hid_device* d, const unsigned char* b, size_t n)
{ int r = hid_write(d, b, n); Frame("write", d, b, n, r); return r; }
inline int Output(hid_device* d, const unsigned char* b, size_t n)
{ int r = hid_send_output_report(d, b, n); Frame("control-output", d, b, n, r); return r; }
inline int Feature(hid_device* d, const unsigned char* b, size_t n)
{ int r = hid_send_feature_report(d, b, n); Frame("feature-send", d, b, n, r); return r; }
inline int GetFeature(hid_device* d, unsigned char* b, size_t n)
{ int r = hid_get_feature_report(d, b, n); Frame("feature-reply", d, b, r > 0 ? size_t(r) : 0, r); return r; }
inline int Read(hid_device* d, unsigned char* b, size_t n, int timeout)
{ int r = hid_read_timeout(d, b, n, timeout); if (r) Frame("read", d, b, r > 0 ? size_t(r) : 0, r); return r; }
}
#define hid_open_path peripheral::hid_trace::Open
#define hid_write peripheral::hid_trace::Write
#define hid_send_output_report peripheral::hid_trace::Output
#define hid_send_feature_report peripheral::hid_trace::Feature
#define hid_get_feature_report peripheral::hid_trace::GetFeature
#define hid_read_timeout peripheral::hid_trace::Read
#endif
