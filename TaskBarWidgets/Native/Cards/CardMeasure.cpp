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
        UNREFERENCED_PARAMETER(environment);
        UNREFERENCED_PARAMETER(widthDip);
        switch (kind)
        {
        case Kind::Weather:
        {
            float height = Layout::Padding * 2.0f + 78.0f;
            if (!snapshot.weather.forecast.empty()) height += 12.0f + 28.0f + 22.0f;
            if (!snapshot.weather.alerts.empty()) height += 54.0f;
            if (!snapshot.weather.message.empty()) height += 16.0f;
            return std::max(Layout::HeightWeather, height);
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
