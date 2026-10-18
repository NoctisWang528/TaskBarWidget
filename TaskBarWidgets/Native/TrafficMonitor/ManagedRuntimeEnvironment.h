#pragma once
#include <Windows.h>
#include <ShlObj.h>                 // SHGetKnownFolderPath, FOLDERID_ProgramFiles
#include <algorithm>
#include <filesystem>
#include <string>
#include <vector>
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "ole32.lib")

// Machine-wide x64 .NET root for shared-runtime deployment. Never reads PATH or user env.
inline std::filesystem::path SystemDotnetRoot()
{
    std::filesystem::path root;
    HKEY key{};
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\dotnet\\Setup\\InstalledVersions\\x64", 0,
                      KEY_READ | KEY_WOW64_64KEY, &key) == ERROR_SUCCESS)
    {
        wchar_t value[32768]{};
        DWORD size = sizeof(value);
        if (RegGetValueW(key, nullptr, L"InstallLocation", RRF_RT_REG_SZ, nullptr, value, &size) == ERROR_SUCCESS
            && std::filesystem::path(value).is_absolute()) root = value;
        RegCloseKey(key);
    }
    if (root.empty())
    {
        PWSTR path = nullptr;
        if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_ProgramFiles, KF_FLAG_DEFAULT, nullptr, &path)))
        {
            root = std::filesystem::path(path) / L"dotnet";
            CoTaskMemFree(path);
        }
    }
    return root;
}

// Child-only environment block: strips runtime-routing and roll-forward overrides so
// production and runtimecheck share one policy; preserves everything else unchanged.
inline std::vector<wchar_t> ManagedEnvironment(const std::filesystem::path& runtime)
{
    constexpr const wchar_t* kNames[] = {
        L"DOTNET_ROOT", L"DOTNET_ROOT_X64", L"DOTNET_MULTILEVEL_LOOKUP",
        L"DOTNET_ROLL_FORWARD", L"DOTNET_ROLL_FORWARD_TO_PRERELEASE", L"DOTNET_ROLL_FORWARD_ON_NO_CANDIDATE_FX"
    };
    const auto stripped = [&](const std::wstring& value)
    {
        for (auto* n : kNames)
        {
            const size_t len = wcslen(n);
            if (value.size() > len && _wcsnicmp(value.c_str(), n, len) == 0 && value[len] == L'=') return true;
        }
        return false;
    };
    std::vector<std::wstring> values;
    LPWCH inherited = GetEnvironmentStringsW();
    if (!inherited) return {};
    {
        for (const wchar_t* v = inherited; *v; v += wcslen(v) + 1)
            if (!stripped(v)) values.emplace_back(v);
        FreeEnvironmentStringsW(inherited);
    }
    const std::wstring r = runtime.wstring();
    values.push_back(L"DOTNET_ROOT=" + r);
    values.push_back(L"DOTNET_ROOT_X64=" + r);
    values.push_back(L"DOTNET_MULTILEVEL_LOOKUP=0");
    std::sort(values.begin(), values.end(),
              [](const auto& a, const auto& b) { return _wcsicmp(a.c_str(), b.c_str()) < 0; });
    std::vector<wchar_t> block;
    for (const auto& v : values) { block.insert(block.end(), v.begin(), v.end()); block.push_back(0); }
    block.push_back(0);
    return block;
}
