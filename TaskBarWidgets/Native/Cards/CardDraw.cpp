#include "CardPch.h"
#include "CardDraw.h"

using Microsoft::WRL::ComPtr;

namespace Cards::Draw
{
    Palette Colors(const CardEnvironment& environment)
    {
        Palette palette;
        if (environment.highContrast)
        {
            const COLORREF window = GetSysColor(COLOR_WINDOW);
            const COLORREF text = GetSysColor(COLOR_WINDOWTEXT);
            const COLORREF highlight = GetSysColor(COLOR_HIGHLIGHT);
            palette.primary = Color{ GetRValue(text) / 255.0f, GetGValue(text) / 255.0f,
                GetBValue(text) / 255.0f, 1.0f };
            palette.secondary = WithAlpha(palette.primary, 0.78f);
            palette.accent = Color{ GetRValue(highlight) / 255.0f, GetGValue(highlight) / 255.0f,
                GetBValue(highlight) / 255.0f, 1.0f };
            palette.border = WithAlpha(palette.primary, 0.35f);
            palette.panel = Color{ GetRValue(window) / 255.0f, GetGValue(window) / 255.0f,
                GetBValue(window) / 255.0f, 1.0f };
            palette.track = WithAlpha(palette.primary, 0.16f);
            palette.fill = WithAlpha(palette.primary, 0.10f);
            return palette;
        }
        const bool dark = environment.dark;
        palette.primary = ParseColor(DefaultPrimaryColor(dark), Color{});
        palette.secondary = ParseColor(DefaultSecondaryColor(dark), Color{});
        palette.accent = ParseColor(DefaultScanColor(dark), Color{});
        palette.border = ParseColor(dark ? L"#26FFFFFF" : L"#1A000000", Color{});
        palette.panel = ParseColor(dark ? L"#1FFFFFFF" : L"#0F000000", Color{});
        palette.track = ParseColor(dark ? L"#33FFFFFF" : L"#33000000", Color{});
        palette.fill = ParseColor(dark ? L"#14FFFFFF" : L"#0A000000", Color{});
        return palette;
    }

    ID2D1SolidColorBrush* Brush(ID2D1DeviceContext* context, Color color)
    {
        if (!context) return nullptr;
        ID2D1SolidColorBrush* brush = nullptr;
        const D2D1_COLOR_F value = D2D1::ColorF(color.r, color.g, color.b, color.a);
        if (FAILED(context->CreateSolidColorBrush(value, &brush))) return nullptr;
        return brush;
    }

    D2D1_RECT_F Rect(float left, float top, float right, float bottom)
    {
        return D2D1::RectF(left, top, right, bottom);
    }

    void FillRect(ID2D1DeviceContext* context, D2D1_RECT_F rect, Color color)
    {
        const auto brush = Brush(context, color);
        if (!brush) return;
        context->FillRectangle(rect, brush);
        brush->Release();
    }

    void FillRound(ID2D1DeviceContext* context, D2D1_RECT_F rect, float radius, Color color)
    {
        const auto brush = Brush(context, color);
        if (!brush) return;
        context->FillRoundedRectangle(D2D1::RoundedRect(rect, radius, radius), brush);
        brush->Release();
    }

    void StrokeRound(ID2D1DeviceContext* context, D2D1_RECT_F rect, float radius, Color color, float width)
    {
        const auto brush = Brush(context, color);
        if (!brush) return;
        context->DrawRoundedRectangle(D2D1::RoundedRect(rect, radius, radius), brush, width);
        brush->Release();
    }

    void Line(ID2D1DeviceContext* context, D2D1_POINT_2F from, D2D1_POINT_2F to, Color color, float width)
    {
        const auto brush = Brush(context, color);
        if (!brush) return;
        context->DrawLine(from, to, brush, width);
        brush->Release();
    }

    void Text(ID2D1DeviceContext* context, Graphics& graphics, const std::wstring& value,
        const std::wstring& family, float size, int weight, Color color, D2D1_RECT_F rect,
        DWRITE_TEXT_ALIGNMENT alignment, bool italic, bool underline, bool strikeout)
    {
        if (!context || value.empty() || !graphics.DWrite() ||
            rect.right <= rect.left || rect.bottom <= rect.top) return;
        const auto format = graphics.TextFormat(family, size, weight, alignment, false);
        if (!format) return;
        ComPtr<IDWriteTextLayout> layout;
        if (FAILED(graphics.DWrite()->CreateTextLayout(value.c_str(), static_cast<UINT32>(value.size()),
            format, rect.right - rect.left, rect.bottom - rect.top, layout.GetAddressOf()))) return;
        const DWRITE_TEXT_RANGE range{ 0, static_cast<UINT32>(value.size()) };
        layout->SetFontStyle(italic ? DWRITE_FONT_STYLE_ITALIC : DWRITE_FONT_STYLE_NORMAL, range);
        layout->SetUnderline(underline, range);
        layout->SetStrikethrough(strikeout, range);
        ComPtr<IDWriteInlineObject> ellipsis;
        if (SUCCEEDED(graphics.DWrite()->CreateEllipsisTrimmingSign(format, ellipsis.GetAddressOf())))
        {
            const DWRITE_TRIMMING trimming{ DWRITE_TRIMMING_GRANULARITY_CHARACTER, 0, 0 };
            layout->SetTrimming(&trimming, ellipsis.Get());
        }
        const auto brush = Brush(context, color);
        if (!brush) return;
        context->DrawTextLayout(D2D1::Point2F(rect.left, rect.top), layout.Get(), brush,
            D2D1_DRAW_TEXT_OPTIONS_CLIP);
        brush->Release();
    }

    std::wstring Ellipsize(ID2D1DeviceContext* context, Graphics& graphics, const std::wstring& value,
        const std::wstring& family, float size, int weight, float maxWidth)
    {
        if (!context || !graphics.DWrite() || value.empty() || maxWidth <= 0.0f) return value;
        const auto format = graphics.TextFormat(family, size, weight, DWRITE_TEXT_ALIGNMENT_LEADING, false);
        if (!format) return value;
        const auto fits = [&](const std::wstring& candidate)
        {
            ComPtr<IDWriteTextLayout> layout;
            if (FAILED(graphics.DWrite()->CreateTextLayout(candidate.c_str(),
                static_cast<UINT32>(candidate.size()), format, maxWidth, 1e6f, layout.GetAddressOf())))
                return true;
            DWRITE_TEXT_METRICS metrics{};
            if (FAILED(layout->GetMetrics(&metrics))) return true;
            return metrics.widthIncludingTrailingWhitespace <= maxWidth;
        };
        if (fits(value)) return value;
        const std::wstring ellipsis = L"\u2026";
        size_t low = 0;
        size_t high = value.size();
        while (low < high)
        {
            const size_t middle = (low + high + 1) / 2;
            if (fits(value.substr(0, middle) + ellipsis)) low = middle;
            else high = middle - 1;
        }
        // Do not cut between the two UTF-16 code units of a supplementary character.
        if (low > 0 && low < value.size() && value[low - 1] >= 0xD800 &&
            value[low - 1] <= 0xDBFF && value[low] >= 0xDC00 && value[low] <= 0xDFFF) --low;
        return value.substr(0, low) + ellipsis;
    }

    void Pill(ID2D1DeviceContext* context, D2D1_RECT_F rect, Color color)
    {
        FillRound(context, rect, (rect.bottom - rect.top) / 2.0f, color);
    }

    void Progress(ID2D1DeviceContext* context, D2D1_RECT_F rect, float ratio, Color track,
        Color fill, bool knob)
    {
        ratio = std::clamp(ratio, 0.0f, 1.0f);
        FillRound(context, rect, (rect.bottom - rect.top) / 2.0f, track);
        const float width = (rect.right - rect.left) * ratio;
        if (width > 0.5f)
        {
            FillRound(context, D2D1::RectF(rect.left, rect.top, rect.left + width, rect.bottom),
                (rect.bottom - rect.top) / 2.0f, fill);
        }
        if (knob)
        {
            const float centerX = rect.left + width;
            const float centerY = (rect.top + rect.bottom) / 2.0f;
            const auto brush = Brush(context, fill);
            if (brush)
            {
                context->FillEllipse(D2D1::Ellipse(D2D1::Point2F(centerX, centerY), 5.0f, 5.0f), brush);
                brush->Release();
            }
        }
    }

    int AddButton(Layout::Table& regions, int id, D2D1_RECT_F rect, const std::wstring& action,
        const std::wstring& name, bool enabled, bool focusable)
    {
        Layout::Region region;
        region.id = id;
        region.type = Layout::RegionType::Button;
        region.bounds = RECT{ static_cast<LONG>(rect.left), static_cast<LONG>(rect.top),
            static_cast<LONG>(std::ceil(rect.right)), static_cast<LONG>(std::ceil(rect.bottom)) };
        region.action = action;
        region.name = name;
        region.enabled = enabled;
        region.focusable = focusable;
        regions.Add(std::move(region));
        return id;
    }

    int AddSlider(Layout::Table& regions, int id, D2D1_RECT_F rect, const std::wstring& action,
        const std::wstring& name, bool enabled)
    {
        Layout::Region region;
        region.id = id;
        region.type = Layout::RegionType::Slider;
        region.bounds = RECT{ static_cast<LONG>(rect.left), static_cast<LONG>(rect.top),
            static_cast<LONG>(std::ceil(rect.right)), static_cast<LONG>(std::ceil(rect.bottom)) };
        region.action = action;
        region.name = name;
        region.enabled = enabled;
        regions.Add(std::move(region));
        return id;
    }

    int AddCell(Layout::Table& regions, int id, D2D1_RECT_F rect, const std::wstring& action,
        const std::wstring& name, bool enabled, bool selected)
    {
        Layout::Region region;
        region.id = id;
        region.type = Layout::RegionType::Cell;
        region.bounds = RECT{ static_cast<LONG>(rect.left), static_cast<LONG>(rect.top),
            static_cast<LONG>(std::ceil(rect.right)), static_cast<LONG>(std::ceil(rect.bottom)) };
        region.action = action;
        region.name = name;
        region.enabled = enabled;
        region.selected = selected;
        regions.Add(std::move(region));
        return id;
    }

    int AddText(Layout::Table& regions, int id, D2D1_RECT_F rect, const std::wstring& name)
    {
        Layout::Region region;
        region.id = id;
        region.type = Layout::RegionType::Text;
        region.bounds = RECT{ static_cast<LONG>(rect.left), static_cast<LONG>(rect.top),
            static_cast<LONG>(std::ceil(rect.right)), static_cast<LONG>(std::ceil(rect.bottom)) };
        region.name = name;
        region.focusable = false;
        regions.Add(std::move(region));
        return id;
    }
}
