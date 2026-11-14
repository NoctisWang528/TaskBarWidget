#pragma once

#include <Windows.h>
#include <d2d1_1.h>
#include <dxgi1_2.h>
#include <wrl/client.h>

#include <cstdint>
#include <memory>

namespace Cards
{
    // Only the owning card/menu HWND is excluded. Restore exactly its former
    // affinity on hide, fallback or destruction; never change another app.
    class CaptureExclusion
    {
    public:
        ~CaptureExclusion() { Restore(); }
        bool Enable(HWND window);
        void Restore();
        bool Active() const { return window != nullptr; }
    private:
        HWND window{};
        DWORD previous{};
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
    // The caller owns window exclusion, scheduling, cropping and rotation. This
    // helper neither creates nor changes windows and never adds a pointer overlay.
    class DesktopBackdrop
    {
    public:
        DesktopBackdrop();
        ~DesktopBackdrop();
        DesktopBackdrop(const DesktopBackdrop&) = delete;
        DesktopBackdrop& operator=(const DesktopBackdrop&) = delete;

        // Replaces any previous session; success does not imply a frame is ready.
        bool Start(IDXGIDevice* renderDevice, ID2D1DeviceContext* renderContext, HMONITOR monitor);
        // True only when bitmap content changed. Acquisition and staging Map never
        // wait for a frame/GPU; cross-adapter delivery is pipelined across polls.
        // S_FALSE means healthy but unchanged (including timeout/busy staging).
        // A failed HRESULT stops capture and clears Frame(); caller must restart.
        bool Poll();
        // Clears all owned images/devices, retaining LastResult for diagnostics.
        void Stop();
        bool Running() const;
        HMONITOR Monitor() const;
        // Borrowed, mutable content, not a snapshot. Do not retain bitmap copies
        // after Stop/device loss if captured content must be released immediately.
        // Successful content serials increase across Stop/Start on this instance.
        const BackdropFrame& Frame() const;
        HRESULT LastResult() const;
        const wchar_t* Diagnostic() const;
        bool CrossAdapter() const;

    private:
        struct Impl;
        std::unique_ptr<Impl> impl;
    };
}
