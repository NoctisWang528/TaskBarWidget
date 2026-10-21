#include "../TrafficMonitor/MediaAnimation.h"
#include "../TrafficMonitor/MediaFrameContract.h"
#include "../TrafficMonitor/MediaLayoutModel.h"

#include <iostream>
#include <stdexcept>

namespace
{
    void Require(bool condition, const char* message)
    {
        if (!condition) throw std::runtime_error(message);
    }

    NativeMedia::Frame MakeFrame(std::uint64_t revision, NativeMedia::Scene scene,
        std::wstring track, int line, std::wstring current = L"")
    {
        NativeMedia::Frame frame;
        frame.revision = revision;
        frame.sourceId = L"QQMusic.exe";
        frame.isPlaying = true;
        frame.hasMedia = true;
        frame.trackId = track;
        frame.lyrics.trackId = track;
        frame.lyrics.scene = scene;
        frame.lyrics.current = std::move(current);
        frame.lyrics.next = L"next";
        frame.lyrics.currentLineIndex = line;
        frame.lyrics.isPlaying = true;
        return frame;
    }
}

int main()
{
    using namespace NativeMedia;
    FrameStore store;
    Require(!store.HasFrame(), "A new frame store is empty.");

    Update first;
    first.hasFrame = true;
    first.frame = MakeFrame(10, Scene::Lyrics, L"track-a", 1, L"line one");
    first.style = Style{};
    Cover cover;
    cover.dataUri = L"data:image/png;base64,AAAA";
    cover.trackId = L"track-a";
    first.cover = cover;
    first.tuning = SpectrumTuning{};
    first.spectrum = std::vector<double>{ 0.1, 0.2, 0.3 };
    Require(store.Apply(first) == ApplyResult::Applied, "First full frame applies.");
    Require(store.HasStyle(), "First full frame establishes style.");
    Require(store.CurrentCover().has_value(), "First cover is retained.");

    Update nullCover = first;
    nullCover.frame.revision = 11;
    nullCover.cover.reset();
    nullCover.style.reset();
    Require(store.Apply(nullCover) == ApplyResult::Applied, "Null cover update applies.");
    Require(store.CurrentCover() && store.CurrentCover()->trackId == L"track-a",
        "Null cover reuses the previous cover.");

    Update cleared = nullCover;
    cleared.frame.revision = 12;
    Cover empty;
    empty.cleared = true;
    cleared.cover = empty;
    Require(store.Apply(cleared) == ApplyResult::Applied, "Empty cover applies.");
    Require(store.CurrentCover()->cleared, "An empty cover object clears the image.");

    Update mismatch = cleared;
    mismatch.frame.revision = 13;
    mismatch.frame.lyrics.trackId = L"track-b";
    mismatch.frame.trackId = L"track-b";
    Cover stale;
    stale.dataUri = L"data:image/png;base64,BBBB";
    stale.trackId = L"track-a";
    mismatch.cover = stale;
    Require(store.Apply(mismatch) == ApplyResult::Applied, "Mismatched cover frame remains valid.");
    Require(store.CurrentCover()->cleared, "A cover for another track is rejected.");

    Update staleRevision;
    staleRevision.hasFrame = true;
    staleRevision.frame = MakeFrame(9, Scene::Lyrics, L"track-b", 1);
    Require(store.Apply(staleRevision) == ApplyResult::Stale, "Older revisions are rejected.");

    FrameStore trackChange;
    Update trackStart;
    trackStart.hasFrame = true;
    trackStart.frame = MakeFrame(1, Scene::Lyrics, L"track-a", 1);
    trackStart.style = Style{};
    Cover trackCover;
    trackCover.dataUri = L"data:image/png;base64,AAAA";
    trackCover.trackId = L"track-a";
    trackStart.cover = trackCover;
    Require(trackChange.Apply(trackStart) == ApplyResult::Applied,
        "Track cover fixture applies.");
    const auto coverVersion = trackChange.CoverVersion();
    Update trackNext;
    trackNext.hasFrame = true;
    trackNext.frame = MakeFrame(2, Scene::Lyrics, L"track-b", 1);
    Require(trackChange.Apply(trackNext) == ApplyResult::Applied,
        "Track-only frame updates without a cover.");
    Require(trackChange.CurrentCover() && trackChange.CurrentCover()->cleared,
        "A track change invalidates a cover that was not replaced.");
    Require(trackChange.CoverVersion() > coverVersion,
        "Cover invalidation advances its cache generation.");

    const auto samePlan = PlanTransition(MakeFrame(1, Scene::Lyrics, L"a", 1),
        MakeFrame(2, Scene::Lyrics, L"a", 1), true, false);
    Require(samePlan.kind == TransitionKind::Immediate,
        "Same line/progress is a patch with no roll.");
    const auto rollPlan = PlanTransition(MakeFrame(1, Scene::Lyrics, L"a", 1),
        MakeFrame(2, Scene::Lyrics, L"a", 2), true, false);
    Require(rollPlan.kind == TransitionKind::SingleRoll,
        "Line changes use a single roll.");
    auto translationTarget = MakeFrame(2, Scene::Lyrics, L"a", 2);
    translationTarget.lyrics.translationMode = true;
    const auto translationPlan = PlanTransition(MakeFrame(1, Scene::Lyrics, L"a", 1),
        translationTarget, true, false);
    Require(translationPlan.kind == TransitionKind::TranslationRoll,
        "Translation changes use a paired roll.");
    const auto reducedPlan = PlanTransition(MakeFrame(1, Scene::Lyrics, L"a", 1),
        translationTarget, true, true);
    Require(reducedPlan.kind == TransitionKind::Immediate,
        "Reduced motion cancels rolls.");

    WordScanSmoother scan;
    scan.Observe(L"track", 1, true, 0.10, 1000);
    const auto initialScan = scan.Current(1000);
    Require(initialScan.value() > 0.099 && initialScan.value() < 0.101,
        "Initial word scan is immediate.");
    scan.Observe(L"track", 1, true, 0.15, 1010);
    const double nextProgress = scan.Current(1055).value();
    Require(nextProgress >= 0.10 && nextProgress < 0.15,
        "Continuous scan progress is smoothed.");
    scan.Observe(L"track", 1, false, 0.30, 1055);
    const auto frozenValue = scan.Current(1500);
    Require(frozenValue.has_value(), "Pause retains a scan value.");
    const double frozen = frozenValue.value();
    Require(std::isfinite(frozen), "Pause retains a finite scan value.");
    Require(frozen >= 0.0 && frozen <= 1.0, "Pause retains a bounded scan value.");
    Require(std::abs(frozen - nextProgress) < 0.0001,
        "Pause freezes the smoothed scan position.");
    scan.Observe(L"track", 2, true, 0.02, 1600);
    Require(std::abs(scan.Current(1600).value() - 0.02) < 0.0001,
        "Line identity change cancels the old interpolation.");
    scan.Observe(L"track", 2, true, std::nullopt, 1700);
    Require(!scan.HasValue(), "Unknown scan progress stops extrapolation.");

    EffectiveStyle style;
    style.fontSizePx = 13;
    style.coverSizePx = 34;
    style.coverGapPx = 8;
    style.lyricsPaneTopPaddingPx = 3;
    style.lyricsPaneRightPaddingPx = 4;
    style.lyricsPaneLeftPaddingPx = 2;
    style.viewportDescenderBufferPx = 2;
    style.layoutScalePercent = 100;
    style.coverPosition = L"Left";
    style.showCover = true;
    style.spectrumWidthPx = 210;
    style.spectrumBarWidthPx = 3;
    style.spectrumGapPx = 3;
    const auto layout = CalculateLayout(300, 32, 1, style, true);
    Require(layout.coverShown && layout.cover.right <= layout.pane.left,
        "Cover and lyric pane never overlap.");
    Require(layout.rowHeight * 2 + layout.rowGap <= layout.hostHeight + 0.001f,
        "Rows fit inside the lyric viewport.");
    const float scroll = CalculateHorizontalScroll(400, 100, 0.65f, 1);
    Require(scroll >= 0 && scroll <= 300, "Horizontal scan scroll stays bounded.");

    SpectrumGeometry geometry = CalculateSpectrumGeometry(100, 1, style,
        SpectrumTuning{});
    Require(geometry.barCount >= 1 && geometry.totalWidth <= 100.0001f,
        "Spectrum geometry fits the available pane.");

    SpectrumAnimator animator;
    SpectrumTuning tuning;
    tuning.rise = 0.5;
    tuning.fall = 0.5;
    animator.Configure(tuning, { 0.0, 0.5, 1.0 }, true);
    Require(animator.Tick(1.0 / 60), "Spectrum transitions remain active while rising.");
    for (int index = 0; index < 8; ++index) animator.Tick(2.0);
    Require(!animator.Tick(2.0), "Spectrum settles and stops requesting frames.");

    RollTransition transition;
    transition.Begin(TransitionKind::SingleRoll, MakeFrame(1, Scene::Lyrics, L"a", 1),
        MakeFrame(2, Scene::Lyrics, L"a", 2), 1000, 560);
    Require(!transition.Finished(1200), "A roll remains active before its duration.");
    Require(transition.Finished(1600), "A roll completes at its duration.");

    std::cout << "Native media frame, layout, scan and spectrum assertions passed.\n";
    return 0;
}
