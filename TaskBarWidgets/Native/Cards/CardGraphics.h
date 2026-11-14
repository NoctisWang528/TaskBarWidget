#pragma once
// Shared graphics device for the native cards.
//
// One D3D11/D2D device, one DirectWrite factory and one WIC factory belong to the
// card STA thread. Each card owns only its own visuals and surfaces;
// bitmaps and text formats come from this shared stack so four cards never build
// four device stacks.

#include "CardText.h"

#include <Windows.h>
#include <d2d1_3.h>
#include <d3d11_1.h>
#include <dwrite_3.h>
#include <wincodec.h>
#include <wrl/client.h>

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.UI.Composition.h>

#include <map>
#include <string>
#include <tuple>
#include <vector>

namespace Cards
{
    enum class Symbol;
    using SurfaceHandle = winrt::Windows::UI::Composition::CompositionDrawingSurface;

    // A drawing surface plus the BeginDraw/EndDraw pairing rules. The D2D context
    // returned by Begin() belongs to that one frame and is never cached.
    class DrawingSurface
    {
    public:
        void Ensure(const winrt::Windows::UI::Composition::CompositionGraphicsDevice& device,
            int widthPixels, int heightPixels);
        ID2D1DeviceContext* Begin(D2D1_POINT_2F& offset);
        void End();
        void Release();
        bool Ready() const { return surface != nullptr; }
        HRESULT LastResult() const { return lastResult; }
        int Width() const { return width; }
        int Height() const { return height; }
        SurfaceHandle Handle() const { return surface; }

    private:
        SurfaceHandle surface{ nullptr };
        Microsoft::WRL::ComPtr<ID2D1DeviceContext> context;
        int width{ 0 };
        int height{ 0 };
        bool drawing{ false };
        HRESULT lastResult{ S_OK };
    };

    class Graphics
    {
    public:
        Graphics() = default;
        ~Graphics();

        // Creates the shared device stack. Safe to call repeatedly; a failed
        // attempt is retried on the next call.
        bool Initialize();
        void Release();
        // Drops every device-bound bitmap so the caller can rebuild and repaint.
        void OnDeviceLost();

        bool Ready() const { return ready; }
        const std::wstring& Diagnostic() const { return diagnostic; }

        ID2D1DeviceContext* ResourceContext() const { return context.Get(); }
        IDXGIDevice* DxgiDevice() const { return dxgiDevice.Get(); }
        IDWriteFactory5* DWrite() const { return write.Get(); }
        ID2D1Factory1* D2DFactory() const { return factory.Get(); }
        IWICImagingFactory2* Wic() const { return wic.Get(); }

        // A graphics device is created per compositor, but all of them share the
        // one D3D device above.
        winrt::Windows::UI::Composition::CompositionGraphicsDevice CreateGraphicsDevice(
            const winrt::Windows::UI::Composition::Compositor& compositor, std::wstring& error);

        // Decodes a data: URL or a local file, downscaling to at most twice the
        // requested pixel size so a DPI change re-decodes crisply.
        ID2D1Bitmap1* DecodeImage(const std::wstring& source, int decodeWidthPixels, int decodeHeightPixels);
        void ForgetImage(const std::wstring& source);

        IDWriteTextFormat* TextFormat(const std::wstring& family, float sizeDip, int weight,
            DWRITE_TEXT_ALIGNMENT alignment, bool wrap);

        // Immutable lyric layouts, bounded to 64 entries. Paint-only colors and scan
        // positions never enter the key; semantic text, style, geometry and DPI do.
        Microsoft::WRL::ComPtr<IDWriteTextLayout> TextLayout(const std::wstring& text,
            const std::wstring& family, float sizeDip, int weight, float widthDip, float heightDip,
            bool italic, bool underline, bool strikeout, bool ellipsis, float dpiScale,
            DWRITE_TEXT_ALIGNMENT alignment = DWRITE_TEXT_ALIGNMENT_LEADING,
            DWRITE_PARAGRAPH_ALIGNMENT vertical = DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
        size_t TextLayoutCacheSize() const { return layouts.size(); }
        uint64_t TextLayoutCreateCount() const { return textLayoutCreateCount; }
        // Rasterize only small vector symbols locally so Composition atlas moves
        // cannot change curve tessellation. Device loss clears the bounded cache.
        void WeatherIcon(ID2D1DeviceContext* target, Symbol symbol, bool night, bool dark,
            float left, float top, float size);

        // A tinted mask rendered offscreen and blurred once, so a glow is one
        // composite pass instead of one shadow object per element. The mask uses
        // the caller's DIP coordinates and its own target-sized bitmap.
        ID2D1DeviceContext* BeginMask(ID2D1DeviceContext* target);
        void EndMask(ID2D1DeviceContext* target, float blurDip);
        void ReleaseMask();

        // Cached solid brushes are not used: colours carry the theme, so painters
        // create brushes per layer with the current palette.
        Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> Brush(ID2D1DeviceContext* target, Color color);

    private:
        bool CreateFactories();
        ID2D1Bitmap1* BitmapFromConverter(IWICFormatConverter* converter);

        Microsoft::WRL::ComPtr<IDXGIDevice> dxgiDevice;
        Microsoft::WRL::ComPtr<ID2D1Factory1> factory;
        Microsoft::WRL::ComPtr<ID2D1Device> d2dDevice;
        Microsoft::WRL::ComPtr<ID2D1DeviceContext> context;
        Microsoft::WRL::ComPtr<IDWriteFactory5> write;
        Microsoft::WRL::ComPtr<IWICImagingFactory2> wic;
        std::map<std::wstring, Microsoft::WRL::ComPtr<ID2D1Bitmap1>> bitmaps;
        std::map<std::wstring, Microsoft::WRL::ComPtr<IDWriteTextFormat>> formats;
        using LayoutKey = std::tuple<std::wstring, std::wstring, float, int, float, float,
            bool, bool, bool, bool, float, DWRITE_TEXT_ALIGNMENT, DWRITE_PARAGRAPH_ALIGNMENT>;
        std::vector<std::pair<LayoutKey, Microsoft::WRL::ComPtr<IDWriteTextLayout>>> layouts;
        uint64_t textLayoutCreateCount{};
        using WeatherIconKey = std::tuple<Symbol, bool, bool, float, float, int, int>;
        std::vector<std::pair<WeatherIconKey, Microsoft::WRL::ComPtr<ID2D1Bitmap1>>> weatherIcons;
        Microsoft::WRL::ComPtr<ID2D1DeviceContext> iconContext;
        Microsoft::WRL::ComPtr<ID2D1Bitmap1> mask;
        Microsoft::WRL::ComPtr<ID2D1DeviceContext> maskContext;
        D2D1_MATRIX_3X2_F maskTransform{};
        Microsoft::WRL::ComPtr<ID2D1Effect> maskBlur;
        std::wstring diagnostic;
        bool ready{ false };
    };

    // RAII pairing so a painter can never leak an open BeginDraw.
    struct DrawScope
    {
        explicit DrawScope(DrawingSurface& target, float scale = 1.0f) : surface(target)
        {
            context = surface.Begin(offset);
            if (context) Configure(context, scale, offset);
        }
        // Layout stays in DIPs; Composition's atlas offset is always physical
        // pixels. Apply it after scaling, without dividing and rounding it.
        static void Configure(ID2D1DeviceContext* context, float scale, D2D1_POINT_2F offset)
        {
            context->SetDpi(96.0f, 96.0f);
            context->SetUnitMode(D2D1_UNIT_MODE_DIPS);
            context->SetTransform(D2D1::Matrix3x2F::Scale(scale, scale) *
                D2D1::Matrix3x2F::Translation(offset.x, offset.y));
        }
        ~DrawScope() { if (context) surface.End(); }
        DrawScope(const DrawScope&) = delete;
        DrawScope& operator=(const DrawScope&) = delete;
        explicit operator bool() const { return context != nullptr; }
        ID2D1DeviceContext* operator->() const { return context; }
        ID2D1DeviceContext* Get() const { return context; }

        DrawingSurface& surface;
        D2D1_POINT_2F offset{};
        ID2D1DeviceContext* context{ nullptr };
    };
}
