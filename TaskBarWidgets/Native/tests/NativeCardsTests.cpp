#include "CardPch.h"
#include "CardRuntime.h"
#include "CardHost.h"
#include "CardWindow.h"
#include "CardDraw.h"
#include <iostream>
#include <stdexcept>

static void Require(bool ok, const char* message) { if (!ok) throw std::runtime_error(message); }
struct Services : Cards::CardHostServices
{
    std::wstring action;
    void OnRequest(const std::wstring& value, const std::wstring&) override { action = value; }
    void OnHideCards(const std::wstring&) override {}
    void OnOpenSettings() override {}
    void OnMenuOpened(bool) override {}
    void OnLayoutInvalidated() override {}
};

int main()
{
    try
    {
        winrt::init_apartment(winrt::apartment_type::single_threaded);
        {
            Cards::Runtime runtime;
            winrt::check_hresult(runtime.Initialize());
            Cards::Graphics graphics;
            Require(graphics.Initialize(), "graphics initialization");
            auto compositor = winrt::Windows::UI::Composition::Compositor();
            std::wstring error;
            auto device = graphics.CreateGraphicsDevice(compositor, error);
            Require(device != nullptr, "composition must be backed by the D2D device");
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
            for (const float scale : {1.0f, 1.5f, 2.0f})
            for (const auto kind : {Cards::Kind::Weather, Cards::Kind::Traffic, Cards::Kind::Device, Cards::Kind::Media, Cards::Kind::Update})
            {
                Cards::CardWindow card(kind, services);
                Require(card.Create(environment, GetModuleHandleW(nullptr)), "native card window creation");
                card.SetSnapshot(snapshot);
                const LONG width = static_cast<LONG>(Cards::Layout::WidthDip(kind) * scale);
                const LONG height = static_cast<LONG>(Cards::Layout::DefaultHeightDip(kind) * scale);
                card.Place(RECT{-30000, -30000, -30000 + width, -30000 + height}, scale);
                card.Paint();
                Require(card.HasRenderedContent(), "card must commit content while hidden");
                Require(!IsWindowVisible(card.Handle()), "probe must not show or activate a card");
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
            Require(host.Start(nullptr, GetModuleHandleW(nullptr)), "card STA bootstrap");
            host.Stop();
        }
        std::cout << "Native card rendering, mask restore, scaled hit-testing and STA shutdown passed.\n";
        return 0;
    }
    catch (const winrt::hresult_error& e) { std::cerr << "HRESULT " << std::hex << e.code().value << '\n'; return 1; }
    catch (const std::exception& e) { std::cerr << e.what() << '\n'; return 1; }
}
