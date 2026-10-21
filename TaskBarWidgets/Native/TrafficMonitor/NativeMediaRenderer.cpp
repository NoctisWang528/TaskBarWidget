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

        bool IsDeviceLostResult(HRESULT result)
        {
            return result == DXGI_ERROR_DEVICE_REMOVED || result == DXGI_ERROR_DEVICE_RESET ||
                result == D2DERR_RECREATE_TARGET || result == D2DERR_WRONG_STATE ||
                result == static_cast<HRESULT>(0x8899000CUL);
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

    NativeMediaRenderer::~NativeMediaRenderer()
    {
        DetachHost();
    }

    void NativeMediaRenderer::Log(const std::wstring& message, HRESULT result)
    {
        if (m_logger) m_logger(message, result);
    }

    void NativeMediaRenderer::Fail(const std::wstring& message, HRESULT result)
    {
        m_diagnostic = message;
        Log(message + L" (" + FormatHResult(result) + L")", result);
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
        m_wordScan.Reset();
        m_displayedScan.reset();
        m_scanTrack.clear();
        m_scanLine = -1;
        m_scanPlaying = false;
        m_scanInitialized = false;
        m_scanTarget.reset();
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
        m_reducedMotion = reducedMotion;
        m_highContrast = highContrast;
        ComputeEffectiveStyle();
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
        if (!m_rootVisual && FAILED(m_compositionDevice->CreateVisual(&m_rootVisual)))
            return false;
        HRESULT result = m_compositionDevice->CreateTargetForHwnd(m_host, TRUE,
            &m_compositionTarget);
        if (FAILED(result)) return false;
        result = m_compositionTarget->SetRoot(m_rootVisual.Get());
        if (FAILED(result)) return false;
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
            Fail(L"Direct3D device creation failed.", result);
            ScheduleRetry(GetTickCount64(), result);
            return false;
        }
        result = m_d3dDevice.As(&m_dxgiDevice);
        if (FAILED(result))
        {
            Fail(L"DXGI device could not be obtained.", result);
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
            Fail(L"Direct2D device creation failed.", result);
            ScheduleRetry(GetTickCount64(), result);
            return false;
        }
        result = m_d2dDevice->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE,
            &m_context);
        if (FAILED(result))
        {
            Fail(L"Direct2D device context creation failed.", result);
            ScheduleRetry(GetTickCount64(), result);
            return false;
        }
        m_context->SetDpi(96.0f, 96.0f);
        result = DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED,
            __uuidof(IDWriteFactory),
            reinterpret_cast<IUnknown**>(m_writeFactory.GetAddressOf()));
        if (FAILED(result))
        {
            Fail(L"DirectWrite factory creation failed.", result);
            ScheduleRetry(GetTickCount64(), result);
            return false;
        }
        std::wstring painterError;
        if (!m_painter.Initialize(m_d2dFactory.Get(), m_d2dDevice.Get(),
            m_writeFactory.Get(), painterError))
        {
            Fail(painterError, E_FAIL);
            ScheduleRetry(GetTickCount64(), E_FAIL);
            return false;
        }
        result = DCompositionCreateDevice(m_dxgiDevice.Get(),
            IID_PPV_ARGS(&m_compositionDevice));
        if (FAILED(result))
        {
            Fail(L"DirectComposition device creation failed.", result);
            ScheduleRetry(GetTickCount64(), result);
            return false;
        }
        if (!CreateCompositionTarget())
        {
            Fail(L"DirectComposition target creation failed.", result);
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
        if (FAILED(m_dxgiDevice->GetAdapter(&adapter)) ||
            FAILED(adapter->GetParent(IID_PPV_ARGS(&factory))))
            return false;
        const UINT width = static_cast<UINT>((std::max)(1.0f, std::round(m_width)));
        const UINT height = static_cast<UINT>((std::max)(1.0f, std::round(m_height)));
        DXGI_SWAP_CHAIN_DESC1 description{};
        description.Width = width;
        description.Height = height;
        description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        description.SampleDesc.Count = 1;
        description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        description.BufferCount = 2;
        description.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
        description.AlphaMode = DXGI_ALPHA_MODE_PREMULTIPLIED;
        description.Scaling = DXGI_SCALING_STRETCH;

        const auto create = [&](Layer& layer, bool above, float opacity)
        {
            if (FAILED(m_compositionDevice->CreateVisual(&layer.visual))) return false;
            if (FAILED(factory->CreateSwapChainForComposition(m_d3dDevice.Get(),
                &description, nullptr, &layer.swapChain)))
                return false;
            layer.width = width;
            layer.height = height;
            layer.visual->SetContent(layer.swapChain.Get());
            SetLayerOpacity(layer, opacity);
            if (FAILED(m_rootVisual->AddVisual(layer.visual.Get(), above, nullptr)))
                return false;
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
                HandleDeviceLost(result, L"resizing media composition layers");
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
        m_compositionTarget->SetRoot(m_rootVisual.Get());
        m_fade.Set(m_compositionDevice.Get(), m_rootVisual.Get(), m_requestedVisible, false);
        m_compositionDevice->Commit();
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
            HandleDeviceLost(result, L"acquiring a composition buffer");
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
                HandleDeviceLost(result, L"creating a composition target bitmap");
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
            layer.target.Reset();
            HandleDeviceLost(result, L"drawing media composition");
            return false;
        }
        result = layer.swapChain->Present(0, 0);
        if (FAILED(result))
        {
            HandleDeviceLost(result, L"presenting media composition");
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

    bool NativeMediaRenderer::PaintCoverLayer(bool standby, const Cover* cover,
        std::uint64_t key)
    {
        Cover fallback;
        fallback.cleared = true;
        fallback.fallbackText = L"N";
        if (!cover) cover = &fallback;
        auto state = MakePaintState(m_effectiveStyle, m_pendingFrame, cover, m_tuning);
        CoverBitmap bitmap;
        const bool hasBitmap = m_painter.PrepareCover(*cover, m_dpiScale, bitmap);
        CoverPaint paint;
        paint.state = &state;
        paint.width = m_width;
        paint.height = m_height;
        paint.dpiScale = m_dpiScale;
        paint.image = hasBitmap ? bitmap.bitmap.Get() : nullptr;
        paint.cover = cover;
        Layer& layer = standby ? m_coverBack : m_coverFront;
        const bool painted = PaintLayer(layer, [&](ID2D1DeviceContext* context)
        {
            m_painter.PaintCover(context, paint);
        });
        if (painted)
        {
            m_coverDirty = false;
            if (!standby && !m_coverFadeActive && key) m_coverKey = key;
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
        const bool decoded = m_painter.PrepareCover(*cover, m_dpiScale, bitmap);
        if (!decoded && !cover->dataUri.empty())
            Log(L"Media cover image could not be decoded; using its fallback.",
                E_FAIL);
        const std::uint64_t key = decoded ? bitmap.key : NativeMediaPainter::HashText(
            cover->dataUri.empty() ? cover->fallbackIconDataUri : cover->dataUri);
        if (!m_ready || !m_coverFront.visual || !m_coverBack.visual) return false;
        if (decoded && key == m_coverKey && m_coverFront.hasContent)
        {
            PaintCoverLayer(false, cover, key);
            return true;
        }
        const bool standby = m_coverFrontIsActive;
        PaintCoverLayer(standby, cover, key);
        m_coverPendingKey = key;
        m_coverBackOpacity = 0.0f;
        m_coverFrontOpacity = 1.0f;
        SetLayerOpacity(standby ? m_coverBack : m_coverFront, 0.0f);
        SetLayerOpacity(standby ? m_coverFront : m_coverBack, 1.0f);
        AnimateLayerOpacity(standby ? m_coverBack : m_coverFront, 0.0f, 1.0f,
            m_reducedMotion ? 0 : 440);
        AnimateLayerOpacity(standby ? m_coverFront : m_coverBack, 1.0f, 0.0f,
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
        SetLayerOpacity(standbyWasFront ? m_coverBack : m_coverFront, 1.0f);
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
            AnimateLayerOpacity(m_coverFrontIsActive ? m_coverFront : m_coverBack,
                m_coverTargetOpacity, m_coverTargetOpacity, 0);
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
        if (!m_ready || !m_compositionDevice || !m_rootVisual) return;
        if (visible && !wasVisible)
        {
            m_transition.Reset();
            m_layerTransitionActive = false;
            m_coverFadeActive = false;
            m_coverCrossPending = false;
            m_coverKey = 0;
            m_displayedFrame = m_pendingFrame;
            m_displayedScan.reset();
            m_wordScan.Reset();
            m_spectrumAnimator.SettleNow();
            SetEffectiveScene();
            SetLayerOpacity(m_lyrics, m_lyricsTargetOpacity);
            SetLayerOpacity(m_spectrum, m_spectrumTargetOpacity);
            SetLayerOpacity(m_browser, m_browserTargetOpacity);
            SetLayerOpacity(m_coverFrontIsActive ? m_coverFront : m_coverBack,
                m_coverTargetOpacity);
            MarkAllDirty();
        }
        m_fade.Set(m_compositionDevice.Get(), m_rootVisual.Get(), visible,
            animate && !m_reducedMotion);
        m_compositionDevice->Commit();
    }

    bool NativeMediaRenderer::HitTest(float x, float y, HitAction& action)
    {
        action = HitAction::None;
        if (!m_hasState || !m_ready) return false;
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
            if (m_reducedMotion && targetScan)
            {
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
                if (PaintCoverLayer(m_coverFrontIsActive,
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

    void NativeMediaRenderer::HandleDeviceLost(HRESULT result, const wchar_t* operation)
    {
        if (!IsDeviceLost(result))
        {
            Fail(std::wstring(L"Native media rendering failed while ") + operation, result);
            return;
        }
        Fail(std::wstring(L"Native media device was lost while ") + operation, result);
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
