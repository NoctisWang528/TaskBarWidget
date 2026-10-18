#pragma once
#include <cstdint>
#include <cwctype>
#include <string>
#include <windows.h>
// Shared with native startup. Collision only denies concurrent startup; it grants no access.
inline std::wstring TaskBarWidgetsUpdateMutexName(const std::wstring &installationRoot)
{
    wchar_t full[32768]{};
    DWORD count = GetFullPathNameW(installationRoot.c_str(), 32768, full, nullptr);
    if (!count || count >= 32768)
        return {};
    std::wstring path(full, count);
    while (path.size() > 3 && (path.back() == L'\\' || path.back() == L'/'))
        path.pop_back();
    uint64_t hash = 14695981039346656037ull;
    for (wchar_t c : path)
    {
        c = static_cast<wchar_t>(towlower(c == L'/' ? L'\\' : c));
        hash ^= static_cast<uint16_t>(c);
        hash *= 1099511628211ull;
    }
    wchar_t suffix[17]{};
    swprintf_s(suffix, L"%016llx", static_cast<unsigned long long>(hash));
    return L"Local\\TaskBarWidgets.Update." + std::wstring(suffix);
}
