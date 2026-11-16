#include "CardPch.h"
#include "CardDraw.h"
#include <filesystem>
#include <stdexcept>

namespace
{
    Cards::WeatherState WeatherFixture(int variant)
    {
        Cards::WeatherState weather;
        weather.city = L"示例城市";
        weather.provider = L"示例天气来源";
        weather.hasData = true;
        weather.temperature = L"23";
        weather.feelsLike = L"25";
        weather.weather = L"多云";
        weather.conditionKind = L"cloudy";
        weather.observedAt = L"2026-11-03T14:00:00+08:00";
        weather.humidity = L"68"; weather.wind = L"东北风"; weather.windSpeed = L"7";
        weather.airQuality = L"42"; weather.airCategory = L"优"; weather.airIndexName = L"中国 AQI";
        weather.uvIndex = L"4"; weather.precipitation = L"0.2"; weather.precipProbability = L"30";
        weather.sunrise = L"06:23"; weather.sunset = L"17:16";
        weather.airAttribution = L"示例空气质量数据 · 保留来源署名\nhttps://developer.qweather.com/attribution.html";
        weather.alertsAttribution = L"https://developer.qweather.com/attribution.html";
        for (int i = 0; i < 24; ++i)
        {
            Cards::WeatherHour hour;
            const int h = (14 + i) % 24;
            wchar_t time[40]{};
            swprintf_s(time, L"2026-11-%02dT%02d:00:00+08:00", 3 + (14 + i) / 24, h);
            hour.time = time; hour.temperature = std::to_wstring(23 - (i % 7));
            hour.conditionKind = i % 4 == 0 ? L"rain" : i % 3 == 0 ? L"clear" : L"cloudy";
            hour.precipProbability = std::to_wstring((i * 7) % 80);
            // Include amount-only hours so mm cannot be mistaken for percent.
            if (i == 2) { hour.precipProbability.clear(); hour.precipitation = L"1.2"; }
            weather.hourly.push_back(hour);
        }
        for (int i = 0; i < 7; ++i)
        {
            Cards::WeatherForecastDay day;
            day.date = L"2026-11-" + std::wstring(i + 3 < 10 ? L"0" : L"") + std::to_wstring(i + 3);
            day.min = std::to_wstring(10 + i); day.max = std::to_wstring(20 + i);
            day.dayKind = i % 2 ? L"clear" : L"rain";
            day.weatherDay = i % 2 ? L"晴" : L"小雨";
            day.precipProbability = std::to_wstring(i * 10);
            day.sunrise = weather.sunrise; day.sunset = weather.sunset; day.uvIndex = weather.uvIndex;
            weather.forecast.push_back(day);
        }
        if (variant == 0 || variant == 1 || variant == 6)
        {
            Cards::WeatherAlert alert;
            alert.typeName = L"大风"; alert.level = L"黄色";
            alert.title = L"大风黄色预警：请注意防范。";
            if (variant == 1)
                alert.text = L"气象台发布大风黄色预警：预计今天下午至夜间，本市部分地区将出现阵风七至八级，"
                    L"请注意防范高空坠物，妥善安置室外物品，避免在广告牌、临时搭建物及大树附近停留。";
            weather.alerts.push_back(alert);
        }
        weather.forecastView = variant == 2 || variant == 3 || variant == 8 || variant == 9 || variant == 12;
        if (variant == 3) weather.forecastOffset = 5;
        if (variant == 4) weather.forecastOffset = 18;
        if (variant == 5)
        {
            weather.hourly.clear();
            weather.hourlyError = L"当前来源暂未提供逐小时预报";
        }
        if (variant == 6)
        {
            weather.stale = true; weather.state = L"error";
            weather.city = L"新设置的城市"; weather.lastSuccessCity = L"上次成功的城市";
            weather.message = L"刷新失败，保留上次成功数据";
            weather.airError = L"空气质量更新失败";
        }
        if (variant == 7)
        {
            weather = {};
            weather.state = L"error"; weather.message = L"还没有配置天气来源";
        }
        if (variant == 8)
        {
            weather.forecast[0].date = L"2026-02-30";
            weather.forecast[1].date.clear();
            weather.humidity = L"105"; weather.uvIndex = L"8%";
            weather.precipitation = L"40%"; weather.precipProbability = L"2 mm";
            weather.sunrise = L"-1:00"; weather.sunset = L"25:00";
        }
        if (variant == 9)
        {
            weather.temperature = L"-100.5";
            weather.city = L"很长的城市名称 · 区域和行政区示例";
            weather.forecast[0].min = weather.forecast[0].max = L"40";
            weather.forecast[1].min = L"25"; weather.forecast[1].max = L"-20";
            weather.forecast[2].max.clear();
            weather.forecast[3].min = L"-45"; weather.forecast[3].max = L"51";
            weather.forecast[4].min = L"NaN";
            weather.forecast[4].date = L"7日（周六）"; // China Weather supplies a source label.
        }
        if (variant == 10)
        {
            weather.updating = true;
            weather.hourly.clear(); weather.forecast.clear();
            weather.uvIndex.clear(); weather.precipitation.clear(); weather.precipProbability.clear();
            weather.sunrise.clear(); weather.sunset.clear();
        }
        if (variant == 11)
        {
            weather.hourly.resize(20); weather.forecastOffset = 999;
        }
        if (variant == 13)
        {
            weather.hourly[0].time = L"2026-02-30T07:00:00+08:00";
            weather.hourly[1].time = L"24:01";
            weather.hourly[2].time = L"08:30:99";
            weather.hourly[3].time = L"05:30";
            weather.hourly[4].time = L"2026-11-04T00:30:00Z";
            weather.hourly[5].time = L"08:30garbage";
            weather.forecastOffset = -4;
        }
        return weather;
    }

    // Exercise the real painter's interaction/accessibility table. These checks
    // run as part of --traffic-preview; they require no product HWND or HTTP.
    void CheckWeatherLayout(const Cards::WeatherState& weather, const Cards::CardEnvironment& environment,
        const Cards::Layout::Table& regions, float height, int variant, std::map<int, RECT>& controls)
    {
        const auto require = [](bool ok, const char* message)
        {
            if (!ok) throw std::runtime_error(std::string("Weather preview layout: ") + message);
        };
        // Fixed fixtures: empty, alert+error, one optional footer row, or no footer.
        const float expectedHeight = variant == 7 ? 152.f : variant == 6 ? 370.f :
            variant == 0 || variant == 1 || variant == 5 || variant == 10 ? 356.f : 340.f;
        require(height == expectedHeight, "weather natural height differs from its fixture's content rows");
        Cards::Snapshot snapshot; snapshot.weather = weather;
        snapshot.weather.forecastView = !weather.forecastView;
        snapshot.weather.forecastOffset = 999;
        require(Cards::Paint::WeatherHeight(snapshot, environment, 384) == height, "view/page changed geometry");
        std::map<int, bool> ids;
        for (const auto& region : regions.All())
        {
            require(ids.emplace(region.id, true).second, "duplicate region id");
            require(region.bounds.left >= 0 && region.bounds.top >= 0 && region.bounds.right <= 384 &&
                region.bounds.bottom <= height && region.bounds.right > region.bounds.left &&
                region.bounds.bottom > region.bounds.top, "region lies outside the card");
            if (region.type != Cards::Layout::RegionType::Button) continue;
            const auto [it, inserted] = controls.emplace(region.id, region.bounds);
            require(inserted || EqualRect(&it->second, &region.bounds), "control moved between views/pages");
        }
        for (const auto& [id, action] : std::map<int, std::wstring>{ {400, L"refreshWeather"},
            {401, L"openWeatherSettings"}, {404, L"weatherView:today"}, {405, L"weatherView:forecast"} })
            require(regions.Find(id) && regions.Find(id)->action == action, "missing header action");
        require(regions.Find(404)->selected != weather.forecastView &&
            regions.Find(405)->selected == weather.forecastView, "wrong segmented selection");
        require(regions.Find(400)->enabled != weather.updating, "refresh must lock while updating");
        if (variant == 7) { require(regions.Find(452) != nullptr, "empty state lost its explanation"); return; }
        const auto* previous = regions.Find(406); const auto* next = regions.Find(407);
        require(previous && next && previous->action == L"weatherPage:previous" &&
            next->action == L"weatherPage:next", "missing page actions");
        const size_t pageSize = weather.forecastView ? 5 : 6;
        const size_t count = weather.forecastView ? weather.forecast.size() : std::min<size_t>(24, weather.hourly.size());
        const size_t start = count ? std::min(static_cast<size_t>(std::max(0, weather.forecastOffset)) / pageSize,
            (count - 1) / pageSize) * pageSize : 0;
        require(previous->enabled == (start > 0) && next->enabled == (start + pageSize < count), "wrong page boundary");
        require(!regions.Find(402) && !regions.Find(403) && !regions.Find(451), "removed weather branding and attribution regions returned");
        if (variant == 2)
        {
            require(regions.Find(440)->name.find(L"今天") == 0, "observation date not labeled today");
            require(regions.Find(441)->name.find(L"11/4 周三") == 0, "weekday must derive from actual date");
            require(!regions.Find(445), "forecast page exceeds five rows");
        }
        if (variant == 3) require(regions.Find(440)->name.find(L"11/8 周日") == 0, "second day page is wrong");
        if (variant == 4) require(regions.Find(430)->name.find(L"08:00") == 0, "hour page lost the midnight crossing");
        if (variant == 5) require(regions.Find(430)->name == weather.hourlyError, "hourly unavailable state lost");
        if (variant == 6) require(regions.Find(450)->name.find(weather.lastSuccessCity) != std::wstring::npos &&
            regions.Find(450)->name.find(weather.message) != std::wstring::npos, "stale/error context lost");
        if (variant == 8)
        {
            require(regions.Find(440)->name.find(L"日期未知") == 0 &&
                regions.Find(441)->name.find(L"日期未知") == 0, "invalid dates produced weekday labels");
            require(!regions.Find(414) && !regions.Find(415) && !regions.Find(420), "invalid units/time produced fake data");
            require(regions.Find(411)->name == L"湿度 --", "invalid humidity became a real percentage");
        }
        if (variant == 11) require(regions.Find(431) && !regions.Find(432), "partial final hour page lost slots");
        if (variant == 9) require(regions.Find(444)->name.find(L"7日（周六）") == 0 &&
            regions.Find(444)->name.find(L"NaN") == std::wstring::npos, "source date label or invalid temperature handling lost");
        if (variant == 13)
        {
            for (int id : {430, 431, 432, 435})
                require(regions.Find(id)->name.find(L"--:--") == 0, "unsafe hourly time label");
            require(regions.Find(433)->name.find(L"05:30") == 0 &&
                regions.Find(434)->name.find(L"00:30") == 0, "valid clock formats rejected");
        }
        if (variant == 0)
        {
            require(regions.Find(432)->name.find(L"1.2 mm") != std::wstring::npos, "rainfall mm lost its unit");
            require(regions.Find(431)->name.find(L"7%") != std::wstring::npos, "rain probability lost percent");
            require(regions.Find(414) && regions.Find(415) && regions.Find(420), "optional weather metrics lost");
        }
    }
}

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
    std::map<int, RECT> weatherControls;
    for (bool dark : {true, false})
    for (int visual = 0; visual < 4; ++visual)
    for (int variant = 0; variant < (visual == 2 ? 14 : visual == 3 ? 10 : 3); ++variant)
    {
        const bool ai = visual == 3;
        const bool media = visual == 1;
        const bool weather = visual == 2;
        auto painted = snapshot;
        if (variant == 2) painted.traffic = {};
        if (weather) painted.weather = WeatherFixture(variant);
        Cards::CardEnvironment environment;
        environment.graphics = &graphics; environment.dark = dark;
        environment.highContrast = weather && variant == 12;
        constexpr UINT width = 480;
        const float heightDip = ai ? 400.0f : media ? Cards::Layout::HeightMedia : weather ?
            Cards::Paint::WeatherHeight(painted, environment, 384) : Cards::Layout::HeightTraffic;
        const UINT height = static_cast<UINT>(std::ceil(heightDip * 1.25f));
        constexpr float dpi = 96.0f;
        const auto format = D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED);
        ComPtr<ID2D1Bitmap1> bitmap;
        winrt::check_hresult(context->CreateBitmap(D2D1::SizeU(width, height), nullptr, 0,
            D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET, format, dpi, dpi), bitmap.GetAddressOf()));
        context->SetTarget(bitmap.Get());
        Cards::DrawScope::Configure(context, 1.25f, D2D1::Point2F());
        context->BeginDraw();
        context->Clear(D2D1::ColorF(0, 0.0f));
        const auto rect = D2D1::RectF(0.5f, 0.5f, 383.5f, heightDip - 0.5f);
        Cards::Draw::FillRound(context, rect, Cards::Layout::CornerRadius,
            environment.highContrast ? Cards::Draw::Colors(environment).panel :
                Cards::ParseColor(dark ? L"#30262B" : L"#F3EFF1", {}));
        Cards::Layout::Table regions;
        if (ai)
        {
            auto& usage = painted.aiUsage;
            usage.status = variant == 1 ? L"partial" : variant == 2 ? L"error" : L"ready";
            usage.error = variant == 1 ? L"部分额度未返回，Token 记录来自官方统计" : variant == 2 ? L"未检测到 Codex，请登录后刷新" : L"";
            usage.updatedAt = L"2026-11-14T18:42:00-05:00";
            usage.providerId = L"codex"; usage.providerName = L"ChatGPT";
            usage.enabledServices = {{L"openai", true}, {L"deepseek", true}, {L"gemini", true}, {L"claude", true}};
            usage.sourceLabel = variant > 0 && variant < 3 ? L"未连接" : L"官方数据";
            usage.providers = {{L"codex",L"ChatGPT",L"subscription",L"openai"},
                {L"openai-api",L"ChatGPT API",L"api",L"openai"},
                {L"deepseek-web",L"DeepSeek",L"subscription",L"deepseek"},
                {L"deepseek-api",L"DeepSeek API",L"api",L"deepseek"},
                {L"gemini-web",L"Gemini",L"subscription",L"gemini"},
                {L"gemini-api",L"Gemini API",L"api",L"gemini"},
                {L"claude-web",L"Claude",L"subscription",L"claude"},
                {L"claude-api",L"Claude API",L"api",L"claude"}};
            if (variant != 2)
            {
                usage.lifetimeTokens = 2976664580LL;
                if (!variant) usage.quotas.push_back({L"weekly", L"每周额度", 19, 10080, 1800000000});
                SYSTEMTIME now{}; GetLocalTime(&now);
                const std::chrono::sys_days today = std::chrono::year{now.wYear}/std::chrono::month{now.wMonth}/std::chrono::day{now.wDay};
                for (int i = 0; i < 84; ++i)
                {
                    if (i % 7 == 2) continue;
                    const std::chrono::year_month_day date{today - std::chrono::days{i}};
                    wchar_t key[16]{}; swprintf_s(key, L"%04d-%02u-%02u", int(date.year()), unsigned(date.month()), unsigned(date.day()));
                    usage.days[key] = i % 11 == 0 ? 0 : 1000000LL * (i % 23 + 1);
                }
            }
            if (variant == 3)
            {
                usage.providerId = L"deepseek-api"; usage.providerName = L"DeepSeek API"; usage.showApi = true;
                usage.balances = {{L"CNY",L"0.00",L"可用余额"},{L"USD",L"12.3456789",L"赠送余额"}};
                usage.quotaNote = L"API 无订阅额度数据";
            }
            if (variant == 4)
            {
                usage.providerId = L"claude-web"; usage.providerName = L"Claude";
                usage.activityUnit = L"requests"; usage.sourceLabel = L"手动记录";
                usage.quotaNote = L"未提供额度 · 查看详情";
                usage.historyNote = L"仅包含手动记录的请求次数";
                for (auto& [date, count] : usage.days) count /= 1000000;
                usage.lifetimeTokens = 502;
            }
            if (variant == 5)
            {
                usage.providerId = L"gemini-api"; usage.providerName = L"Gemini API"; usage.showApi = true; usage.activityUtc = true;
                usage.days.clear(); usage.lifetimeTokens.reset();
                usage.quotaNote = L"官方暂未提供可用额度接口";
                usage.historyNote = L"尚未连接 · 历史未知不代表零";
            }
            if (variant == 6)
            {
                usage.providerId = L"gemini-api"; usage.providerName = L"Gemini API"; usage.showApi = false;
                usage.balances = {{L"USD",L"88.00",L"隐藏金额不可见"}};
            }
            if (variant == 7 || variant == 9)
                usage.quotas = {{L"primary", L"5 小时额度", 10, 300, 1800000000},
                    {L"weekly", L"每周额度", 19, 10080, 1800000000}};
            if (variant == 8)
            {
                for (auto& [date, tokens] : usage.days) tokens = 0;
                usage.lifetimeTokens = 0;
                usage.days[L"9999-12-31"] = 99999;
            }
            Cards::Paint::Interaction interaction;
            interaction.glass = variant == 9;
            interaction.dark = dark;
            if (interaction.glass) environment.material.kind = Cards::MaterialKind::LiquidGlass;
            Cards::Paint::AiUsage(context, painted, environment, regions, 384, heightDip, interaction);
        }
        else if (media)
        {
            painted.media.title = variant ? L"示例歌曲" : L"没有媒体";
            painted.media.artist = variant ? L"示例艺术家" : L"";
            painted.media.canPlayPause = painted.media.canPrevious = painted.media.canNext = variant != 0;
            painted.media.canSeek = variant != 0;
            painted.media.duration = variant ? 240 : 0;
            painted.media.position = variant ? 75 : 0;
            Cards::Paint::Interaction interaction;
            if (variant == 2) interaction.hover[100] = 1.0f;
            Cards::Paint::Media(context, painted, {}, {}, environment, regions, 384, heightDip, interaction);
        }
        else if (weather)
        {
            Cards::Paint::Weather(context, painted, environment, regions, 384, heightDip);
            CheckWeatherLayout(painted.weather, environment, regions, heightDip, variant, weatherControls);
        }
        else
        {
            Cards::Paint::Interaction interaction;
            if (variant == 1) interaction.hover[340] = 1.0f;
            Cards::Paint::Traffic(context, painted, environment, regions, 384, heightDip, interaction);
        }
        Cards::Draw::StrokeRound(context, rect, Cards::Layout::CornerRadius,
            Cards::Draw::Colors(environment).border, 0.5f);
        winrt::check_hresult(context->EndDraw());
        context->SetTarget(nullptr);

        static const wchar_t* weatherNames[] = { L"weather-title-alert", L"weather-long-alert", L"weather-forecast",
            L"weather-forecast-page2", L"weather-hourly-page4", L"weather-no-hourly", L"weather-stale",
            L"weather-empty", L"weather-invalid-data", L"weather-extreme-temperatures", L"weather-updating",
            L"weather-hourly-partial-page", L"weather-high-contrast", L"weather-invalid-times" };
        const auto path = folder / (std::wstring(ai ? (variant == 9 ? L"ai-usage-glass" : variant == 8 ? L"ai-usage-known-zero" : variant == 7 ? L"ai-usage-two-quotas" : variant == 6 ? L"ai-usage-api-hidden" : variant == 5 ? L"ai-usage-api-unknown" : variant == 4 ? L"ai-usage-requests" : variant == 3 ? L"ai-usage-balances" : variant == 2 ? L"ai-usage-error" : variant == 1 ? L"ai-usage-partial" : L"ai-usage") : media ? (variant == 2 ? L"media-hover" : variant == 1 ? L"media" : L"media-empty") :
            weather ? weatherNames[variant] :
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
