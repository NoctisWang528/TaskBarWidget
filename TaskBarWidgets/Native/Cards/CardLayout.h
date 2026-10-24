#pragma once
// Card geometry and hit regions. Everything here is expressed in DIP and only
// converted to physical pixels when a window rectangle is produced, so a DPI
// change is just another placement pass.

#include "CardTypes.h"

#include <Windows.h>

#include <map>
#include <string>
#include <vector>

namespace Cards
{
    namespace Layout
    {
        constexpr float CardWidth{ 384.0f };
        constexpr float UpdateCardWidth{ 220.0f };
        constexpr float Gap{ 12.0f };
        constexpr float Padding{ 16.0f };
        constexpr float HeightTraffic{ 284.0f };
        constexpr float HeightDevice{ 244.0f };   // baseline; measured by content
        constexpr float HeightMedia{ 248.0f };
        constexpr float HeightWeather{ 280.0f };  // baseline; measured by content
        constexpr float HeightUpdate{ 180.0f };
        constexpr float CornerRadius{ 8.0f };
        constexpr float EdgeReserve{ 20.0f };

        float DefaultHeightDip(Kind kind);
        bool AutoHeight(Kind kind);
        float WidthDip(Kind kind);

        struct Slot
        {
            Kind kind{ Kind::Media };
            RECT bounds{};      // physical pixels, screen coordinates
            float scale{ 1.0f };  // fit * dpi/96: DIP -> pixels for this slot
        };

        struct Placement
        {
            std::vector<Slot> slots;
            float dpiScale{ 1.0f };   // monitor DPI / 96
            float fit{ 1.0f };        // overall shrink applied when space is short
            bool valid{ false };
            std::wstring diagnostic;
        };

        // Port of CardGroup.Place. `heights` carries measured DIP heights; the
        // defaults fill anything missing. `anchor` is the taskbar click point in
        // screen pixels. `reference` only supplies the fallback DPI.
        Placement PlaceGroup(const Config& config, const std::map<Kind, float>& heights,
            POINT anchor, HWND reference, bool updateVisible, float updateHeightDip = HeightUpdate);

        enum class RegionType
        {
            Button,
            Slider,
            Checkbox,
            Cell,
            Text,
            MenuItem,
        };

        struct Region
        {
            int id{ 0 };
            RegionType type{ RegionType::Button };
            RECT bounds{};   // DIP, card-local
            bool enabled{ true };
            bool selected{ false };
            bool focusable{ true };
            std::wstring action;
            std::wstring name;
        };

        // One table per card. Painting, hit testing, keyboard navigation and the
        // automation provider all read this, so a drawn control can never be in a
        // different place from its click area.
        class Table
        {
        public:
            void Clear() { regions.clear(); }
            void Add(Region region) { regions.push_back(std::move(region)); }
            const std::vector<Region>& All() const { return regions; }
            const Region* Hit(POINT dip) const;
            const Region* Find(int id) const;
            const Region* NextFocus(const Region* current, int direction) const;
            bool Empty() const { return regions.empty(); }
        private:
            std::vector<Region> regions;
        };

        inline POINT ToDip(POINT pixels, float scale)
        {
            const float inverse = scale > 0.0f ? 1.0f / scale : 1.0f;
            return POINT{ static_cast<LONG>(pixels.x * inverse + 0.5f),
                static_cast<LONG>(pixels.y * inverse + 0.5f) };
        }
    }
}
