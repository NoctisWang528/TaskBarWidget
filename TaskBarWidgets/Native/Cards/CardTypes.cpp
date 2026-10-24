#include "CardPch.h"
#include "CardTypes.h"

#include "CardText.h"

#include <algorithm>
#include <cwctype>

namespace Cards
{
    const wchar_t* IdOf(Kind kind)
    {
        switch (kind)
        {
        case Kind::Weather: return L"weather";
        case Kind::Traffic: return L"traffic";
        case Kind::Device: return L"device";
        case Kind::Media: return L"media";
        case Kind::Update: return L"update";
        }
        return L"media";
    }

    bool KindFromId(const std::wstring& id, Kind& kind)
    {
        for (Kind candidate : { Kind::Weather, Kind::Traffic, Kind::Device, Kind::Media })
        {
            if (id == IdOf(candidate)) { kind = candidate; return true; }
        }
        return false;
    }

    bool Config::IsEnabled(Kind kind) const
    {
        const auto found = enabled.find(IdOf(kind));
        return found != enabled.end() && found->second;
    }

    int Config::ColumnFor(Kind kind) const
    {
        const auto found = columns.find(IdOf(kind));
        return found != columns.end() && found->second == 1 ? 1 : 0;
    }

    bool Config::OnDemand() const
    {
        std::wstring mode = resourceMode;
        std::transform(mode.begin(), mode.end(), mode.begin(),
            [](wchar_t character) { return static_cast<wchar_t>(std::towlower(character)); });
        return mode == L"ondemand";
    }

    const TrafficDay* TrafficState::Selected() const
    {
        for (const auto& day : days)
            if (day.selected && !day.isFuture) return &day;
        for (const auto& day : days)
            if (day.isToday) return &day;
        return nullptr;
    }

    void TrafficState::Select(const std::wstring& date)
    {
        selectedDate = date;
        for (auto& day : days)
            day.selected = !day.isFuture && day.date == date;
        const auto* selected = Selected();
        detail = selected ? selected->date : std::wstring{};
        if (selected)
        {
            if (selected->isFuture) detail += L" \u00b7 \u672a\u6765\u65e5\u671f";
            else if (!selected->known) detail += L" \u00b7 \u6ca1\u6709\u8bb0\u5f55";
            else detail += L" \u00b7 " + FormatBytes(selected->totalKiB);
        }
    }

    bool UpdateState::Visible() const
    {
        return phase == L"available" || phase == L"downloading" || phase == L"verifying" || phase == L"ready";
    }

    int UpdateState::ProgressPercent() const
    {
        if (totalBytes <= 0) return 0;
        const double ratio = static_cast<double>(bytesDownloaded) / static_cast<double>(totalBytes);
        return static_cast<int>(std::clamp(ratio, 0.0, 1.0) * 100.0 + 0.5);
    }
}
