#include "CardPch.h"
#include "CardDraw.h"

#include <climits>
#include <cstring>
#include <iostream>
#include <sstream>
#include <stdexcept>

namespace
{
    using namespace Cards;
    using Microsoft::WRL::ComPtr;

    void Require(bool condition, const std::string& message)
    {
        if (!condition) throw std::runtime_error("Card rendering: " + message);
    }

    void Check(HRESULT result, const char* operation, const std::string& label)
    {
        if (SUCCEEDED(result)) return;
        std::ostringstream error;
        error << label << ": " << operation << " HRESULT=0x" << std::hex << result;
        throw std::runtime_error(error.str());
    }

    struct Frame
    {
        Kind kind{};
        UINT width{}, height{};
        std::vector<unsigned char> pixels; // Packed BGRA, cropped back to card coordinates.
        Layout::Table regions;

        const unsigned char* Pixel(UINT x, UINT y) const
        {
            return pixels.data() + (static_cast<size_t>(y) * width + x) * 4;
        }
    };

    std::string Label(Kind kind, float scale, bool dark, D2D1_POINT_2F offset)
    {
        std::ostringstream text;
        text << WideToUtf8(IdOf(kind)) << " scale=" << scale << " theme=" << (dark ? "dark" : "light")
             << " atlas=(" << offset.x << ',' << offset.y << ')';
        return text.str();
    }

    // A dedicated context avoids disturbing the caller's target/state. It shares
    // the real Graphics device, text formats and mask resources with the painters.
    Frame Render(Graphics& graphics, Kind kind, const Snapshot& snapshot, bool dark,
        float scale, D2D1_POINT_2F offset, const Paint::Interaction& interaction = {})
    {
        const auto label = Label(kind, scale, dark, offset);
        CardEnvironment environment;
        environment.graphics = &graphics;
        environment.dark = dark;
        const float width = Layout::WidthDip(kind);
        const float height = std::max(Layout::DefaultHeightDip(kind),
            Paint::MeasureHeight(kind, snapshot, environment, width));
        Frame frame;
        frame.kind = kind;
        frame.width = static_cast<UINT>(std::ceil(width * scale));
        frame.height = static_cast<UINT>(std::ceil(height * scale));
        const auto size = D2D1::SizeU(frame.width + 144, frame.height + 64);
        ComPtr<ID2D1Device> device;
        graphics.ResourceContext()->GetDevice(device.GetAddressOf());
        ComPtr<ID2D1DeviceContext> context;
        Check(device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, context.GetAddressOf()),
            "CreateDeviceContext", label);
        const auto format = D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED);
        ComPtr<ID2D1Bitmap1> target, readback;
        Check(context->CreateBitmap(size, nullptr, 0,
            D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET, format, 96, 96), target.GetAddressOf()),
            "CreateBitmap(target)", label);
        Check(context->CreateBitmap(size, nullptr, 0, D2D1::BitmapProperties1(
            D2D1_BITMAP_OPTIONS_CPU_READ | D2D1_BITMAP_OPTIONS_CANNOT_DRAW, format, 96, 96),
            readback.GetAddressOf()), "CreateBitmap(readback)", label);
        context->SetTarget(target.Get());
        context->BeginDraw();
        // Seed stale state deliberately: Configure must reset both DPI and units.
        context->SetDpi(144, 144);
        context->SetUnitMode(D2D1_UNIT_MODE_PIXELS);
        DrawScope::Configure(context.Get(), scale, offset);
        float dpiX{}, dpiY{};
        context->GetDpi(&dpiX, &dpiY);
        D2D1_MATRIX_3X2_F transform{};
        context->GetTransform(&transform);
        Require(dpiX == 96 && dpiY == 96 && context->GetUnitMode() == D2D1_UNIT_MODE_DIPS &&
            transform._11 == scale && transform._22 == scale && transform._12 == 0 && transform._21 == 0 &&
            transform.dx == offset.x && transform.dy == offset.y, label + ": Configure left stale DPI/transform");
        context->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
        context->Clear(dark ? D2D1::ColorF(0.12f, 0.13f, 0.14f) : D2D1::ColorF(0.94f, 0.95f, 0.96f));
        switch (kind)
        {
        case Kind::Weather: Paint::Weather(context.Get(), snapshot, environment, frame.regions, width, height, interaction); break;
        case Kind::Traffic: Paint::Traffic(context.Get(), snapshot, environment, frame.regions, width, height, interaction); break;
        case Kind::Device: Paint::Device(context.Get(), snapshot, environment, frame.regions, width, height, interaction); break;
        case Kind::Media: Paint::Media(context.Get(), snapshot, {}, {}, environment, frame.regions, width, height, interaction); break;
        case Kind::Update: Paint::Update(context.Get(), snapshot, environment, frame.regions, width, height, interaction); break;
        }
        Check(context->EndDraw(), "EndDraw", label);
        context->SetTarget(nullptr);
        Check(readback->CopyFromBitmap(nullptr, target.Get(), nullptr), "CopyFromBitmap", label);
        frame.pixels.resize(static_cast<size_t>(frame.width) * frame.height * 4);
        D2D1_MAPPED_RECT mapped{};
        Check(readback->Map(D2D1_MAP_OPTIONS_READ, &mapped), "Map", label);
        for (UINT y = 0; y < frame.height; ++y)
            std::memcpy(frame.pixels.data() + static_cast<size_t>(y) * frame.width * 4,
                mapped.bits + (y + static_cast<UINT>(offset.y)) * mapped.pitch +
                static_cast<UINT>(offset.x) * 4, static_cast<size_t>(frame.width) * 4);
        Check(readback->Unmap(), "Unmap", label);
        return frame;
    }

    bool Inside(float x, float y, const D2D1_RECT_F& bounds)
    {
        return x >= bounds.left && x < bounds.right && y >= bounds.top && y < bounds.bottom;
    }

    D2D1_RECT_F Bounds(const Layout::Region& region, float padding = 0)
    {
        return D2D1::RectF(region.bounds.left - padding, region.bounds.top - padding,
            region.bounds.right + padding, region.bounds.bottom + padding);
    }

    const char* PixelRegion(const Frame& frame, float x, float y)
    {
        if (frame.kind != Kind::Weather) return "other-card";
        // These are the fixed weather fixture's actual painter rectangles, not
        // exclusion masks: every category still participates in exact comparison.
        constexpr float leftWidth = (Layout::CardWidth - 2 * Layout::Padding) * 0.45f - 12;
        constexpr float rightLeft = Layout::Padding + (Layout::CardWidth - 2 * Layout::Padding) * 0.45f + 12;
        constexpr float temperatureRight = Layout::Padding + leftWidth - 48;
        if (Inside(x, y, D2D1::RectF(temperatureRight + 6, 52, temperatureRight + 48, 94)))
            return "weather.hero-icon";
        for (int row = 0; row < 5; ++row)
        {
            const float top = 24 + row * 32.0f;
            if (Inside(x, y, D2D1::RectF(rightLeft + 33, top + 2, rightLeft + 61, top + 30)))
                return "weather.forecast-icon";
            if (Inside(x, y, D2D1::RectF(rightLeft + 94, top + 13, 337, top + 19)))
                return "weather.forecast-gradient-rail";
            if (Inside(x, y, D2D1::RectF(rightLeft, top + 7, rightLeft + 30, top + 27)) ||
                Inside(x, y, D2D1::RectF(rightLeft + 64, top + 7, rightLeft + 90, top + 27)) ||
                Inside(x, y, D2D1::RectF(340, top + 7, 368, top + 27)))
                return "weather.forecast-text";
        }
        if (Inside(x, y, D2D1::RectF(16, 138, 16 + leftWidth, 192))) return "weather.alert-text";
        if (Inside(x, y, D2D1::RectF(16, 44, temperatureRight, 102))) return "weather.temperature-text";
        if (Inside(x, y, D2D1::RectF(16, 106, 16 + leftWidth, 138))) return "weather.summary-text";
        if (Inside(x, y, D2D1::RectF(16, 19, 16 + leftWidth - 34, 43))) return "weather.city-text";
        for (const auto& region : frame.regions.All())
            if (Inside(x, y, Bounds(region, 1))) return "weather.control";
        return "weather.other";
    }

    struct PixelDiff
    {
        size_t count{}, deltaOne{}, deltaTwo{}, deltaLarger{}, alpha{}, edge{};
        UINT left{UINT_MAX}, top{UINT_MAX}, right{}, bottom{};
        int maxChannelDelta{};

        void Add(const Frame& before, const Frame& after, UINT x, UINT y)
        {
            ++count;
            left = std::min(left, x); top = std::min(top, y);
            right = std::max(right, x + 1); bottom = std::max(bottom, y + 1);
            int delta{};
            for (int channel = 0; channel < 4; ++channel)
                delta = std::max(delta, std::abs(int(before.Pixel(x, y)[channel]) - int(after.Pixel(x, y)[channel])));
            maxChannelDelta = std::max(maxChannelDelta, delta);
            if (delta == 1) ++deltaOne;
            else if (delta == 2) ++deltaTwo;
            else ++deltaLarger;
            if (before.Pixel(x, y)[3] != after.Pixel(x, y)[3]) ++alpha;
            // Report whether changed pixels sit on high-contrast edges or flat
            // gradients. This is evidence only, never a displacement heuristic
            // used to waive the byte-exact assertion.
            int contrast{};
            for (const auto step : {POINT{-1, 0}, POINT{1, 0}, POINT{0, -1}, POINT{0, 1}})
            {
                const int nx = static_cast<int>(x) + step.x, ny = static_cast<int>(y) + step.y;
                if (nx < 0 || ny < 0 || nx >= static_cast<int>(before.width) || ny >= static_cast<int>(before.height)) continue;
                for (int channel = 0; channel < 3; ++channel)
                    contrast = std::max(contrast, std::abs(int(before.Pixel(x, y)[channel]) - int(before.Pixel(nx, ny)[channel])));
            }
            if (contrast >= 12) ++edge;
        }

        void Describe(std::ostringstream& text) const
        {
            text << "count=" << count << " bbox=[" << left << ',' << top << ',' << right << ',' << bottom
                 << ") max-channel-delta=" << maxChannelDelta << " pixel-max-delta{1=" << deltaOne
                 << ",2=" << deltaTwo << ",>2=" << deltaLarger << "} alpha-changes=" << alpha
                 << " high-contrast-edge-pixels=" << edge;
        }
    };

    // Exact comparison intentionally catches single-pixel shifts. Hover feedback
    // is allowed only in its control (plus stroke/blur support), never in labels.
    size_t Compare(const Frame& before, const Frame& after, float scale, const std::string& label,
        const D2D1_RECT_F& allowed = {}, int channelTolerance = 0)
    {
        Require(before.width == after.width && before.height == after.height, label + ": frame size changed");
        Require(before.regions.All().size() == after.regions.All().size(), label + ": region count changed");
        for (const auto& region : before.regions.All())
        {
            const auto* next = after.regions.Find(region.id);
            Require(next && EqualRect(&region.bounds, &next->bounds) && region.enabled == next->enabled,
                label + ": hit region moved/changed id=" + std::to_string(region.id));
        }
        size_t changed = 0;
        PixelDiff staticDiff, allDiff;
        std::map<std::string, PixelDiff> categories;
        UINT firstX{}, firstY{};
        for (UINT y = 0; y < before.height; ++y)
        for (UINT x = 0; x < before.width; ++x)
        {
            if (std::memcmp(before.Pixel(x, y), after.Pixel(x, y), 4) == 0) continue;
            ++changed;
            allDiff.Add(before, after, x, y);
            if (Inside((x + 0.5f) / scale, (y + 0.5f) / scale, allowed)) continue;
            if (!staticDiff.count) { firstX = x; firstY = y; }
            staticDiff.Add(before, after, x, y);
            categories[PixelRegion(before, (x + 0.5f) / scale, (y + 0.5f) / scale)].Add(before, after, x, y);
        }
        if (staticDiff.maxChannelDelta > channelTolerance || staticDiff.alpha)
        {
            std::ostringstream error;
            error << label << ": static pixel changed; first=(" << firstX << ',' << firstY << ") BGRA=";
            for (int channel = 0; channel < 4; ++channel)
                error << int(before.Pixel(firstX, firstY)[channel]) << "->" << int(after.Pixel(firstX, firstY)[channel]) << ' ';
            error << "\nall differences: "; allDiff.Describe(error);
            error << "\nstatic differences: "; staticDiff.Describe(error);
            size_t textPixels{}, iconPixels{};
            for (const auto& [category, diff] : categories)
            {
                if (category.find("text") != std::string::npos) textPixels += diff.count;
                if (category.find("icon") != std::string::npos) iconPixels += diff.count;
                error << '\n' << category << ": ";
                diff.Describe(error);
            }
            if (before.kind == Kind::Weather)
                error << "\nregion-summary: text-diff-pixels=" << textPixels << " icon-diff-pixels=" << iconPixels
                      << " other-diff-pixels=" << staticDiff.count - textPixels - iconPixels;
            Require(false, error.str());
        }
        if (staticDiff.count)
            std::cout << "Atlas raster quantization: " << label << " pixels=" << staticDiff.count
                << " max-channel-delta=" << staticDiff.maxChannelDelta << '\n';
        return changed;
    }

    Snapshot Fixture()
    {
        Snapshot snapshot;
        snapshot.weather.hasData = true;
        snapshot.weather.city = L"示例城市";
        snapshot.weather.temperature = L"23";
        snapshot.weather.weather = L"多云";
        snapshot.weather.conditionKind = L"cloudy";
        snapshot.weather.observedAt = L"2026-10-30T12:00:00+08:00";
        WeatherAlert alert;
        alert.typeName = L"大风";
        alert.level = L"黄色";
        alert.title = L"大风黄色预警";
        alert.text = L"气象台发布大风黄色预警：预计今天下午至夜间，本市部分地区将出现阵风七至八级，"
            L"请注意防范高空坠物，妥善安置室外物品，避免在广告牌、临时搭建物及大树附近停留。"
            L"有关部门应当做好防风工作，驾驶人员注意道路交通安全。";
        snapshot.weather.alerts.push_back(alert); // One alert: no clock-dependent carousel changes.
        for (int i = 0; i < 5; ++i)
        {
            WeatherForecastDay day;
            day.min = std::to_wstring(12 + i);
            day.max = std::to_wstring(20 + i);
            day.dayKind = L"cloudy";
            snapshot.weather.forecast.push_back(day);
        }
        snapshot.media.title = L"固定媒体标题";
        snapshot.media.artist = L"示例艺术家";
        snapshot.media.source = L"示例播放器";
        snapshot.media.canPlayPause = snapshot.media.canPrevious = snapshot.media.canNext = snapshot.media.canSeek = true;
        snapshot.media.duration = 240;
        snapshot.media.position = 75;
        snapshot.lyrics.available = true;
        snapshot.lyrics.current = L"固定歌词正文，不随重绘移动";
        snapshot.lyrics.next = L"固定的下一行";
        snapshot.lyrics.currentLineIndex = 0;
        snapshot.lyrics.wordScan = 0.45;
        snapshot.lyrics.fontItalic = true;
        snapshot.lyrics.fontUnderline = true;
        snapshot.lyrics.fontStrikeOut = true;
        AudioEndpoint endpoint;
        endpoint.id = L"fixture-output";
        endpoint.name = L"固定输出设备";
        endpoint.isDefault = true;
        endpoint.volume = 0.55;
        snapshot.audio.outputs.push_back(endpoint);
        endpoint.id = L"fixture-input";
        endpoint.name = L"固定输入设备";
        snapshot.audio.inputs.push_back(endpoint);
        snapshot.traffic.available = true;
        snapshot.traffic.scopeLabel = L"上传 + 下载";
        snapshot.traffic.detail = L"固定流量详情";
        for (int i = 0; i < 84; ++i)
        {
            TrafficDay day;
            day.week = i / 7;
            day.weekday = i % 7;
            const std::chrono::year_month_day date{std::chrono::sys_days{
                std::chrono::year{2026}/8/3} + std::chrono::days{i}};
            wchar_t text[16]{};
            swprintf_s(text, L"%04d-%02u-%02u", int(date.year()), unsigned(date.month()), unsigned(date.day()));
            day.date = text;
            day.known = true;
            day.level = 3;
            snapshot.traffic.days.push_back(day);
        }
        snapshot.traffic.today = snapshot.traffic.selectedDate = snapshot.traffic.days.back().date;
        snapshot.update.phase = L"ready";
        snapshot.update.currentVersion = L"261028-1417";
        snapshot.update.latestVersion = L"261030-1200";
        snapshot.update.canInstall = true;
        return snapshot;
    }

    std::vector<DWRITE_LINE_METRICS> Lines(Graphics& graphics, const std::wstring& body,
        const std::string& label)
    {
        // Same typography and column width used by Weather's wrapped alert body.
        const float width = (Layout::CardWidth - 2 * Layout::Padding) * 0.45f - 12.0f;
        const auto format = graphics.TextFormat(L"Microsoft YaHei UI", 10, 400, DWRITE_TEXT_ALIGNMENT_LEADING, true);
        Require(format != nullptr, label + ": wrapped TextFormat unavailable");
        ComPtr<IDWriteTextLayout> layout;
        Check(graphics.DWrite()->CreateTextLayout(body.c_str(), static_cast<UINT32>(body.size()),
            format, width, 100000, layout.GetAddressOf()), "CreateTextLayout", label);
        UINT32 count{};
        const auto result = layout->GetLineMetrics(nullptr, 0, &count);
        Require(result == E_NOT_SUFFICIENT_BUFFER && count > 3, label + ": long warning did not wrap");
        std::vector<DWRITE_LINE_METRICS> lines(count);
        Check(layout->GetLineMetrics(lines.data(), count, &count), "GetLineMetrics", label);
        UINT32 length{};
        for (const auto& line : lines) length += line.length;
        Require(length == body.size(), label + ": line lengths lost warning characters");
        return lines;
    }

    void EvictFormats(Graphics& graphics, const std::string& label)
    {
        for (int i = 0; i < 80; ++i)
            Require(graphics.TextFormat(L"Microsoft YaHei UI", 7.125f + i * 0.125f, 400,
                DWRITE_TEXT_ALIGNMENT_CENTER, i % 2 == 0) != nullptr, label + ": cache churn format creation");
    }

    void CompareLines(const std::vector<DWRITE_LINE_METRICS>& before,
        const std::vector<DWRITE_LINE_METRICS>& after, const std::string& label)
    {
        Require(before.size() == after.size(), label + ": wrapped line count changed");
        for (size_t i = 0; i < before.size(); ++i)
            Require(before[i].length == after[i].length && before[i].newlineLength == after[i].newlineLength &&
                before[i].trailingWhitespaceLength == after[i].trailingWhitespaceLength &&
                std::abs(before[i].baseline - after[i].baseline) < 0.0001f &&
                std::abs(before[i].height - after[i].height) < 0.0001f,
                label + ": wrapped length/baseline changed at line=" + std::to_string(i));
    }

    void CheckCircleAndGlyph(const Frame& before, const Frame& after, const Layout::Region& region,
        float scale, bool dark, const std::string& label)
    {
        const auto rect = Bounds(region);
        const float centerY = (rect.top + rect.bottom) / 2;
        // The circular transport/mute background is flat here, outside the glyph.
        const UINT sampleX = static_cast<UINT>((rect.left + 4) * scale);
        const UINT sampleY = static_cast<UINT>(centerY * scale);
        const float oldBackground = before.Pixel(sampleX, sampleY)[0];
        const float newBackground = after.Pixel(sampleX, sampleY)[0];
        const float foreground = dark ? 255.0f : 20.0f;
        size_t ink = 0, corners = 0;
        for (UINT y = 0; y < before.height; ++y)
        for (UINT x = 0; x < before.width; ++x)
        {
            const float dipX = (x + 0.5f) / scale, dipY = (y + 0.5f) / scale;
            if (Inside(dipX, dipY, D2D1::RectF(rect.left + 0.5f, rect.top + 0.5f, rect.left + 3, rect.top + 3)))
            {
                ++corners;
                Require(std::memcmp(before.Pixel(x, y), after.Pixel(x, y), 4) == 0,
                    label + ": circular corner received rectangular hover feedback");
            }
            if (!Inside(dipX, dipY, D2D1::RectF(rect.left + 8, rect.top + 8, rect.right - 8, rect.bottom - 8))) continue;
            // Compare recovered glyph coverage, not blended edge colours: changing
            // a background legitimately changes antialiased glyph pixel colours.
            const float oldCoverage = (before.Pixel(x, y)[0] - oldBackground) / (foreground - oldBackground);
            const float newCoverage = (after.Pixel(x, y)[0] - newBackground) / (foreground - newBackground);
            if (oldCoverage > 0.7f) ++ink;
            Require(std::abs(oldCoverage - newCoverage) <= 0.03f,
                label + ": glyph coverage moved or feedback painted above glyph at pixel=" +
                std::to_string(x) + "," + std::to_string(y));
        }
        Require(corners > 0 && ink > 4, label + ": circle/glyph check sampled no useful pixels");
    }

    void CheckHalo(const Frame& zero, const Frame& half, const Frame& full,
        const Layout::Region& region, float scale, const std::string& label)
    {
        size_t signal = 0;
        for (UINT y = 0; y < zero.height; ++y)
        for (UINT x = 0; x < zero.width; ++x)
        {
            const float dipX = (x + 0.5f) / scale, dipY = (y + 0.5f) / scale;
            if (!Inside(dipX, dipY, Bounds(region, 18)) || Inside(dipX, dipY, Bounds(region, 1.5f))) continue;
            for (int channel = 0; channel < 3; ++channel)
            {
                const int a = zero.Pixel(x, y)[channel], b = half.Pixel(x, y)[channel], c = full.Pixel(x, y)[channel];
                if (c != a) ++signal;
                // Fixed mask geometry makes the outer halo affine in opacity.
                // Three byte levels cover mask + blur + composite quantization.
                Require(std::abs(2 * b - a - c) <= 6,
                    label + ": outer halo changed shape at pixel=" + std::to_string(x) + "," + std::to_string(y));
            }
        }
        Require(signal > 0, label + ": halo produced no measurable outer feedback");
    }
}

void TestCardRenderingStability(Cards::Graphics& graphics)
{
    Require(graphics.Ready(), "Graphics must already be initialized in the caller's apartment");
    const Snapshot snapshot = Fixture();
    auto revised = snapshot;
    // Automatic snapshot delivery with unchanged visible data must be pixel-stable.
    ++revised.weather.revision; ++revised.traffic.revision; ++revised.audio.revision;
    ++revised.media.revision; ++revised.lyrics.revision; ++revised.update.revision;
    const Kind kinds[] = {Kind::Weather, Kind::Device, Kind::Media, Kind::Traffic, Kind::Update};
    for (bool dark : {false, true})
    for (float dpi : {1.0f, 1.25f, 1.5f, 2.0f})
    for (float fit : {1.0f, 0.93f})
    {
        const float scale = dpi * fit;
        const D2D1_POINT_2F origin{};
        const auto label = Label(Kind::Weather, scale, dark, origin);
        const auto lines = Lines(graphics, snapshot.weather.alerts.front().text, label);
        const auto weather = Render(graphics, Kind::Weather, snapshot, dark, scale, origin);
        for (Kind kind : kinds) Render(graphics, kind, snapshot, dark, scale, origin);
        CompareLines(lines, Lines(graphics, snapshot.weather.alerts.front().text, label), label + " interleaved");
        EvictFormats(graphics, label);
        CompareLines(lines, Lines(graphics, snapshot.weather.alerts.front().text, label), label + " cache eviction");
        Compare(weather, Render(graphics, Kind::Weather, revised, dark, scale, origin), scale, label + " cache/revision");
        auto blank = snapshot;
        blank.weather.alerts.front().text = L" ";
        const auto withoutBody = Render(graphics, Kind::Weather, blank, dark, scale, origin);
        Require(Compare(weather, withoutBody, scale, label + " warning body presence",
            D2D1::RectF(15, 155, 163.4f, 193)) > 20, label + ": real weather painter did not draw wrapped warning");

        for (Kind kind : kinds)
        {
            const auto baseline = Render(graphics, kind, snapshot, dark, scale, origin);
            const auto kindLabel = Label(kind, scale, dark, origin);
            Compare(baseline, Render(graphics, kind, revised, dark, scale, origin), scale, kindLabel + " repeat/revision");
            for (const auto offset : {D2D1::Point2F(17, 29), D2D1::Point2F(111, 7)})
            {
                const auto translated = Render(graphics, kind, snapshot, dark, scale, offset);
                Compare(translated, Render(graphics, kind, snapshot, dark, scale, offset), scale,
                    Label(kind, scale, dark, offset) + " same-offset repeat");
                // Integer translation can change a vector edge's final UNORM8
                // rounding by one level. Repeated frames remain byte-exact;
                // this narrow allowance applies only across atlas origins.
                Compare(baseline, translated, scale,
                    Label(kind, scale, dark, offset) + " atlas translation (same-offset repeat byte-exact)", {}, 1);
            }

            const std::vector<int> ids = kind == Kind::Device ? std::vector<int>{200, 201, 202} :
                kind == Kind::Media ? std::vector<int>{100} : kind == Kind::Traffic ? std::vector<int>{340} :
                kind == Kind::Weather ? std::vector<int>{400} : std::vector<int>{500};
            for (int id : ids)
            {
                const auto* region = baseline.regions.Find(id);
                Require(region && region->enabled, kindLabel + ": fixture control unavailable id=" + std::to_string(id));
                const auto controlLabel = kindLabel + " control=" + std::to_string(id);
                Frame half;
                for (float opacity : {0.25f, 0.5f, 1.0f})
                {
                    Paint::Interaction interaction;
                    interaction.hover[id] = opacity;
                    auto hovered = Render(graphics, kind, snapshot, dark, scale, origin, interaction);
                    const auto frameLabel = controlLabel + " hover=" + std::to_string(opacity);
                    auto allowed = Bounds(*region, kind == Kind::Traffic ? 20.0f : 2.0f);
                    if (region->type == Layout::RegionType::Slider)
                    {
                        const float center = (region->bounds.top + region->bounds.bottom) / 2.0f;
                        allowed = D2D1::RectF(allowed.left - 6, center - 7, allowed.right + 6, center + 7);
                    }
                    Require(Compare(baseline, hovered, scale, frameLabel, allowed) > 0, frameLabel + ": feedback absent");
                    if (id == 100 || id == 201)
                        CheckCircleAndGlyph(baseline, hovered, *region, scale, dark, frameLabel);
                    if (kind == Kind::Traffic && opacity == 1.0f)
                        CheckHalo(baseline, half, hovered, *region, scale, frameLabel);
                    if (opacity == 0.5f) half = std::move(hovered);
                }
                if (region->type == Layout::RegionType::Button)
                {
                    Paint::Interaction interaction;
                    interaction.pressed = id;
                    const auto pressed = Render(graphics, kind, snapshot, dark, scale, origin, interaction);
                    Require(Compare(baseline, pressed, scale, controlLabel + " pressed", Bounds(*region, 2)) > 0,
                        controlLabel + ": pressed feedback absent");
                    if (id == 100 || id == 201)
                        CheckCircleAndGlyph(baseline, pressed, *region, scale, dark, controlLabel + " pressed");
                }
                Paint::Interaction focus;
                focus.focused = id;
                const auto focused = Render(graphics, kind, snapshot, dark, scale, origin, focus);
                Require(Compare(baseline, focused, scale, controlLabel + " focused",
                    Bounds(*region, kind == Kind::Traffic ? 20.0f : 2.0f)) > 0,
                    controlLabel + ": keyboard focus feedback absent");
                if (id == 100 || id == 201)
                    CheckCircleAndGlyph(baseline, focused, *region, scale, dark, controlLabel + " focused");
                // Returning to the same immutable input must remove all hover residue.
                Compare(baseline, Render(graphics, kind, snapshot, dark, scale, origin), scale, controlLabel + " leave");
            }
        }
    }
    // Cells without a persistent halo must also approach zero continuously.
    // Testing only populated level-3 cells misses the old base-intensity jump.
    for (const int level : {0, 1})
    for (const bool dark : {false, true})
    {
        auto low = snapshot;
        low.traffic.days[40].level = level;
        constexpr float scale = 1.25f;
        const auto label = "low traffic level=" + std::to_string(level);
        const auto zero = Render(graphics, Kind::Traffic, low, dark, scale, {});
        Paint::Interaction interaction;
        interaction.hover[340] = 0.001f;
        const auto nearZero = Render(graphics, Kind::Traffic, low, dark, scale, {}, interaction);
        for (size_t i = 0; i < zero.pixels.size(); ++i)
            if (std::abs(int(zero.pixels[i]) - int(nearZero.pixels[i])) > 1)
                Require(false, label + ": first hover frame jumps in brightness");
        interaction.hover[340] = 0.5f;
        const auto half = Render(graphics, Kind::Traffic, low, dark, scale, {}, interaction);
        interaction.hover[340] = 1.0f;
        const auto full = Render(graphics, Kind::Traffic, low, dark, scale, {}, interaction);
        CheckHalo(zero, half, full, *zero.regions.Find(340), scale, label);
    }
    std::cout << "Card pixel stability and Chinese line metrics passed: 100/125/150/200% DPI, "
        "100/93% fit, light/dark, three atlas origins, cache eviction, control shapes and low-level halos.\n";
}
