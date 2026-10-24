#pragma once
// Encoding, colour and number helpers shared by the card painters. Kept free of
// D2D/DirectWrite so protocol code and unit fixtures can use them too.

#include <Windows.h>

#include <string>

namespace Cards
{
    struct Color
    {
        float r{ 0 }, g{ 0 }, b{ 0 }, a{ 1 };
    };

    std::wstring Utf8ToWide(const std::string& text);
    std::string WideToUtf8(const std::wstring& text);

    // Parses "#RRGGBB", "#AARRGGBB", "rgb(r, g, b)" and "rgba(r, g, b, a)".
    // Returns `fallback` for anything unparsable so a protocol hiccup can never
    // make a card invisible.
    Color ParseColor(const std::wstring& text, Color fallback);
    Color WithAlpha(Color color, float alpha);
    Color Mix(Color from, Color to, float amount);

    std::wstring FormatBytes(double gib);
    std::wstring FormatDateLabel(const std::wstring& date);
    std::wstring FormatDuration(double seconds);
    std::wstring FormatPercent(int value);
    std::wstring FormatTemperature(const std::wstring& value);

    // Baseline text used when a payload omits an optional string.
    const wchar_t* DefaultPrimaryColor(bool dark);
    const wchar_t* DefaultSecondaryColor(bool dark);
    const wchar_t* DefaultTranslationColor(bool dark);
    const wchar_t* DefaultScanColor(bool dark);
    const wchar_t* DefaultLyricsFont();
}
