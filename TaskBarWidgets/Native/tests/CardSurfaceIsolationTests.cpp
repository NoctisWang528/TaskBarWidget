#include "CardPch.h"
#include "CardWindow.h"
#include "CardDraw.h"
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <cstring>
#include <iostream>
#include <stdexcept>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "windowsapp.lib")

namespace
{
    using namespace winrt::Windows::Graphics;
    using Microsoft::WRL::ComPtr;
    void RequireIsolation(bool condition, const char* message)
    {
        if (!condition) throw std::runtime_error(message);
    }

    template<class Predicate> void WaitIsolation(Predicate ready, const char* failure)
    {
        const auto deadline = GetTickCount64() + 3000;
        while (!ready())
        {
            RequireIsolation(GetTickCount64() < deadline, failure);
            MSG message{};
            while (GetTickCount64() < deadline && PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
            { TranslateMessage(&message); DispatchMessageW(&message); }
            Sleep(5);
        }
    }

    struct CaptureIsolation
    {
        ComPtr<ID3D11Device> device;
        ComPtr<ID3D11DeviceContext> context;
        winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice captureDevice{ nullptr };

        CaptureIsolation() try
        {
            RequireIsolation(Capture::GraphicsCaptureSession::IsSupported(),
                "UNSUPPORTED surface isolation: Windows Graphics Capture unavailable");
            winrt::check_hresult(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION,
                device.GetAddressOf(), nullptr, context.GetAddressOf()));
            ComPtr<IDXGIDevice> dxgi;
            winrt::check_hresult(device.As(&dxgi));
            winrt::com_ptr<IInspectable> projected;
            winrt::check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgi.Get(), projected.put()));
            captureDevice = projected.as<winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice>();
        }
        catch (winrt::hresult_error const& error)
        {
            throw std::runtime_error("UNSUPPORTED surface isolation capture setup: " + winrt::to_string(error.message()));
        }

        std::vector<uint8_t> Pixels(Cards::CardWindow& card,
            winrt::Windows::UI::Composition::Compositor const& compositor) try
        {
            DWORD process{};
            GetWindowThreadProcessId(card.Handle(), &process);
            RECT bounds{};
            GetWindowRect(card.Handle(), &bounds);
            RequireIsolation(process == GetCurrentProcessId() && IsWindowVisible(card.Handle()) &&
                bounds.right < GetSystemMetrics(SM_XVIRTUALSCREEN) &&
                bounds.bottom < GetSystemMetrics(SM_YVIRTUALSCREEN),
                "surface isolation may capture only its own visible offscreen diagnostic HWND");
            auto commit = compositor.RequestCommitAsync();
            WaitIsolation([&] { return commit.Status() != winrt::Windows::Foundation::AsyncStatus::Started; },
                "UNSUPPORTED surface isolation: composition commit timed out");
            commit.GetResults();
            auto factory = winrt::get_activation_factory<Capture::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
            Capture::GraphicsCaptureItem item{ nullptr };
            winrt::check_hresult(factory->CreateForWindow(card.Handle(),
                winrt::guid_of<Capture::GraphicsCaptureItem>(), winrt::put_abi(item)));
            const auto size = item.Size();
            RequireIsolation(size.Width == bounds.right - bounds.left && size.Height == bounds.bottom - bounds.top,
                "UNSUPPORTED surface isolation: offscreen capture dimensions differ from diagnostic HWND");
            // A fresh session after each commit cannot reuse a frame queued before the mutation.
            struct Resources
            {
                Capture::Direct3D11CaptureFramePool pool{ nullptr };
                Capture::GraphicsCaptureSession session{ nullptr };
                ~Resources() { try { if (session) session.Close(); if (pool) pool.Close(); } catch (...) {} }
            } resources;
            resources.pool = Capture::Direct3D11CaptureFramePool::CreateFreeThreaded(captureDevice,
                winrt::Windows::Graphics::DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size);
            resources.session = resources.pool.CreateCaptureSession(item);
            resources.session.IsCursorCaptureEnabled(false);
            resources.session.StartCapture();
            Capture::Direct3D11CaptureFrame frame{ nullptr };
            WaitIsolation([&] { frame = resources.pool.TryGetNextFrame(); return bool(frame); },
                "UNSUPPORTED surface isolation: offscreen HWND produced no capture frame");
            RequireIsolation(frame.ContentSize().Width == size.Width && frame.ContentSize().Height == size.Height,
                "UNSUPPORTED surface isolation: captured content was resized or clipped");
            ComPtr<ID3D11Texture2D> texture;
            winrt::check_hresult(frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>()
                ->GetInterface(IID_PPV_ARGS(texture.GetAddressOf())));
            D3D11_TEXTURE2D_DESC description{};
            texture->GetDesc(&description);
            RequireIsolation(description.Width >= UINT(size.Width) && description.Height >= UINT(size.Height) &&
                description.Format == DXGI_FORMAT_B8G8R8A8_UNORM,
                "UNSUPPORTED surface isolation: unexpected capture texture");
            description.Usage = D3D11_USAGE_STAGING;
            description.BindFlags = description.MiscFlags = 0;
            description.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            ComPtr<ID3D11Texture2D> staging;
            winrt::check_hresult(device->CreateTexture2D(&description, nullptr, staging.GetAddressOf()));
            context->CopyResource(staging.Get(), texture.Get());
            // Exclude window edges/capture decorations; compare the complete content interior.
            constexpr int inset = 12;
            RequireIsolation(size.Width > 2 * inset && size.Height > 2 * inset, "capture interior is empty");
            std::vector<uint8_t> pixels(size_t(size.Width - 2 * inset) * (size.Height - 2 * inset) * 4);
            D3D11_MAPPED_SUBRESOURCE mapped{};
            winrt::check_hresult(context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped));
            const size_t row = size_t(size.Width - 2 * inset) * 4;
            for (int y = inset; y < size.Height - inset; ++y)
                std::memcpy(pixels.data() + (y - inset) * row,
                    static_cast<uint8_t*>(mapped.pData) + y * mapped.RowPitch + inset * 4, row);
            context->Unmap(staging.Get(), 0);
            frame.Close();
            size_t visible = 0, bright = 0;
            for (size_t i = 0; i < pixels.size(); i += 4)
            {
                if (pixels[i + 3] && pixels[i] + pixels[i + 1] + pixels[i + 2] > 30) ++visible;
                if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 420) ++bright;
            }
            RequireIsolation(visible > 1000 && bright > 100,
                "UNSUPPORTED surface isolation: blank/offscreen capture lacks meaningful painted content");
            return pixels;
        }
        catch (winrt::hresult_error const& error)
        {
            throw std::runtime_error("UNSUPPORTED surface isolation capture/commit/readback: " +
                winrt::to_string(error.message()));
        }
    };

    size_t ChangedPixels(const std::vector<uint8_t>& a, const std::vector<uint8_t>& b)
    {
        RequireIsolation(a.size() == b.size(), "surface isolation capture size changed");
        size_t changed = 0;
        for (size_t i = 0; i < a.size(); i += 4)
            if (std::memcmp(a.data() + i, b.data() + i, 4)) ++changed;
        return changed;
    }

    struct IsolationServices : Cards::CardHostServices
    {
        HRESULT failure{ S_OK };
        void OnRequest(const std::wstring&, const std::wstring&) override {}
        void OnHideCards(const std::wstring&) override {}
        void OnOpenSettings() override {}
        void OnLayoutInvalidated() override {}
        void OnGraphicsFailure(HRESULT result) override { if (FAILED(result)) failure = result; }
    };
}

// Call on the initialized card STA, before other diagnostic cards are created.
// Requires Windows 10 1903+ WGC HWND interop, a hardware D3D11 device and working
// offscreen HWND capture. Unsupported/blank capture throws; it never counts as a pass.
void TestCardSurfaceIsolation(Cards::Graphics& graphics,
    winrt::Windows::UI::Composition::Compositor const& compositor,
    winrt::Windows::UI::Composition::CompositionGraphicsDevice const& device)
{
    CaptureIsolation capture;
    IsolationServices services;
    Cards::CardEnvironment environment;
    environment.graphics = &graphics; environment.compositor = compositor; environment.device = device;
    environment.reducedMotion = true; // Immediate hover, while keeping the normal-color glow path.
    environment.mica.tint = environment.mica.darkTint = 1;
    environment.mica.luminosity = environment.mica.darkLuminosity = 0;
    Cards::CardWindow traffic(Cards::Kind::Traffic, services), media(Cards::Kind::Media, services),
        peripheral(Cards::Kind::PeripheralBattery, services);
    RequireIsolation(traffic.Create(environment, GetModuleHandleW(nullptr)) &&
        media.Create(environment, GetModuleHandleW(nullptr)) && peripheral.Create(environment, GetModuleHandleW(nullptr)), "create isolation diagnostic cards");
    const LONG x = GetSystemMetrics(SM_XVIRTUALSCREEN) - 1000;
    const LONG y = GetSystemMetrics(SM_YVIRTUALSCREEN) - 1000;
    traffic.Place({x, y, x + 384, y + 284}, 1);
    media.Place({x + 400, y, x + 784, y + 248}, 1);
    peripheral.Place({x, y - 320, x + 384, y - 136}, 1);
    traffic.Show(false); media.Show(false); peripheral.Show(false); // SW_SHOWNOACTIVATE; no host, hooks, picker or user HWND.
    const auto emptyTraffic = capture.Pixels(traffic, compositor);
    const auto emptyMedia = capture.Pixels(media, compositor);
    const auto emptyPeripheral = capture.Pixels(peripheral, compositor);
    Cards::Snapshot snapshot;
    snapshot.media.title = L"SURFACE ISOLATION MEDIA";
    snapshot.media.artist = L"Synthetic artist";
    snapshot.media.source = L"Diagnostic player";
    snapshot.media.canPlayPause = true;
    // Static playback/lyrics: only explicit revisions below repaint media. A timer
    // could repair corruption before capture, hiding traffic-to-media contamination.
    snapshot.traffic.available = true;
    for (int i = 0; i < 84; ++i)
    {
        Cards::TrafficDay day;
        const std::chrono::year_month_day date{std::chrono::sys_days{std::chrono::year{2026}/8/3} + std::chrono::days{i}};
        wchar_t text[16]{};
        swprintf_s(text, L"%04d-%02u-%02u", int(date.year()), unsigned(date.month()), unsigned(date.day()));
        day.date = text; day.week = i / 7; day.weekday = i % 7;
        day.known = true; day.level = i % 5; day.totalKiB = 1024 * (i + 1);
        snapshot.traffic.days.push_back(day);
    }
    snapshot.traffic.today = snapshot.traffic.selectedDate = snapshot.traffic.days.back().date;
    snapshot.peripheralBattery.revision = 1;
    for (int i = 0; i < 5; ++i)
    {
        Cards::PeripheralDevice device;
        device.id = L"isolated-" + std::to_wstring(i); device.name = L"Synthetic peripheral " + std::to_wstring(i);
        device.kind = L"mouse"; device.state = L"online"; device.canRead = device.showInCard = true;
        device.batteries.push_back({L"main", 70, L"", false, false});
        snapshot.peripheralBattery.devices.push_back(std::move(device));
    }
    traffic.SetSnapshot(snapshot); media.SetSnapshot(snapshot); peripheral.SetSnapshot(snapshot);
    auto trafficPixels = capture.Pixels(traffic, compositor);
    auto mediaPixels = capture.Pixels(media, compositor);
    auto peripheralPixels = capture.Pixels(peripheral, compositor);
    RequireIsolation(ChangedPixels(emptyTraffic, trafficPixels) > 128 && ChangedPixels(emptyMedia, mediaPixels) > 128 &&
        ChangedPixels(emptyPeripheral, peripheralPixels) > 128,
        "UNSUPPORTED surface isolation: capture did not observe synthetic card content");
    RequireIsolation(ChangedPixels(trafficPixels, capture.Pixels(traffic, compositor)) == 0 &&
        ChangedPixels(mediaPixels, capture.Pixels(media, compositor)) == 0, "isolation baseline is not stable");
    int nonzeroOffsets = 0;
    for (int round = 0; round < 6; ++round)
    {
        // Real shared-device surface allocations/commits, never standalone D2D targets.
        std::vector<std::unique_ptr<Cards::DrawingSurface>> churn;
        for (int i = 0; i < 8; ++i)
        {
            auto surface = std::make_unique<Cards::DrawingSurface>();
            surface->Ensure(device, 320 + i * 13, 190 + round * 7);
            { Cards::DrawScope draw(*surface);
              RequireIsolation(bool(draw), "BeginDraw isolation allocation churn");
              nonzeroOffsets += draw.offset.x != 0 || draw.offset.y != 0;
              draw->Clear(D2D1::ColorF(0.1f, 0.2f, 0.3f, 1)); }
            RequireIsolation(SUCCEEDED(surface->LastResult()), "EndDraw isolation allocation churn");
            churn.push_back(std::move(surface));
        }
        snapshot.media.revision = round;
        snapshot.media.title = round % 2 ? L"SURFACE ISOLATION MEDIA A" : L"SURFACE ISOLATION MEDIA B";
        media.SetSnapshot(snapshot);
        RequireIsolation(ChangedPixels(trafficPixels, capture.Pixels(traffic, compositor)) == 0,
            "ISOLATION FAILURE: media update changed committed traffic pixels");
        const auto updatedMedia = capture.Pixels(media, compositor);
        RequireIsolation(ChangedPixels(mediaPixels, updatedMedia) > 4, "capture missed explicit media mutation");
        mediaPixels = updatedMedia;
        RequireIsolation(ChangedPixels(peripheralPixels, capture.Pixels(peripheral, compositor)) == 0,
            "ISOLATION FAILURE: media mutation changed committed peripheral pixels");
        const auto* cell = traffic.Regions().Find(300 + round);
        RequireIsolation(cell && cell->enabled, "synthetic heatmap cell missing");
        const auto bounds = cell->bounds;
        traffic.OnPointer(WM_MOUSEMOVE, {x + (bounds.left + bounds.right) / 2,
            y + (bounds.top + bounds.bottom) / 2}, false);
        TRACKMOUSEEVENT cancel{sizeof(cancel), TME_CANCEL | TME_LEAVE, traffic.Handle(), 0};
        TrackMouseEvent(&cancel);
        snapshot.traffic.Select(snapshot.traffic.days[round].date);
        ++snapshot.traffic.revision;
        traffic.SetSnapshot(snapshot); // Select without WM_LBUTTONDOWN/SetFocus/SetCapture.
        RequireIsolation(ChangedPixels(mediaPixels, capture.Pixels(media, compositor)) == 0,
            "ISOLATION FAILURE: traffic hover/selection changed committed media pixels");
        const auto updatedTraffic = capture.Pixels(traffic, compositor);
        RequireIsolation(ChangedPixels(trafficPixels, updatedTraffic) > 4, "capture missed traffic interaction");
        trafficPixels = updatedTraffic;
        RequireIsolation(ChangedPixels(peripheralPixels, capture.Pixels(peripheral, compositor)) == 0,
            "ISOLATION FAILURE: traffic interaction changed committed peripheral pixels");
        ++snapshot.peripheralBattery.revision;
        for (auto& peripheralDevice : snapshot.peripheralBattery.devices)
            peripheralDevice.batteries.front().percentage = round % 2 ? 10 : 80;
        peripheral.SetSnapshot(snapshot);
        peripheral.SetPeripheralPage(round % 2);
        RequireIsolation(ChangedPixels(trafficPixels, capture.Pixels(traffic, compositor)) == 0 &&
            ChangedPixels(mediaPixels, capture.Pixels(media, compositor)) == 0,
            "ISOLATION FAILURE: peripheral reading/page changed another committed card");
        const auto updatedPeripheral = capture.Pixels(peripheral, compositor);
        RequireIsolation(ChangedPixels(peripheralPixels, updatedPeripheral) > 4, "capture missed peripheral reading/page mutation");
        peripheralPixels = updatedPeripheral;
        RequireIsolation(SUCCEEDED(services.failure), "diagnostic card reported a graphics failure");
    }
    std::cout << "Committed card pixel isolation passed; allocation-churn nonzero offsets=" << nonzeroOffsets
        << " (does not prove the two card surfaces shared an atlas texture).\n";
}
