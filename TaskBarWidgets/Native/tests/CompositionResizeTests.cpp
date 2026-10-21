// Regression for the taskbar swap-chain resize failure (DXGI_ERROR_INVALID_CALL, 0x887A0001).
// Mirrors the production order of:
//   CD2D1DeviceContextHelper::Reinitialize()            - Reset() before replacing m_p_device_context
//   CD2D1DeviceContextHelper::Reset()                   - SetTarget(nullptr) on the context it still owns
//   CD2D1DeviceContextHelper::RebindRenderTargetSurface()
//   CDCompositionHelper::Resize()                       - ResizeBuffers, rebuilt chain when it cannot resize
// The global bitmap/icon caches keep ID2D1RenderTarget references to the old context, so that context
// outlives the helper reset and must not keep the swap-chain buffer as its target.
#include <Windows.h>
#include <d3d10_1.h>
#include <dxgi1_2.h>
#include <d2d1_1.h>
#include <wrl/client.h>
#include <iostream>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>
#include "../TrafficMonitor/CompositionFade.h"
#pragma comment(lib,"d3d10_1.lib")
#pragma comment(lib,"dxgi.lib")
#pragma comment(lib,"d2d1.lib")
#pragma comment(lib,"dcomp.lib")
#pragma comment(lib,"user32.lib")
using Microsoft::WRL::ComPtr;
namespace
{
struct TestFailure
{
    std::string message;
    HRESULT result;
};

[[noreturn]] void Fail(std::string message, const HRESULT result = S_OK)
{
    throw TestFailure{std::move(message), result};
}

void Check(const HRESULT result, const char* p_message)
{
    if (FAILED(result))
    {
        Fail(p_message, result);
    }
}

void Require(const bool condition, const char* p_message)
{
    if (!condition)
    {
        Fail(p_message);
    }
}

ComPtr<ID2D1Image> GetTarget(ID2D1DeviceContext* p_context)
{
    ComPtr<ID2D1Image> result{};
    if (p_context)
    {
        p_context->GetTarget(&result);
    }
    return result;
}

constexpr UINT kInitialWidth = 315;
constexpr UINT kInitialHeight = 32;

// DPI 120 -> 144 scales the taskbar draw size by 1.2; the remaining steps keep resizing afterwards.
const std::vector<D2D1_SIZE_U> kResizeSteps{{378, 38}, {420, 32}, {378, 38}, {315, 32}};

struct GraphicsDevice
{
    ComPtr<ID3D10Device1> graphics;
    ComPtr<IDXGIDevice> dxgi_device;
    ComPtr<ID2D1Device> d2d;
};

GraphicsDevice CreateGraphicsDevice(ID2D1Factory1* p_factory)
{
    GraphicsDevice result{};
    Check(D3D10CreateDevice1(nullptr, D3D10_DRIVER_TYPE_WARP, nullptr, D3D10_CREATE_DEVICE_BGRA_SUPPORT,
                             D3D10_FEATURE_LEVEL_10_1, D3D10_1_SDK_VERSION, &result.graphics),
          "Create WARP D3D10.1 device failed.");
    Check(result.graphics.As(&result.dxgi_device), "Get IDXGIDevice from ID3D10Device1 failed.");
    Check(p_factory->CreateDevice(result.dxgi_device.Get(), &result.d2d), "Create ID2D1Device failed.");
    return result;
}

// Same as CD2D1Device::Recreate(): a new ID2D1Device on the unchanged IDXGIDevice, so the existing
// swap chain and its buffers survive while only the D2D device is replaced.
ComPtr<ID2D1Device> RecreateD2D1Device(ID2D1Factory1* p_factory, IDXGIDevice* p_dxgi_device)
{
    ComPtr<ID2D1Device> result{};
    Check(p_factory->CreateDevice(p_dxgi_device, &result), "Create ID2D1Device failed.");
    return result;
}

// Same as CDCompositionHelper::InitializeSwapChain().
ComPtr<IDXGISwapChain1> CreateCompositionSwapChain(IDXGIFactory2* p_factory, ID3D10Device1* p_graphics,
                                                   const D2D1_SIZE_U size)
{
    DXGI_SWAP_CHAIN_DESC1 desc{};
    desc.Width = size.width;
    desc.Height = size.height;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    desc.BufferCount = 2;
    desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
    desc.AlphaMode = DXGI_ALPHA_MODE_PREMULTIPLIED;
    ComPtr<IDXGISwapChain1> result{};
    Check(p_factory->CreateSwapChainForComposition(p_graphics, &desc, nullptr, &result),
          "Create swap chain for composition failed.");
    return result;
}

// CD2D1BitmapCache and CD2D1IconCache keep a strong ID2D1RenderTarget reference to the context.
struct RenderTargetCache
{
    ComPtr<ID2D1RenderTarget> render_target;

    void RebindRenderTarget(ID2D1RenderTarget* p_render_target)
    {
        render_target = p_render_target;
    }
};

// Member layout and lifetime of CD2D1DeviceContextHelper.
struct DeviceContextHelper
{
    ComPtr<ID2D1Device> device;
    ComPtr<ID2D1DeviceContext> device_context;
    ComPtr<ID2D1Bitmap1> target_bitmap;
    ComPtr<ID2D1SolidColorBrush> foreground_brush;
    RenderTargetCache* p_bitmap_cache{nullptr};

    void Reset()
    {
        // The caches can still own this context: detach its swap-chain target explicitly.
        if (device_context)
        {
            device_context->SetTarget(nullptr);
        }
        device_context.Reset();
        target_bitmap.Reset();
        foreground_brush.Reset();
    }

    // detach_before_replace = false reproduces the pre-fix Reinitialize() that overwrote the context
    // while the old one still targeted the swap-chain buffer.
    void Reinitialize(const bool detach_before_replace)
    {
        if (detach_before_replace)
        {
            Reset();
        }
        Check(device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &device_context),
              "Create ID2D1DeviceContext failed.");
        Check(device_context->CreateSolidColorBrush(D2D1::ColorF(D2D1::ColorF::White), &foreground_brush),
              "Create ID2D1SolidColorBrush failed.");
        if (p_bitmap_cache)
        {
            p_bitmap_cache->RebindRenderTarget(device_context.Get());
        }
    }
};

// Same as CD2D1DeviceContextHelper::RebindRenderTargetSurface().
void RebindRenderTargetSurface(DeviceContextHelper& helper, IDXGISwapChain1* p_swap_chain)
{
    if (!helper.device_context)
    {
        helper.Reinitialize(true);
    }
    ComPtr<IDXGISurface> surface{};
    Check(p_swap_chain->GetBuffer(0, IID_PPV_ARGS(&surface)), "Get buffer from composition swap chain failed.");
    const auto properties = D2D1::BitmapProperties1(
        D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
        D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED),
        96.f,
        96.f);
    helper.target_bitmap.Reset();
    Check(helper.device_context->CreateBitmapFromDxgiSurface(surface.Get(), &properties, &helper.target_bitmap),
          "Create ID2D1Bitmap1 from the swap chain buffer failed.");
    helper.device_context->SetTarget(helper.target_bitmap.Get());
}

// One frame of CD2D1DeviceContextWindowSupport::Resize() plus the draw that CTaskBarDlgDrawCommon::Create()
// performs afterwards. The caches still reference the previous frame context here, exactly as in
// production, and are only rebound once the swap chain has been resized.
void ResizeFrame(DeviceContextHelper& helper, IDXGISwapChain1* p_swap_chain, const D2D1_SIZE_U size,
                 RenderTargetCache& icon_cache)
{
    helper.Reset();
    const HRESULT resize_result = p_swap_chain->ResizeBuffers(0, size.width, size.height, DXGI_FORMAT_UNKNOWN, 0);
    Require(resize_result != DXGI_ERROR_INVALID_CALL, "ResizeBuffers returned DXGI_ERROR_INVALID_CALL.");
    Check(resize_result, "ResizeBuffers failed.");
    RebindRenderTargetSurface(helper, p_swap_chain);
    helper.device_context->BeginDraw();
    helper.device_context->Clear(D2D1::ColorF(D2D1::ColorF::Black, 0.f));
    Check(helper.device_context->EndDraw(), "EndDraw failed after the resize.");
    DXGI_SWAP_CHAIN_DESC1 desc{};
    Check(p_swap_chain->GetDesc1(&desc), "Get swap chain description failed.");
    Require(desc.Width == size.width && desc.Height == size.height,
            "The swap chain must end up with the requested size.");
    icon_cache.RebindRenderTarget(helper.device_context.Get());
}

void CheckRepeatedResizes(DeviceContextHelper& helper, ComPtr<IDXGISwapChain1>& swap_chain,
                          RenderTargetCache& icon_cache)
{
    for (const auto size : kResizeSteps)
    {
        ResizeFrame(helper, swap_chain.Get(), size, icon_cache);
    }
}

// Fixed order: the caches keep the old context, the D2D device is recreated, resizes must still succeed.
void VerifyRecreationWithCachedContext(ID2D1Factory1* p_factory, IDXGIFactory2* p_dxgi_factory)
{
    GraphicsDevice device = CreateGraphicsDevice(p_factory);
    ComPtr<IDXGISwapChain1> swap_chain = CreateCompositionSwapChain(
        p_dxgi_factory, device.graphics.Get(), D2D1_SIZE_U{kInitialWidth, kInitialHeight});

    RenderTargetCache bitmap_cache{};
    RenderTargetCache icon_cache{};
    DeviceContextHelper helper{};
    helper.device = device.d2d;
    helper.p_bitmap_cache = &bitmap_cache;

    helper.Reinitialize(true);
    RebindRenderTargetSurface(helper, swap_chain.Get());
    icon_cache.RebindRenderTarget(helper.device_context.Get());

    const ComPtr<ID2D1DeviceContext> old_context = helper.device_context;
    Require(GetTarget(old_context.Get()).Get() == static_cast<ID2D1Image*>(helper.target_bitmap.Get()),
            "The old context target bitmap must point at the swap-chain buffer.");

    // D2D device recreation keeps the swap chain, like RecreateD2D1Device() in production.
    helper.device = RecreateD2D1Device(p_factory, device.dxgi_device.Get());
    helper.Reinitialize(true);

    Require(GetTarget(old_context.Get()).Get() == nullptr,
            "Reinitialize() must detach the old context target before replacing the device context.");
    Require(icon_cache.render_target.Get() == static_cast<ID2D1RenderTarget*>(old_context.Get()),
            "The icon cache must still hold the old context when the first resize happens.");

    CheckRepeatedResizes(helper, swap_chain, icon_cache);
    Require(GetTarget(old_context.Get()).Get() == nullptr,
            "A cached old context must stay detached after the resizes.");
    std::cout << "  device recreation with a cached old context: " << kResizeSteps.size()
              << " consecutive resizes kept the swap chain resizable.\n";
}

// Negative control: without the detach the reported failure must still reproduce.
void VerifyPreFixOrderReproducesInvalidCall(ID2D1Factory1* p_factory, IDXGIFactory2* p_dxgi_factory)
{
    GraphicsDevice device = CreateGraphicsDevice(p_factory);
    ComPtr<IDXGISwapChain1> swap_chain = CreateCompositionSwapChain(
        p_dxgi_factory, device.graphics.Get(), D2D1_SIZE_U{kInitialWidth, kInitialHeight});

    RenderTargetCache bitmap_cache{};
    RenderTargetCache icon_cache{};
    DeviceContextHelper helper{};
    helper.device = device.d2d;
    helper.p_bitmap_cache = &bitmap_cache;

    helper.Reinitialize(true);
    RebindRenderTargetSurface(helper, swap_chain.Get());
    icon_cache.RebindRenderTarget(helper.device_context.Get());
    const ComPtr<ID2D1DeviceContext> old_context = helper.device_context;

    helper.device = RecreateD2D1Device(p_factory, device.dxgi_device.Get());
    helper.Reinitialize(false); // Pre-fix order: the old context keeps its swap-chain target.
    helper.Reset();             // The Reset() inside Resize() only reaches the new context.

    const auto size = kResizeSteps.front();
    const HRESULT resize_result = swap_chain->ResizeBuffers(0, size.width, size.height, DXGI_FORMAT_UNKNOWN, 0);
    if (resize_result != DXGI_ERROR_INVALID_CALL)
    {
        Fail("The pre-fix order must reproduce DXGI_ERROR_INVALID_CALL.", resize_result);
    }

    // Detaching the target that the caches keep alive is what makes the chain resizable again.
    old_context->SetTarget(nullptr);
    Check(swap_chain->ResizeBuffers(0, size.width, size.height, DXGI_FORMAT_UNKNOWN, 0),
          "Releasing the cached old context target must make the swap chain resizable.");
    std::cout << "  pre-fix order reproduced DXGI_ERROR_INVALID_CALL and the detach recovered it.\n";
}

// Recovery path used by CDCompositionHelper::Resize(): a chain that cannot be resized is rebuilt.
void VerifySwapChainRebuildAfterInvalidCall(ID2D1Factory1* p_factory, IDXGIFactory2* p_dxgi_factory)
{
    GraphicsDevice device = CreateGraphicsDevice(p_factory);
    ComPtr<IDXGISwapChain1> swap_chain = CreateCompositionSwapChain(
        p_dxgi_factory, device.graphics.Get(), D2D1_SIZE_U{kInitialWidth, kInitialHeight});

    RenderTargetCache bitmap_cache{};
    RenderTargetCache icon_cache{};
    DeviceContextHelper helper{};
    helper.device = device.d2d;
    helper.p_bitmap_cache = &bitmap_cache;

    helper.Reinitialize(true);
    RebindRenderTargetSurface(helper, swap_chain.Get());
    icon_cache.RebindRenderTarget(helper.device_context.Get());
    const ComPtr<ID2D1DeviceContext> old_context = helper.device_context;

    helper.device = RecreateD2D1Device(p_factory, device.dxgi_device.Get());
    helper.Reinitialize(false);
    helper.Reset();

    const auto size = kResizeSteps.front();
    if (swap_chain->ResizeBuffers(0, size.width, size.height, DXGI_FORMAT_UNKNOWN, 0) != DXGI_ERROR_INVALID_CALL)
    {
        Fail("The leaked buffer reference must block ResizeBuffers before the rebuild is tested.");
    }

    swap_chain.Reset();
    swap_chain = CreateCompositionSwapChain(p_dxgi_factory, device.graphics.Get(), size);
    Require(GetTarget(old_context.Get()).Get() != nullptr, "The cached old context still pins the retired buffer.");

    CheckRepeatedResizes(helper, swap_chain, icon_cache);
    std::cout << "  rebuilt swap chain resized " << kResizeSteps.size()
              << " times while the old context kept the retired buffer.\n";
}

void VerifyCompositionFade()
{
    ComPtr<IDCompositionDevice> composition{};
    Check(DCompositionCreateDevice(nullptr, IID_PPV_ARGS(&composition)), "Create IDCompositionDevice failed.");
    ComPtr<IDCompositionVisual> visual{};
    Check(composition->CreateVisual(&visual), "Create IDCompositionVisual failed.");
    CompositionFade fade;
    Check(fade.Set(composition.Get(), visual.Get(), false, false), "CompositionFade::Set (hidden) failed.");
    Check(fade.Set(composition.Get(), visual.Get(), true), "CompositionFade::Set (shown) failed.");
    Check(fade.Set(composition.Get(), visual.Get(), false), "CompositionFade::Set (hidden again) failed.");
    Check(composition->Commit(), "IDCompositionDevice::Commit failed.");
}
} // namespace

int main()
{
    try
    {
        ComPtr<ID2D1Factory1> p_factory{};
        Check(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, IID_PPV_ARGS(&p_factory)),
              "Create ID2D1Factory1 failed.");
        ComPtr<IDXGIFactory2> p_dxgi_factory{};
        Check(CreateDXGIFactory1(IID_PPV_ARGS(&p_dxgi_factory)), "Create IDXGIFactory2 failed.");

        VerifyRecreationWithCachedContext(p_factory.Get(), p_dxgi_factory.Get());
        VerifyPreFixOrderReproducesInvalidCall(p_factory.Get(), p_dxgi_factory.Get());
        VerifySwapChainRebuildAfterInvalidCall(p_factory.Get(), p_dxgi_factory.Get());
        VerifyCompositionFade();

        std::cout << "Cached-context device recreation, repeated resizes, chain rebuild and composition fades"
                     " passed (WARP, no window).\n";
        return 0;
    }
    catch (const TestFailure& failure)
    {
        std::cerr << "Composition resize regression: " << failure.message;
        if (FAILED(failure.result))
        {
            std::cerr << " HRESULT=0x" << std::hex << static_cast<unsigned long>(failure.result);
        }
        std::cerr << "\n";
        return 1;
    }
    catch (const std::exception& ex)
    {
        std::cerr << "Composition resize regression: " << ex.what() << "\n";
        return 1;
    }
}

