#pragma once
#include "ManagedRuntimeEnvironment.h"
#include <cwctype>
#include <filesystem>
#include <string>
#include <vector>

struct DotnetCheckResult
{
    bool success = false;
    int errorCode = 0;           // Win32 GetLastError() / hostfxr status
    std::wstring error;
};

namespace
{
    struct DotnetInitParams { size_t size; const wchar_t* host_path; const wchar_t* dotnet_root; };
    using InitializeFn = int (*)(const wchar_t*, const DotnetInitParams*, void**);
    using CloseFn = int (*)(void*);
    using ErrorWriterFn = void (*)(const wchar_t*);
    using SetErrorWriterFn = ErrorWriterFn (*)(ErrorWriterFn);

    // Used only during single-threaded startup. hostfxr reads this process's
    // environment; mirror the sanitized child policy and restore it on return.
    // SetEnvironmentVariable changes no user/machine setting or parent process.
    struct ScopedHostPolicy
    {
        const wchar_t* names[3] = {L"DOTNET_ROLL_FORWARD", L"DOTNET_ROLL_FORWARD_TO_PRERELEASE", L"DOTNET_ROLL_FORWARD_ON_NO_CANDIDATE_FX"};
        std::wstring values[3];
        bool existed[3]{};
        ScopedHostPolicy()
        {
            for (int i = 0; i < 3; ++i)
            {
                const DWORD length = GetEnvironmentVariableW(names[i], nullptr, 0);
                existed[i] = length != 0;
                if (length) { values[i].resize(length); GetEnvironmentVariableW(names[i], values[i].data(), length); values[i].resize(length - 1); }
                SetEnvironmentVariableW(names[i], nullptr);
            }
        }
        ~ScopedHostPolicy()
        {
            for (int i = 0; i < 3; ++i) SetEnvironmentVariableW(names[i], existed[i] ? values[i].c_str() : nullptr);
        }
    };

    struct QuietWriter { std::wstring text; bool active = false; };
    inline QuietWriter& Quiet()
    {
        static thread_local QuietWriter writer;   // lazy, no pre-main globals
        return writer;
    }
    inline void QuietCapture(const wchar_t* message)
    {
        auto& w = Quiet();
        if (!w.active || !message) return;
        constexpr size_t kCap = 4096;             // bounded diagnostics
        if (w.text.size() >= kCap) return;
        w.text.append(message, (std::min)(wcslen(message), kCap - w.text.size()));
    }

    struct DotnetVersion { unsigned major{}, minor{}, patch{}; bool stable = false; };
    inline DotnetVersion ParseVersion(const std::wstring& name)
    {
        DotnetVersion v; wchar_t extra{};
        if (name.empty() || name.find_first_not_of(L"0123456789.") != std::wstring::npos || name.size() > 24) return v;
        v.stable = swscanf_s(name.c_str(), L"%u.%u.%u%c", &v.major, &v.minor, &v.patch, &extra, 1u) == 3;
        return v;
    }

    inline std::wstring DescribeDotnetStatus(int status, const std::wstring& quiet, const std::filesystem::path& root)
    {
        wchar_t hex[32]{}; swprintf_s(hex, L"0x%08X", static_cast<unsigned>(status));
        std::wstring lower = quiet;
        for (auto& c : lower) c = static_cast<wchar_t>(towlower(c));
        if (lower.find(L"framework") != std::wstring::npos &&
            (lower.find(L"not") != std::wstring::npos || lower.find(L"missing") != std::wstring::npos))
            return L"A required .NET framework (Core/Desktop) is missing or incompatible under " + root.wstring() + L".";
        if (lower.find(L"invalid") != std::wstring::npos || lower.find(L"json") != std::wstring::npos)
            return L"The runtime config could not be read/parsed.";
        return L"hostfxr failed (status " + std::wstring(hex) + L") resolving the runtime config.";
    }
}

// Check that <root> can host the app described by <runtimeconfig>:
// load the highest stable host\fxr\<v>\hostfxr.dll, initialize off the real config with
// dotnet_root=<root>, close. No managed entrypoint or window/audio; host policy is restored.
inline DotnetCheckResult CheckDotnetRuntime(const std::filesystem::path& root,
                                            const std::filesystem::path& runtimeconfig)
{
    DotnetCheckResult result;
    std::error_code ec;
    if (root.empty() || !std::filesystem::is_directory(root, ec))
    { result.error = L"Invalid runtime root: " + root.wstring(); result.errorCode = ERROR_DIRECTORY; return result; }
    const std::filesystem::path fxrDir = root / L"host" / L"fxr";
    if (!std::filesystem::is_directory(fxrDir, ec))
    { result.error = L"Runtime has no host/fxr directory (x64 .NET 8 Desktop not installed under " + root.wstring() + L")."; result.errorCode = ERROR_FILE_NOT_FOUND; return result; }
    if (runtimeconfig.empty() || !std::filesystem::is_regular_file(runtimeconfig, ec))
    { result.error = L"Runtimeconfig missing: " + runtimeconfig.wstring(); result.errorCode = ERROR_FILE_NOT_FOUND; return result; }

    std::filesystem::path hostfxr; unsigned best[3] = {};
    for (auto iterator = std::filesystem::directory_iterator(fxrDir, ec);
         !ec && iterator != std::filesystem::directory_iterator(); iterator.increment(ec))
    {
        const auto& entry = *iterator;
        if (!entry.is_directory(ec)) continue;
        const auto v = ParseVersion(entry.path().filename().wstring());
        if (!v.stable) continue;
        if (hostfxr.empty() || v.major > best[0] || (v.major == best[0] && v.minor > best[1]) ||
            (v.major == best[0] && v.minor == best[1] && v.patch > best[2]))
        { best[0] = v.major; best[1] = v.minor; best[2] = v.patch; hostfxr = entry.path() / L"hostfxr.dll"; }
    }
    if (ec) { result.errorCode = ec.value(); result.error = L"Cannot read the installed .NET host directory."; return result; }
    if (hostfxr.empty())
    { result.error = L"No stable hostfxr under " + fxrDir.wstring() + L"; install x64 .NET 8 Desktop."; result.errorCode = ERROR_FILE_NOT_FOUND; return result; }

    HMODULE module = LoadLibraryExW(hostfxr.c_str(), nullptr,
                                    LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!module)
    { const DWORD e = GetLastError(); result.error = L"Cannot load " + hostfxr.wstring(); result.errorCode = static_cast<int>(e); return result; }
    const auto initialize   = reinterpret_cast<InitializeFn>(GetProcAddress(module, "hostfxr_initialize_for_runtime_config"));
    const auto closeHost    = reinterpret_cast<CloseFn>(GetProcAddress(module, "hostfxr_close"));
    const auto setWriter    = reinterpret_cast<SetErrorWriterFn>(GetProcAddress(module, "hostfxr_set_error_writer"));
    if (!initialize || !closeHost)
    { result.error = L"hostfxr.dll is missing required entry points."; result.errorCode = ERROR_PROC_NOT_FOUND; FreeLibrary(module); return result; }

    ErrorWriterFn previous = setWriter ? setWriter(QuietCapture) : nullptr;   // replace plus record old
    auto& quiet = Quiet(); quiet.text.clear(); quiet.active = true;

    std::filesystem::path appFile = runtimeconfig.parent_path() / (runtimeconfig.stem().stem().wstring() + L".exe");
    const std::wstring hostPath = std::filesystem::is_regular_file(appFile, ec)
                                ? appFile.wstring() : (root / L"dotnet.exe").wstring();
    const std::wstring dotnetRoot = root.wstring();
    DotnetInitParams params{ sizeof(DotnetInitParams), hostPath.c_str(), dotnetRoot.c_str() };

    void* hostContext = nullptr;
    int status;
    {
        ScopedHostPolicy policy;
        status = initialize(runtimeconfig.c_str(), &params, &hostContext);
    }
    if (hostContext) closeHost(hostContext);        // before unloading module
    quiet.active = false;
    if (setWriter) setWriter(previous);             // restore writer (same thread)
    FreeLibrary(module);

    if (status == 0 || status == 1 || status == 2) { result.success = true; return result; }
    result.errorCode = status;
    result.error = DescribeDotnetStatus(status, quiet.text, root);
    if (!quiet.text.empty()) result.error += L"  " + quiet.text;   // meaningful cause
    return result;
}
