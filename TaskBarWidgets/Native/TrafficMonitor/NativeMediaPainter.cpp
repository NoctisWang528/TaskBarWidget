#include "NativeMediaPainter.h"

#include <algorithm>
#include <cwctype>
#include <cstring>
#include <string_view>

#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "dwrite.lib")
#pragma comment(lib, "windowscodecs.lib")
#pragma comment(lib, "ole32.lib")

using Microsoft::WRL::ComPtr;

namespace NativeMedia
{
    namespace
    {
        constexpr std::wstring_view SearchingFallback = L"\u6b63\u5728\u68c0\u7d22\u6b4c\u8bcd...";
        constexpr std::wstring_view BrowserFallback = L"\u6d4f\u89c8\u5668\u5a92\u4f53";

        DWRITE_FONT_WEIGHT FontWeight(int value)
        {
            const int snapped = std::clamp((value + 50) / 100 * 100, 100, 950);
            return static_cast<DWRITE_FONT_WEIGHT>(snapped);
        }

        DWRITE_TEXT_ALIGNMENT TextAlignment(int alignment)
        {
            if (alignment == 1) return DWRITE_TEXT_ALIGNMENT_CENTER;
            if (alignment == 2) return DWRITE_TEXT_ALIGNMENT_TRAILING;
            return DWRITE_TEXT_ALIGNMENT_LEADING;
        }

        int AlignmentIndex(const std::wstring& alignment)
        {
            if (alignment == L"Center") return 1;
            if (alignment == L"Right") return 2;
            return 0;
        }

        D2D1_COLOR_F WithOpacity(D2D1_COLOR_F color, float opacity)
        {
            color.a = std::clamp(color.a * opacity, 0.0f, 1.0f);
            return color;
        }

        D2D1_COLOR_F MixColor(D2D1_COLOR_F from, D2D1_COLOR_F to, float amount)
        {
            const float t = std::clamp(amount, 0.0f, 1.0f);
            return D2D1::ColorF(
                from.r + (to.r - from.r) * t,
                from.g + (to.g - from.g) * t,
                from.b + (to.b - from.b) * t,
                from.a + (to.a - from.a) * t);
        }

        bool IsTransparent(const std::wstring& value)
        {
            return value.empty() || value == L"transparent" || value == L"none";
        }

        std::wstring FirstCharacter(std::wstring value)
        {
            if (value.empty()) return L"N";
            value = value.substr(0, 1);
            if (value[0] >= L'a' && value[0] <= L'z') value[0] -= 0x20;
            return value;
        }

        std::wstring DisplayLine(const std::wstring& value, std::wstring_view fallback)
        {
            size_t first = 0;
            while (first < value.size() && std::iswspace(value[first])) ++first;
            size_t last = value.size();
            while (last > first && std::iswspace(value[last - 1])) --last;
            if (last == first) return std::wstring(fallback);
            return value.substr(first, last - first);
        }

        float RoundRectRadius(float size)
        {
            return (std::max)(0.0f, (std::min)(size / 2.0f, 999.0f));
        }

        std::uint32_t PackColor(D2D1_COLOR_F color)
        {
            const auto byte = [](float value)
            {
                return static_cast<std::uint32_t>(std::clamp(value, 0.0f, 1.0f) * 255.0f + 0.5f);
            };
            return (byte(color.a) << 24) | (byte(color.r) << 16) |
                (byte(color.g) << 8) | byte(color.b);
        }

        std::vector<unsigned char> DecodeBase64(std::wstring_view text)
        {
            std::vector<unsigned char> result;
            result.reserve(text.size() * 3 / 4 + 4);
            int value = 0;
            int bits = -8;
            for (const wchar_t character : text)
            {
                int digit;
                if (character >= L'A' && character <= L'Z') digit = character - L'A';
                else if (character >= L'a' && character <= L'z') digit = character - L'a' + 26;
                else if (character >= L'0' && character <= L'9') digit = character - L'0' + 52;
                else if (character == L'+') digit = 62;
                else if (character == L'/') digit = 63;
                else if (character == L'=') break;
                else continue;
                value = (value << 6) + digit;
                bits += 6;
                if (bits >= 0)
                {
                    result.push_back(static_cast<unsigned char>((value >> bits) & 0xff));
                    bits -= 8;
                }
            }
            return result;
        }

        bool DataUriBytes(const std::wstring& uri, std::vector<unsigned char>& bytes)
        {
            if (uri.size() < 16 || uri.compare(0, 5, L"data:") != 0) return false;
            const auto comma = uri.find(L',');
            if (comma == std::wstring::npos || comma + 1 >= uri.size()) return false;
            const auto header = uri.substr(0, comma);
            if (header.find(L";base64") == std::wstring::npos) return false;
            bytes = DecodeBase64(std::wstring_view(uri).substr(comma + 1));
            return !bytes.empty();
        }
    }

    bool NativeMediaPainter::Initialize(ID2D1Factory1* factory, ID2D1Device* device,
        IDWriteFactory* writeFactory, std::wstring& error)
    {
        if (!factory || !device || !writeFactory)
        {
            error = L"Native media painter requires Direct2D and DirectWrite devices.";
            return false;
        }
        m_factory = factory;
        m_writeFactory = writeFactory;
        if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
            IID_PPV_ARGS(&m_wic))))
        {
            error = L"Windows Imaging Component could not be created.";
            return false;
        }
        ComPtr<IDWriteTextFormat> ellipsisFormat;
        if (SUCCEEDED(m_writeFactory->CreateTextFormat(L"Segoe UI", nullptr,
            DWRITE_FONT_WEIGHT_NORMAL, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL,
            13.0f, L"", &ellipsisFormat)))
        {
            m_writeFactory->CreateEllipsisTrimmingSign(ellipsisFormat.Get(), &m_ellipsis);
        }
        SetDevice(device);
        return true;
    }

    void NativeMediaPainter::SetDevice(ID2D1Device* device)
    {
        m_device = device;
        m_context.Reset();
        m_brushes.clear();
        m_covers.clear();
        if (m_device) m_device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &m_context);
        if (m_context) m_context->SetDpi(96.0f, 96.0f);
    }

    void NativeMediaPainter::ResetDeviceResources()
    {
        m_context.Reset();
        m_brushes.clear();
        m_covers.clear();
        m_usedCoverKeys.clear();
    }

    D2D1_COLOR_F NativeMediaPainter::ParseColor(const std::wstring& value, D2D1_COLOR_F fallback)
    {
        if (IsTransparent(value)) return D2D1::ColorF(0, 0, 0, 0);
        std::wstring text = value;
        std::transform(text.begin(), text.end(), text.begin(),
            [](wchar_t character) { return static_cast<wchar_t>(std::towlower(character)); });
        auto trim = [](std::wstring& input)
        {
            while (!input.empty() && std::iswspace(input.front())) input.erase(input.begin());
            while (!input.empty() && std::iswspace(input.back())) input.pop_back();
        };
        trim(text);
        if (text == L"white") return D2D1::ColorF(1, 1, 1, 1);
        if (text == L"black") return D2D1::ColorF(0, 0, 0, 1);
        if (!text.empty() && text.front() == L'#')
        {
            try
            {
                const auto hex = text.substr(1);
                unsigned long number = std::stoul(hex, nullptr, 16);
                if (hex.size() == 6)
                {
                    return D2D1::ColorF(((number >> 16) & 0xff) / 255.0f,
                        ((number >> 8) & 0xff) / 255.0f, (number & 0xff) / 255.0f, 1.0f);
                }
                if (hex.size() == 8)
                {
                    return D2D1::ColorF(((number >> 16) & 0xff) / 255.0f,
                        ((number >> 8) & 0xff) / 255.0f, (number & 0xff) / 255.0f,
                        ((number >> 24) & 0xff) / 255.0f);
                }
            }
            catch (...) { return fallback; }
        }
        const auto open = text.find(L'(');
        const auto close = text.rfind(L')');
        if (open != std::wstring::npos && close != std::wstring::npos && close > open)
        {
            std::wstring inside = text.substr(open + 1, close - open - 1);
            float channels[4]{};
            int count = 0;
            size_t start = 0;
            while (start <= inside.size() && count < 4)
            {
                const auto comma = inside.find(L',', start);
                const auto item = inside.substr(start,
                    comma == std::wstring::npos ? std::wstring::npos : comma - start);
                channels[count++] = static_cast<float>(std::wcstod(item.c_str(), nullptr));
                if (comma == std::wstring::npos) break;
                start = comma + 1;
            }
            if (count >= 3)
            {
                const float alpha = count >= 4
                    ? (channels[3] > 1.0f ? channels[3] / 255.0f : channels[3])
                    : 1.0f;
                return D2D1::ColorF(std::clamp(channels[0] / 255.0f, 0.0f, 1.0f),
                    std::clamp(channels[1] / 255.0f, 0.0f, 1.0f),
                    std::clamp(channels[2] / 255.0f, 0.0f, 1.0f),
                    std::clamp(alpha, 0.0f, 1.0f));
            }
        }
        return fallback;
    }

    std::uint64_t NativeMediaPainter::HashText(const std::wstring& value)
    {
        std::uint64_t hash = 1469598103934665603ull;
        for (const wchar_t character : value)
        {
            hash ^= static_cast<std::uint64_t>(character);
            hash *= 1099511628211ull;
        }
        return hash;
    }

    Microsoft::WRL::ComPtr<IDWriteTextFormat> NativeMediaPainter::GetTextFormat(
        const EffectiveStyle& style, float fontSize, int alignment)
    {
        TextFormatKey key{ style.fontFamily, fontSize, style.fontWeight, style.italic,
            style.underline, style.strikeOut, alignment };
        const auto found = m_formats.find(key);
        if (found != m_formats.end()) return found->second;

        ComPtr<IDWriteTextFormat> format;
        const wchar_t* families[] = { style.fontFamily.c_str(), L"Segoe UI Variable Text", L"Segoe UI" };
        for (const auto family : families)
        {
            if (!family || !*family) continue;
            if (SUCCEEDED(m_writeFactory->CreateTextFormat(family, nullptr,
                FontWeight(style.fontWeight),
                style.italic ? DWRITE_FONT_STYLE_ITALIC : DWRITE_FONT_STYLE_NORMAL,
                DWRITE_FONT_STRETCH_NORMAL, (std::max)(1.0f, fontSize), L"", &format)))
                break;
            format.Reset();
        }
        if (!format) return {};
        format->SetTextAlignment(TextAlignment(alignment));
        format->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_NEAR);
        format->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
        if (m_formats.size() >= 48) m_formats.clear();
        m_formats.emplace(key, format);
        return format;
    }

    NativeMediaPainter::TextLayoutEntry* NativeMediaPainter::GetLayout(
        ID2D1DeviceContext* context, const EffectiveStyle& style, const std::wstring& text,
        float fontSize, float boxWidth, bool constrained, int alignment)
    {
        (void)context;
        TextLayoutKey key{ TextFormatKey{ style.fontFamily, fontSize, style.fontWeight,
            style.italic, style.underline, style.strikeOut, constrained ? alignment : 0 },
            text, boxWidth, constrained };
        auto found = m_layouts.find(key);
        if (found == m_layouts.end())
        {
            TextLayoutEntry entry;
            auto naturalFormat = GetTextFormat(style, fontSize, 0);
            if (!naturalFormat) return nullptr;
            if (FAILED(m_writeFactory->CreateTextLayout(text.c_str(),
                static_cast<UINT32>(text.size()), naturalFormat.Get(), 100000.0f, 1000.0f,
                &entry.natural)))
                return nullptr;
            DWRITE_TEXT_METRICS metrics{};
            entry.natural->GetMetrics(&metrics);
            entry.naturalWidth = metrics.widthIncludingTrailingWhitespace > 0
                ? metrics.widthIncludingTrailingWhitespace
                : metrics.width;
            entry.naturalHeight = metrics.height;
            const DWRITE_TEXT_RANGE fullRange{ 0, static_cast<UINT32>(text.size()) };
            if (style.underline) entry.natural->SetUnderline(TRUE, fullRange);
            if (style.strikeOut) entry.natural->SetStrikethrough(TRUE, fullRange);
            if (constrained)
            {
                auto constrainedFormat = GetTextFormat(style, fontSize, alignment);
                if (!constrainedFormat) return nullptr;
                if (FAILED(m_writeFactory->CreateTextLayout(text.c_str(),
                    static_cast<UINT32>(text.size()), constrainedFormat.Get(),
                    (std::max)(1.0f, boxWidth), 1000.0f, &entry.constrained)))
                    return nullptr;
                if (m_ellipsis)
                {
                    DWRITE_TRIMMING trimming{ DWRITE_TRIMMING_GRANULARITY_CHARACTER, 0, 0 };
                    entry.constrained->SetTrimming(&trimming, m_ellipsis.Get());
                }
                if (style.underline) entry.constrained->SetUnderline(TRUE, fullRange);
                if (style.strikeOut) entry.constrained->SetStrikethrough(TRUE, fullRange);
            }
            const auto inserted = m_layouts.emplace(key, std::move(entry));
            inserted.first->second.lastUsed = ++m_paintStamp;
            TrimLayouts();
            found = m_layouts.find(key);
            if (found == m_layouts.end()) return nullptr;
        }
        found->second.lastUsed = ++m_paintStamp;
        m_usedLayouts.insert(key);
        return &found->second;
    }

    ID2D1SolidColorBrush* NativeMediaPainter::GetBrush(ID2D1DeviceContext* context,
        D2D1_COLOR_F color)
    {
        if (!context) return nullptr;
        const BrushKey key{ PackColor(color) };
        const auto found = m_brushes.find(key);
        if (found != m_brushes.end()) return found->second.Get();
        ComPtr<ID2D1SolidColorBrush> brush;
        if (FAILED(context->CreateSolidColorBrush(color, &brush))) return nullptr;
        if (m_brushes.size() >= 32) TrimBrushes();
        return m_brushes.emplace(key, brush).first->second.Get();
    }

    D2D1_COLOR_F NativeMediaPainter::GetColor(const std::wstring& value,
        D2D1_COLOR_F fallback)
    {
        const auto found = m_colors.find(value);
        if (found != m_colors.end()) return found->second;
        if (m_colors.size() >= 32) m_colors.clear();
        const auto color = ParseColor(value, fallback);
        m_colors.emplace(value, color);
        return color;
    }

    void NativeMediaPainter::PaintBackground(ID2D1DeviceContext* context,
        const PaintState& state, float width, float height, float dpiScale)
    {
        if (!context) return;
        const auto layout = CalculateLayout(width, height, dpiScale, state.style,
            state.cover && !state.cover->IsEmpty());
        const auto color = GetColor(state.style.surfaceColor, D2D1::ColorF(0, 0, 0, 0));
        if (color.a <= 0.0001f && !state.style.surfaceShadow) return;
        const auto rect = D2D1::RectF(0, 0, layout.width, layout.height);
        const float radius = static_cast<float>(state.style.surfaceRadiusPx);
        if (color.a > 0.0001f)
            context->FillRoundedRectangle(D2D1::RoundedRect(rect, radius, radius),
                GetBrush(context, color));
        if (state.style.surfaceShadow)
        {
            auto border = D2D1::ColorF(1, 1, 1, 0.16f);
            context->DrawRoundedRectangle(D2D1::RoundedRect(
                D2D1::RectF(rect.left + 0.5f, rect.top + 0.5f, rect.right - 0.5f,
                    rect.bottom - 0.5f), radius, radius),
                GetBrush(context, border), (std::max)(1.0f, dpiScale));
        }
    }

    void NativeMediaPainter::DrawTextLine(ID2D1DeviceContext* context,
        const EffectiveStyle& style, const std::wstring& text, float fontSize, float dpiScale,
        float left, float top, float width, float height, int alignment,
        D2D1_COLOR_F color, float opacity, std::optional<double> scanProgress,
        bool translationPlaceholder)
    {
        if (!context || width <= 0 || height <= 0) return;
        std::wstring value = text;
        if (translationPlaceholder && value.empty()) value = L"\u2026";
        if (value.empty()) value = L" ";
        const bool scan = scanProgress.has_value() && *scanProgress >= 0.0 &&
            *scanProgress <= 1.0;
        const auto* layoutEntry = GetLayout(context, style, value, fontSize, width,
            !scan, alignment);
        if (!layoutEntry) return;
        const auto& layout = scan ? layoutEntry->natural : layoutEntry->constrained;
        if (!layout) return;
        const float layoutHeight = (std::max)(1.0f, layoutEntry->naturalHeight);
        float y = top + (height - layoutHeight) / 2.0f;
        float x = left;
        float scroll = 0;
        if (scan)
            scroll = CalculateHorizontalScroll(layoutEntry->naturalWidth, width,
                static_cast<float>(*scanProgress), dpiScale);
        if (scan && layoutEntry->naturalWidth > width)
            x -= scroll;

        D2D1_RECT_F clipRect = D2D1::RectF(left, top, left + width, top + height);
        context->PushAxisAlignedClip(clipRect, D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
        if (style.textShadow)
        {
            const auto shadow = D2D1::ColorF(0, 0, 0, 0.36f * opacity);
            context->DrawTextLayout(D2D1::Point2F(x, y + (std::max)(1.0f, dpiScale)),
                layout.Get(), GetBrush(context, shadow),
                D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT);
        }
        auto primary = WithOpacity(color, opacity);
        if (!scan)
        {
            context->DrawTextLayout(D2D1::Point2F(x, y), layout.Get(),
                GetBrush(context, primary), D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT);
            context->PopAxisAlignedClip();
            return;
        }

        const float scanX = static_cast<float>(*scanProgress) *
            layoutEntry->naturalWidth - scroll;
        const float feather = (std::min)(fontSize * 0.12f,
            (std::min)(std::abs(scanX), std::abs(width - scanX)));
        auto baseColor = WithOpacity(GetColor(style.secondaryColor,
            D2D1::ColorF(1, 1, 1, 0.6f)), opacity);
        auto overlayColor = WithOpacity(GetColor(style.wordScanOverlayColor,
            D2D1::ColorF(1, 1, 1, 0.75f)), opacity);
        const float visibleScanX = std::clamp(scanX, 0.0f, width);

        if (visibleScanX < width)
        {
            context->PushAxisAlignedClip(D2D1::RectF(left + visibleScanX, top,
                left + width, top + height), D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
            context->DrawTextLayout(D2D1::Point2F(x, y), layout.Get(),
                GetBrush(context, baseColor), D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT);
            context->PopAxisAlignedClip();
        }
        if (visibleScanX > 0)
        {
            const float solidRight = (std::max)(0.0f, visibleScanX - feather);
            if (solidRight > 0)
            {
                context->PushAxisAlignedClip(D2D1::RectF(left, top,
                    left + solidRight, top + height), D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
                context->DrawTextLayout(D2D1::Point2F(x, y), layout.Get(),
                    GetBrush(context, overlayColor), D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT);
                context->PopAxisAlignedClip();
            }
            if (feather > 0.25f)
            {
                D2D1_GRADIENT_STOP stops[2]{
                    { 0.0f, WithOpacity(overlayColor, 0.0f) },
                    { 1.0f, overlayColor }
                };
                ComPtr<ID2D1GradientStopCollection> collection;
                if (SUCCEEDED(context->CreateGradientStopCollection(stops, 2,
                    D2D1_GAMMA_2_2, D2D1_EXTEND_MODE_CLAMP, &collection)))
                {
                    ComPtr<ID2D1LinearGradientBrush> gradient;
                    D2D1_LINEAR_GRADIENT_BRUSH_PROPERTIES properties{
                        D2D1::Point2F(left + visibleScanX - feather, 0),
                        D2D1::Point2F(left + visibleScanX, 0)
                    };
                    if (SUCCEEDED(context->CreateLinearGradientBrush(properties,
                        collection.Get(), &gradient)))
                    {
                        context->PushAxisAlignedClip(D2D1::RectF(
                            left + visibleScanX - feather, top, left + visibleScanX,
                            top + height), D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
                        context->DrawTextLayout(D2D1::Point2F(x, y), layout.Get(),
                            gradient.Get(), D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT);
                        context->PopAxisAlignedClip();
                    }
                }
            }
        }
        context->PopAxisAlignedClip();
    }

    void NativeMediaPainter::PaintLyrics(ID2D1DeviceContext* context, const LyricsPaint& paint)
    {
        if (!context || !paint.state) return;
        const auto& state = *paint.state;
        const auto& style = state.style;
        const auto layout = CalculateLayout(paint.width, paint.height, paint.dpiScale,
            style, state.cover && !state.cover->IsEmpty());
        const auto& frame = state.frame;
        if (frame.isBrowser && frame.browser.active) return;
        if (frame.lyrics.scene == Scene::Spectrum &&
            paint.transitionProgress >= 1.0) return;

        const float row0 = layout.paneTopPadding + static_cast<float>(style.primaryOffsetYPx);
        const float row1 = layout.paneTopPadding + layout.rowHeight + layout.rowGap +
            static_cast<float>(style.secondaryOffsetYPx);
        const float pitch = layout.linePitch;
        const float left = layout.lyricsLayer.left;
        const float width = layout.lyricsLayer.Width();
        const float rowHeight = layout.rowHeight;
        const auto primaryColor = GetColor(style.primaryColor, D2D1::ColorF(1, 1, 1, 0.9f));
        const auto secondaryColor = GetColor(style.secondaryColor, D2D1::ColorF(1, 1, 1, 0.6f));
        const auto translationColor = GetColor(style.translationColor, D2D1::ColorF(1, 1, 1, 0.7f));
        const int alignment = AlignmentIndex(style.textAlignment);
        const bool translationMode = frame.lyrics.translationMode;

        const auto drawPair = [&](const Frame& target, float y0, float opacity,
            float currentSize, float nextSize, bool scanTarget)
        {
            auto current = DisplayLine(target.lyrics.current, SearchingFallback);
            auto secondary = translationMode
                ? DisplayLine(target.lyrics.currentTranslation, L"\u2026")
                : DisplayLine(target.lyrics.next, L" ");
            const auto currentColor = translationMode ? primaryColor : primaryColor;
            const auto secondColor = translationMode ? translationColor : secondaryColor;
            DrawTextLine(context, style, current, currentSize, paint.dpiScale,
                left, y0, width, rowHeight,
                alignment, currentColor, opacity * 0.98f,
                scanTarget ? paint.wordScanProgress : std::nullopt, false);
            DrawTextLine(context, style, secondary, nextSize, paint.dpiScale,
                left, y0 + pitch, width,
                rowHeight, alignment, secondColor, opacity *
                    static_cast<float>(paint.secondaryOpacity) *
                    (translationMode && target.lyrics.currentTranslation.empty()
                        ? 0.55f : 1.0f),
                std::nullopt, translationMode && target.lyrics.currentTranslation.empty());
        };

        context->PushAxisAlignedClip(D2D1::RectF(layout.pane.left,
            layout.pane.top, layout.pane.right, layout.pane.bottom),
            D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
        const bool transition = paint.transitionProgress < 1.0 &&
            paint.transitionKind != TransitionKind::Immediate &&
            paint.transitionFrom != nullptr;
        if (transition && paint.transitionKind == TransitionKind::SingleRoll)
        {
            const float t = static_cast<float>(paint.transitionProgress);
            drawPair(*paint.transitionFrom, row0 - pitch * t, 0.98f - 0.82f * t,
                layout.currentFontSize, layout.nextFontSize, false);
            const float promotedTop = row1 - pitch * t;
            const float incomingTop = row0 + pitch * 2.0f - pitch * t;
            const float promotedSize = layout.nextFontSize +
                (layout.currentFontSize - layout.nextFontSize) * t;
            DrawTextLine(context, style,
                DisplayLine(frame.lyrics.current, SearchingFallback), promotedSize,
                paint.dpiScale,
                left, promotedTop, width, rowHeight, alignment,
                MixColor(secondaryColor, primaryColor, t), 0.72f + 0.26f * t,
                paint.wordScanProgress, false);
            DrawTextLine(context, style,
                DisplayLine(translationMode ? frame.lyrics.currentTranslation
                    : frame.lyrics.next, translationMode ? L"\u2026" : L" "),
                layout.nextFontSize, paint.dpiScale,
                left, incomingTop + (translationMode ? 0.0f : pitch),
                width, rowHeight, alignment,
                translationMode ? translationColor : secondaryColor,
                static_cast<float>(paint.secondaryOpacity) * t, std::nullopt,
                translationMode && frame.lyrics.currentTranslation.empty());
        }
        else if (transition &&
            (paint.transitionKind == TransitionKind::TranslationRoll ||
                paint.transitionKind == TransitionKind::LayerSwitch))
        {
            const float t = static_cast<float>(paint.transitionProgress);
            drawPair(*paint.transitionFrom, row0 - pitch * t * 1.4f, 1.0f - t,
                layout.currentFontSize, layout.nextFontSize, false);
            drawPair(frame, row0 + pitch * (1.0f - t) * 1.4f, t,
                layout.currentFontSize, layout.nextFontSize, true);
        }
        else
        {
            drawPair(frame, row0, 1.0f, layout.currentFontSize,
                layout.nextFontSize, true);
        }
        context->PopAxisAlignedClip();
    }

    void NativeMediaPainter::PaintSpectrum(ID2D1DeviceContext* context,
        const SpectrumPaint& paint)
    {
        if (!context || !paint.state || !paint.bars) return;
        const auto& state = *paint.state;
        const auto& style = state.style;
        const auto layout = CalculateLayout(paint.width, paint.height, paint.dpiScale,
            style, state.cover && !state.cover->IsEmpty());
        const float available = layout.spectrumLayer.Width();
        const auto geometry = CalculateSpectrumGeometry(available, layout.dpiScale,
            style, state.tuning);
        const float scale = static_cast<float>(
            std::clamp(style.layoutScalePercent, 20.0, 300.0) / 100.0);
        const float centerY = (layout.spectrumLayer.top + layout.spectrumLayer.bottom) / 2.0f;
        const auto color = WithOpacity(GetColor(style.primaryColor,
            D2D1::ColorF(1, 1, 1, 0.9f)), static_cast<float>(state.tuning.opacity));
        float x = layout.spectrumLayer.left;
        const int count = (std::min)(geometry.barCount,
            static_cast<int>(paint.bars->size()));
        for (int index = 0; index < count; ++index)
        {
            const float level = static_cast<float>(Clamp01((*paint.bars)[index]));
            const float barHeight = (std::max)(1.0f,
                static_cast<float>(state.tuning.minHeight + level * state.tuning.heightRange) *
                    scale);
            const float top = centerY - barHeight / 2.0f;
            const float radius = geometry.barWidth / 2.0f;
            context->FillRoundedRectangle(D2D1::RoundedRect(
                D2D1::RectF(x, top, x + geometry.barWidth, top + barHeight),
                radius, radius), GetBrush(context, color));
            x += geometry.barWidth + geometry.gap;
        }
    }

    void NativeMediaPainter::PaintCover(ID2D1DeviceContext* context,
        const CoverPaint& paint)
    {
        if (!context || !paint.state || !paint.cover) return;
        const auto& style = paint.state->style;
        const auto layout = CalculateLayout(paint.width, paint.height, paint.dpiScale,
            style, true);
        if (!layout.coverShown) return;
        const auto rect = D2D1::RectF(layout.cover.left, layout.cover.top,
            layout.cover.right, layout.cover.bottom);
        const float radius = static_cast<float>(style.coverCornerRadiusPx);
        const auto fallbackColor = GetColor(paint.cover->fallbackColor,
            D2D1::ColorF(0.12f, 0.16f, 0.22f, 1.0f));
        const bool hasImage = paint.image && paint.image->GetPixelSize().width > 0;
        const bool appIcon = paint.cover->dataUri.empty() &&
            !paint.cover->fallbackIconDataUri.empty();
        if (!appIcon)
            context->FillRoundedRectangle(D2D1::RoundedRect(rect, radius, radius),
                GetBrush(context, fallbackColor));
        if (!hasImage)
        {
            const auto fallbackText = FirstCharacter(
                !paint.cover->fallbackText.empty() ? paint.cover->fallbackText : L"N");
            auto fallbackStyle = style;
            fallbackStyle.fontWeight = 700;
            const auto* layoutEntry = GetLayout(context, fallbackStyle, fallbackText,
                static_cast<float>(style.coverFallbackFontSizePx), rect.right - rect.left,
                true, 1);
            if (layoutEntry && layoutEntry->constrained)
            {
                DWRITE_TEXT_METRICS metrics{};
                layoutEntry->constrained->GetMetrics(&metrics);
                const float textTop = rect.top + (rect.bottom - rect.top - metrics.height) / 2.0f;
                context->DrawTextLayout(D2D1::Point2F(rect.left, textTop),
                    layoutEntry->constrained.Get(),
                    GetBrush(context, D2D1::ColorF(1, 1, 1, 0.96f)),
                    D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT);
            }
            return;
        }

        ComPtr<ID2D1RoundedRectangleGeometry> mask;
        if (SUCCEEDED(m_factory->CreateRoundedRectangleGeometry(
            D2D1::RoundedRect(rect, radius, radius), &mask)))
        {
            const D2D1_LAYER_PARAMETERS1 parameters = D2D1::LayerParameters1(
                D2D1::InfiniteRect(), mask.Get(), D2D1_ANTIALIAS_MODE_PER_PRIMITIVE,
                D2D1::IdentityMatrix(), 1.0f, nullptr, D2D1_LAYER_OPTIONS1_NONE);
            context->PushLayer(parameters, nullptr);
        }
        else
        {
            context->PushAxisAlignedClip(rect, D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
        }
        context->DrawBitmap(paint.image, rect, 1.0f, D2D1_INTERPOLATION_MODE_HIGH_QUALITY_CUBIC,
            nullptr, nullptr);
        if (mask) context->PopLayer();
        else context->PopAxisAlignedClip();
    }

    void NativeMediaPainter::DrawBrowserText(ID2D1DeviceContext* context,
        const EffectiveStyle& style, const std::wstring& text, float left, float top,
        float width, float height, float fontSize, int weight, D2D1_COLOR_F color,
        float opacity)
    {
        if (!context || width <= 0 || height <= 0) return;
        auto textStyle = style;
        textStyle.fontWeight = weight;
        const auto* entry = GetLayout(context, textStyle, text, fontSize, width, true, 0);
        if (!entry || !entry->constrained) return;
        context->PushAxisAlignedClip(D2D1::RectF(left, top, left + width, top + height),
            D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
        context->DrawTextLayout(D2D1::Point2F(left, top), entry->constrained.Get(),
            GetBrush(context, WithOpacity(color, opacity)),
            D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT | D2D1_DRAW_TEXT_OPTIONS_CLIP);
        context->PopAxisAlignedClip();
    }

    void NativeMediaPainter::PaintBrowser(ID2D1DeviceContext* context,
        const BrowserPaint& paint)
    {
        if (!context || !paint.state) return;
        const auto& state = *paint.state;
        const auto& browser = state.frame.browser;
        if (!state.frame.isBrowser || !browser.active) return;
        const auto& style = state.style;
        const auto layout = CalculateLayout(paint.width, paint.height, paint.dpiScale,
            style, false);
        const auto primary = GetColor(style.primaryColor, D2D1::ColorF(1, 1, 1, 0.9f));
        const auto secondary = WithOpacity(
            GetColor(style.secondaryColor, D2D1::ColorF(1, 1, 1, 0.6f)), 0.7f);
        const float metadataWidth = layout.browser.metadata.Width();
        const float titleSize = static_cast<float>(style.fontSizePx);
        const float sourceSize = (std::max)(9.0f, 10.0f * paint.dpiScale);
        const float groupHeight = titleSize + sourceSize;
        const float titleTop = layout.browser.metadata.top +
            (layout.browser.metadata.Height() - groupHeight) / 2.0f;
        DrawBrowserText(context, style,
            browser.title.empty() ? std::wstring(BrowserFallback) : browser.title,
            layout.browser.metadata.left, titleTop, metadataWidth, titleSize,
            titleSize, 600, primary, 1.0f);
        const auto sourceText = browser.artist.empty() ? browser.source : browser.artist;
        DrawBrowserText(context, style, sourceText,
            layout.browser.metadata.left, titleTop + titleSize, metadataWidth, sourceSize,
            sourceSize, 400, secondary, 1.0f);

        const auto fillTriangle = [&](D2D1_POINT_2F first, D2D1_POINT_2F second,
            D2D1_POINT_2F third, D2D1_COLOR_F color)
        {
            ComPtr<ID2D1PathGeometry> geometry;
            ComPtr<ID2D1GeometrySink> sink;
            if (FAILED(m_factory->CreatePathGeometry(&geometry)) ||
                FAILED(geometry->Open(&sink))) return;
            sink->BeginFigure(first, D2D1_FIGURE_BEGIN_FILLED);
            sink->AddLine(second);
            sink->AddLine(third);
            sink->EndFigure(D2D1_FIGURE_END_CLOSED);
            if (SUCCEEDED(sink->Close()))
                context->FillGeometry(geometry.Get(), GetBrush(context, color));
        };
        const auto drawButton = [&](const LayoutRect& rect, int kind, bool enabled)
        {
            const auto color = WithOpacity(primary, enabled ? 1.0f : 0.35f);
            const float centerX = (rect.left + rect.right) / 2.0f;
            const float centerY = (rect.top + rect.bottom) / 2.0f;
            const float half = 4.5f * paint.dpiScale;
            const float bar = 1.8f * paint.dpiScale;
            if (kind == 1)
            {
                context->FillRectangle(D2D1::RectF(centerX + 3.0f * paint.dpiScale - bar,
                    centerY - half, centerX + 3.0f * paint.dpiScale, centerY + half),
                    GetBrush(context, color));
                fillTriangle(D2D1::Point2F(centerX + 2.0f * paint.dpiScale, centerY - half),
                    D2D1::Point2F(centerX + 2.0f * paint.dpiScale, centerY + half),
                    D2D1::Point2F(centerX - 4.0f * paint.dpiScale, centerY), color);
            }
            else if (kind == 2)
            {
                if (browser.isPlaying)
                {
                    const float gap = 1.2f * paint.dpiScale;
                    const float width = 2.6f * paint.dpiScale;
                    context->FillRectangle(D2D1::RectF(centerX - gap - width,
                        centerY - half, centerX - gap, centerY + half),
                        GetBrush(context, color));
                    context->FillRectangle(D2D1::RectF(centerX + gap,
                        centerY - half, centerX + gap + width, centerY + half),
                        GetBrush(context, color));
                }
                else
                {
                    fillTriangle(D2D1::Point2F(centerX - half, centerY - half),
                        D2D1::Point2F(centerX - half, centerY + half),
                        D2D1::Point2F(centerX + half, centerY), color);
                }
            }
            else
            {
                context->FillRectangle(D2D1::RectF(centerX - 3.0f * paint.dpiScale,
                    centerY - half, centerX - 3.0f * paint.dpiScale + bar, centerY + half),
                    GetBrush(context, color));
                fillTriangle(D2D1::Point2F(centerX - 2.0f * paint.dpiScale, centerY - half),
                    D2D1::Point2F(centerX - 2.0f * paint.dpiScale, centerY + half),
                    D2D1::Point2F(centerX + 4.0f * paint.dpiScale, centerY), color);
            }
        };
        drawButton(layout.browser.previous, 1, browser.canPrevious);
        drawButton(layout.browser.playPause, 2, browser.canPlayPause);
        drawButton(layout.browser.next, 3, browser.canNext);
    }

    bool NativeMediaPainter::HitTest(const BrowserPaint& paint, float x, float y,
        HitAction& action) const
    {
        action = HitAction::None;
        if (!paint.state || !paint.state->frame.isBrowser ||
            !paint.state->frame.browser.active) return false;
        const auto layout = CalculateLayout(paint.width, paint.height, paint.dpiScale,
            paint.state->style, false);
        const auto& browser = paint.state->frame.browser;
        if (layout.browser.previous.Contains(x, y))
        {
            if (!browser.canPrevious) return true;
            action = HitAction::Previous;
            return true;
        }
        if (layout.browser.playPause.Contains(x, y))
        {
            if (!browser.canPlayPause) return true;
            action = HitAction::PlayPause;
            return true;
        }
        if (layout.browser.next.Contains(x, y))
        {
            if (!browser.canNext) return true;
            action = HitAction::Next;
            return true;
        }
        return false;
    }

    bool NativeMediaPainter::PrepareCover(const Cover& cover, float dpiScale,
        CoverBitmap& result)
    {
        if (!m_context) return false;
        const bool appIcon = cover.dataUri.empty() && !cover.fallbackIconDataUri.empty();
        const auto& uri = appIcon ? cover.fallbackIconDataUri : cover.dataUri;
        if (uri.empty()) return false;
        auto key = HashText(uri);
        if (appIcon) key ^= 0x9e3779b97f4a7c15ull;
        const auto found = m_covers.find(key);
        if (found != m_covers.end())
        {
            found->second.lastUsed = ++m_paintStamp;
            m_usedCoverKeys.insert(key);
            result.bitmap = found->second.bitmap;
            result.key = key;
            result.appIcon = found->second.appIcon;
            return true;
        }
        std::wstring error;
        auto bitmap = DecodeBitmap(m_context.Get(), m_wic.Get(), uri, appIcon, error);
        if (!bitmap) return false;
        if (m_covers.size() >= 4)
        {
            auto oldest = m_covers.begin();
            for (auto iter = m_covers.begin(); iter != m_covers.end(); ++iter)
                if (iter->second.lastUsed < oldest->second.lastUsed) oldest = iter;
            m_covers.erase(oldest);
        }
        CoverCacheEntry cache;
        cache.bitmap = bitmap;
        cache.lastUsed = ++m_paintStamp;
        cache.appIcon = appIcon;
        m_covers.emplace(key, cache);
        m_usedCoverKeys.insert(key);
        result.bitmap = bitmap;
        result.key = key;
        result.appIcon = appIcon;
        (void)dpiScale;
        return true;
    }

    void NativeMediaPainter::BeginPaintFrame()
    {
        m_usedLayouts.clear();
        m_usedCoverKeys.clear();
    }

    void NativeMediaPainter::EndPaintFrame()
    {
    }

    void NativeMediaPainter::ReleaseInactiveTextLayouts()
    {
        for (auto iter = m_layouts.begin(); iter != m_layouts.end();)
        {
            if (m_usedLayouts.find(iter->first) == m_usedLayouts.end())
                iter = m_layouts.erase(iter);
            else ++iter;
        }
    }

    void NativeMediaPainter::ReleaseCoverBitmapsExcept(
        const std::unordered_set<std::uint64_t>& keys)
    {
        for (auto iter = m_covers.begin(); iter != m_covers.end();)
        {
            if (keys.find(iter->first) == keys.end()) iter = m_covers.erase(iter);
            else ++iter;
        }
    }

    void NativeMediaPainter::TrimLayouts()
    {
        while (m_layouts.size() > 160)
        {
            auto oldest = m_layouts.begin();
            for (auto iter = m_layouts.begin(); iter != m_layouts.end(); ++iter)
                if (iter->second.lastUsed < oldest->second.lastUsed) oldest = iter;
            m_layouts.erase(oldest);
        }
    }

    void NativeMediaPainter::TrimBrushes()
    {
        while (m_brushes.size() >= 32) m_brushes.erase(m_brushes.begin());
    }

    Microsoft::WRL::ComPtr<ID2D1Bitmap1> NativeMediaPainter::DecodeBitmap(
        ID2D1DeviceContext* context, IWICImagingFactory* wic, const std::wstring& uri,
        bool appIcon, std::wstring& error)
    {
        std::vector<unsigned char> bytes;
        if (!DataUriBytes(uri, bytes))
        {
            error = L"Cover data URI is not base64 image data.";
            return {};
        }
        ComPtr<IWICStream> stream;
        ComPtr<IWICBitmapDecoder> decoder;
        ComPtr<IWICBitmapFrameDecode> frame;
        ComPtr<IWICFormatConverter> converter;
        if (FAILED(wic->CreateStream(&stream)) ||
            FAILED(stream->InitializeFromMemory(bytes.data(), static_cast<DWORD>(bytes.size()))) ||
            FAILED(wic->CreateDecoderFromStream(stream.Get(), nullptr,
                WICDecodeMetadataCacheOnLoad, &decoder)) ||
            FAILED(decoder->GetFrame(0, &frame)) ||
            FAILED(wic->CreateFormatConverter(&converter)))
        {
            error = L"Cover image could not be decoded.";
            return {};
        }
        UINT width{}, height{};
        if (FAILED(frame->GetSize(&width, &height)) || width == 0 || height == 0 ||
            width > 2048 || height > 2048 ||
            static_cast<uint64_t>(width) * height > 4096 * 1024)
        {
            error = L"Cover image dimensions are unsupported.";
            return {};
        }
        if (FAILED(converter->Initialize(frame.Get(), GUID_WICPixelFormat32bppPBGRA,
            WICBitmapDitherTypeNone, nullptr, 0.0, WICBitmapPaletteTypeCustom)))
        {
            error = L"Cover image could not be converted.";
            return {};
        }
        ComPtr<ID2D1Bitmap1> bitmap;
        const auto properties = D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_NONE,
            D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED),
            96.0f, 96.0f);
        if (FAILED(context->CreateBitmapFromWicBitmap(converter.Get(), properties, &bitmap)))
        {
            error = L"Cover image could not be uploaded.";
            return {};
        }
        (void)appIcon;
        return bitmap;
    }
}
