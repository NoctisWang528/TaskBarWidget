#include "CardPch.h"
#include "CardIcons.h"
#include "BrandLogos.h"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <cwctype>
#include <d2d1svg.h>
#include <tuple>

using Microsoft::WRL::ComPtr;

namespace Cards
{
    namespace
    {
        constexpr float Pi = 3.14159265358979323846f;

        using BrandLogoKey = std::tuple<float, float, float, float, float, float, int, int>;
        struct LogoCache
        {
            ComPtr<ID2D1Device> device;
            ComPtr<ID2D1DeviceContext> context;
            std::pair<BrandLogoKey, ComPtr<ID2D1Bitmap1>> icons[5];
        };

        LogoCache& PeripheralBrandCache()
        {
            static thread_local LogoCache cache;
            return cache;
        }

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

        D2D1_POINT_2F WeatherVisualCenter(Symbol symbol, bool night)
        {
            // Visible artwork centers in the design space, including rays,
            // precipitation and shadows. Clouds occupy its lower-right area;
            // centering the canvas alone makes forecast columns look staggered.
            switch (symbol)
            {
            case Symbol::Clear: return night ? D2D1::Point2F(71, 64) : D2D1::Point2F(64, 62);
            case Symbol::PartlyCloudy: return night ? D2D1::Point2F(75, 59.5f) : D2D1::Point2F(68, 55);
            case Symbol::Overcast: return D2D1::Point2F(78.25f, 63);
            case Symbol::Rain: return D2D1::Point2F(85.5f, 78.5f);
            case Symbol::Drizzle: return D2D1::Point2F(85.5f, 78);
            case Symbol::Snow: return D2D1::Point2F(85.5f, 79);
            case Symbol::Sleet: return D2D1::Point2F(85.5f, 77);
            case Symbol::Fog: return D2D1::Point2F(74, 78);
            case Symbol::Haze: return D2D1::Point2F(64, 57);
            case Symbol::Dust: return D2D1::Point2F(58.5f, 74);
            case Symbol::Thunder: return D2D1::Point2F(85.5f, 81.5f);
            case Symbol::Wind: return D2D1::Point2F(58.5f, 63);
            default: return D2D1::Point2F(85.5f, 78.5f);
            }
        }

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
        const auto center = WeatherVisualCenter(symbol, night);
        context->SetTransform(D2D1::Matrix3x2F::Translation(64 - center.x, 64 - center.y) *
            D2D1::Matrix3x2F::Scale(scale, scale) * D2D1::Matrix3x2F::Translation(left, top) * previous);

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
        case Glyph::Lightning:
            FillGeometry(context, color, MakeGeometry<ID2D1PathGeometry>(context,
                {{11.5f, 1.5f}, {4.5f, 11.f}, {9.f, 11.f}, {8.f, 18.5f},
                 {15.5f, 8.5f}, {11.f, 8.5f}}, true).Get());
            break;
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

namespace Cards
{
    PeripheralBrand PeripheralBrandFrom(const std::wstring& name, const std::wstring& source,
        const std::wstring& identity)
    {
        if (!identity.empty())
        {
            std::wstring brand = identity;
            std::transform(brand.begin(), brand.end(), brand.begin(),
                [](wchar_t c) { return static_cast<wchar_t>(std::towlower(c)); });
            if (brand == L"asusrog" || brand == L"rog") return PeripheralBrand::Rog;
            if (brand == L"razer") return PeripheralBrand::Razer;
            if (brand == L"logitech") return PeripheralBrand::Logitech;
            if (brand == L"xiaomi") return PeripheralBrand::Xiaomi;
            if (brand == L"apple") return PeripheralBrand::Apple;
            return PeripheralBrand::Unknown;
        }
        const auto identify = [](std::wstring value)
        {
            std::transform(value.begin(), value.end(), value.begin(),
                [](wchar_t c) { return static_cast<wchar_t>(std::towlower(c)); });
            const auto word = [&](const std::wstring& brand)
            {
                // Treat punctuation/underscore as separators, but not letters or digits.
                const auto letterOrDigit = [](wchar_t c)
                { return (c >= L'a' && c <= L'z') || (c >= L'0' && c <= L'9'); };
                for (size_t at = value.find(brand); at != std::wstring::npos; at = value.find(brand, at + 1))
                    if ((at == 0 || !letterOrDigit(value[at - 1])) &&
                        (at + brand.size() == value.size() || !letterOrDigit(value[at + brand.size()])))
                        return true;
                return false;
            };
            if (word(L"rog") || word(L"republic of gamers") || value.find(L"玩家国度") != std::wstring::npos)
                return PeripheralBrand::Rog;
            if (word(L"razer") || value.find(L"雷蛇") != std::wstring::npos) return PeripheralBrand::Razer;
            if (word(L"logitech") || word(L"logi") || word(L"logicool") ||
                value.find(L"罗技") != std::wstring::npos) return PeripheralBrand::Logitech;
            if (word(L"xiaomi") || value.find(L"小米") != std::wstring::npos) return PeripheralBrand::Xiaomi;
            if (word(L"airpods")) return PeripheralBrand::Apple;
            return PeripheralBrand::Unknown;
        };
        const auto brand = identify(name);
        if (brand != PeripheralBrand::Unknown) return brand;
        // These provider IDs are Logitech HID++ protocols, not generic HID/Bluetooth.
        if (source == L"hidpp20" || source == L"hidpp10") return PeripheralBrand::Logitech;
        return identify(source);
    }

    void ClearPeripheralBrandCache()
    {
        PeripheralBrandCache() = {};
    }

    bool DrawPeripheralBrand(ID2D1DeviceContext* context, PeripheralBrand brand,
        Color color, float left, float top, float size)
    {
        if (!context || size <= 0) return false;
        const char* svg = nullptr;
        switch (brand)
        {
        case PeripheralBrand::Rog: svg = BrandLogos::Rog; break;
        case PeripheralBrand::Razer: svg = BrandLogos::Razer; break;
        case PeripheralBrand::Logitech: svg = BrandLogos::Logitech; break;
        case PeripheralBrand::Xiaomi: svg = BrandLogos::Xiaomi; break;
        case PeripheralBrand::Apple: svg = BrandLogos::Apple; break;
        default: return false;
        }
        D2D1_MATRIX_3X2_F transform{};
        context->GetTransform(&transform);
        const float scale = transform._11;
        if (scale <= 0 || transform._22 != scale || transform._12 || transform._21) return false;
        // Like weather symbols, render near (0,0) and retain the small raster at
        // the current DPI. This keeps repeated curve tessellation pixel-stable.
        const double x = static_cast<double>(left) * scale, y = static_cast<double>(top) * scale;
        const int phaseX = static_cast<int>(std::lround((x - std::floor(x)) * 256));
        const int phaseY = static_cast<int>(std::lround((y - std::floor(y)) * 256));
        const BrandLogoKey key{size, scale, color.r, color.g, color.b, color.a, phaseX, phaseY};
        // At most five tiny bitmaps per card thread, cleared by Graphics::Release
        // or device replacement; theme/DPI/subpixel changes replace that brand.
        auto& cache = PeripheralBrandCache();
        ComPtr<ID2D1Device> device;
        context->GetDevice(device.GetAddressOf());
        if (cache.device.Get() != device.Get()) { cache = {}; cache.device = device; }
        auto& entry = cache.icons[static_cast<size_t>(brand) - 1];
        constexpr float padding = 2;
        if (!entry.second || entry.first != key)
        {
            if (!cache.context && FAILED(device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE,
                cache.context.GetAddressOf()))) return false;
            ComPtr<ID2D1DeviceContext5> svgContext;
            ComPtr<IStream> stream;
            if (FAILED(cache.context.As(&svgContext)) ||
                FAILED(CreateStreamOnHGlobal(nullptr, TRUE, stream.GetAddressOf()))) return false;
            const auto length = static_cast<ULONG>(std::strlen(svg));
            ULONG written = 0;
            if (FAILED(stream->Write(svg, length, &written)) || written != length ||
                FAILED(stream->Seek({}, STREAM_SEEK_SET, nullptr))) return false;
            ComPtr<ID2D1SvgDocument> document;
            if (FAILED(svgContext->CreateSvgDocument(stream.Get(), D2D1::SizeF(size, size),
                document.GetAddressOf()))) return false;
            ComPtr<ID2D1SvgElement> root;
            ComPtr<ID2D1SvgPaint> fill;
            document->GetRoot(root.GetAddressOf());
            const auto ink = D2D1::ColorF(color.r, color.g, color.b, color.a);
            if (!root || FAILED(document->CreatePaint(D2D1_SVG_PAINT_TYPE_COLOR, &ink, nullptr,
                fill.GetAddressOf())) || FAILED(root->SetAttributeValue(L"fill", fill.Get()))) return false;
            ComPtr<ID2D1Bitmap1> bitmap;
            const UINT pixels = static_cast<UINT>(std::ceil(size * scale)) + 5;
            if (FAILED(cache.context->CreateBitmap(D2D1::SizeU(pixels, pixels), nullptr, 0,
                D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET,
                    D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED), 96, 96),
                bitmap.GetAddressOf()))) return false;
            cache.context->SetTarget(bitmap.Get());
            cache.context->SetDpi(96, 96);
            cache.context->SetUnitMode(D2D1_UNIT_MODE_DIPS);
            cache.context->SetTransform(D2D1::Matrix3x2F::Scale(scale, scale) *
                D2D1::Matrix3x2F::Translation(padding + phaseX / 256.f, padding + phaseY / 256.f));
            cache.context->BeginDraw();
            cache.context->Clear(D2D1::ColorF(0, 0.f));
            svgContext->DrawSvgDocument(document.Get());
            const auto result = cache.context->EndDraw();
            cache.context->SetTarget(nullptr);
            if (FAILED(result)) return false;
            entry = {key, bitmap};
        }
        const auto pixels = entry.second->GetPixelSize();
        const float drawX = static_cast<float>(std::floor(x)) + transform.dx - padding;
        const float drawY = static_cast<float>(std::floor(y)) + transform.dy - padding;
        context->SetTransform(D2D1::Matrix3x2F::Identity());
        context->DrawBitmap(entry.second.Get(), D2D1::RectF(drawX, drawY, drawX + pixels.width, drawY + pixels.height),
            1, D2D1_BITMAP_INTERPOLATION_MODE_NEAREST_NEIGHBOR);
        context->SetTransform(transform);
        return true;
    }

    void DrawPeripheralIcon(ID2D1DeviceContext* context, const std::wstring& kind,
        Color color, float left, float top, float size)
    {
        if (!context) return;
        Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> brush;
        if (FAILED(context->CreateSolidColorBrush(D2D1::ColorF(color.r, color.g, color.b, color.a), &brush))) return;
        D2D1_MATRIX_3X2_F original{}; context->GetTransform(&original);
        context->SetTransform(D2D1::Matrix3x2F::Scale(size / 24, size / 24) *
            D2D1::Matrix3x2F::Translation(left, top) * original);
        const auto line = [&](float x, float y, float x2, float y2)
        { context->DrawLine({x, y}, {x2, y2}, brush.Get(), 1.6f); };
        const auto box = [&](float x, float y, float x2, float y2, float radius)
        { context->DrawRoundedRectangle(D2D1::RoundedRect({x, y, x2, y2}, radius, radius), brush.Get(), 1.6f); };
        if (kind == L"mouse")
        {
            box(5, 2, 19, 22, 7); line(12, 2, 12, 10); line(5, 10, 19, 10);
        }
        else if (kind == L"keyboard")
        {
            box(1, 5, 23, 20, 2);
            for (float y : {9.f, 13.f}) for (float x : {5.f, 9.f, 13.f, 17.f}) line(x, y, x + 1, y);
            line(7, 17, 17, 17);
        }
        else if (kind == L"headset")
        {
            // Open headband: a closed rounded rectangle incorrectly joins the
            // earcups under the listener's chin. Use the same 1.6-DIP stroke.
            ComPtr<ID2D1PathGeometry> headband;
            ComPtr<ID2D1GeometrySink> sink;
            const auto factory = IconFactory(context);
            if (factory && SUCCEEDED(factory->CreatePathGeometry(headband.GetAddressOf())) &&
                SUCCEEDED(headband->Open(sink.GetAddressOf())))
            {
                sink->BeginFigure({3, 15}, D2D1_FIGURE_BEGIN_HOLLOW);
                sink->AddLine({3, 11});
                sink->AddBezier(D2D1::BezierSegment({3, 6.03f}, {7.03f, 2}, {12, 2}));
                sink->AddBezier(D2D1::BezierSegment({16.97f, 2}, {21, 6.03f}, {21, 11}));
                sink->AddLine({21, 15});
                sink->EndFigure(D2D1_FIGURE_END_OPEN);
                if (SUCCEEDED(sink->Close())) context->DrawGeometry(headband.Get(), brush.Get(), 1.6f);
            }
            box(2, 11, 6, 21, 2); box(18, 11, 22, 21, 2);
        }
        else if (kind == L"earbuds")
        {
            box(3, 3, 10, 11, 3); box(14, 3, 21, 11, 3);
            line(8, 10, 8, 21); line(16, 10, 16, 21);
        }
        else if (kind == L"gamepad")
        {
            box(1, 6, 23, 20, 5); line(7, 10, 7, 16); line(4, 13, 10, 13);
            line(16, 11, 17, 11); line(19, 14, 20, 14);
        }
        else if (kind == L"pen")
        {
            line(4, 20, 7, 13); line(7, 13, 18, 2); line(18, 2, 22, 6);
            line(22, 6, 11, 17); line(11, 17, 4, 20); line(7, 13, 11, 17);
        }
        else { box(3, 6, 21, 20, 2); line(9, 3, 15, 3); line(8, 11, 16, 11); line(12, 8, 12, 15); }
        context->SetTransform(original);
    }
}
