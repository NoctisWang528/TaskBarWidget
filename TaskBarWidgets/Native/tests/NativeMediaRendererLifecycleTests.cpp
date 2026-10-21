#include "../TrafficMonitor/NativeMediaRenderer.h"

#include <cmath>
#include <iostream>
#include <stdexcept>
#include <string>

namespace
{
    void Require(bool condition, const char* message)
    {
        if (!condition) throw std::runtime_error(message);
    }

    NativeMedia::Frame MakeFrame(std::uint64_t revision, NativeMedia::Scene scene,
        int line, double progress, std::optional<double> scan, bool translation = false)
    {
        NativeMedia::Frame frame;
        frame.revision = revision;
        frame.sourceId = L"fixture";
        frame.isPlaying = true;
        frame.hasMedia = true;
        frame.trackId = L"track";
        frame.lyrics.trackId = L"track";
        frame.lyrics.scene = scene;
        frame.lyrics.current = L"fixture line";
        frame.lyrics.next = L"next line";
        frame.lyrics.currentTranslation = L"translated";
        frame.lyrics.currentLineIndex = line;
        frame.lyrics.progress = progress;
        frame.lyrics.isPlaying = true;
        frame.lyrics.translationMode = translation;
        frame.lyrics.isPureMusic = scene == NativeMedia::Scene::Spectrum;
        if (scan)
        {
            frame.lyrics.hasWordScanProgress = true;
            frame.lyrics.wordScanProgress = *scan;
        }
        return frame;
    }

    NativeMedia::RenderUpdate MakeUpdate(const NativeMedia::Frame& frame, bool force = false)
    {
        static const NativeMedia::Style style;
        static const NativeMedia::SpectrumTuning tuning;
        static const std::vector<double> spectrum{ 0.2, 0.4, 0.6 };
        NativeMedia::RenderUpdate update;
        update.frame = &frame;
        update.style = &style;
        update.tuning = &tuning;
        update.spectrum = &spectrum;
        update.forceFull = force;
        return update;
    }
}

int main()
{
    using namespace NativeMedia;

    NativeMediaRenderer renderer;
    renderer.BeginHeadlessTest();
    auto first = MakeFrame(1, Scene::Lyrics, 1, 0.1, 0.10);
    renderer.Apply(MakeUpdate(first, true));
    renderer.AdvanceHeadlessTest(1000);

    auto second = MakeFrame(2, Scene::Lyrics, 1, 0.1, 0.20);
    renderer.Apply(MakeUpdate(second));
    const auto scanStart = renderer.AdvanceHeadlessTest(1010);
    const auto scanMiddle = renderer.AdvanceHeadlessTest(1055);
    Require(scanMiddle.displayedScan.has_value(), "Scan remains available during smoothing.");
    Require(*scanMiddle.displayedScan > 0.10 && *scanMiddle.displayedScan < 0.20,
        "Renderer presents interpolated scan progress between authoritative frames.");
    Require(scanMiddle.needsAnimation && scanMiddle.scanActive,
        "A scan interpolation keeps the animation scheduler active.");
    const auto settled = renderer.AdvanceHeadlessTest(3000);
    Require(!settled.scanActive && !settled.needsAnimation,
        "A stable scan target settles and stops the animation scheduler.");
    (void)scanStart;

    NativeMediaRenderer roll;
    roll.BeginHeadlessTest();
    auto lineOne = MakeFrame(10, Scene::Lyrics, 1, 0.1, std::nullopt);
    roll.Apply(MakeUpdate(lineOne, true));
    roll.AdvanceHeadlessTest(2000);
    auto lineTwo = MakeFrame(11, Scene::Lyrics, 2, 0.2, std::nullopt);
    roll.Apply(MakeUpdate(lineTwo));
    const auto rollStart = roll.AdvanceHeadlessTest(3000);
    Require(rollStart.transitionActive &&
        rollStart.transitionKind == TransitionKind::SingleRoll,
        "A line change starts a single roll.");
    const auto deadline = rollStart.transitionDeadline;
    Require(deadline == 3560, "Single roll uses its original 560 ms deadline.");

    auto lineTwoProgress = MakeFrame(12, Scene::Lyrics, 2, 0.35, std::nullopt);
    roll.Apply(MakeUpdate(lineTwoProgress));
    const auto rollMiddle = roll.AdvanceHeadlessTest(3200);
    Require(rollMiddle.transitionActive && rollMiddle.transitionDeadline == deadline,
        "Progress revisions update the target without restarting a line roll.");
    Require(rollMiddle.transitionProgress > rollStart.transitionProgress,
        "A running line roll advances with wall-clock time.");
    auto lineTwoMore = MakeFrame(13, Scene::Lyrics, 2, 0.5, std::nullopt);
    roll.Apply(MakeUpdate(lineTwoMore));
    const auto rollLate = roll.AdvanceHeadlessTest(3500);
    Require(rollLate.transitionActive && rollLate.transitionDeadline == deadline,
        "Further same-line revisions keep the original transition deadline.");
    const auto rollDone = roll.AdvanceHeadlessTest(3600);
    Require(!rollDone.transitionActive,
        "A line roll completes at its original deadline.");

    auto lineThree = MakeFrame(14, Scene::Lyrics, 3, 0.2, std::nullopt, true);
    roll.Apply(MakeUpdate(lineThree));
    const auto translationStart = roll.AdvanceHeadlessTest(4000);
    Require(translationStart.transitionActive &&
        translationStart.transitionKind == TransitionKind::TranslationRoll,
        "A translation line change starts a paired transition.");
    const auto translationDeadline = translationStart.transitionDeadline;
    auto translationProgress = MakeFrame(15, Scene::Lyrics, 3, 0.4, std::nullopt, true);
    roll.Apply(MakeUpdate(translationProgress));
    const auto translationMiddle = roll.AdvanceHeadlessTest(4300);
    Require(translationMiddle.transitionActive &&
        translationMiddle.transitionDeadline == translationDeadline,
        "Translation progress updates keep the paired transition deadline.");
    const auto translationDone = roll.AdvanceHeadlessTest(4760);
    Require(!translationDone.transitionActive,
        "A translation pair transition completes at its original deadline.");

    auto spectrum = MakeFrame(16, Scene::Spectrum, -1, 0.0, std::nullopt);
    roll.Apply(MakeUpdate(spectrum));
    const auto spectrumStart = roll.AdvanceHeadlessTest(5000);
    Require(spectrumStart.transitionActive &&
        spectrumStart.transitionKind == TransitionKind::LayerSwitch,
        "Lyrics to spectrum uses a layer transition.");
    const auto spectrumDeadline = spectrumStart.transitionDeadline;
    auto spectrumProgress = MakeFrame(17, Scene::Spectrum, -1, 0.1, std::nullopt);
    roll.Apply(MakeUpdate(spectrumProgress));
    const auto spectrumMiddle = roll.AdvanceHeadlessTest(5200);
    Require(spectrumMiddle.transitionActive &&
        spectrumMiddle.transitionDeadline == spectrumDeadline,
        "Spectrum progress frames keep the layer transition deadline.");
    const auto spectrumDone = roll.AdvanceHeadlessTest(5300);
    Require(!spectrumDone.transitionActive,
        "Spectrum layer transition completes at its original deadline.");

    auto lyricsAgain = MakeFrame(18, Scene::Lyrics, 3, 0.2, std::nullopt, true);
    roll.Apply(MakeUpdate(lyricsAgain));
    const auto lyricsStart = roll.AdvanceHeadlessTest(6000);
    Require(lyricsStart.transitionActive &&
        lyricsStart.transitionKind == TransitionKind::LayerSwitch,
        "Spectrum to lyrics uses a layer transition.");
    const auto lyricsDeadline = lyricsStart.transitionDeadline;
    auto lyricsProgress = MakeFrame(19, Scene::Lyrics, 3, 0.3, std::nullopt, true);
    roll.Apply(MakeUpdate(lyricsProgress));
    const auto lyricsMiddle = roll.AdvanceHeadlessTest(6200);
    Require(lyricsMiddle.transitionActive &&
        lyricsMiddle.transitionDeadline == lyricsDeadline,
        "Lyrics progress frames keep the exit layer transition deadline.");
    const auto lyricsDone = roll.AdvanceHeadlessTest(6300);
    Require(!lyricsDone.transitionActive,
        "Lyrics exit layer transition completes at its original deadline.");

    std::cout << "Native renderer lifecycle scheduling assertions passed.\n";
    return 0;
}
