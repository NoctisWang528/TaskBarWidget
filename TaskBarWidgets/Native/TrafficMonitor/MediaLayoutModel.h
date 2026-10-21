#pragma once

#include "MediaFrameContract.h"
#include <algorithm>
#include <cmath>

namespace NativeMedia
{
    struct LayoutRect
    {
        float left{};
        float top{};
        float right{};
        float bottom{};

        float Width() const { return (std::max)(0.0f, right - left); }
        float Height() const { return (std::max)(0.0f, bottom - top); }
        bool Contains(float x, float y) const
        {
            return x >= left && x < right && y >= top && y < bottom;
        }
    };

    struct BrowserLayout
    {
        LayoutRect metadata;
        LayoutRect previous;
        LayoutRect playPause;
        LayoutRect next;
    };

    struct MediaLayout
    {
        float width{};
        float height{};
        float dpiScale{ 1.0f };
        bool coverShown{};
        bool coverOnRight{};
        LayoutRect cover;
        LayoutRect pane;
        LayoutRect lyricsLayer;
        LayoutRect spectrumLayer;
        float paneTopPadding{};
        float paneContentHeight{};
        float hostHeight{};
        float rowHeight{};
        float rowGap{};
        float linePitch{};
        float currentFontSize{};
        float nextFontSize{};
        float layerOffset{};
        BrowserLayout browser;
    };

    inline float SnapPixel(float value, float dpiScale)
    {
        if (!std::isfinite(value) || !std::isfinite(dpiScale) || dpiScale <= 0) return value;
        return std::round(value * dpiScale) / dpiScale;
    }

    inline MediaLayout CalculateLayout(float width, float height, float dpiScale,
        const EffectiveStyle& style, bool coverAvailable)
    {
        MediaLayout layout;
        layout.width = (std::max)(1.0f, width);
        layout.height = (std::max)(1.0f, height);
        layout.dpiScale = std::isfinite(dpiScale) && dpiScale > 0 ? dpiScale : 1.0f;

        const float layoutPadding = static_cast<float>(style.layoutHorizontalPaddingPx);
        const float shellLeft = layoutPadding;
        const float shellRight = layout.width - layoutPadding;
        const float shellWidth = (std::max)(1.0f, shellRight - shellLeft);
        layout.coverShown = style.showCover && coverAvailable;
        layout.coverOnRight = style.coverPosition == L"Right";

        float paneLeft = shellLeft;
        float paneRight = shellRight;
        if (layout.coverShown)
        {
            const float coverSize = (std::min)(static_cast<float>(style.coverSizePx),
                (std::max)(1.0f, shellWidth));
            const float coverGap = static_cast<float>(style.coverGapPx);
            const float coverTop = (layout.height - coverSize) / 2.0f;
            if (layout.coverOnRight)
            {
                layout.cover = { shellRight - coverSize, coverTop, shellRight, coverTop + coverSize };
                paneRight = layout.cover.left - coverGap;
            }
            else
            {
                layout.cover = { shellLeft, coverTop, shellLeft + coverSize, coverTop + coverSize };
                paneLeft = layout.cover.right + coverGap;
            }
        }

        paneRight = (std::max)(paneLeft + 1.0f, paneRight);
        layout.pane = { paneLeft, 0.0f, paneRight, layout.height };
        layout.paneTopPadding = static_cast<float>(style.lyricsPaneTopPaddingPx);
        layout.paneContentHeight = (std::max)(1.0f,
            layout.height - layout.paneTopPadding);
        layout.lyricsLayer = {
            paneLeft + static_cast<float>(style.lyricsPaneLeftPaddingPx),
            layout.paneTopPadding,
            paneRight - static_cast<float>(style.lyricsPaneRightPaddingPx),
            layout.height
        };
        layout.spectrumLayer = {
            paneLeft + static_cast<float>(style.lyricsPaneLeftPaddingPx),
            0.0f,
            paneRight - static_cast<float>(style.lyricsPaneRightPaddingPx),
            layout.height
        };

        const float descender = static_cast<float>(style.viewportDescenderBufferPx);
        const float scale = static_cast<float>(std::clamp(style.layoutScalePercent, 20.0, 300.0) / 100.0);
        const float minimumHostHeight = (std::max)(2.0f, std::round(26.0f * scale));
        layout.hostHeight = (std::max)(minimumHostHeight,
            layout.paneContentHeight - descender);
        layout.rowHeight = (std::max)(1.0f, std::floor(layout.hostHeight / 2.0f));
        layout.rowGap = (std::max)(0.0f, layout.hostHeight - layout.rowHeight * 2.0f);
        layout.linePitch = layout.rowHeight + layout.rowGap;
        const float currentSizeMax = (std::max)(11.2f * scale, layout.rowHeight * 0.92f);
        layout.currentFontSize = (std::min)(static_cast<float>(style.fontSizePx), currentSizeMax);
        layout.nextFontSize = (std::max)(9.0f * scale, layout.currentFontSize * 0.92f);
        layout.layerOffset = static_cast<float>(style.layerTransitionOffsetPx);

        const float buttonWidth = 26.0f * layout.dpiScale;
        const float buttonGap = 2.0f * layout.dpiScale;
        const float buttonHeight = (std::min)(layout.height, 28.0f * layout.dpiScale);
        const float buttonTop = (layout.height - buttonHeight) / 2.0f;
        const float rightPadding = 10.0f * layout.dpiScale;
        float right = layout.width - rightPadding;
        layout.browser.next = { right - buttonWidth, buttonTop, right, buttonTop + buttonHeight };
        right = layout.browser.next.left - buttonGap;
        layout.browser.playPause = { right - buttonWidth, buttonTop, right, buttonTop + buttonHeight };
        right = layout.browser.playPause.left - buttonGap;
        layout.browser.previous = { right - buttonWidth, buttonTop, right, buttonTop + buttonHeight };
        layout.browser.metadata = {
            10.0f * layout.dpiScale,
            buttonTop,
            layout.browser.previous.left - 8.0f * layout.dpiScale,
            buttonTop + buttonHeight
        };
        return layout;
    }

    inline float CalculateHorizontalScroll(float contentWidth, float viewportWidth,
        float progress, float dpiScale)
    {
        const float overflow = (std::max)(0.0f, contentWidth - viewportWidth);
        if (overflow < 0.5f) return 0.0f;
        const float scanHead = static_cast<float>(Clamp01(progress)) * contentWidth;
        const float anchor = viewportWidth * 0.65f;
        const float offset = std::clamp(scanHead - anchor, 0.0f, overflow);
        return SnapPixel(offset, dpiScale);
    }

    struct SpectrumGeometry
    {
        int barCount{};
        float barWidth{};
        float gap{};
        float totalWidth{};
    };

    inline SpectrumGeometry CalculateSpectrumGeometry(float paneWidthDip, float dpiScale,
        const EffectiveStyle& style, const SpectrumTuning& tuning)
    {
        SpectrumGeometry geometry;
        const float available = (std::max)(1.0f, paneWidthDip);
        const float requestedWidth = (std::max)(1.0f, static_cast<float>(style.spectrumWidthPx));
        const float requestedBar = (std::max)(0.5f, static_cast<float>(style.spectrumBarWidthPx));
        const float requestedGap = (std::max)(0.0f, static_cast<float>(style.spectrumGapPx));
        const int maxVisible = (std::max)(1,
            static_cast<int>(std::floor((available + requestedGap) / (requestedBar + requestedGap))));
        geometry.barCount = (std::max)(1, (std::min)(tuning.barCount, maxVisible));
        const int gaps = (std::max)(0, geometry.barCount - 1);
        const float desired = requestedBar * geometry.barCount + requestedGap * gaps;
        geometry.barWidth = requestedBar;
        geometry.gap = requestedGap;
        if (desired > available)
        {
            const float ratio = available / desired;
            geometry.barWidth = (std::max)(1.0f / dpiScale,
                std::floor(requestedBar * ratio * dpiScale) / dpiScale);
            const float remaining = (std::max)(0.0f, available - geometry.barWidth * geometry.barCount);
            geometry.gap = gaps > 0
                ? std::floor((std::min)(requestedGap, remaining / gaps) * dpiScale) / dpiScale
                : 0.0f;
        }
        geometry.totalWidth = (std::min)(available,
            geometry.barWidth * geometry.barCount + geometry.gap * gaps);
        (void)requestedWidth;
        return geometry;
    }
}
