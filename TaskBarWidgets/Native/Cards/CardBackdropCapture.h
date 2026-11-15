#pragma once

#include <Windows.h>
#include <d2d1_1.h>
#include <dxgi1_2.h>
#include <wrl/client.h>

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
        // A changed list restarts only the WGC producer, before any affinity is
        // restored. No queued frame from the previous list can reach the renderer.
        bool SetExcludedWindows(const std::vector<HWND>& windows);
        // True only when bitmap content changed. Acquisition and staging Map never
        // wait for a frame/GPU; cross-adapter delivery is pipelined across polls.
        // S_FALSE means healthy but unchanged (including timeout/busy staging).
        // A failed HRESULT stops capture and clears Frame(); caller must restart.
        bool Poll();
        // Clears all owned images/sessions, retaining LastResult for diagnostics
        // and the content-free cross-adapter device (released on failure/destruction).
        void Stop();
        // Full idle/device-loss teardown, including the content-free cached
        // cross-adapter device that ordinary hide/reopen keeps briefly warm.
        void Release();
        bool Running() const;
        HMONITOR Monitor() const;
        // Borrowed, mutable content, not a snapshot. Do not retain bitmap copies
        // after Stop/device loss if captured content must be released immediately.
        // Successful content serials increase across Stop/Start on this instance.
        const BackdropFrame& Frame() const;
        HRESULT LastResult() const;
        const wchar_t* Diagnostic() const;
        bool CrossAdapter() const;
        bool HasResources() const;

    private:
        struct Impl;
        std::unique_ptr<Impl> impl;
    };
}
