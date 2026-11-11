#include "CardPch.h"
#include "CardDraw.h"
#include <cwctype>

namespace Cards::Paint
{
    Microsoft::WRL::ComPtr<IDWriteTextLayout> PeripheralValueLayout(Graphics& graphics,
        const std::wstring& value, const std::wstring& family, float size, int weight,
        float width, float height, DWRITE_TEXT_ALIGNMENT alignment)
    {
        auto natural = graphics.TextLayout(value, family, size, weight, 10000, 10000,
            false, false, false, false, 1);
        DWRITE_TEXT_METRICS metrics{};
        if (!natural || FAILED(natural->GetMetrics(&metrics)) || width <= 1 || height <= 1) return {};
        const float fit = std::min((width - 1) / std::max(1.f, metrics.widthIncludingTrailingWhitespace),
            (height - 0.5f) / std::max(1.f, metrics.height));
        if (fit < 1) size *= fit * 0.98f;
        return graphics.TextLayout(value, family, size, weight, width, height,
            false, false, false, false, 1, alignment);
    }

    namespace
    {
        void DeviceName(ID2D1DeviceContext* context, Graphics& graphics, const PeripheralDevice& device,
            const CardEnvironment& environment, Color color, D2D1_RECT_F rect, float size, bool centered,
            std::vector<PeripheralName>* names)
        {
            const auto brand = PeripheralBrandFrom(device.name, device.source, device.brand);
            constexpr float logoSize = 16, gap = 5;
            // A single measured group keeps short names centered and long names ellipsized.
            // The name row, other readings and card dimensions remain unchanged.
            float left = rect.left;
            if (brand != PeripheralBrand::Unknown && rect.right - rect.left > logoSize + gap + size)
            {
                if (centered)
                {
                    const auto layout = graphics.TextLayout(device.name, environment.fontFamily, size, 600,
                        10000, rect.bottom - rect.top, false, false, false, false, 1);
                    DWRITE_TEXT_METRICS metrics{};
                    if (layout && SUCCEEDED(layout->GetMetrics(&metrics)))
                        left += std::max(0.f, (rect.right - rect.left - logoSize - gap - metrics.width) / 2);
                }
                if (DrawPeripheralBrand(context, brand, color, left,
                    rect.top + (rect.bottom - rect.top - logoSize) / 2, logoSize))
                {
                    rect.left = left + logoSize + gap;
                    centered = false;
                }
            }
            if (names && !environment.reducedMotion && !environment.highContrast)
            {
                const auto layout = graphics.TextLayout(device.name, environment.fontFamily, size, 600,
                    10000, rect.bottom - rect.top, false, false, false, false, 1);
                DWRITE_TEXT_METRICS metrics{};
                D2D1_MATRIX_3X2_F transform{};
                context->GetTransform(&transform);
                if (layout && SUCCEEDED(layout->GetMetrics(&metrics)) &&
                    (std::ceil(metrics.widthIncludingTrailingWhitespace) + 1) * transform._11 <= context->GetMaximumBitmapSize() &&
                    metrics.widthIncludingTrailingWhitespace > rect.right - rect.left + 0.5f)
                {
                    names->push_back({device.name, rect, size, std::ceil(metrics.widthIncludingTrailingWhitespace) + 1});
                    return;
                }
            }
            Draw::Text(context, graphics, device.name, environment.fontFamily, size, 600, color, rect,
                centered ? DWRITE_TEXT_ALIGNMENT_CENTER : DWRITE_TEXT_ALIGNMENT_LEADING);
        }

        std::wstring Role(const std::wstring& role)
        {
            if (role == L"left") return L"左";
            if (role == L"right") return L"右";
            if (role == L"case") return L"盒";
            if (role == L"main" || role == L"battery" || role == L"device") return L"";
            return role;
        }

        std::wstring Value(const PeripheralBatteryReading& battery)
        {
            std::wstring value = battery.percentage ? std::to_wstring(*battery.percentage) + L"%" :
                battery.levelText.empty() ? L"—" : battery.levelText;
            if (battery.estimated) value = L"≈" + value;
            return value;
        }

        const PeripheralBatteryReading* PrimaryBattery(const PeripheralDevice& device)
        {
            if (device.batteries.size() == 1) return &device.batteries.front();
            for (const auto& battery : device.batteries) if (battery.role == L"main") return &battery;
            return nullptr; // Independent roles never become an averaged/maximum device percentage.
        }

        std::wstring ReadingTime(const std::wstring& value, bool full)
        {
            // DateTimeOffset JSON: ISO 8601 with Z or an explicit offset, with
            // optional fractional seconds. Never substitute the refresh time.
            const auto number = [&](size_t at, size_t count)
            {
                int result = 0;
                if (at + count > value.size()) return -1;
                for (size_t i = at; i < at + count; ++i)
                {
                    if (value[i] < L'0' || value[i] > L'9') return -1;
                    result = result * 10 + value[i] - L'0';
                }
                return result;
            };
            if (value.size() < 20 || value[4] != L'-' || value[7] != L'-' ||
                value[10] != L'T' || value[13] != L':' || value[16] != L':') return L"—";
            const int year = number(0, 4), month = number(5, 2), day = number(8, 2);
            const int hour = number(11, 2), minute = number(14, 2), second = number(17, 2);
            if (year < 1601 || month < 1 || month > 12 || day < 1 || day > 31 ||
                hour < 0 || hour > 23 || minute < 0 || minute > 59 || second < 0 || second > 59) return L"—";
            if (!std::chrono::year_month_day{std::chrono::year{year}, std::chrono::month{static_cast<unsigned>(month)},
                std::chrono::day{static_cast<unsigned>(day)}}.ok()) return L"—";
            size_t zone = 19;
            if (value[zone] == L'.')
            {
                const size_t start = ++zone;
                while (zone < value.size() && value[zone] >= L'0' && value[zone] <= L'9') ++zone;
                if (zone == start) return L"—";
            }
            int offset = 0;
            if (zone + 1 == value.size() && value[zone] == L'Z') {}
            else if (zone + 6 == value.size() && (value[zone] == L'+' || value[zone] == L'-') && value[zone + 3] == L':')
            {
                const int hours = number(zone + 1, 2), minutes = number(zone + 4, 2);
                if (hours < 0 || hours > 14 || minutes < 0 || minutes > 59 || (hours == 14 && minutes)) return L"—";
                offset = (hours * 60 + minutes) * (value[zone] == L'+' ? 1 : -1);
            }
            else return L"—";
            SYSTEMTIME stamp{};
            stamp.wYear = static_cast<WORD>(year); stamp.wMonth = static_cast<WORD>(month); stamp.wDay = static_cast<WORD>(day);
            stamp.wHour = static_cast<WORD>(hour); stamp.wMinute = static_cast<WORD>(minute); stamp.wSecond = static_cast<WORD>(second);
            FILETIME file{};
            if (!SystemTimeToFileTime(&stamp, &file)) return L"—";
            ULARGE_INTEGER ticks{}; ticks.LowPart = file.dwLowDateTime; ticks.HighPart = file.dwHighDateTime;
            const LONGLONG utc = static_cast<LONGLONG>(ticks.QuadPart) - offset * 600000000LL;
            if (utc < 0) return L"—";
            ticks.QuadPart = static_cast<ULONGLONG>(utc); file = {ticks.LowPart, ticks.HighPart};
            SYSTEMTIME local{}, today{};
            if (!FileTimeToSystemTime(&file, &stamp) || !SystemTimeToTzSpecificLocalTime(nullptr, &stamp, &local)) return L"—";
            GetLocalTime(&today);
            wchar_t text[32]{};
            if (full)
                swprintf_s(text, L"%04u-%02u-%02u %02u:%02u:%02u", local.wYear, local.wMonth, local.wDay, local.wHour, local.wMinute, local.wSecond);
            else if (local.wYear != today.wYear || local.wMonth != today.wMonth || local.wDay != today.wDay)
                swprintf_s(text, L"%02u/%02u %02u:%02u", local.wMonth, local.wDay, local.wHour, local.wMinute);
            else swprintf_s(text, L"%02u:%02u:%02u", local.wHour, local.wMinute, local.wSecond);
            return text;
        }

        std::wstring Status(const PeripheralDevice& device, bool accessible = false)
        {
            auto connection = device.connection;
            std::transform(connection.begin(), connection.end(), connection.begin(),
                [](wchar_t c) { return static_cast<wchar_t>(std::towlower(c)); });
            const std::wstring label = connection == L"bluetooth" || connection == L"ble" ? L"蓝牙" :
                connection == L"usb" ? L"USB" : connection == L"usb-2.4g" || connection == L"wireless" ? L"2.4G" : L"未知连接";
            auto result = label + (accessible ? L"，数据读取时间 " : L" · ") + ReadingTime(device.updatedAt, accessible);
            if (accessible && device.state == L"disconnected") result += L"，已断开";
            if (accessible && device.state == L"sleeping") result += L"，休眠";
            if (accessible && device.stale) result += L"，保留上次读数";
            return result;
        }

        Color BatteryColor(const PeripheralBatteryReading& battery, const Draw::Palette& palette,
            const CardEnvironment& environment)
        {
            if (environment.highContrast || !battery.percentage) return palette.primary;
            if (*battery.percentage <= 10) return ParseColor(environment.dark ? L"#FF9090" : L"#B42318", palette.primary);
            if (*battery.percentage <= 20) return ParseColor(environment.dark ? L"#F5C56B" : L"#8A5700", palette.primary);
            return palette.accent;
        }

        void Ring(ID2D1DeviceContext* context, float x, float y, float radius,
            const PeripheralBatteryReading* battery, const Draw::Palette& palette, const CardEnvironment& environment)
        {
            const float ratio = battery && battery->percentage ? *battery->percentage / 100.f : -1;
            const auto color = battery ? BatteryColor(*battery, palette, environment) : palette.secondary;
            // A fixed vector path: no timer, spinning indicator or invented percentage for missing data.
            constexpr int segments = 64;
            for (int i = 0; i < segments; ++i)
            {
                const float a = -1.5707963f + i * 6.2831853f / segments;
                const float b = a + 6.2831853f / segments;
                Draw::Line(context, {x + std::cos(a) * radius, y + std::sin(a) * radius},
                    {x + std::cos(b) * radius, y + std::sin(b) * radius},
                    ratio >= (i + 1.f) / segments ? color : palette.track, 2);
            }
        }
    }

    void PeripheralBattery(ID2D1DeviceContext* context, const Snapshot& snapshot,
        const CardEnvironment& environment, Layout::Table& regions, float widthDip, float heightDip,
        const Interaction& interaction, int page, std::vector<PeripheralName>* names)
    {
        if (!context || !environment.graphics) return;
        auto& graphics = *environment.graphics;
        const auto palette = Draw::Colors(environment);
        const auto& state = snapshot.peripheralBattery;
        const bool detailed = state.mode == L"detailed";
        const auto selected = state.Selected();
        page = state.ClampPage(page);
        const auto text = [&](const std::wstring& value, float size, Color color, D2D1_RECT_F rect,
            int weight = 400, DWRITE_TEXT_ALIGNMENT align = DWRITE_TEXT_ALIGNMENT_LEADING)
        { Draw::Text(context, graphics, value, environment.fontFamily, size, weight, color, rect, align); };
        text(L"外设电量", 14, palette.primary, Draw::Rect(16, 17, 200, 38), 600);
        const auto button = [&](int id, D2D1_RECT_F rect, const std::wstring& action,
            const std::wstring& label, bool enabled = true)
        {
            Draw::ControlBackground(context, rect, 5, palette.fill, palette.accent, interaction, id, enabled);
            Draw::AddButton(regions, id, rect, action, label, enabled);
        };
        const auto mode = Draw::Rect(widthDip - 120, 12, widthDip - 56, 40);
        button(600, mode, detailed ? L"peripheralMode:compact" : L"peripheralMode:detailed",
            detailed ? L"切换为缩略模式" : L"切换为详细模式");
        text(detailed ? L"缩略" : L"详细", 12, palette.primary,
            Draw::Rect(mode.left, 18, mode.right, 36), 400, DWRITE_TEXT_ALIGNMENT_CENTER);
        const auto refresh = Draw::Rect(widthDip - 44, 12, widthDip - 16, 40);
        button(601, refresh, L"refreshPeripheralBatteries", state.refreshing ? L"正在刷新" : L"刷新", !state.refreshing);
        DrawGlyph(context, Glyph::Refresh, state.refreshing ? palette.secondary : palette.primary,
            refresh.left + 6, refresh.top + 6, 16);

        const int first = page * state.PageSize();
        for (int item = 0; item < state.PageSize() && first + item < static_cast<int>(selected.size()); ++item)
        {
            const auto& device = *selected[first + item];
            const float tileWidth = (widthDip - 48) / 3;
            const float x = detailed ? 16 : 16 + item * (tileWidth + 8);
            const float y = detailed ? 48 + item * 50.f : 46;
            const float right = detailed ? widthDip - 16 : x + tileWidth;
            const auto* primary = PrimaryBattery(device);
            const bool multiple = device.batteries.size() > 1;
            const int batteryCount = static_cast<int>(device.batteries.size());
            const int detailColumns = (batteryCount + 2) / 3;
            const float valuesLeft = right - std::min(208.f, 104.f * detailColumns);
            const float nameRight = multiple ? valuesLeft - 12 : 270;
            const auto readingText = [&](const PeripheralBatteryReading& battery, D2D1_RECT_F rect,
                float size, bool showRole, DWRITE_TEXT_ALIGNMENT align)
            {
                const auto color = BatteryColor(battery, palette, environment);
                const bool charging = battery.charging == true;
                const float boltSize = size > 16 ? 16.f : size <= 9 ? 8.f : 10.f;
                if (charging)
                {
                    DrawGlyph(context, Glyph::Lightning, color, rect.right - boltSize,
                        rect.top + (size > 16 ? 8 : 2), boltSize);
                    rect.right -= boltSize + 2;
                }
                const auto role = showRole ? Role(battery.role) : L"";
                const auto label = role.empty() ? Value(battery) : role + L" " + Value(battery);
                const auto layout = PeripheralValueLayout(graphics, label, environment.fontFamily, size,
                    size > 16 ? 600 : 400, rect.right - rect.left, rect.bottom - rect.top, align);
                const auto brush = Draw::Brush(context, color);
                if (layout && brush) context->DrawTextLayout({rect.left, rect.top}, layout.Get(), brush, D2D1_DRAW_TEXT_OPTIONS_CLIP);
                if (brush) brush->Release();
            };
            if (detailed)
            {
                DrawPeripheralIcon(context, device.kind, palette.primary, x, y + 8, 28);
                DeviceName(context, graphics, device, environment, palette.primary,
                    Draw::Rect(60, y + 1, nameRight, y + 20), 13, false, names);
                text(Status(device), 10, palette.secondary, Draw::Rect(60, y + 22, nameRight, y + 37));
                if (!multiple && primary)
                    readingText(*primary, Draw::Rect(278, y + 7, right, y + 40),
                        primary->percentage ? 24.f : 16.f, false, DWRITE_TEXT_ALIGNMENT_TRAILING);
                else if (!multiple)
                    text(L"—", 24, palette.secondary, Draw::Rect(278, y + 7, right, y + 40), 600, DWRITE_TEXT_ALIGNMENT_TRAILING);
                if (!multiple)
                    Draw::Progress(context, Draw::Rect(60, y + 46, 270, y + 48),
                        primary && primary->percentage ? *primary->percentage / 100.f : 0,
                        palette.track, primary ? BatteryColor(*primary, palette, environment) : palette.track, false);
            }
            else
            {
                Ring(context, x + tileWidth / 2, 70, 22, primary, palette, environment);
                DrawPeripheralIcon(context, device.kind, palette.primary, x + tileWidth / 2 - 12, 58, 24);
                DeviceName(context, graphics, device, environment, palette.primary, Draw::Rect(x + 2, Layout::PeripheralCompactNameTop,
                    right - 2, Layout::PeripheralCompactNameTop + Layout::PeripheralCompactNameHeight),
                    12, true, names);
                if (!multiple)
                {
                    if (primary) readingText(*primary, Draw::Rect(x + 16, Layout::PeripheralCompactDetailsTop,
                        right - 16, Layout::PeripheralCompactDetailsTop + 24),
                        16, false, DWRITE_TEXT_ALIGNMENT_CENTER);
                    else text(L"—", 16, palette.secondary, Draw::Rect(x, Layout::PeripheralCompactDetailsTop,
                        right, Layout::PeripheralCompactDetailsTop + 24),
                        400, DWRITE_TEXT_ALIGNMENT_CENTER);
                }
                text(Status(device), 9, palette.secondary, Draw::Rect(x + 2, Layout::PeripheralCompactStatusTop,
                    right - 2, Layout::PeripheralCompactStatusTop + Layout::PeripheralCompactStatusHeight),
                    400, DWRITE_TEXT_ALIGNMENT_CENTER);
            }

            std::wstring accessible = device.name + L", " + Status(device, true);
            const int count = static_cast<int>(device.batteries.size());
            for (int batteryIndex = 0; batteryIndex < count; ++batteryIndex)
            {
                const auto& battery = device.batteries[batteryIndex];
                accessible += L", " + Role(battery.role) + L" " + Value(battery);
                accessible += !battery.charging.has_value() ? L", 充电状态未知" :
                    *battery.charging ? L", 充电中" : L", 未充电";
                if (!multiple) continue;
                // Each role keeps its own measured value, including the percent sign.
                const int columns = detailed ? detailColumns : (count + 1) / 2;
                const float detailLeft = detailed ? valuesLeft : x;
                const float detailRight = right;
                const float cellWidth = (detailRight - detailLeft) / columns;
                const float top = detailed ? y + 1 + (batteryIndex / columns) * 15.f :
                    Layout::PeripheralCompactDetailsTop + (batteryIndex / columns) * Layout::PeripheralCompactDetailHeight;
                readingText(battery, Draw::Rect(detailLeft + (batteryIndex % columns) * cellWidth,
                    top, detailLeft + (batteryIndex % columns + 1) * cellWidth - 3,
                    top + (detailed ? 15 : Layout::PeripheralCompactDetailHeight)),
                    detailed ? 11.5f : 9.f, true, detailed ? DWRITE_TEXT_ALIGNMENT_TRAILING : DWRITE_TEXT_ALIGNMENT_CENTER);
            }
            Layout::Region reading;
            reading.id = 610 + item; reading.type = Layout::RegionType::Text;
            reading.bounds = {static_cast<LONG>(x), static_cast<LONG>(y), static_cast<LONG>(right),
                static_cast<LONG>(detailed ? y + 48 : Layout::PeripheralCompactStatusTop + Layout::PeripheralCompactStatusHeight)};
            reading.name = std::move(accessible); reading.action = L"peripheralReading"; reading.focusable = false;
            regions.Add(std::move(reading));
        }

        const float footer = heightDip - 24 - Layout::PeripheralFooterBottomGap;
        text(state.refreshing ? L"正在刷新…" : L"",
            10, palette.secondary, Draw::Rect(16, footer + 4, 220, footer + 22));
        button(602, Draw::Rect(widthDip - 156, footer, widthDip - 128, footer + 24),
            L"peripheralPage:previous", L"上一页", page > 0);
        button(603, Draw::Rect(widthDip - 44, footer, widthDip - 16, footer + 24),
            L"peripheralPage:next", L"下一页", page + 1 < state.PageCount());
        text(L"‹", 16, palette.secondary, Draw::Rect(widthDip - 156, footer, widthDip - 128, footer + 24), 400, DWRITE_TEXT_ALIGNMENT_CENTER);
        text(L"›", 16, palette.secondary, Draw::Rect(widthDip - 44, footer, widthDip - 16, footer + 24), 400, DWRITE_TEXT_ALIGNMENT_CENTER);
        text(std::to_wstring(page + 1) + L" / " + std::to_wstring(state.PageCount()), 11, palette.secondary,
            Draw::Rect(widthDip - 124, footer + 4, widthDip - 48, footer + 22), 400, DWRITE_TEXT_ALIGNMENT_CENTER);
    }
}
