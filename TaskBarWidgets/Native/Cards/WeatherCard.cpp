#include "CardPch.h"
#include "CardDraw.h"

namespace Cards::Paint
{
    namespace
    {
        constexpr float HeroIcon{ 42.0f };
        constexpr float RowIcon{ 28.0f };

        bool Night(const std::wstring& observedAt)
        {
            if (observedAt.size() < 13) return false;
            const int hour = std::stoi(observedAt.substr(11, 2));
            return hour >= 19 || hour < 6;
        }

        float RangeBar(ID2D1DeviceContext* context, float left, float top, float width,
            const std::wstring& min, const std::wstring& max, const Draw::Palette& palette, bool dark)
        {
            // Five-day band drawn as one continuous rail with each day's own span.
            const float railTop = top + 6.0f;
            Draw::FillRound(context, Draw::Rect(left, railTop, left + width, railTop + 4.0f), 2.0f,
                palette.track);
            if (!min.empty() && !max.empty())
            {
                const int low = static_cast<int>(std::lround(std::wcstod(min.c_str(), nullptr)));
                const int high = static_cast<int>(std::lround(std::wcstod(max.c_str(), nullptr)));
                const float ratio = std::clamp((low + 20) / 60.0f, 0.0f, 1.0f);
                const float span = std::clamp((high - low) / 60.0f, 0.04f, 1.0f);
                Draw::FillRound(context, Draw::Rect(left + width * ratio, railTop,
                    left + width * std::min(1.0f, ratio + span), railTop + 4.0f), 2.0f,
                    dark ? ParseColor(L"#FF8ED8FF", Color{}) : ParseColor(L"#FF005FB8", Color{}));
            }
            return railTop + 12.0f;
        }
    }

    void Weather(ID2D1DeviceContext* context, const Snapshot& snapshot, const CardEnvironment& environment,
        Layout::Table& regions, float widthDip, float heightDip)
    {
        UNREFERENCED_PARAMETER(heightDip);
        if (!context || !environment.graphics) return;
        Graphics& graphics = *environment.graphics;
        const Draw::Palette palette = Draw::Colors(environment);
        const auto& weather = snapshot.weather;
        const WeatherPalette colors = WeatherColors(environment.dark);
        const float left = Layout::Padding;
        const float contentWidth = widthDip - Layout::Padding * 2.0f;
        const bool night = Night(weather.observedAt);

        // Hero block: symbol on the left, temperature + description on the right.
        const float heroTop = Layout::Padding;
        DrawWeatherSymbol(context, SymbolFromKind(weather.conditionKind), night, colors,
            left, heroTop, HeroIcon);
        Draw::AddButton(regions, 400, Draw::Rect(left, heroTop, left + HeroIcon, heroTop + HeroIcon),
            L"refreshWeather", L"刷新天气", false, false);

        // The hero temperature shrinks to the measured column width and always
        // keeps a gap from the icon, so it can never paint under the symbol.
        const float textLeft = left + HeroIcon + 12.0f;
        const float textWidth = contentWidth - HeroIcon - 12.0f;
        float heroSize = 44.0f;
        const std::wstring temperature = FormatTemperature(weather.temperature);
        {
            const auto measure = [&](float size)
            {
                const auto format = graphics.TextFormat(environment.fontFamily, size, 600,
                    DWRITE_TEXT_ALIGNMENT_LEADING, false);
                if (!format) return true;
                Microsoft::WRL::ComPtr<IDWriteTextLayout> layout;
                if (FAILED(graphics.DWrite()->CreateTextLayout((temperature + L"\u00b0").c_str(),
                    static_cast<UINT32>(temperature.size() + 1), format, textWidth, 1e6f,
                    layout.GetAddressOf())))
                    return true;
                DWRITE_TEXT_METRICS metrics{};
                if (FAILED(layout->GetMetrics(&metrics))) return true;
                return metrics.widthIncludingTrailingWhitespace <= textWidth;
            };
            while (heroSize > 18.0f && !measure(heroSize)) heroSize -= 1.0f;
        }
        Draw::Text(context, graphics, temperature + L"\u00b0", environment.fontFamily, heroSize, 600,
            palette.primary, Draw::Rect(textLeft, heroTop - 4.0f, textLeft + textWidth,
                heroTop + heroSize * 1.2f));
        Draw::Text(context, graphics, weather.weather, environment.fontFamily, 12.0f, 400,
            palette.secondary, Draw::Rect(textLeft, heroTop + heroSize * 1.2f - 6.0f,
                textLeft + textWidth, heroTop + heroSize * 1.2f + 10.0f));
        const std::wstring place = weather.city.empty() ? L"--" : weather.city +
            (weather.region.empty() ? L"" : L" \u00b7 " + weather.region);
        Draw::Text(context, graphics, place, environment.fontFamily, 10.0f, 400, palette.secondary,
            Draw::Rect(textLeft, heroTop + heroSize * 1.2f + 8.0f, textLeft + textWidth,
                heroTop + heroSize * 1.2f + 24.0f));

        float top = heroTop + 78.0f;
        if (!weather.forecast.empty())
        {
            top = RangeBar(context, left, top, contentWidth,
                weather.forecast.front().min, weather.forecast.front().max, palette, environment.dark);
            const float columnWidth = contentWidth / static_cast<float>(weather.forecast.size());
            for (size_t index = 0; index < weather.forecast.size(); ++index)
            {
                const auto& day = weather.forecast[index];
                const float columnLeft = left + columnWidth * static_cast<float>(index);
                DrawWeatherSymbol(context, SymbolFromKind(day.dayKind), false, colors,
                    columnLeft + columnWidth / 2.0f - RowIcon / 2.0f, top, RowIcon);
                const std::wstring range = day.min + L"\u00b0 / " + day.max + L"\u00b0";
                Draw::Text(context, graphics, range, environment.fontFamily, 9.0f, 400,
                    palette.secondary, Draw::Rect(columnLeft, top + RowIcon + 2.0f,
                        columnLeft + columnWidth, top + RowIcon + 16.0f), DWRITE_TEXT_ALIGNMENT_CENTER);
            }
            top += RowIcon + 22.0f;
        }

        if (!weather.alerts.empty())
        {
            const auto& alert = weather.alerts.front();
            Draw::FillRound(context, Draw::Rect(left, top, left + contentWidth, top + 50.0f), 6.0f,
                WithAlpha(palette.accent, 0.12f));
            Draw::Text(context, graphics, alert.title, environment.fontFamily, 10.0f, 600,
                palette.primary, Draw::Rect(left + 8.0f, top + 4.0f, left + contentWidth - 8.0f,
                    top + 20.0f));
            Draw::Text(context, graphics, alert.text, environment.fontFamily, 9.0f, 400,
                palette.secondary, Draw::Rect(left + 8.0f, top + 20.0f, left + contentWidth - 8.0f,
                    top + 46.0f));
            top += 54.0f;
        }

        if (!weather.message.empty())
        {
            Draw::Text(context, graphics, weather.message, environment.fontFamily, 9.0f, 400,
                palette.secondary, Draw::Rect(left, top, left + contentWidth, top + 16.0f));
        }
        else if (weather.updating)
        {
            Draw::Text(context, graphics, L"正在更新天气…", environment.fontFamily, 9.0f, 400,
                palette.secondary, Draw::Rect(left, top, left + contentWidth, top + 16.0f));
        }
    }
}
