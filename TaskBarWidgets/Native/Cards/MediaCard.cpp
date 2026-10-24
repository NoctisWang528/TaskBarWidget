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
            return snapshot.media.source;
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
                secondary = lyrics.currentTranslation.empty() ? lyrics.next : lyrics.currentTranslation;
            }
            const float mainSize = std::clamp(static_cast<float>(lyrics.fontSize) > 5.0f ? static_cast<float>(lyrics.fontSize) : 14.0f,
                11.2f, LyricHeight * 0.92f);
            const float secondarySize = std::max(9.0f, mainSize * 0.92f);
            const std::wstring family = lyrics.fontFamily.empty() ? environment.fontFamily : lyrics.fontFamily;
            const bool twoRows = !secondary.empty() && LyricHeight >= mainSize * 2.4f;

            Color shadowColor = WithAlpha(Color{ 0, 0, 0, 1 }, 0.36f);
            if (lyrics.textShadow == L"none" || environment.highContrast) shadowColor = WithAlpha(Color{}, 0.0f);

            const auto row = [&](const std::wstring& text, float size, Color color, float rowTop,
                float scan, float marquee)
            {
                if (text.empty()) return;
                const D2D1_RECT_F rect = Draw::Rect(left, rowTop, left + width, rowTop + size * 1.35f);
                // An overlong line scrolls instead of clipping; the measured overflow
                // drives both the clip and the offset.
                float overflow = 0.0f;
                const auto format = graphics.TextFormat(family, size, lyrics.fontWeight,
                    DWRITE_TEXT_ALIGNMENT_LEADING, false);
                if (format && graphics.DWrite())
                {
                    winrt::com_ptr<IDWriteTextLayout> layout;
                    if (SUCCEEDED(graphics.DWrite()->CreateTextLayout(text.c_str(),
                        static_cast<UINT32>(text.size()), format, width * 4.0f, size * 2.0f,
                        layout.put())))
                    {
                        DWRITE_TEXT_METRICS metrics{};
                        if (SUCCEEDED(layout->GetMetrics(&metrics)))
                            overflow = (std::max)(0.0f, metrics.widthIncludingTrailingWhitespace - width);
                    }
                }
                const bool scrolls = overflow > 1.0f;
                const std::wstring shown = scrolls ? text
                    : Draw::Ellipsize(context, graphics, text, family, size, lyrics.fontWeight, width);
                const float shift = scrolls ? -overflow * std::clamp(marquee, 0.0f, 1.0f) : 0.0f;
                const auto alignment = scrolls ? DWRITE_TEXT_ALIGNMENT_LEADING
                    : DWRITE_TEXT_ALIGNMENT_CENTER;
                const D2D1_RECT_F target = scrolls
                    ? D2D1::RectF(rect.left + shift, rect.top, rect.left + shift + width * 4.0f, rect.bottom)
                    : rect;
                if (scrolls)
                {
                    context->PushAxisAlignedClip(rect, D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
                    const D2D1_RECT_F wide = D2D1::RectF(rect.left + shift, rect.top,
                        rect.left + shift + width * 4.0f, rect.bottom);
                    if (shadowColor.a > 0.0f)
                        Draw::Text(context, graphics, shown, family, size, lyrics.fontWeight,
                            shadowColor, D2D1::RectF(wide.left, wide.top + 1.0f, wide.right, wide.bottom + 1.0f),
                            alignment);
                    Draw::Text(context, graphics, shown, family, size, lyrics.fontWeight, color, wide,
                        alignment);
                    context->PopAxisAlignedClip();
                }
                else
                {
                    if (shadowColor.a > 0.0f)
                        Draw::Text(context, graphics, shown, family, size, lyrics.fontWeight,
                            shadowColor, D2D1::RectF(target.left, target.top + 1.0f, target.right,
                                target.bottom + 1.0f), alignment);
                    Draw::Text(context, graphics, shown, family, size, lyrics.fontWeight, color, target,
                        alignment);
                }
                if (scan > 0.01f && scan < 0.999f)
                {
                    // Scan highlight: a clipped overlay rather than per-glyph work, so
                    // the base text exists once and the highlight is only a clip.
                    const D2D1_RECT_F scanRect = D2D1::RectF(rect.left, rect.top,
                        rect.left + (rect.right - rect.left) * scan, rect.bottom);
                    context->PushAxisAlignedClip(scanRect, D2D1_ANTIALIAS_MODE_ALIASED);
                    const auto scanColor = ParseColor(lyrics.scanColor.empty()
                        ? DefaultScanColor(environment.dark) : lyrics.scanColor, palette.accent);
                    Draw::Text(context, graphics, shown, family, size, lyrics.fontWeight, scanColor,
                        target, alignment);
                    context->PopAxisAlignedClip();
                }
            };

            // Wrap transition: the previous line leaves upward while the new one
            // enters from below. Both exist only while the transition runs.
            const float wrap = std::clamp(transition.progress, 0.0f, 1.0f);
            if (wrap < 0.999f && !transition.previous.empty())
            {
                const float shift = (1.0f - wrap) * mainSize * 1.25f;
                const float alpha = wrap;
                row(transition.previous, mainSize, WithAlpha(palette.primary, 0.98f * alpha),
                    top - shift, 0.0f, 0.0f);
                if (twoRows && !transition.previousSecondary.empty())
                    row(transition.previousSecondary, secondarySize,
                        WithAlpha(palette.primary, 0.62f * alpha), top + mainSize * 1.3f - shift, 0.0f,
                        0.0f);
            }
            const float enter = (1.0f - wrap) * mainSize * 0.55f;
            row(main, mainSize, WithAlpha(palette.primary, 0.98f * (0.35f + 0.65f * wrap)), top + enter,
                lyrics.progress, transition.marquee);
            if (twoRows) row(secondary, secondarySize,
                WithAlpha(palette.primary, 0.62f * (0.35f + 0.65f * wrap)),
                top + mainSize * 1.3f + enter, 0.0f, 0.0f);
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
            const auto format = graphics.TextFormat(environment.fontFamily, 27.0f, 400,
                DWRITE_TEXT_ALIGNMENT_CENTER, false);
            if (format)
            {
                const auto brush = Draw::Brush(context, WithAlpha(palette.primary, 0.45f));
                if (brush)
                {
                    context->DrawTextW(L"\u266B", 1, format, cover, brush,
                        D2D1_DRAW_TEXT_OPTIONS_CLIP);
                    brush->Release();
                }
            }
        }
        Draw::AddButton(regions, 1, cover, L"cover", L"封面", false, false);

        // Metadata column.
        const float metadataLeft = left + Cover + Gap;
        const float metadataWidth = contentWidth - Cover - Gap - 44.0f;
        Draw::Text(context, graphics, snapshot.media.title.empty() ? L"未在播放" : snapshot.media.title,
            environment.fontFamily, 15.0f, 600, palette.primary,
            Draw::Rect(metadataLeft, top, metadataLeft + metadataWidth, top + 22.0f));
        Draw::Text(context, graphics, snapshot.media.artist, environment.fontFamily, 12.0f, 400,
            palette.secondary, Draw::Rect(metadataLeft, top + 22.0f, metadataLeft + metadataWidth, top + 40.0f));

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
            400, palette.secondary, Draw::Rect(iconLeft + 28.0f, sourceTop + 8.0f,
                sourceRect.right - 20.0f, sourceTop + 28.0f));
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
