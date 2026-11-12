#pragma once
// Weather/control artwork with system-font audio/settings icons and vector
// fallbacks. No fonts or artwork are packaged for these symbols.
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
        Lightning,
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

    // Visually centers the symbol in the `size` DIP square at (left, top).
    void DrawWeatherSymbol(ID2D1DeviceContext* context, Symbol symbol, bool night,
        const WeatherPalette& palette, float left, float top, float size);

    // Draws a control glyph into `size` DIP at (left, top). Audio/settings prefer
    // installed Segoe Fluent Icons, then Segoe MDL2 Assets, then vector artwork.
    void DrawGlyph(ID2D1DeviceContext* context, Glyph glyph, Color color, float left, float top, float size);
    void DrawPeripheralIcon(ID2D1DeviceContext* context, const std::wstring& kind,
        Color color, float left, float top, float size);

    enum class PeripheralBrand { Unknown, Rog, Razer, Logitech, Xiaomi, Apple };
    PeripheralBrand PeripheralBrandFrom(const std::wstring& name, const std::wstring& source,
        const std::wstring& brand = {});
    // Source-backed Simple Icons artwork; false leaves the full name width available.
    bool DrawPeripheralBrand(ID2D1DeviceContext* context, PeripheralBrand brand,
        Color color, float left, float top, float size);
    // Called on the rendering thread by the shared graphics teardown, before
    // releasing its device. Also clears the thread-local system glyph/font cache.
    // Safe when unused or already cleared.
    void ClearPeripheralBrandCache();
}
