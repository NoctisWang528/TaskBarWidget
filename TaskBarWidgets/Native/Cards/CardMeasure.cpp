#include "CardPch.h"
#include "CardDraw.h"

namespace Cards::Paint
{
    // Auto-height measurement. The weather and device cards grow with their
    // content exactly like the WinUI versions (padding plus measured rows); the
    // fixed cards report their constant height.
    float MeasureHeight(Kind kind, const Snapshot& snapshot, const CardEnvironment& environment,
        float widthDip, int peripheralPage)
    {
        switch (kind)
        {
        case Kind::Weather:
        {
            return WeatherHeight(snapshot, environment, widthDip);
        }
        case Kind::PeripheralBattery:
        {
            const auto& state = snapshot.peripheralBattery;
            if (state.mode != L"detailed") return Layout::HeightPeripheralCompact;
            const int rows = std::clamp(static_cast<int>(state.Selected().size()) -
                state.ClampPage(peripheralPage) * state.PageSize(), 0, state.PageSize());
            return Layout::HeightPeripheralDetailed - (4 - rows) * 50.f;
        }
        case Kind::Device:
        {
            const float section = 16.0f + 3.0f + 32.0f + 4.0f + 32.0f + 8.0f;
            float height = Layout::Padding * 2.0f + section * 2.0f;
            if (!snapshot.audio.message.empty()) height += 22.0f;
            return std::max(Layout::HeightDevice, height);
        }
        default:
            return Layout::DefaultHeightDip(kind);
        }
    }
}
