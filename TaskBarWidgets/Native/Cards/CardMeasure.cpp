#include "CardPch.h"
#include "CardDraw.h"

namespace Cards::Paint
{
    // Auto-height measurement. The weather and device cards grow with their
    // content exactly like the WinUI versions (padding plus measured rows); the
    // fixed cards report their constant height.
    float MeasureHeight(Kind kind, const Snapshot& snapshot, const CardEnvironment& environment,
        float widthDip)
    {
        switch (kind)
        {
        case Kind::Weather:
        {
            return WeatherHeight(snapshot, environment, widthDip);
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
