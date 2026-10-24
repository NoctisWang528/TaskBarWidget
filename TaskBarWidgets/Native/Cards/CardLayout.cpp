#include "CardPch.h"
#include "CardLayout.h"

#include <algorithm>
#include <cmath>

namespace Cards::Layout
{
    namespace
    {
        struct ColumnItems
        {
            std::vector<Kind> kinds;
            float heightDip{ 0.0f };
        };

        float Round(float value) { return std::floor(value + 0.5f); }

        UINT DpiOf(HWND window)
        {
            if (!window) return 96;
            using GetDpi = UINT(WINAPI*)(HWND);
            static const auto getDpi = [] () -> GetDpi
            {
                HMODULE user32 = GetModuleHandleW(L"user32.dll");
                return user32 ? reinterpret_cast<GetDpi>(
                    reinterpret_cast<void*>(GetProcAddress(user32, "GetDpiForWindow"))) : nullptr;
            }();
            if (getDpi)
            {
                const UINT dpi = getDpi(window);
                if (dpi) return dpi;
            }
            return 96;
        }
    }

    float DefaultHeightDip(Kind kind)
    {
        switch (kind)
        {
        case Kind::Traffic: return HeightTraffic;
        case Kind::Device: return HeightDevice;
        case Kind::Media: return HeightMedia;
        case Kind::Weather: return HeightWeather;
        case Kind::Update: return HeightUpdate;
        }
        return HeightMedia;
    }

    bool AutoHeight(Kind kind)
    {
        return kind == Kind::Weather || kind == Kind::Device;
    }

    float WidthDip(Kind kind)
    {
        return kind == Kind::Update ? UpdateCardWidth : CardWidth;
    }

    Placement PlaceGroup(const Config& config, const std::map<Kind, float>& heights,
        POINT anchor, HWND reference, bool updateVisible, float updateHeightDip)
    {
        Placement placement;
        const auto heightOf = [&heights](Kind kind, float fallback)
        {
            const auto found = heights.find(kind);
            const float measured = found != heights.end() ? found->second : 0.0f;
            return measured > 0.0f ? measured : fallback;
        };

        ColumnItems left;
        ColumnItems right;
        for (const auto& id : config.order)
        {
            Kind kind{};
            if (!KindFromId(id, kind)) continue;
            if (!config.IsEnabled(kind)) continue;
            auto& column = config.ColumnFor(kind) == 1 ? right : left;
            column.kinds.push_back(kind);
        }

        const MONITORINFO monitor = [anchor]()
        {
            MONITORINFO info{ sizeof(info) };
            const HMONITOR handle = MonitorFromPoint(anchor, MONITOR_DEFAULTTONEAREST);
            if (!handle || !GetMonitorInfoW(handle, &info)) info.rcWork = RECT{ 0, 0, 0, 0 };
            return info;
        }();
        if (IsRectEmpty(&monitor.rcWork))
        {
            placement.diagnostic = L"work area unavailable";
            return placement;
        }
        const long workWidth = monitor.rcWork.right - monitor.rcWork.left;
        const long workHeight = monitor.rcWork.bottom - monitor.rcWork.top;

        const float dpiScale = static_cast<float>(DpiOf(reference)) / 96.0f;
        placement.dpiScale = dpiScale;

        const auto columnHeight = [&](const ColumnItems& column)
        {
            if (column.kinds.empty()) return 0.0f;
            float total = 0.0f;
            for (const auto kind : column.kinds) total += heightOf(kind, DefaultHeightDip(kind));
            total += Gap * static_cast<float>(column.kinds.size() - 1);
            return total;
        };
        left.heightDip = columnHeight(left);
        right.heightDip = columnHeight(right);

        const bool anyBase = !left.kinds.empty() || !right.kinds.empty();
        float desiredBaseHeight = std::max(left.heightDip, right.heightDip);
        if (!anyBase) desiredBaseHeight = updateHeightDip;

        const int baseColumns = (left.kinds.empty() ? 0 : 1) + (right.kinds.empty() ? 0 : 1);
        const int updateColumns = updateVisible ? 1 : 0;
        const int totalColumns = baseColumns + updateColumns;
        if (totalColumns == 0)
        {
            placement.diagnostic = L"no visible card";
            return placement;
        }

        const float reserve = EdgeReserve * dpiScale;
        const float baseWidthPx = std::min(Round(CardWidth * dpiScale),
            static_cast<float>(workWidth) - reserve);
        const float updateWidthPx = std::min(Round(UpdateCardWidth * dpiScale),
            static_cast<float>(workWidth) - reserve);
        const float gapPx = Round(Gap * dpiScale);

        const float totalWidth = baseColumns * baseWidthPx + updateColumns * updateWidthPx +
            gapPx * static_cast<float>(totalColumns - 1);
        const float availableWidth = static_cast<float>(workWidth) - reserve;
        const float availableHeight = static_cast<float>(workHeight) - reserve;
        float fit = 1.0f;
        if (totalWidth > 0.0f) fit = std::min(fit, availableWidth / totalWidth);
        if (desiredBaseHeight > 0.0f)
            fit = std::min(fit, availableHeight / (desiredBaseHeight * dpiScale));
        fit = std::max(0.0f, std::min(1.0f, fit));
        placement.fit = fit;
        placement.valid = true;

        const auto pixelHeight = [&](Kind kind)
        {
            const float dip = heightOf(kind, DefaultHeightDip(kind));
            return std::max(1.0f, std::floor(dip * dpiScale * fit));
        };
        const auto pixelWidth = [&](Kind kind)
        {
            const float base = kind == Kind::Update ? updateWidthPx : baseWidthPx;
            return std::max(1.0f, std::floor(base * fit));
        };

        float baseBlockHeight = 0.0f;
        for (const auto* column : { &left, &right })
        {
            if (column->kinds.empty()) continue;
            float total = 0.0f;
            for (const auto kind : column->kinds) total += pixelHeight(kind);
            total += gapPx * static_cast<float>(column->kinds.size() - 1);
            baseBlockHeight = std::max(baseBlockHeight, total);
        }
        baseBlockHeight = std::ceil(baseBlockHeight);

        const float baseWidth = pixelWidth(Kind::Weather);
        float blockWidth = baseColumns * baseWidth + updateColumns * pixelWidth(Kind::Update) +
            gapPx * static_cast<float>(totalColumns - 1);

        const auto clamp = [](float value, float low, float high)
        {
            if (high < low) high = low;
            return std::max(low, std::min(high, value));
        };
        const long groupLeft = static_cast<long>(clamp(Round(anchor.x - blockWidth / 2.0f),
            static_cast<float>(monitor.rcWork.left),
            static_cast<float>(monitor.rcWork.right) - blockWidth));
        const long groupTop = static_cast<long>(clamp(Round(anchor.y - baseBlockHeight - 10.0f * dpiScale),
            static_cast<float>(monitor.rcWork.top),
            static_cast<float>(monitor.rcWork.bottom) - baseBlockHeight));

        int columnIndex = 0;
        for (const auto* column : { &left, &right })
        {
            if (column->kinds.empty()) continue;
            float columnHeight = 0.0f;
            for (const auto kind : column->kinds) columnHeight += pixelHeight(kind);
            columnHeight += gapPx * static_cast<float>(column->kinds.size() - 1);
            const long columnLeft = groupLeft + static_cast<long>(
                static_cast<float>(columnIndex) * (baseWidth + gapPx));
            long top = groupTop + static_cast<long>(baseBlockHeight) -
                static_cast<long>(Round(columnHeight));
            for (const auto kind : column->kinds)
            {
                Slot slot;
                slot.kind = kind;
                slot.scale = dpiScale * fit;
                const long width = static_cast<long>(pixelWidth(kind));
                const long height = static_cast<long>(pixelHeight(kind));
                slot.bounds = RECT{ columnLeft, top, columnLeft + width, top + height };
                placement.slots.push_back(slot);
                top += height + static_cast<long>(gapPx);
            }
            ++columnIndex;
        }

        if (updateVisible)
        {
            Slot slot;
            slot.kind = Kind::Update;
            slot.scale = dpiScale * fit;
            const long width = static_cast<long>(pixelWidth(Kind::Update));
            const long height = static_cast<long>(pixelHeight(Kind::Update));
            const long left = groupLeft + static_cast<long>(
                static_cast<float>(baseColumns) * (baseWidth + gapPx));
            const long preferred = groupTop + static_cast<long>(baseBlockHeight) - height;
            slot.bounds = RECT{ left, std::max(monitor.rcWork.top, static_cast<long>(preferred)),
                left + width, 0 };
            slot.bounds.bottom = slot.bounds.top + height;
            placement.slots.push_back(slot);
        }
        return placement;
    }

    const Region* Table::Hit(POINT dip) const
    {
        for (const auto& region : regions)
        {
            if (dip.x >= region.bounds.left && dip.x < region.bounds.right &&
                dip.y >= region.bounds.top && dip.y < region.bounds.bottom)
                return &region;
        }
        return nullptr;
    }

    const Region* Table::Find(int id) const
    {
        for (const auto& region : regions)
            if (region.id == id) return &region;
        return nullptr;
    }

    const Region* Table::NextFocus(const Region* current, int direction) const
    {
        if (regions.empty()) return nullptr;
        std::vector<const Region*> focusable;
        for (const auto& region : regions)
            if (region.focusable && region.enabled) focusable.push_back(&region);
        if (focusable.empty()) return nullptr;
        const auto found = current ? std::find(focusable.begin(), focusable.end(), current)
            : focusable.end();
        long index = found == focusable.end() ? (direction < 0 ? 0 : -1)
            : static_cast<long>(found - focusable.begin());
        const long count = static_cast<long>(focusable.size());
        index = (index + direction % count + count) % count;
        return focusable[static_cast<size_t>(index)];
    }
}
