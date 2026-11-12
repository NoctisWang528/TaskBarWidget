#include "CardPch.h"
#include "CardWindow.h"

#include <stdexcept>
#include <iostream>

namespace
{
    void Require(bool condition, const char* message)
    {
        if (!condition) throw std::runtime_error(message);
    }

    int PumpMediaFor(ULONGLONG milliseconds, HWND window)
    {
        int timers = 0;
        const auto until = GetTickCount64() + milliseconds;
        while (GetTickCount64() < until)
        {
            MSG message{};
            while (GetTickCount64() < until && PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
            {
                if (message.hwnd == window && message.message == WM_TIMER) ++timers;
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }
            Sleep(5);
        }
        return timers;
    }

    struct MediaServices : Cards::CardHostServices
    {
        std::wstring action;
        void OnRequest(const std::wstring& value, const std::wstring&) override { action = value; }
        void OnHideCards(const std::wstring&) override {}
        void OnOpenSettings() override {}
        void OnLayoutInvalidated() override {}
    };

    POINT Center(const Cards::CardWindow& card, int id)
    {
        const auto* region = card.Regions().Find(id);
        Require(region != nullptr, "media control has a hit region");
        const auto bounds = card.Bounds();
        const float scale = card.DpiScale();
        return { bounds.left + static_cast<LONG>((region->bounds.left + region->bounds.right) * scale / 2),
            bounds.top + static_cast<LONG>((region->bounds.top + region->bounds.bottom) * scale / 2) };
    }

    void CheckLayoutCache(Cards::Graphics& graphics)
    {
        const auto layout = [&](const std::wstring& text, float scale = 1, bool italic = false,
            float width = 300, const std::wstring& font = L"Segoe UI")
        {
            return graphics.TextLayout(text, font, 13, 600, width, 24, italic, false, false, false, scale);
        };
        const auto first = layout(L"media-cache-regression");
        Require(first.Get() != nullptr, "create cached media text layout");
        const auto creates = graphics.TextLayoutCreateCount();
        for (int scan = 0; scan != 100; ++scan)
            Require(layout(L"media-cache-regression").Get() == first.Get(), "scan frames reuse the same immutable layout");
        Require(graphics.TextLayoutCreateCount() == creates, "scan frames create no text layouts");
        Require(layout(L"media-cache-regression", 1.25f).Get() != first.Get(), "DPI invalidates layout identity");
        Require(layout(L"media-cache-regression", 1, true).Get() != first.Get(), "italic style invalidates layout identity");
        Require(layout(L"media-cache-regression", 1, false, 250).Get() != first.Get(), "width invalidates layout identity");
        Require(layout(L"media-cache-regression", 1, false, 300, L"Microsoft YaHei UI").Get() != first.Get(),
            "font invalidates layout identity");
        for (int line = 0; line != 100; ++line) layout(L"media-cache-line-" + std::to_wstring(line));
        Require(graphics.TextLayoutCacheSize() <= 64, "long playback keeps the text cache bounded");
        DWRITE_TEXT_METRICS metrics{};
        Require(SUCCEEDED(first->GetMetrics(&metrics)), "eviction cannot invalidate a layout retained by a painter");
    }
}

// Link into the isolated native-card diagnostic and call after its shared
// compositor/device setup. No pipe, player, audio device or weather HTTP is used.
void TestMediaRedraw(Cards::CardEnvironment environment)
{
    using namespace Cards;
    Require(environment.graphics && environment.device && environment.compositor, "media diagnostic has shared graphics");
    CheckLayoutCache(*environment.graphics);
    {
        MediaServices weatherServices;
        CardWindow weather(Kind::Weather, weatherServices);
        Snapshot update;
        update.weather.city = L"Fixture city";
        update.weather.forecast.resize(7);
        weather.SetSnapshot(update);
        weather.SetWeatherView(true, 5);
        weather.SetSnapshot(update);
        Require(weather.CurrentSnapshot().weather.forecastView && weather.CurrentSnapshot().weather.forecastOffset == 5,
            "weather updates preserve the selected view and page");
        weather.SetWeatherView(false);
        Require(!weather.CurrentSnapshot().weather.forecastView && weather.CurrentSnapshot().weather.forecastOffset == 0,
            "hourly view resets the forecast page");
    }
    environment.reducedMotion = false;
    environment.highContrast = false;
    MediaServices services;
    CardWindow card(Kind::Media, services);
    Require(card.Create(environment, GetModuleHandleW(nullptr)), "create media redraw diagnostic");
    Snapshot snapshot;
    snapshot.media.coverKey = L"fixture-red";
    snapshot.media.coverDataUrl = L"data:image/bmp;base64,Qk06AAAAAAAAADYAAAAoAAAAAQAAAAEAAAABABgAAAAAAAQAAAATCwAAEwsAAAAAAAAAAAAAAAD/AA==";
    Require(environment.graphics->DecodeImage(snapshot.media.coverDataUrl, 128, 128) != nullptr, "decode synthetic cover fixture");
    snapshot.media.title = L"Redraw fixture";
    snapshot.media.artist = L"Artist";
    snapshot.media.selectedSourceId = L"fixture";
    snapshot.media.sources.push_back({ L"fixture", L"Source", L"" });
    snapshot.media.isPlaying = true;
    snapshot.media.canPlayPause = snapshot.media.canPrevious = snapshot.media.canNext = snapshot.media.canSeek = true;
    snapshot.media.duration = 180;
    snapshot.media.position = 10;
    snapshot.lyrics.available = snapshot.lyrics.isPlaying = true;
    snapshot.lyrics.trackId = L"fixture-track";
    snapshot.lyrics.current = L"A stable lyric line";
    snapshot.lyrics.next = L"Next line";
    snapshot.lyrics.currentLineIndex = 0;
    snapshot.lyrics.wordScan = 0.2;
    snapshot.lyrics.revision = 1;
    card.SetSnapshot(snapshot);
    const LONG x = GetSystemMetrics(SM_XVIRTUALSCREEN) - 2000;
    const LONG y = GetSystemMetrics(SM_YVIRTUALSCREEN) - 2000;
    card.Place({ x, y, x + 384, y + 248 }, 1);
    card.Show(false);
    Require(card.HasRenderedContent() && card.StaticPaintCount() && card.DynamicPaintCount(), "both media surfaces contain pixels");
    const auto stable = card.StaticPaintCount();
    const auto regions = card.RegionBuildCount();
    const auto dynamic = card.DynamicPaintCount();
    PumpMediaFor(80, card.Handle());
    snapshot.lyrics.wordScan = 0.3;
    ++snapshot.lyrics.revision;
    card.SetSnapshot(snapshot);
    PumpMediaFor(120, card.Handle());
    Require(card.DynamicPaintCount() > dynamic, "playing scan repaints the dynamic surface");
    Require(card.StaticPaintCount() == stable && card.RegionBuildCount() == regions,
        "playing scan retains metadata, controls and hit regions");
    std::cout << "Media redraw sample: dynamic=" << card.DynamicPaintCount() - dynamic
        << ", static=" << card.StaticPaintCount() - stable << ", regions=" << card.RegionBuildCount() - regions << '\n';
    Require(card.CurrentSnapshot().media.position > 10.1 && card.CurrentSnapshot().lyrics.wordScan > 0.3,
        "timeline and scan still interpolate");

    snapshot.media.isPlaying = snapshot.lyrics.isPlaying = false;
    card.SetSnapshot(snapshot);
    PumpMediaFor(220, card.Handle());
    const auto paused = card.ContentPaintCount();
    for (int duplicate = 0; duplicate != 30; ++duplicate)
    {
        ++snapshot.media.revision;
        ++snapshot.lyrics.revision;
        snapshot.weather.city = L"unrelated weather";
        ++snapshot.audio.revision;
        card.SetSnapshot(snapshot);
    }
    Require(card.ContentPaintCount() == paused, "duplicate and unrelated snapshots do not repaint paused media");
    Require(PumpMediaFor(300, card.Handle()) == 0 && card.ContentPaintCount() == paused,
        "paused media has no idle redraw timer");

    const auto seek = Center(card, 4);
    card.OnPointer(WM_MOUSEMOVE, seek, false);
    const auto seekStable = card.StaticPaintCount();
    const auto seekDynamic = card.DynamicPaintCount();
    PumpMediaFor(180, card.Handle());
    Require(card.StaticPaintCount() == seekStable && card.DynamicPaintCount() > seekDynamic,
        "seek hover animates only the dynamic layer");
    card.OnPointer(WM_LBUTTONDOWN, seek, true);
    Require(GetCapture() == card.Handle() && card.CurrentInteraction().pressed == 4, "seek retains pointer capture");
    snapshot.media.canSeek = false;
    services.action.clear();
    card.SetSnapshot(snapshot);
    Require(card.RegionBuildCount() > regions && card.Regions().Find(2) && card.Regions().Find(3) &&
        card.Regions().Find(38) && card.Regions().Find(100) && card.Regions().Find(162),
        "capability changes rebuild the complete hit table");
    Require(GetCapture() != card.Handle() && card.CurrentInteraction().pressed == 0, "disabled seek cancels capture");
    card.OnPointer(WM_LBUTTONUP, seek, false);
    Require(services.action.empty(), "disabled seek never commits a canceled drag");
    card.OnMouseLeave();
    PumpMediaFor(180, card.Handle());

    const auto source = Center(card, 2);
    card.OnPointer(WM_LBUTTONDOWN, source, true);
    card.OnPointer(WM_LBUTTONUP, source, false);
    Require(services.action == L"showSources", "retained source selector still invokes its command");
    card.OnMouseLeave();
    PumpMediaFor(180, card.Handle());

    const auto fadeStable = card.StaticPaintCount();
    const auto fadeDynamic = card.DynamicPaintCount();
    snapshot.media.coverKey = L"fixture-blue";
    snapshot.media.coverDataUrl = L"data:image/bmp;base64,Qk06AAAAAAAAADYAAAAoAAAAAQAAAAEAAAABABgAAAAAAAQAAAATCwAAEwsAAAAAAAAAAAAA/wAAAA==";
    card.SetSnapshot(snapshot);
    PumpMediaFor(80, card.Handle());
    Require(card.StaticPaintCount() == fadeStable && card.DynamicPaintCount() > fadeDynamic + 1,
        "cover crossfade advances without repainting stable controls");
    PumpMediaFor(220, card.Handle());
    const auto faded = card.ContentPaintCount();
    Require(PumpMediaFor(120, card.Handle()) == 0 && card.ContentPaintCount() == faded,
        "completed paused cover fade stops its timer");

    const auto beforeMove = card.ContentPaintCount();
    auto moved = card.Bounds();
    OffsetRect(&moved, 9, -12);
    HDWP batch = BeginDeferWindowPos(1);
    Require(batch != nullptr, "start atomic group move fixture");
    batch = DeferWindowPos(batch, card.Handle(), nullptr, moved.left, moved.top, 0, 0,
        SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    Require(batch && EndDeferWindowPos(batch), "commit atomic group move fixture");
    card.CommitGroupBounds(moved);
    const auto cached = card.Bounds();
    Require(EqualRect(&cached, &moved) && card.ContentPaintCount() == beforeMove,
        "group bounds commit updates only cached position");
    const auto beforeFont = card.StaticPaintCount();
    environment.fontFamily = L"Segoe UI";
    card.ApplyEnvironment(environment);
    Require(card.StaticPaintCount() > beforeFont, "font changes invalidate stable pixels");
    const auto beforeDpi = card.StaticPaintCount();
    card.Place({ x, y, x + 480, y + 310 }, 1.25f);
    Require(card.StaticPaintCount() > beforeDpi, "DPI changes invalidate stable pixels");

    card.Hide(false);
    const auto hidden = card.ContentPaintCount();
    snapshot.media.isPlaying = snapshot.lyrics.isPlaying = true;
    card.SetSnapshot(snapshot);
    card.Tick();
    Require(PumpMediaFor(120, card.Handle()) == 0 && card.ContentPaintCount() == hidden,
        "hidden playback performs no timer paints");
}
