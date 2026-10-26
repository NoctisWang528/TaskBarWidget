#include "CardPch.h"
#include "CardDraw.h"
#include <filesystem>

// Calls the product painter with synthetic data; the solid backing is not Mica.
void WriteTrafficPreviews(Cards::Graphics& graphics, const wchar_t* directory)
{
    using Microsoft::WRL::ComPtr;
    Cards::Snapshot snapshot;
    auto& traffic = snapshot.traffic;
    traffic.available = true;
    traffic.today = traffic.selectedDate = L"2026-10-24";
    traffic.scopeLabel = L"已记录的监测网卡（上传 + 下载）";
    traffic.detail = L"2026-10-24 · 30.56 GB";
    traffic.totalKiB = 1528.88 * 1024 * 1024;
    traffic.peakKiB = 202.1 * 1024 * 1024;
    for (int i = 0; i < 84; ++i)
    {
        const std::chrono::year_month_day date{std::chrono::sys_days{
            std::chrono::year{2026}/8/3} + std::chrono::days{i}};
        wchar_t text[16]{};
        swprintf_s(text, L"%04d-%02u-%02u", int(date.year()), unsigned(date.month()), unsigned(date.day()));
        Cards::TrafficDay day;
        day.date = text; day.week = i / 7; day.weekday = i % 7;
        day.known = true; day.level = (i % 13 == 7) ? 4 : 3;
        if (i < 6 || i == 32 || i == 48 || i == 54 || i == 55 || i == 56 || i == 57 || i == 73)
            day.level = 0;
        if (i == 40) day.level = 5;
        day.isToday = i == 82; day.isFuture = i == 83;
        traffic.days.push_back(day);
    }
    const auto folder = std::filesystem::path(directory);
    std::filesystem::create_directories(folder);
    auto* context = graphics.ResourceContext();
    ComPtr<ID2D1Device> device;
    context->GetDevice(device.GetAddressOf());
    ComPtr<IWICImageEncoder> imageEncoder;
    winrt::check_hresult(graphics.Wic()->CreateImageEncoder(device.Get(), imageEncoder.GetAddressOf()));
    for (bool dark : {true, false})
    for (int visual = 0; visual < 3; ++visual)
    for (int variant = 0; variant < (visual == 0 ? 3 : 1); ++variant)
    {
        const bool media = visual == 1;
        const bool weather = visual == 2;
        auto painted = snapshot;
        if (variant == 2) painted.traffic = {};
        constexpr UINT width = 480;
        const float heightDip = media ? Cards::Layout::HeightMedia : weather ? Cards::Layout::HeightWeather : Cards::Layout::HeightTraffic;
        const UINT height = static_cast<UINT>(heightDip * 1.25f);
        constexpr float dpi = 120.0f;
        const auto format = D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED);
        ComPtr<ID2D1Bitmap1> bitmap;
        winrt::check_hresult(context->CreateBitmap(D2D1::SizeU(width, height), nullptr, 0,
            D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET, format, dpi, dpi), bitmap.GetAddressOf()));
        context->SetTarget(bitmap.Get());
        context->SetDpi(dpi, dpi);
        context->BeginDraw();
        context->Clear(D2D1::ColorF(0, 0.0f));
        const auto rect = D2D1::RectF(0.5f, 0.5f, 383.5f, heightDip - 0.5f);
        Cards::Draw::FillRound(context, rect, Cards::Layout::CornerRadius,
            Cards::ParseColor(dark ? L"#30262B" : L"#F3EFF1", {}));
        Cards::CardEnvironment environment;
        environment.graphics = &graphics; environment.dark = dark;
        Cards::Layout::Table regions;
        if (media)
        {
            painted.media.title = L"没有媒体";
            Cards::Paint::Media(context, painted, {}, {}, environment, regions, 384, heightDip);
        }
        else if (weather)
        {
            painted.weather.city = L"示例城市";
            painted.weather.hasData = true;
            painted.weather.temperature = L"23";
            painted.weather.weather = L"多云";
            painted.weather.conditionKind = L"cloudy";
            Cards::WeatherAlert alert;
            alert.typeName = L"大风"; alert.level = L"黄色";
            alert.title = L"大风黄色预警：请注意防范。";
            painted.weather.alerts.push_back(alert);
            Cards::Paint::Weather(context, painted, environment, regions, 384, heightDip);
        }
        else
        {
            Cards::Paint::Interaction interaction;
            if (variant == 1) interaction.hot = 340;
            Cards::Paint::Traffic(context, painted, environment, regions, 384, heightDip, interaction);
        }
        Cards::Draw::StrokeRound(context, rect, Cards::Layout::CornerRadius,
            Cards::Draw::Colors(environment).border, 0.5f);
        winrt::check_hresult(context->EndDraw());
        context->SetTarget(nullptr);

        const auto path = folder / (std::wstring(media ? L"media-empty" : weather ? L"weather-title-alert" :
            variant == 1 ? L"traffic-hover" : variant == 2 ? L"traffic-loading" : L"traffic") +
            (dark ? L"-dark.png" : L"-light.png"));
        ComPtr<IWICStream> stream;
        winrt::check_hresult(graphics.Wic()->CreateStream(stream.GetAddressOf()));
        winrt::check_hresult(stream->InitializeFromFilename(path.c_str(), GENERIC_WRITE));
        ComPtr<IWICBitmapEncoder> encoder;
        winrt::check_hresult(graphics.Wic()->CreateEncoder(GUID_ContainerFormatPng, nullptr, encoder.GetAddressOf()));
        winrt::check_hresult(encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache));
        ComPtr<IWICBitmapFrameEncode> frame;
        winrt::check_hresult(encoder->CreateNewFrame(frame.GetAddressOf(), nullptr));
        winrt::check_hresult(frame->Initialize(nullptr));
        WICImageParameters parameters{format, dpi, dpi, 0, 0, width, height};
        winrt::check_hresult(imageEncoder->WriteFrame(bitmap.Get(), frame.Get(), &parameters));
        winrt::check_hresult(frame->Commit());
        winrt::check_hresult(encoder->Commit());
    }
}
