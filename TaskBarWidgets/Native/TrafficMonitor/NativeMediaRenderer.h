#pragma once

#include "CompositionFade.h"
#include "MediaAnimation.h"
#include "MediaFrameContract.h"
#include "NativeMediaPainter.h"

#include <d2d1_1.h>
#include <d3d11.h>
#include <dcomp.h>
#include <dxgi1_3.h>
#include <wrl/client.h>

#include <cstddef>
#include <cstdint>
#include <functional>
#include <optional>
#include <string>
#include <unordered_set>
#include <vector>

namespace NativeMedia
{
    struct RenderUpdate
    {
        const Frame* frame{};
        const Style* style{};
        const Cover* cover{};
        const SpectrumTuning* tuning{};
        const std::vector<double>* spectrum{};
        bool forceFull{};
        bool coverChanged{};
    };

    struct RendererProbe
    {
        bool needsAnimation{};
        bool transitionActive{};
        TransitionKind transitionKind{ TransitionKind::Immediate };
        std::uint64_t transitionDeadline{};
        double transitionProgress{ 1.0 };
        std::optional<double> displayedScan;
        bool scanActive{};
    };

    // Read-only footprint sample of the renderer caches.
    struct FootprintSnapshot
    {
        unsigned layers{};
        unsigned swapChains{};
        std::size_t textLayouts{};
        std::size_t textFormats{};
        std::size_t brushes{};
        std::size_t covers{};
        std::uint64_t coverBytes{};
    };

    // Failure site of the composition pipeline. The value names the operation that
    // returned the failing HRESULT, so the log says what failed instead of only 0x.
    enum class FailureOp : unsigned char
    {
        None,
        CreateD3DDevice,
        GetDxgiDevice,
        CreateD2DDevice,
        CreateDeviceContext,
        CreateWriteFactory,
        InitializePainter,
        CreateCompositionDevice,
        CreateCompositionTarget,
        CreateCompositionVisual,
        CreateSwapChain,
        AddCompositionVisual,
        ApplyVisualTree,
        ResizeBuffers,
        AcquireBuffer,
        CreateTargetBitmap,
        Draw,
        Present
    };

    // Stable lowercase identifier of a failure site. Written to diagnostics and
    // metrics, so it must stay a fixed token without paths, sizes or media text.
    const wchar_t* FailureOpName(FailureOp op);

    // Read-only failure attribution. POD only: recording a repeat on the paint path
    // touches no heap, takes no lock and formats no string.
    struct FailureSnapshot
    {
        std::uint64_t failedOps{};       // failures reported by a checked call site
        std::uint64_t suppressedOps{};   // repeats folded into the 5 s log window
        std::uint64_t silentOps{};       // failures swallowed by an unchecked HRESULT
        std::uint64_t deviceLost{};      // failures classified as device loss
        std::uint64_t invalidCall{};     // DXGI_ERROR_INVALID_CALL (0x887A0001)
        std::uint32_t lastFailureHr{};   // HRESULT of the last failure, as raw bits
        FailureOp lastFailureOp{ FailureOp::None };
        std::uint32_t width{};           // swap-chain width at the last failure
        std::uint32_t height{};          // swap-chain height at the last failure
        std::uint32_t buffers{};         // swap-chain buffer count (2)
        bool targetBound{};              // the layer had a bound D2D bitmap
    };

    class NativeMediaRenderer
    {
    public:
        NativeMediaRenderer() = default;
        ~NativeMediaRenderer();

        void SetHost(HWND host);
        void SetLogger(std::function<void(const std::wstring&, HRESULT)> logger)
        {
            m_logger = std::move(logger);
        }
        void SetAppearance(const NativeAppearance& appearance);
        void SetOptions(bool reducedMotion, bool highContrast);
        void UpdateGeometry(float width, float height, float dpiScale);
        void Apply(const RenderUpdate& update);

        bool EnsureInitialized(std::uint64_t now);
        bool Present(std::uint64_t now);
        void SetVisible(bool visible, bool animate);
        bool HitTest(float x, float y, HitAction& action);

        bool IsReady() const { return m_ready; }
        bool IsRequestedVisible() const { return m_requestedVisible; }
        bool IsFullyHidden() const { return !m_requestedVisible && m_fade.Finished(); }
        bool FadeActive() const { return m_fade.Active(); }
        bool Failed() const { return !m_diagnostic.empty(); }
        const std::wstring& Diagnostic() const { return m_diagnostic; }
        std::uint64_t NextRetry() const { return m_nextRetry; }
        bool NeedsAnimation() const { return m_animationActive; }
        void DetachHost();
        void RetryNow();
        // Deterministic lifecycle harness for native tests. It exercises the
        // same Present/transition scheduler without creating GPU resources.
        void BeginHeadlessTest();
        RendererProbe AdvanceHeadlessTest(std::uint64_t now);
        // Renderer state only changes on the UI thread, so no lock is needed.
        FootprintSnapshot Footprint() const;
        FailureSnapshot Failures() const { return m_failures; }

    private:
        friend struct NativeMediaRendererTestAccess;
        struct Layer
        {
            Microsoft::WRL::ComPtr<IDXGISwapChain1> swapChain;
            Microsoft::WRL::ComPtr<ID2D1Bitmap1> target;
            Microsoft::WRL::ComPtr<IDCompositionVisual> visual;
            UINT width{};
            UINT height{};
            bool hasContent{};

            void Reset()
            {
                target.Reset();
                swapChain.Reset();
                visual.Reset();
                width = height = 0;
                hasContent = false;
            }
        };

        HWND m_host{};
        std::function<void(const std::wstring&, HRESULT)> m_logger;
        NativeAppearance m_appearance;
        bool m_reducedMotion{};
        bool m_highContrast{};
        float m_width{}, m_height{}, m_dpiScale{ 1.0f };

        Microsoft::WRL::ComPtr<ID3D11Device> m_d3dDevice;
        Microsoft::WRL::ComPtr<IDXGIDevice> m_dxgiDevice;
        Microsoft::WRL::ComPtr<ID2D1Factory1> m_d2dFactory;
        Microsoft::WRL::ComPtr<ID2D1Device> m_d2dDevice;
        Microsoft::WRL::ComPtr<IDWriteFactory> m_writeFactory;
        Microsoft::WRL::ComPtr<ID2D1DeviceContext> m_context;
        Microsoft::WRL::ComPtr<IDCompositionDevice> m_compositionDevice;
        Microsoft::WRL::ComPtr<IDCompositionTarget> m_compositionTarget;
        Microsoft::WRL::ComPtr<IDCompositionVisual> m_rootVisual;
        NativeMediaPainter m_painter;
        CompositionFade m_fade;
        bool m_ready{};
        bool m_headlessTest{};
        bool m_requestedVisible{};
        std::wstring m_diagnostic;
        std::uint64_t m_nextRetry{};
        unsigned m_retryAttempt{};

        FailureSnapshot m_failures;
        // Last failure line actually written. A repeat of the same operation and
        // HRESULT inside the 5 s window is only counted, never formatted or logged.
        FailureOp m_loggedOp{ FailureOp::None };
        HRESULT m_loggedHr{ S_OK };
        std::uint64_t m_loggedTick{};
        // Last line actually written; caps every failure key at one line per second.
        std::uint64_t m_lastFailureLogTick{};
        std::uint64_t m_pendingRepeats{};
        // Failure of a step that only reports success or failure; the caller's Fail
        // consumes it to name the real operation and HRESULT.
        FailureOp m_stepOp{ FailureOp::None };
        HRESULT m_stepHr{ S_OK };

        Layer m_background;
        Layer m_lyrics;
        Layer m_spectrum;
        Layer m_browser;
        Layer m_coverFront;
        Layer m_coverBack;
        bool m_coverFrontIsActive{ true };

        Frame m_displayedFrame;
        Frame m_pendingFrame;
        bool m_framePending{};
        Style m_mediaStyle;
        std::optional<Cover> m_cover;
        SpectrumTuning m_tuning;
        std::vector<double> m_spectrumValues;
        EffectiveStyle m_effectiveStyle;
        bool m_hasState{};
        bool m_styleDirty{};
        bool m_coverDirty{};
        bool m_tuningDirty{};
        bool m_spectrumDirty{};
        bool m_frameDirty{};
        bool m_geometryDirty{};
        bool m_browserDirty{};
        bool m_lyricsDirty{};
        bool m_releaseTextAfterPaint{};
        float m_browserTargetOpacity{};
        float m_coverTargetOpacity{ 1.0f };

        RollTransition m_transition;
        WordScanSmoother m_wordScan;
        SpectrumAnimator m_spectrumAnimator;
        bool m_spectrumSignalLogged{};
        std::optional<double> m_displayedScan;
        std::wstring m_scanTrack;
        int m_scanLine{ -1 };
        bool m_scanPlaying{};
        bool m_scanInitialized{};
        std::optional<double> m_scanTarget;
        double m_secondaryOpacity{ 0.72 };
        double m_secondaryTarget{ 0.72 };
        std::uint64_t m_lastPresent{};
        bool m_animationActive{};

        bool m_coverFadeActive{};
        std::uint64_t m_coverFadeUntil{};
        std::uint64_t m_coverKey{};
        std::uint64_t m_coverPendingKey{};
        float m_coverFrontOpacity{ 1.0f };
        float m_coverBackOpacity{ 0.0f };
        bool m_coverCrossPending{};

        bool m_layerTransitionActive{};
        std::uint64_t m_layerTransitionUntil{};
        float m_lyricsTargetOpacity{ 1.0f };
        float m_spectrumTargetOpacity{ 0.0f };

        bool CreateDevice();
        void ReleaseDevice();
        void ReleaseLayers();
        bool CreateLayers();
        bool ResizeLayers();
        void MarkAllDirty();
        void ComputeEffectiveStyle();
        void BeginFrameTransition(const Frame& next, std::uint64_t now);
        void CompleteFrameTransition(std::uint64_t now);
        void StartLayerTransition(bool toSpectrum, std::uint64_t now, int durationMs);
        void CompleteLayerTransition(std::uint64_t now);
        void CompleteCoverTransition(std::uint64_t now);
        void SetLayerOpacity(Layer& layer, float opacity);
        HRESULT AnimateLayerOpacity(Layer& layer, float from, float to, int durationMs);
        bool PaintLayer(Layer& layer, const std::function<void(ID2D1DeviceContext*)>& paint);
        bool PaintBackground();
        bool PaintLyricsLayer();
        bool PaintSpectrumLayer();
        bool PaintBrowserLayer();
        bool PaintCoverLayer(Layer& layer, const Cover* cover, std::uint64_t key);
        void UpdateSecondaryOpacity(std::uint64_t now);
        void UpdateSpectrum(std::uint64_t now);
        bool UpdateCover(std::uint64_t now);
        void ApplyVisualTree();
        void Log(const std::wstring& message, HRESULT result);
        // Records one failure and returns true when the log line should be written.
        // layer is the failing layer, or null when the failure is device wide.
        bool RecordFailure(FailureOp op, HRESULT result, const Layer* layer);
        std::wstring FailureContext();
        void NoteStepFailure(FailureOp op, HRESULT result);
        void Fail(FailureOp op, const std::wstring& message, HRESULT result);
        void ScheduleRetry(std::uint64_t now, HRESULT result);
        void HandleDeviceLost(HRESULT result, FailureOp op, const wchar_t* operation, const Layer* layer);
        bool IsDeviceLost(HRESULT result) const;
        void SetEffectiveScene();
        bool LayerNeedsPaint(Layer& layer) const;
        bool CreateCompositionTarget();
        void ResetSurfaceForReveal();
        void InvalidateScanSignal();
        Frame FrameForLyrics() const;
    };
}
