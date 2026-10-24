#include "CardPch.h"
#include "CardDraw.h"
#include "CardWindow.h"

namespace Cards::Paint
{
    namespace
    {
        constexpr float Cover{ 64.0f };
        constexpr float Gap{ 12.0f };
        constexpr float LyricHeight{ 46.0f };

        std::wstring SourceLabel(const Snapshot& snapshot)
        {
            for (const auto& source : snapshot.media.sources)
                if (source.id == snapshot.media.selectedSourceId) return source.label;
            return snapshot.media.source.empty() ? L"暂无播放来源" : snapshot.media.source;
        }

        void DrawTransport(ID2D1DeviceContext* context, Graphics& graphics, const Draw::Palette& palette,
            const CardEnvironment& environment, const Snapshot& snapshot, Layout::Table& regions,
            float centerX, float top)
        {
            const float radius = 21.0f;
            const float smallSize = 16.0f;
            const struct { float offset; Glyph glyph; const wchar_t* action; const wchar_t* name; bool enabled; } buttons[] = {
                { -62.0f, Glyph::Previous, L"previous", L"上一首", snapshot.media.canPrevious },
                { 0.0f, snapshot.media.isPlaying ? Glyph::Pause : Glyph::Play, L"playPause", L"播放/暂停", snapshot.media.canPlayPause },
                { 62.0f, Glyph::Next, L"next", L"下一首", snapshot.media.canNext },
            };
            for (const auto& button : buttons)
            {
                const bool primary = button.offset == 0.0f;
                const float size = primary ? radius * 2.0f : 32.0f;
                const float left = centerX + button.offset - size / 2.0f;
                const D2D1_RECT_F rect = Draw::Rect(left, top, left + size, top + size);
                Draw::FillRound(context, rect, size / 2.0f,
                    primary ? WithAlpha(palette.primary, 0.14f) : WithAlpha(palette.primary, 0.06f));
                const float glyphSize = primary ? 20.0f : smallSize;
                DrawGlyph(context, button.glyph, WithAlpha(palette.primary, button.enabled ? 1.0f : 0.4f),
                    left + (size - glyphSize) / 2.0f, top + (size - glyphSize) / 2.0f, glyphSize);
                Draw::AddButton(regions, 100 + static_cast<int>(button.offset), rect, button.action,
                    button.name, button.enabled);
            }
        }

        void DrawLyrics(ID2D1DeviceContext* context, Graphics& graphics, const Draw::Palette& palette,
            const CardEnvironment& environment, const Snapshot& snapshot,
            const LyricTransition& transition, float left, float top, float width)
        {
            const auto& lyrics = snapshot.lyrics;
            const bool displayable = lyrics.available && !lyrics.isPureMusic &&
                (!lyrics.current.empty() || !lyrics.next.empty() || lyrics.currentLineIndex >= 0);
            std::wstring main;
            std::wstring secondary;
            if (!displayable)
            {
                if (snapshot.media.title.empty()) main = L"等待播放...";
                else if (lyrics.scene == L"searching") main = L"正在检索歌词...";
                else main = L"暂未找到歌词";
            }
            else
            {
                main = lyrics.current;
                secondary = lyrics.translationMode
                    ? (lyrics.currentTranslation.empty() ? L"\u2026" : lyrics.currentTranslation) : lyrics.next;
            }
            const float layoutScale = std::clamp(static_cast<float>(lyrics.layoutScalePercent) / 100.0f,
                0.25f, 4.0f);
            const float requestedSize = std::clamp(static_cast<float>(lyrics.fontSize), 5.0f, 72.0f);
            const bool twoRows = displayable && !secondary.empty() && LyricHeight >= requestedSize * 2.4f;
            const float rowHeight = LyricHeight / (twoRows ? 2.0f : 1.0f);
            const float mainSize = std::min(requestedSize, std::max(11.2f * layoutScale, rowHeight * 0.92f));
            const float secondarySize = std::max(9.0f * layoutScale, mainSize * 0.92f);
            const std::wstring family = lyrics.fontFamily.empty() ? environment.fontFamily : lyrics.fontFamily;
            const Color primary = environment.highContrast ? palette.primary
                : ParseColor(lyrics.primaryColor, palette.primary);
            Color secondaryColor = environment.highContrast ? palette.primary
                : ParseColor(lyrics.translationMode ? lyrics.translationColor : lyrics.secondaryColor,
                    palette.secondary);
            if (!environment.highContrast && lyrics.translationMode && lyrics.currentTranslation.empty())
                secondaryColor.a *= 0.55f;
            Color shadowColor = WithAlpha(Color{ 0, 0, 0, 1 }, 0.36f);
            if (lyrics.textShadow.empty() || lyrics.textShadow == L"none" || environment.highContrast)
                shadowColor.a = 0.0f;

            const auto row = [&](const std::wstring& text, float size, Color color, float rowTop,
                float scan)
            {
                if (text.empty() || width <= 0.0f || !graphics.DWrite()) return;
                const auto format = graphics.TextFormat(family, size, lyrics.fontWeight,
                    DWRITE_TEXT_ALIGNMENT_LEADING, false);
                if (!format) return;
                winrt::com_ptr<IDWriteTextLayout> layout;
                if (FAILED(graphics.DWrite()->CreateTextLayout(text.c_str(),
                    static_cast<UINT32>(text.size()), format, width, rowHeight, layout.put()))) return;
                layout->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
                // Apply before measuring: italic glyph widths must drive both
                // alignment/scrolling and the scan clip, without changing cached formats.
                const DWRITE_TEXT_RANGE range{ 0, static_cast<UINT32>(text.size()) };
                layout->SetFontStyle(lyrics.fontItalic ? DWRITE_FONT_STYLE_ITALIC : DWRITE_FONT_STYLE_NORMAL, range);
                layout->SetUnderline(lyrics.fontUnderline, range);
                layout->SetStrikethrough(lyrics.fontStrikeOut, range);
                DWRITE_TEXT_METRICS metrics{};
                if (FAILED(layout->GetMetrics(&metrics))) return;
                const float textWidth = metrics.widthIncludingTrailingWhitespace;
                const bool scanning = std::isfinite(scan) && scan >= 0.0f;
                const float progress = scanning ? std::clamp(scan, 0.0f, 1.0f) : 0.0f;
                const float overflow = std::max(0.0f, textWidth - width);
                const float shift = scanning ? -std::clamp(progress * textWidth - width * 0.65f,
                    0.0f, overflow) : 0.0f;
                const float alignedLeft = lyrics.textAlignment == L"Right" ? std::max(0.0f, width - textWidth)
                    : lyrics.textAlignment == L"Center" ? std::max(0.0f, (width - textWidth) / 2.0f) : 0.0f;
                const float textLeft = left + (overflow > 0.5f ? shift : alignedLeft);
                // Keep one measured layout for the base, shadow and scan. Scan width
                // belongs to the full line, not to the viewport or an ellipsized copy.
                if (!scanning)
                {
                    winrt::com_ptr<IDWriteInlineObject> ellipsis;
                    if (SUCCEEDED(graphics.DWrite()->CreateEllipsisTrimmingSign(format, ellipsis.put())))
                    {
                        const DWRITE_TRIMMING trimming{ DWRITE_TRIMMING_GRANULARITY_CHARACTER, 0, 0 };
                        layout->SetTrimming(&trimming, ellipsis.get());
                    }
                }
                const auto draw = [&](Color ink, float y)
                {
                    const auto brush = graphics.Brush(context, ink);
                    if (brush) context->DrawTextLayout(D2D1::Point2F(textLeft, y), layout.get(), brush.Get());
                };
                const auto rect = Draw::Rect(left, rowTop, left + width, rowTop + rowHeight);
                context->PushAxisAlignedClip(rect, D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
                if (shadowColor.a > 0.0f) draw(shadowColor, rowTop + 1.0f);
                draw(scanning && !environment.highContrast
                    ? ParseColor(lyrics.secondaryColor, palette.secondary) : color, rowTop);
                if (scanning && progress > 0.0f)
                {
                    const float scanRight = std::clamp(textLeft + textWidth * progress, left, left + width);
                    context->PushAxisAlignedClip(Draw::Rect(left, rowTop, scanRight, rect.bottom),
                        D2D1_ANTIALIAS_MODE_ALIASED);
                    draw(environment.highContrast ? palette.primary : ParseColor(lyrics.scanColor, palette.accent), rowTop);
                    context->PopAxisAlignedClip();
                }
                context->PopAxisAlignedClip();
            };

            // Clip the rolling rows so they cannot paint over metadata or transport.
            const float wrap = environment.reducedMotion || !lyrics.animateTransition ? 1.0f
                : std::clamp(transition.progress, 0.0f, 1.0f);
            context->PushAxisAlignedClip(Draw::Rect(left, top, left + width, top + LyricHeight),
                D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
            if (wrap < 0.999f && !transition.previous.empty())
            {
                const float shift = wrap * rowHeight;
                const float alpha = 1.0f - wrap;
                row(transition.previous, mainSize, WithAlpha(primary, primary.a * alpha),
                    top - shift, -1.0f);
                if (twoRows && !transition.previousSecondary.empty())
                    row(transition.previousSecondary, secondarySize,
                        WithAlpha(secondaryColor, secondaryColor.a * alpha), top + rowHeight - shift, -1.0f);
            }
            const float enter = (1.0f - wrap) * rowHeight;
            row(main, mainSize, WithAlpha(primary, primary.a * (0.35f + 0.65f * wrap)), top + enter,
                displayable ? static_cast<float>(lyrics.wordScan) : -1.0f);
            if (twoRows && !secondary.empty()) row(secondary, secondarySize,
                WithAlpha(secondaryColor, secondaryColor.a * (0.35f + 0.65f * wrap)),
                top + rowHeight + enter, -1.0f);
            context->PopAxisAlignedClip();
        }
    }

    void Media(ID2D1DeviceContext* context, const Snapshot& snapshot, const CoverTransition& transition,
        const LyricTransition& lyricTransition, const CardEnvironment& environment,
        Layout::Table& regions, float widthDip, float heightDip)
    {
        if (!context || !environment.graphics) return;
        Graphics& graphics = *environment.graphics;
        const Draw::Palette palette = Draw::Colors(environment);
        const float left = Layout::Padding;
        const float top = Layout::Padding;
        const float contentWidth = widthDip - Layout::Padding * 2.0f;

        // Cover art: current bitmap decoded at display size for this DPI.
        const D2D1_RECT_F cover = Draw::Rect(left, top + 5.0f, left + Cover, top + 5.0f + Cover);
        Draw::FillRound(context, cover, 8.0f, WithAlpha(palette.primary, 0.07f));
        if (transition.previous && transition.previousOpacity > 0.01f)
        {
            context->PushAxisAlignedClip(cover, D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
            context->DrawBitmap(transition.previous, cover, transition.previousOpacity,
                D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);
            context->PopAxisAlignedClip();
        }
        if (auto* bitmap = graphics.DecodeImage(snapshot.media.coverDataUrl,
            static_cast<int>(Cover * 2.0f), static_cast<int>(Cover * 2.0f)))
        {
            context->PushAxisAlignedClip(cover, D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
            context->DrawBitmap(bitmap, cover, 1.0f, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);
            context->PopAxisAlignedClip();
        }
        else
        {
            Draw::Text(context, graphics, L"\u266B", environment.fontFamily, 27.0f, 400,
                WithAlpha(palette.primary, 0.45f), cover, DWRITE_TEXT_ALIGNMENT_CENTER,
                false, false, false, DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
        }
        Draw::AddButton(regions, 1, cover, L"cover", L"封面", false, false);

        // Metadata column.
        const float metadataLeft = left + Cover + Gap;
        const float metadataWidth = contentWidth - Cover - Gap - 44.0f;
        Draw::Text(context, graphics, snapshot.media.title.empty() ? L"未在播放" : snapshot.media.title,
            environment.fontFamily, 15.0f, 600, palette.primary,
            Draw::Rect(metadataLeft, top, metadataLeft + metadataWidth,
                top + (snapshot.media.artist.empty() ? 40.0f : 22.0f)),
            DWRITE_TEXT_ALIGNMENT_LEADING, false, false, false, DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
        Draw::Text(context, graphics, snapshot.media.artist, environment.fontFamily, 12.0f, 400,
            palette.secondary, Draw::Rect(metadataLeft, top + 22.0f, metadataLeft + metadataWidth, top + 40.0f),
            DWRITE_TEXT_ALIGNMENT_LEADING, false, false, false, DWRITE_PARAGRAPH_ALIGNMENT_CENTER);

        // Source selector with its icon and chevron.
        const float sourceTop = top + 46.0f;
        const D2D1_RECT_F sourceRect = Draw::Rect(metadataLeft, sourceTop,
            metadataLeft + metadataWidth, sourceTop + 34.0f);
        Draw::FillRound(context, sourceRect, 8.0f, WithAlpha(palette.primary, 0.06f));
        Draw::AddButton(regions, 2, sourceRect, L"showSources", L"选择播放来源",
            !snapshot.media.sources.empty());
        float iconLeft = metadataLeft + 8.0f;
        for (const auto& source : snapshot.media.sources)
        {
            if (source.id != snapshot.media.selectedSourceId || source.iconDataUrl.empty()) continue;
            if (auto* icon = graphics.DecodeImage(source.iconDataUrl, 20, 20))
                context->DrawBitmap(icon, Draw::Rect(iconLeft, sourceTop + 7.0f, iconLeft + 20.0f, sourceTop + 27.0f),
                    1.0f, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);
        }
        Draw::Text(context, graphics, Draw::Ellipsize(context, graphics, SourceLabel(snapshot),
            environment.fontFamily, 12.0f, 400, metadataWidth - 46.0f), environment.fontFamily, 12.0f,
            400, palette.secondary, Draw::Rect(iconLeft + 28.0f, sourceTop,
                sourceRect.right - 20.0f, sourceRect.bottom), DWRITE_TEXT_ALIGNMENT_LEADING,
            false, false, false, DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
        DrawGlyph(context, Glyph::Chevron, WithAlpha(palette.secondary, 0.9f),
            sourceRect.right - 22.0f, sourceTop + 11.0f, 12.0f);

        // Settings gear pinned top-right.
        const D2D1_RECT_F gear = Draw::Rect(left + contentWidth - 32.0f, top,
            left + contentWidth, top + 32.0f);
        Draw::FillRound(context, gear, 16.0f, WithAlpha(palette.primary, 0.06f));
        DrawGlyph(context, Glyph::Settings, WithAlpha(palette.primary, 0.85f), gear.left + 8.0f,
            gear.top + 8.0f, 16.0f);
        Draw::AddButton(regions, 3, gear, L"openSettings", L"设置");

        // Lyrics viewport.
        const float lyricTop = top + 90.0f;
        DrawLyrics(context, graphics, palette, environment, snapshot, lyricTransition, left, lyricTop,
            contentWidth);

        // Transport row centred above the progress bar.
        const float transportTop = heightDip - Layout::Padding - 84.0f;
        DrawTransport(context, graphics, palette, environment, snapshot, regions,
            widthDip / 2.0f, transportTop);

        // Progress + times.
        const float progressTop = heightDip - Layout::Padding - 24.0f;
        const std::wstring position = FormatDuration(snapshot.media.position);
        const std::wstring duration = FormatDuration(snapshot.media.duration);
        Draw::Text(context, graphics, position, environment.fontFamily, 10.0f, 400, palette.secondary,
            Draw::Rect(left, progressTop, left + 40.0f, progressTop + 16.0f));
        const float railLeft = left + 44.0f;
        const float railRight = left + contentWidth - 44.0f;
        const float railWidth = std::max(1.0f, railRight - railLeft);
        const float railTop = progressTop + 7.0f;
        const float ratio = snapshot.media.duration > 0.0
            ? static_cast<float>(snapshot.media.position / snapshot.media.duration) : 0.0f;
        Draw::Progress(context, Draw::Rect(railLeft, railTop, railRight, railTop + 3.0f), ratio,
            palette.track, palette.accent, true);
        Draw::Text(context, graphics, duration, environment.fontFamily, 10.0f, 400, palette.secondary,
            Draw::Rect(railRight + 4.0f, progressTop, left + contentWidth, progressTop + 16.0f),
            DWRITE_TEXT_ALIGNMENT_TRAILING);
        Draw::AddSlider(regions, 4, Draw::Rect(railLeft, railTop - 8.0f, railRight, railTop + 11.0f),
            L"seek", L"播放进度", snapshot.media.canSeek);
    }
}
