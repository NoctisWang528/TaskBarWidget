#pragma once

#include <Windows.h>
#include <d2d1_1.h>
#include <dxgi1_2.h>
#include <wrl/client.h>

#include <chrono>
#include <cstdint>
#include <memory>
#include <vector>

namespace Cards
{
    // Only the owning card/menu HWND is excluded. Restore exactly its former
    // affinity on hide, fallback or destruction; never change another app.
    class CaptureExclusion
    {
    public:
        ~CaptureExclusion() { Restore(); }
        // sessionScoped is valid only after this HWND's session list is installed.
        bool Enable(HWND window, bool sessionScoped = false);
        bool Restore();
        bool Active() const { return window != nullptr; }
    private:
        HWND window{};
        DWORD previous{};
        bool sessionOnly{};
    };

    struct BackdropFrame
    {
        Microsoft::WRL::ComPtr<ID2D1Bitmap1> bitmap;
        RECT screenBounds{};
        DXGI_MODE_ROTATION rotation{ DXGI_MODE_ROTATION_IDENTITY };
        uint64_t serial{};
    };

    // Caller-owned, single-threaded capture resource. Use on the render device's
    // owning thread, outside BeginDraw/EndDraw; renderContext must use renderDevice.
    // The caller owns HWND affinity, scheduling, cropping and rotation. This
    // helper owns WGC's session exclusion list and never changes windows.
    class DesktopBackdrop
    {
    public:
        DesktopBackdrop();
        ~DesktopBackdrop();
        DesktopBackdrop(const DesktopBackdrop&) = delete;
        DesktopBackdrop& operator=(const DesktopBackdrop&) = delete;

        // Replaces any previous session; success does not imply a frame is ready.
        // A cross-adapter output needs its own D3D device, cached after the first
        // creation. Without allowSlowDevice, an uncached device is not created:
        // Start fails with LastResult() == E_PENDING so an animation never blocks.
        bool Start(IDXGIDevice* renderDevice, ID2D1DeviceContext* renderContext, HMONITOR monitor,
            const std::vector<HWND>& excludedWindows, bool allowSlowDevice = true);
        bool SessionExclusion() const;
        // Update a live session first; restart the producer only on failure.
        // During settling no new bitmap content is published to the renderer.
        bool Settling() const;
        bool SetExcludedWindows(const std::vector<HWND>& windows);
        // WGC uses about 90% of this interval; updates a live session without
        // restarting it. Retained across Stop/Start; older systems may ignore it.
        void SetTargetFrameInterval(std::chrono::nanoseconds interval);
        // True only when bitmap content changed. Acquisition and staging Map never
        // wait for a frame/GPU; cross-adapter delivery is pipelined across polls.
        // S_FALSE means healthy but unchanged (including timeout/busy staging).
        // A failed HRESULT stops capture and clears Frame(); caller must restart.
        bool Poll();
        // Stops the producer and clears Frame(); retains WGC allocations for reuse.
        // Desktop Duplication still drops images and retains only its cached device.
        void Stop();
        // Closes the pool and drops all capture allocations, then clears/flushes D3D.
        void ReleaseResources();
        bool ResourcesRetained() const;
        // Lets the host trim shared graphics caches after an internal rebuild/failure.
        bool TakeResourcesReleased();
        bool Running() const;
        HMONITOR Monitor() const;
        // Borrowed, mutable content, not a snapshot. Do not retain bitmap copies
        // after Stop/device loss if captured content must be released immediately.
        // Successful content serials increase across Stop/Start on this instance.
        const BackdropFrame& Frame() const;
        HRESULT LastResult() const;
        const wchar_t* Diagnostic() const;
        bool CrossAdapter() const;
        // Successfully published capture frames, retained across Stop/Start.
        uint64_t CapturedFrames() const;

    private:
        struct Impl;
        std::unique_ptr<Impl> impl;
    };
}
