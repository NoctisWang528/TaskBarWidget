#pragma once

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <optional>
#include <string>
#include <utility>
#include <vector>

namespace NativeMedia
{
    enum class Scene
    {
        Searching,
        Lyrics,
        Spectrum,
        NoPlayback,
        Message
    };

    inline Scene SceneFromWire(const std::wstring& value, bool isPureMusic,
        const std::wstring& current, int currentLineIndex)
    {
        if (value == L"searching") return Scene::Searching;
        if (value == L"lyrics") return Scene::Lyrics;
        if (value == L"spectrum") return Scene::Spectrum;
        if (value == L"noPlayback") return Scene::NoPlayback;
        if (value == L"message") return Scene::Message;
        if (isPureMusic) return Scene::Spectrum;
        if (current == L"\u6b63\u5728\u68c0\u7d22\u6b4c\u8bcd..." ||
            current == L"\u6b63\u5728\u5339\u914d\u6b4c\u8bcd...")
            return Scene::Searching;
        return currentLineIndex >= 0 ? Scene::Lyrics : Scene::Message;
    }

    inline bool IsSearchingText(const std::wstring& text)
    {
        return text == L"\u6b63\u5728\u68c0\u7d22\u6b4c\u8bcd..." ||
            text == L"\u6b63\u5728\u5339\u914d\u6b4c\u8bcd...";
    }

    inline double Clamp01(double value)
    {
        if (!std::isfinite(value)) return 0.0;
        return (std::max)(0.0, (std::min)(1.0, value));
    }

    struct LyricsFrame
    {
        std::wstring current;
        std::wstring next;
        std::wstring currentTranslation;
        std::wstring nextTranslation;
        std::wstring trackId;
        double progress{};
        bool hasWordScanProgress{};
        double wordScanProgress{};
        int currentLineIndex{ -1 };
        bool isPureMusic{};
        bool isPlaying{ true };
        bool translationMode{};
        bool animateTransition{ true };
        Scene scene{ Scene::Message };
    };

    struct Style
    {
        std::wstring fontFamily{ L"Segoe UI Variable Text" };
        double layoutScalePercent{ 100.0 };
        double fontSize{ 13.0 };
        bool showCover{ true };
        std::wstring coverPosition{ L"Left" };
        bool enableControlPanel{ true };
        double coverSize{ 34.0 };
        double coverGap{ 8.0 };
        double coverCornerRadius{ 6.0 };
        double viewportDescenderBuffer{ 2.0 };
        double layoutHorizontalPadding{ 0.0 };
        double lyricsPaneTopPadding{ 3.0 };
        double lyricsPaneRightPadding{ 4.0 };
        double lyricsPaneLeftPadding{ 2.0 };
        double primaryOffsetY{ 1.0 };
        double secondaryOffsetY{ 1.0 };
        double lineTextBottomPadding{ 2.0 };
        double surfaceRadius{ 8.0 };
        double layerTransitionOffset{ 2.0 };
        double coverFallbackFontSize{ 13.0 };
        double spectrumWidth{ 210.0 };
        double spectrumHeight{ 22.0 };
        double spectrumGap{ 3.0 };
        double spectrumBarWidth{ 3.0 };
        double spectrumBarHeight{ 8.0 };
        double spectrumLowHeight{ 6.0 };
        double spectrumHighHeight{ 18.0 };
        double spectrumMiddleHeight{ 11.0 };
        int fontWeight{ 500 };
        std::wstring primaryColor{ L"rgba(255, 255, 255, 0.90)" };
        std::wstring secondaryColor{ L"rgba(255, 255, 255, 0.60)" };
        std::wstring translationColor{ L"rgba(255, 255, 255, 0.70)" };
        std::wstring wordScanOverlayColor{ L"rgba(255, 255, 255, 0.75)" };
        std::wstring surfaceColor{ L"transparent" };
        std::wstring surfaceShadow{ L"none" };
        std::wstring textShadow{ L"none" };
        std::wstring textAlignment{ L"Left" };
    };

    struct Cover
    {
        std::wstring dataUri;
        std::wstring fallbackText;
        std::wstring fallbackColor;
        std::wstring fallbackIconDataUri;
        std::wstring trackId;
        bool cleared{};

        bool IsEmpty() const
        {
            return cleared || (dataUri.empty() && fallbackIconDataUri.empty() &&
                fallbackText.empty());
        }
    };

    struct Browser
    {
        bool active{};
        std::wstring title;
        std::wstring artist;
        std::wstring source;
        bool isPlaying{};
        bool canPrevious{};
        bool canNext{};
        bool canPlayPause{};
    };

    struct SpectrumTuning
    {
        double rise{ 0.56 };
        double fall{ 0.24 };
        double minHeight{ 5.0 };
        double heightRange{ 17.0 };
        double opacity{ 0.78 };
        int barCount{ 21 };

        void Normalize()
        {
            rise = std::clamp(std::isfinite(rise) ? rise : 0.56, 0.02, 1.0);
            fall = std::clamp(std::isfinite(fall) ? fall : 0.24, 0.02, 1.0);
            minHeight = std::clamp(std::isfinite(minHeight) ? minHeight : 5.0, 1.0, 18.0);
            heightRange = std::clamp(std::isfinite(heightRange) ? heightRange : 17.0, 2.0, 40.0);
            opacity = std::clamp(std::isfinite(opacity) ? opacity : 0.78, 0.2, 1.0);
            barCount = std::clamp(barCount <= 0 ? 21 : barCount, 8, 32);
        }
    };

    struct NativeAppearance
    {
        std::wstring fontFamily;
        double fontSizePx{};
        int fontWeight{};
        bool italic{};
        bool underline{};
        bool strikeOut{};
        std::wstring surfaceColor;
        bool valid{};
    };

    struct Frame
    {
        std::uint64_t revision{};
        std::wstring sourceId;
        bool isPlaying{};
        bool hasMedia{};
        bool isBrowser{};
        std::wstring trackId;
        LyricsFrame lyrics;
        Browser browser;
    };

    struct Update
    {
        Frame frame;
        std::optional<Style> style;
        std::optional<Cover> cover;
        std::optional<SpectrumTuning> tuning;
        std::optional<std::vector<double>> spectrum;
        bool hasFrame{};
    };

    struct EffectiveStyle
    {
        std::wstring fontFamily;
        double fontSizePx{};
        int fontWeight{};
        bool italic{};
        bool underline{};
        bool strikeOut{};
        bool showCover{};
        std::wstring coverPosition;
        bool enableControlPanel{};
        double layoutScalePercent{ 100.0 };
        double coverSizePx{};
        double coverGapPx{};
        double coverCornerRadiusPx{};
        double viewportDescenderBufferPx{};
        double layoutHorizontalPaddingPx{};
        double lyricsPaneTopPaddingPx{};
        double lyricsPaneRightPaddingPx{};
        double lyricsPaneLeftPaddingPx{};
        double primaryOffsetYPx{};
        double secondaryOffsetYPx{};
        double lineTextBottomPaddingPx{};
        double surfaceRadiusPx{};
        double layerTransitionOffsetPx{};
        double coverFallbackFontSizePx{};
        double spectrumWidthPx{};
        double spectrumHeightPx{};
        double spectrumGapPx{};
        double spectrumBarWidthPx{};
        double spectrumBarHeightPx{};
        double spectrumLowHeightPx{};
        double spectrumHighHeightPx{};
        double spectrumMiddleHeightPx{};
        std::wstring primaryColor;
        std::wstring secondaryColor;
        std::wstring translationColor;
        std::wstring wordScanOverlayColor;
        std::wstring surfaceColor;
        bool surfaceShadow{};
        bool textShadow{};
        std::wstring textAlignment;
    };

    inline EffectiveStyle MakeEffectiveStyle(const Style& style, const NativeAppearance& appearance,
        float dpiScale)
    {
        if (!std::isfinite(dpiScale) || dpiScale <= 0) dpiScale = 1.0f;
        const double scale = std::clamp(style.layoutScalePercent, 20.0, 300.0) / 100.0;
        EffectiveStyle result;
        result.fontFamily = appearance.fontFamily.empty() ? style.fontFamily : appearance.fontFamily;
        const double baseFontSize = appearance.fontSizePx > 0 ? appearance.fontSizePx : style.fontSize;
        result.fontSizePx = baseFontSize * dpiScale;
        result.fontWeight = appearance.fontWeight > 0 ? appearance.fontWeight : style.fontWeight;
        result.italic = appearance.italic;
        result.underline = appearance.underline;
        result.strikeOut = appearance.strikeOut;
        result.showCover = style.showCover;
        result.coverPosition = style.coverPosition == L"Right" ? L"Right" : L"Left";
        result.enableControlPanel = style.enableControlPanel;
        result.layoutScalePercent = style.layoutScalePercent;
        result.coverSizePx = (std::max)(0.0, style.coverSize) * dpiScale;
        result.coverGapPx = (std::max)(0.0, style.coverGap) * dpiScale;
        result.coverCornerRadiusPx = std::clamp(style.coverCornerRadius, 0.0, style.coverSize / 2) * dpiScale;
        result.viewportDescenderBufferPx = (std::max)(0.0, style.viewportDescenderBuffer) * dpiScale;
        result.layoutHorizontalPaddingPx = (std::max)(0.0, style.layoutHorizontalPadding) * dpiScale;
        result.lyricsPaneTopPaddingPx = (std::max)(0.0, style.lyricsPaneTopPadding) * dpiScale;
        result.lyricsPaneRightPaddingPx = (std::max)(0.0, style.lyricsPaneRightPadding) * dpiScale;
        result.lyricsPaneLeftPaddingPx = (std::max)(0.0, style.lyricsPaneLeftPadding) * dpiScale;
        result.primaryOffsetYPx = style.primaryOffsetY * dpiScale;
        result.secondaryOffsetYPx = style.secondaryOffsetY * dpiScale;
        result.lineTextBottomPaddingPx = (std::max)(0.0, style.lineTextBottomPadding) * dpiScale;
        result.surfaceRadiusPx = (std::max)(0.0, style.surfaceRadius) * dpiScale;
        result.layerTransitionOffsetPx = style.layerTransitionOffset * dpiScale;
        result.coverFallbackFontSizePx = (std::max)(0.0, style.coverFallbackFontSize) * dpiScale;
        result.spectrumWidthPx = (std::max)(0.0, style.spectrumWidth) * dpiScale;
        result.spectrumHeightPx = (std::max)(0.0, style.spectrumHeight) * dpiScale;
        result.spectrumGapPx = (std::max)(0.0, style.spectrumGap) * dpiScale;
        result.spectrumBarWidthPx = (std::max)(0.0, style.spectrumBarWidth) * dpiScale;
        result.spectrumBarHeightPx = (std::max)(0.0, style.spectrumBarHeight) * dpiScale;
        result.spectrumLowHeightPx = (std::max)(0.0, style.spectrumLowHeight) * dpiScale;
        result.spectrumHighHeightPx = (std::max)(0.0, style.spectrumHighHeight) * dpiScale;
        result.spectrumMiddleHeightPx = (std::max)(0.0, style.spectrumMiddleHeight) * dpiScale;
        result.primaryColor = style.primaryColor;
        result.secondaryColor = style.secondaryColor;
        result.translationColor = style.translationColor;
        result.wordScanOverlayColor = style.wordScanOverlayColor;
        result.surfaceColor = appearance.surfaceColor.empty() ? style.surfaceColor : appearance.surfaceColor;
        result.surfaceShadow = style.surfaceShadow != L"none" && !style.surfaceShadow.empty();
        result.textShadow = style.textShadow != L"none" && !style.textShadow.empty();
        result.textAlignment = style.textAlignment == L"Center" || style.textAlignment == L"Right"
            ? style.textAlignment : L"Left";
        (void)scale;
        return result;
    }

    inline std::wstring EffectiveTrackId(const Frame& frame)
    {
        return !frame.lyrics.trackId.empty() ? frame.lyrics.trackId : frame.trackId;
    }

    enum class ApplyResult
    {
        Applied,
        Stale,
        Invalid
    };

    class FrameStore
    {
    public:
        void Reset()
        {
            m_frame = {};
            m_style.reset();
            m_cover.reset();
            m_tuning.reset();
            m_spectrum.clear();
            m_revision = 0;
            m_hasFrame = false;
            m_coverVersion = 0;
            ++m_generation;
            m_lastApply = ApplyResult::Applied;
        }

        ApplyResult Apply(const Update& update)
        {
            if (update.hasFrame)
            {
                if (!std::isfinite(static_cast<double>(update.frame.revision)) ||
                    (m_hasFrame && update.frame.revision < m_revision))
                {
                    m_lastApply = ApplyResult::Stale;
                    return m_lastApply;
                }
                if (update.frame.lyrics.progress < 0 || update.frame.lyrics.progress > 1 ||
                    (update.frame.lyrics.hasWordScanProgress &&
                        (update.frame.lyrics.wordScanProgress < 0 ||
                            update.frame.lyrics.wordScanProgress > 1)))
                {
                    m_lastApply = ApplyResult::Invalid;
                    return m_lastApply;
                }
            }

            if (update.style)
            {
                m_style = update.style;
            }
            else if (update.hasFrame && !m_style)
            {
                m_style = Style{};
            }

            const auto trackId = update.hasFrame ? EffectiveTrackId(update.frame) :
                EffectiveTrackId(m_frame);
            if (update.cover)
            {
                const bool identityMismatch = !update.cover->cleared &&
                    !update.cover->trackId.empty() && !trackId.empty() &&
                    update.cover->trackId != trackId;
                if (!identityMismatch)
                {
                    m_cover = update.cover;
                    ++m_coverVersion;
                }
            }
            else if (update.hasFrame && m_cover && !m_cover->cleared &&
                !m_cover->trackId.empty() && !trackId.empty() &&
                m_cover->trackId != trackId)
            {
                Cover cleared;
                cleared.cleared = true;
                m_cover = cleared;
                ++m_coverVersion;
            }

            if (update.tuning)
            {
                auto tuning = *update.tuning;
                tuning.Normalize();
                m_tuning = tuning;
            }
            if (update.spectrum)
            {
                m_spectrum.clear();
                m_spectrum.reserve(update.spectrum->size());
                for (const auto value : *update.spectrum)
                    m_spectrum.push_back(Clamp01(value));
            }

            if (update.hasFrame)
            {
                m_frame = update.frame;
                m_revision = update.frame.revision;
                m_hasFrame = true;
            }
            m_lastApply = ApplyResult::Applied;
            return m_lastApply;
        }

        bool HasFrame() const { return m_hasFrame; }
        bool HasStyle() const { return m_style.has_value(); }
        const Frame& CurrentFrame() const { return m_frame; }
        const Style& CurrentStyle() const
        {
            static const Style fallback;
            return m_style ? *m_style : fallback;
        }
        const std::optional<Cover>& CurrentCover() const { return m_cover; }
        const SpectrumTuning& CurrentTuning() const
        {
            static const SpectrumTuning defaults;
            return m_tuning ? *m_tuning : defaults;
        }
        const std::vector<double>& CurrentSpectrum() const { return m_spectrum; }
        std::uint64_t Revision() const { return m_revision; }
        std::uint64_t Generation() const { return m_generation; }
        std::uint64_t CoverVersion() const { return m_coverVersion; }
        ApplyResult LastApply() const { return m_lastApply; }

    private:
        Frame m_frame;
        std::optional<Style> m_style;
        std::optional<Cover> m_cover;
        std::optional<SpectrumTuning> m_tuning;
        std::vector<double> m_spectrum;
        std::uint64_t m_revision{};
        std::uint64_t m_generation{};
        std::uint64_t m_coverVersion{};
        bool m_hasFrame{};
        ApplyResult m_lastApply{ ApplyResult::Applied };
    };

    enum class TransitionKind
    {
        Immediate,
        ReplaceInPlace,
        SingleRoll,
        TranslationRoll,
        LayerSwitch
    };

    struct TransitionPlan
    {
        TransitionKind kind{ TransitionKind::Immediate };
        int durationMs{};
    };

    inline bool SameLyricsIdentity(const Frame& left, const Frame& right)
    {
        return left.lyrics.scene == Scene::Lyrics && right.lyrics.scene == Scene::Lyrics &&
            left.lyrics.currentLineIndex >= 0 &&
            left.lyrics.currentLineIndex == right.lyrics.currentLineIndex &&
            EffectiveTrackId(left) == EffectiveTrackId(right);
    }

    inline TransitionPlan PlanTransition(const Frame& current, const Frame& target,
        bool animateTransition, bool reducedMotion)
    {
        if (!animateTransition || !target.lyrics.animateTransition || reducedMotion)
            return { TransitionKind::Immediate, 0 };

        const auto currentScene = current.lyrics.scene;
        const auto targetScene = target.lyrics.scene;
        const bool translation = target.lyrics.translationMode;

        if (targetScene == Scene::Spectrum)
        {
            if (currentScene == Scene::Searching || currentScene == Scene::NoPlayback)
                return { TransitionKind::LayerSwitch, 760 };
            if (currentScene == Scene::Spectrum) return { TransitionKind::Immediate, 0 };
            return { TransitionKind::LayerSwitch, 300 };
        }

        if (currentScene == Scene::Spectrum)
            return { TransitionKind::LayerSwitch, targetScene == Scene::Searching ? 760 : 300 };

        if (targetScene == Scene::Searching)
        {
            if (currentScene == Scene::Searching)
            {
                const bool trackChanged = !EffectiveTrackId(current).empty() &&
                    !EffectiveTrackId(target).empty() &&
                    EffectiveTrackId(current) != EffectiveTrackId(target);
                const bool textChanged = current.lyrics.current != target.lyrics.current ||
                    current.lyrics.next != target.lyrics.next ||
                    current.lyrics.currentTranslation != target.lyrics.currentTranslation ||
                    current.lyrics.nextTranslation != target.lyrics.nextTranslation;
                if (trackChanged) return { TransitionKind::SingleRoll, 560 };
                return { textChanged ? TransitionKind::ReplaceInPlace : TransitionKind::Immediate, 0 };
            }
            return { TransitionKind::SingleRoll, 560 };
        }

        if (SameLyricsIdentity(current, target))
        {
            const bool textChanged = current.lyrics.current != target.lyrics.current ||
                current.lyrics.next != target.lyrics.next ||
                current.lyrics.currentTranslation != target.lyrics.currentTranslation ||
                current.lyrics.nextTranslation != target.lyrics.nextTranslation ||
                current.lyrics.translationMode != target.lyrics.translationMode;
            return { textChanged ? TransitionKind::ReplaceInPlace : TransitionKind::Immediate, 0 };
        }

        if (targetScene == Scene::Lyrics && currentScene == Scene::Lyrics)
            return { translation ? TransitionKind::TranslationRoll : TransitionKind::SingleRoll,
                translation ? 760 : 560 };
        if (targetScene == Scene::Lyrics &&
            (currentScene == Scene::Searching || currentScene == Scene::NoPlayback))
            return { translation ? TransitionKind::TranslationRoll : TransitionKind::SingleRoll,
                translation ? 760 : 560 };
        if (targetScene == Scene::Message || targetScene == Scene::NoPlayback)
        {
            const bool textChanged = currentScene != targetScene ||
                current.lyrics.current != target.lyrics.current ||
                current.lyrics.next != target.lyrics.next ||
                current.lyrics.currentTranslation != target.lyrics.currentTranslation ||
                current.lyrics.nextTranslation != target.lyrics.nextTranslation;
            return { textChanged ? TransitionKind::SingleRoll : TransitionKind::Immediate,
                textChanged ? 560 : 0 };
        }
        return { TransitionKind::ReplaceInPlace, 0 };
    }
}
