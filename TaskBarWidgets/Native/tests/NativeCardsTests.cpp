#include "CardPch.h"
#include "CardRuntime.h"
#include "CardHost.h"
#include "CardWindow.h"
#include "CardDraw.h"
#include "CardProtocol.h"
#include "CardPopup.h"
#include <iostream>
#include <stdexcept>
#include <dbghelp.h>
#pragma comment(lib, "dbghelp.lib")

static LONG WINAPI ReportCrash(EXCEPTION_POINTERS* exception)
{
    const HANDLE process = GetCurrentProcess();
    SymSetOptions(SYMOPT_LOAD_LINES | SYMOPT_UNDNAME);
    SymInitialize(process, nullptr, TRUE);
    CONTEXT context = *exception->ContextRecord;
    STACKFRAME64 frame{};
    frame.AddrPC.Offset = context.Rip; frame.AddrPC.Mode = AddrModeFlat;
    frame.AddrStack.Offset = context.Rsp; frame.AddrStack.Mode = AddrModeFlat;
    frame.AddrFrame.Offset = context.Rbp; frame.AddrFrame.Mode = AddrModeFlat;
    std::cerr << "Native probe exception: 0x" << std::hex << exception->ExceptionRecord->ExceptionCode << '\n';
    for (int depth = 0; depth < 20 && frame.AddrPC.Offset; ++depth)
    {
        char storage[sizeof(SYMBOL_INFO) + MAX_SYM_NAME]{};
        auto* symbol = reinterpret_cast<SYMBOL_INFO*>(storage);
        symbol->SizeOfStruct = sizeof(SYMBOL_INFO); symbol->MaxNameLen = MAX_SYM_NAME;
        DWORD64 displacement{};
        if (SymFromAddr(process, frame.AddrPC.Offset, &displacement, symbol))
            std::cerr << symbol->Name << "+0x" << displacement << '\n';
        else std::cerr << "0x" << frame.AddrPC.Offset << '\n';
        IMAGEHLP_LINE64 line{sizeof(line)};
        DWORD lineOffset{};
        if (SymGetLineFromAddr64(process, frame.AddrPC.Offset, &lineOffset, &line))
            std::cerr << line.FileName << ':' << std::dec << line.LineNumber << std::hex << '\n';
        if (!StackWalk64(IMAGE_FILE_MACHINE_AMD64, process, GetCurrentThread(), &frame, &context,
            nullptr, SymFunctionTableAccess64, SymGetModuleBase64, nullptr)) break;
    }
    return EXCEPTION_EXECUTE_HANDLER;
}

static void Require(bool ok, const char* message) { if (!ok) throw std::runtime_error(message); }
static int VisibleProbeCards()
{
    int count = 0;
    EnumWindows([](HWND window, LPARAM data) -> BOOL
    {
        DWORD process{};
        GetWindowThreadProcessId(window, &process);
        wchar_t name[80]{};
        GetClassNameW(window, name, ARRAYSIZE(name));
        if (process == GetCurrentProcessId() && wcscmp(name, L"TaskBarWidgetsCardWindow") == 0 && IsWindowVisible(window))
        {
            RECT bounds{}; GetWindowRect(window, &bounds);
            if (bounds.right < GetSystemMetrics(SM_XVIRTUALSCREEN) &&
                bounds.bottom < GetSystemMetrics(SM_YVIRTUALSCREEN)) ++*reinterpret_cast<int*>(data);
        }
        return TRUE;
    }, reinterpret_cast<LPARAM>(&count));
    return count;
}

static void WaitForCards(Cards::CardHost& host, int count)
{
    const auto deadline = GetTickCount64() + 10000;
    while (GetTickCount64() < deadline)
    {
        if (host.Visible() == (count != 0) && VisibleProbeCards() == count) return;
        Sleep(10);
    }
    throw std::runtime_error("host toggle did not show/hide the expected offscreen windows");
}
struct Services : Cards::CardHostServices
{
    std::wstring action;
    void OnRequest(const std::wstring& value, const std::wstring&) override { action = value; }
    void OnHideCards(const std::wstring&) override {}
    void OnOpenSettings() override {}
    void OnMenuOpened(bool) override {}
    void OnLayoutInvalidated() override {}
};

void WriteTrafficPreviews(Cards::Graphics& graphics, const wchar_t* directory);

int wmain(int argc, wchar_t** argv)
{
    SetUnhandledExceptionFilter(ReportCrash);
    std::cout.setf(std::ios::unitbuf);
    try
    {
        winrt::init_apartment(winrt::apartment_type::single_threaded);
        if (argc == 3 && wcscmp(argv[1], L"--traffic-preview") == 0)
        {
            {
                Cards::Graphics graphics;
                Require(graphics.Initialize(), "preview graphics initialization");
                WriteTrafficPreviews(graphics, argv[2]);
            }
            winrt::uninit_apartment();
            std::cout << "Traffic previews exported using the product painter.\n";
            return 0;
        }
        {
            Cards::Runtime runtime;
            winrt::check_hresult(runtime.Initialize());
            Cards::Graphics graphics;
            Require(graphics.Initialize(), "graphics initialization");
            auto compositor = winrt::Windows::UI::Composition::Compositor();
            std::wstring error;
            auto device = graphics.CreateGraphicsDevice(compositor, error);
            Require(device != nullptr, "composition must be backed by the D2D device");
            Cards::Snapshot duplicate;
            Cards::Protocol::Changes initial, repeated;
            const std::wstring weather = LR"({"weather":{"revision":7,"temperature":"23"}})";
            Require(Cards::Protocol::ApplySnapshot(duplicate, weather, initial) && initial.weather, "first weather frame");
            Require(Cards::Protocol::ApplySnapshot(duplicate, weather, repeated) && !repeated.Any(),
                "unchanged weather must not invalidate the whole group");
            Cards::AudioState audio;
            Require(Cards::Protocol::ApplyAudioReply(audio, LR"({"revision":3,"outputs":[],"inputs":[]})"), "initial audio state");
            Require(Cards::Protocol::ApplyAudioReply(audio, LR"({"revision":3,"message":"selection failed"})") &&
                audio.message == L"selection failed", "same-revision reply preserves operation feedback");
            Require(!Cards::Protocol::ApplyAudioReply(audio, LR"({"revision":2,"message":"stale"})") &&
                audio.message == L"selection failed", "stale reply cannot overwrite operation feedback");
            Cards::DrawingSurface surface;
            surface.Ensure(device, 160, 120);
            {
                Cards::DrawScope scope(surface);
                Require(static_cast<bool>(scope), "BeginDraw must return an ID2D1DeviceContext");
                Microsoft::WRL::ComPtr<ID2D1Image> before, after;
                scope->GetTarget(before.GetAddressOf());
                const auto transform = D2D1::Matrix3x2F::Translation(3, 7);
                scope->SetTransform(transform);
                Require(graphics.BeginMask(scope.Get()), "mask creation");
                Cards::Draw::FillRound(scope.Get(), D2D1::RectF(10, 10, 30, 30), 4, Cards::Color{1, 0, 0, 1});
                graphics.EndMask(scope.Get(), 4);
                scope->GetTarget(after.GetAddressOf());
                Require(before.Get() == after.Get(), "mask must restore the composition target");
                D2D1_MATRIX_3X2_F restored{}; scope->GetTransform(&restored);
                Require(restored.dx == 3 && restored.dy == 7, "mask must restore the transform");
            }
            Require(surface.Ready(), "surface update must commit successfully");

            Services services;
            Cards::Snapshot snapshot;
            snapshot.media.title = L"Native card probe";
            snapshot.media.canPlayPause = true;
            snapshot.media.canSeek = true;
            snapshot.media.duration = 100;
            snapshot.lyrics.current = L"An offscreen lyric line";
            snapshot.lyrics.available = true;
            snapshot.lyrics.currentLineIndex = 0;
            snapshot.lyrics.revision = 1;
            snapshot.weather.hasData = true;
            snapshot.weather.temperature = L"23";
            snapshot.weather.city = L"Test city";
            snapshot.weather.conditionKind = L"clear";
            snapshot.update.phase = L"ready";
            for (int i = 0; i < 84; ++i)
            {
                Cards::TrafficDay day; day.week = i / 7; day.weekday = i % 7; day.level = 3;
                day.known = true; day.date = L"2026-10-01"; snapshot.traffic.days.push_back(day);
            }
            Cards::CardEnvironment environment;
            environment.graphics = &graphics; environment.compositor = compositor; environment.device = device;
            environment.reducedMotion = true;
            {
                Cards::CardPopup popup;
                Require(popup.Create(GetModuleHandleW(nullptr), environment), "popup creation");
                std::vector<Cards::Layout::Region> items(3);
                for (int i = 0; i < 3; ++i)
                {
                    items[i].name = L"Fixture device";
                    items[i].enabled = i != 1;
                    items[i].selected = i == 0;
                }
                int activated = 0;
                popup.SetActivation([&] { ++activated; });
                popup.Show(L"output", items, RECT{0, 0, 100, 30}, true);
                Require(popup.Bounds().right < GetSystemMetrics(SM_XVIRTUALSCREEN), "popup remains offscreen");
                HWND popupWindow{};
                EnumWindows([](HWND window, LPARAM data) -> BOOL
                {
                    DWORD process{}; GetWindowThreadProcessId(window, &process);
                    wchar_t name[80]{}; GetClassNameW(window, name, ARRAYSIZE(name));
                    if (process == GetCurrentProcessId() && wcscmp(name, L"TaskBarWidgetsCardPopup") == 0)
                        *reinterpret_cast<HWND*>(data) = window;
                    return TRUE;
                }, reinterpret_cast<LPARAM>(&popupWindow));
                Require(popupWindow != nullptr, "popup HWND");
                const float dpi = GetDpiForWindow(popupWindow) / 96.0f;
                const LPARAM first = MAKELPARAM(static_cast<int>(15 * dpi), static_cast<int>(21 * dpi));
                SendMessageW(popupWindow, WM_LBUTTONDOWN, MK_LBUTTON, first);
                Require(activated == 0, "menu must not commit on press");
                SendMessageW(popupWindow, WM_LBUTTONUP, 0, MAKELPARAM(-10, -10));
                Require(activated == 0, "releasing outside must cancel menu selection");
                SendMessageW(popupWindow, WM_LBUTTONDOWN, MK_LBUTTON, first);
                SendMessageW(popupWindow, WM_LBUTTONUP, 0, first);
                Require(activated == 1, "menu commits once on release");
                popup.MoveSelection(1);
                Require(popup.Selected() == 2, "menu keyboard navigation skips disabled rows");
                popup.Hide();
            }
            for (const float scale : {1.0f, 1.5f, 2.0f})
            for (const auto kind : {Cards::Kind::Weather, Cards::Kind::Traffic, Cards::Kind::Device, Cards::Kind::Media, Cards::Kind::Update})
            {
                Cards::CardWindow card(kind, services);
                Require(card.Create(environment, GetModuleHandleW(nullptr)), "native card window creation");
                if (winrt::Microsoft::UI::Composition::SystemBackdrops::MicaController::IsSupported())
                {
                    SendMessageW(card.Handle(), WM_ACTIVATE, WA_INACTIVE, 0);
                    SendMessageW(card.Handle(), WM_NCACTIVATE, FALSE, 0);
                    const auto deadline = GetTickCount64() + 500;
                    while (GetTickCount64() < deadline)
                    {
                        MSG message{};
                        while (GetTickCount64() < deadline && PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
                        { TranslateMessage(&message); DispatchMessageW(&message); }
                        Sleep(5);
                    }
                    Require(card.HasActiveBackdropConfiguration(), "inactive cards retain an Active Mica controller");
                }
                card.SetSnapshot(snapshot);
                const LONG width = static_cast<LONG>(Cards::Layout::WidthDip(kind) * scale);
                const LONG height = static_cast<LONG>(Cards::Layout::DefaultHeightDip(kind) * scale);
                card.Place(RECT{-30000, -30000, -30000 + width, -30000 + height}, scale);
                card.Paint();
                Require(card.HasRenderedContent(), "card must commit content while hidden");
                Require(!IsWindowVisible(card.Handle()), "probe must not show or activate a card");
                if (kind == Cards::Kind::Traffic)
                {
                    card.Show(false); // remains entirely outside the virtual desktop
                    const auto paints = card.ContentPaintCount();
                    for (int refresh = 0; refresh < 20; ++refresh)
                    {
                        card.Place(card.Bounds(), scale);
                        card.Show(false);
                    }
                    Require(card.ContentPaintCount() == paints, "unchanged layout must not repaint traffic");
                    card.Hide(false);
                }
                if (kind == Cards::Kind::Media)
                {
                    auto regions = card.Regions().All();
                    for (const auto& region : regions) if (region.action == L"playPause")
                    {
                        const POINT point{-30000 + static_cast<LONG>((region.bounds.left + region.bounds.right) * scale / 2),
                            -30000 + static_cast<LONG>((region.bounds.top + region.bounds.bottom) * scale / 2)};
                        card.OnPointer(WM_LBUTTONDOWN, point, true);
                        card.OnPointer(WM_LBUTTONUP, point, false);
                        Require(services.action == L"playPause", "screen coordinates must map to card-local hit regions");
                    }
                }
            }
        }
        winrt::uninit_apartment();
        {
            Cards::CardHost host;
            std::cout << "Checking the card host toggle path...\n";
            Require(host.Start(nullptr, GetModuleHandleW(nullptr), true), "card STA bootstrap");
            Cards::Config config;
            config.resourceMode = L"OnDemand";
            for (auto& [id, enabled] : config.enabled) enabled = true;
            host.ApplyConfig(config);
            const POINT anchor{ GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN) };
            host.Toggle(anchor);
            WaitForCards(host, 4);
            host.Toggle(anchor);
            WaitForCards(host, 0);
            host.Toggle(anchor);
            WaitForCards(host, 4);
            host.Stop();
            Require(VisibleProbeCards() == 0, "host shutdown must destroy the card windows");
        }
        std::cout << "Native card rendering, mask restore, scaled hit-testing, host toggle/reopen and STA shutdown passed.\n";
        return 0;
    }
    catch (const winrt::hresult_error& e) { std::cerr << "HRESULT " << std::hex << e.code().value << '\n'; return 1; }
    catch (const std::exception& e) { std::cerr << e.what() << '\n'; return 1; }
}
