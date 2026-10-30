#include "CardPch.h"
#include "CardDraw.h"

#include <cwctype>

namespace Cards::Paint
{
    namespace
    {
        constexpr float BodyHeight{ 176.0f };
        bool HasValue(const std::wstring& value) { return !value.empty() && value != L"--"; }
        const std::wstring& ShownCity(const WeatherState& weather)
        {
            return weather.stale && HasValue(weather.lastSuccessCity) ? weather.lastSuccessCity : weather.city;
        }
        bool HasWeather(const WeatherState& weather)
        {
            return HasValue(ShownCity(weather)) && (weather.hasData || HasValue(weather.temperature) ||
                HasValue(weather.weather) || !weather.forecast.empty() || !weather.alerts.empty() ||
                HasValue(weather.airQuality) || HasValue(weather.airCategory) ||
                HasValue(weather.pm25) || HasValue(weather.pm10));
        }
        bool Night(const std::wstring& time)
        {
            if (time.size() < 13 || time[11] < L'0' || time[11] > L'2' ||
                time[12] < L'0' || time[12] > L'9') return false;
            const int hour = (time[11] - L'0') * 10 + time[12] - L'0';
            return hour < 24 && (hour >= 20 || hour < 6);
        }
        bool TemperatureNumber(const std::wstring& value, float& result)
        {
            if (!HasValue(value)) return false;
            wchar_t* end = nullptr;
            const double parsed = std::wcstod(value.c_str(), &end);
            if (end == value.c_str() || !std::isfinite(parsed)) return false;
            while (*end == L' ') ++end;
            if (*end && std::wstring(end) != L"\u00b0" && std::wstring(end) != L"\u2103" &&
                std::wstring(end) != L"\u00b0C") return false;
            result = static_cast<float>(parsed);
            return std::isfinite(result);
        }
        Color TemperatureColor(float value)
        {
            const Color stops[] = { { 70/255.0f, 102/255.0f, 215/255.0f, 1 },
                { 78/255.0f, 129/255.0f, 221/255.0f, 1 }, { 78/255.0f, 168/255.0f, 222/255.0f, 1 },
                { 77/255.0f, 184/255.0f, 162/255.0f, 1 }, { 159/255.0f, 198/255.0f, 92/255.0f, 1 },
                { 243/255.0f, 184/255.0f, 75/255.0f, 1 }, { 232/255.0f, 102/255.0f, 79/255.0f, 1 } };
            const float index = std::clamp((value + 20.0f) / 10.0f, 0.0f, 6.0f);
            const int lower = static_cast<int>(index);
            return Mix(stops[lower], stops[std::min(6, lower + 1)], index - lower);
        }
        // Identical wrapping in measurement and paint prevents status/empty text clipping.
        float TextBlock(ID2D1DeviceContext* context, const CardEnvironment& environment,
            const std::wstring& text, float left, float top, float width, float size, int weight, Color color)
        {
            if (text.empty() || width <= 0.0f || !environment.graphics) return 0.0f;
            auto& graphics = *environment.graphics;
            const auto format = graphics.TextFormat(environment.fontFamily, size, weight,
                DWRITE_TEXT_ALIGNMENT_LEADING, true);
            if (!format || !graphics.DWrite()) return size * 1.5f;
            winrt::com_ptr<IDWriteTextLayout> layout;
            if (FAILED(graphics.DWrite()->CreateTextLayout(text.c_str(), static_cast<UINT32>(text.size()),
                format, width, 100000.0f, layout.put()))) return size * 1.5f;
            DWRITE_TEXT_METRICS metrics{};
            if (FAILED(layout->GetMetrics(&metrics))) return size * 1.5f;
            if (context)
            {
                const auto brush = graphics.Brush(context, color);
                if (brush) context->DrawTextLayout(D2D1::Point2F(left, top), layout.get(), brush.Get());
            }
            return std::ceil(metrics.height);
        }
        std::wstring Status(const WeatherState& weather)
        {
            std::vector<std::wstring> parts;
            const auto append = [&](const std::wstring& part)
            {
                if (HasValue(part) && std::find(parts.begin(), parts.end(), part) == parts.end())
                    parts.push_back(part);
            };
            if (weather.updating) append(L"正在更新");
            if (weather.stale) append(HasValue(weather.lastSuccessCity)
                ? L"当前显示上次成功数据（" + weather.lastSuccessCity + L"）" : L"当前显示上次成功数据");
            if (weather.state == L"error" || !HasWeather(weather)) append(weather.message);
            append(weather.alertsError);
            append(weather.airError);
            std::wstring text;
            for (const auto& part : parts) text += (text.empty() ? L"" : L" · ") + part;
            return text;
        }
        std::wstring EmptyText(const WeatherState& weather)
        {
            const auto status = Status(weather);
            if (!HasValue(ShownCity(weather)))
                return L"还没有设置天气城市。设置城市后，这里会显示当前天气和预报。" +
                    (status.empty() ? std::wstring{} : L" · " + status);
            return status.empty() ? L"当前天气来源还没有完整数据。可以稍后刷新，或在天气设置中切换来源。" : status;
        }
        std::wstring AirText(const WeatherState& weather)
        {
            std::wstring text;
            const auto append = [&](const std::wstring& part)
            {
                if (HasValue(part)) text += (text.empty() ? L"" : L" · ") + part;
            };
            if (HasValue(weather.airQuality))
            {
                const auto standard = HasValue(weather.airIndexName) ? weather.airIndexName :
                    HasValue(weather.airIndexCode) ? weather.airIndexCode : L"AQI（标准未提供）";
                append(standard + L" " + weather.airQuality);
            }
            append(weather.airCategory);
            if (!HasValue(weather.airQuality))
            {
                if (HasValue(weather.pm25)) append(L"PM2.5 " + weather.pm25 + L" " +
                    (HasValue(weather.pm25Unit) ? weather.pm25Unit : L"（单位未提供）"));
                if (HasValue(weather.pm10)) append(L"PM10 " + weather.pm10 + L" " +
                    (HasValue(weather.pm10Unit) ? weather.pm10Unit : L"（单位未提供）"));
            }
            return text.empty() ? text : L"空气质量 · " + text;
        }
        float Summary(ID2D1DeviceContext* context, const Snapshot& snapshot,
            const CardEnvironment& environment, float left, float top, float width)
        {
            const auto palette = Draw::Colors(environment);
            float height = TextBlock(context, environment, snapshot.weather.weather, left, top,
                width, 13.0f, 600, palette.primary);
            const auto air = AirText(snapshot.weather);
            if (!air.empty())
                height += TextBlock(context, environment, air, left, top + height + 2.0f, width, 11.0f, 400,
                    palette.secondary) + 2.0f;
            return height;
        }
        float Attribution(ID2D1DeviceContext* context, const Snapshot& snapshot,
            const CardEnvironment& environment, float left, float top, float width,
            Layout::Table* regions = nullptr, const Interaction& interaction = {})
        {
            std::vector<std::wstring> lines;
            bool qweatherLinks = false;
            for (const auto* source : { &snapshot.weather.alertsAttribution, &snapshot.weather.airAttribution })
            {
                size_t start = 0;
                while (start < source->size())
                {
                    const auto end = source->find(L'\n', start);
                    auto line = source->substr(start, end == std::wstring::npos ? end : end - start);
                    const auto first = std::find_if_not(line.begin(), line.end(), [](wchar_t c) { return std::iswspace(c); });
                    const auto last = std::find_if_not(line.rbegin(), line.rend(), [](wchar_t c) { return std::iswspace(c); }).base();
                    line = first < last ? std::wstring(first, last) : std::wstring{};
                    if (line == L"https://developer.qweather.com/attribution.html") qweatherLinks = true;
                    else if (HasValue(line) && std::find(lines.begin(), lines.end(), line) == lines.end())
                        lines.push_back(std::move(line));
                    if (end == std::wstring::npos) break;
                    start = end + 1;
                }
            }
            std::wstring text;
            for (const auto& line : lines) text += (text.empty() ? L"" : L"\n") + line;
            const auto palette = Draw::Colors(environment);
            float height = TextBlock(context, environment, text, left, top, width, 10.0f, 400, palette.secondary);
            if (qweatherLinks)
            {
                const float rowTop = top + height;
                if (context && environment.graphics)
                {
                    auto& graphics = *environment.graphics;
                    const auto ink = environment.highContrast ? palette.primary : palette.accent;
                    Draw::ControlBackground(context,
                        Draw::Rect(left, rowTop, left + 48.0f, rowTop + 20.0f), 3.0f,
                        WithAlpha(palette.accent, 0.0f), palette.accent, interaction, 402);
                    Draw::ControlBackground(context,
                        Draw::Rect(left + 60.0f, rowTop, left + 108.0f, rowTop + 20.0f), 3.0f,
                        WithAlpha(palette.accent, 0.0f), palette.accent, interaction, 403);
                    Draw::Text(context, graphics, L"和风天气", environment.fontFamily, 10.0f, 400,
                        ink, Draw::Rect(left, rowTop, left + 48.0f, rowTop + 20.0f),
                        DWRITE_TEXT_ALIGNMENT_LEADING, false, true);
                    Draw::Text(context, graphics, L"·", environment.fontFamily, 10.0f, 400,
                        palette.secondary, Draw::Rect(left + 48.0f, rowTop, left + 60.0f, rowTop + 20.0f));
                    Draw::Text(context, graphics, L"数据来源", environment.fontFamily, 10.0f, 400,
                        ink, Draw::Rect(left + 60.0f, rowTop, left + 108.0f, rowTop + 20.0f),
                        DWRITE_TEXT_ALIGNMENT_LEADING, false, true);
                    if (regions)
                    {
                        // Host maps these actions to fixed URLs; payload text cannot
                        // turn an attribution into an arbitrary external navigation.
                        Draw::AddButton(*regions, 402, Draw::Rect(left, rowTop, left + 48.0f, rowTop + 20.0f),
                            L"openWeatherProvider", L"和风天气");
                        Draw::AddButton(*regions, 403, Draw::Rect(left + 60.0f, rowTop, left + 108.0f, rowTop + 20.0f),
                            L"openWeatherAttribution", L"数据来源");
                    }
                }
                height += 20.0f;
            }
            return height;
        }
        float Footer(ID2D1DeviceContext* context, const Snapshot& snapshot, const CardEnvironment& environment,
            float left, float top, float width, Layout::Table* regions = nullptr,
            const Interaction& interaction = {})
        {
            const auto palette = Draw::Colors(environment);
            float height = TextBlock(context, environment, Status(snapshot.weather), left, top,
                width, 11.0f, 400, palette.secondary);
            const float attribution = Attribution(context, snapshot, environment, left, top + height + 4.0f,
                width, regions, interaction);
            if (attribution > 0.0f) height += attribution + 4.0f;
            return height;
        }
    }

    float WeatherHeight(const Snapshot& snapshot, const CardEnvironment& environment, float widthDip)
    {
        const float width = widthDip - Layout::Padding * 2.0f;
        if (!HasWeather(snapshot.weather))
            return Layout::Padding * 2.0f + std::max(BodyHeight,
                TextBlock(nullptr, environment, EmptyText(snapshot.weather), 0, 0, width, 12.0f, 400, Color{}) + 44.0f +
                Attribution(nullptr, snapshot, environment, 0, 0, width));
        const float summary = Summary(nullptr, snapshot, environment, 0, 0, width * 0.45f - 12.0f);
        const float body = std::max(BodyHeight, 90.0f + summary + (snapshot.weather.alerts.empty() ? 0.0f : 58.0f));
        return Layout::Padding * 2.0f + body + Footer(nullptr, snapshot, environment, 0, 0, width);
    }

    void Weather(ID2D1DeviceContext* context, const Snapshot& snapshot, const CardEnvironment& environment,
        Layout::Table& regions, float widthDip, float heightDip, const Interaction& interaction)
    {
        UNREFERENCED_PARAMETER(heightDip);
        if (!context || !environment.graphics) return;
        auto& graphics = *environment.graphics;
        const auto palette = Draw::Colors(environment);
        const auto& weather = snapshot.weather;
        const auto colors = WeatherColors(environment.dark);
        const float left = Layout::Padding, top = Layout::Padding;
        const float width = widthDip - Layout::Padding * 2.0f;
        const auto refresh = [&](float x, float y)
        {
            const auto rect = Draw::Rect(x, y, x + 28.0f, y + 28.0f);
            Draw::ControlBackground(context, rect, 8.0f, palette.fill, palette.accent,
                interaction, 400, !weather.updating);
            DrawGlyph(context, Glyph::Refresh, WithAlpha(palette.primary, weather.updating ? 0.4f : 0.9f),
                x + 6.0f, y + 6.0f, 16.0f);
            Draw::AddButton(regions, 400, rect, L"refreshWeather", L"刷新天气", !weather.updating);
        };
        if (!HasWeather(weather))
        {
            const float textHeight = TextBlock(context, environment, EmptyText(weather), left, top,
                width, 12.0f, 400, palette.secondary);
            refresh(left, top + textHeight + 8.0f);
            if (!HasValue(ShownCity(weather)))
            {
                const auto button = Draw::Rect(left + 36.0f, top + textHeight + 8.0f,
                    left + 168.0f, top + textHeight + 36.0f);
                Draw::ControlBackground(context, button, 8.0f, palette.fill, palette.accent,
                    interaction, 401);
                Draw::Text(context, graphics, L"打开天气设置", environment.fontFamily, 12.0f, 400,
                    palette.primary, Draw::Rect(button.left + 8.0f, button.top + 5.0f, button.right - 8.0f,
                        button.bottom), DWRITE_TEXT_ALIGNMENT_CENTER);
                Draw::AddButton(regions, 401, button, L"openWeatherSettings", L"打开天气设置");
            }
            Attribution(context, snapshot, environment, left, top + textHeight + 44.0f, width,
                &regions, interaction);
            return;
        }

        // Preserve the original 45/55 composition and five forecast rows.
        const float leftWidth = width * 0.45f - 12.0f;
        const float rightLeft = left + width * 0.45f + 12.0f;
        const float rightWidth = left + width - rightLeft;
        Draw::Text(context, graphics, ShownCity(weather), environment.fontFamily, 15.0f, 600,
            palette.primary, Draw::Rect(left, top + 3.0f, left + leftWidth - 34.0f, top + 27.0f));
        refresh(left + leftWidth - 28.0f, top);
        const float temperatureWidth = leftWidth - 48.0f;
        const auto temperature = FormatTemperature(weather.temperature);
        float heroSize = 48.0f;
        const auto fits = [&](float size)
        {
            const auto format = graphics.TextFormat(environment.fontFamily, size, 300,
                DWRITE_TEXT_ALIGNMENT_LEADING, false);
            if (!format || !graphics.DWrite()) return true;
            winrt::com_ptr<IDWriteTextLayout> layout;
            if (FAILED(graphics.DWrite()->CreateTextLayout(temperature.c_str(),
                static_cast<UINT32>(temperature.size()), format, temperatureWidth, 1000.0f, layout.put()))) return true;
            DWRITE_TEXT_METRICS metrics{};
            return FAILED(layout->GetMetrics(&metrics)) || metrics.widthIncludingTrailingWhitespace <= temperatureWidth;
        };
        while (heroSize > 18.0f && !fits(heroSize)) --heroSize;
        Draw::Text(context, graphics, temperature, environment.fontFamily, heroSize, 300, palette.primary,
            Draw::Rect(left, top + 28.0f + (58.0f - heroSize * 1.2f) / 2.0f,
                left + temperatureWidth, top + 86.0f));
        DrawWeatherSymbol(context, SymbolFromKind(weather.conditionKind), Night(weather.observedAt), colors,
            left + temperatureWidth + 6.0f, top + 36.0f, 42.0f);
        const float summary = Summary(context, snapshot, environment, left, top + 90.0f, leftWidth);
        const float body = std::max(BodyHeight, 90.0f + summary + (weather.alerts.empty() ? 0.0f : 58.0f));

        float minimum = 0.0f, maximum = 0.0f;
        bool hasRange = false;
        const size_t count = std::min<size_t>(5, weather.forecast.size());
        for (size_t index = 0; index < count; ++index)
            for (const auto* value : { &weather.forecast[index].min, &weather.forecast[index].max })
            {
                float parsed = 0.0f;
                if (!TemperatureNumber(*value, parsed)) continue;
                if (!hasRange) { minimum = maximum = parsed; hasRange = true; }
                minimum = std::min(minimum, parsed);
                maximum = std::max(maximum, parsed);
            }
        const float span = std::max(1.0f, maximum - minimum);
        static const wchar_t* labels[] = { L"今天", L"明天", L"后天", L"周四", L"周五" };
        for (size_t index = 0; index < count; ++index)
        {
            const auto& day = weather.forecast[index];
            const float rowTop = top + (body - count * 32.0f) / 2.0f + index * 32.0f;
            Draw::Text(context, graphics, labels[index], environment.fontFamily, 11.0f, 600,
                palette.primary, Draw::Rect(rightLeft, rowTop + 7.0f, rightLeft + 30.0f, rowTop + 27.0f));
            DrawWeatherSymbol(context, SymbolFromKind(day.dayKind), false, colors,
                rightLeft + 33.0f, rowTop + 2.0f, 28.0f);
            Draw::Text(context, graphics, FormatTemperature(day.min), environment.fontFamily, 11.0f, 300,
                palette.secondary, Draw::Rect(rightLeft + 64.0f, rowTop + 7.0f, rightLeft + 90.0f, rowTop + 27.0f));
            Draw::Text(context, graphics, FormatTemperature(day.max), environment.fontFamily, 11.0f, 600,
                palette.primary, Draw::Rect(rightLeft + rightWidth - 28.0f, rowTop + 7.0f,
                    rightLeft + rightWidth, rowTop + 27.0f), DWRITE_TEXT_ALIGNMENT_TRAILING);
            const float railLeft = rightLeft + 94.0f, railWidth = std::max(1.0f, rightWidth - 125.0f);
            const auto rail = Draw::Rect(railLeft, rowTop + 13.0f, railLeft + railWidth, rowTop + 19.0f);
            Draw::FillRound(context, rail, 3.0f, palette.track);
            float low = 0.0f, high = 0.0f;
            if (!TemperatureNumber(day.min, low) || !TemperatureNumber(day.max, high)) continue;
            if (high < low) std::swap(low, high);
            const float offset = std::clamp((low - minimum) / span, 0.0f, 1.0f) * railWidth;
            const float fillWidth = std::min(railWidth - offset, std::max(5.0f, (high - low) / span * railWidth));
            if (fillWidth <= 0.0f) continue;
            const auto fill = Draw::Rect(railLeft + offset, rail.top, railLeft + offset + fillWidth, rail.bottom);
            if (environment.highContrast) Draw::FillRound(context, fill, 3.0f, palette.accent);
            else
            {
                const Color cold = TemperatureColor(low), warm = TemperatureColor(high);
                const D2D1_GRADIENT_STOP stops[] = { { 0.0f, D2D1::ColorF(cold.r, cold.g, cold.b) },
                    { 1.0f, D2D1::ColorF(warm.r, warm.g, warm.b) } };
                winrt::com_ptr<ID2D1GradientStopCollection> collection;
                winrt::com_ptr<ID2D1LinearGradientBrush> brush;
                if (SUCCEEDED(context->CreateGradientStopCollection(stops, 2, collection.put())) &&
                    SUCCEEDED(context->CreateLinearGradientBrush(D2D1::LinearGradientBrushProperties(
                        D2D1::Point2F(fill.left, fill.top), D2D1::Point2F(fill.right, fill.top)),
                        collection.get(), brush.put())))
                    context->FillRoundedRectangle(D2D1::RoundedRect(fill, 3.0f, 3.0f), brush.get());
            }
        }
        if (!weather.alerts.empty())
        {
            const auto& alert = weather.alerts[(GetTickCount64() / 4000) % weather.alerts.size()];
            const float alertTop = top + body - 54.0f;
            const auto type = HasValue(alert.typeName) ? alert.typeName :
                HasValue(alert.type) ? alert.type : L"天气预警";
            const auto title = alert.level.empty() ? type : alert.level + L" · " + type;
            Draw::Text(context, graphics, title, environment.fontFamily, 11.0f, 600, palette.primary,
                Draw::Rect(left, alertTop, left + leftWidth, alertTop + 18.0f));
            context->PushAxisAlignedClip(Draw::Rect(left, alertTop + 18.0f, left + leftWidth,
                alertTop + 54.0f), D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
            const auto description = HasValue(alert.text) ? alert.text : HasValue(alert.title) ? alert.title : title;
            TextBlock(context, environment, description, left, alertTop + 18.0f, leftWidth, 10.0f, 400,
                palette.secondary);
            context->PopAxisAlignedClip();
        }
        Footer(context, snapshot, environment, left, top + body, width, &regions, interaction);
    }
}
