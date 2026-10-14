#pragma once
#include <cmath>
#include <string>
#include <cstdint>

namespace NativeSettingsValidation
{
    inline bool Integer(double value, int minimum, int maximum)
    { return std::isfinite(value) && value >= minimum && value <= maximum && std::floor(value) == value; }

    inline bool Color(const std::wstring& value)
    { return value.size() == 7 && value[0] == L'#' && value.find_first_not_of(L"0123456789abcdefABCDEF", 1) == std::wstring::npos; }

    inline bool Unexpired(double expiresAt, int64_t now)
    { return std::isfinite(expiresAt) && expiresAt > static_cast<double>(now); }
}
