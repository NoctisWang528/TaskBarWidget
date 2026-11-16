#include "CardPch.h"
#include "CardTypes.h"

#include "CardText.h"

#include <algorithm>
#include <cwctype>

namespace Cards
{
    MaterialKind MaterialFromId(const std::wstring& id)
    {
        if (id == L"LiquidGlass") return MaterialKind::LiquidGlass;
        if (id == L"Solid") return MaterialKind::Solid;
        return MaterialKind::Mica;
    }

    const wchar_t* IdOf(MaterialKind kind)
    {
        switch (kind)
        {
        case MaterialKind::LiquidGlass: return L"LiquidGlass";
        case MaterialKind::Solid: return L"Solid";
        default: return L"Mica";
        }
    }

    void MaterialSettings::Set(const std::wstring& id, double blur)
    {
        kind = MaterialFromId(id);
        glassBlur = std::isfinite(blur) ? std::clamp(blur, 0.0, 1.0) : 0.35;
    }

    const wchar_t* IdOf(Kind kind)
    {
        switch (kind)
        {
        case Kind::Weather: return L"weather";
        case Kind::Traffic: return L"traffic";
        case Kind::Device: return L"device";
        case Kind::Media: return L"media";
        case Kind::Update: return L"update";
        case Kind::PeripheralBattery: return L"peripheralBattery";
        case Kind::AiUsage: return L"aiUsage";
        }
        return L"media";
    }

    bool KindFromId(const std::wstring& id, Kind& kind)
    {
        for (Kind candidate : { Kind::Weather, Kind::Traffic, Kind::Device, Kind::Media, Kind::PeripheralBattery, Kind::AiUsage })
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

    void Config::SetAnimation(double speedPercent, double fps)
    {
        animationSpeedPercent = std::isfinite(speedPercent)
            ? static_cast<int>(std::clamp(speedPercent, 50.0, 300.0)) : 150;
        animationFps = 0;
        for (const int option : {30, 60, 90, 120, 144, 165, 240})
            if (fps == option) { animationFps = option; break; }
    }

    bool IsAiProviderId(const std::wstring& id)
    {
        for (const wchar_t* known : { L"codex", L"openai-api", L"deepseek-web", L"deepseek-api",
            L"gemini-web", L"gemini-api", L"claude-web", L"claude-api" })
            if (id == known) return true;
        return false;
    }

    bool IsAiApiProviderId(const std::wstring& id)
    {
        return id == L"openai-api" || id == L"deepseek-api" || id == L"gemini-api" || id == L"claude-api";
    }

    std::wstring AiServiceId(const std::wstring& id)
    {
        if (id == L"codex" || id == L"openai-api") return L"openai";
        if (id == L"deepseek-web" || id == L"deepseek-api") return L"deepseek";
        if (id == L"gemini-web" || id == L"gemini-api") return L"gemini";
        if (id == L"claude-web" || id == L"claude-api") return L"claude";
        return {};
    }

    bool AiUsageState::ProviderVisible(const std::wstring& id) const
    {
        const auto service = enabledServices.find(AiServiceId(id));
        return IsAiProviderId(id) && service != enabledServices.end() && service->second &&
            (!IsAiApiProviderId(id) || showApi);
    }

    bool AiUsageState::Visible() const
    {
        for (const wchar_t* id : { L"codex", L"deepseek-web", L"gemini-web", L"claude-web" })
            if (ProviderVisible(id)) return true;
        return false;
    }

    std::vector<const AiProvider*> AiUsageState::VisibleProviders() const
    {
        std::vector<const AiProvider*> visible;
        for (const auto& provider : providers)
        {
            if (!ProviderVisible(provider.id)) continue;
            const bool api = IsAiApiProviderId(provider.id);
            if (provider.mode != (api ? L"api" : L"subscription") || (api && !showApi)) continue;
            visible.push_back(&provider);
        }
        return visible;
    }

    std::wstring AiUsageState::AdjacentProviderId(int direction) const
    {
        const auto visible = VisibleProviders();
        if (visible.empty()) return {};
        const auto found = std::find_if(visible.begin(), visible.end(),
            [&](const auto* provider) { return provider->id == providerId; });
        if (found == visible.end()) return visible.front()->id;
        const auto index = static_cast<int>(found - visible.begin());
        const auto count = static_cast<int>(visible.size());
        return visible[(index + (direction < 0 ? count - 1 : 1)) % count]->id;
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

    bool PeripheralDevice::Eligible(bool hideDisconnected) const
    {
        return showInCard && !id.empty() && compatibility != L"identityOnly" && state != L"unsupported" &&
            (canRead || !batteries.empty()) && (!hideDisconnected || state != L"disconnected");
    }

    std::vector<const PeripheralDevice*> PeripheralBatteryState::Selected() const
    {
        std::vector<const PeripheralDevice*> selected;
        for (const auto& device : devices) if (device.Eligible(hideDisconnected)) selected.push_back(&device);
        return selected;
    }

    bool PeripheralBatteryState::Visible() const
    {
        return std::any_of(devices.begin(), devices.end(), [&](const auto& device) { return device.Eligible(hideDisconnected); });
    }

    int PeripheralBatteryState::PageCount() const
    {
        return std::max(1, (static_cast<int>(Selected().size()) + PageSize() - 1) / PageSize());
    }

    int PeripheralBatteryState::ClampPage(int page) const { return std::clamp(page, 0, PageCount() - 1); }

    bool PeripheralBatteryState::SameAppearance(const PeripheralBatteryState& other, int page) const
    {
        if (mode != other.mode || refreshing != other.refreshing) return false;
        const auto a = Selected(), b = other.Selected();
        const int first = ClampPage(page) * PageSize();
        if (PageCount() != other.PageCount() || ClampPage(page) != other.ClampPage(page)) return false;
        const auto end = std::min(a.size(), static_cast<size_t>(first + PageSize()));
        if (end != std::min(b.size(), static_cast<size_t>(first + PageSize()))) return false;
        for (size_t i = first; i < end; ++i)
        {
            // Successful-read time is visible; snapshot revisions alone still do not repaint.
            if (a[i]->id != b[i]->id || a[i]->name != b[i]->name || a[i]->kind != b[i]->kind ||
                a[i]->brand != b[i]->brand || a[i]->source != b[i]->source ||
                a[i]->connection != b[i]->connection || a[i]->state != b[i]->state ||
                a[i]->stale != b[i]->stale || a[i]->updatedAt != b[i]->updatedAt ||
                a[i]->batteries != b[i]->batteries) return false;
        }
        return true;
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
