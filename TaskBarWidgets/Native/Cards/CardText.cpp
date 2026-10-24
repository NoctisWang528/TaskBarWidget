#include "CardPch.h"
#include "CardText.h"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cwctype>

namespace Cards
{
    std::wstring Utf8ToWide(const std::string& text)
    {
        if (text.empty()) return {};
        const int length = MultiByteToWideChar(CP_UTF8, 0, text.data(),
            static_cast<int>(text.size()), nullptr, 0);
        if (length <= 0) return {};
        std::wstring result(static_cast<size_t>(length), L'\0');
        MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()),
            result.data(), length);
        return result;
    }

    std::string WideToUtf8(const std::wstring& text)
    {
        if (text.empty()) return {};
        const int length = WideCharToMultiByte(CP_UTF8, 0, text.data(),
            static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
        if (length <= 0) return {};
        std::string result(static_cast<size_t>(length), '\0');
        WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()),
            result.data(), length, nullptr, nullptr);
        return result;
    }

    namespace
    {
        bool ParseHex(const std::wstring& text, Color& color)
        {
            if (text.size() != 7 && text.size() != 9) return false;
            if (text[0] != L'#') return false;
            wchar_t* end{};
            const unsigned long value = std::wcstoul(text.c_str() + 1, &end, 16);
            if (!end || *end) return false;
            color.a = text.size() == 9 ? ((value >> 24) & 0xFF) / 255.0f : 1.0f;
            color.r = ((value >> 16) & 0xFF) / 255.0f;
            color.g = ((value >> 8) & 0xFF) / 255.0f;
            color.b = (value & 0xFF) / 255.0f;
            return true;
        }

        bool ParseFunction(const std::wstring& text, Color& color)
        {
            const auto open = text.find(L'(');
            const auto close = text.rfind(L')');
            if (open == std::wstring::npos || close == std::wstring::npos || close < open) return false;
            auto head = text.substr(0, open);
            std::transform(head.begin(), head.end(), head.begin(),
                [](wchar_t character) { return static_cast<wchar_t>(std::towlower(character)); });
            const bool rgba = head == L"rgba";
            if (!rgba && head != L"rgb") return false;
            std::wstring body = text.substr(open + 1, close - open - 1);
            double values[4]{ 0, 0, 0, 1 };
            int index = 0;
            size_t start = 0;
            while (index < 4 && start <= body.size())
            {
                const auto comma = body.find(L',', start);
                const auto piece = body.substr(start, comma == std::wstring::npos ? std::wstring::npos : comma - start);
                if (!piece.empty())
                {
                    const double value = std::wcstod(piece.c_str(), nullptr);
                    if (std::isfinite(value)) values[index] = value;
                }
                ++index;
                if (comma == std::wstring::npos) break;
                start = comma + 1;
            }
            if (!rgba && index < 3) return false;
            color.r = static_cast<float>(std::clamp(values[0] / 255.0, 0.0, 1.0));
            color.g = static_cast<float>(std::clamp(values[1] / 255.0, 0.0, 1.0));
            color.b = static_cast<float>(std::clamp(values[2] / 255.0, 0.0, 1.0));
            color.a = static_cast<float>(std::clamp(values[3], 0.0, 1.0));
            return true;
        }
    }

    Color ParseColor(const std::wstring& text, Color fallback)
    {
        std::wstring trimmed = text;
        while (!trimmed.empty() && std::iswspace(trimmed.front())) trimmed.erase(trimmed.begin());
        while (!trimmed.empty() && std::iswspace(trimmed.back())) trimmed.pop_back();
        if (trimmed.empty()) return fallback;
        Color color = fallback;
        if (ParseHex(trimmed, color)) return color;
        if (ParseFunction(trimmed, color)) return color;
        return fallback;
    }

    Color WithAlpha(Color color, float alpha)
    {
        color.a = std::clamp(alpha, 0.0f, 1.0f);
        return color;
    }

    Color Mix(Color from, Color to, float amount)
    {
        amount = std::clamp(amount, 0.0f, 1.0f);
        return Color{ from.r + (to.r - from.r) * amount, from.g + (to.g - from.g) * amount,
            from.b + (to.b - from.b) * amount, from.a + (to.a - from.a) * amount };
    }

    std::wstring FormatBytes(double totalKiB)
    {
        wchar_t buffer[64]{};
        if (totalKiB > 0 && totalKiB < 10.24) return L"<0.01 MB";
        if (totalKiB >= 1024.0 * 1024.0)
            swprintf_s(buffer, L"%.2f GB", totalKiB / (1024.0 * 1024.0));
        else
            swprintf_s(buffer, L"%.2f MB", totalKiB / 1024.0);
        std::wstring text = buffer;
        // Drop the trailing zeros the WinUI "0.##" format would not print.
        const auto dot = text.find(L'.');
        if (dot != std::wstring::npos)
        {
            while (!text.empty() && text.back() == L'0') text.pop_back();
            if (!text.empty() && text.back() == L'.') text.pop_back();
        }
        return text;
    }

    std::wstring FormatDateLabel(const std::wstring& date)
    {
        if (date.size() < 10) return date;
        return date.substr(5);
    }

    std::wstring FormatDuration(double seconds)
    {
        if (!std::isfinite(seconds) || seconds < 0) seconds = 0;
        seconds = std::min(seconds, 359999.0);
        const int total = static_cast<int>(seconds + 0.5);
        wchar_t buffer[32]{};
        if (total >= 3600)
            swprintf_s(buffer, L"%d:%02d:%02d", total / 3600, (total / 60) % 60, total % 60);
        else
            swprintf_s(buffer, L"%d:%02d", total / 60, total % 60);
        return buffer;
    }

    std::wstring FormatPercent(int value) { return std::to_wstring(std::clamp(value, 0, 100)); }

    std::wstring FormatTemperature(const std::wstring& value)
    {
        if (value.empty()) return L"--";
        return value;
    }

    const wchar_t* DefaultPrimaryColor(bool dark) { return dark ? L"#F2FFFFFF" : L"#F2141414"; }
    const wchar_t* DefaultSecondaryColor(bool dark) { return dark ? L"#A6FFFFFF" : L"#A6141414"; }
    const wchar_t* DefaultTranslationColor(bool dark) { return dark ? L"#B3FFFFFF" : L"#B3141414"; }
    const wchar_t* DefaultScanColor(bool dark) { return dark ? L"#FF8ED8FF" : L"#FF005FB8"; }
    const wchar_t* DefaultLyricsFont() { return L"Segoe UI Variable Text"; }
}
