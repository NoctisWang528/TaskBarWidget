#include "CardPch.h"
#include "CardIcons.h"

#include <algorithm>
#include <cmath>
#include <cwctype>

using Microsoft::WRL::ComPtr;

namespace Cards
{
    namespace
    {
        constexpr float Pi = 3.14159265358979323846f;

        // Geometry and stroke styles must belong to the caller's D2D factory.
        ComPtr<ID2D1Factory> IconFactory(ID2D1DeviceContext* context)
        {
            ComPtr<ID2D1Factory> factory;
            if (context) context->GetFactory(factory.GetAddressOf());
            return factory;
        }
        ComPtr<ID2D1SolidColorBrush> Solid(ID2D1DeviceContext* context, Color color)
        {
            ComPtr<ID2D1SolidColorBrush> brush;
            if (context)
                context->CreateSolidColorBrush(D2D1::ColorF(color.r, color.g, color.b, color.a),
                    brush.GetAddressOf());
            return brush;
        }

        void FillEllipse(ID2D1DeviceContext* context, Color color, float cx, float cy, float rx, float ry)
        {
            const auto brush = Solid(context, color);
            if (brush) context->FillEllipse(D2D1::Ellipse(D2D1::Point2F(cx, cy), rx, ry), brush.Get());
        }

        void FillRect(ID2D1DeviceContext* context, Color color, D2D1_RECT_F rect, float radius)
        {
            const auto brush = Solid(context, color);
            if (!brush) return;
            if (radius <= 0.0f) context->FillRectangle(rect, brush.Get());
            else context->FillRoundedRectangle(D2D1::RoundedRect(rect, radius, radius), brush.Get());
        }

        template <typename T>
        ComPtr<T> MakeGeometry(ID2D1DeviceContext* context, const std::vector<D2D1_POINT_2F>& points, bool close)
        {
            ComPtr<ID2D1PathGeometry> geometry;
            ComPtr<ID2D1GeometrySink> sink;
            if (!IconFactory(context) || points.size() < 2) return nullptr;
            if (FAILED(IconFactory(context)->CreatePathGeometry(geometry.GetAddressOf()))) return nullptr;
            if (FAILED(geometry->Open(sink.GetAddressOf()))) return nullptr;
            sink->BeginFigure(points.front(), D2D1_FIGURE_BEGIN_FILLED);
            for (size_t index = 1; index < points.size(); ++index) sink->AddLine(points[index]);
            sink->EndFigure(close ? D2D1_FIGURE_END_CLOSED : D2D1_FIGURE_END_OPEN);
            if (FAILED(sink->Close())) return nullptr;
            return geometry;
        }

        void FillGeometry(ID2D1DeviceContext* context, Color color, ID2D1Geometry* geometry)
        {
            const auto brush = Solid(context, color);
            if (brush && geometry) context->FillGeometry(geometry, brush.Get());
        }

        void DrawGeometry(ID2D1DeviceContext* context, Color color, ID2D1Geometry* geometry, float width)
        {
            const auto brush = Solid(context, color);
            if (brush && geometry) context->DrawGeometry(geometry, brush.Get(), width);
        }

        // ---- weather primitives (128 x 128 design space) ----

        void CloudLayer(ID2D1DeviceContext* context, Color color, float ox, float oy, float scale)
        {
            const auto point = [&](float x, float y)
            {
                return D2D1::Point2F(ox + x * scale, oy + y * scale);
            };
            FillEllipse(context, color, point(60.0f, 84.0f).x, point(60.0f, 84.0f).y, 15.0f * scale, 15.0f * scale);
            FillEllipse(context, color, point(84.0f, 74.0f).x, point(84.0f, 74.0f).y, 23.0f * scale, 23.0f * scale);
            FillEllipse(context, color, point(106.0f, 84.0f).x, point(106.0f, 84.0f).y, 16.0f * scale, 16.0f * scale);
            const D2D1_POINT_2F topLeft = point(60.0f, 83.0f);
            const D2D1_POINT_2F bottomRight = point(60.0f + 46.0f, 83.0f + 17.0f);
            FillRect(context, color, D2D1::RectF(topLeft.x, topLeft.y, bottomRight.x, bottomRight.y), 8.5f * scale);
        }

        void AddCloud(ID2D1DeviceContext* context, const WeatherPalette& palette, float ox, float oy)
        {
            CloudLayer(context, palette.cloudShade, ox + 4.0f, oy + 6.0f, 1.0f);
            CloudLayer(context, palette.cloud, ox, oy, 1.0f);
        }

        void AddArc(ID2D1GeometrySink* sink, D2D1_POINT_2F to, float radius,
            D2D1_SWEEP_DIRECTION direction, D2D1_ARC_SIZE size)
        {
            sink->AddArc(D2D1::ArcSegment(to, D2D1::SizeF(radius, radius), 0.0f, direction, size));
        }

        void AddSun(ID2D1DeviceContext* context, const WeatherPalette& palette, float cx, float cy,
            float radius, float rayInner, float rayOuter, float rayWidth)
        {
            ComPtr<ID2D1PathGeometry> rays;
            ComPtr<ID2D1GeometrySink> sink;
            if (IconFactory(context) && SUCCEEDED(IconFactory(context)->CreatePathGeometry(rays.GetAddressOf())) &&
                SUCCEEDED(rays->Open(sink.GetAddressOf())))
            {
                for (int index = 0; index < 8; ++index)
                {
                    const float angle = Pi * static_cast<float>(index) / 4.0f;
                    sink->BeginFigure(D2D1::Point2F(cx + rayInner * std::cos(angle),
                        cy + rayInner * std::sin(angle)), D2D1_FIGURE_BEGIN_HOLLOW);
                    sink->AddLine(D2D1::Point2F(cx + rayOuter * std::cos(angle),
                        cy + rayOuter * std::sin(angle)));
                    sink->EndFigure(D2D1_FIGURE_END_OPEN);
                }
                sink->Close();
                ComPtr<ID2D1StrokeStyle> style;
                const D2D1_STROKE_STYLE_PROPERTIES properties{ D2D1_CAP_STYLE_ROUND, D2D1_CAP_STYLE_ROUND,
                    D2D1_CAP_STYLE_ROUND, D2D1_LINE_JOIN_ROUND, 10.0f, D2D1_DASH_STYLE_SOLID, 0.0f };
                IconFactory(context)->CreateStrokeStyle(properties, nullptr, 0, style.GetAddressOf());
                const auto brush = Solid(context, palette.sun);
                if (brush) context->DrawGeometry(rays.Get(), brush.Get(), rayWidth, style.Get());
            }
            FillEllipse(context, palette.sun, cx, cy, radius, radius);
        }

        void AddMoon(ID2D1DeviceContext* context, const WeatherPalette& palette, float cx, float cy, float radius)
        {
            const auto brush = Solid(context, palette.moon);
            if (!brush || !IconFactory(context)) return;
            ComPtr<ID2D1PathGeometry> geometry;
            ComPtr<ID2D1GeometrySink> sink;
            if (FAILED(IconFactory(context)->CreatePathGeometry(geometry.GetAddressOf()))) return;
            if (FAILED(geometry->Open(sink.GetAddressOf()))) return;
            // Alternate fill: the outer disc minus an offset inner disc forms the
            // crescent. Both figures are closed so the subtraction is exact.
            sink->SetFillMode(D2D1_FILL_MODE_ALTERNATE);
            sink->BeginFigure(D2D1::Point2F(cx - radius, cy), D2D1_FIGURE_BEGIN_FILLED);
            AddArc(sink.Get(), D2D1::Point2F(cx + radius, cy), radius, D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_LARGE);
            AddArc(sink.Get(), D2D1::Point2F(cx - radius, cy), radius, D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_LARGE);
            sink->EndFigure(D2D1_FIGURE_END_CLOSED);
            const float inner = 0.88f * radius;
            const float ix = cx + 0.42f * radius;
            const float iy = cy - 0.34f * radius;
            sink->BeginFigure(D2D1::Point2F(ix - inner, iy), D2D1_FIGURE_BEGIN_FILLED);
            AddArc(sink.Get(), D2D1::Point2F(ix + inner, iy), inner, D2D1_SWEEP_DIRECTION_COUNTER_CLOCKWISE, D2D1_ARC_SIZE_LARGE);
            AddArc(sink.Get(), D2D1::Point2F(ix - inner, iy), inner, D2D1_SWEEP_DIRECTION_COUNTER_CLOCKWISE, D2D1_ARC_SIZE_LARGE);
            sink->EndFigure(D2D1_FIGURE_END_CLOSED);
            if (FAILED(sink->Close())) return;
            context->FillGeometry(geometry.Get(), brush.Get());
        }

        void AddDrop(ID2D1DeviceContext* context, const WeatherPalette& palette, float x, float y, float radius)
        {
            if (!IconFactory(context)) return;
            ComPtr<ID2D1PathGeometry> geometry;
            ComPtr<ID2D1GeometrySink> sink;
            if (FAILED(IconFactory(context)->CreatePathGeometry(geometry.GetAddressOf()))) return;
            if (FAILED(geometry->Open(sink.GetAddressOf()))) return;
            sink->BeginFigure(D2D1::Point2F(x, y), D2D1_FIGURE_BEGIN_FILLED);
            sink->AddBezier(D2D1::BezierSegment(D2D1::Point2F(x + 0.72f * radius, y + 0.94f * radius),
                D2D1::Point2F(x + radius, y + 1.36f * radius),
                D2D1::Point2F(x + radius, y + 1.83f * radius)));
            AddArc(sink.Get(), D2D1::Point2F(x - radius, y + 1.83f * radius), radius,
                D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL);
            sink->AddBezier(D2D1::BezierSegment(D2D1::Point2F(x - radius, y + 1.36f * radius),
                D2D1::Point2F(x - 0.72f * radius, y + 0.94f * radius), D2D1::Point2F(x, y)));
            sink->EndFigure(D2D1_FIGURE_END_CLOSED);
            if (FAILED(sink->Close())) return;
            FillGeometry(context, palette.rain, geometry.Get());
        }

        void AddFlake(ID2D1DeviceContext* context, const WeatherPalette& palette, float x, float y, float radius)
        {
            const auto brush = Solid(context, palette.snow);
            if (!brush) return;
            for (int index = 0; index < 3; ++index)
            {
                const float angle = Pi * static_cast<float>(index) / 3.0f;
                const float dx = radius * std::cos(angle);
                const float dy = radius * std::sin(angle);
                context->DrawLine(D2D1::Point2F(x - dx, y - dy), D2D1::Point2F(x + dx, y + dy),
                    brush.Get(), 2.6f);
            }
        }

        void AddBar(ID2D1DeviceContext* context, const WeatherPalette& palette, float x, float y, float width)
        {
            FillRect(context, palette.fog, D2D1::RectF(x, y, x + width, y + 9.0f), 4.5f);
        }

        void AddDot(ID2D1DeviceContext* context, const WeatherPalette& palette, float x, float y, float radius)
        {
            FillEllipse(context, palette.dust, x, y, radius, radius);
        }

        void AddStar(ID2D1DeviceContext* context, const WeatherPalette& palette, float x, float y, float radius)
        {
            const auto geometry = MakeGeometry<ID2D1PathGeometry>(context, { D2D1::Point2F(x, y - radius),
                D2D1::Point2F(x + 0.34f * radius, y), D2D1::Point2F(x, y + radius),
                D2D1::Point2F(x - 0.34f * radius, y) }, true);
            FillGeometry(context, palette.star, geometry.Get());
        }

        void AddBolt(ID2D1DeviceContext* context, const WeatherPalette& palette)
        {
            const auto geometry = MakeGeometry<ID2D1PathGeometry>(context, { { 70.0f, 90.0f }, { 56.0f, 112.0f },
                { 68.0f, 112.0f }, { 62.0f, 126.0f }, { 84.0f, 102.0f }, { 71.0f, 102.0f },
                { 80.0f, 90.0f } }, true);
            FillGeometry(context, palette.bolt, geometry.Get());
        }

        void AddWind(ID2D1DeviceContext* context, const WeatherPalette& palette, float thickness)
        {
            if (!IconFactory(context)) return;
            const auto brush = Solid(context, palette.fog);
            if (!brush) return;
            ComPtr<ID2D1StrokeStyle> style;
            const D2D1_STROKE_STYLE_PROPERTIES properties{ D2D1_CAP_STYLE_ROUND, D2D1_CAP_STYLE_ROUND,
                D2D1_CAP_STYLE_ROUND, D2D1_LINE_JOIN_ROUND, 10.0f, D2D1_DASH_STYLE_SOLID, 0.0f };
            IconFactory(context)->CreateStrokeStyle(properties, nullptr, 0, style.GetAddressOf());

            ComPtr<ID2D1PathGeometry> geometry;
            ComPtr<ID2D1GeometrySink> sink;
            if (FAILED(IconFactory(context)->CreatePathGeometry(geometry.GetAddressOf())) ||
                FAILED(geometry->Open(sink.GetAddressOf())))
                return;
            sink->BeginFigure(D2D1::Point2F(16.0f, 48.0f), D2D1_FIGURE_BEGIN_HOLLOW);
            sink->AddLine(D2D1::Point2F(78.0f, 48.0f));
            AddArc(sink.Get(), D2D1::Point2F(87.0f, 48.0f), 9.0f, D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL);
            sink->AddLine(D2D1::Point2F(87.0f, 30.0f));
            sink->EndFigure(D2D1_FIGURE_END_OPEN);

            sink->BeginFigure(D2D1::Point2F(30.0f, 72.0f), D2D1_FIGURE_BEGIN_HOLLOW);
            sink->AddLine(D2D1::Point2F(92.0f, 72.0f));
            AddArc(sink.Get(), D2D1::Point2F(101.0f, 72.0f), 9.0f, D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL);
            sink->AddLine(D2D1::Point2F(101.0f, 90.0f));
            sink->EndFigure(D2D1_FIGURE_END_OPEN);

            sink->BeginFigure(D2D1::Point2F(42.0f, 96.0f), D2D1_FIGURE_BEGIN_HOLLOW);
            sink->AddLine(D2D1::Point2F(72.0f, 96.0f));
            sink->EndFigure(D2D1_FIGURE_END_OPEN);
            if (FAILED(sink->Close())) return;
            context->DrawGeometry(geometry.Get(), brush.Get(), thickness, style.Get());
        }

        // ---- control glyphs (20 x 20 design space) ----

        void AddRing(ID2D1DeviceContext* context, Color color, float cx, float cy,
            float outer, float inner, float startDegrees, float endDegrees)
        {
            if (!IconFactory(context)) return;
            ComPtr<ID2D1PathGeometry> geometry;
            ComPtr<ID2D1GeometrySink> sink;
            if (FAILED(IconFactory(context)->CreatePathGeometry(geometry.GetAddressOf())) ||
                FAILED(geometry->Open(sink.GetAddressOf())))
                return;
            const auto point = [&](float radius, float degrees)
            {
                const float radians = degrees * Pi / 180.0f;
                return D2D1::Point2F(cx + radius * std::cos(radians), cy + radius * std::sin(radians));
            };
            const bool large = std::abs(endDegrees - startDegrees) > 180.0f;
            const auto arcSize = large ? D2D1_ARC_SIZE_LARGE : D2D1_ARC_SIZE_SMALL;
            sink->BeginFigure(point(outer, startDegrees), D2D1_FIGURE_BEGIN_FILLED);
            sink->AddArc(D2D1::ArcSegment(point(outer, endDegrees), D2D1::SizeF(outer, outer), 0.0f,
                D2D1_SWEEP_DIRECTION_CLOCKWISE, arcSize));
            sink->AddLine(point(inner, endDegrees));
            sink->AddArc(D2D1::ArcSegment(point(inner, startDegrees), D2D1::SizeF(inner, inner), 0.0f,
                D2D1_SWEEP_DIRECTION_COUNTER_CLOCKWISE, arcSize));
            sink->EndFigure(D2D1_FIGURE_END_CLOSED);
            if (FAILED(sink->Close())) return;
            FillGeometry(context, color, geometry.Get());
        }

        void AddRoundRect(ID2D1DeviceContext* context, Color color, D2D1_RECT_F rect, float radius)
        {
            FillRect(context, color, rect, radius);
        }
    }

    WeatherPalette WeatherColors(bool dark)
    {
        WeatherPalette palette;
        const wchar_t* const* values = nullptr;
        const wchar_t* darkColors[] = { L"#FFFFCB5C", L"#FFE9F2FC", L"#FF6C7F92", L"#FF59ABF0",
            L"#FFE6F1FB", L"#FFFFD166", L"#FF8BA0B2", L"#FFC9A87A", L"#FFFBEEC2", L"#FFFFE08C" };
        const wchar_t* lightColors[] = { L"#FFFFC247", L"#FFF6FBFF", L"#FFA9C2D8", L"#FF3E97DC",
            L"#FFCFE4F6", L"#FFFFC53D", L"#FFB7C7D6", L"#FFD9B98A", L"#FFF7E7B4", L"#FFFFD16B" };
        values = dark ? darkColors : lightColors;
        palette.sun = ParseColor(values[0], Color{});
        palette.cloud = ParseColor(values[1], Color{});
        palette.cloudShade = ParseColor(values[2], Color{});
        palette.rain = ParseColor(values[3], Color{});
        palette.snow = ParseColor(values[4], Color{});
        palette.bolt = ParseColor(values[5], Color{});
        palette.fog = ParseColor(values[6], Color{});
        palette.dust = ParseColor(values[7], Color{});
        palette.moon = ParseColor(values[8], Color{});
        palette.star = ParseColor(values[9], Color{});
        return palette;
    }

    Symbol SymbolFromKind(const std::wstring& kind)
    {
        std::wstring value = kind;
        while (!value.empty() && std::iswspace(value.front())) value.erase(value.begin());
        while (!value.empty() && std::iswspace(value.back())) value.pop_back();
        std::transform(value.begin(), value.end(), value.begin(),
            [](wchar_t character) { return static_cast<wchar_t>(std::towlower(character)); });
        if (value == L"clear" || value == L"sunny") return Symbol::Clear;
        if (value == L"partly-cloudy" || value == L"cloudy" || value == L"few-clouds") return Symbol::PartlyCloudy;
        if (value == L"overcast") return Symbol::Overcast;
        if (value == L"rain" || value == L"shower") return Symbol::Rain;
        if (value == L"drizzle") return Symbol::Drizzle;
        if (value == L"snow") return Symbol::Snow;
        if (value == L"sleet") return Symbol::Sleet;
        if (value == L"fog" || value == L"mist") return Symbol::Fog;
        if (value == L"haze" || value == L"smog") return Symbol::Haze;
        if (value == L"dust" || value == L"sand") return Symbol::Dust;
        if (value == L"thunder" || value == L"thunderstorm") return Symbol::Thunder;
        if (value == L"wind" || value == L"windy") return Symbol::Wind;
        return Symbol::Unknown;
    }

    void DrawWeatherSymbol(ID2D1DeviceContext* context, Symbol symbol, bool night,
        const WeatherPalette& palette, float left, float top, float size)
    {
        if (!context || size <= 0.0f) return;
        D2D1_MATRIX_3X2_F previous{};
        context->GetTransform(&previous);
        const float scale = size / 128.0f;
        context->SetTransform(D2D1::Matrix3x2F::Scale(scale, scale) * D2D1::Matrix3x2F::Translation(left, top) * previous);

        switch (symbol)
        {
        case Symbol::Clear:
            if (night)
            {
                AddMoon(context, palette, 62.0f, 60.0f, 30.0f);
                AddStar(context, palette, 98.0f, 38.0f, 7.0f);
                AddStar(context, palette, 108.0f, 70.0f, 5.5f);
                AddStar(context, palette, 86.0f, 100.0f, 4.5f);
            }
            else
            {
                AddSun(context, palette, 64.0f, 62.0f, 26.0f, 37.0f, 48.0f, 7.0f);
            }
            break;
        case Symbol::PartlyCloudy:
            if (night) AddMoon(context, palette, 46.0f, 40.0f, 22.0f);
            else AddSun(context, palette, 46.0f, 40.0f, 16.0f, 25.0f, 33.0f, 5.5f);
            AddCloud(context, palette, 0.0f, 0.0f);
            break;
        case Symbol::Overcast:
            CloudLayer(context, palette.cloudShade, -10.0f, -26.0f, 0.9f);
            AddCloud(context, palette, 0.0f, 0.0f);
            break;
        case Symbol::Rain:
            AddCloud(context, palette, 0.0f, -10.0f);
            AddDrop(context, palette, 60.0f, 94.0f, 4.2f);
            AddDrop(context, palette, 84.0f, 104.0f, 4.2f);
            AddDrop(context, palette, 108.0f, 94.0f, 4.2f);
            break;
        case Symbol::Drizzle:
            AddCloud(context, palette, 0.0f, -10.0f);
            AddDrop(context, palette, 68.0f, 98.0f, 3.2f);
            AddDrop(context, palette, 96.0f, 106.0f, 3.2f);
            break;
        case Symbol::Snow:
            AddCloud(context, palette, 0.0f, -10.0f);
            AddFlake(context, palette, 60.0f, 102.0f, 5.5f);
            AddFlake(context, palette, 84.0f, 112.0f, 5.5f);
            AddFlake(context, palette, 108.0f, 102.0f, 5.5f);
            break;
        case Symbol::Sleet:
            AddCloud(context, palette, 0.0f, -10.0f);
            AddDrop(context, palette, 62.0f, 98.0f, 3.8f);
            AddFlake(context, palette, 88.0f, 108.0f, 5.0f);
            AddDrop(context, palette, 110.0f, 98.0f, 3.8f);
            break;
        case Symbol::Fog:
            AddCloud(context, palette, 0.0f, -20.0f);
            AddBar(context, palette, 22.0f, 84.0f, 84.0f);
            AddBar(context, palette, 34.0f, 100.0f, 72.0f);
            AddBar(context, palette, 46.0f, 116.0f, 42.0f);
            break;
        case Symbol::Haze:
            AddSun(context, palette, 64.0f, 42.0f, 22.0f, 33.0f, 42.0f, 6.0f);
            AddBar(context, palette, 22.0f, 92.0f, 84.0f);
            AddBar(context, palette, 36.0f, 108.0f, 70.0f);
            break;
        case Symbol::Dust:
        {
            auto dustPalette = palette;
            dustPalette.fog = palette.dust;
            AddWind(context, dustPalette, 6.0f);
            AddDot(context, palette, 30.0f, 108.0f, 3.4f);
            AddDot(context, palette, 62.0f, 118.0f, 2.6f);
            AddDot(context, palette, 96.0f, 108.0f, 3.0f);
            break;
        }
        case Symbol::Thunder:
            AddCloud(context, palette, 0.0f, -14.0f);
            AddBolt(context, palette);
            break;
        case Symbol::Wind:
            AddWind(context, palette, 6.0f);
            break;
        case Symbol::Unknown:
        default:
            AddCloud(context, palette, 0.0f, 0.0f);
            break;
        }
        context->SetTransform(previous);
    }

    void DrawGlyph(ID2D1DeviceContext* context, Glyph glyph, Color color, float left, float top, float size)
    {
        if (!context || size <= 0.0f) return;
        D2D1_MATRIX_3X2_F previous{};
        context->GetTransform(&previous);
        const float scale = size / 20.0f;
        context->SetTransform(D2D1::Matrix3x2F::Scale(scale, scale) * D2D1::Matrix3x2F::Translation(left, top) * previous);

        switch (glyph)
        {
        case Glyph::Previous:
            FillRect(context, color, D2D1::RectF(3.4f, 3.5f, 5.6f, 16.5f), 0.0f);
            FillGeometry(context, color, MakeGeometry<ID2D1PathGeometry>(context, { { 16.8f, 3.4f }, { 16.8f, 16.6f },
                { 7.2f, 10.0f } }, true).Get());
            break;
        case Glyph::Play:
            FillGeometry(context, color, MakeGeometry<ID2D1PathGeometry>(context, { { 6.3f, 3.3f }, { 16.7f, 10.0f },
                { 6.3f, 16.7f } }, true).Get());
            break;
        case Glyph::Pause:
            AddRoundRect(context, color, D2D1::RectF(5.1f, 3.4f, 8.7f, 16.6f), 1.4f);
            AddRoundRect(context, color, D2D1::RectF(11.3f, 3.4f, 14.9f, 16.6f), 1.4f);
            break;
        case Glyph::Next:
            FillGeometry(context, color, MakeGeometry<ID2D1PathGeometry>(context, { { 3.2f, 3.4f }, { 12.8f, 10.0f },
                { 3.2f, 16.6f } }, true).Get());
            FillRect(context, color, D2D1::RectF(14.4f, 3.5f, 16.6f, 16.5f), 0.0f);
            break;
        case Glyph::Speaker:
        case Glyph::SpeakerMuted:
            FillGeometry(context, color, MakeGeometry<ID2D1PathGeometry>(context, { { 1.8f, 7.2f }, { 5.8f, 7.2f },
                { 10.8f, 3.1f }, { 10.8f, 16.9f }, { 5.8f, 12.8f }, { 1.8f, 12.8f } }, true).Get());
            if (glyph == Glyph::Speaker)
            {
                AddRing(context, color, 10.4f, 10.0f, 5.7f, 4.7f, -52.0f, 52.0f);
                AddRing(context, color, 10.4f, 10.0f, 8.1f, 7.1f, -48.0f, 48.0f);
            }
            else
            {
                FillGeometry(context, color, MakeGeometry<ID2D1PathGeometry>(context, { { 13.2f, 6.2f }, { 15.2f, 4.2f },
                    { 18.2f, 7.2f }, { 17.2f, 8.2f }, { 18.2f, 9.2f }, { 15.2f, 12.2f }, { 13.2f, 10.2f },
                    { 14.2f, 9.2f }, { 13.2f, 8.2f }, { 14.2f, 7.2f } }, true).Get());
            }
            break;
        case Glyph::Microphone:
        case Glyph::MicrophoneMuted:
            AddRoundRect(context, color, D2D1::RectF(7.3f, 2.1f, 12.7f, 10.9f), 2.7f);
            AddRing(context, color, 10.0f, 10.1f, 4.7f, 3.8f, 28.0f, 152.0f);
            FillRect(context, color, D2D1::RectF(9.3f, 13.8f, 10.7f, 17.1f), 0.0f);
            AddRoundRect(context, color, D2D1::RectF(6.9f, 16.2f, 13.1f, 17.9f), 0.85f);
            if (glyph == Glyph::MicrophoneMuted)
                FillGeometry(context, color, MakeGeometry<ID2D1PathGeometry>(context, { { 3.2f, 2.7f }, { 4.8f, 2.7f },
                    { 17.2f, 17.3f }, { 15.6f, 17.3f } }, true).Get());
            break;
        case Glyph::Settings:
        {
            std::vector<D2D1_POINT_2F> points;
            for (int index = 0; index < 8; ++index)
            {
                const float angle = static_cast<float>(index) * 45.0f;
                const auto point = [&](float radius, float degrees)
                {
                    const float radians = degrees * Pi / 180.0f;
                    return D2D1::Point2F(10.0f + radius * std::cos(radians),
                        10.0f + radius * std::sin(radians));
                };
                points.push_back(point(7.2f, angle - 22.0f));
                points.push_back(point(9.3f, angle - 13.0f));
                points.push_back(point(9.3f, angle + 13.0f));
                points.push_back(point(7.2f, angle + 22.0f));
            }
            if (!IconFactory(context)) break;
            ComPtr<ID2D1PathGeometry> geometry;
            ComPtr<ID2D1GeometrySink> sink;
            if (FAILED(IconFactory(context)->CreatePathGeometry(geometry.GetAddressOf())) ||
                FAILED(geometry->Open(sink.GetAddressOf())))
                break;
            sink->SetFillMode(D2D1_FILL_MODE_ALTERNATE);
            for (size_t index = 0; index + 3 < points.size(); index += 4)
            {
                sink->BeginFigure(points[index], D2D1_FIGURE_BEGIN_FILLED);
                sink->AddLine(points[index + 1]);
                sink->AddLine(points[index + 2]);
                sink->AddLine(points[index + 3]);
                sink->EndFigure(D2D1_FIGURE_END_CLOSED);
            }
            // The centre hole is subtracted by the alternate fill rule.
            const float hole = 3.15f;
            sink->BeginFigure(D2D1::Point2F(10.0f - hole, 10.0f), D2D1_FIGURE_BEGIN_FILLED);
            AddArc(sink.Get(), D2D1::Point2F(10.0f + hole, 10.0f), hole, D2D1_SWEEP_DIRECTION_COUNTER_CLOCKWISE, D2D1_ARC_SIZE_LARGE);
            AddArc(sink.Get(), D2D1::Point2F(10.0f - hole, 10.0f), hole, D2D1_SWEEP_DIRECTION_COUNTER_CLOCKWISE, D2D1_ARC_SIZE_LARGE);
            sink->EndFigure(D2D1_FIGURE_END_CLOSED);
            if (SUCCEEDED(sink->Close())) FillGeometry(context, color, geometry.Get());
            break;
        }
        case Glyph::Refresh:
            AddRing(context, color, 10.0f, 10.0f, 8.4f, 7.1f, 35.0f, 310.0f);
            FillGeometry(context, color, MakeGeometry<ID2D1PathGeometry>(context, { { 12.5f, 1.4f }, { 12.5f, 7.5f },
                { 18.4f, 4.45f } }, true).Get());
            break;
        case Glyph::Check:
            FillGeometry(context, color, MakeGeometry<ID2D1PathGeometry>(context, { { 4.0f, 10.4f }, { 5.9f, 8.5f },
                { 8.8f, 11.4f }, { 14.4f, 5.6f }, { 16.3f, 7.5f }, { 8.9f, 15.0f } }, true).Get());
            break;
        case Glyph::Chevron:
            FillGeometry(context, color, MakeGeometry<ID2D1PathGeometry>(context, { { 6.2f, 8.2f }, { 10.0f, 12.0f },
                { 13.8f, 8.2f }, { 15.2f, 9.6f }, { 10.0f, 14.8f }, { 4.8f, 9.6f } }, true).Get());
            break;
        }
        context->SetTransform(previous);
    }
}
