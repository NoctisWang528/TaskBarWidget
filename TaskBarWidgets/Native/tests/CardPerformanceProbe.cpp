#include "CardPch.h"
#include "CardRuntime.h"
#include "CardWindow.h"
#include <psapi.h>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <locale>
#include <sstream>
#include <stdexcept>

namespace
{
    using Clock = std::chrono::steady_clock;
    using CardList = std::vector<std::unique_ptr<Cards::CardWindow>>;

    struct Services : Cards::CardHostServices
    {
        void OnRequest(const std::wstring&, const std::wstring&) override {}
        void OnHideCards(const std::wstring&) override {}
        void OnOpenSettings() override {}
        void OnLayoutInvalidated() override {}
    };

    void Require(bool condition, const char* message)
    {
        if (!condition) throw std::runtime_error(message);
    }

    double Milliseconds(Clock::time_point since)
    {
        return std::chrono::duration<double, std::milli>(Clock::now() - since).count();
    }

    void Pump(unsigned milliseconds)
    {
        const auto deadline = Clock::now() + std::chrono::milliseconds(milliseconds);
        do
        {
            MSG message{};
            while (Clock::now() < deadline && PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
            {
                if (message.message == WM_QUIT)
                {
                    PostQuitMessage(static_cast<int>(message.wParam));
                    throw std::runtime_error("Card measurement interrupted by WM_QUIT");
                }
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }
            if (Clock::now() < deadline)
                MsgWaitForMultipleObjectsEx(0, nullptr, 5, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
        } while (Clock::now() < deadline);
    }

    struct Sample
    {
        SIZE_T privateBytes{}, workingSet{};
        double cpuMs{};
    };

    Sample ReadSample()
    {
        PROCESS_MEMORY_COUNTERS_EX memory{};
        memory.cb = sizeof(memory);
        Require(K32GetProcessMemoryInfo(GetCurrentProcess(),
            reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&memory), sizeof(memory)) != FALSE,
            "Read card measurement process memory");
        FILETIME created{}, exited{}, kernel{}, user{};
        Require(GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user) != FALSE,
            "Read card measurement process CPU time");
        const auto ticks = [](FILETIME time) {
            return (static_cast<uint64_t>(time.dwHighDateTime) << 32) | time.dwLowDateTime;
        };
        return {memory.PrivateUsage, memory.WorkingSetSize, (ticks(kernel) + ticks(user)) / 10000.0};
    }

    void WriteSample(std::ostream& output, const Sample& sample)
    {
        output << "{\"privateBytes\":" << sample.privateBytes
            << ",\"workingSet\":" << sample.workingSet << ",\"cpuMs\":" << sample.cpuMs << '}';
    }

    uint64_t Paints(const CardList& cards)
    {
        uint64_t count{};
        for (const auto& card : cards) count += card->ContentPaintCount();
        return count;
    }

    // This measures HWND visibility only, including synchronous Show/Hide work.
    // It is neither animation completion nor a GPU/presentation timestamp.
    double SetVisible(CardList& cards, bool visible)
    {
        const auto started = Clock::now();
        for (auto& card : cards)
        {
            if (visible) card->Show(true);
            else card->Hide(true);
        }
        while (!std::all_of(cards.begin(), cards.end(), [visible](const auto& card) {
            return card->Visible() == visible && (IsWindowVisible(card->Handle()) != FALSE) == visible;
        }))
        {
            Require(Milliseconds(started) < 2000, "Card HWND visibility did not settle within two seconds");
            Pump(5);
        }
        return Milliseconds(started);
    }
}

// Caller owns the STA apartment and initialized process-wide WinUiRuntime.
// Writes only outputPath, after all owned windows/graphics/dispatcher are released.
// Throws on initialization, visibility, measurement or output failure.
void MeasureCardPerformance(const wchar_t* outputPath)
{
    Require(outputPath && *outputPath, "Card measurement requires a JSON output path");
    std::ostringstream stages;
    stages.imbue(std::locale::classic());
    stages << std::fixed << std::setprecision(3);
    const auto started = Clock::now();
    const auto baseline = ReadSample();
    uint64_t totalPaints{};
    bool glassMeasurement = false;
    double cardPreparationMs{};
    {
        Cards::Runtime runtime;
        winrt::check_hresult(runtime.Initialize());
        Cards::Graphics graphics;
        Require(graphics.Initialize(), "Card measurement graphics initialization");
        auto compositor = winrt::Windows::UI::Composition::Compositor();
        std::wstring error;
        auto device = graphics.CreateGraphicsDevice(compositor, error);
        Require(device != nullptr, "Card measurement composition device initialization");
        Cards::CardEnvironment environment;
        environment.graphics = &graphics;
        environment.compositor = compositor;
        environment.device = device;
        environment.dark = true;
        environment.reducedMotion = false;
        environment.highContrast = false;
        // Opt-in diagnostic only; no settings/protocol mutation. This reports
        // process CPU/visibility timing, not DWM GPU time or presented frames.
        wchar_t materialMode[32]{};
        if (GetEnvironmentVariableW(L"TASKBARWIDGETS_PROBE_MATERIAL", materialMode, ARRAYSIZE(materialMode)) &&
            wcscmp(materialMode, L"LiquidGlass") == 0)
        {
            environment.material.Set(L"LiquidGlass", .35);
            glassMeasurement = true;
        }
        Services services;
        Cards::Snapshot snapshot;
        snapshot.media.title = L"Deterministic card measurement";
        snapshot.media.artist = L"Synthetic artist";
        snapshot.media.duration = 180;
        snapshot.media.position = 10;
        snapshot.media.canPlayPause = snapshot.media.canSeek = true;
        snapshot.media.revision = 1;
        snapshot.lyrics.current = L"A synthetic lyric line";
        snapshot.lyrics.next = L"The next synthetic lyric line";
        snapshot.lyrics.available = true;
        snapshot.lyrics.currentLineIndex = 0;
        snapshot.lyrics.wordScan = 0.2;
        snapshot.lyrics.revision = 1;
        snapshot.weather.hasData = true;
        snapshot.weather.city = L"Fixture city";
        snapshot.weather.temperature = L"23";
        snapshot.weather.conditionKind = L"clear";
        snapshot.traffic.available = true;
        snapshot.traffic.revision = 1;
        for (int i = 0; i < 84; ++i)
        {
            const std::chrono::year_month_day date{std::chrono::sys_days{
                std::chrono::year{2026}/8/3} + std::chrono::days{i}};
            wchar_t text[16]{};
            swprintf_s(text, L"%04d-%02u-%02u", static_cast<int>(date.year()),
                static_cast<unsigned>(date.month()), static_cast<unsigned>(date.day()));
            Cards::TrafficDay day;
            day.week = i / 7; day.weekday = i % 7; day.date = text;
            day.known = true; day.level = i % 5; day.totalKiB = (i % 5) * 1024;
            snapshot.traffic.days.push_back(day);
        }
        snapshot.traffic.today = snapshot.traffic.days.back().date;
        snapshot.traffic.days.back().isToday = true;
        CardList cards;
        // Keep every HWND above and left of the entire virtual desktop.
        const LONG x = GetSystemMetrics(SM_XVIRTUALSCREEN) - 4096;
        const LONG y = GetSystemMetrics(SM_YVIRTUALSCREEN) - 4096;
        const auto preparationStarted = Clock::now();
        for (auto kind : {Cards::Kind::Weather, Cards::Kind::Traffic, Cards::Kind::Device, Cards::Kind::Media})
        {
            auto card = std::make_unique<Cards::CardWindow>(kind, services);
            Require(card->Create(environment, GetModuleHandleW(nullptr)), "Create offscreen measurement card");
            card->SetSnapshot(snapshot);
            const LONG width = static_cast<LONG>(Cards::Layout::WidthDip(kind));
            const LONG height = static_cast<LONG>(Cards::Layout::DefaultHeightDip(kind));
            card->Place(RECT{x, y, x + width, y + height}, 1.0f);
            card->Paint();
            Require(!IsWindowVisible(card->Handle()), "Measurement card must begin hidden");
            cards.push_back(std::move(card));
        }
        cardPreparationMs = Milliseconds(preparationStarted);
        bool first = true;
        const auto stage = [&](const char* name, auto action) {
            const auto before = ReadSample();
            const auto paints = Paints(cards);
            const auto stageStarted = Clock::now();
            const double visibilityMs = action();
            Pump(2000);
            const auto after = ReadSample();
            if (!first) stages << ',';
            first = false;
            stages << "{\"name\":\"" << name << "\",\"elapsedMs\":" << Milliseconds(stageStarted)
                << ",\"hwndVisibilitySettlementMs\":" << visibilityMs << ",\"baseline\":";
            WriteSample(stages, before);
            stages << ",\"final\":";
            WriteSample(stages, after);
            stages << ",\"cpuDeltaMs\":" << after.cpuMs - before.cpuMs
                << ",\"privateBytesDelta\":" << static_cast<int64_t>(after.privateBytes) - static_cast<int64_t>(before.privateBytes)
                << ",\"workingSetDelta\":" << static_cast<int64_t>(after.workingSet) - static_cast<int64_t>(before.workingSet)
                << ",\"contentPaintCount\":" << Paints(cards)
                << ",\"contentPaintDelta\":" << Paints(cards) - paints << '}';
        };
        stage("hidden", [&] { return SetVisible(cards, false); });
        stage("visibleStatic", [&] { return SetVisible(cards, true); });
        stage("mediaPlaying", [&] {
            snapshot.media.isPlaying = snapshot.lyrics.isPlaying = true;
            ++snapshot.media.revision; ++snapshot.lyrics.revision;
            cards.back()->SetSnapshot(snapshot);
            return SetVisible(cards, true);
        });
        stage("rapidToggles", [&] {
            snapshot.media.isPlaying = snapshot.lyrics.isPlaying = false;
            ++snapshot.media.revision; ++snapshot.lyrics.revision;
            cards.back()->SetSnapshot(snapshot);
            for (int i = 0; i < 19; ++i)
            {
                for (auto& card : cards) { if (i % 2) card->Show(true); else card->Hide(true); }
                Pump(80);
            }
            // The twentieth request shows the cards; preceding requests intentionally interrupt.
            return SetVisible(cards, true);
        });
        stage("hiddenAfterToggles", [&] { return SetVisible(cards, false); });
        totalPaints = Paints(cards);
    }
    Pump(2000);
    const auto final = ReadSample();
    const double elapsed = Milliseconds(started);
    std::ofstream output(std::filesystem::path(outputPath), std::ios::binary | std::ios::trunc);
    output.exceptions(std::ios::failbit | std::ios::badbit);
    output.imbue(std::locale::classic());
    output << std::fixed << std::setprecision(3)
        << "{\"schemaVersion\":1,\"sequence\":\"cards-v1\",\"offscreen\":true,"
        << "\"timingScope\":\"HWND visibility only; not animation completion or presented frames\","
        << "\"dwellMs\":2000,\"toggleCount\":20,\"toggleIntervalMs\":80,"
        << "\"scale\":1,\"dark\":true,\"reducedMotion\":false,\"highContrast\":false,"
        << "\"material\":\"" << (glassMeasurement ? "LiquidGlass" : "Mica") << "\","
        << "\"cardPreparationMs\":" << cardPreparationMs << ','
        << "\"elapsedMs\":" << elapsed << ",\"baseline\":";
    WriteSample(output, baseline);
    output << ",\"final\":";
    WriteSample(output, final);
    output << ",\"cpuDeltaMs\":" << final.cpuMs - baseline.cpuMs
        << ",\"privateBytesDelta\":" << static_cast<int64_t>(final.privateBytes) - static_cast<int64_t>(baseline.privateBytes)
        << ",\"workingSetDelta\":" << static_cast<int64_t>(final.workingSet) - static_cast<int64_t>(baseline.workingSet)
        << ",\"contentPaintCount\":" << totalPaints << ",\"stages\":[" << stages.str() << "]}\n";
    output.close();
}
