#include "CardPch.h"
#include "CardDraw.h"
#include <chrono>
#include <ctime>

namespace Cards::Paint
{
    namespace
    {
        std::wstring Count(long long value)
        {
            wchar_t text[64]{};
            if (value >= 1000000000) swprintf_s(text, L"%.2fB", value / 1000000000.0);
            else if (value >= 1000000) swprintf_s(text, L"%.1fM", value / 1000000.0);
            else if (value >= 1000) swprintf_s(text, L"%.1fK", value / 1000.0);
            else swprintf_s(text, L"%lld", value);
            return text;
        }
        std::wstring Date(std::chrono::sys_days day)
        {
            const std::chrono::year_month_day date{day};
            wchar_t text[16]{};
            swprintf_s(text, L"%04d-%02u-%02u", int(date.year()), unsigned(date.month()), unsigned(date.day()));
            return text;
        }
        std::wstring UpdatedTime(const std::wstring& iso)
        {
            if (iso.empty()) return L"尚无数据时间";
            int year{}, month{}, day{}, hour{}, minute{}, second{};
            if (swscanf_s(iso.c_str(), L"%d-%d-%dT%d:%d:%d", &year, &month, &day, &hour, &minute, &second) != 6)
                return L"数据时间未知";
            const std::chrono::year_month_day date{std::chrono::year{year}, std::chrono::month{static_cast<unsigned>(month)}, std::chrono::day{static_cast<unsigned>(day)}};
            if (!date.ok() || hour < 0 || hour > 23 || minute < 0 || minute > 59 || second < 0 || second > 59) return L"数据时间未知";
            std::tm time{};
            time.tm_year = year - 1900; time.tm_mon = month - 1; time.tm_mday = day;
            time.tm_hour = hour; time.tm_min = minute; time.tm_sec = second;
            auto stamp = _mkgmtime64(&time);
            const auto offset = iso.find_first_of(L"+-", 19);
            if (offset != std::wstring::npos)
            {
                int hours{}, minutes{};
                if (swscanf_s(iso.c_str() + offset + 1, L"%d:%d", &hours, &minutes) != 2 || hours > 23 || minutes > 59)
                    return L"数据时间未知";
                stamp -= (iso[offset] == L'+' ? 1 : -1) * (hours * 60LL + minutes) * 60;
            }
            else if (iso.find(L'Z', 19) == std::wstring::npos) return L"数据时间未知";
            std::tm local{};
            if (_localtime64_s(&local, &stamp)) return L"数据时间未知";
            wchar_t text[40]{};
            swprintf_s(text, L"更新 %02d/%02d %02d:%02d", local.tm_mon + 1, local.tm_mday, local.tm_hour, local.tm_min);
            return text;
        }
        std::wstring Reset(const AiQuota& quota)
        {
            if (!quota.resetsAt) return L"重置时间未知";
            const std::time_t time = static_cast<std::time_t>(*quota.resetsAt);
            std::tm local{};
            if (localtime_s(&local, &time)) return L"重置时间未知";
            wchar_t text[64]{};
            swprintf_s(text, L"%02d/%02d %02d:%02d 重置", local.tm_mon + 1, local.tm_mday, local.tm_hour, local.tm_min);
            return text;
        }
        // Same geometry and ramp as the traffic heatmap so the two calendars read alike.
        constexpr float CellSize{ 21.0f };
        constexpr float SlotSize{ 25.0f };
        constexpr float WeekdayGutter{ 20.0f };
        constexpr float MonthsHeight{ 12.0f };
        constexpr int Weeks{ 12 };
        Color RampColor(int level, bool dark)
        {
            static const wchar_t* light[] = { L"#00FFFFFF", L"#E4E9EF", L"#D3E6FB", L"#96C3EF", L"#4E97DC", L"#2365AA" };
            static const wchar_t* night[] = { L"#00202B37", L"#343E4B", L"#284764", L"#326B9C", L"#55A1D5", L"#B4E6FF" };
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
                return Color{ GetRValue(text) / 255.0f, GetGValue(text) / 255.0f, GetBValue(text) / 255.0f, 0.18f };
            const float ratio = (level - 1) / 4.0f;
            return Color{ GetRValue(highlight) / 255.0f, GetGValue(highlight) / 255.0f,
                GetBValue(highlight) / 255.0f, 0.25f + ratio * 0.75f };
        }
        std::wstring WindowText(int minutes)
        {
            if (minutes <= 0) return L"窗口未知";
            if (minutes >= 1440) return std::to_wstring(minutes / 1440) + L" 天窗口";
            if (minutes >= 60) return std::to_wstring(minutes / 60) + L" 小时窗口";
            return std::to_wstring(minutes) + L" 分钟窗口";
        }
    }
    void AiUsage(ID2D1DeviceContext* context, const Snapshot& snapshot, const CardEnvironment& environment,
        Layout::Table& regions, float widthDip, float heightDip, const Interaction& interaction)
    {
        UNREFERENCED_PARAMETER(heightDip);
        if (!context || !environment.graphics) return;
        auto& graphics = *environment.graphics;
        const auto palette = Draw::Colors(environment);
        const bool glassCells = interaction.glass && interaction.compositedGlass &&
            interaction.glassControls && !environment.highContrast;
        auto state = snapshot.aiUsage;
        // Preferences can hide a cached selection before the next reply arrives.
        // Never render activity or monetary data for a source that is switched off.
        const bool selectedVisible = state.ProviderVisible(state.providerId);
        if (!selectedVisible)
        {
            state.quotas.clear(); state.balances.clear(); state.days.clear();
            state.lifetimeTokens.reset(); state.peakDailyTokens.reset();
            state.providerName = L"选择来源"; state.sourceLabel.clear();
            state.status = L"disabled"; state.error = state.Visible() ? L"来源已隐藏，请选择已开启的服务" : L"打开需要查看的 AI 服务";
            state.quotaNote.clear(); state.historyNote.clear(); state.updatedAt.clear();
        }
        const auto visibleProviders = state.VisibleProviders();
        const float left = Layout::Padding, right = widthDip - left, contentWidth = right - left;
        const auto text = [&](const std::wstring& value, float size, Color color, D2D1_RECT_F rect, int weight = 400,
            DWRITE_TEXT_ALIGNMENT align = DWRITE_TEXT_ALIGNMENT_LEADING,
            DWRITE_PARAGRAPH_ALIGNMENT vertical = DWRITE_PARAGRAPH_ALIGNMENT_NEAR)
        {
            Draw::Text(context, graphics, value, environment.fontFamily, size, weight, color, rect, align,
                false, false, false, vertical);
        };
        constexpr auto Center = DWRITE_TEXT_ALIGNMENT_CENTER;
        constexpr auto Trailing = DWRITE_TEXT_ALIGNMENT_TRAILING;
        constexpr auto Middle = DWRITE_PARAGRAPH_ALIGNMENT_CENTER;
        const auto chevron = [&](D2D1_RECT_F rect, bool next, Color color)
        {
            const float cx = (rect.left + rect.right) / 2, cy = (rect.top + rect.bottom) / 2;
            D2D1_MATRIX_3X2_F previous{};
            context->GetTransform(&previous);
            // The shared chevron points down; rotate it into a horizontal pager arrow.
            context->SetTransform(D2D1::Matrix3x2F::Rotation(next ? -90.0f : 90.0f, D2D1::Point2F(cx, cy)) * previous);
            DrawGlyph(context, Glyph::Chevron, color, cx - 6, cy - 6, 12);
            context->SetTransform(previous);
        };
        const bool loading = state.status == L"loading";

        // Header: title + source on the left; provider pager, details and refresh on the right.
        const float headerTop = 16, headerBottom = 44;
        const auto refresh = Draw::Rect(right - 28, headerTop, right, headerBottom);
        const bool canRefresh = !loading && selectedVisible;
        Draw::ControlBackground(context, refresh, 14, WithAlpha(palette.fill, 0), palette.accent, interaction, 700, canRefresh);
        DrawGlyph(context, Glyph::Refresh, WithAlpha(palette.primary, canRefresh ? 0.9f : 0.4f),
            refresh.left + 6, refresh.top + 6, 16);
        Draw::AddButton(regions, 700, refresh, L"refreshAiUsage", loading ? L"正在读取 AI 用量" : L"刷新 AI 用量", canRefresh);
        const auto details = Draw::Rect(right - 60, headerTop, right - 32, headerBottom);
        Draw::ControlBackground(context, details, 14, WithAlpha(palette.fill, 0), palette.accent, interaction, 701);
        DrawGlyph(context, Glyph::Settings, WithAlpha(palette.primary, 0.85f), details.left + 6, details.top + 6, 16);
        Draw::AddButton(regions, 701, details, L"openAiUsageSettings", L"查看全部 AI 额度和使用详情");

        const bool canChoose = visibleProviders.size() > 1 || (!visibleProviders.empty() &&
            std::none_of(visibleProviders.begin(), visibleProviders.end(), [&](const auto* item) { return item->id == state.providerId; }));
        const float pagerRight = right - 66, pagerLeft = pagerRight - 132;
        const auto pager = Draw::Rect(pagerLeft, headerTop, pagerRight, headerBottom);
        // Decorative capsule: never reuse the input IDs 702/703.
        Draw::ControlBackground(context, pager, 14, WithAlpha(palette.primary, 0.06f), palette.accent, interaction, 4702);
        for (int index = 0; index < 2; ++index)
        {
            const auto button = index ? Draw::Rect(pagerRight - 28, headerTop, pagerRight, headerBottom)
                : Draw::Rect(pagerLeft, headerTop, pagerLeft + 28, headerBottom);
            Draw::ControlBackground(context, button, 14, WithAlpha(palette.fill, 0), palette.accent, interaction, 702 + index, canChoose);
            chevron(button, index == 1, WithAlpha(canChoose ? palette.primary : palette.secondary, canChoose ? 0.9f : 0.45f));
            Draw::AddButton(regions, 702 + index, button, index ? L"aiUsageProvider:next" : L"aiUsageProvider:previous",
                index ? L"下一个 AI 来源" : L"上一个 AI 来源", canChoose);
        }
        text(state.providerName.empty() ? L"选择来源" : state.providerName, 11, palette.primary,
            Draw::Rect(pagerLeft + 26, headerTop, pagerRight - 26, headerBottom), 600, Center, Middle);
        text(L"AI用量", 14, palette.primary, Draw::Rect(left, headerTop, left + 60, headerBottom), 600,
            DWRITE_TEXT_ALIGNMENT_LEADING, Middle);
        text(state.sourceLabel, 10, palette.secondary, Draw::Rect(left + 62, headerTop + 1, pagerLeft - 6, headerBottom),
            400, DWRITE_TEXT_ALIGNMENT_LEADING, Middle);

        // Hero: service limits or balances, independent of the token activity below.
        const float heroTop = 54;
        const float columnGap = 16, columnWidth = (contentWidth - columnGap) / 2;
        if (!state.balances.empty())
        {
            const size_t count = (std::min)(state.balances.size(), size_t{ 2 });
            for (size_t index = 0; index < count; ++index)
            {
                const auto& balance = state.balances[index];
                const float x = left + static_cast<float>(index) * (columnWidth + columnGap);
                const float end = count == 1 ? right : x + columnWidth;
                const auto amount = balance.currency + L" " + balance.available;
                text(amount, count == 1 ? 22.0f : 18.0f, palette.primary, Draw::Rect(x, heroTop + (count == 1 ? 0 : 3), end, heroTop + 30), 600);
                text(balance.label.empty() ? L"可用余额" : balance.label, 9, palette.secondary,
                    Draw::Rect(x, heroTop + 32, end, heroTop + 46));
                Draw::AddText(regions, 730 + static_cast<int>(index), Draw::Rect(x, heroTop, end, heroTop + 46), amount + L"，" + balance.label);
            }
        }
        else if (!state.quotas.empty())
        {
            const size_t count = (std::min)(state.quotas.size(), size_t{ 2 });
            for (size_t index = 0; index < count; ++index)
            {
                const auto& quota = state.quotas[index];
                const float x = left + static_cast<float>(index) * (columnWidth + columnGap);
                const float end = count == 1 ? right : x + columnWidth;
                const double remainingPercent = std::clamp(100.0 - quota.usedPercent, 0.0, 100.0);
                wchar_t percent[16]{};
                swprintf_s(percent, L"%.0f%%", remainingPercent);
                const auto label = quota.label.empty() ? quota.id : quota.label;
                const auto window = WindowText(quota.windowMinutes);
                text(percent, 22, palette.primary, Draw::Rect(x, heroTop - 2, end, heroTop + 28), 600);
                text(L"剩余 · " + label, 10, palette.secondary, Draw::Rect(x + 56, heroTop + 7, end, heroTop + 24),
                    400, Trailing);
                Draw::Progress(context, Draw::Rect(x, heroTop + 31, end, heroTop + 35), static_cast<float>(remainingPercent / 100.0),
                    palette.track, palette.accent, false);
                text(window + L" · " + Reset(quota), 9, palette.secondary, Draw::Rect(x, heroTop + 38, end, heroTop + 52));
                Draw::AddText(regions, 710 + static_cast<int>(index), Draw::Rect(x, heroTop, end, heroTop + 52),
                    label + L"，" + window + L"，剩余 " + percent + L"，" + Reset(quota));
            }
        }
        else
        {
            const auto note = state.quotaNote.empty() ? std::wstring(L"剩余额度未知") : state.quotaNote;
            text(L"—", 22, palette.primary, Draw::Rect(left, heroTop - 2, left + 40, heroTop + 28), 600);
            text(note, 10, palette.secondary, Draw::Rect(left, heroTop + 32, right, heroTop + 46));
            Draw::AddText(regions, 712, Draw::Rect(left, heroTop, right, heroTop + 46), note + L"，无可用记录不代表用量为零");
        }

        // Activity calendar, laid out exactly like the traffic heatmap.
        SYSTEMTIME calendar{};
        if (state.activityUtc) GetSystemTime(&calendar); else GetLocalTime(&calendar);
        using namespace std::chrono;
        const sys_days today = year{calendar.wYear}/month{calendar.wMonth}/day{calendar.wDay};
        const auto monday = today - days{weekday{today}.iso_encoding() - 1};
        const auto first = monday - weeks{Weeks - 1};
        long long peak = 0;
        bool hasKnownDay = false;
        for (const auto& [date, tokens] : state.days)
            if (date >= Date(first) && date <= Date(today))
            {
                hasKnownDay = true;
                peak = (std::max)(peak, tokens);
            }
        const std::wstring unit = state.activityUnit == L"requests" ? L"次请求" : L"tokens";
        const std::wstring activityLabel = state.activityUnit == L"requests" ? L"请求活动" : L"Token 活动";
        const auto more = state.balances.size() > 2 ? L" · 更多余额见详情" : state.quotas.size() > 2 ? L" · 更多额度见详情" : L"";
        const auto caption = activityLabel + (state.activityUtc ? L" · UTC" : L"") + L" · 近 12 周" + more;
        const float captionTop = 122;
        text(caption, 10, palette.secondary, Draw::Rect(left, captionTop, right - 150, captionTop + 16));
        Draw::AddText(regions, 740, Draw::Rect(left, captionTop, right - 150, captionTop + 16), caption);

        float legendRight = right;
        const auto peakText = L"峰值 " + (hasKnownDay ? Count(peak) : std::wstring(L"—"));
        const auto peakBounds = Draw::Rect(legendRight - 80, captionTop + 1, legendRight, captionTop + 16);
        text(peakText, 9, palette.secondary, peakBounds, 400, Trailing);
        Draw::AddText(regions, 742, peakBounds, peakText);
        legendRight -= 84;
        for (int level = 5; level >= 1; --level)
        {
            legendRight -= 10;
            const Color color = environment.highContrast ? HighContrastLevel(level) : RampColor(level, environment.dark);
            Draw::FillRound(context, Draw::Rect(legendRight, captionTop + 5, legendRight + 7, captionTop + 12), 2, color);
        }
        legendRight -= 12;
        text(L"0", 9, palette.secondary, Draw::Rect(legendRight - 8, captionTop + 1, legendRight, captionTop + 16));

        const float gridWidth = WeekdayGutter + Weeks * SlotSize;
        const float gridLeft = left + (contentWidth - gridWidth) / 2;
        const float monthsTop = captionTop + 20;
        const float gridTop = monthsTop + MonthsHeight + 3;
        const float inset = (SlotSize - CellSize) / 2;
        for (int week = 0; week < Weeks; ++week)
        {
            const year_month_day start{ first + weeks{week} };
            if (week > 0 && year_month_day{ first + weeks{week - 1} }.month() == start.month()) continue;
            text(std::to_wstring(unsigned(start.month())) + L"月", 9, palette.secondary,
                Draw::Rect(gridLeft + WeekdayGutter + week * SlotSize, monthsTop,
                    gridLeft + WeekdayGutter + (week + 1) * SlotSize + 8, monthsTop + MonthsHeight));
        }
        static const wchar_t* weekdayNames[] = { L"一", L"", L"三", L"", L"五", L"", L"日" };
        for (int row = 0; row < 7; ++row)
            if (weekdayNames[row][0])
                text(weekdayNames[row], 9, palette.secondary, Draw::Rect(gridLeft, gridTop + row * SlotSize,
                    gridLeft + WeekdayGutter, gridTop + (row + 1) * SlotSize));

        struct Cell { D2D1_RECT_F rect; std::wstring key; long long tokens; int level; bool known, future, isToday; };
        std::vector<Cell> cells;
        cells.reserve(Weeks * 7);
        for (int index = 0; index < Weeks * 7; ++index)
        {
            const auto date = first + days{index};
            const auto key = Date(date);
            const auto found = state.days.find(key);
            const bool future = date > today;
            const bool known = !future && found != state.days.end();
            const long long tokens = known ? found->second : 0;
            int level = -1;
            if (known)
            {
                const double ratio = peak > 0 ? std::sqrt(static_cast<double>(tokens) / peak) : 0.0;
                level = tokens <= 0 ? 0 : 1 + (std::min)(4, static_cast<int>(ratio * 5));
            }
            const float x = gridLeft + WeekdayGutter + (index / 7) * SlotSize + inset;
            const float y = gridTop + (index % 7) * SlotSize + inset;
            cells.push_back({ Draw::Rect(x, y, x + CellSize, y + CellSize), key, tokens, level, known, future, date == today });
        }
        // Restrained halo under busy days, one shared mask and blur pass like the traffic card.
        if (auto* mask = environment.highContrast ? nullptr : graphics.BeginMask(context))
        {
            const Color halo = ParseColor(environment.dark ? L"#6969BDFA" : L"#345281D3", Color{});
            for (const auto& cell : cells)
            {
                if (cell.level < 2 && !cell.isToday) continue;
                const auto& r = cell.rect;
                Draw::FillRound(mask, Draw::Rect(r.left + 1, r.top + 1, r.right - 1, r.bottom - 1), 3,
                    WithAlpha(halo, halo.a * (cell.isToday ? 0.75f : 0.45f)));
            }
            graphics.EndMask(context, 4.0f);
        }
        for (size_t index = 0; index < cells.size(); ++index)
        {
            const auto& cell = cells[index];
            const int id = 800 + static_cast<int>(index);
            const Color fill = cell.known
                ? environment.highContrast ? HighContrastLevel(cell.level) : RampColor(cell.level, environment.dark)
                : cell.future ? Color{ 0, 0, 0, 0 } : WithAlpha(palette.secondary, 0.08f);
            if (glassCells)
            {
                // Future dates stay blank. Unknown days have a faint tint;
                // recorded zero has a clear lens and a border, never a usage tint.
                if (!cell.future)
                {
                    GlassControl control;
                    control.id = id; control.kind = GlassControl::Kind::Cell;
                    control.bounds = cell.rect; control.radius = 4.0f;
                    // These are read-only surfaces; input semantics remain AddText.
                    control.focused = cell.isToday || (cell.known && cell.level == 0);
                    control.ink = cell.isToday ? palette.accent : palette.border;
                    control.fillColor = cell.known
                        ? WithAlpha(fill, fill.a * (environment.dark ? .45f : .55f)) : fill;
                    interaction.glassControls->push_back(control);
                }
            }
            else
            {
                if (fill.a > 0) Draw::FillRound(context, cell.rect, 4, fill);
                if (cell.known && cell.level == 0) Draw::StrokeRound(context, cell.rect, 4, palette.border, 1);
                if (cell.isToday) Draw::StrokeRound(context, cell.rect, 4, palette.accent, 1);
            }
            Draw::AddText(regions, id, cell.rect, cell.key + (cell.future ? L"，未来"
                : cell.known ? L"，" + std::to_wstring(cell.tokens) + L" " + unit : L"，无记录"));
        }

        // Footer: totals with data time, then the status line.
        const float footerTop = gridTop + 7 * SlotSize + 10;
        const auto current = state.days.find(Date(today));
        const auto todayText = current == state.days.end() ? L"—" : Count(current->second);
        const auto totals = std::wstring(state.activityUtc ? L"今日 UTC " : L"今日 ") + todayText + L" · 累计 " +
            (state.lifetimeTokens ? Count(*state.lifetimeTokens) : L"—") + L" " + unit;
        text(totals, 10, palette.primary, Draw::Rect(left, footerTop, right - 110, footerTop + 18));
        Draw::AddText(regions, 741, Draw::Rect(left, footerTop, right - 110, footerTop + 18), totals);
        text(UpdatedTime(state.updatedAt), 9, palette.secondary, Draw::Rect(right - 110, footerTop + 1, right, footerTop + 18), 400, Trailing);
        const auto status = loading ? L"正在读取用量…" : !state.error.empty() ? state.error
            : state.status == L"partial" ? L"部分数据暂不可用" : state.status == L"error" ? L"读取失败，请刷新重试"
            : state.status == L"idle" ? L"等待连接所选来源" : !state.historyNote.empty() ? state.historyNote : L"额度与活动记录分别统计";
        text(status, 9, palette.secondary, Draw::Rect(left, footerTop + 22, right, footerTop + 38));
        Draw::AddText(regions, 720, Draw::Rect(left, footerTop, right, footerTop + 36), status + L"，来源 " + state.sourceLabel +
            L"，数据时间 " + state.updatedAt + L"，" + state.quotaNote + L"，" + state.historyNote + L"，淡色格=无记录，空白=未来");
    }
}
