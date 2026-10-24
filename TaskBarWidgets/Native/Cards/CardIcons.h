#pragma once
// Hand-authored vector artwork, ported 1:1 from the WinUI cards so the native
// front-end shows the same symbols and control glyphs with no packaged assets.
//
// Weather symbols live in a 128x128 DIP design space; control glyphs in a 20x20
// space that is scaled into a 16 DIP box. Nothing here animates: state is only
// the day/night variant and the light/dark palette.

#include "CardText.h"

#include <Windows.h>
#include <d2d1_3.h>
#include <wrl/client.h>

#include <string>

namespace Cards
{
    enum class Symbol
    {
        Clear,
        PartlyCloudy,
        Overcast,
        Rain,
        Drizzle,
        Snow,
        Sleet,
        Fog,
        Haze,
        Dust,
        Thunder,
        Wind,
        Unknown,
    };

    enum class Glyph
    {
        Previous,
        Play,
        Pause,
        Next,
        Speaker,
        SpeakerMuted,
        Microphone,
        MicrophoneMuted,
        Settings,
        Refresh,
        Check,
        Chevron,
    };

    struct WeatherPalette
    {
        Color sun;
        Color cloud;
        Color cloudShade;
        Color rain;
        Color snow;
        Color bolt;
        Color fog;
        Color dust;
        Color moon;
        Color star;
    };

    WeatherPalette WeatherColors(bool dark);

    // Kind string from the weather payload -> symbol. Unknown input maps to
    // Cloud exactly like the WinUI implementation.
    Symbol SymbolFromKind(const std::wstring& kind);

    // Draws the symbol into `size` DIP at (left, top).
    void DrawWeatherSymbol(ID2D1DeviceContext* context, Symbol symbol, bool night,
        const WeatherPalette& palette, float left, float top, float size);

    // Draws a control glyph into `size` DIP at (left, top).
    void DrawGlyph(ID2D1DeviceContext* context, Glyph glyph, Color color, float left, float top, float size);
}
