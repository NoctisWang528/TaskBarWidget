#include "../TrafficMonitor/NativeMediaRenderer.h"

#include <cmath>
#include <iostream>
#include <stdexcept>
#include <string>

namespace NativeMedia
{
    struct NativeMediaRendererTestAccess
    {
        static bool ActiveCoverWasPainted(NativeMediaRenderer& renderer)
        {
            const auto& layer = renderer.m_coverFrontIsActive ? renderer.m_coverFront : renderer.m_coverBack;
            return layer.hasContent && layer.target != nullptr;
        }
    };
}

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

    bool Near(double actual, double expected, double tolerance = 1e-6)
    {
        return std::abs(actual - expected) <= tolerance;
    }

    void RequireScan(const NativeMedia::RendererProbe& probe, double expected, const char* message)
    {
        Require(probe.displayedScan.has_value(), message);
        Require(Near(*probe.displayedScan, expected), message);
    }

    NativeMedia::Frame MakePausedFrame(std::uint64_t revision, NativeMedia::Scene scene,
        int line, double progress, std::optional<double> scan)
    {
        auto frame = MakeFrame(revision, scene, line, progress, scan);
        frame.isPlaying = false;
        frame.lyrics.isPlaying = false;
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
try
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
    // The tick that establishes the new target must present the interpolation
    // start, not jump to the endpoint and walk backwards afterwards.
    RequireScan(scanStart, 0.10, "A new scan target starts at the previous value.");
    Require(scanMiddle.displayedScan.has_value(), "Scan remains available during smoothing.");
    Require(*scanMiddle.displayedScan > 0.10 && *scanMiddle.displayedScan < 0.20,
        "Renderer presents interpolated scan progress between authoritative frames.");
    Require(scanMiddle.needsAnimation && scanMiddle.scanActive,
        "A scan interpolation keeps the animation scheduler active.");
    const auto settled = renderer.AdvanceHeadlessTest(3000);
    Require(!settled.scanActive && !settled.needsAnimation,
        "A stable scan target settles and stops the animation scheduler.");

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

    // ---- Issue 2: a new target must present start/middle/end in order. ----
    {
        NativeMediaRenderer scan;
        scan.BeginHeadlessTest();
        auto seed = MakeFrame(100, Scene::Lyrics, 1, 0.10, 0.10);
        scan.Apply(MakeUpdate(seed, true));
        RequireScan(scan.AdvanceHeadlessTest(10000), 0.10, "Seed scan value is immediate.");

        auto target = MakeFrame(101, Scene::Lyrics, 1, 0.10, 0.20);
        scan.Apply(MakeUpdate(target));
        RequireScan(scan.AdvanceHeadlessTest(10010), 0.10,
            "The tick that observes a new target presents the interpolation start.");
        RequireScan(scan.AdvanceHeadlessTest(10055), 0.15,
            "The middle tick presents the interpolated value.");
        RequireScan(scan.AdvanceHeadlessTest(10100), 0.20,
            "The deadline tick presents the target.");

        auto monotonic = MakeFrame(102, Scene::Lyrics, 1, 0.10, 0.20);
        scan.Apply(MakeUpdate(monotonic));
        auto settledStart = scan.AdvanceHeadlessTest(10200);
        RequireScan(settledStart, 0.20, "An already reached target stays at the endpoint.");
        double previous = *settledStart.displayedScan;
        for (int step = 1; step <= 6; ++step)
        {
            auto probe = scan.AdvanceHeadlessTest(10200 + static_cast<std::uint64_t>(step) * 5);
            Require(probe.displayedScan.has_value(), "A settled scan keeps its value.");
            Require(*probe.displayedScan >= previous - 1e-9,
                "A settled scan never moves backwards.");
            previous = *probe.displayedScan;
        }
        Require(!scan.AdvanceHeadlessTest(10300).needsAnimation,
            "A converged scan stops requesting animation frames.");
    }

    // ---- Issue 2: full forward sequence is monotonic from start to end. ----
    {
        NativeMediaRenderer sequence;
        sequence.BeginHeadlessTest();
        auto seed = MakeFrame(110, Scene::Lyrics, 4, 0.2, 0.10);
        sequence.Apply(MakeUpdate(seed, true));
        RequireScan(sequence.AdvanceHeadlessTest(20000), 0.10, "Sequence seed value.");

        auto target = MakeFrame(111, Scene::Lyrics, 4, 0.2, 0.20);
        sequence.Apply(MakeUpdate(target));
        double previous = *sequence.AdvanceHeadlessTest(20010).displayedScan;
        Require(Near(previous, 0.10), "Forward scan starts at the previous value.");
        for (int step = 1; step <= 9; ++step)
        {
            const auto probe = sequence.AdvanceHeadlessTest(
                20010 + static_cast<std::uint64_t>(step) * 10);
            Require(probe.displayedScan.has_value(), "Forward scan keeps a value.");
            Require(*probe.displayedScan >= previous - 1e-9,
                "Forward scan output never goes backwards.");
            Require(*probe.displayedScan <= 0.20 + 1e-9,
                "Forward scan output never overshoots the target.");
            previous = *probe.displayedScan;
        }
        Require(Near(previous, 0.20), "Forward scan ends exactly at the target.");
    }

    // ---- Issue 2: identical targets never restart the interpolation. ----
    {
        NativeMediaRenderer steady;
        steady.BeginHeadlessTest();
        auto seed = MakeFrame(120, Scene::Lyrics, 2, 0.3, 0.10);
        steady.Apply(MakeUpdate(seed, true));
        RequireScan(steady.AdvanceHeadlessTest(30000), 0.10, "Restart-check seed value.");

        auto target = MakeFrame(121, Scene::Lyrics, 2, 0.3, 0.20);
        steady.Apply(MakeUpdate(target));
        RequireScan(steady.AdvanceHeadlessTest(30010), 0.10, "Restart-check start value.");
        RequireScan(steady.AdvanceHeadlessTest(30050), 0.144444, "Restart-check midpoint.");
        // A newer revision with the same scan target must not restart the 90 ms
        // interpolation that started at 30010.
        auto repeated = MakeFrame(122, Scene::Lyrics, 2, 0.3, 0.20);
        steady.Apply(MakeUpdate(repeated));
        RequireScan(steady.AdvanceHeadlessTest(30100), 0.20,
            "The original interpolation deadline still applies to a repeated target.");
    }

    // ---- Issue 2: seek/jump corrections stay immediate. ----
    {
        NativeMediaRenderer corrections;
        corrections.BeginHeadlessTest();
        auto seed = MakeFrame(130, Scene::Lyrics, 3, 0.4, 0.60);
        corrections.Apply(MakeUpdate(seed, true));
        RequireScan(corrections.AdvanceHeadlessTest(40000), 0.60, "Correction seed value.");

        auto backward = MakeFrame(131, Scene::Lyrics, 3, 0.4, 0.20);
        corrections.Apply(MakeUpdate(backward));
        RequireScan(corrections.AdvanceHeadlessTest(40010), 0.20,
            "A backward seek corrects the scan immediately.");

        auto forwardJump = MakeFrame(132, Scene::Lyrics, 3, 0.4, 0.90);
        corrections.Apply(MakeUpdate(forwardJump));
        RequireScan(corrections.AdvanceHeadlessTest(40020), 0.90,
            "A large forward jump corrects the scan immediately.");
    }

    // ---- Issue 3: a reveal restores the scan from the retained frame. ----
    {
        NativeMediaRenderer reveal;
        reveal.BeginHeadlessTest();
        auto paused = MakePausedFrame(200, Scene::Lyrics, 2, 0.3, 0.62);
        reveal.Apply(MakeUpdate(paused, true));
        RequireScan(reveal.AdvanceHeadlessTest(50000), 0.62, "Paused scan is presented.");

        reveal.SetVisible(false, false);
        reveal.SetVisible(true, false);
        RequireScan(reveal.AdvanceHeadlessTest(50100), 0.62,
            "A paused hide/reveal restores the same scan without waiting for lyrics.");

        auto repeated = MakePausedFrame(201, Scene::Lyrics, 2, 0.3, 0.62);
        reveal.Apply(MakeUpdate(repeated));
        RequireScan(reveal.AdvanceHeadlessTest(50200), 0.62,
            "A repeated identical snapshot still shows the scan.");

        // Window rebinding or device resource recovery re-enters the reveal path.
        reveal.DetachHost();
        reveal.SetVisible(true, false);
        RequireScan(reveal.AdvanceHeadlessTest(50300), 0.62,
            "Rebinding the host restores the scan from the retained frame.");
    }

    // ---- Issue 3: a reveal during a word gap must not invent a scan value. ----
    {
        NativeMediaRenderer gap;
        gap.BeginHeadlessTest();
        auto gapFrame = MakeFrame(210, Scene::Lyrics, 3, 0.5, std::nullopt);
        gap.Apply(MakeUpdate(gapFrame, true));
        Require(!gap.AdvanceHeadlessTest(60000).displayedScan.has_value(),
            "A word gap presents no scan.");

        gap.SetVisible(false, false);
        gap.SetVisible(true, false);
        Require(!gap.AdvanceHeadlessTest(60100).displayedScan.has_value(),
            "A word-gap reveal keeps the scan empty instead of reusing a stale value.");

        auto nextWord = MakeFrame(211, Scene::Lyrics, 3, 0.5, 0.40);
        gap.Apply(MakeUpdate(nextWord));
        RequireScan(gap.AdvanceHeadlessTest(60200), 0.40,
            "The next authoritative scan target appears after a reveal.");
    }

    // ---- Issue 4: reduced motion always presents the authoritative target. ----
    {
        NativeMediaRenderer reduced;
        reduced.BeginHeadlessTest();
        reduced.SetOptions(true, false);
        auto frame = MakeFrame(300, Scene::Lyrics, 5, 0.2, 0.30);
        reduced.Apply(MakeUpdate(frame, true));
        const auto reducedStart = reduced.AdvanceHeadlessTest(70000);
        RequireScan(reducedStart, 0.30, "Reduced motion presents the authoritative scan.");
        Require(!reducedStart.scanActive, "Reduced motion does not animate the scan.");

        const auto reducedSecond = reduced.AdvanceHeadlessTest(70016);
        RequireScan(reducedSecond, 0.30,
            "A later paint with the same target keeps the scan in reduced motion.");

        auto lineChange = MakeFrame(301, Scene::Lyrics, 6, 0.0, 0.30);
        reduced.Apply(MakeUpdate(lineChange));
        RequireScan(reduced.AdvanceHeadlessTest(70032), 0.30,
            "A line-change repaint keeps the scan in reduced motion.");

        reduced.SetOptions(false, false);
        RequireScan(reduced.AdvanceHeadlessTest(70048), 0.30,
            "Leaving reduced motion rebuilds the scan instead of clearing it.");

        reduced.SetOptions(true, false);
        RequireScan(reduced.AdvanceHeadlessTest(70064), 0.30,
            "Re-entering reduced motion keeps the authoritative scan.");

        reduced.SetVisible(false, false);
        reduced.SetVisible(true, false);
        RequireScan(reduced.AdvanceHeadlessTest(70080), 0.30,
            "A reduced-motion reveal keeps the scan.");
    }

    // Exercise actual composition layers: painter-only fixtures cannot catch
    // painting the hidden standby swap chain instead of the visible cover.
    {
        const HRESULT apartment = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
        Require(SUCCEEDED(apartment), "COM initializes for the isolated renderer.");
        HWND window = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            L"STATIC", L"Cover layer regression", WS_POPUP, -16000, -16000,
            400, 48, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
        Require(window != nullptr, "An inactive offscreen host exists.");
        {
            NativeMediaRenderer covers;
            covers.SetHost(window);
            covers.UpdateGeometry(400, 48, 1);
            Require(covers.EnsureInitialized(GetTickCount64()), "Composition initializes.");
            auto frame = MakeFrame(400, Scene::Lyrics, 1, 0, std::nullopt);
            Cover cover;
            cover.fallbackText = L"A";
            cover.fallbackColor = L"#ff0000";
            cover.trackId = frame.trackId;
            auto update = MakeUpdate(frame, true);
            update.cover = &cover;
            update.coverChanged = true;
            covers.Apply(update);
            covers.SetVisible(true, false);
            auto now = GetTickCount64();
            covers.Present(now);
            Require(NativeMediaRendererTestAccess::ActiveCoverWasPainted(covers),
                "The initially visible cover layer was painted and presented.");
            for (int i = 0; i < 2; ++i)
            {
                cover.fallbackText = i ? L"C" : L"B";
                update.forceFull = false;
                covers.Apply(update);
                covers.Present(now += 1000);
                covers.Present(now += 500);
                covers.SetVisible(false, false);
                covers.SetVisible(true, false);
                covers.Present(now += 1000);
                Require(NativeMediaRendererTestAccess::ActiveCoverWasPainted(covers),
                    "Both swapped cover layers repaint correctly after hide/reveal.");
            }
        }
        DestroyWindow(window);
        CoUninitialize();
    }
    std::cout << "Native renderer lifecycle scheduling assertions passed.\n";
    return 0;
}
catch (const std::exception& error)
{
    std::cerr << "Renderer regression failed: " << error.what() << '\n';
    return 1;
}
