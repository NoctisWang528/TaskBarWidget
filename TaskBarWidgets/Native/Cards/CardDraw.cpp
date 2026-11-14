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
        if (environment.material.kind == MaterialKind::LiquidGlass)
        {
            // Translucent desktop content needs opaque text. Keep hierarchy in
            // size/weight rather than allowing the backdrop through glyphs.
            palette.primary = dark ? Color{1, 1, 1, 1} : Color{.07f, .07f, .07f, 1};
            palette.secondary = dark ? Color{.94f, .94f, .94f, 1} : Color{.14f, .14f, .14f, 1};
        }
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

    namespace
    {
        float GlassHover(const Paint::Interaction& interaction, int id)
        {
            const float value = interaction.Hover(id);
            const float hover = std::isfinite(value) ? std::clamp(value, 0.0f, 1.0f) : 0.0f;
            return interaction.reducedMotion ? (hover > 0.0f ? 1.0f : 0.0f) : hover;
        }

        // Reflections use a small clipped gradient; no effects, backdrop
        // sampling, wall-clock animation or foreground blur belong in painters.
        void GlassSheen(ID2D1DeviceContext* context, D2D1_RECT_F rect, float radius,
            float opacity, bool pressed)
        {
            if (!context || opacity <= 0.0f || rect.right <= rect.left || rect.bottom <= rect.top) return;
            const D2D1_GRADIENT_STOP stops[] = {
                { 0.0f, D2D1::ColorF(1, 1, 1, opacity) },
                { 0.45f, D2D1::ColorF(1, 1, 1, opacity * 0.22f) },
                { 1.0f, D2D1::ColorF(1, 1, 1, 0) },
            };
            ComPtr<ID2D1GradientStopCollection> collection;
            if (FAILED(context->CreateGradientStopCollection(stops, 3, D2D1_GAMMA_2_2,
                D2D1_EXTEND_MODE_CLAMP, collection.GetAddressOf()))) return;
            ComPtr<ID2D1LinearGradientBrush> brush;
            const auto from = D2D1::Point2F(rect.left, pressed ? rect.bottom : rect.top);
            const auto to = D2D1::Point2F(rect.right, pressed ? rect.top : rect.bottom);
            if (SUCCEEDED(context->CreateLinearGradientBrush(D2D1::LinearGradientBrushProperties(from, to),
                collection.Get(), brush.GetAddressOf())))
                context->FillRoundedRectangle(D2D1::RoundedRect(rect, radius, radius), brush.Get());
        }
    }

    void ControlBackground(ID2D1DeviceContext* context, D2D1_RECT_F rect, float radius,
        Color base, Color accent, const Paint::Interaction& interaction, int id, bool enabled)
    {
        if (interaction.glass)
        {
            const float width = rect.right - rect.left, height = rect.bottom - rect.top;
            if (width <= 0.0f || height <= 0.0f) return;
            const float diameter = std::min(width, height);
            // Normalize the visual only; callers retain their original input bounds.
            if (std::max(width, height) <= 40.0f && std::max(width, height) <= diameter * 1.25f)
            {
                const float x = (rect.left + rect.right) / 2, y = (rect.top + rect.bottom) / 2;
                rect = Rect(x - diameter / 2, y - diameter / 2, x + diameter / 2, y + diameter / 2);
            }
            radius = diameter / 2;
        }
        if (interaction.glass && interaction.compositedGlass)
        {
            if (interaction.glassControls)
            {
                GlassControl control;
                control.id = id; control.bounds = rect; control.radius = radius;
                control.enabled = enabled;
                control.hover = enabled ? GlassHover(interaction, id) : 0.0f;
                control.pressed = enabled && interaction.pressed == id;
                control.focused = enabled && interaction.focused == id; control.ink = accent;
                interaction.glassControls->push_back(control);
            }
            return;
        }
        if (base.a > 0.0f) FillRound(context, rect, radius,
            interaction.glass && !enabled ? WithAlpha(base, base.a * 0.55f) : base);
        if (interaction.glass)
        {
            const float hover = enabled ? GlassHover(interaction, id) : 0.0f;
            const bool pressed = enabled && interaction.pressed == id;
            const bool focused = enabled && interaction.focused == id;
            const float emphasis = enabled ? 1.0f : 0.55f;
            // Keep the reflection and lift inside the original control bounds.
            const float inset = pressed ? 1.5f : 1.0f;
            const float lift = interaction.reducedMotion || pressed ? 0.0f : hover * 0.65f;
            const auto surface = Rect(rect.left + inset, rect.top + inset - lift,
                rect.right - inset, rect.bottom - inset - lift);
            const float corner = std::max(0.0f, radius - inset);
            FillRound(context, surface, corner, WithAlpha(accent,
                (pressed ? 0.24f : 0.04f + hover * 0.10f) * emphasis));
            if (pressed) FillRound(context, surface, corner,
                Color{ 0, 0, 0, interaction.dark ? 0.18f : 0.09f });
            const float activity = pressed ? 1.0f : std::max(hover, focused ? 0.65f : 0.0f);
            GlassSheen(context, surface, corner,
                (interaction.dark ? 0.22f : 0.34f) * std::max(activity, 0.18f) * emphasis, pressed);
            StrokeRound(context, surface, corner, Color{ 1, 1, 1,
                ((interaction.dark ? 0.13f : 0.24f) + activity * 0.24f) * emphasis }, 1.0f);
            if (focused) StrokeRound(context, rect, radius, accent, 1.5f);
            return;
        }
        if (!enabled) return;
        const float opacity = interaction.pressed == id ? 0.18f : 0.10f * interaction.Hover(id);
        if (opacity > 0.0f) FillRound(context, rect, radius, WithAlpha(accent, opacity));
        if (interaction.focused == id) StrokeRound(context, rect, radius, accent, 1.5f);
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
        DWRITE_TEXT_ALIGNMENT alignment, bool italic, bool underline, bool strikeout,
        DWRITE_PARAGRAPH_ALIGNMENT vertical)
    {
        if (!context || value.empty() || !graphics.DWrite() ||
            rect.right <= rect.left || rect.bottom <= rect.top) return;
        const auto format = graphics.TextFormat(family, size, weight, alignment, false);
        if (!format) return;
        ComPtr<IDWriteTextLayout> layout;
        if (FAILED(graphics.DWrite()->CreateTextLayout(value.c_str(), static_cast<UINT32>(value.size()),
            format, rect.right - rect.left, rect.bottom - rect.top, layout.GetAddressOf()))) return;
        const DWRITE_TEXT_RANGE range{ 0, static_cast<UINT32>(value.size()) };
        layout->SetParagraphAlignment(vertical);
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
        Color fill, bool knob, bool focused, Color focusColor,
        const Paint::Interaction* interaction, int id)
    {
        ratio = std::clamp(ratio, 0.0f, 1.0f);
        if (knob && interaction && interaction->glass)
        {
            const float y = (rect.top + rect.bottom) / 2;
            rect.top = y - 2.0f; rect.bottom = y + 2.0f;
        }
        if (knob && interaction && interaction->glass && interaction->compositedGlass)
        {
            if (interaction->glassControls)
            {
                const float x = rect.left + (rect.right - rect.left) * ratio;
                const float y = (rect.top + rect.bottom) / 2;
                GlassControl control;
                control.id = id; control.kind = GlassControl::Kind::Thumb;
                control.bounds = Rect(x - 10, y - 7, x + 10, y + 7); control.radius = 7;
                control.track = rect;
                control.ratio = ratio; control.trackColor = track; control.fillColor = fill;
                control.hover = GlassHover(*interaction, id); control.pressed = interaction->pressed == id;
                control.focused = focused; control.ink = focusColor;
                interaction->glassControls->push_back(control);
            }
            return;
        }
        FillRound(context, rect, (rect.bottom - rect.top) / 2.0f, track);
        const float width = (rect.right - rect.left) * ratio;
        if (width > 0.5f)
        {
            FillRound(context, D2D1::RectF(rect.left, rect.top, rect.left + width, rect.bottom),
                (rect.bottom - rect.top) / 2.0f, fill);
        }
        if (focused)
            StrokeRound(context, rect, (rect.bottom - rect.top) / 2.0f, focusColor, 1.5f);
        if (knob)
        {
            const float centerX = rect.left + width;
            const float centerY = (rect.top + rect.bottom) / 2.0f;
            if (interaction && interaction->glass)
            {
                const float hover = GlassHover(*interaction, id);
                const bool pressed = interaction->pressed == id;
                const float activity = pressed ? 1.0f : std::max(hover, focused ? 0.65f : 0.0f);
                const float radius = 7.0f;
                const auto bounds = Rect(centerX - 10, centerY - 7, centerX + 10, centerY + 7);
                // Rim/shadow and a brighter crown distinguish drag from hover.
                FillRound(context, Rect(bounds.left, bounds.top + 1.0f, bounds.right, bounds.bottom + 1.0f),
                    radius, Color{ 0, 0, 0, interaction->dark ? 0.32f : 0.18f });
                FillRound(context, bounds, radius, Mix(fill, Color{ 1, 1, 1, 1 }, pressed ? 0.22f : 0.12f));
                GlassSheen(context, bounds, radius, activity * 0.60f, pressed);
                StrokeRound(context, bounds, radius, Color{ 1, 1, 1, 0.55f + activity * 0.35f }, 1.0f);
                if (activity > 0.0f) StrokeRound(context,
                    Rect(bounds.left - 1.5f, bounds.top - 1.5f, bounds.right + 1.5f, bounds.bottom + 1.5f),
                    radius + 1.5f, WithAlpha(focusColor, 0.20f + activity * 0.45f), 1.0f);
                return;
            }
            const auto brush = Brush(context, fill);
            if (brush)
            {
                context->FillEllipse(D2D1::Ellipse(D2D1::Point2F(centerX, centerY), 5.0f, 5.0f), brush);
                brush->Release();
            }
            if (focused)
            {
                const auto focusBrush = Brush(context, focusColor);
                if (focusBrush)
                {
                    context->DrawEllipse(D2D1::Ellipse(D2D1::Point2F(centerX, centerY), 7.0f, 7.0f),
                        focusBrush, 1.5f);
                    focusBrush->Release();
                }
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
