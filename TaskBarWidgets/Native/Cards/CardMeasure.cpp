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
            return DeviceHeight(snapshot, environment, widthDip);
        default:
            return Layout::DefaultHeightDip(kind);
        }
    }
}
