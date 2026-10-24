#include "../TrafficMonitor/NativeMediaPainter.h"

#include <d2d1_1.h>
#include <d3d11.h>
#include <dwrite_3.h>
#include <wincodec.h>
#include <wrl/client.h>

#include <algorithm>
#include <cmath>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>

#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dwrite.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "windowscodecs.lib")
#pragma comment(lib, "ole32.lib")

using Microsoft::WRL::ComPtr;

namespace
{
    void Require(bool condition, const char* message)
    {
        if (!condition) throw std::runtime_error(message);
    }

    std::wstring Base64(const std::vector<unsigned char>& bytes)
    {
        constexpr wchar_t table[] =
            L"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        std::wstring result;
        result.reserve((bytes.size() + 2) / 3 * 4);
        for (size_t index = 0; index < bytes.size(); index += 3)
        {
            const unsigned first = bytes[index];
            const unsigned second = index + 1 < bytes.size() ? bytes[index + 1] : 0;
            const unsigned third = index + 2 < bytes.size() ? bytes[index + 2] : 0;
            const unsigned combined = (first << 16) | (second << 8) | third;
            result.push_back(table[(combined >> 18) & 63]);
            result.push_back(table[(combined >> 12) & 63]);
            result.push_back(index + 1 < bytes.size() ? table[(combined >> 6) & 63] : L'=');
            result.push_back(index + 2 < bytes.size() ? table[combined & 63] : L'=');
        }
        return result;
    }

    std::wstring CreateCoverDataUri(IWICImagingFactory* wic)
    {
        ComPtr<IWICBitmap> source;
        Require(SUCCEEDED(wic->CreateBitmap(12, 12, GUID_WICPixelFormat32bppPBGRA,
            WICBitmapCacheOnLoad, &source)), "Create offscreen cover bitmap.");
        WICRect area{ 0, 0, 12, 12 };
        ComPtr<IWICBitmapLock> lock;
        Require(SUCCEEDED(source->Lock(&area, WICBitmapLockWrite, &lock)),
            "Lock offscreen cover bitmap.");
        UINT stride{};
        UINT size{};
        BYTE* pixels{};
        Require(SUCCEEDED(lock->GetStride(&stride)), "Read cover stride.");
        Require(SUCCEEDED(lock->GetDataPointer(&size, &pixels)), "Read cover pixels.");
        for (UINT y = 0; y < 12; ++y)
        {
            for (UINT x = 0; x < 12; ++x)
            {
                BYTE* pixel = pixels + y * stride + x * 4;
                pixel[0] = static_cast<BYTE>(40 + x * 12);
                pixel[1] = static_cast<BYTE>(90 + y * 10);
                pixel[2] = 210;
                pixel[3] = 255;
            }
        }
        lock.Reset();

        std::vector<unsigned char> encoded(64 * 1024);
        ComPtr<IWICStream> stream;
        Require(SUCCEEDED(wic->CreateStream(&stream)), "Create cover stream.");
        Require(SUCCEEDED(stream->InitializeFromMemory(encoded.data(),
            static_cast<DWORD>(encoded.size()))), "Initialize cover stream.");
        ComPtr<IWICBitmapEncoder> encoder;
        Require(SUCCEEDED(wic->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder)),
            "Create PNG encoder.");
        Require(SUCCEEDED(encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache)),
            "Initialize PNG encoder.");
        ComPtr<IWICBitmapFrameEncode> frame;
        Require(SUCCEEDED(encoder->CreateNewFrame(&frame, nullptr)), "Create PNG frame.");
        Require(SUCCEEDED(frame->Initialize(nullptr)), "Initialize PNG frame.");
        WICPixelFormatGUID format = GUID_WICPixelFormat32bppPBGRA;
        Require(SUCCEEDED(frame->SetPixelFormat(&format)), "Set PNG pixel format.");
        Require(SUCCEEDED(frame->WriteSource(source.Get(), nullptr)), "Write PNG pixels.");
        Require(SUCCEEDED(frame->Commit()), "Commit PNG frame.");
        Require(SUCCEEDED(encoder->Commit()), "Commit PNG encoder.");
        STATSTG statistics{};
        Require(SUCCEEDED(stream->Stat(&statistics, STATFLAG_NONAME)), "Read PNG size.");
        Require(statistics.cbSize.QuadPart > 0 &&
            statistics.cbSize.QuadPart <= encoded.size(), "PNG size is valid.");
        encoded.resize(static_cast<size_t>(statistics.cbSize.QuadPart));
        return L"data:image/png;base64," + Base64(encoded);
    }

    void SavePng(IWICImagingFactory* wic, IWICBitmap* bitmap, const std::filesystem::path& path)
    {
        ComPtr<IWICStream> stream;
        Require(SUCCEEDED(wic->CreateStream(&stream)), "Create output stream.");
        Require(SUCCEEDED(stream->InitializeFromFilename(path.c_str(), GENERIC_WRITE)),
            "Open offscreen output.");
        ComPtr<IWICBitmapEncoder> encoder;
        Require(SUCCEEDED(wic->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder)),
            "Create output encoder.");
        Require(SUCCEEDED(encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache)),
            "Initialize output encoder.");
        ComPtr<IWICBitmapFrameEncode> frame;
        Require(SUCCEEDED(encoder->CreateNewFrame(&frame, nullptr)), "Create output frame.");
        Require(SUCCEEDED(frame->Initialize(nullptr)), "Initialize output frame.");
        WICPixelFormatGUID format = GUID_WICPixelFormat32bppPBGRA;
        Require(SUCCEEDED(frame->SetPixelFormat(&format)), "Set output format.");
        Require(SUCCEEDED(frame->WriteSource(bitmap, nullptr)), "Write output pixels.");
        Require(SUCCEEDED(frame->Commit()), "Commit output frame.");
        Require(SUCCEEDED(encoder->Commit()), "Commit output encoder.");
    }

    bool HasVisiblePixels(IWICBitmap* bitmap)
    {
        WICRect area{ 0, 0, 400, 48 };
        ComPtr<IWICBitmapLock> lock;
        if (FAILED(bitmap->Lock(&area, WICBitmapLockRead, &lock))) return false;
        UINT stride{}, size{};
        BYTE* pixels{};
        if (FAILED(lock->GetStride(&stride)) || FAILED(lock->GetDataPointer(&size, &pixels)))
            return false;
        for (int y = 0; y < area.Height; ++y)
        {
            for (int x = 0; x < area.Width; ++x)
            {
                if (pixels[y * stride + x * 4 + 3] != 0)
                    return true;
            }
        }
        return false;
    }

    // Lowest row in [firstRow, 48) holding text ink between x = 0 and 300. The
    // background surface is opaque, so ink has to be found by luminance, not alpha.
    int LowestInkRow(IWICBitmap* bitmap, int firstRow)
    {
        WICRect area{ 0, firstRow, 300, 48 - firstRow };
        ComPtr<IWICBitmapLock> lock;
        if (FAILED(bitmap->Lock(&area, WICBitmapLockRead, &lock))) return -1;
        UINT stride{}, size{};
        BYTE* pixels{};
        if (FAILED(lock->GetStride(&stride)) || FAILED(lock->GetDataPointer(&size, &pixels)))
            return -1;
        for (int y = area.Height - 1; y >= 0; --y)
        {
            for (int x = 0; x < area.Width; ++x)
            {
                const BYTE* pixel = pixels + y * stride + x * 4;
                if ((pixel[0] + pixel[1] + pixel[2]) / 3 > 90) return firstRow + y;
            }
        }
        return -1;
    }

    ComPtr<IWICBitmap> CopyTextureToWic(IWICImagingFactory* wic,
        ID3D11Device* device, ID3D11DeviceContext* context, ID3D11Texture2D* texture)
    {
        D3D11_TEXTURE2D_DESC source{};
        texture->GetDesc(&source);
        D3D11_TEXTURE2D_DESC staging = source;
        staging.Usage = D3D11_USAGE_STAGING;
        staging.BindFlags = 0;
        staging.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        staging.MiscFlags = 0;
        ComPtr<ID3D11Texture2D> stagingTexture;
        Require(SUCCEEDED(device->CreateTexture2D(&staging, nullptr, &stagingTexture)),
            "Create offscreen staging texture.");
        context->CopyResource(stagingTexture.Get(), texture);
        D3D11_MAPPED_SUBRESOURCE mapped{};
        Require(SUCCEEDED(context->Map(stagingTexture.Get(), 0, D3D11_MAP_READ, 0, &mapped)),
            "Map offscreen staging texture.");
        ComPtr<IWICBitmap> bitmap;
        Require(SUCCEEDED(wic->CreateBitmap(source.Width, source.Height,
            GUID_WICPixelFormat32bppPBGRA, WICBitmapCacheOnLoad, &bitmap)),
            "Create offscreen readback bitmap.");
        WICRect area{ 0, 0, static_cast<INT>(source.Width), static_cast<INT>(source.Height) };
        ComPtr<IWICBitmapLock> lock;
        Require(SUCCEEDED(bitmap->Lock(&area, WICBitmapLockWrite, &lock)),
            "Lock offscreen readback bitmap.");
        UINT stride{}, size{};
        BYTE* target{};
        Require(SUCCEEDED(lock->GetStride(&stride)), "Read readback stride.");
        Require(SUCCEEDED(lock->GetDataPointer(&size, &target)), "Read readback pixels.");
        for (UINT row = 0; row < source.Height; ++row)
            memcpy(target + row * stride,
                static_cast<const BYTE*>(mapped.pData) + row * mapped.RowPitch,
                static_cast<size_t>(source.Width) * 4);
        lock.Reset();
        context->Unmap(stagingTexture.Get(), 0);
        return bitmap;
    }

    NativeMedia::PaintState MakeState(const std::wstring& coverUri, NativeMedia::Cover& cover)
    {
        NativeMedia::EffectiveStyle style;
        style.fontFamily = L"Segoe UI";
        style.fontSizePx = 13;
        style.fontWeight = 600;
        style.layoutScalePercent = 100;
        style.showCover = true;
        style.coverPosition = L"Left";
        style.coverSizePx = 34;
        style.coverGapPx = 8;
        style.coverCornerRadiusPx = 6;
        style.viewportDescenderBufferPx = 2;
        style.lyricsPaneTopPaddingPx = 3;
        style.lyricsPaneRightPaddingPx = 4;
        style.lyricsPaneLeftPaddingPx = 2;
        style.lineTextBottomPaddingPx = 2;
        style.primaryOffsetYPx = 1;
        style.secondaryOffsetYPx = 1;
        style.surfaceRadiusPx = 8;
        style.layerTransitionOffsetPx = 2;
        style.coverFallbackFontSizePx = 13;
        style.spectrumWidthPx = 210;
        style.spectrumHeightPx = 22;
        style.spectrumGapPx = 3;
        style.spectrumBarWidthPx = 3;
        style.spectrumBarHeightPx = 8;
        style.spectrumLowHeightPx = 6;
        style.spectrumHighHeightPx = 18;
        style.spectrumMiddleHeightPx = 11;
        style.primaryColor = L"rgba(255,255,255,0.95)";
        style.secondaryColor = L"rgba(255,255,255,0.62)";
        style.translationColor = L"rgba(255,255,255,0.76)";
        style.wordScanOverlayColor = L"rgba(255,255,255,0.98)";
        style.surfaceColor = L"rgba(24,30,40,0.88)";
        style.surfaceShadow = true;
        style.textShadow = true;
        style.textAlignment = L"Left";

        NativeMedia::PaintState state;
        state.style = style;
        state.frame.revision = 2;
        state.frame.sourceId = L"fixture";
        state.frame.isPlaying = true;
        state.frame.hasMedia = true;
        state.frame.trackId = L"fixture-track";
        state.frame.lyrics.trackId = L"fixture-track";
        state.frame.lyrics.current = L"Native media renderer";
        state.frame.lyrics.next = L"Composition-native next line";
        state.frame.lyrics.currentTranslation = L"native line translation \u672c\u5730\u6b4c\u8bcd";
        state.frame.lyrics.nextTranslation = L"next translation";
        state.frame.lyrics.currentLineIndex = 3;
        state.frame.lyrics.progress = 0.62;
        state.frame.lyrics.isPlaying = true;
        state.frame.lyrics.translationMode = true;
        state.frame.lyrics.scene = NativeMedia::Scene::Lyrics;
        state.frame.lyrics.hasWordScanProgress = true;
        state.frame.lyrics.wordScanProgress = 0.62;
        state.tuning.barCount = 16;
        cover.dataUri = coverUri;
        cover.fallbackText = L"N";
        cover.fallbackColor = L"rgba(40,100,180,1)";
        cover.trackId = L"fixture-track";
        state.cover = &cover;
        return state;
    }
}

int main(int argc, char** argv)
{
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    try
    {
        const auto output = std::filesystem::absolute(argc > 1 ?
            std::filesystem::path(argv[1]) : std::filesystem::path(L"NativeMediaOffscreen"));
        std::filesystem::create_directories(output);

        ComPtr<IWICImagingFactory> wic;
        Require(SUCCEEDED(CoCreateInstance(CLSID_WICImagingFactory, nullptr,
            CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&wic))), "Create WIC factory.");
        ComPtr<ID3D11Device> d3d;
        ComPtr<ID3D11DeviceContext> d3dContext;
        D3D_FEATURE_LEVEL level{};
        Require(SUCCEEDED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION,
            &d3d, &level, &d3dContext)), "Create WARP device.");
        ComPtr<IDXGIDevice> dxgi;
        Require(SUCCEEDED(d3d.As(&dxgi)), "Query DXGI device.");
        ComPtr<ID2D1Factory> factoryBase;
        Require(SUCCEEDED(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED,
            __uuidof(ID2D1Factory), nullptr,
            reinterpret_cast<void**>(factoryBase.GetAddressOf()))),
            "Create Direct2D factory.");
        ComPtr<ID2D1Factory1> factory;
        Require(SUCCEEDED(factoryBase.As(&factory)), "Query Direct2D factory 1.");
        ComPtr<ID2D1Device> d2d;
        Require(SUCCEEDED(factory->CreateDevice(dxgi.Get(), &d2d)), "Create Direct2D device.");
        ComPtr<ID2D1DeviceContext> context;
        Require(SUCCEEDED(d2d->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &context)),
            "Create Direct2D context.");
        context->SetDpi(96, 96);
        ComPtr<IDWriteFactory> write;
        Require(SUCCEEDED(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED,
            __uuidof(IDWriteFactory),
            reinterpret_cast<IUnknown**>(write.GetAddressOf()))), "Create DirectWrite factory.");
        D3D11_TEXTURE2D_DESC textureDescription{};
        textureDescription.Width = 400;
        textureDescription.Height = 48;
        textureDescription.MipLevels = 1;
        textureDescription.ArraySize = 1;
        textureDescription.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        textureDescription.SampleDesc.Count = 1;
        textureDescription.Usage = D3D11_USAGE_DEFAULT;
        textureDescription.BindFlags = D3D11_BIND_RENDER_TARGET;
        ComPtr<ID3D11Texture2D> renderTexture;
        Require(SUCCEEDED(d3d->CreateTexture2D(&textureDescription, nullptr,
            &renderTexture)), "Create offscreen render texture.");
        ComPtr<IDXGISurface> renderSurface;
        Require(SUCCEEDED(renderTexture.As(&renderSurface)), "Query render texture surface.");
        const auto targetProperties = D2D1::BitmapProperties1(
            D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
            D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM,
                D2D1_ALPHA_MODE_PREMULTIPLIED), 96, 96);
        ComPtr<ID2D1Bitmap1> target;
        Require(SUCCEEDED(context->CreateBitmapFromDxgiSurface(renderSurface.Get(),
            targetProperties, &target)), "Create Direct2D target bitmap.");

        NativeMedia::NativeMediaPainter painter;
        std::wstring error;
        Require(painter.Initialize(factory.Get(), d2d.Get(), write.Get(), error),
            "Initialize native media painter.");
        const auto coverUri = CreateCoverDataUri(wic.Get());
        NativeMedia::Cover cover;
        auto state = MakeState(coverUri, cover);
        NativeMedia::CoverBitmap coverBitmap;
        Require(painter.PrepareCover(*state.cover, 96u, coverBitmap),
            "Decode offscreen cover.");
        // The cover source is 12x12, so a 96 px edge keeps the native extent and the cache
        // holds one entry for this size only.
        D2D1_SIZE_U decoded{};
        if (coverBitmap.bitmap) decoded = coverBitmap.bitmap->GetPixelSize();
        Require(decoded.width == 12 && decoded.height == 12,
            "Cover keeps its extent when it is smaller than the display edge.");
        Require(painter.CoverCacheBytes() == 12 * 12 * 4, "Cover cache accounts decoded bytes.");

        NativeMedia::CoverBitmap smallBitmap;
        Require(painter.PrepareCover(*state.cover, 6u, smallBitmap),
            "Decode offscreen cover at a smaller edge.");
        D2D1_SIZE_U smallDecoded{};
        if (smallBitmap.bitmap) smallDecoded = smallBitmap.bitmap->GetPixelSize();
        Require(smallDecoded.width == 6 && smallDecoded.height == 6,
            "Cover is scaled down to the display edge.");
        // Both sizes coexist: the cache is keyed by edge as well as by source.
        Require(painter.CoverCacheBytes() == 12 * 12 * 4 + 6 * 6 * 4,
            "Cover cache keeps one entry per decoded edge.");

        painter.BeginPaintFrame();
        context->SetTarget(target.Get());
        context->BeginDraw();
        context->Clear(D2D1::ColorF(0, 0, 0, 0));
        painter.PaintBackground(context.Get(), state, 400, 48, 1);
        NativeMedia::CoverPaint coverPaint{ &state, 400, 48, 1, coverBitmap.bitmap.Get(),
            state.cover };
        painter.PaintCover(context.Get(), coverPaint);
        NativeMedia::LyricsPaint lyricsPaint;
        lyricsPaint.state = &state;
        lyricsPaint.width = 400;
        lyricsPaint.height = 48;
        lyricsPaint.dpiScale = 1;
        lyricsPaint.wordScanProgress = 0.62;
        lyricsPaint.secondaryOpacity = 0.64;
        painter.PaintLyrics(context.Get(), lyricsPaint);
        Require(SUCCEEDED(context->EndDraw()), "Draw offscreen lyrics frame.");
        context->SetTarget(nullptr);
        painter.EndPaintFrame();
        auto targetBitmap = CopyTextureToWic(wic.Get(), d3d.Get(), d3dContext.Get(),
            renderTexture.Get());
        SavePng(wic.Get(), targetBitmap.Get(), output / L"lyrics-translation-scan.png");
        Require(HasVisiblePixels(targetBitmap.Get()), "Lyrics offscreen pixels are visible.");

        state.frame.lyrics.translationMode = false;
        state.frame.lyrics.current =
            L"Long lyric line with a horizontal scan that must follow the rendered text";
        state.frame.lyrics.currentTranslation.clear();
        state.frame.lyrics.hasWordScanProgress = true;
        state.frame.lyrics.wordScanProgress = 0.82;
        painter.BeginPaintFrame();
        context->SetTarget(target.Get());
        context->BeginDraw();
        context->Clear(D2D1::ColorF(0, 0, 0, 0));
        painter.PaintBackground(context.Get(), state, 400, 48, 1);
        painter.PaintCover(context.Get(), coverPaint);
        lyricsPaint.wordScanProgress = 0.82;
        painter.PaintLyrics(context.Get(), lyricsPaint);
        Require(SUCCEEDED(context->EndDraw()), "Draw offscreen long lyric frame.");
        context->SetTarget(nullptr);
        painter.EndPaintFrame();
        targetBitmap = CopyTextureToWic(wic.Get(), d3d.Get(), d3dContext.Get(),
            renderTexture.Get());
        SavePng(wic.Get(), targetBitmap.Get(), output / L"lyrics-long-scroll.png");
        Require(HasVisiblePixels(targetBitmap.Get()), "Long lyric offscreen pixels are visible.");

        state.frame.lyrics.scene = NativeMedia::Scene::Spectrum;
        state.frame.lyrics.translationMode = false;
        state.frame.isBrowser = false;
        std::vector<double> bars;
        for (int index = 0; index < 16; ++index)
            bars.push_back(0.2 + 0.75 * std::abs(std::sin(index * 0.45)));
        painter.BeginPaintFrame();
        context->SetTarget(target.Get());
        context->BeginDraw();
        context->Clear(D2D1::ColorF(0, 0, 0, 0));
        painter.PaintBackground(context.Get(), state, 400, 48, 1);
        NativeMedia::SpectrumPaint spectrumPaint{ &state, 400, 48, 1, &bars };
        painter.PaintSpectrum(context.Get(), spectrumPaint);
        Require(SUCCEEDED(context->EndDraw()), "Draw offscreen spectrum frame.");
        context->SetTarget(nullptr);
        painter.EndPaintFrame();
        targetBitmap = CopyTextureToWic(wic.Get(), d3d.Get(), d3dContext.Get(),
            renderTexture.Get());
        SavePng(wic.Get(), targetBitmap.Get(), output / L"spectrum.png");
        Require(HasVisiblePixels(targetBitmap.Get()), "Spectrum offscreen pixels are visible.");

        state.frame.isBrowser = true;
        state.frame.browser = NativeMedia::Browser{ true, L"Browser fixture",
            L"Fixture artist", L"Chrome", true, true, true, true };
        NativeMedia::BrowserPaint hitPaint{ &state, 400, 48, 1 };
        const auto browserLayout = NativeMedia::CalculateLayout(400, 48, 1,
            state.style, false);
        NativeMedia::HitAction hitAction{};
        Require(painter.HitTest(hitPaint,
            (browserLayout.browser.previous.left + browserLayout.browser.previous.right) / 2,
            (browserLayout.browser.previous.top + browserLayout.browser.previous.bottom) / 2,
            hitAction) && hitAction == NativeMedia::HitAction::Previous,
            "Previous browser control hit testing.");
        Require(painter.HitTest(hitPaint,
            (browserLayout.browser.playPause.left + browserLayout.browser.playPause.right) / 2,
            (browserLayout.browser.playPause.top + browserLayout.browser.playPause.bottom) / 2,
            hitAction) && hitAction == NativeMedia::HitAction::PlayPause,
            "Play/pause browser control hit testing.");
        Require(painter.HitTest(hitPaint,
            (browserLayout.browser.next.left + browserLayout.browser.next.right) / 2,
            (browserLayout.browser.next.top + browserLayout.browser.next.bottom) / 2,
            hitAction) && hitAction == NativeMedia::HitAction::Next,
            "Next browser control hit testing.");
        state.frame.browser.canPrevious = false;
        Require(painter.HitTest(hitPaint,
            (browserLayout.browser.previous.left + browserLayout.browser.previous.right) / 2,
            (browserLayout.browser.previous.top + browserLayout.browser.previous.bottom) / 2,
            hitAction) && hitAction == NativeMedia::HitAction::None,
            "Disabled browser control consumes hit testing without an action.");
        state.frame.browser.canPrevious = true;
        painter.BeginPaintFrame();
        context->SetTarget(target.Get());
        context->BeginDraw();
        context->Clear(D2D1::ColorF(0, 0, 0, 0));
        painter.PaintBackground(context.Get(), state, 400, 48, 1);
        NativeMedia::BrowserPaint browserPaint{ &state, 400, 48, 1 };
        painter.PaintBrowser(context.Get(), browserPaint);
        Require(SUCCEEDED(context->EndDraw()), "Draw offscreen browser frame.");
        context->SetTarget(nullptr);
        painter.EndPaintFrame();
        targetBitmap = CopyTextureToWic(wic.Get(), d3d.Get(), d3dContext.Get(),
            renderTexture.Get());
        SavePng(wic.Get(), targetBitmap.Get(), output / L"browser-controls.png");
        Require(HasVisiblePixels(targetBitmap.Get()), "Browser offscreen pixels are visible.");

        // A DirectWrite line box is taller than its font size, so clipping the metadata
        // lines at the font size cut the bottom off every glyph, descenders included.
        const int plainBottom = LowestInkRow(targetBitmap.Get(), 24);
        state.frame.browser.artist = L"Fixture artist gyp";
        painter.BeginPaintFrame();
        context->SetTarget(target.Get());
        context->BeginDraw();
        context->Clear(D2D1::ColorF(0, 0, 0, 0));
        painter.PaintBackground(context.Get(), state, 400, 48, 1);
        painter.PaintBrowser(context.Get(), browserPaint);
        Require(SUCCEEDED(context->EndDraw()), "Draw offscreen browser descender frame.");
        context->SetTarget(nullptr);
        painter.EndPaintFrame();
        targetBitmap = CopyTextureToWic(wic.Get(), d3d.Get(), d3dContext.Get(),
            renderTexture.Get());
        SavePng(wic.Get(), targetBitmap.Get(), output / L"browser-metadata.png");
        const int descenderBottom = LowestInkRow(targetBitmap.Get(), 24);
        Require(plainBottom > 0 && descenderBottom > plainBottom,
            "Browser metadata descenders are not clipped.");

        std::cout << "Native media offscreen rendering assertions passed: "
            << output.string() << "\n";
        CoUninitialize();
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << error.what() << "\n";
        CoUninitialize();
        return 1;
    }
}
