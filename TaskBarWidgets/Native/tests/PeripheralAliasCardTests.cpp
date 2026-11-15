// Offscreen alias/layout fixture. Define PERIPHERAL_ALIAS_CARD_TEST_MAIN
// for a standalone runner linked with the native card objects/libraries.
#include "CardPch.h"
#include "CardDraw.h"
#include "CardProtocol.h"
#include <iostream>
#include <stdexcept>

namespace
{
    void RequireAliasCard(bool value, const char* message)
    { if (!value) throw std::runtime_error(message); }
}

void TestPeripheralAliasesAndLayout(Cards::Graphics& graphics)
{
    using namespace Cards;
    PeripheralBatteryState state;
    RequireAliasCard(Protocol::ApplyPeripheralBatteryReply(state, LR"({"version":1,"revision":1,
        "devices":[{"id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","name":"Old mouse","canRead":true,"showInCard":true}]})"), "v1 compatibility");
    RequireAliasCard(Protocol::ApplyPeripheralBatteryReply(state, LR"({"version":2,"revision":2,
        "aliases":{"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"},
        "devices":[{"id":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","name":"Mouse","canRead":true,"showInCard":true}]})"), "v2 aliases");
    RequireAliasCard(state.devices.size() == 1 && state.aliases.size() == 1, "one canonical card device");
    RequireAliasCard(!Protocol::ApplyPeripheralBatteryReply(state, LR"({"version":2,"revision":3,
        "aliases":{"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"devices":[]})") &&
        state.revision == 2 && state.devices.size() == 1, "card rejects historical alias cycles atomically");
    RequireAliasCard(!Protocol::ApplyPeripheralBatteryReply(state, LR"({"version":2,"revision":3,
        "aliases":{"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa":"cccccccccccccccccccccccccccccccc"},"devices":[]})"), "card rejects alias conflicts");
    RequireAliasCard(!Protocol::ApplyPeripheralBatteryReply(state, LR"({"version":2,"revision":3,
        "aliases":{},"devices":[{"id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},{"id":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}]})"), "canonical device conflicts");
    RequireAliasCard(!Protocol::ApplyPeripheralBatteryReply(state, LR"({"version":3,"revision":3,"devices":[]})"), "unknown version");

    Snapshot snapshot;
    for (int i = 0; i < 5; ++i)
    {
        PeripheralDevice device;
        device.id = std::wstring(32, wchar_t(L'a' + i)); device.name = L"Fixture mouse";
        device.canRead = device.showInCard = true; device.kind = L"mouse"; device.state = L"online";
        device.batteries.push_back({L"main", 75, L"", false, false});
        snapshot.peripheralBattery.devices.push_back(device);
    }
    for (const auto* mode : {L"compact", L"detailed"})
    for (bool highContrast : {false, true})
    for (bool dark : {false, true})
    for (float scale : {1.f, 1.5f, 2.f})
    {
        snapshot.peripheralBattery.mode = mode;
        for (int page = 0; page < snapshot.peripheralBattery.PageCount(); ++page)
        {
            CardEnvironment environment;
            environment.graphics = &graphics; environment.highContrast = highContrast; environment.dark = dark;
            environment.reducedMotion = true;
            const float height = Paint::MeasureHeight(Kind::PeripheralBattery, snapshot, environment, 384, page);
            const auto size = D2D1::SizeU(UINT(std::ceil(384 * scale)), UINT(std::ceil(height * scale)));
            Microsoft::WRL::ComPtr<ID2D1Device> device;
            graphics.ResourceContext()->GetDevice(device.GetAddressOf());
            Microsoft::WRL::ComPtr<ID2D1DeviceContext> context;
            winrt::check_hresult(device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, context.GetAddressOf()));
            Microsoft::WRL::ComPtr<ID2D1Bitmap1> target, readback;
            const auto format = D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED);
            winrt::check_hresult(context->CreateBitmap(size, nullptr, 0,
                D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET, format, 96, 96), target.GetAddressOf()));
            winrt::check_hresult(context->CreateBitmap(size, nullptr, 0, D2D1::BitmapProperties1(
                D2D1_BITMAP_OPTIONS_CPU_READ | D2D1_BITMAP_OPTIONS_CANNOT_DRAW, format, 96, 96), readback.GetAddressOf()));
            context->SetTarget(target.Get()); context->BeginDraw();
            DrawScope::Configure(context.Get(), scale, {});
            context->Clear(D2D1::ColorF(0, 0.f));
            Layout::Table regions;
            Paint::Interaction interaction; interaction.focused = 601;
            Paint::PeripheralBattery(context.Get(), snapshot, environment, regions, 384, height, interaction, page);
            winrt::check_hresult(context->EndDraw()); context->SetTarget(nullptr);
            RequireAliasCard(!regions.Find(604), "removed attribution has no input or accessibility region");
            const int visible = std::min(snapshot.peripheralBattery.PageSize(), 5 - page * snapshot.peripheralBattery.PageSize());
            RequireAliasCard(height == (snapshot.peripheralBattery.mode == L"compact" ? 184.f : 88.f + visible * 50) &&
                regions.All().size() == size_t(4 + visible), "natural card height and active region count");
            RequireAliasCard(regions.Find(602)->bounds.bottom == height - 8,
                "paging ends at the natural bottom inset");
            const auto* focus = regions.NextFocus(nullptr, 1);
            bool reachable = false;
            for (int i = 0; focus && i < 10; ++i)
            { reachable |= focus->id == 601; focus = regions.NextFocus(focus, 1); }
            RequireAliasCard(reachable && regions.Hit({354, 26})->id == 601,
                "refresh remains reachable by keyboard and hit testing");
            for (const auto& region : regions.All())
                if (region.action == L"peripheralReading") RequireAliasCard(
                    region.bounds.bottom <= Layout::PeripheralPagingTop(height), "readings cannot overlap paging");
            winrt::check_hresult(readback->CopyFromBitmap(nullptr, target.Get(), nullptr));
            D2D1_MAPPED_RECT pixels{};
            winrt::check_hresult(readback->Map(D2D1_MAP_OPTIONS_READ, &pixels));
            size_t painted = 0;
            for (UINT y = UINT(Layout::PeripheralPagingTop(height) * scale); y < UINT((height - 8) * scale); ++y)
                for (UINT x = UINT(16 * scale); x < UINT(368 * scale); ++x)
                    painted += pixels.bits[y * pixels.pitch + x * 4 + 3] != 0;
            readback->Unmap();
            RequireAliasCard(painted > 10, "paging paints at the natural height in light/dark/high contrast at every DPI");
        }
    }
    std::cout << "Peripheral card v1/v2 alias validation and natural layout/paging/keyboard regions passed.\n";
}

#ifdef PERIPHERAL_ALIAS_CARD_TEST_MAIN
int main()
{
    try
    {
        winrt::init_apartment(winrt::apartment_type::single_threaded);
        {
            Cards::Graphics graphics;
            RequireAliasCard(graphics.Initialize(), "graphics initialization");
            TestPeripheralAliasesAndLayout(graphics);
        }
        winrt::uninit_apartment();
        return 0;
    }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
    catch (const winrt::hresult_error& error) { std::wcerr << error.message().c_str() << '\n'; return 1; }
}
#endif
