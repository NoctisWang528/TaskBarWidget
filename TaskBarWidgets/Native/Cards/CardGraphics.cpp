#include "CardPch.h"
#include <d2d1effects.h>

namespace
{
    // The Gaussian blur effect CLSID as documented by Direct2D; declared locally so
    // the linker does not need a GUID definition out of d2d1.lib.
    const CLSID GaussianBlurEffect = { 0x1feb6d69, 0x2fe6, 0x4ac9,
        { 0x8c, 0x58, 0x1d, 0x7f, 0x93, 0xe7, 0xa6, 0xa5 } };
}
#include "CardGraphics.h"

#include <cryptuiapi.h>
#include <wincrypt.h>

#pragma comment(lib, "crypt32.lib")
#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dwrite.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "windowscodecs.lib")

using Microsoft::WRL::ComPtr;

namespace Cards
{
    namespace
    {
        template <typename T>
        winrt::com_ptr<T> Abi(const winrt::Windows::Foundation::IUnknown& value)
        {
            winrt::com_ptr<T> result;
            winrt::check_hresult(value.as(winrt::guid_of<T>(), result.put_void()));
            return result;
        }

        std::wstring FormatKey(const std::wstring& family, float size, int weight, int alignment, bool wrap)
        {
            wchar_t buffer[160]{};
            swprintf_s(buffer, L"%s|%.2f|%d|%d|%d", family.c_str(), size, weight, alignment, wrap ? 1 : 0);
            return buffer;
        }

        bool DecodeBase64(const std::wstring& text, std::vector<BYTE>& bytes)
        {
            if (text.empty()) return false;
            // Base64 payloads are ASCII, so a UTF-8 round trip is exact.
            const std::string narrow = WideToUtf8(text);
            DWORD length = 0;
            if (!CryptStringToBinaryA(narrow.c_str(), static_cast<DWORD>(narrow.size()),
                CRYPT_STRING_BASE64, nullptr, &length, nullptr, nullptr) || length == 0)
                return false;
            bytes.resize(length);
            return CryptStringToBinaryA(narrow.c_str(), static_cast<DWORD>(narrow.size()),
                CRYPT_STRING_BASE64, bytes.data(), &length, nullptr, nullptr) != FALSE;
        }

        bool ReadWholeFile(const std::wstring& path, std::vector<BYTE>& bytes)
        {
            const HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr,
                OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (file == INVALID_HANDLE_VALUE) return false;
            LARGE_INTEGER size{};
            bool ok = GetFileSizeEx(file, &size) != FALSE && size.QuadPart > 0 &&
                size.QuadPart < static_cast<LONGLONG>(64 * 1024 * 1024);
            if (ok)
            {
                bytes.resize(static_cast<size_t>(size.QuadPart));
                DWORD read{};
                ok = ReadFile(file, bytes.data(), static_cast<DWORD>(bytes.size()), &read, nullptr) != FALSE &&
                    read == bytes.size();
            }
            CloseHandle(file);
            return ok;
        }
    }

    Graphics::~Graphics() { Release(); }

    void Graphics::Release()
    {
        bitmaps.clear();
        formats.clear();
        geometries.clear();
        context.Reset();
        d2dDevice.Reset();
        factory.Reset();
        write.Reset();
        wic.Reset();
        dxgiDevice.Reset();
        ready = false;
    }

    void Graphics::OnDeviceLost()
    {
        // Device loss invalidates every bitmap built on it; the host rebuilds the
        // stack and repaints from the last snapshot.
        Release();
    }

    bool Graphics::CreateFactories()
    {
        D2D1_FACTORY_OPTIONS options{};
#ifdef _DEBUG
        options.debugLevel = D2D1_DEBUG_LEVEL_NONE;
#endif
        if (FAILED(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED,
            __uuidof(ID2D1Factory1), &options, reinterpret_cast<void**>(factory.GetAddressOf()))))
        {
            diagnostic = L"D2D1CreateFactory failed";
            return false;
        }
        // The card thread owns the factory: DWRITE_FACTORY_TYPE_SHARED still
        // returns a thread-safe object but never serializes the whole process.
        if (FAILED(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory5),
            reinterpret_cast<IUnknown**>(write.GetAddressOf()))))
        {
            diagnostic = L"DWriteCreateFactory failed";
            return false;
        }
        if (FAILED(CoCreateInstance(CLSID_WICImagingFactory2, nullptr, CLSCTX_INPROC_SERVER,
            IID_PPV_ARGS(wic.GetAddressOf()))))
        {
            diagnostic = L"WIC imaging factory unavailable";
            return false;
        }
        return true;
    }

    bool Graphics::Initialize()
    {
        if (ready) return true;
        if (!factory && !CreateFactories()) return false;

        ComPtr<ID3D11Device> device;
        ComPtr<ID3D11DeviceContext> deviceContext;
        const UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
        D3D_FEATURE_LEVEL level{};
        HRESULT result = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags,
            nullptr, 0, D3D11_SDK_VERSION, device.GetAddressOf(), &level, deviceContext.GetAddressOf());
        if (FAILED(result))
        {
            result = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, flags,
                nullptr, 0, D3D11_SDK_VERSION, device.GetAddressOf(), &level, deviceContext.GetAddressOf());
        }
        if (FAILED(result)) { diagnostic = L"D3D11CreateDevice failed"; return false; }
        if (FAILED(device.As(&dxgiDevice))) { diagnostic = L"DXGI device unavailable"; return false; }
        if (FAILED(factory->CreateDevice(dxgiDevice.Get(), d2dDevice.GetAddressOf())))
        {
            diagnostic = L"ID2D1Factory1::CreateDevice failed";
            return false;
        }
        if (FAILED(d2dDevice->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE,
            context.GetAddressOf())))
        {
            diagnostic = L"CreateDeviceContext failed";
            return false;
        }
        context->SetAntialiasMode(D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
        context->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
        ready = true;
        return true;
    }

    winrt::Windows::UI::Composition::CompositionGraphicsDevice Graphics::CreateGraphicsDevice(
        const winrt::Windows::UI::Composition::Compositor& compositor, std::wstring& error)
    {
        error.clear();
        if (!Initialize())
        {
            error = diagnostic.empty() ? L"graphics device unavailable" : diagnostic;
            return nullptr;
        }
        if (!compositor)
        {
            error = L"compositor unavailable";
            return nullptr;
        }
        const auto interop = Abi<ABI::Windows::UI::Composition::ICompositorInterop>(compositor);
        winrt::com_ptr<ABI::Windows::UI::Composition::ICompositionGraphicsDevice> device;
        if (FAILED(interop->CreateGraphicsDevice(dxgiDevice.Get(), device.put())))
        {
            error = L"CreateGraphicsDevice failed";
            return nullptr;
        }
        return device.as<winrt::Windows::UI::Composition::CompositionGraphicsDevice>();
    }

    void DrawingSurface::Release()
    {
        if (drawing && surface)
        {
            if (auto resource = surface.try_as<ABI::Windows::UI::Composition::ICompositionDrawingSurfaceInterop>())
                resource->EndDraw();
        }
        drawing = false;
        context.Reset();
        surface = nullptr;
        width = 0;
        height = 0;
    }

    void DrawingSurface::Ensure(const winrt::Windows::UI::Composition::CompositionGraphicsDevice& device,
        int widthPixels, int heightPixels)
    {
        widthPixels = (std::max)(1, widthPixels);
        heightPixels = (std::max)(1, heightPixels);
        if (!device) return;
        if (!surface)
        {
            surface = device.CreateDrawingSurface(
                winrt::Windows::Foundation::Size(static_cast<float>(widthPixels),
                    static_cast<float>(heightPixels)),
                winrt::Windows::Graphics::DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized,
                winrt::Windows::Graphics::DirectX::DirectXAlphaMode::Premultiplied);
            width = widthPixels;
            height = heightPixels;
            return;
        }
        if (width == widthPixels && height == heightPixels) return;
        try
        {
            const auto resource = Abi<ABI::Windows::UI::Composition::ICompositionDrawingSurfaceInterop>(surface);
            if (SUCCEEDED(resource->Resize(SIZE{ widthPixels, heightPixels })))
            {
                width = widthPixels;
                height = heightPixels;
            }
        }
        catch (...) { Release(); }
    }

    ID2D1DeviceContext* DrawingSurface::Begin(D2D1_POINT_2F& offset)
    {
        offset = D2D1_POINT_2F{ 0, 0 };
        if (!surface || drawing) return nullptr;
        try
        {
            const auto resource = Abi<ABI::Windows::UI::Composition::ICompositionDrawingSurfaceInterop>(surface);
            ComPtr<ID2D1DeviceContext> device;
            POINT updateOffset{};
            if (FAILED(resource->BeginDraw(nullptr, __uuidof(ID2D1DeviceContext),
                reinterpret_cast<void**>(device.GetAddressOf()), &updateOffset)))
                return nullptr;
            context = device;
            drawing = true;
            offset = D2D1_POINT_2F{ static_cast<float>(updateOffset.x), static_cast<float>(updateOffset.y) };
            return context.Get();
        }
        catch (...) { return nullptr; }
    }

    void DrawingSurface::End()
    {
        if (!drawing || !surface) { drawing = false; context.Reset(); return; }
        drawing = false;
        context.Reset();
        try
        {
            const auto resource = Abi<ABI::Windows::UI::Composition::ICompositionDrawingSurfaceInterop>(surface);
            if (FAILED(resource->EndDraw())) Release();
        }
        catch (...) { Release(); }
    }

    ID2D1Bitmap1* Graphics::BitmapFromConverter(IWICFormatConverter* converter)
    {
        if (!context || !converter) return nullptr;
        const auto properties = D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_NONE,
            D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED));
        ComPtr<ID2D1Bitmap1> bitmap;
        if (FAILED(context->CreateBitmapFromWicBitmap(converter, &properties, bitmap.GetAddressOf())))
            return nullptr;
        return bitmap.Detach();
    }

    ID2D1Bitmap1* Graphics::DecodeImage(const std::wstring& source, int decodeWidthPixels,
        int decodeHeightPixels)
    {
        if (source.empty() || !wic || !context) return nullptr;
        const auto cached = bitmaps.find(source);
        if (cached != bitmaps.end()) return cached->second.Get();

        std::vector<BYTE> bytes;
        if (source.rfind(L"data:", 0) == 0)
        {
            const auto comma = source.find(L',');
            if (comma == std::wstring::npos || !DecodeBase64(source.substr(comma + 1), bytes) || bytes.empty())
                return nullptr;
        }
        else if (!ReadWholeFile(source, bytes))
        {
            return nullptr;
        }

        ComPtr<IWICStream> stream;
        ComPtr<IWICBitmapDecoder> decoder;
        ComPtr<IWICBitmapFrameDecode> frame;
        ComPtr<IWICFormatConverter> converter;
        if (FAILED(wic->CreateStream(stream.GetAddressOf()))) return nullptr;
        if (FAILED(stream->InitializeFromMemory(bytes.data(), static_cast<DWORD>(bytes.size())))) return nullptr;
        if (FAILED(wic->CreateDecoderFromStream(stream.Get(), nullptr, WICDecodeMetadataCacheOnDemand,
            decoder.GetAddressOf()))) return nullptr;
        if (FAILED(decoder->GetFrame(0, frame.GetAddressOf()))) return nullptr;
        if (FAILED(wic->CreateFormatConverter(converter.GetAddressOf()))) return nullptr;
        if (FAILED(converter->Initialize(frame.Get(), GUID_WICPixelFormat32bppPBGRA,
            WICBitmapDitherTypeNone, nullptr, 0.0, WICBitmapPaletteTypeCustom)))
            return nullptr;

        UINT sourceWidth{}, sourceHeight{};
        converter->GetSize(&sourceWidth, &sourceHeight);
        ComPtr<IWICBitmapSource> final = converter;
        ComPtr<IWICBitmapScaler> scaler;
        const int limitWidth = (std::max)(1, decodeWidthPixels * 2);
        const int limitHeight = (std::max)(1, decodeHeightPixels * 2);
        if (sourceWidth > 0 && sourceHeight > 0 &&
            (sourceWidth > static_cast<UINT>(limitWidth) || sourceHeight > static_cast<UINT>(limitHeight)) &&
            SUCCEEDED(wic->CreateBitmapScaler(scaler.GetAddressOf())))
        {
            const double scale = (std::min)(static_cast<double>(limitWidth) / sourceWidth,
                static_cast<double>(limitHeight) / sourceHeight);
            const UINT targetWidth = (std::max)(1u, static_cast<UINT>(sourceWidth * scale + 0.5));
            const UINT targetHeight = (std::max)(1u, static_cast<UINT>(sourceHeight * scale + 0.5));
            if (SUCCEEDED(scaler->Initialize(converter.Get(), targetWidth, targetHeight,
                WICBitmapInterpolationModeFant)))
            {
                ComPtr<IWICFormatConverter> scaled;
                if (SUCCEEDED(wic->CreateFormatConverter(scaled.GetAddressOf())) &&
                    SUCCEEDED(scaled->Initialize(scaler.Get(), GUID_WICPixelFormat32bppPBGRA,
                        WICBitmapDitherTypeNone, nullptr, 0.0, WICBitmapPaletteTypeCustom)))
                    final = scaled;
            }
        }

        ComPtr<IWICFormatConverter> finalConverter;
        final.As(&finalConverter);
        if (!finalConverter) return nullptr;
        ComPtr<ID2D1Bitmap1> bitmap;
        bitmap.Attach(BitmapFromConverter(finalConverter.Get()));
        if (!bitmap) return nullptr;
        auto entry = bitmaps.emplace(source, bitmap);
        return entry.first->second.Get();
    }

    ID2D1PathGeometry* Graphics::RoundedRect(float rectWidth, float rectHeight, float radius)
    {
        if (!factory || rectWidth <= 0.0f || rectHeight <= 0.0f) return nullptr;
        wchar_t buffer[64]{};
        swprintf_s(buffer, L"%.2f|%.2f|%.2f", rectWidth, rectHeight, radius);
        const std::wstring key = buffer;
        const auto cached = geometries.find(key);
        if (cached != geometries.end()) return cached->second.Get();

        ComPtr<ID2D1PathGeometry> geometry;
        ComPtr<ID2D1GeometrySink> sink;
        if (FAILED(factory->CreatePathGeometry(geometry.GetAddressOf()))) return nullptr;
        if (FAILED(geometry->Open(sink.GetAddressOf()))) return nullptr;
        const float r = (std::min)(radius, (std::min)(rectWidth, rectHeight) / 2.0f);
        const float right = rectWidth;
        const float bottom = rectHeight;
        sink->BeginFigure(D2D1::Point2F(0.0f, r), D2D1_FIGURE_BEGIN_FILLED);
        sink->AddArc(D2D1::ArcSegment(D2D1::Point2F(r, 0.0f), D2D1::SizeF(r, r), 0.0f,
            D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
        sink->AddLine(D2D1::Point2F(right - r, 0.0f));
        sink->AddArc(D2D1::ArcSegment(D2D1::Point2F(right, r), D2D1::SizeF(r, r), 0.0f,
            D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
        sink->AddLine(D2D1::Point2F(right, bottom - r));
        sink->AddArc(D2D1::ArcSegment(D2D1::Point2F(right - r, bottom), D2D1::SizeF(r, r), 0.0f,
            D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
        sink->AddLine(D2D1::Point2F(r, bottom));
        sink->AddArc(D2D1::ArcSegment(D2D1::Point2F(0.0f, bottom - r), D2D1::SizeF(r, r), 0.0f,
            D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
        sink->AddLine(D2D1::Point2F(0.0f, r));
        sink->EndFigure(D2D1_FIGURE_END_CLOSED);
        if (FAILED(sink->Close())) return nullptr;
        sink.Reset();
        const auto entry = geometries.emplace(key, geometry);
        return entry.first->second.Get();
    }

    IDWriteTextFormat* Graphics::TextFormat(const std::wstring& family, float sizeDip, int weight,
        DWRITE_TEXT_ALIGNMENT alignment, bool wrap)
    {
        if (!write) return nullptr;
        const std::wstring key = FormatKey(family, sizeDip, weight, static_cast<int>(alignment), wrap);
        const auto cached = formats.find(key);
        if (cached != formats.end()) return cached->second.Get();
        ComPtr<IDWriteTextFormat> format;
        if (FAILED(write->CreateTextFormat(family.c_str(), nullptr,
            static_cast<DWRITE_FONT_WEIGHT>(weight), DWRITE_FONT_STYLE_NORMAL,
            DWRITE_FONT_STRETCH_NORMAL, sizeDip, L"zh-CN", format.GetAddressOf())))
            return nullptr;
        format->SetTextAlignment(alignment);
        format->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_NEAR);
        format->SetWordWrapping(wrap ? DWRITE_WORD_WRAPPING_WRAP : DWRITE_WORD_WRAPPING_NO_WRAP);
        const auto entry = formats.emplace(key, format);
        return entry.first->second.Get();
    }

    // The glow is the plan's "one transparent mask + central blur": every glowing
    // cell writes into an offscreen bitmap, the bitmap is blurred once, and the
    // result is composited in a single pass.
    bool Graphics::BeginMask(ID2D1DeviceContext* target)
    {
        if (!target || !context) return false;
        D2D1_SIZE_F size = target->GetSize();
        float dpiX = 96.0f, dpiY = 96.0f;
        target->GetDpi(&dpiX, &dpiY);
        const UINT width = (std::max)(1u, static_cast<UINT>(std::lround(size.width)));
        const UINT height = (std::max)(1u, static_cast<UINT>(std::lround(size.height)));
        if (!mask || mask->GetPixelSize().width != width || mask->GetPixelSize().height != height)
        {
            mask.Reset();
            const D2D1_BITMAP_PROPERTIES1 properties = D2D1::BitmapProperties1(
                D2D1_BITMAP_OPTIONS_TARGET,
                D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED),
                dpiX, dpiY);
            if (FAILED(target->CreateBitmap(D2D1::SizeU(width, height), nullptr, 0, properties,
                mask.GetAddressOf()))) return false;
        }
        maskTarget = target;
        target->SetTarget(mask.Get());
        target->SetTransform(D2D1::Matrix3x2F::Identity());
        target->Clear(D2D1::ColorF(0, 0.0f));
        return true;
    }

    void Graphics::EndMask(ID2D1DeviceContext* target, float blurDip)
    {
        if (!target || !mask) return;
        target->SetTarget(nullptr);
        float dpiX = 96.0f, dpiY = 96.0f;
        target->GetDpi(&dpiX, &dpiY);
        if (!maskBlur && FAILED(target->CreateEffect(GaussianBlurEffect, maskBlur.GetAddressOf())))
        {
            maskBlur.Reset();
            return;
        }
        const float scale = dpiX / 96.0f;
        maskBlur->SetInput(0, mask.Get());
        maskBlur->SetValue(D2D1_GAUSSIANBLUR_PROP_STANDARD_DEVIATION, (std::max)(0.1f, blurDip * scale * 0.6f));
        maskBlur->SetValue(D2D1_GAUSSIANBLUR_PROP_BORDER_MODE, D2D1_BORDER_MODE_SOFT);
        target->SetTransform(D2D1::Matrix3x2F::Identity());
        target->DrawImage(maskBlur.Get(), D2D1_INTERPOLATION_MODE_LINEAR);
    }

    void Graphics::ReleaseMask()
    {
        mask.Reset();
        maskTarget = nullptr;
        maskBlur.Reset();
    }

    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> Graphics::Brush(ID2D1DeviceContext* target, Color color)
    {
        Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> brush;
        if (!target) return brush;
        const D2D1_COLOR_F value = D2D1::ColorF(color.r, color.g, color.b, color.a);
        target->CreateSolidColorBrush(value, brush.GetAddressOf());
        return brush;
    }
}
