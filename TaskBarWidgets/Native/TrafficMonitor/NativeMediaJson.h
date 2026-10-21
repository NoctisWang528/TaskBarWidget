#pragma once

#include "MediaFrameContract.h"

#include <winrt/Windows.Data.Json.h>
#include <winrt/Windows.Foundation.Collections.h>

#include <algorithm>
#include <cmath>
#include <cstdlib>
#include <cwctype>
#include <string>

namespace NativeMedia::Json
{
    inline std::wstring Text(const winrt::Windows::Data::Json::JsonObject& object,
        const wchar_t* key, const std::wstring& fallback = L"")
    {
        if (!object.HasKey(key)) return fallback;
        try { return object.GetNamedString(key).c_str(); }
        catch (...) { return fallback; }
    }

    inline double Number(const winrt::Windows::Data::Json::JsonObject& object,
        const wchar_t* key, double fallback = 0)
    {
        if (!object.HasKey(key)) return fallback;
        try
        {
            const double value = object.GetNamedNumber(key);
            return std::isfinite(value) ? value : fallback;
        }
        catch (...) { return fallback; }
    }

    inline bool Boolean(const winrt::Windows::Data::Json::JsonObject& object,
        const wchar_t* key, bool fallback = false)
    {
        if (!object.HasKey(key)) return fallback;
        try { return object.GetNamedBoolean(key); }
        catch (...) { return fallback; }
    }

    inline int NormalizeFontWeight(std::wstring value, int fallback = 500)
    {
        while (!value.empty() && std::iswspace(value.front())) value.erase(value.begin());
        while (!value.empty() && std::iswspace(value.back())) value.pop_back();
        std::transform(value.begin(), value.end(), value.begin(),
            [](wchar_t character) { return static_cast<wchar_t>(std::towlower(character)); });
        if (value == L"thin") return 100;
        if (value == L"extralight" || value == L"ultralight") return 200;
        if (value == L"light") return 300;
        if (value == L"normal" || value == L"regular") return 400;
        if (value == L"medium") return 500;
        if (value == L"semibold" || value == L"demibold") return 600;
        if (value == L"bold") return 700;
        if (value == L"extrabold" || value == L"ultrabold") return 800;
        if (value == L"black" || value == L"heavy") return 900;
        if (value == L"extrablack") return 950;
        wchar_t* end{};
        const long numeric = std::wcstol(value.c_str(), &end, 10);
        if (end && *end == L'\0')
            return std::clamp(static_cast<int>(numeric), 100, 950);
        return fallback;
    }

    inline int FontWeight(const winrt::Windows::Data::Json::JsonObject& object,
        const wchar_t* key, int fallback = 500)
    {
        if (!object.HasKey(key)) return fallback;
        try
        {
            const auto value = object.GetNamedValue(key);
            if (value.ValueType() == winrt::Windows::Data::Json::JsonValueType::String)
                return NormalizeFontWeight(value.GetString().c_str(), fallback);
            if (value.ValueType() == winrt::Windows::Data::Json::JsonValueType::Number)
                return std::clamp(static_cast<int>(std::lround(value.GetNumber())), 100, 950);
        }
        catch (...) {}
        return fallback;
    }

    inline Style ReadStyle(const winrt::Windows::Data::Json::JsonObject& object)
    {
        Style style;
        style.fontFamily = Text(object, L"fontFamily", style.fontFamily);
        style.layoutScalePercent = Number(object, L"layoutScalePercent", style.layoutScalePercent);
        style.fontSize = Number(object, L"fontSize", style.fontSize);
        style.showCover = Boolean(object, L"showCover", style.showCover);
        style.coverPosition = Text(object, L"coverPosition", style.coverPosition);
        style.enableControlPanel = Boolean(object, L"enableControlPanel", style.enableControlPanel);
        style.coverSize = Number(object, L"coverSize", style.coverSize);
        style.coverGap = Number(object, L"coverGap", style.coverGap);
        style.coverCornerRadius = Number(object, L"coverCornerRadius", style.coverCornerRadius);
        style.viewportDescenderBuffer = Number(object, L"viewportDescenderBuffer",
            style.viewportDescenderBuffer);
        style.layoutHorizontalPadding = Number(object, L"layoutHorizontalPadding",
            style.layoutHorizontalPadding);
        style.lyricsPaneTopPadding = Number(object, L"lyricsPaneTopPadding",
            style.lyricsPaneTopPadding);
        style.lyricsPaneRightPadding = Number(object, L"lyricsPaneRightPadding",
            style.lyricsPaneRightPadding);
        style.lyricsPaneLeftPadding = Number(object, L"lyricsPaneLeftPadding",
            style.lyricsPaneLeftPadding);
        style.primaryOffsetY = Number(object, L"primaryOffsetY", style.primaryOffsetY);
        style.secondaryOffsetY = Number(object, L"secondaryOffsetY", style.secondaryOffsetY);
        style.lineTextBottomPadding = Number(object, L"lineTextBottomPadding",
            style.lineTextBottomPadding);
        style.surfaceRadius = Number(object, L"surfaceRadius", style.surfaceRadius);
        style.layerTransitionOffset = Number(object, L"layerTransitionOffset",
            style.layerTransitionOffset);
        style.coverFallbackFontSize = Number(object, L"coverFallbackFontSize",
            style.coverFallbackFontSize);
        style.spectrumWidth = Number(object, L"spectrumWidth", style.spectrumWidth);
        style.spectrumHeight = Number(object, L"spectrumHeight", style.spectrumHeight);
        style.spectrumGap = Number(object, L"spectrumGap", style.spectrumGap);
        style.spectrumBarWidth = Number(object, L"spectrumBarWidth", style.spectrumBarWidth);
        style.spectrumBarHeight = Number(object, L"spectrumBarHeight", style.spectrumBarHeight);
        style.spectrumLowHeight = Number(object, L"spectrumLowHeight", style.spectrumLowHeight);
        style.spectrumHighHeight = Number(object, L"spectrumHighHeight", style.spectrumHighHeight);
        style.spectrumMiddleHeight = Number(object, L"spectrumMiddleHeight",
            style.spectrumMiddleHeight);
        style.fontWeight = FontWeight(object, L"fontWeight", style.fontWeight);
        style.primaryColor = Text(object, L"primaryColor", style.primaryColor);
        style.secondaryColor = Text(object, L"secondaryColor", style.secondaryColor);
        style.translationColor = Text(object, L"translationColor", style.translationColor);
        style.wordScanOverlayColor = Text(object, L"wordScanOverlayColor",
            style.wordScanOverlayColor);
        style.surfaceColor = Text(object, L"surfaceColor", style.surfaceColor);
        style.surfaceShadow = Text(object, L"surfaceShadow", style.surfaceShadow);
        style.textShadow = Text(object, L"textShadow", style.textShadow);
        style.textAlignment = Text(object, L"textAlignment", style.textAlignment);
        return style;
    }
}
