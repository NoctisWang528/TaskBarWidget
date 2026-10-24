#include "NativeMediaRenderer.h"

#include <algorithm>
#include <cmath>
#include <sstream>

#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dcomp.lib")
#pragma comment(lib, "dwrite.lib")
#pragma comment(lib, "dxgi.lib")

using Microsoft::WRL::ComPtr;

namespace NativeMedia
{
    namespace
    {
        bool SameDouble(double left, double right)
        {
            return std::abs(left - right) < 0.00001;
        }

        bool SameFrame(const Frame& left, const Frame& right)
        {
            return left.revision == right.revision &&
                left.sourceId == right.sourceId &&
                left.isPlaying == right.isPlaying &&
                left.hasMedia == right.hasMedia &&
                left.isBrowser == right.isBrowser &&
                left.trackId == right.trackId &&
                left.lyrics.current == right.lyrics.current &&
                left.lyrics.next == right.lyrics.next &&
                left.lyrics.currentTranslation == right.lyrics.currentTranslation &&
                left.lyrics.nextTranslation == right.lyrics.nextTranslation &&
                left.lyrics.trackId == right.lyrics.trackId &&
                SameDouble(left.lyrics.progress, right.lyrics.progress) &&
                left.lyrics.hasWordScanProgress == right.lyrics.hasWordScanProgress &&
                (!left.lyrics.hasWordScanProgress ||
                    SameDouble(left.lyrics.wordScanProgress, right.lyrics.wordScanProgress)) &&
                left.lyrics.currentLineIndex == right.lyrics.currentLineIndex &&
                left.lyrics.isPureMusic == right.lyrics.isPureMusic &&
                left.lyrics.isPlaying == right.lyrics.isPlaying &&
                left.lyrics.translationMode == right.lyrics.translationMode &&
                left.lyrics.animateTransition == right.lyrics.animateTransition &&
                left.lyrics.scene == right.lyrics.scene &&
                left.browser.active == right.browser.active &&
                left.browser.title == right.browser.title &&
                left.browser.artist == right.browser.artist &&
                left.browser.source == right.browser.source &&
                left.browser.isPlaying == right.browser.isPlaying &&
                left.browser.canPrevious == right.browser.canPrevious &&
                left.browser.canNext == right.browser.canNext &&
                left.browser.canPlayPause == right.browser.canPlayPause;
        }

        bool SameStyle(const Style& left, const Style& right)
        {
            return left.fontFamily == right.fontFamily &&
                SameDouble(left.layoutScalePercent, right.layoutScalePercent) &&
                SameDouble(left.fontSize, right.fontSize) &&
                left.showCover == right.showCover &&
                left.coverPosition == right.coverPosition &&
                left.enableControlPanel == right.enableControlPanel &&
                SameDouble(left.coverSize, right.coverSize) &&
                SameDouble(left.coverGap, right.coverGap) &&
                SameDouble(left.coverCornerRadius, right.coverCornerRadius) &&
                SameDouble(left.viewportDescenderBuffer, right.viewportDescenderBuffer) &&
                SameDouble(left.layoutHorizontalPadding, right.layoutHorizontalPadding) &&
                SameDouble(left.lyricsPaneTopPadding, right.lyricsPaneTopPadding) &&
                SameDouble(left.lyricsPaneRightPadding, right.lyricsPaneRightPadding) &&
                SameDouble(left.lyricsPaneLeftPadding, right.lyricsPaneLeftPadding) &&
                SameDouble(left.primaryOffsetY, right.primaryOffsetY) &&
                SameDouble(left.secondaryOffsetY, right.secondaryOffsetY) &&
                SameDouble(left.lineTextBottomPadding, right.lineTextBottomPadding) &&
                SameDouble(left.surfaceRadius, right.surfaceRadius) &&
                SameDouble(left.layerTransitionOffset, right.layerTransitionOffset) &&
                SameDouble(left.coverFallbackFontSize, right.coverFallbackFontSize) &&
                SameDouble(left.spectrumWidth, right.spectrumWidth) &&
                SameDouble(left.spectrumHeight, right.spectrumHeight) &&
                SameDouble(left.spectrumGap, right.spectrumGap) &&
                SameDouble(left.spectrumBarWidth, right.spectrumBarWidth) &&
                SameDouble(left.spectrumBarHeight, right.spectrumBarHeight) &&
                SameDouble(left.spectrumLowHeight, right.spectrumLowHeight) &&
                SameDouble(left.spectrumHighHeight, right.spectrumHighHeight) &&
                SameDouble(left.spectrumMiddleHeight, right.spectrumMiddleHeight) &&
                left.fontWeight == right.fontWeight &&
                left.primaryColor == right.primaryColor &&
                left.secondaryColor == right.secondaryColor &&
                left.translationColor == right.translationColor &&
                left.wordScanOverlayColor == right.wordScanOverlayColor &&
                left.surfaceColor == right.surfaceColor &&
                left.surfaceShadow == right.surfaceShadow &&
                left.textShadow == right.textShadow &&
                left.textAlignment == right.textAlignment;
        }

        bool SameCover(const std::optional<Cover>& left, const std::optional<Cover>& right)
        {
            if (left.has_value() != right.has_value()) return false;
            if (!left) return true;
            return left->dataUri == right->dataUri &&
                left->fallbackText == right->fallbackText &&
                left->fallbackColor == right->fallbackColor &&
                left->fallbackIconDataUri == right->fallbackIconDataUri &&
                left->trackId == right->trackId &&
                left->cleared == right->cleared;
        }

        bool SameVisualContent(const Frame& left, const Frame& right)
        {
            return left.sourceId == right.sourceId &&
                left.isPlaying == right.isPlaying &&
                left.hasMedia == right.hasMedia &&
                left.isBrowser == right.isBrowser &&
                left.trackId == right.trackId &&
                left.lyrics.current == right.lyrics.current &&
                left.lyrics.next == right.lyrics.next &&
                left.lyrics.currentTranslation == right.lyrics.currentTranslation &&
                left.lyrics.nextTranslation == right.lyrics.nextTranslation &&
                left.lyrics.trackId == right.lyrics.trackId &&
                left.lyrics.currentLineIndex == right.lyrics.currentLineIndex &&
                left.lyrics.isPureMusic == right.lyrics.isPureMusic &&
                left.lyrics.isPlaying == right.lyrics.isPlaying &&
                left.lyrics.translationMode == right.lyrics.translationMode &&
                left.lyrics.scene == right.lyrics.scene &&
                left.browser.active == right.browser.active &&
                left.browser.title == right.browser.title &&
                left.browser.artist == right.browser.artist &&
                left.browser.source == right.browser.source &&
                left.browser.isPlaying == right.browser.isPlaying &&
                left.browser.canPrevious == right.browser.canPrevious &&
                left.browser.canNext == right.browser.canNext &&
                left.browser.canPlayPause == right.browser.canPlayPause;
        }

        bool SameTransitionIdentity(const Frame& left, const Frame& right)
        {
            return EffectiveTrackId(left) == EffectiveTrackId(right) &&
                left.lyrics.scene == right.lyrics.scene &&
                left.lyrics.currentLineIndex == right.lyrics.currentLineIndex &&
                left.lyrics.translationMode == right.lyrics.translationMode &&
                left.lyrics.isPureMusic == right.lyrics.isPureMusic &&
                left.isBrowser == right.isBrowser &&
                left.browser.active == right.browser.active;
        }

        bool SameTuning(const SpectrumTuning& left, const SpectrumTuning& right)
        {
            return SameDouble(left.rise, right.rise) &&
                SameDouble(left.fall, right.fall) &&
                SameDouble(left.minHeight, right.minHeight) &&
                SameDouble(left.heightRange, right.heightRange) &&
                SameDouble(left.opacity, right.opacity) &&
                left.barCount == right.barCount;
        }

        // Buffer count of every composition swap chain. The failure context reports
        // the same value so the description and the diagnostics cannot drift apart.
        constexpr UINT SwapChainBuffers = 2;

        // Same window as NativeMediaHost::Failure. Inside it a repeated operation and
        // HRESULT pair only bumps counters instead of formatting another log line.
        constexpr std::uint64_t FailureLogWindowMs = 5000;

        // Global cap shared by every failure key: however many distinct keys fail,
        // at most one failure line per second is written.
        constexpr std::uint64_t FailureLogMinIntervalMs = 1000;

        bool IsDeviceLostResult(HRESULT result)
        {
            return result == DXGI_ERROR_DEVICE_REMOVED || result == DXGI_ERROR_DEVICE_RESET ||
                result == D2DERR_RECREATE_TARGET || result == D2DERR_WRONG_STATE ||
                result == static_cast<HRESULT>(0x8899000CUL);
        }

        // DXGI_ERROR_INVALID_CALL is deliberately not device loss: it is a caller side
        // argument or state error, so it is counted and reported on its own until the
        // desktop log proves which class the observed 0x887A0001 belongs to.
        const wchar_t* FailureKindName(HRESULT result)
        {
            if (IsDeviceLostResult(result)) return L"device_lost";
            if (result == DXGI_ERROR_INVALID_CALL) return L"invalid_call";
            return L"failure";
        }

        std::uint32_t SizeToPixels(float value)
        {
            return value > 0.0f ? static_cast<std::uint32_t>(value + 0.5f) : 0u;
        }

        std::wstring FormatHResult(HRESULT result)
        {
            wchar_t text[32]{};
            swprintf_s(text, L"0x%08X", static_cast<unsigned>(result));
            return text;
        }

        std::wstring ColorString(COLORREF color, double alpha)
        {
            return L"rgba(" + std::to_wstring(GetRValue(color)) + L", " +
                std::to_wstring(GetGValue(color)) + L", " +
                std::to_wstring(GetBValue(color)) + L", " +
                std::to_wstring(alpha) + L")";
        }

        HRESULT AnimateOpacity(IDCompositionDevice* device, IDCompositionVisual* visual,
            float from, float to, int durationMs)
        {
            if (!device || !visual) return E_INVALIDARG;
            ComPtr<IDCompositionEffectGroup> effect;
            HRESULT result = device->CreateEffectGroup(&effect);
            if (FAILED(result)) return result;
            if (durationMs <= 0)
            {
                result = effect->SetOpacity(to);
                return SUCCEEDED(result) ? visual->SetEffect(effect.Get()) : result;
            }
            ComPtr<IDCompositionAnimation> animation;
            result = device->CreateAnimation(&animation);
            const double seconds = durationMs / 1000.0;
            if (SUCCEEDED(result))
                result = animation->AddCubic(0, from, static_cast<float>((to - from) / seconds), 0, 0);
            if (SUCCEEDED(result)) result = animation->End(seconds, to);
            if (SUCCEEDED(result)) result = effect->SetOpacity(animation.Get());
            if (SUCCEEDED(result)) result = visual->SetEffect(effect.Get());
            return result;
        }

        HRESULT AnimateOffsetY(IDCompositionDevice* device, IDCompositionVisual* visual,
            float from, float to, int durationMs)
        {
            if (!device || !visual) return E_INVALIDARG;
            if (durationMs <= 0) return visual->SetOffsetY(to);
            ComPtr<IDCompositionAnimation> animation;
            HRESULT result = device->CreateAnimation(&animation);
            const double seconds = durationMs / 1000.0;
            if (SUCCEEDED(result))
                result = animation->AddCubic(0, from, static_cast<float>((to - from) / seconds), 0, 0);
            if (SUCCEEDED(result)) result = animation->End(seconds, to);
            if (SUCCEEDED(result)) result = visual->SetOffsetY(animation.Get());
            return result;
        }
    }

    const wchar_t* FailureOpName(FailureOp op)
    {
        switch (op)
        {
        case FailureOp::CreateD3DDevice: return L"create_d3d_device";
        case FailureOp::GetDxgiDevice: return L"get_dxgi_device";
        case FailureOp::CreateD2DDevice: return L"create_d2d_device";
        case FailureOp::CreateDeviceContext: return L"create_device_context";
        case FailureOp::CreateWriteFactory: return L"create_write_factory";
        case FailureOp::InitializePainter: return L"initialize_painter";
        case FailureOp::CreateCompositionDevice: return L"create_composition_device";
        case FailureOp::CreateCompositionTarget: return L"create_composition_target";
        case FailureOp::CreateCompositionVisual: return L"create_composition_visual";
        case FailureOp::CreateSwapChain: return L"create_swap_chain";
        case FailureOp::AddCompositionVisual: return L"add_composition_visual";
        case FailureOp::ApplyVisualTree: return L"apply_visual_tree";
        case FailureOp::ResizeBuffers: return L"resize_buffers";
        case FailureOp::AcquireBuffer: return L"acquire_buffer";
        case FailureOp::CreateTargetBitmap: return L"create_target_bitmap";
        case FailureOp::Draw: return L"draw";
        case FailureOp::Present: return L"present";
        case FailureOp::None: break;
        }
        return L"none";
    }

    NativeMediaRenderer::~NativeMediaRenderer()
    {
        DetachHost();
    }

    void NativeMediaRenderer::Log(const std::wstring& message, HRESULT result)
    {
        if (m_logger) m_logger(message, result);
    }

    // Attribution only: POD writes and one GetTickCount64, no allocation, no lock.
    // Returns true when this failure has to be formatted and logged.
    bool NativeMediaRenderer::RecordFailure(FailureOp op, HRESULT result, const Layer* layer)
    {
        ++m_failures.failedOps;
        m_failures.lastFailureHr = static_cast<std::uint32_t>(result);
        m_failures.lastFailureOp = op;
        m_failures.width = layer ? layer->width : SizeToPixels(m_width);
        m_failures.height = layer ? layer->height : SizeToPixels(m_height);
        m_failures.buffers = SwapChainBuffers;
        m_failures.targetBound = layer && layer->target != nullptr;
        if (result == DXGI_ERROR_INVALID_CALL) ++m_failures.invalidCall;
        if (IsDeviceLostResult(result)) ++m_failures.deviceLost;

        const std::uint64_t now = GetTickCount64();
        const bool sameKey = op == m_loggedOp && result == m_loggedHr;
        if (sameKey && now - m_loggedTick < FailureLogWindowMs)
        {
            ++m_failures.suppressedOps;
            ++m_pendingRepeats;
            return false;
        }
        // Alternating failure keys must not become a per-frame log storm, so the
        // global cap suppresses every key for one second after the last written
        // line. Suppressed calls only count; the five second window above is
        // unchanged and still wins for same-key repeats.
        if (now - m_lastFailureLogTick < FailureLogMinIntervalMs)
        {
            ++m_failures.suppressedOps;
            if (sameKey) ++m_pendingRepeats;
            return false;
        }
        // Repeats counted above belong to the previously logged site, so a different
        // failure never reports them as its own.
        if (!sameKey) m_pendingRepeats = 0;
        m_loggedOp = op;
        m_loggedHr = result;
        m_loggedTick = now;
        m_lastFailureLogTick = now;
        return true;
    }

    // Called only where a log line is produced, never per frame and never per repeat.
    std::wstring NativeMediaRenderer::FailureContext()
    {
        const std::uint64_t repeats = m_pendingRepeats;
        m_pendingRepeats = 0;
        wchar_t size[48]{};
        swprintf_s(size, L"%ux%u", m_failures.width, m_failures.height);
        const HRESULT result = static_cast<HRESULT>(m_failures.lastFailureHr);
        return std::wstring(L" op=") + FailureOpName(m_failures.lastFailureOp) +
            L" kind=" + FailureKindName(result) +
            L" size=" + size +
            L" buffers=" + std::to_wstring(m_failures.buffers) +
            L" targetBound=" + (m_failures.targetBound ? L"1" : L"0") +
            L" repeats=" + std::to_wstring(repeats);
    }

    void NativeMediaRenderer::NoteStepFailure(FailureOp op, HRESULT result)
    {
        m_stepOp = op;
        m_stepHr = result;
    }

    void NativeMediaRenderer::Fail(FailureOp op, const std::wstring& message, HRESULT result)
    {
        // Consume a pending step first: it names the real failing operation and
        // HRESULT and must never outlive its own failure path to label another one.
        if (m_stepOp != FailureOp::None)
        {
            op = m_stepOp;
            result = m_stepHr;
            m_stepOp = FailureOp::None;
        }
        if (!RecordFailure(op, result, nullptr)) return;
        // Updated only when a line is actually written, matching HandleDeviceLost:
        // a suppressed repeat keeps the previously reported diagnostic text.
        m_diagnostic = message;
        Log(message + L" (" + FormatHResult(result) + L")" + FailureContext(), result);
    }

    void NativeMediaRenderer::SetHost(HWND host)
    {
        if (m_host == host) return;
        DetachHost();
        m_host = host;
        m_diagnostic.clear();
        m_retryAttempt = 0;
        m_nextRetry = 0;
        if (!host) return;
        if (m_ready)
        {
            if (!CreateCompositionTarget())
            {
                // This path never reaches Fail(), so consume a pending step here
                // instead of letting it label a later, unrelated failure.
                m_stepOp = FailureOp::None;
                ScheduleRetry(GetTickCount64(), E_FAIL);
                return;
            }
            MarkAllDirty();
        }
    }

    void NativeMediaRenderer::DetachHost()
    {
        if (m_compositionTarget)
        {
            m_compositionTarget->SetRoot(nullptr);
            if (m_compositionDevice) m_compositionDevice->Commit();
        }
        ReleaseLayers();
        m_compositionTarget.Reset();
        m_rootVisual.Reset();
        m_ready = false;
        m_requestedVisible = false;
        m_fade = CompositionFade{};
    }

    void NativeMediaRenderer::RetryNow()
    {
        m_nextRetry = 0;
        if (!m_ready) ReleaseDevice();
    }

    void NativeMediaRenderer::BeginHeadlessTest()
    {
        m_headlessTest = true;
        m_requestedVisible = true;
        m_hasState = false;
        m_framePending = false;
        m_displayedFrame = {};
        m_pendingFrame = {};
        m_transition.Reset();
        m_layerTransitionActive = false;
        m_coverCrossPending = false;
        m_coverFadeActive = false;
        InvalidateScanSignal();
        m_secondaryOpacity = 0.72;
        m_secondaryTarget = 0.72;
        m_lastPresent = 0;
        m_animationActive = false;
        m_styleDirty = m_coverDirty = m_tuningDirty = m_spectrumDirty = true;
        m_frameDirty = m_geometryDirty = m_browserDirty = m_lyricsDirty = true;
        m_width = 400;
        m_height = 48;
        m_dpiScale = 1;
    }

    RendererProbe NativeMediaRenderer::AdvanceHeadlessTest(std::uint64_t now)
    {
        const bool needsAnimation = Present(now);
        RendererProbe probe;
        probe.needsAnimation = needsAnimation;
        probe.transitionActive = m_transition.active;
        probe.transitionKind = m_transition.kind;
        if (m_transition.active)
        {
            probe.transitionDeadline = m_transition.started +
                static_cast<std::uint64_t>((std::max)(0, m_transition.durationMs));
            probe.transitionProgress = m_transition.Progress(now);
        }
        else if (m_layerTransitionActive)
            probe.transitionDeadline = m_layerTransitionUntil;
        probe.displayedScan = m_displayedScan;
        probe.scanActive = m_wordScan.Active(now);
        return probe;
    }

    void NativeMediaRenderer::SetAppearance(const NativeAppearance& appearance)
    {
        if (m_appearance.fontFamily == appearance.fontFamily &&
            SameDouble(m_appearance.fontSizePx, appearance.fontSizePx) &&
            m_appearance.fontWeight == appearance.fontWeight &&
            m_appearance.italic == appearance.italic &&
            m_appearance.underline == appearance.underline &&
            m_appearance.strikeOut == appearance.strikeOut &&
            m_appearance.surfaceColor == appearance.surfaceColor &&
            m_appearance.valid == appearance.valid)
            return;
        m_appearance = appearance;
        ComputeEffectiveStyle();
        MarkAllDirty();
    }

    void NativeMediaRenderer::SetOptions(bool reducedMotion, bool highContrast)
    {
        if (m_reducedMotion == reducedMotion && m_highContrast == highContrast) return;
        const bool motionChanged = m_reducedMotion != reducedMotion;
        m_reducedMotion = reducedMotion;
        m_highContrast = highContrast;
        ComputeEffectiveStyle();
        // A motion-mode switch changes which value the scan must present. Drop the
        // interpolator together with its input cache so the next frame rebuilds
        // both under the new rules instead of reading an empty interpolator.
        if (motionChanged) InvalidateScanSignal();
        MarkAllDirty();
    }

    void NativeMediaRenderer::UpdateGeometry(float width, float height, float dpiScale)
    {
        if (width < 1.0f || height < 1.0f || !std::isfinite(width) || !std::isfinite(height))
            return;
        if (!std::isfinite(dpiScale) || dpiScale <= 0) dpiScale = 1.0f;
        if (SameDouble(m_width, width) && SameDouble(m_height, height) &&
            SameDouble(m_dpiScale, dpiScale))
            return;
        m_width = width;
        m_height = height;
        m_dpiScale = dpiScale;
        ComputeEffectiveStyle();
        m_geometryDirty = true;
        m_styleDirty = true;
        m_coverDirty = true;
        m_browserDirty = true;
        m_spectrumDirty = true;
        m_frameDirty = true;
        if (m_coverFadeActive) CompleteCoverTransition(GetTickCount64());
        if (m_ready) ResizeLayers();
    }

    void NativeMediaRenderer::Apply(const RenderUpdate& update)
    {
        if (!update.frame || !update.style || !update.tuning || !update.spectrum) return;
        bool styleChanged = update.forceFull || !m_hasState || !SameStyle(m_mediaStyle, *update.style);
        if (styleChanged)
        {
            m_mediaStyle = *update.style;
            ComputeEffectiveStyle();
            MarkAllDirty();
            if (m_coverFadeActive) CompleteCoverTransition(GetTickCount64());
        }
        const bool frameChanged = update.forceFull || !m_hasState ||
            !SameFrame(m_pendingFrame, *update.frame);
        if (frameChanged)
        {
            m_pendingFrame = *update.frame;
            m_framePending = true;
        }
        bool coverChanged = false;
        std::optional<Cover> cover;
        if (update.forceFull || !m_hasState || update.coverChanged)
        {
            if (update.cover) cover = *update.cover;
            coverChanged = update.forceFull || !m_hasState ||
                !SameCover(m_cover, cover);
        }
        if (coverChanged)
        {
            m_cover = std::move(cover);
            m_coverDirty = true;
            m_coverCrossPending = m_hasState && m_ready;
            if (!m_hasState) m_coverCrossPending = false;
        }
        if (update.forceFull || !m_hasState || !SameTuning(m_tuning, *update.tuning))
        {
            m_tuning = *update.tuning;
            m_tuning.Normalize();
            m_tuningDirty = true;
            m_spectrumDirty = true;
            m_spectrumAnimator.Configure(m_tuning, *update.spectrum,
                m_pendingFrame.lyrics.isPlaying);
        }
        if (update.forceFull || !m_hasState || m_spectrumValues != *update.spectrum)
        {
            m_spectrumValues = *update.spectrum;
            m_spectrumDirty = true;
            m_spectrumAnimator.SetTargets(m_spectrumValues,
                m_pendingFrame.lyrics.isPlaying);
        }
        if (!m_hasState)
        {
            m_hasState = true;
            m_displayedFrame = m_pendingFrame;
            m_transition.Reset();
            m_secondaryOpacity = 0.72;
        }
    }

    void NativeMediaRenderer::ComputeEffectiveStyle()
    {
        m_effectiveStyle = MakeEffectiveStyle(m_mediaStyle, m_appearance, m_dpiScale);
        if (!m_highContrast) return;
        const auto text = GetSysColor(COLOR_WINDOWTEXT);
        const auto background = GetSysColor(COLOR_WINDOW);
        m_effectiveStyle.primaryColor = ColorString(text, 1.0);
        m_effectiveStyle.secondaryColor = ColorString(text, 0.8);
        m_effectiveStyle.translationColor = ColorString(text, 0.9);
        m_effectiveStyle.wordScanOverlayColor = ColorString(text, 1.0);
        m_effectiveStyle.surfaceColor = ColorString(background, 1.0);
        m_effectiveStyle.surfaceShadow = true;
    }

    void NativeMediaRenderer::MarkAllDirty()
    {
        m_styleDirty = m_coverDirty = m_tuningDirty = m_spectrumDirty = true;
        m_frameDirty = m_geometryDirty = m_browserDirty = true;
        m_lyricsDirty = true;
    }

    bool NativeMediaRenderer::CreateCompositionTarget()
    {
        if (!m_host || !m_compositionDevice) return false;
        if (!m_rootVisual)
        {
            const HRESULT visual = m_compositionDevice->CreateVisual(&m_rootVisual);
            if (FAILED(visual))
            {
                NoteStepFailure(FailureOp::CreateCompositionVisual, visual);
                return false;
            }
        }
        HRESULT result = m_compositionDevice->CreateTargetForHwnd(m_host, TRUE,
            &m_compositionTarget);
        if (FAILED(result))
        {
            NoteStepFailure(FailureOp::CreateCompositionTarget, result);
            return false;
        }
        result = m_compositionTarget->SetRoot(m_rootVisual.Get());
        if (FAILED(result))
        {
            NoteStepFailure(FailureOp::ApplyVisualTree, result);
            return false;
        }
        if (!CreateLayers()) return false;
        ApplyVisualTree();
        m_fade.Set(m_compositionDevice.Get(), m_rootVisual.Get(), m_requestedVisible, false);
        m_compositionDevice->Commit();
        return true;
    }

    bool NativeMediaRenderer::EnsureInitialized(std::uint64_t now)
    {
        if (m_ready) return true;
        if (!m_host || now < m_nextRetry) return false;
        if (CreateDevice())
        {
            m_ready = true;
            m_diagnostic.clear();
            m_retryAttempt = 0;
            m_nextRetry = 0;
            MarkAllDirty();
            return true;
        }
        return false;
    }

    bool NativeMediaRenderer::CreateDevice()
    {
        if (!m_host) return false;
        ReleaseDevice();
        const D3D_FEATURE_LEVEL levels[]{
            D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0,
            D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0
        };
        UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
        D3D_FEATURE_LEVEL level{};
        HRESULT result = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
            flags, levels, static_cast<UINT>(std::size(levels)), D3D11_SDK_VERSION,
            &m_d3dDevice, &level, nullptr);
        if (FAILED(result))
            result = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, flags,
                levels, static_cast<UINT>(std::size(levels)), D3D11_SDK_VERSION,
                &m_d3dDevice, &level, nullptr);
        if (FAILED(result))
        {
            Fail(FailureOp::CreateD3DDevice, L"Direct3D device creation failed.", result);
            ScheduleRetry(GetTickCount64(), result);
            return false;
        }
        result = m_d3dDevice.As(&m_dxgiDevice);
        if (FAILED(result))
        {
            Fail(FailureOp::GetDxgiDevice, L"DXGI device could not be obtained.", result);
            ScheduleRetry(GetTickCount64(), result);
            return false;
        }
        D2D1_FACTORY_OPTIONS options{};
        result = D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED,
            __uuidof(ID2D1Factory1), &options,
            reinterpret_cast<void**>(m_d2dFactory.GetAddressOf()));
        if (FAILED(result) ||
            FAILED(m_d2dFactory->CreateDevice(m_dxgiDevice.Get(), &m_d2dDevice)))
        {
            Fail(FailureOp::CreateD2DDevice, L"Direct2D device creation failed.", result);
            ScheduleRetry(GetTickCount64(), result);
            return false;
        }
        result = m_d2dDevice->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE,
            &m_context);
        if (FAILED(result))
        {
            Fail(FailureOp::CreateDeviceContext, L"Direct2D device context creation failed.", result);
            ScheduleRetry(GetTickCount64(), result);
            return false;
        }
        m_context->SetDpi(96.0f, 96.0f);
        result = DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED,
            __uuidof(IDWriteFactory),
            reinterpret_cast<IUnknown**>(m_writeFactory.GetAddressOf()));
        if (FAILED(result))
        {
            Fail(FailureOp::CreateWriteFactory, L"DirectWrite factory creation failed.", result);
            ScheduleRetry(GetTickCount64(), result);
            return false;
        }
        std::wstring painterError;
        if (!m_painter.Initialize(m_d2dFactory.Get(), m_d2dDevice.Get(),
            m_writeFactory.Get(), painterError))
        {
            Fail(FailureOp::InitializePainter, painterError, E_FAIL);
            ScheduleRetry(GetTickCount64(), E_FAIL);
            return false;
        }
        result = DCompositionCreateDevice(m_dxgiDevice.Get(),
            IID_PPV_ARGS(&m_compositionDevice));
        if (FAILED(result))
        {
            Fail(FailureOp::CreateCompositionDevice, L"DirectComposition device creation failed.", result);
            ScheduleRetry(GetTickCount64(), result);
            return false;
        }
        if (!CreateCompositionTarget())
        {
            // The step that really failed records its own HRESULT; result still holds
            // the successful DCompositionCreateDevice value and is the fallback.
            const FailureOp op = m_stepOp != FailureOp::None
                ? m_stepOp : FailureOp::CreateCompositionTarget;
            const HRESULT step = m_stepOp != FailureOp::None ? m_stepHr : result;
            m_stepOp = FailureOp::None;
            Fail(op, L"DirectComposition target creation failed.", step);
            ScheduleRetry(GetTickCount64(), E_FAIL);
            return false;
        }
        Log(L"Native media renderer initialized.", S_OK);
        return true;
    }

    void NativeMediaRenderer::ReleaseDevice()
    {
        ReleaseLayers();
        m_compositionTarget.Reset();
        m_rootVisual.Reset();
        m_compositionDevice.Reset();
        m_context.Reset();
        m_painter.ResetDeviceResources();
        m_writeFactory.Reset();
        m_d2dDevice.Reset();
        m_d2dFactory.Reset();
        m_dxgiDevice.Reset();
        m_d3dDevice.Reset();
        m_ready = false;
    }

    void NativeMediaRenderer::ReleaseLayers()
    {
        for (Layer* layer : { &m_background, &m_lyrics, &m_spectrum, &m_browser,
            &m_coverFront, &m_coverBack })
            layer->Reset();
    }

    bool NativeMediaRenderer::CreateLayers()
    {
        if (!m_compositionDevice || !m_rootVisual) return false;
        ComPtr<IDXGIAdapter> adapter;
        ComPtr<IDXGIFactory2> factory;
        HRESULT queryResult = m_dxgiDevice->GetAdapter(&adapter);
        if (SUCCEEDED(queryResult)) queryResult = adapter->GetParent(IID_PPV_ARGS(&factory));
        if (FAILED(queryResult))
        {
            NoteStepFailure(FailureOp::CreateSwapChain, queryResult);
            return false;
        }
        const UINT width = static_cast<UINT>((std::max)(1.0f, std::round(m_width)));
        const UINT height = static_cast<UINT>((std::max)(1.0f, std::round(m_height)));
        DXGI_SWAP_CHAIN_DESC1 description{};
        description.Width = width;
        description.Height = height;
        description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        description.SampleDesc.Count = 1;
        description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        description.BufferCount = SwapChainBuffers;
        description.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
        description.AlphaMode = DXGI_ALPHA_MODE_PREMULTIPLIED;
        description.Scaling = DXGI_SCALING_STRETCH;

        const auto create = [&](Layer& layer, bool above, float opacity)
        {
            HRESULT stepResult = m_compositionDevice->CreateVisual(&layer.visual);
            if (FAILED(stepResult))
            {
                NoteStepFailure(FailureOp::CreateCompositionVisual, stepResult);
                return false;
            }
            stepResult = factory->CreateSwapChainForComposition(m_d3dDevice.Get(),
                &description, nullptr, &layer.swapChain);
            if (FAILED(stepResult))
            {
                NoteStepFailure(FailureOp::CreateSwapChain, stepResult);
                return false;
            }
            layer.width = width;
            layer.height = height;
            layer.visual->SetContent(layer.swapChain.Get());
            SetLayerOpacity(layer, opacity);
            stepResult = m_rootVisual->AddVisual(layer.visual.Get(), above, nullptr);
            if (FAILED(stepResult))
            {
                NoteStepFailure(FailureOp::AddCompositionVisual, stepResult);
                return false;
            }
            return true;
        };
        if (!create(m_background, true, 1.0f)) return false;
        if (!create(m_lyrics, true, 1.0f)) return false;
        if (!create(m_spectrum, true, 0.0f)) return false;
        if (!create(m_browser, true, 0.0f)) return false;
        if (!create(m_coverFront, true, 1.0f)) return false;
        if (!create(m_coverBack, true, 0.0f)) return false;
        return true;
    }

    bool NativeMediaRenderer::ResizeLayers()
    {
        if (!m_ready) return false;
        const UINT width = static_cast<UINT>((std::max)(1.0f, std::round(m_width)));
        const UINT height = static_cast<UINT>((std::max)(1.0f, std::round(m_height)));
        for (Layer* layer : { &m_background, &m_lyrics, &m_spectrum, &m_browser,
            &m_coverFront, &m_coverBack })
        {
            if (!layer->swapChain) return false;
            layer->target.Reset();
            if (layer->width == width && layer->height == height) continue;
            const HRESULT result = layer->swapChain->ResizeBuffers(0, width, height,
                DXGI_FORMAT_UNKNOWN, 0);
            if (FAILED(result))
            {
                HandleDeviceLost(result, FailureOp::ResizeBuffers,
                    L"resizing media composition layers", layer);
                return false;
            }
            layer->width = width;
            layer->height = height;
            layer->hasContent = false;
        }
        return true;
    }

    void NativeMediaRenderer::SetLayerOpacity(Layer& layer, float opacity)
    {
        if (!m_compositionDevice || !layer.visual) return;
        ComPtr<IDCompositionEffectGroup> effect;
        if (SUCCEEDED(m_compositionDevice->CreateEffectGroup(&effect)) &&
            SUCCEEDED(effect->SetOpacity(opacity)))
            layer.visual->SetEffect(opacity >= 0.999f ? nullptr : effect.Get());
    }

    HRESULT NativeMediaRenderer::AnimateLayerOpacity(Layer& layer, float from, float to,
        int durationMs)
    {
        return AnimateOpacity(m_compositionDevice.Get(), layer.visual.Get(), from, to,
            m_reducedMotion ? 0 : durationMs);
    }

    void NativeMediaRenderer::ApplyVisualTree()
    {
        if (!m_rootVisual || !m_compositionTarget) return;
        // The visual tree is applied on every present; a failed call used to be
        // dropped silently. Only the failure count is recorded here so the render
        // path stays free of logging, allocation and locks.
        if (FAILED(m_compositionTarget->SetRoot(m_rootVisual.Get()))) ++m_failures.silentOps;
        m_fade.Set(m_compositionDevice.Get(), m_rootVisual.Get(), m_requestedVisible, false);
        if (FAILED(m_compositionDevice->Commit())) ++m_failures.silentOps;
    }

    Frame NativeMediaRenderer::FrameForLyrics() const
    {
        if (m_transition.active && m_transition.kind == TransitionKind::LayerSwitch &&
            m_pendingFrame.lyrics.scene == Scene::Spectrum)
            return m_transition.from;
        return m_pendingFrame;
    }

    bool NativeMediaRenderer::PaintLayer(Layer& layer,
        const std::function<void(ID2D1DeviceContext*)>& paint)
    {
        if (!m_context || !layer.swapChain || layer.width == 0 || layer.height == 0)
            return false;
        ComPtr<IDXGISurface> surface;
        HRESULT result = layer.swapChain->GetBuffer(0, IID_PPV_ARGS(&surface));
        if (FAILED(result))
        {
            HandleDeviceLost(result, FailureOp::AcquireBuffer,
                L"acquiring a composition buffer", &layer);
            return false;
        }
        if (!layer.target)
        {
            const auto properties = D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET |
                D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
                D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM,
                    D2D1_ALPHA_MODE_PREMULTIPLIED), 96.0f, 96.0f);
            result = m_context->CreateBitmapFromDxgiSurface(surface.Get(), properties,
                &layer.target);
            if (FAILED(result))
            {
                HandleDeviceLost(result, FailureOp::CreateTargetBitmap,
                    L"creating a composition target bitmap", &layer);
                return false;
            }
        }
        m_context->SetTarget(layer.target.Get());
        m_context->BeginDraw();
        m_context->Clear(D2D1::ColorF(0, 0, 0, 0));
        paint(m_context.Get());
        result = m_context->EndDraw();
        m_context->SetTarget(nullptr);
        if (FAILED(result))
        {
            // Report before the bitmap is released so the diagnostics can say that a
            // target was bound; the reset below still runs on every failing path.
            HandleDeviceLost(result, FailureOp::Draw, L"drawing media composition", &layer);
            layer.target.Reset();
            return false;
        }
        result = layer.swapChain->Present(0, 0);
        if (FAILED(result))
        {
            HandleDeviceLost(result, FailureOp::Present,
                L"presenting media composition", &layer);
            return false;
        }
        layer.hasContent = true;
        return true;
    }

    PaintState MakePaintState(const EffectiveStyle& style, const Frame& frame,
        const Cover* cover, const SpectrumTuning& tuning)
    {
        PaintState state;
        state.style = style;
        state.frame = frame;
        state.cover = cover;
        state.tuning = tuning;
        return state;
    }

    bool NativeMediaRenderer::PaintBackground()
    {
        const auto state = MakePaintState(m_effectiveStyle, FrameForLyrics(),
            m_cover ? &*m_cover : nullptr, m_tuning);
        const bool painted = PaintLayer(m_background, [&](ID2D1DeviceContext* context)
        {
            m_painter.PaintBackground(context, state, m_width, m_height, m_dpiScale);
        });
        if (painted) m_styleDirty = false;
        return painted;
    }

    bool NativeMediaRenderer::PaintLyricsLayer()
    {
        const Frame frame = FrameForLyrics();
        const auto state = MakePaintState(m_effectiveStyle, frame,
            m_cover ? &*m_cover : nullptr, m_tuning);
        LyricsPaint paint;
        paint.state = &state;
        paint.width = m_width;
        paint.height = m_height;
        paint.dpiScale = m_dpiScale;
        paint.wordScanProgress = m_displayedScan;
        paint.secondaryOpacity = m_secondaryOpacity;
        if (m_transition.active &&
            (m_transition.kind == TransitionKind::SingleRoll ||
                m_transition.kind == TransitionKind::TranslationRoll))
        {
            paint.transitionKind = m_transition.kind;
            paint.transitionFrom = &m_transition.from;
            paint.transitionProgress = m_transition.Progress(GetTickCount64());
        }
        const bool painted = PaintLayer(m_lyrics, [&](ID2D1DeviceContext* context)
        {
            m_painter.PaintLyrics(context, paint);
        });
        if (painted) m_lyricsDirty = false;
        return painted;
    }

    bool NativeMediaRenderer::PaintSpectrumLayer()
    {
        const auto state = MakePaintState(m_effectiveStyle, m_pendingFrame,
            m_cover ? &*m_cover : nullptr, m_tuning);
        SpectrumPaint paint;
        paint.state = &state;
        paint.width = m_width;
        paint.height = m_height;
        paint.dpiScale = m_dpiScale;
        paint.bars = &m_spectrumAnimator.Values();
        const bool painted = PaintLayer(m_spectrum, [&](ID2D1DeviceContext* context)
        {
            m_painter.PaintSpectrum(context, paint);
        });
        if (painted)
        {
            m_spectrumDirty = false;
            m_tuningDirty = false;
            if (!m_spectrumSignalLogged && std::any_of(m_spectrumAnimator.Values().begin(),
                m_spectrumAnimator.Values().end(), [](double value) { return value > 0.01; }))
            {
                m_spectrumSignalLogged = true;
                Log(L"Spectrum rendered with a nonzero audio signal.", S_OK);
            }
        }
        return painted;
    }

    bool NativeMediaRenderer::PaintBrowserLayer()
    {
        if (!m_pendingFrame.isBrowser || !m_pendingFrame.browser.active) return true;
        const auto state = MakePaintState(m_effectiveStyle, m_pendingFrame,
            m_cover ? &*m_cover : nullptr, m_tuning);
        BrowserPaint paint;
        paint.state = &state;
        paint.width = m_width;
        paint.height = m_height;
        paint.dpiScale = m_dpiScale;
        const bool painted = PaintLayer(m_browser, [&](ID2D1DeviceContext* context)
        {
            m_painter.PaintBrowser(context, paint);
        });
        if (painted) m_browserDirty = false;
        return painted;
    }

    bool NativeMediaRenderer::PaintCoverLayer(Layer& layer, const Cover* cover,
        std::uint64_t key)
    {
        Cover fallback;
        fallback.cleared = true;
        fallback.fallbackText = L"N";
        if (!cover) cover = &fallback;
        auto state = MakePaintState(m_effectiveStyle, m_pendingFrame, cover, m_tuning);
        CoverBitmap bitmap;
        // Decode covers at the physical edge they are drawn at, not at the source extent.
        const bool hasBitmap = m_painter.PrepareCover(*cover,
            static_cast<UINT>(std::lround((std::max)(1.0, m_effectiveStyle.coverSizePx))),
            bitmap);
        CoverPaint paint;
        paint.state = &state;
        paint.width = m_width;
        paint.height = m_height;
        paint.dpiScale = m_dpiScale;
        paint.image = hasBitmap ? bitmap.bitmap.Get() : nullptr;
        paint.cover = cover;
        const bool painted = PaintLayer(layer, [&](ID2D1DeviceContext* context)
        {
            m_painter.PaintCover(context, paint);
        });
        if (painted)
        {
            m_coverDirty = false;
            // Record the key the cached bitmap is actually stored under: a DPI or cover
            // size change decodes a different size, and keeping the older edge in
            // m_coverKey would make ReleaseCoverBitmapsExcept drop the on-screen bitmap.
            if (!m_coverFadeActive)
            {
                if (hasBitmap) m_coverKey = bitmap.key;
                else if (key) m_coverKey = key;
            }
        }
        return painted;
    }

    void NativeMediaRenderer::UpdateSecondaryOpacity(std::uint64_t now)
    {
        (void)now;
        const auto& frame = m_pendingFrame;
        if (frame.isBrowser || frame.lyrics.scene == Scene::Spectrum)
        {
            m_secondaryTarget = 1.0;
        }
        else if (frame.lyrics.translationMode)
        {
            m_secondaryTarget = 1.0;
        }
        else
        {
            m_secondaryTarget = 0.58 + (1.0 - Clamp01(frame.lyrics.progress)) * 0.16;
        }
        const double delta = m_secondaryTarget - m_secondaryOpacity;
        if (m_reducedMotion)
        {
            if (std::abs(delta) > 0.0001) m_lyricsDirty = true;
            m_secondaryOpacity = m_secondaryTarget;
            return;
        }
        if (std::abs(delta) > 0.002)
        {
            m_secondaryOpacity += delta * 0.28;
            m_lyricsDirty = true;
        }
        else
        {
            m_secondaryOpacity = m_secondaryTarget;
        }
    }

    void NativeMediaRenderer::UpdateSpectrum(std::uint64_t now)
    {
        const auto& frame = m_pendingFrame;
        if (frame.lyrics.scene != Scene::Spectrum || !frame.lyrics.isPlaying)
        {
            m_spectrumSignalLogged = false;
            if (m_spectrumDirty)
            {
                m_spectrumAnimator.SetTargets({}, false);
                m_spectrumAnimator.SettleNow();
            }
            return;
        }
        if (m_tuningDirty)
        {
            m_spectrumAnimator.Configure(m_tuning, m_spectrumValues, frame.lyrics.isPlaying);
            m_tuningDirty = false;
            m_spectrumDirty = true;
        }
        if (m_reducedMotion)
        {
            m_spectrumAnimator.SettleNow();
            m_spectrumDirty = true;
            return;
        }
        const double elapsed = m_lastPresent == 0 ? 1.0 / 60.0 :
            std::clamp((now - m_lastPresent) / 1000.0, 0.0, 0.1);
        if (m_spectrumAnimator.Tick(elapsed)) m_spectrumDirty = true;
    }

    bool NativeMediaRenderer::UpdateCover(std::uint64_t now)
    {
        if (!m_coverCrossPending) return false;
        m_coverCrossPending = false;
        const Cover* cover = m_cover ? &*m_cover : nullptr;
        Cover fallback;
        fallback.cleared = true;
        fallback.fallbackText = L"N";
        if (!cover) cover = &fallback;
        CoverBitmap bitmap;
        const bool decoded = m_painter.PrepareCover(*cover,
            static_cast<UINT>(std::lround((std::max)(1.0, m_effectiveStyle.coverSizePx))),
            bitmap);
        if (!decoded && !cover->dataUri.empty())
            Log(L"Media cover image could not be decoded; using its fallback.",
                E_FAIL);
        // The fallback derives exactly the key a successful decode would use (same
        // uri selection, decode edge and app-icon mix), so a later successful decode
        // of the same cover is a cache hit and the keep set never holds a key
        // outside the cache's key space.
        const std::uint64_t key = decoded ? bitmap.key : NativeMediaPainter::CoverCacheKey(*cover,
            static_cast<UINT>(std::lround((std::max)(1.0, m_effectiveStyle.coverSizePx))));
        if (!m_ready || !m_coverFront.visual || !m_coverBack.visual) return false;
        Layer& active = m_coverFrontIsActive ? m_coverFront : m_coverBack;
        Layer& standby = m_coverFrontIsActive ? m_coverBack : m_coverFront;
        if (decoded && key == m_coverKey && active.hasContent)
        {
            PaintCoverLayer(active, cover, key);
            return true;
        }
        if (!PaintCoverLayer(standby, cover, key)) return false;
        m_coverPendingKey = key;
        m_coverBackOpacity = 0.0f;
        m_coverFrontOpacity = 1.0f;
        SetLayerOpacity(standby, 0.0f);
        SetLayerOpacity(active, m_coverTargetOpacity);
        AnimateLayerOpacity(standby, 0.0f, m_coverTargetOpacity,
            m_reducedMotion ? 0 : 440);
        AnimateLayerOpacity(active, m_coverTargetOpacity, 0.0f,
            m_reducedMotion ? 0 : 440);
        m_coverFadeActive = true;
        m_coverFadeUntil = now + (m_reducedMotion ? 0 : 440);
        m_compositionDevice->Commit();
        return true;
    }

    void NativeMediaRenderer::CompleteCoverTransition(std::uint64_t now)
    {
        (void)now;
        const bool standbyWasFront = m_coverFrontIsActive;
        m_coverFrontIsActive = !m_coverFrontIsActive;
        SetLayerOpacity(standbyWasFront ? m_coverBack : m_coverFront, m_coverTargetOpacity);
        SetLayerOpacity(standbyWasFront ? m_coverFront : m_coverBack, 0.0f);
        if (m_compositionDevice) m_compositionDevice->Commit();
        m_coverKey = m_coverPendingKey;
        m_coverFadeActive = false;
        std::unordered_set<std::uint64_t> keep;
        if (m_coverKey) keep.insert(m_coverKey);
        m_painter.ReleaseCoverBitmapsExcept(keep);
        m_coverDirty = false;
    }

    void NativeMediaRenderer::BeginFrameTransition(const Frame& next, std::uint64_t now)
    {
        const Frame& previousTarget = m_transition.active
            ? m_transition.to : m_displayedFrame;
        if (m_transition.active && SameTransitionIdentity(previousTarget, next))
        {
            const bool contentChanged = !SameVisualContent(previousTarget, next);
            const bool playbackChanged = previousTarget.lyrics.isPlaying !=
                next.lyrics.isPlaying;
            m_transition.to = next;
            if (contentChanged)
            {
                m_lyricsDirty = true;
                m_frameDirty = true;
            }
            if (playbackChanged && next.lyrics.scene == Scene::Spectrum)
                m_spectrumDirty = true;
            return;
        }
        const bool browser = next.isBrowser && next.browser.active;
        const bool wasBrowser = previousTarget.isBrowser &&
            previousTarget.browser.active;
        const bool playbackChanged = previousTarget.lyrics.isPlaying !=
            next.lyrics.isPlaying;
        const bool contentChanged = !SameVisualContent(previousTarget, next);
        const bool sceneChanged = previousTarget.lyrics.scene != next.lyrics.scene ||
            previousTarget.isBrowser != next.isBrowser ||
            previousTarget.browser.active != next.browser.active;
        const auto plan = browser
            ? TransitionPlan{ TransitionKind::Immediate, 0 }
            : PlanTransition(previousTarget, next, next.lyrics.animateTransition,
                m_reducedMotion);
        if (plan.kind == TransitionKind::Immediate ||
            plan.kind == TransitionKind::ReplaceInPlace)
        {
            m_transition.Reset();
            m_displayedFrame = next;
            if (contentChanged)
            {
                m_lyricsDirty = true;
                m_releaseTextAfterPaint = true;
                m_secondaryOpacity = 0.72;
            }
        }
        else
        {
            m_transition.Begin(plan.kind, previousTarget, next, now, plan.durationMs);
            if (plan.kind == TransitionKind::LayerSwitch)
            {
                StartLayerTransition(next.lyrics.scene == Scene::Spectrum, now,
                    plan.durationMs);
            }
            m_lyricsDirty = true;
        }
        m_frameDirty = contentChanged;
        m_browserDirty = contentChanged && (browser || wasBrowser);
        m_spectrumDirty = m_spectrumDirty || sceneChanged ||
            (playbackChanged && next.lyrics.scene == Scene::Spectrum);
        const bool spectrum = !browser && next.lyrics.scene == Scene::Spectrum;
        m_lyricsTargetOpacity = (!browser && !spectrum) ? 1.0f : 0.0f;
        m_spectrumTargetOpacity = spectrum ? 1.0f : 0.0f;
        m_browserTargetOpacity = browser ? 1.0f : 0.0f;
        m_coverTargetOpacity = browser ? 0.0f : 1.0f;
        if (!m_transition.active)
        {
            SetLayerOpacity(m_lyrics, m_lyricsTargetOpacity);
            SetLayerOpacity(m_spectrum, m_spectrumTargetOpacity);
            SetLayerOpacity(m_browser, m_browserTargetOpacity);
            if (!m_coverFadeActive)
            {
                SetLayerOpacity(m_coverFrontIsActive ? m_coverFront : m_coverBack,
                    m_coverTargetOpacity);
                SetLayerOpacity(m_coverFrontIsActive ? m_coverBack : m_coverFront, 0.0f);
            }
        }
        if (m_compositionDevice) m_compositionDevice->Commit();
    }

    void NativeMediaRenderer::CompleteFrameTransition(std::uint64_t now)
    {
        (void)now;
        m_displayedFrame = m_transition.to;
        m_transition.Reset();
        m_lyricsDirty = true;
        m_browserDirty = true;
        m_releaseTextAfterPaint = true;
        if (m_compositionDevice) m_compositionDevice->Commit();
    }

    void NativeMediaRenderer::StartLayerTransition(bool toSpectrum, std::uint64_t now,
        int durationMs)
    {
        m_layerTransitionActive = true;
        m_layerTransitionUntil = now + static_cast<std::uint64_t>((std::max)(0, durationMs));
        if (!m_ready || !m_lyrics.visual || !m_spectrum.visual) return;
        const float duration = static_cast<float>((std::max)(0, durationMs)) / 1000.0f;
        (void)duration;
        if (toSpectrum)
        {
            AnimateLayerOpacity(m_lyrics, 1.0f, 0.0f, durationMs);
            AnimateLayerOpacity(m_spectrum, 0.0f, 1.0f, durationMs);
            AnimateOffsetY(m_compositionDevice.Get(), m_lyrics.visual.Get(), 0.0f,
                static_cast<float>(m_effectiveStyle.layerTransitionOffsetPx), durationMs);
            AnimateOffsetY(m_compositionDevice.Get(), m_spectrum.visual.Get(),
                static_cast<float>(m_effectiveStyle.layerTransitionOffsetPx), 0.0f, durationMs);
        }
        else
        {
            AnimateLayerOpacity(m_lyrics, 0.0f, 1.0f, durationMs);
            AnimateLayerOpacity(m_spectrum, 1.0f, 0.0f, durationMs);
            AnimateOffsetY(m_compositionDevice.Get(), m_lyrics.visual.Get(),
                -static_cast<float>(m_effectiveStyle.layerTransitionOffsetPx), 0.0f, durationMs);
            AnimateOffsetY(m_compositionDevice.Get(), m_spectrum.visual.Get(), 0.0f,
                -static_cast<float>(m_effectiveStyle.layerTransitionOffsetPx), durationMs);
        }
        SetLayerOpacity(m_browser, m_browserTargetOpacity);
        if (m_compositionDevice) m_compositionDevice->Commit();
    }

    void NativeMediaRenderer::CompleteLayerTransition(std::uint64_t now)
    {
        (void)now;
        m_layerTransitionActive = false;
        SetLayerOpacity(m_lyrics, m_lyricsTargetOpacity);
        SetLayerOpacity(m_spectrum, m_spectrumTargetOpacity);
        SetLayerOpacity(m_browser, m_browserTargetOpacity);
        if (m_lyrics.visual) m_lyrics.visual->SetOffsetY(0.0f);
        if (m_spectrum.visual) m_spectrum.visual->SetOffsetY(0.0f);
        if (m_compositionDevice) m_compositionDevice->Commit();
    }

    void NativeMediaRenderer::SetVisible(bool visible, bool animate)
    {
        const bool wasVisible = m_requestedVisible;
        if (m_requestedVisible == visible && m_ready) return;
        m_requestedVisible = visible;
        const bool revealing = visible && !wasVisible;
        // Reset the presentation state before touching composition resources: a
        // reveal may happen while layers are being recreated, and the retained
        // frame is the only complete snapshot to restore from.
        if (revealing) ResetSurfaceForReveal();
        if (!m_ready || !m_compositionDevice || !m_rootVisual) return;
        if (revealing)
        {
            SetLayerOpacity(m_lyrics, m_lyricsTargetOpacity);
            SetLayerOpacity(m_spectrum, m_spectrumTargetOpacity);
            SetLayerOpacity(m_browser, m_browserTargetOpacity);
            SetLayerOpacity(m_coverFrontIsActive ? m_coverFront : m_coverBack,
                m_coverTargetOpacity);
            SetLayerOpacity(m_coverFrontIsActive ? m_coverBack : m_coverFront, 0.0f);
            MarkAllDirty();
        }
        m_fade.Set(m_compositionDevice.Get(), m_rootVisual.Get(), visible,
            animate && !m_reducedMotion);
        m_compositionDevice->Commit();
    }

    void NativeMediaRenderer::ResetSurfaceForReveal()
    {
        m_transition.Reset();
        m_layerTransitionActive = false;
        m_coverFadeActive = false;
        m_coverCrossPending = false;
        m_coverKey = 0;
        m_displayedFrame = m_pendingFrame;
        // The interpolator and its input cache share one lifetime. Clearing only
        // the interpolator left the same revision/progress looking like "no new
        // signal", so the restored panel rendered an empty scan.
        InvalidateScanSignal();
        m_spectrumAnimator.SettleNow();
        SetEffectiveScene();
    }

    void NativeMediaRenderer::InvalidateScanSignal()
    {
        m_displayedScan.reset();
        m_wordScan.Reset();
        m_scanInitialized = false;
        m_scanTarget.reset();
        m_scanTrack.clear();
        m_scanLine = -1;
        m_scanPlaying = false;
    }

    bool NativeMediaRenderer::HitTest(float x, float y, HitAction& action)
    {
        action = HitAction::None;
        if (!m_hasState || !m_ready || !m_requestedVisible) return false;
        if (m_pendingFrame.isBrowser && m_pendingFrame.browser.active)
        {
            const auto state = MakePaintState(m_effectiveStyle, m_pendingFrame,
                m_cover ? &*m_cover : nullptr, m_tuning);
            BrowserPaint paint;
            paint.state = &state;
            paint.width = m_width;
            paint.height = m_height;
            paint.dpiScale = m_dpiScale;
            if (m_painter.HitTest(paint, x, y, action)) return true;
        }
        action = HitAction::Panel;
        return true;
    }

    FootprintSnapshot NativeMediaRenderer::Footprint() const
    {
        const Layer* const all[] = { &m_background, &m_lyrics, &m_spectrum,
            &m_browser, &m_coverFront, &m_coverBack };
        FootprintSnapshot snapshot;
        for (const Layer* layer : all)
        {
            if (layer->visual) ++snapshot.layers;
            if (layer->swapChain) ++snapshot.swapChains;
        }
        snapshot.textLayouts = m_painter.TextLayoutCount();
        snapshot.textFormats = m_painter.TextFormatCount();
        snapshot.brushes = m_painter.BrushCount();
        snapshot.covers = m_painter.CoverCount();
        snapshot.coverBytes = m_painter.CoverCacheBytes();
        return snapshot;
    }

    bool NativeMediaRenderer::Present(std::uint64_t now)
    {
        if (!m_hasState) return false;
        if (!m_headlessTest && !m_ready) return false;
        if (!m_headlessTest && !m_requestedVisible && !m_fade.Active())
        {
            m_framePending = false;
            m_displayedFrame = m_pendingFrame;
            m_transition.Reset();
            m_layerTransitionActive = false;
            m_coverCrossPending = false;
            m_coverFadeActive = false;
            m_animationActive = false;
            m_lastPresent = now;
            return false;
        }
        bool animation = false;
        if (m_framePending)
        {
            m_framePending = false;
            BeginFrameTransition(m_pendingFrame, now);
        }
        if (m_transition.active)
        {
            if (m_transition.Finished(now)) CompleteFrameTransition(now);
            else animation = true;
        }
        if (m_layerTransitionActive)
        {
            if (now >= m_layerTransitionUntil) CompleteLayerTransition(now);
            else animation = true;
        }
        const bool rollActive = m_transition.active &&
            (m_transition.kind == TransitionKind::SingleRoll ||
                m_transition.kind == TransitionKind::TranslationRoll);
        if (rollActive)
        {
            // A roll is authored by the painter, not by a Composition
            // animation, so it must receive a fresh frame on every tick.
            m_lyricsDirty = true;
        }
        const auto& frame = m_pendingFrame;
        std::optional<double> targetScan;
        if (frame.lyrics.hasWordScanProgress)
            targetScan = Clamp01(frame.lyrics.wordScanProgress);
        if (frame.lyrics.scene == Scene::Spectrum || frame.isBrowser)
            targetScan.reset();
        const auto trackId = EffectiveTrackId(frame);
        const auto previousScan = m_displayedScan;
        const bool scanSignalChanged = !m_scanInitialized ||
            m_scanTrack != trackId || m_scanLine != frame.lyrics.currentLineIndex ||
            m_scanPlaying != frame.lyrics.isPlaying ||
            m_scanTarget != targetScan;
        if (scanSignalChanged)
        {
            m_scanInitialized = true;
            m_scanTrack = trackId;
            m_scanLine = frame.lyrics.currentLineIndex;
            m_scanPlaying = frame.lyrics.isPlaying;
            m_scanTarget = targetScan;
            if (m_reducedMotion)
            {
                // Reduced motion never interpolates, so the interpolator stays
                // empty and must not be read back on later frames.
                m_wordScan.Reset();
                m_displayedScan = targetScan;
            }
            else
            {
                m_wordScan.Observe(trackId, frame.lyrics.currentLineIndex,
                    frame.lyrics.isPlaying, targetScan, now);
                m_displayedScan = m_wordScan.Current(now);
            }
        }
        else if (m_reducedMotion)
        {
            m_displayedScan = targetScan;
        }
        else
        {
            m_displayedScan = m_wordScan.Current(now);
        }
        if (previousScan != m_displayedScan)
        {
            m_lyricsDirty = true;
        }
        if (m_wordScan.Active(now)) animation = true;

        UpdateSecondaryOpacity(now);
        if (std::abs(m_secondaryTarget - m_secondaryOpacity) > 0.002) animation = true;
        UpdateSpectrum(now);
        if (m_spectrumDirty && frame.lyrics.scene == Scene::Spectrum) animation = true;
        if (m_coverCrossPending && !m_headlessTest) UpdateCover(now);
        if (m_coverFadeActive)
        {
            if (now >= m_coverFadeUntil) CompleteCoverTransition(now);
            else animation = true;
        }
        if (m_fade.Active()) animation = true;

        if (!m_headlessTest)
        {
            m_painter.BeginPaintFrame();
            bool painted = false;
            bool textPainted = false;
            bool paintOk = true;
            const bool geometryDirty = m_geometryDirty || m_styleDirty;
            const bool spectrumScene = !frame.isBrowser &&
                frame.lyrics.scene == Scene::Spectrum;
            if (geometryDirty)
            {
                if (PaintBackground()) painted = true;
                else paintOk = false;
            }
            if (geometryDirty || m_frameDirty ||
                (m_lyricsDirty && (m_lyricsTargetOpacity > 0.001f || m_transition.active ||
                    (m_layerTransitionActive && m_spectrumTargetOpacity > 0.001f))))
            {
                if (PaintLyricsLayer())
                {
                    painted = true;
                    textPainted = true;
                    m_frameDirty = false;
                }
                else paintOk = false;
            }
            if (geometryDirty || (m_spectrumDirty && (spectrumScene || m_layerTransitionActive)))
            {
                if (PaintSpectrumLayer()) painted = true;
                else paintOk = false;
            }
            if (m_browserDirty || geometryDirty)
            {
                if (PaintBrowserLayer())
                {
                    painted = true;
                    textPainted = true;
                }
                else paintOk = false;
            }
            if ((m_coverDirty || geometryDirty) && !m_coverFadeActive)
            {
                if (PaintCoverLayer(m_coverFrontIsActive ? m_coverFront : m_coverBack,
                    m_cover ? &*m_cover : nullptr, m_coverKey))
                {
                    painted = true;
                    textPainted = true;
                }
                else paintOk = false;
            }
            if (paintOk) m_geometryDirty = false;
            m_painter.EndPaintFrame();
            if (m_releaseTextAfterPaint && textPainted)
            {
                m_painter.ReleaseInactiveTextLayouts();
                m_releaseTextAfterPaint = false;
            }
            if (painted && m_compositionDevice) m_compositionDevice->Commit();
        }
        m_lastPresent = now;
        m_animationActive = animation || m_transition.active || m_layerTransitionActive ||
            m_coverFadeActive || m_fade.Active();
        return m_animationActive;
    }

    bool NativeMediaRenderer::IsDeviceLost(HRESULT result) const
    {
        return IsDeviceLostResult(result);
    }

    void NativeMediaRenderer::HandleDeviceLost(HRESULT result, FailureOp op,
        const wchar_t* operation, const Layer* layer)
    {
        // Consume a pending step exactly like Fail(): it names the real failing
        // operation and HRESULT and must not outlive its own failure path.
        if (m_stepOp != FailureOp::None)
        {
            op = m_stepOp;
            result = m_stepHr;
            m_stepOp = FailureOp::None;
        }
        const bool lost = IsDeviceLost(result);
        // A repeat inside the dedup window returns before any string is built, so a
        // failing frame still allocates nothing.
        if (RecordFailure(op, result, layer))
        {
            const std::wstring message = std::wstring(lost
                ? L"Native media device was lost while "
                : L"Native media rendering failed while ") + operation;
            m_diagnostic = message;
            Log(message + L" (" + FormatHResult(result) + L")" + FailureContext(), result);
        }
        if (!lost) return;
        ReleaseDevice();
        ScheduleRetry(GetTickCount64(), result);
        if (m_compositionTarget) m_compositionTarget->SetRoot(nullptr);
    }

    void NativeMediaRenderer::ScheduleRetry(std::uint64_t now, HRESULT result)
    {
        m_ready = false;
        ++m_retryAttempt;
        const std::uint64_t delay = (std::min)(30000ull,
            500ull << (std::min)(m_retryAttempt - 1, 6u));
        m_nextRetry = now + delay;
        Log(L"Native media renderer retry scheduled in " +
            std::to_wstring(delay) + L" ms.", result);
    }

    bool NativeMediaRenderer::LayerNeedsPaint(Layer& layer) const
    {
        return !layer.hasContent;
    }

    void NativeMediaRenderer::SetEffectiveScene()
    {
        const bool browser = m_pendingFrame.isBrowser && m_pendingFrame.browser.active;
        const bool spectrum = !browser && m_pendingFrame.lyrics.scene == Scene::Spectrum;
        m_lyricsTargetOpacity = (!browser && !spectrum) ? 1.0f : 0.0f;
        m_spectrumTargetOpacity = spectrum ? 1.0f : 0.0f;
        m_browserTargetOpacity = browser ? 1.0f : 0.0f;
        m_coverTargetOpacity = browser ? 0.0f : 1.0f;
    }
}
