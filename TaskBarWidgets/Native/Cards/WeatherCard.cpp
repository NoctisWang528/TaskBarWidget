#include "CardPch.h"
#include "CardDraw.h"

#include <cwctype>

namespace Cards::Paint
{
    namespace
    {
        constexpr size_t HourPageSize{ 6 }, DayPageSize{ 5 };
        bool HasValue(const std::wstring& value) { return !value.empty() && value != L"--"; }
        const std::wstring& ShownCity(const WeatherState& weather)
        {
            return weather.stale && HasValue(weather.lastSuccessCity) ? weather.lastSuccessCity : weather.city;
        }
        bool HasWeather(const WeatherState& weather)
        {
            return HasValue(ShownCity(weather)) && (weather.hasData || HasValue(weather.temperature) ||
                HasValue(weather.weather) || !weather.forecast.empty() || !weather.hourly.empty() || !weather.alerts.empty() ||
                HasValue(weather.airQuality) || HasValue(weather.airCategory) ||
                HasValue(weather.pm25) || HasValue(weather.pm10));
        }
        std::optional<std::chrono::year_month_day> Date(const std::wstring& text)
        {
            if (text.size() < 10 || text[4] != L'-' || text[7] != L'-' ||
                (text.size() > 10 && text[10] != L'T' && text[10] != L' ')) return {};
            for (size_t i : { 0, 1, 2, 3, 5, 6, 8, 9 })
                if (text[i] < L'0' || text[i] > L'9') return {};
            const int year = (text[0] - L'0') * 1000 + (text[1] - L'0') * 100 +
                (text[2] - L'0') * 10 + text[3] - L'0';
            const unsigned month = (text[5] - L'0') * 10 + text[6] - L'0';
            const unsigned day = (text[8] - L'0') * 10 + text[9] - L'0';
            const std::chrono::year_month_day date{ std::chrono::year{year},
                std::chrono::month{month}, std::chrono::day{day} };
            return year >= 1601 && date.ok() ? std::optional{date} : std::nullopt;
        }
        int Minutes(const std::wstring& text)
        {
            const size_t start = text.size() > 10 && Date(text) ? 11 : 0;
            if (text.size() < start + 5 || text[start + 2] != L':') return -1;
            for (size_t i : { start, start + 1, start + 3, start + 4 })
                if (text[i] < L'0' || text[i] > L'9') return -1;
            const int hour = (text[start] - L'0') * 10 + text[start + 1] - L'0';
            const int minute = (text[start + 3] - L'0') * 10 + text[start + 4] - L'0';
            size_t end = start + 5;
            if (text.size() > end && text[end] == L':')
            {
                if (text.size() < end + 3 || text[end + 1] < L'0' || text[end + 1] > L'5' ||
                    text[end + 2] < L'0' || text[end + 2] > L'9') return -1;
                end += 3;
            }
            if (text.size() > end && text.substr(end) != L"Z")
            {
                if (text.size() != end + 6 || (text[end] != L'+' && text[end] != L'-') ||
                    text[end + 3] != L':' || Minutes(text.substr(end + 1)) < 0) return -1;
            }
            return hour < 24 && minute < 60 ? hour * 60 + minute : -1;
        }
        std::wstring Clock(const std::wstring& text)
        {
            const int minutes = Minutes(text);
            if (minutes < 0) return L"--:--";
            wchar_t clock[6]{};
            swprintf_s(clock, L"%02d:%02d", minutes / 60, minutes % 60);
            return clock;
        }
        bool Night(const std::wstring& time)
        {
            const int minutes = Minutes(time);
            return minutes >= 0 && (minutes >= 20 * 60 || minutes < 6 * 60);
        }
        std::wstring DayLabel(const std::wstring& text, const std::wstring& observed)
        {
            const auto date = Date(text);
            if (!date) return HasValue(text) && text.size() <= 32 && text.find(L'-') == std::wstring::npos ? text : L"日期未知";
            if (date == Date(observed)) return L"今天";
            static const wchar_t* weekdays[] = { L"周日", L"周一", L"周二", L"周三", L"周四", L"周五", L"周六" };
            const auto weekday = std::chrono::weekday{std::chrono::sys_days{*date}}.c_encoding();
            return std::to_wstring(unsigned(date->month())) + L"/" +
                std::to_wstring(unsigned(date->day())) + L" " + weekdays[weekday];
        }
        size_t PageStart(int offset, size_t count, size_t pageSize)
        {
            if (!count) return 0;
            return std::min(static_cast<size_t>(std::max(0, offset)) / pageSize,
                (count - 1) / pageSize) * pageSize;
        }
        // Reject mismatched units rather than turning rainfall into probability.
        std::wstring Unit(const std::wstring& value, const std::wstring& unit, double maximum = 1e6)
        {
            if (!HasValue(value)) return L"--";
            wchar_t* end{};
            const double number = std::wcstod(value.c_str(), &end);
            if (end == value.c_str() || !std::isfinite(number) || number < 0 || number > maximum) return L"--";
            while (std::iswspace(*end)) ++end;
            auto suffix = std::wstring(end);
            while (!suffix.empty() && std::iswspace(suffix.back())) suffix.pop_back();
            if (!suffix.empty() && suffix != unit) return L"--";
            std::wstring numeric(value.c_str(), static_cast<size_t>(end - value.c_str()));
            while (!numeric.empty() && std::iswspace(numeric.back())) numeric.pop_back();
            return numeric + (unit.empty() || unit == L"%" ? L"" : L" ") + unit;
        }
        std::wstring Precipitation(const std::wstring& amount, const std::wstring& probability)
        {
            const auto mm = Unit(amount, L"mm"), percent = Unit(probability, L"%", 100);
            return mm == L"--" ? percent : percent == L"--" ? mm : mm + L" · " + percent;
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
        std::wstring Temperature(const std::wstring& value)
        {
            float number{};
            return TemperatureNumber(value, number) ? FormatTemperature(value) : L"--";
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
        void TextBlock(ID2D1DeviceContext* context, const CardEnvironment& environment,
            const std::wstring& text, float left, float top, float width, float size, int weight, Color color)
        {
            if (text.empty() || width <= 0.0f || !environment.graphics) return;
            auto& graphics = *environment.graphics;
            const auto format = graphics.TextFormat(environment.fontFamily, size, weight,
                DWRITE_TEXT_ALIGNMENT_LEADING, true);
            if (!format || !graphics.DWrite()) return;
            winrt::com_ptr<IDWriteTextLayout> layout;
            if (FAILED(graphics.DWrite()->CreateTextLayout(text.c_str(), static_cast<UINT32>(text.size()),
                format, width, 100000.0f, layout.put()))) return;
            if (context)
            {
                const auto brush = graphics.Brush(context, color);
                if (brush) context->DrawTextLayout(D2D1::Point2F(left, top), layout.get(), brush.Get());
            }
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
            append(weather.hourlyError);
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
        void Footer(ID2D1DeviceContext* context, const Snapshot& snapshot,
            const CardEnvironment& environment, float left, float top, float width,
            Layout::Table& regions, const Interaction& interaction)
        {
            auto& graphics = *environment.graphics;
            const auto palette = Draw::Colors(environment);
            const auto status = Status(snapshot.weather);
            Draw::Text(context, graphics, status, environment.fontFamily, 10, 400, palette.secondary,
                Draw::Rect(left, top, left + width, top + 14));
            if (!status.empty()) Draw::AddText(regions, 450,
                Draw::Rect(left, top, left + width, top + 14), status);
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
            for (const auto& line : lines) text += (text.empty() ? L"" : L" · ") + line;
            if (text.empty() && HasValue(snapshot.weather.provider)) text = snapshot.weather.provider;
            const float rowTop = top + 15;
            const float textRight = left + width - (qweatherLinks ? 120.0f : 0.0f);
            Draw::Text(context, graphics, text, environment.fontFamily, 9, 400, palette.secondary,
                Draw::Rect(left, rowTop, textRight, rowTop + 16));
            if (!text.empty()) Draw::AddText(regions, 451,
                Draw::Rect(left, rowTop, textRight, rowTop + 16), text);
            if (qweatherLinks)
            {
                for (int i = 0; i < 2; ++i)
                {
                    const auto rect = Draw::Rect(left + width - 112 + i * 60, rowTop,
                        left + width - 64 + i * 60, rowTop + 18);
                    Draw::ControlBackground(context, rect, 3, WithAlpha(palette.accent, 0), palette.accent,
                        interaction, 402 + i);
                    Draw::Text(context, graphics, i ? L"数据来源" : L"和风天气", environment.fontFamily, 10, 400,
                        environment.highContrast ? palette.primary : palette.accent, rect,
                        DWRITE_TEXT_ALIGNMENT_LEADING, false, true);
                    // Only fixed host actions; attribution text never becomes a URL.
                    Draw::AddButton(regions, 402 + i, rect, i ? L"openWeatherAttribution" : L"openWeatherProvider",
                        i ? L"数据来源" : L"和风天气");
                }
            }
        }
    }

    float WeatherHeight(const Snapshot&, const CardEnvironment&, float)
    {
        return Layout::HeightWeather;
    }

    void Weather(ID2D1DeviceContext* context, const Snapshot& snapshot, const CardEnvironment& environment,
        Layout::Table& regions, float widthDip, float heightDip, const Interaction& interaction)
    {
        if (!context || !environment.graphics) return;
        auto& graphics = *environment.graphics;
        const auto palette = Draw::Colors(environment);
        const auto& weather = snapshot.weather;
        const auto colors = WeatherColors(environment.dark);
        const float left = Layout::Padding, right = widthDip - Layout::Padding;
        const float width = right - left;
        const auto text = [&](const std::wstring& value, float x, float y, float w, float h,
            float size, int weight, Color ink, DWRITE_TEXT_ALIGNMENT align = DWRITE_TEXT_ALIGNMENT_LEADING)
        {
            Draw::Text(context, graphics, value, environment.fontFamily, size, weight, ink,
                Draw::Rect(x, y, x + w, y + h), align);
        };
        const auto button = [&](int id, D2D1_RECT_F rect, const std::wstring& action,
            const std::wstring& label, bool enabled, bool selected = false)
        {
            Draw::ControlBackground(context, rect, 6, selected ? palette.fill : WithAlpha(palette.fill, 0),
                palette.accent, interaction, id, enabled);
            Layout::Region region;
            region.id = id; region.bounds = RECT{ static_cast<LONG>(rect.left), static_cast<LONG>(rect.top),
                static_cast<LONG>(rect.right), static_cast<LONG>(rect.bottom) };
            region.action = action; region.name = label; region.enabled = enabled; region.selected = selected;
            regions.Add(std::move(region));
            if (selected)
                Draw::FillRound(context, Draw::Rect(rect.left + 12, rect.bottom - 3, rect.right - 12, rect.bottom - 1),
                    1, palette.accent);
        };
        const float tabsLeft = right - 130;
        const auto cityRect = Draw::Rect(left - 4, 16, tabsLeft - 8, 44);
        button(401, cityRect, L"openWeatherSettings", L"打开天气设置", true);
        text(HasValue(ShownCity(weather)) ? ShownCity(weather) : L"设置天气城市", left, 20,
            cityRect.right - left - 4, 24, 14, 600, palette.primary);
        button(404, Draw::Rect(tabsLeft, 16, tabsLeft + 44, 44), L"weatherView:today", L"小时", true, !weather.forecastView);
        button(405, Draw::Rect(tabsLeft + 44, 16, tabsLeft + 88, 44), L"weatherView:forecast", L"多日", true, weather.forecastView);
        text(L"小时", tabsLeft, 22, 44, 18, 11, 600, palette.primary, DWRITE_TEXT_ALIGNMENT_CENTER);
        text(L"多日", tabsLeft + 44, 22, 44, 18, 11, 600, palette.primary, DWRITE_TEXT_ALIGNMENT_CENTER);
        const auto refreshRect = Draw::Rect(right - 28, 16, right, 44);
        button(400, refreshRect, L"refreshWeather", L"刷新天气", !weather.updating);
        DrawGlyph(context, Glyph::Refresh, WithAlpha(palette.primary, weather.updating ? 0.4f : 0.9f),
            right - 22, 22, 16);

        Footer(context, snapshot, environment, left, heightDip - 48, width, regions, interaction);
        if (!HasWeather(weather))
        {
            const auto empty = EmptyText(weather);
            context->PushAxisAlignedClip(Draw::Rect(left, 70, right, heightDip - 58), D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
            TextBlock(context, environment, empty, left, 70, width, 12, 400, palette.secondary);
            context->PopAxisAlignedClip();
            Draw::AddText(regions, 452, Draw::Rect(left, 70, right, heightDip - 58), empty);
            return;
        }

        graphics.WeatherIcon(context, SymbolFromKind(weather.conditionKind), Night(weather.observedAt), environment.dark,
            left, 51, 56);
        const auto temperature = Temperature(weather.temperature);
        const float temperatureLeft = left + 68;
        const float temperatureWidth = std::min(142.0f, width * 0.43f);
        float heroSize = 44;
        for (; heroSize > 18; --heroSize)
        {
            const auto format = graphics.TextFormat(environment.fontFamily, heroSize, 300, DWRITE_TEXT_ALIGNMENT_LEADING, false);
            if (!format || !graphics.DWrite()) break;
            winrt::com_ptr<IDWriteTextLayout> layout;
            if (FAILED(graphics.DWrite()->CreateTextLayout(temperature.c_str(), static_cast<UINT32>(temperature.size()),
                format, temperatureWidth, 1000, layout.put()))) break;
            DWRITE_TEXT_METRICS metrics{};
            if (FAILED(layout->GetMetrics(&metrics)) || metrics.widthIncludingTrailingWhitespace <= temperatureWidth) break;
        }
        text(temperature, temperatureLeft, 48 + (54 - heroSize * 1.2f) / 2, temperatureWidth, 58,
            heroSize, 300, palette.primary);
        const float conditionLeft = temperatureLeft + temperatureWidth + 8;
        text(weather.weather, conditionLeft, 59, right - conditionLeft, 22, 13, 600, palette.primary);
        const auto feelsLike = HasValue(weather.feelsLike) ? L"体感 " + Temperature(weather.feelsLike) : L"体感 --";
        text(feelsLike, conditionLeft, 83, right - conditionLeft, 18, 10, 400, palette.secondary);
        Draw::AddText(regions, 410, Draw::Rect(left, 48, right, 108),
            temperature + L" · " + weather.weather + L" · " + feelsLike);

        struct Metric { std::wstring label, value, accessible; };
        std::wstring wind = HasValue(weather.windSpeed) ? Unit(weather.windSpeed, L"km/h") : L"--";
        if (wind == L"--" && HasValue(weather.windScale)) wind = weather.windScale + L"级";
        const auto air = AirText(weather);
        const auto airLabel = HasValue(weather.airQuality) ? L"AQI" : HasValue(weather.airCategory) ? L"空气质量" :
            HasValue(weather.pm25) ? L"PM2.5" : HasValue(weather.pm10) ? L"PM10" : L"空气质量";
        const auto aqi = HasValue(weather.airQuality) ? weather.airQuality +
            (HasValue(weather.airCategory) ? L" " + weather.airCategory : L"") :
            HasValue(weather.airCategory) ? weather.airCategory :
            HasValue(weather.pm25) ? weather.pm25 + (HasValue(weather.pm25Unit) ? L" " + weather.pm25Unit : L"（单位未提供）") :
            HasValue(weather.pm10) ? weather.pm10 + (HasValue(weather.pm10Unit) ? L" " + weather.pm10Unit : L"（单位未提供）") : L"--";
        std::vector<Metric> metrics{ { L"湿度", Unit(weather.humidity, L"%", 100), L"" },
            { HasValue(weather.wind) ? weather.wind : L"风力", wind, L"风力 " + weather.wind + L" " + wind },
            { airLabel, aqi, air } };
        const auto uv = Unit(weather.uvIndex, L"");
        if (uv != L"--") metrics.push_back({ L"紫外线", uv, L"" });
        const auto precip = Precipitation(weather.precipitation, weather.precipProbability);
        const auto mm = Unit(weather.precipitation, L"mm"), percent = Unit(weather.precipProbability, L"%", 100);
        if (precip != L"--") metrics.push_back({ mm != L"--" && percent != L"--" ? L"降水 " + percent : L"降水",
            mm != L"--" ? mm : percent, L"降水 " + precip });
        const float metricWidth = width / static_cast<float>(metrics.size());
        for (size_t i = 0; i < metrics.size(); ++i)
        {
            const float x = left + i * metricWidth;
            text(metrics[i].label, x, 115, metricWidth - 6, 14, 9, 400, palette.secondary);
            text(metrics[i].value, x, 130, metricWidth - 6, 18, 11, 600, palette.primary);
            Draw::AddText(regions, 411 + static_cast<int>(i), Draw::Rect(x, 114, x + metricWidth - 4, 149),
                metrics[i].accessible.empty() ? metrics[i].label + L" " + metrics[i].value : metrics[i].accessible);
        }
        Draw::Line(context, D2D1::Point2F(left, 153), D2D1::Point2F(right, 153), palette.border, 0.5f);

        const int sunrise = Minutes(weather.sunrise), sunset = Minutes(weather.sunset);
        if (sunrise >= 0 || sunset >= 0)
        {
            text(L"日出 " + Clock(weather.sunrise), left, 163, 75, 16, 10, 400, palette.secondary);
            text(L"日落 " + Clock(weather.sunset), right - 75, 163, 75, 16, 10, 400, palette.secondary,
                DWRITE_TEXT_ALIGNMENT_TRAILING);
            Draw::AddText(regions, 420, Draw::Rect(left, 157, right, 182),
                L"日出 " + Clock(weather.sunrise) + L" · 日落 " + Clock(weather.sunset));
            if (sunrise >= 0 && sunset > sunrise)
            {
                const float arcLeft = left + 82, arcWidth = width - 164;
                const auto point = [&](float t) { return D2D1::Point2F(arcLeft + arcWidth * t,
                    177 - std::sin(t * 3.14159265f) * 17); };
                const int observed = Minutes(weather.observedAt);
                const float progress = observed < 0 ? -1.0f : std::clamp(
                    static_cast<float>(observed - sunrise) / (sunset - sunrise), 0.0f, 1.0f);
                const auto gold = environment.highContrast ? palette.accent : colors.sun;
                for (int i = 0; i < 32; ++i)
                    Draw::Line(context, point(i / 32.0f), point((i + 1) / 32.0f),
                        progress >= (i + 1) / 32.0f ? gold : palette.track, 1.5f);
                // No invented current-sun position without an observation time.
                if (progress >= 0)
                {
                    const auto position = point(progress);
                    Draw::FillRound(context, Draw::Rect(position.x - 2.5f, position.y - 2.5f,
                        position.x + 2.5f, position.y + 2.5f), 2.5f, gold);
                }
            }
        }
        else text(L"日出 / 日落时间暂无数据", left, 163, width, 18, 10, 400, palette.secondary,
            DWRITE_TEXT_ALIGNMENT_CENTER);

        // The lower panel reserves the same geometry in both views, on every page.
        const size_t pageSize = weather.PageSize();
        const size_t count = weather.PageItemCount();
        const size_t start = PageStart(weather.forecastOffset, count, pageSize);
        const auto page = count ? std::to_wstring(start / pageSize + 1) + L" / " +
            std::to_wstring((count + pageSize - 1) / pageSize) : L"";
        text(weather.forecastView ? L"每日预报" : L"逐小时预报", left, 190, width - 130, 18, 11, 600, palette.primary);
        text(page, right - 130, 192, 62, 16, 10, 400, palette.secondary, DWRITE_TEXT_ALIGNMENT_TRAILING);
        for (int i = 0; i < 2; ++i)
        {
            const auto rect = Draw::Rect(right - 60 + i * 32, 186, right - 32 + i * 32, 210);
            const bool enabled = i ? start + pageSize < count : start > 0;
            button(406 + i, rect, i ? L"weatherPage:next" : L"weatherPage:previous", i ? L"下一页" : L"上一页", enabled);
            const float centerX = (rect.left + rect.right) / 2, centerY = (rect.top + rect.bottom) / 2;
            const float direction = i ? 1.0f : -1.0f;
            const auto ink = WithAlpha(palette.primary, enabled ? 0.9f : 0.3f);
            Draw::Line(context, D2D1::Point2F(centerX - direction * 2, centerY - 4),
                D2D1::Point2F(centerX + direction * 2, centerY), ink, 1.3f);
            Draw::Line(context, D2D1::Point2F(centerX + direction * 2, centerY),
                D2D1::Point2F(centerX - direction * 2, centerY + 4), ink, 1.3f);
        }
        if (!count)
        {
            const auto unavailable = weather.forecastView ? L"暂无每日预报" :
                HasValue(weather.hourlyError) ? weather.hourlyError : L"当前来源暂未提供逐小时预报";
            text(unavailable, left + 8, 251, width - 16, 40, 11, 400, palette.secondary, DWRITE_TEXT_ALIGNMENT_CENTER);
            Draw::AddText(regions, 430, Draw::Rect(left, 212, right, 322), unavailable);
        }
        else if (!weather.forecastView)
        {
            const float column = width / HourPageSize;
            for (size_t i = start; i < std::min(count, start + HourPageSize); ++i)
            {
                const auto& hour = weather.hourly[i];
                const float x = left + (i - start) * column;
                text(Clock(hour.time), x, 221, column, 18, 10, 400, palette.secondary, DWRITE_TEXT_ALIGNMENT_CENTER);
                graphics.WeatherIcon(context, SymbolFromKind(hour.conditionKind), Night(hour.time), environment.dark,
                    x + (column - 30) / 2, 243, 30);
                text(Temperature(hour.temperature), x, 278, column, 20, 13, 600, palette.primary,
                    DWRITE_TEXT_ALIGNMENT_CENTER);
                const auto rain = Precipitation(hour.precipitation, hour.precipProbability);
                const auto probability = Unit(hour.precipProbability, L"%", 100);
                text(probability == L"--" ? Unit(hour.precipitation, L"mm") : probability,
                    x, 301, column, 16, 9, 400, environment.highContrast ? palette.primary : palette.accent,
                    DWRITE_TEXT_ALIGNMENT_CENTER);
                Draw::AddText(regions, 430 + static_cast<int>(i - start), Draw::Rect(x, 214, x + column, 320),
                    Clock(hour.time) + L" · " + Temperature(hour.temperature) + L" · 降水 " + rain);
            }
        }
        else
        {
            float minimum{}, maximum{};
            bool hasRange = false;
            // One scale across all pages makes temperature bars comparable.
            for (const auto& day : weather.forecast)
            {
                float low{}, high{};
                if (!TemperatureNumber(day.min, low) || !TemperatureNumber(day.max, high)) continue;
                if (high < low) std::swap(low, high);
                if (!hasRange) { minimum = low; maximum = high; hasRange = true; }
                minimum = std::min(minimum, low); maximum = std::max(maximum, high);
            }
            const float span = std::max(1.0f, maximum - minimum);
            for (size_t i = start; i < std::min(count, start + DayPageSize); ++i)
            {
                const auto& day = weather.forecast[i];
                const float y = 212 + (i - start) * 22;
                const auto label = DayLabel(day.date, weather.observedAt);
                text(label, left, y + 4, 76, 18, 10, 400, palette.primary);
                graphics.WeatherIcon(context, SymbolFromKind(day.dayKind), false, environment.dark, left + 77, y, 23);
                text(Temperature(day.min), left + 106, y + 4, 34, 18, 11, 400, palette.secondary,
                    DWRITE_TEXT_ALIGNMENT_TRAILING);
                text(Temperature(day.max), right - 35, y + 4, 35, 18, 11, 600, palette.primary,
                    DWRITE_TEXT_ALIGNMENT_TRAILING);
                const float railLeft = left + 148, railRight = right - 43, railWidth = railRight - railLeft;
                const auto rail = Draw::Rect(railLeft, y + 11, railRight, y + 16);
                Draw::FillRound(context, rail, 2.5f, palette.track);
                Draw::AddText(regions, 440 + static_cast<int>(i - start), Draw::Rect(left, y, right, y + 22),
                    label + L" · " + day.weatherDay + L" · " + Temperature(day.min) + L" 至 " +
                    Temperature(day.max) + L" · 降水 " + Precipitation(day.precipitation, day.precipProbability));
                float low{}, high{};
                if (!TemperatureNumber(day.min, low) || !TemperatureNumber(day.max, high) || railWidth <= 0) continue;
                if (high < low) std::swap(low, high);
                const float offset = std::min(railWidth - 4, std::clamp((low - minimum) / span, 0.0f, 1.0f) * railWidth);
                const float fillWidth = std::min(railWidth - offset, std::max(4.0f, (high - low) / span * railWidth));
                const auto fill = Draw::Rect(railLeft + offset, rail.top, railLeft + offset + fillWidth, rail.bottom);
                if (environment.highContrast) Draw::FillRound(context, fill, 2.5f, palette.accent);
                else
                {
                    const Color cold = TemperatureColor(low), warm = TemperatureColor(high);
                    const D2D1_GRADIENT_STOP stops[] = { { 0, D2D1::ColorF(cold.r, cold.g, cold.b) },
                        { 1, D2D1::ColorF(warm.r, warm.g, warm.b) } };
                    winrt::com_ptr<ID2D1GradientStopCollection> collection;
                    winrt::com_ptr<ID2D1LinearGradientBrush> brush;
                    if (SUCCEEDED(context->CreateGradientStopCollection(stops, 2, collection.put())) &&
                        SUCCEEDED(context->CreateLinearGradientBrush(D2D1::LinearGradientBrushProperties(
                            D2D1::Point2F(fill.left, fill.top), D2D1::Point2F(fill.right, fill.top)), collection.get(), brush.put())))
                        context->FillRoundedRectangle(D2D1::RoundedRect(fill, 2.5f, 2.5f), brush.get());
                }
            }
        }
        if (!weather.alerts.empty())
        {
            const auto& alert = weather.alerts[(GetTickCount64() / 4000) % weather.alerts.size()];
            const auto type = HasValue(alert.typeName) ? alert.typeName : HasValue(alert.type) ? alert.type : L"天气预警";
            const auto title = HasValue(alert.level) ? alert.level + L" · " + type : type;
            const auto description = HasValue(alert.text) ? alert.text : HasValue(alert.title) ? alert.title : title;
            const auto warning = title + L" · " + description;
            text(warning, left, 326, width, 14, 10, 600, palette.primary);
            Draw::AddText(regions, 452, Draw::Rect(left, 325, right, 340), warning);
        }
    }
}
