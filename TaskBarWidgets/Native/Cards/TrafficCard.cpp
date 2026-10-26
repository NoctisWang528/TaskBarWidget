#include "CardPch.h"
#include "CardDraw.h"

namespace Cards::Paint
{
    namespace
    {
        constexpr float CellSize{ 21.0f };
        constexpr float SlotSize{ 25.0f };
        constexpr float WeekdayGutter{ 20.0f };
        constexpr float MonthsHeight{ 12.0f };
        constexpr int Weeks{ 12 };

        Color RampColor(int level, bool dark)
        {
            static const wchar_t* light[] = { L"#00FFFFFF", L"#E4E9EF", L"#D3E6FB", L"#96C3EF",
                L"#4E97DC", L"#2365AA" };
            static const wchar_t* night[] = { L"#00202B37", L"#343E4B", L"#284764", L"#326B9C",
                L"#55A1D5", L"#B4E6FF" };
            if (level < 0) return Color{ 0, 0, 0, 0 };
            level = std::clamp(level, 0, 5);
            return ParseColor(dark ? night[level] : light[level], Color{});
        }

        Color HighContrastLevel(int level)
        {
            const COLORREF text = GetSysColor(COLOR_WINDOWTEXT);
            const COLORREF highlight = GetSysColor(COLOR_HIGHLIGHT);
            if (level <= 0) return Color{ 0, 0, 0, 0 };
            if (level == 1)
                return Color{ GetRValue(text) / 255.0f, GetGValue(text) / 255.0f,
                    GetBValue(text) / 255.0f, 0.18f };
            const float ratio = (level - 1) / 4.0f;
            return Color{ GetRValue(highlight) / 255.0f, GetGValue(highlight) / 255.0f,
                GetBValue(highlight) / 255.0f, 0.25f + ratio * 0.75f };
        }

        std::wstring MonthLabel(const TrafficDay& day)
        {
            if (day.date.size() < 10) return {};
            if (day.date[5] < L'0' || day.date[5] > L'1' ||
                day.date[6] < L'0' || day.date[6] > L'9') return {};
            const int month = (day.date[5] - L'0') * 10 + day.date[6] - L'0';
            if (month < 1 || month > 12) return {};
            return std::to_wstring(month) + L"\u6708";
        }
    }

    void Traffic(ID2D1DeviceContext* context, const Snapshot& snapshot, const CardEnvironment& environment,
        Layout::Table& regions, float widthDip, float heightDip, const Interaction& interaction)
    {
        UNREFERENCED_PARAMETER(heightDip);
        if (!context || !environment.graphics) return;
        Graphics& graphics = *environment.graphics;
        const Draw::Palette palette = Draw::Colors(environment);
        const float left = Layout::Padding;
        const float contentWidth = widthDip - Layout::Padding * 2.0f;
        const auto& traffic = snapshot.traffic;

        Draw::Text(context, graphics, L"流量消耗", environment.fontFamily, 13.0f, 600, palette.primary,
            Draw::Rect(left, Layout::Padding, left + 200.0f, Layout::Padding + 20.0f));
        Draw::Text(context, graphics, L"近 12 周", environment.fontFamily, 10.0f, 400,
            palette.secondary, Draw::Rect(left + 84.0f, Layout::Padding + 3.0f, left + 200.0f,
                Layout::Padding + 20.0f));
        Draw::Text(context, graphics, traffic.available ? FormatBytes(traffic.totalKiB) : L"—", environment.fontFamily, 22.0f, 600,
            palette.primary, Draw::Rect(left + contentWidth - 160.0f, Layout::Padding - 2.0f,
                left + contentWidth, Layout::Padding + 26.0f), DWRITE_TEXT_ALIGNMENT_TRAILING);
        const std::wstring scope = traffic.available
            ? (traffic.scopeLabel.empty() ? L"上传 + 下载" : traffic.scopeLabel)
            : L"正在读取流量历史…";
        Draw::Text(context, graphics, scope, environment.fontFamily, 9.0f, 400, palette.secondary,
            Draw::Rect(left, Layout::Padding + 26.0f, left + contentWidth, Layout::Padding + 40.0f));

        const float gridWidth = WeekdayGutter + Weeks * SlotSize;
        const float gridLeft = left + (contentWidth - gridWidth) / 2.0f;
        const float monthsTop = Layout::Padding + 44.0f;
        const float gridTop = monthsTop + MonthsHeight + 3.0f;

        // The first native snapshot can precede the history reply. Keep the
        // calendar visible while it loads; these are placeholders, not zero usage.
        if (traffic.days.empty())
        {
            for (int week = 0; week < Weeks; ++week)
            for (int row = 0; row < 7; ++row)
            {
                const float x = gridLeft + WeekdayGutter + week * SlotSize + (SlotSize - CellSize) / 2;
                const float y = gridTop + row * SlotSize + (SlotSize - CellSize) / 2;
                Draw::StrokeRound(context, Draw::Rect(x, y, x + CellSize, y + CellSize),
                    4.0f, WithAlpha(palette.secondary, 0.20f), 1.0f);
            }
        }

        for (int week = 0; week < Weeks; ++week)
        {
            const size_t index = static_cast<size_t>(week) * 7;
            if (index >= traffic.days.size()) break;
            if (week > 0 && MonthLabel(traffic.days[index]) == MonthLabel(traffic.days[index - 7]))
                continue;
            Draw::Text(context, graphics, MonthLabel(traffic.days[index]), environment.fontFamily, 9.0f,
                400, palette.secondary, Draw::Rect(gridLeft + WeekdayGutter + week * SlotSize,
                    monthsTop, gridLeft + WeekdayGutter + (week + 1) * SlotSize, monthsTop + MonthsHeight));
        }

        static const wchar_t* weekdayNames[] = { L"\u4e00", L"", L"\u4e09", L"", L"\u4e94", L"",
            L"\u65e5" };
        for (int row = 0; row < 7; ++row)
        {
            if (!weekdayNames[row][0]) continue;
            Draw::Text(context, graphics, weekdayNames[row], environment.fontFamily, 9.0f, 400,
                palette.secondary, Draw::Rect(gridLeft, gridTop + row * SlotSize,
                    gridLeft + WeekdayGutter, gridTop + (row + 1) * SlotSize));
        }

        // Draw the restrained halo under the opaque cells so it cannot wash out
        // the heatmap levels. Keep one shared mask and blur pass; never expand
        // a cell into its four-DIP gutter before blurring.
        if (!environment.highContrast && graphics.BeginMask(context))
        {
            const Color halo = ParseColor(environment.dark ? L"#6969BDFA" : L"#345281D3", Color{});
            for (const auto& day : traffic.days)
            {
                if (day.isFuture || day.week < 0 || day.week >= Weeks ||
                    day.weekday < 0 || day.weekday >= 7) continue;
                const int id = 300 + day.week * 7 + day.weekday;
                const float hover = interaction.Hover(id);
                if (day.level < 2 && hover <= 0 && interaction.focused != id) continue;
                const float cellLeft = gridLeft + WeekdayGutter + day.week * SlotSize +
                    (SlotSize - CellSize) / 2.0f;
                const float cellTop = gridTop + day.weekday * SlotSize + (SlotSize - CellSize) / 2.0f;
                const bool selected = day.date == (traffic.selectedDate.empty() ? traffic.today : traffic.selectedDate);
                const bool emphasized = day.isToday || selected || interaction.focused == id;
                const float inset = emphasized ? 0.0f : 1.0f - hover;
                Draw::FillRound(context, Draw::Rect(cellLeft + inset, cellTop + inset,
                    cellLeft + CellSize - inset, cellTop + CellSize - inset), 4.0f - inset,
                    WithAlpha(halo, halo.a * ((emphasized ? 0.75f : 0.45f) + hover * 0.65f)));
            }
            graphics.EndMask(context, 4.0f);
        }

        for (const auto& day : traffic.days)
        {
            if (day.week < 0 || day.week >= Weeks || day.weekday < 0 || day.weekday >= 7) continue;
            const float cellLeft = gridLeft + WeekdayGutter + day.week * SlotSize +
                (SlotSize - CellSize) / 2.0f;
            const float cellTop = gridTop + day.weekday * SlotSize + (SlotSize - CellSize) / 2.0f;
            const D2D1_RECT_F cell = Draw::Rect(cellLeft, cellTop, cellLeft + CellSize,
                cellTop + CellSize);
            const Color fill = day.isFuture ? Color{ 0, 0, 0, 0 }
                : environment.highContrast ? HighContrastLevel(day.level) : RampColor(day.level, environment.dark);
            if (fill.a > 0.0f) Draw::FillRound(context, cell, 4.0f, fill);
            const float hover = interaction.Hover(300 + day.week * 7 + day.weekday);
            if (!day.isFuture && hover > 0)
                Draw::FillRound(context, cell, 4.0f, WithAlpha(palette.accent, 0.16f * hover));

            const bool selected = day.date == (traffic.selectedDate.empty() ? traffic.today : traffic.selectedDate);
            Draw::AddCell(regions, 300 + day.week * 7 + day.weekday, cell, L"selectDay:" + day.date,
                day.date, !day.isFuture && !day.date.empty(), selected);
        }

        for (const auto& day : traffic.days)
        {
            if (day.week < 0 || day.week >= Weeks || day.weekday < 0 || day.weekday >= 7) continue;
            if (day.isFuture || day.level < 0) continue;
            const int id = 300 + day.week * 7 + day.weekday;
            const float hover = interaction.Hover(id);
            const bool emphasized = day.isToday || interaction.focused == id ||
                day.date == (traffic.selectedDate.empty() ? traffic.today : traffic.selectedDate);
            if (!emphasized && day.level != 0 && hover <= 0) continue;
            const float cellLeft = gridLeft + WeekdayGutter + day.week * SlotSize +
                (SlotSize - CellSize) / 2.0f;
            const float cellTop = gridTop + day.weekday * SlotSize + (SlotSize - CellSize) / 2.0f;
            const D2D1_RECT_F cell = Draw::Rect(cellLeft, cellTop, cellLeft + CellSize,
                cellTop + CellSize);
            Draw::StrokeRound(context, cell, 4.0f,
                emphasized ? palette.accent : hover > 0 ? WithAlpha(palette.accent, hover) : palette.border, 1.0f);
        }

        const float footerTop = gridTop + 7 * SlotSize + 4.0f;
        Draw::Text(context, graphics, traffic.detail, environment.fontFamily, 10.0f, 400,
            palette.secondary, Draw::Rect(left, footerTop, left + contentWidth - 170.0f,
                footerTop + 18.0f));

        float legendRight = left + contentWidth;
        const std::wstring peak = L"\u5cf0\u503c " + (traffic.available ? FormatBytes(traffic.peakKiB) : L"—");
        Draw::Text(context, graphics, peak, environment.fontFamily, 9.0f, 400, palette.secondary,
            Draw::Rect(legendRight - 100.0f, footerTop, legendRight, footerTop + 18.0f),
            DWRITE_TEXT_ALIGNMENT_TRAILING);
        legendRight -= 104.0f;
        for (int level = 5; level >= 1; --level)
        {
            legendRight -= 10.0f;
            const Color color = environment.highContrast ? HighContrastLevel(level)
                : RampColor(level, environment.dark);
            Draw::FillRound(context, Draw::Rect(legendRight, footerTop + 5.0f,
                legendRight + 7.0f, footerTop + 12.0f), 2.0f, color);
        }
        legendRight -= 12.0f;
        Draw::Text(context, graphics, L"0", environment.fontFamily, 9.0f, 400, palette.secondary,
            Draw::Rect(legendRight - 12.0f, footerTop, legendRight, footerTop + 18.0f));
    }
}
