#include "CardPch.h"
#include "CardBackdropCapture.h"

#include <limits>
#include <d3d11_4.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>

namespace Cards
{
    using Microsoft::WRL::ComPtr;
    namespace capture = winrt::Windows::Graphics::Capture;

    bool CaptureExclusion::Enable(HWND value)
    {
        if (value && window == value) return true;
        Restore();
        if (!value) { SetLastError(ERROR_INVALID_WINDOW_HANDLE); return false; }
        // Only our newly created card/menu HWNDs use this owner, and none has
        // another affinity writer. Their initial value is WDA_NONE. The getter
        // documents a layered-window restriction which our Composition HWNDs
        // do not meet; it must not gate the separately supported setter.
        DWORD affinity{WDA_NONE};
        if (!GetWindowDisplayAffinity(value, &affinity)) affinity = WDA_NONE;
        if (affinity != WDA_EXCLUDEFROMCAPTURE && !SetWindowDisplayAffinity(value, WDA_EXCLUDEFROMCAPTURE)) return false;
        window = value; previous = affinity;
        return true;
    }

    void CaptureExclusion::Restore()
    {
        if (window && IsWindow(window)) SetWindowDisplayAffinity(window, previous);
        window = nullptr; previous = WDA_NONE;
    }

    namespace
    {
        // Permit an 8K monitor, but bound allocations and pitch arithmetic even
        // if a driver reports an unusable mode. Each pixel occupies four bytes.
        constexpr uint64_t MaxPixels = 40000000;

        bool ValidSize(UINT width, UINT height)
        {
            return width && height &&
                width <= D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION &&
                height <= D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION &&
                uint64_t(width) * height <= MaxPixels;
        }

        D2D1_BITMAP_PROPERTIES1 BitmapProperties()
        {
            // The duplicated desktop is opaque; its alpha byte is not a mask.
            return D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_NONE,
                D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_IGNORE), 96, 96);
        }

        struct AcquiredFrame
        {
            ComPtr<IDXGIOutputDuplication> duplication;

            explicit AcquiredFrame(IDXGIOutputDuplication* value) : duplication(value) {}
            AcquiredFrame(const AcquiredFrame&) = delete;
            AcquiredFrame& operator=(const AcquiredFrame&) = delete;
            ~AcquiredFrame() { Release(); }

            HRESULT Release()
            {
                if (!duplication) return S_OK;
                const HRESULT result = duplication->ReleaseFrame();
                duplication.Reset();
                return result;
            }
        };

        struct MappedTexture
        {
            ID3D11DeviceContext* context;
            ID3D11Texture2D* texture;
            MappedTexture(ID3D11DeviceContext* value, ID3D11Texture2D* resource)
                : context(value), texture(resource) {}
            MappedTexture(const MappedTexture&) = delete;
            MappedTexture& operator=(const MappedTexture&) = delete;
            ~MappedTexture() { context->Unmap(texture, 0); }
        };

        HRESULT FindOutput(IDXGIFactory1* factory, HMONITOR monitor,
            ComPtr<IDXGIAdapter1>& adapter, ComPtr<IDXGIOutput1>& output, DXGI_OUTPUT_DESC& desc)
        {
            for (UINT adapterIndex = 0; ; ++adapterIndex)
            {
                ComPtr<IDXGIAdapter1> candidate;
                HRESULT result = factory->EnumAdapters1(adapterIndex, candidate.GetAddressOf());
                if (result == DXGI_ERROR_NOT_FOUND) return result;
                if (FAILED(result)) return result;
                for (UINT outputIndex = 0; ; ++outputIndex)
                {
                    ComPtr<IDXGIOutput> candidateOutput;
                    result = candidate->EnumOutputs(outputIndex, candidateOutput.GetAddressOf());
                    if (result == DXGI_ERROR_NOT_FOUND) break;
                    if (FAILED(result)) return result;
                    DXGI_OUTPUT_DESC candidateDesc{};
                    result = candidateOutput->GetDesc(&candidateDesc);
                    if (FAILED(result)) return result;
                    if (candidateDesc.AttachedToDesktop && candidateDesc.Monitor == monitor)
                    {
                        result = candidateOutput.As(&output);
                        if (FAILED(result)) return result;
                        adapter = candidate;
                        desc = candidateDesc;
                        return S_OK;
                    }
                }
            }
        }
    }

    struct DesktopBackdrop::Impl
    {
        struct StagingSlot
        {
            ComPtr<ID3D11Texture2D> texture;
            bool pending{};
        };

        ComPtr<ID3D11Device> renderDevice;
        ComPtr<ID2D1DeviceContext> renderContext;
        ComPtr<ID3D11Device> captureDevice;
        ComPtr<ID3D11DeviceContext> captureContext;
        ComPtr<IDXGIOutputDuplication> duplication;
        // Cross-adapter device creation can block for hundreds of milliseconds.
        // Keep only the device (never captured content) across Stop/Start.
        ComPtr<ID3D11Device> cachedOutputDevice;
        ComPtr<ID3D11DeviceContext> cachedOutputContext;
        LUID cachedOutputLuid{};
        capture::GraphicsCaptureItem captureItem{nullptr};
        capture::Direct3D11CaptureFramePool framePool{nullptr};
        capture::GraphicsCaptureSession captureSession{nullptr};
        ULONGLONG captureStarted{};
        const wchar_t* stage{L"capture/inactive"};
        std::wstring startupDiagnostic;
        ComPtr<ID3D11Texture2D> ownedTexture;
        ComPtr<ID2D1Bitmap1> bitmap;
        StagingSlot staging[2];
        UINT readSlot{};
        UINT writeSlot{};
        UINT width{}, height{};
        HMONITOR monitor{};
        RECT bounds{};
        DXGI_MODE_ROTATION rotation{ DXGI_MODE_ROTATION_IDENTITY };
        BackdropFrame frame;
        uint64_t serial{};
        HRESULT lastResult{ S_OK };
        bool crossAdapter{};
        bool receivedImage{};

        void Clear()
        {
            // No FrameArrived callbacks touch this object. Stop the producer
            // before releasing the pool, image copies or the shared render device.
            try { if (captureSession) captureSession.Close(); } catch (...) {}
            captureSession = nullptr;
            try { if (framePool) framePool.Close(); } catch (...) {}
            framePool = nullptr; captureItem = nullptr; captureStarted = 0;
            frame = {};
            bitmap.Reset();
            ownedTexture.Reset();
            for (auto& slot : staging) slot = {};
            duplication.Reset();
            captureContext.Reset();
            captureDevice.Reset();
            renderContext.Reset();
            renderDevice.Reset();
            readSlot = writeSlot = width = height = 0;
            monitor = nullptr;
            bounds = {};
            rotation = DXGI_MODE_ROTATION_IDENTITY;
            crossAdapter = receivedImage = false;
            // Keep the lifetime serial counter and diagnostic HRESULT.
        }

        void ReleaseCachedDevice()
        {
            cachedOutputContext.Reset();
            cachedOutputDevice.Reset();
            cachedOutputLuid = {};
        }

        bool Fail(HRESULT result)
        {
            Clear();
            // A deferred start has no bad device; any other failure may.
            if (result != E_PENDING) ReleaseCachedDevice();
            lastResult = result;
            return false;
        }

        HRESULT DeviceResult() const
        {
            HRESULT result = renderDevice->GetDeviceRemovedReason();
            if (SUCCEEDED(result) && crossAdapter)
                result = captureDevice->GetDeviceRemovedReason();
            return result;
        }

        HRESULT Start(IDXGIDevice* device, ID2D1DeviceContext* context, HMONITOR requestedMonitor,
            bool allowSlowDevice)
        {
            stage = L"capture/device";
            if (!device || !context || !requestedMonitor) return E_INVALIDARG;
            HRESULT result = device->QueryInterface(IID_PPV_ARGS(renderDevice.GetAddressOf()));
            if (FAILED(result)) return result;
            renderContext = context;

            ComPtr<IDXGIAdapter> renderAdapter;
            result = device->GetAdapter(renderAdapter.GetAddressOf());
            if (FAILED(result)) return result;
            DXGI_ADAPTER_DESC renderDesc{};
            result = renderAdapter->GetDesc(&renderDesc);
            if (FAILED(result)) return result;

            // A fresh factory sees adapter/output changes on an explicit restart.
            ComPtr<IDXGIFactory1> factory;
            result = CreateDXGIFactory1(IID_PPV_ARGS(factory.GetAddressOf()));
            if (FAILED(result)) return result;
            ComPtr<IDXGIAdapter1> outputAdapter;
            ComPtr<IDXGIOutput1> output;
            DXGI_OUTPUT_DESC outputDesc{};
            stage = L"capture/find-output";
            result = FindOutput(factory.Get(), requestedMonitor, outputAdapter, output, outputDesc);
            if (FAILED(result)) return result;
            DXGI_ADAPTER_DESC1 captureDesc{};
            result = outputAdapter->GetDesc1(&captureDesc);
            if (FAILED(result)) return result;
            crossAdapter = renderDesc.AdapterLuid.LowPart != captureDesc.AdapterLuid.LowPart ||
                renderDesc.AdapterLuid.HighPart != captureDesc.AdapterLuid.HighPart;
            if (crossAdapter)
            {
                const bool cached = cachedOutputDevice &&
                    cachedOutputLuid.LowPart == captureDesc.AdapterLuid.LowPart &&
                    cachedOutputLuid.HighPart == captureDesc.AdapterLuid.HighPart &&
                    SUCCEEDED(cachedOutputDevice->GetDeviceRemovedReason());
                if (!cached)
                {
                    ReleaseCachedDevice();
                    stage = L"capture/output-device-deferred";
                    if (!allowSlowDevice) return E_PENDING;
                    // An explicit adapter requires UNKNOWN, never HARDWARE or WARP.
                    stage = L"capture/output-device";
                    result = D3D11CreateDevice(outputAdapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                        D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION,
                        cachedOutputDevice.GetAddressOf(), nullptr, cachedOutputContext.GetAddressOf());
                    if (FAILED(result)) { ReleaseCachedDevice(); return result; }
                    cachedOutputLuid = captureDesc.AdapterLuid;
                }
                captureDevice = cachedOutputDevice;
                captureContext = cachedOutputContext;
            }
            else
            {
                captureDevice = renderDevice;
                renderDevice->GetImmediateContext(captureContext.GetAddressOf());
            }

            bounds = outputDesc.DesktopCoordinates;
            const int64_t boundsWidth = int64_t(bounds.right) - bounds.left;
            const int64_t boundsHeight = int64_t(bounds.bottom) - bounds.top;
            if (boundsWidth <= 0 || boundsHeight <= 0 ||
                boundsWidth > D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION ||
                boundsHeight > D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION ||
                uint64_t(boundsWidth) * uint64_t(boundsHeight) > MaxPixels)
                return E_INVALIDARG;

            stage = L"capture/duplicate-output";
            result = output->DuplicateOutput(captureDevice.Get(), duplication.GetAddressOf());
            if (FAILED(result)) return result;
            DXGI_OUTDUPL_DESC desc{};
            duplication->GetDesc(&desc);
            stage = L"capture/duplication-format";
            if (!ValidSize(desc.ModeDesc.Width, desc.ModeDesc.Height) ||
                desc.ModeDesc.Format != DXGI_FORMAT_B8G8R8A8_UNORM)
                return DXGI_ERROR_UNSUPPORTED;
            stage = L"capture/duplication-rotation";
            if (desc.Rotation == DXGI_MODE_ROTATION_UNSPECIFIED)
                desc.Rotation = outputDesc.Rotation == DXGI_MODE_ROTATION_UNSPECIFIED
                    ? DXGI_MODE_ROTATION_IDENTITY : outputDesc.Rotation;
            switch (desc.Rotation)
            {
            case DXGI_MODE_ROTATION_IDENTITY:
            case DXGI_MODE_ROTATION_ROTATE90:
            case DXGI_MODE_ROTATION_ROTATE180:
            case DXGI_MODE_ROTATION_ROTATE270:
                rotation = desc.Rotation;
                break;
            default:
                return DXGI_ERROR_UNSUPPORTED;
            }
            monitor = requestedMonitor;
            stage = L"capture/dxgi";
            return DeviceResult();
        }

        HRESULT StartWindowsCapture(IDXGIDevice* device, ID2D1DeviceContext* context, HMONITOR requestedMonitor)
        {
            try
            {
                stage = L"capture/wgc-support";
                if (!capture::GraphicsCaptureSession::IsSupported()) return DXGI_ERROR_UNSUPPORTED;
                winrt::check_hresult(device->QueryInterface(IID_PPV_ARGS(renderDevice.GetAddressOf())));
                renderContext = context;
                captureDevice = renderDevice;
                renderDevice->GetImmediateContext(captureContext.GetAddressOf());
                // The free-threaded producer shares this D3D device with D2D.
                ComPtr<ID3D11Multithread> multithread;
                winrt::check_hresult(captureContext.As(&multithread));
                multithread->SetMultithreadProtected(TRUE);
                MONITORINFO info{sizeof(info)};
                if (!GetMonitorInfoW(requestedMonitor, &info)) return HRESULT_FROM_WIN32(GetLastError());
                bounds = info.rcMonitor;
                stage = L"capture/wgc-monitor";
                auto interop = winrt::get_activation_factory<capture::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
                winrt::check_hresult(interop->CreateForMonitor(requestedMonitor,
                    winrt::guid_of<capture::GraphicsCaptureItem>(), winrt::put_abi(captureItem)));
                const auto size = captureItem.Size();
                if (size.Width < 1 || size.Height < 1 || !ValidSize(size.Width, size.Height)) return E_INVALIDARG;
                stage = L"capture/wgc-pool";
                winrt::com_ptr<IInspectable> inspectable;
                winrt::check_hresult(CreateDirect3D11DeviceFromDXGIDevice(device, inspectable.put()));
                const auto projectedDevice = inspectable.as<winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice>();
                framePool = capture::Direct3D11CaptureFramePool::CreateFreeThreaded(projectedDevice,
                    winrt::Windows::Graphics::DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size);
                stage = L"capture/wgc-session";
                captureSession = framePool.CreateCaptureSession(captureItem);
                try { captureSession.IsCursorCaptureEnabled(false); } catch (const winrt::hresult_error&) {}
                // Windows may retain its border without borderless permission.
                // Do not request permission or change system capture preferences.
                try { captureSession.IsBorderRequired(false); } catch (const winrt::hresult_error&) {}
                captureSession.StartCapture();
                captureStarted = GetTickCount64();
                monitor = requestedMonitor;
                // WGC supplies an upright image on the supplied render device,
                // including when the monitor is driven by another GPU.
                rotation = DXGI_MODE_ROTATION_IDENTITY;
                crossAdapter = false;
                stage = L"capture/windows-graphics-capture";
                return DeviceResult();
            }
            catch (const winrt::hresult_error& error) { return error.code(); }
            catch (...) { return E_FAIL; }
        }

        HRESULT PollWindowsCapture()
        {
            stage = L"capture/wgc-frame";
            struct FrameScope
            {
                capture::Direct3D11CaptureFrame value{nullptr};
                ~FrameScope() { try { if (value) value.Close(); } catch (...) {} }
            } acquired{framePool.TryGetNextFrame()};
            if (!acquired.value)
                return !receivedImage && GetTickCount64() - captureStarted > 3000 ? DXGI_ERROR_WAIT_TIMEOUT : S_FALSE;
            // At most two queued frames; prefer the newest without any waits.
            auto newer = framePool.TryGetNextFrame();
            if (newer) { acquired.value.Close(); acquired.value = std::move(newer); }
            const auto size = acquired.value.ContentSize();
            if (size.Width != bounds.right - bounds.left || size.Height != bounds.bottom - bounds.top)
                return DXGI_ERROR_ACCESS_LOST; // Restart the pool on a mode change.
            const auto access = acquired.value.Surface().as<Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
            ComPtr<ID3D11Texture2D> texture;
            winrt::check_hresult(access->GetInterface(IID_PPV_ARGS(texture.GetAddressOf())));
            stage = L"capture/wgc-copy";
            winrt::check_hresult(CopyTexture(texture.Get()));
            return Publish();
        }

        HRESULT EnsureResources(const D3D11_TEXTURE2D_DESC& source)
        {
            if (!ValidSize(source.Width, source.Height) ||
                source.Format != DXGI_FORMAT_B8G8R8A8_UNORM || source.MipLevels != 1 ||
                source.ArraySize != 1 || source.SampleDesc.Count != 1 || source.SampleDesc.Quality != 0)
                return DXGI_ERROR_UNSUPPORTED;
            // A mode change needs a caller-controlled restart, not a retry loop.
            if (width || height)
                return source.Width == width && source.Height == height ? S_OK : DXGI_ERROR_ACCESS_LOST;

            D3D11_TEXTURE2D_DESC desc{};
            desc.Width = source.Width;
            desc.Height = source.Height;
            desc.MipLevels = desc.ArraySize = desc.SampleDesc.Count = 1;
            desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            const auto properties = BitmapProperties();
            HRESULT result;
            if (crossAdapter)
            {
                desc.Usage = D3D11_USAGE_STAGING;
                desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
                for (auto& slot : staging)
                {
                    result = captureDevice->CreateTexture2D(&desc, nullptr, slot.texture.GetAddressOf());
                    if (FAILED(result)) return result;
                }
                result = renderContext->CreateBitmap(D2D1::SizeU(desc.Width, desc.Height),
                    nullptr, 0, &properties, bitmap.GetAddressOf());
            }
            else
            {
                desc.Usage = D3D11_USAGE_DEFAULT;
                desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
                result = renderDevice->CreateTexture2D(&desc, nullptr, ownedTexture.GetAddressOf());
                if (FAILED(result)) return result;
                ComPtr<IDXGISurface> surface;
                result = ownedTexture.As(&surface);
                if (FAILED(result)) return result;
                result = renderContext->CreateBitmapFromDxgiSurface(surface.Get(),
                    &properties, bitmap.GetAddressOf());
            }
            if (FAILED(result)) return result;
            width = desc.Width;
            height = desc.Height;
            return S_OK;
        }

        HRESULT Publish()
        {
            if (serial == (std::numeric_limits<uint64_t>::max)())
                return HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW);
            frame.bitmap = bitmap;
            frame.screenBounds = bounds;
            frame.rotation = rotation;
            frame.serial = ++serial;
            return S_OK;
        }

        HRESULT UploadPending()
        {
            auto& slot = staging[readSlot];
            if (!slot.pending) return S_FALSE;
            D3D11_MAPPED_SUBRESOURCE mapped{};
            HRESULT result = captureContext->Map(slot.texture.Get(), 0, D3D11_MAP_READ,
                D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped);
            if (result == DXGI_ERROR_WAS_STILL_DRAWING) return S_FALSE;
            if (FAILED(result)) return result;
            MappedTexture unmap(captureContext.Get(), slot.texture.Get());
            if (!mapped.pData || mapped.RowPitch < width * 4 ||
                uint64_t(mapped.RowPitch) * height > (std::numeric_limits<UINT>::max)())
                return E_UNEXPECTED;
            result = bitmap->CopyFromMemory(nullptr, mapped.pData, mapped.RowPitch);
            if (FAILED(result)) return result;
            slot.pending = false;
            readSlot = (readSlot + 1) % 2;
            return Publish();
        }

        HRESULT CopyAcquired(IDXGIResource* resource, const DXGI_OUTDUPL_FRAME_INFO& info)
        {
            if (receivedImage && info.LastPresentTime.QuadPart == 0) return S_FALSE;
            ComPtr<ID3D11Texture2D> texture;
            HRESULT result = resource->QueryInterface(IID_PPV_ARGS(texture.GetAddressOf()));
            if (FAILED(result)) return result;
            return CopyTexture(texture.Get());
        }

        HRESULT CopyTexture(ID3D11Texture2D* texture)
        {
            D3D11_TEXTURE2D_DESC desc{};
            texture->GetDesc(&desc);
            HRESULT result = EnsureResources(desc);
            if (FAILED(result)) return result;
            if (crossAdapter)
            {
                auto& slot = staging[writeSlot];
                captureContext->CopyResource(slot.texture.Get(), texture);
                // Submit once; do not wait/spin. Otherwise a DO_NOT_WAIT Map can
                // keep seeing an unsubmitted copy on this capture-only device.
                captureContext->Flush();
                slot.pending = true;
                writeSlot = (writeSlot + 1) % 2;
            }
            else
                captureContext->CopyResource(ownedTexture.Get(), texture);
            receivedImage = true;
            return S_OK;
        }

        bool Poll()
        {
            startupDiagnostic.clear();
            if (!duplication && !captureSession) return false; // Preserve the last failure until Start.
            HRESULT result = DeviceResult();
            if (FAILED(result)) return Fail(result);
            if (captureSession)
            {
                try { result = PollWindowsCapture(); }
                catch (const winrt::hresult_error& error) { result = error.code(); }
                catch (...) { result = E_FAIL; }
                if (FAILED(result)) return Fail(result);
                lastResult = result;
                return result == S_OK;
            }
            stage = L"capture/dxgi-frame";
            bool changed = false;
            if (crossAdapter)
            {
                result = UploadPending();
                if (FAILED(result)) return Fail(result);
                changed = result == S_OK;
            }
            // Never overwrite an in-flight staging copy, or consume a desktop
            // update we have no space to keep. The next poll can acquire it.
            if (!crossAdapter || !staging[writeSlot].pending)
            {
                DXGI_OUTDUPL_FRAME_INFO info{};
                ComPtr<IDXGIResource> resource;
                result = duplication->AcquireNextFrame(0, &info, resource.GetAddressOf());
                if (result != DXGI_ERROR_WAIT_TIMEOUT)
                {
                    if (FAILED(result)) return Fail(result);
                    AcquiredFrame release(duplication.Get());
                    result = resource ? CopyAcquired(resource.Get(), info) : E_UNEXPECTED;
                    resource.Reset();
                    const HRESULT releaseResult = release.Release();
                    if (FAILED(releaseResult)) return Fail(releaseResult);
                    if (FAILED(result)) return Fail(result);
                    if (result == S_OK && !crossAdapter)
                    {
                        result = Publish();
                        if (FAILED(result)) return Fail(result);
                        changed = true;
                    }
                }
            }
            result = DeviceResult();
            if (FAILED(result)) return Fail(result);
            lastResult = changed ? S_OK : S_FALSE;
            return changed;
        }
    };

    DesktopBackdrop::DesktopBackdrop() : impl(std::make_unique<Impl>()) {}
    DesktopBackdrop::~DesktopBackdrop() { impl->Clear(); impl->ReleaseCachedDevice(); }

    bool DesktopBackdrop::Start(IDXGIDevice* renderDevice, ID2D1DeviceContext* renderContext, HMONITOR monitor,
        bool allowSlowDevice)
    {
        impl->Clear();
        impl->startupDiagnostic.clear();
        HRESULT result = impl->Start(renderDevice, renderContext, monitor, allowSlowDevice);
        if (result == DXGI_ERROR_UNSUPPORTED)
        {
            const auto* unsupportedStage = impl->stage;
            impl->Clear();
            result = impl->StartWindowsCapture(renderDevice, renderContext, monitor);
            impl->startupDiagnostic = std::wstring(impl->stage) + L" (after " + unsupportedStage + L")";
        }
        if (FAILED(result)) return impl->Fail(result);
        impl->lastResult = S_OK;
        return true;
    }

    bool DesktopBackdrop::Poll() { return impl->Poll(); }
    void DesktopBackdrop::Stop() { impl->Clear(); }
    bool DesktopBackdrop::Running() const { return impl->duplication != nullptr || impl->captureSession != nullptr; }
    HMONITOR DesktopBackdrop::Monitor() const { return impl->monitor; }
    const BackdropFrame& DesktopBackdrop::Frame() const { return impl->frame; }
    HRESULT DesktopBackdrop::LastResult() const { return impl->lastResult; }
    const wchar_t* DesktopBackdrop::Diagnostic() const
    { return impl->startupDiagnostic.empty() ? impl->stage : impl->startupDiagnostic.c_str(); }
    bool DesktopBackdrop::CrossAdapter() const { return impl->crossAdapter; }
}
