#include "../TrafficMonitor/NativeMediaJson.h"

#include <winrt/Windows.Data.Json.h>
#include <winrt/Windows.Foundation.h>

#include <iostream>
#include <stdexcept>
#include <string>

using namespace winrt::Windows::Data::Json;

namespace
{
    void Require(bool condition, const char* message)
    {
        if (!condition) throw std::runtime_error(message);
    }

    int Weight(const wchar_t* json)
    {
        return NativeMedia::Json::ReadStyle(JsonObject::Parse(json)).fontWeight;
    }
}

int main()
{
    winrt::init_apartment(winrt::apartment_type::single_threaded);
    try
    {
        const auto fixture = JsonObject::Parse(LR"json({
            "fontFamily":"Microsoft YaHei UI",
            "layoutScalePercent":100,
            "fontSize":13,
            "showCover":true,
            "coverPosition":"Left",
            "enableControlPanel":true,
            "coverSize":34,
            "coverGap":8,
            "coverCornerRadius":6,
            "viewportDescenderBuffer":2,
            "layoutHorizontalPadding":0,
            "lyricsPaneTopPadding":3,
            "lyricsPaneRightPadding":4,
            "lyricsPaneLeftPadding":2,
            "primaryOffsetY":1,
            "secondaryOffsetY":1,
            "lineTextBottomPadding":2,
            "surfaceRadius":8,
            "layerTransitionOffset":2,
            "coverFallbackFontSize":13,
            "spectrumWidth":210,
            "spectrumHeight":22,
            "spectrumGap":3,
            "spectrumBarWidth":3,
            "spectrumBarHeight":8,
            "spectrumLowHeight":6,
            "spectrumHighHeight":18,
            "spectrumMiddleHeight":11,
            "fontWeight":"Medium",
            "primaryColor":"rgba(255, 255, 255, 0.9)",
            "secondaryColor":"rgba(255, 255, 255, 0.6)",
            "translationColor":"rgba(255, 255, 255, 0.7)",
            "wordScanOverlayColor":"rgba(255, 255, 255, 0.75)",
            "surfaceColor":"rgba(18, 18, 24, 0.8)",
            "surfaceShadow":"inset 0 0 0 1px rgba(255, 255, 255, 0.16)",
            "textShadow":"0 1px 2px rgba(0, 0, 0, 0.36)",
            "textAlignment":"Left"
        })json");
        const auto style = NativeMedia::Json::ReadStyle(fixture);
        Require(style.fontWeight == 500, "Serialized Medium font weight maps to 500.");
        Require(style.fontFamily == L"Microsoft YaHei UI", "Style font family survives parsing.");
        Require(style.spectrumBarHeight == 8, "Style numeric fields survive parsing.");

        Require(Weight(LR"({"fontWeight":"Light"})") == 300, "Light maps to 300.");
        Require(Weight(LR"({"fontWeight":"Normal"})") == 400, "Normal maps to 400.");
        Require(Weight(LR"({"fontWeight":"SemiBold"})") == 600, "SemiBold maps to 600.");
        Require(Weight(LR"({"fontWeight":"Bold"})") == 700, "Bold maps to 700.");
        Require(Weight(LR"({"fontWeight":"ExtraBold"})") == 800, "ExtraBold maps to 800.");
        Require(Weight(LR"({"fontWeight":"unknown"})") == 500,
            "Unknown font weight uses the style default.");
        Require(Weight(LR"({"fontWeight":650})") == 650,
            "Numeric font weight remains compatible.");

        std::cout << "Native media serialized style fixture assertions passed.\n";
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << error.what() << "\n";
        return 1;
    }
}
