#include "CardPch.h"
#include "CardBackdropCapture.h"
#include "CardWindow.h"
#include "CardDraw.h"
#include "CardText.h"
#include "CardMotion.h"
#include "CardMaterial.h"
#include <dwmapi.h>
#include <tuple>

using Microsoft::WRL::ComPtr;
using namespace winrt::Windows::UI::Composition;

namespace Cards
{
    namespace
    {
        constexpr wchar_t ClassName[] = L"TaskBarWidgetsCardWindow";
        constexpr int AnimationMilliseconds = 150;
        constexpr UINT_PTR AnimationTimer = 0xCD01;
        constexpr UINT PageMotionCompleted = WM_APP + 0x217;
        std::atomic<UINT_PTR> pageGeneration{};
        constexpr UINT BackdropChanged = WM_APP + 0x215;
        constexpr ULONGLONG CoverFadeMilliseconds = 200;
        constexpr ULONGLONG LyricWrapMilliseconds = 180;
        constexpr ULONGLONG HoverMilliseconds = 140;

        bool SameSources(const MediaState& a, const MediaState& b)
        {
            if (a.sources.size() != b.sources.size()) return false;
            for (size_t i = 0; i < a.sources.size(); ++i)
            {
                const auto& x = a.sources[i]; const auto& y = b.sources[i];
                if (std::tie(x.id, x.label, x.iconDataUrl) != std::tie(y.id, y.label, y.iconDataUrl)) return false;
            }
            return true;
        }

        auto StableMedia(const MediaState& media)
        {
            return std::tie(media.title, media.artist, media.source, media.selectedSourceId,
                media.isPlaying, media.canPrevious, media.canNext, media.canPlayPause);
        }

        auto VisibleLyrics(const LyricsState& lyrics)
        {
            return std::tie(lyrics.trackId, lyrics.available, lyrics.isPlaying, lyrics.isPureMusic,
                lyrics.translationMode, lyrics.animateTransition, lyrics.currentLineIndex,
                lyrics.scene, lyrics.current, lyrics.next, lyrics.currentTranslation,
                lyrics.fontFamily, lyrics.fontSize, lyrics.fontWeight, lyrics.fontItalic,
                lyrics.fontUnderline, lyrics.fontStrikeOut, lyrics.primaryColor, lyrics.secondaryColor,
                lyrics.translationColor, lyrics.scanColor, lyrics.textShadow, lyrics.textAlignment,
                lyrics.layoutScalePercent);
        }

        LRESULT CALLBACK CardProc(HWND window, UINT message, WPARAM keys, LPARAM position)
        {
            auto* card = reinterpret_cast<CardWindow*>(GetWindowLongPtrW(window, GWLP_USERDATA));
            if (!card) return DefWindowProcW(window, message, keys, position);
            switch (message)
            {
            case PageMotionCompleted:
                card->OnPageMotionCompleted(keys);
                return 0;
            case MotionCompleted:
                card->OnMotionCompleted(keys);
                return 0;
            case BackdropChanged:
                card->RefreshBackdropConfiguration();
                return 0;
            case WM_ACTIVATE:
            case WM_NCACTIVATE:
                // Run after the HWND backdrop policy has handled activation.
                PostMessageW(window, BackdropChanged, 0, 0);
                break;
            case WM_ERASEBKGND:
                return 1;
            case WM_PAINT:
            {
                PAINTSTRUCT paint{};
                BeginPaint(window, &paint);
                EndPaint(window, &paint);
                if (card->Visible()) card->Paint();
                return 0;
            }
            case WM_MOUSEMOVE:
            case WM_LBUTTONDOWN:
            case WM_LBUTTONUP:
            case WM_LBUTTONDBLCLK:
            {
                POINT point{ GET_X_LPARAM(position), GET_Y_LPARAM(position) };
                ClientToScreen(window, &point);
                const bool left = message == WM_LBUTTONDOWN || message == WM_LBUTTONDBLCLK ||
                    (message == WM_MOUSEMOVE && (keys & MK_LBUTTON) != 0);
                card->OnPointer(message, point, left);
                return 0;
            }
            case WM_MOUSELEAVE:
                card->OnMouseLeave();
                return 0;
            case WM_MOUSEWHEEL:
                if (card->Host() && card->Host()->OnWheel(
                    { GET_X_LPARAM(position), GET_Y_LPARAM(position) }, GET_WHEEL_DELTA_WPARAM(keys))) return 0;
                break;
            case WM_CAPTURECHANGED:
            case WM_CANCELMODE:
                card->CancelPointer();
                return 0;
            case WM_CLOSE:
                if (card->Host()) card->Host()->OnHideCards(L"close");
                return 0;
            case WM_GETOBJECT:
            {
                const LRESULT result = card->OnGetObject(keys, position);
                if (result != 0) return result;
                break;
            }
            case WM_SETCURSOR:
                SetCursor(LoadCursorW(nullptr, IDC_ARROW));
                return TRUE;
            case WM_KEYDOWN:
            case WM_SYSKEYDOWN:
                if (card->OnKey(static_cast<UINT>(keys), (GetKeyState(VK_CONTROL) & 0x8000) != 0,
                    (GetKeyState(VK_SHIFT) & 0x8000) != 0))
                    return 0;
                break;
            case WM_TIMER:
                if (keys == AnimationTimer) card->Tick();
                return 0;
            case WM_DPICHANGED:
                if (card->Host()) card->Host()->OnLayoutInvalidated();
                card->Invalidate();
                return 0;
            default:
                break;
            }
            return DefWindowProcW(window, message, keys, position);
        }

        void EnsureClass(HINSTANCE instance)
        {
            static bool registered = false;
            if (registered) return;
            WNDCLASSEXW definition{ sizeof(definition) };
            definition.style = CS_HREDRAW | CS_VREDRAW;
            definition.lpfnWndProc = CardProc;
            definition.hInstance = instance;
            definition.hCursor = LoadCursorW(nullptr, IDC_ARROW);
            definition.lpszClassName = ClassName;
            RegisterClassExW(&definition);
            registered = true;
        }

        template <typename T>
        winrt::com_ptr<T> Interop(const winrt::Windows::Foundation::IUnknown& value)
        {
            winrt::com_ptr<T> result;
            winrt::check_hresult(value.as(winrt::guid_of<T>(), result.put_void()));
            return result;
        }

        SpriteVisual MakeLayer(const Compositor& compositor)
        {
            auto visual = compositor.CreateSpriteVisual();
            visual.RelativeSizeAdjustment(winrt::Windows::Foundation::Numerics::float2{ 1.0f, 1.0f });
            return visual;
        }

        muxc::MicaKind MicaKindFor(bool dark) { return dark ? muxc::MicaKind::BaseAlt : muxc::MicaKind::Base; }

        winrt::Windows::UI::Color FallbackColor(bool dark)
        {
            return dark ? winrt::Windows::UI::Color{ 255, 32, 32, 32 }
                : winrt::Windows::UI::Color{ 255, 243, 243, 243 };
        }
    }

    struct CardWindow::Impl
    {
        Compositor compositor{ nullptr };
        winrt::Windows::UI::Composition::Desktop::DesktopWindowTarget target{ nullptr };
        ContainerVisual root{ nullptr };
        CompositionRoundedRectangleGeometry corner{ nullptr };
        ContainerVisual motion{ nullptr };
        CardMotion transition;
        SpriteVisual wash{ nullptr };
        SpriteVisual rim{ nullptr };
        LiquidGlassBrush glass;
        CaptureExclusion captureExclusion;
        // While a menu covers this card the HWND joins the capture so the menu
        // refracts real card content; the card's own glass stays frozen. After
        // re-exclusion it waits for two newer frames that no longer contain it.
        bool captureSuspended{};
        bool resumePending{};
        uint64_t resumeSerial{};
        GlassControls glassControls;
        std::array<std::vector<GlassControl>, 2> controlDescriptions;
        bool glassControlsAttached{}, glassControlsFailed{};
        bool glassActive{};
        bool effectsAllowed{ true };
        SpriteVisual content{ nullptr };
        DrawingSurface surface;
        struct PeripheralNameLayer
        {
            Paint::PeripheralName layout;
            DrawingSurface surface;
            ContainerVisual viewport{ nullptr };
            SpriteVisual text{ nullptr };
            ScalarKeyFrameAnimation animation{ nullptr };
            bool running{};
        };
        std::vector<PeripheralNameLayer> peripheralNames;
        std::vector<PeripheralNameLayer> outgoingNames;
        float peripheralNameScale{};
        SpriteVisual dynamicContent{ nullptr };
        DrawingSurface dynamicSurface;
        ContainerVisual pageViewport{ nullptr };
        SpriteVisual outgoingPage{ nullptr };
        DrawingSurface outgoingSurface;
        CompositionScopedBatch pageBatch{ nullptr };
        winrt::event_token pageCompleted{};
        UINT_PTR pageToken{};
        SurfaceHandle boundDynamicSurface{ nullptr };
        bool mediaStaticDirty{ true };
        bool mediaRegionsDirty{ true };
        SurfaceHandle boundSurface{ nullptr };
        SIZE reflowSurfaceSize{};
        UINT timerInterval{};
        muxc::MicaController mica{ nullptr };
        muxc::SystemBackdropConfiguration micaConfiguration{ nullptr };
        bool micaReady{ false };
        bool dirty{ true };
        int hotRegion{ 0 };
        struct HoverFade
        {
            float from{}, to{};
            ULONGLONG started{};

            float Value(ULONGLONG now) const
            {
                if (!started) return to;
                const float t = std::clamp(static_cast<float>(now - started) / HoverMilliseconds, 0.0f, 1.0f);
                const float remaining = 1.0f - t;
                return from + (to - from) * (1.0f - remaining * remaining * remaining);
            }
        };
        std::map<int, HoverFade> hover;
        int focusedRegion{ 0 };
        bool keyboardFocus{};
        int capturedRegion{ 0 };
        bool trackingLeave{ false };
        bool dragging{ false };
        // A dragged slider shows the pointer value immediately; the snapshot only
        // wins again after the gesture ends, so a stale reply cannot pull it back.
        int sliderId{};
        std::wstring sliderAction;
        float sliderRatio{};
        float sliderPointerOffset{};
        bool sliderSeeking{};
        ULONGLONG lastSliderSent{};
        ULONGLONG sliderPendingUntil{};
        double mediaPosition{};
        ULONGLONG mediaPositionTick{};
        ULONGLONG lastWeatherAlert{};
        // Lyric scanning is interpolated on the card thread: a line frame carries
        // the position within that line, so the card learns the rate from two
        // consecutive frames and advances the highlight until the next one.
        long long lyricsRevision{ -1 };
        double lyricsProgress{};
        double lyricsRate{};
        double scanProgress{ -1 };
        double scanRate{};
        ULONGLONG scanTick{};
        ULONGLONG lastLyricPaint{};
        // Cover cross-fade: the outgoing bitmap is released as soon as the fade ends.
        std::wstring coverKey;
        std::wstring previousCoverKey;
        std::wstring previousCoverData;
        ULONGLONG coverFadeStart{};
        // Wrap transition state for the lyric rows.
        std::wstring lyricPrevious;
        std::wstring lyricPreviousSecondary;
        ULONGLONG lyricWrapStart{};
        int lyricLineIndex{ -1 };
        ULONGLONG lyricLineStarted{};
        ULONGLONG lyricsTick{};
    };

    CardWindow::CardWindow(Kind cardKind, CardHostServices& services)
        : impl(std::make_unique<Impl>()), kind(cardKind), host(services)
    {
    }

    CardWindow::~CardWindow() { Destroy(); }

    bool CardWindow::Create(const CardEnvironment& cardEnvironment, HINSTANCE instance)
    {
        environment = cardEnvironment;
        impl->glassControlsFailed = false;
        EnsureClass(instance);
        const DWORD style = WS_POPUP;
        const DWORD extended = WS_EX_TOOLWINDOW | WS_EX_TOPMOST |
            WS_EX_NOREDIRECTIONBITMAP;
        window = CreateWindowExW(extended, ClassName, L"", style, 0, 0,
            static_cast<int>(Layout::CardWidth), static_cast<int>(Layout::HeightMedia),
            nullptr, nullptr, instance, nullptr);
        if (!window) return false;
        SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(this));
        const DWM_WINDOW_CORNER_PREFERENCE preference = DWMWCP_ROUND;
        DwmSetWindowAttribute(window, DWMWA_WINDOW_CORNER_PREFERENCE, &preference, sizeof(preference));
        BuildVisualTree(instance);
        ApplyEnvironment(cardEnvironment);
        if (!impl->compositor) return false;
        accessibility.Attach(window, AccessibleSummary(), [this] { return std::cref(regions); },
            [this] { return scale; },
            [this](int id)
            {
                const auto* region = regions.Find(id);
                if (!region) return 0.0;
                if (region->action == L"seek") return snapshot.media.duration > 0 ? snapshot.media.position / snapshot.media.duration : 0.0;
                const auto& endpoints = region->action == L"setVolume:input" ? snapshot.audio.inputs : snapshot.audio.outputs;
                for (const auto& endpoint : endpoints) if (endpoint.isDefault) return endpoint.volume;
                return endpoints.empty() ? 0.0 : endpoints.front().volume;
            },
            [this](const Layout::Region& region, const std::wstring& payload)
            {
                if (kind == Kind::PeripheralBattery && region.action.starts_with(L"peripheralPage:")) Activate(region);
                else if (auto* host = Host()) host->OnRequest(region.action, payload);
            }, [this](const Layout::Region& region) { return host.IsMenuExpanded(region.action); },
            [this](int id)
            {
                impl->focusedRegion = id;
                impl->keyboardFocus = true;
                SetFocus(window);
                impl->dirty = true;
                impl->mediaStaticDirty = true;
                if (visible) Paint();
            });
        return true;
    }

    void CardWindow::BuildVisualTree(HINSTANCE instance)
    {
        UNREFERENCED_PARAMETER(instance);
        try
        {
            impl->compositor = environment.compositor ? environment.compositor : Compositor();
            const auto interop = Interop<ABI::Windows::UI::Composition::Desktop::ICompositorDesktopInterop>(
                impl->compositor);
            // The interop header only forward-declares the ABI target interface,
            // so the created object is taken as IUnknown and projected afterwards.
            ::IUnknown* created = nullptr;
            winrt::check_hresult(interop->CreateDesktopWindowTarget(window, TRUE,
                reinterpret_cast<ABI::Windows::UI::Composition::Desktop::IDesktopWindowTarget**>(&created)));
            winrt::com_ptr<::IUnknown> target;
            target.attach(created);
            impl->target = target.as<winrt::Windows::UI::Composition::Desktop::DesktopWindowTarget>();

            impl->root = impl->compositor.CreateContainerVisual();
            impl->root.RelativeSizeAdjustment(winrt::Windows::Foundation::Numerics::float2{ 1.0f, 1.0f });
            impl->target.Root(impl->root);
            impl->corner = impl->compositor.CreateRoundedRectangleGeometry();
            impl->root.Clip(impl->compositor.CreateGeometricClip(impl->corner));

            // Motion container: the show/hide animation changes only its opacity
            // and offset for the individual API. CardHost moves the whole group.
            impl->motion = impl->compositor.CreateContainerVisual();
            impl->motion.RelativeSizeAdjustment(winrt::Windows::Foundation::Numerics::float2{ 1.0f, 1.0f });
            impl->root.Children().InsertAtTop(impl->motion);

            impl->wash = MakeLayer(impl->compositor);
            impl->rim = MakeLayer(impl->compositor);
            impl->content = MakeLayer(impl->compositor);
            impl->motion.Children().InsertAtTop(impl->wash);
            impl->motion.Children().InsertAtTop(impl->rim);
            if (kind == Kind::Media || HasPageContent())
            {
                impl->dynamicContent = MakeLayer(impl->compositor);
                if (HasPageContent())
                {
                    impl->pageViewport = impl->compositor.CreateContainerVisual();
                    impl->pageViewport.RelativeSizeAdjustment({1, 1});
                    impl->outgoingPage = impl->compositor.CreateSpriteVisual();
                    impl->pageViewport.Children().InsertAtTop(impl->outgoingPage);
                    impl->pageViewport.Children().InsertAtTop(impl->dynamicContent);
                    impl->motion.Children().InsertAtTop(impl->pageViewport);
                }
                else impl->motion.Children().InsertAtTop(impl->dynamicContent);
            }
            // Transport stays above rolling lyrics where their viewports meet.
            impl->motion.Children().InsertAtTop(impl->content);
            impl->transition.Bind(impl->motion, visible, scale);
        }
        catch (const winrt::hresult_error&)
        {
            impl->compositor = nullptr;
            impl->target = nullptr;
            impl->root = nullptr;
        }
    }

    void CardWindow::Destroy()
    {
        ResetPageAnimation();
        ClearPeripheralNames();
        impl->transition.Detach();
        accessibility.Detach();
        if (window)
        {
            KillTimer(window, AnimationTimer);
            impl->timerInterval = 0;
            SetWindowLongPtrW(window, GWLP_USERDATA, 0);
        }
        if (impl->mica)
        {
            try { impl->mica.RemoveAllSystemBackdropTargets(); } catch (...) { }
            try { impl->mica.Close(); } catch (...) { }
            impl->mica = nullptr;
        }
        impl->micaConfiguration = nullptr;
        impl->micaReady = false;
        impl->dynamicSurface.Release();
        impl->boundDynamicSurface = nullptr;
        if (impl->dynamicContent) impl->dynamicContent.Brush(nullptr);
        impl->dynamicContent = nullptr;
        impl->outgoingPage = nullptr;
        impl->pageViewport = nullptr;
        impl->surface.Release();
        impl->boundSurface = nullptr;
        if (impl->content) impl->content.Brush(nullptr);
        impl->content = nullptr;
        if (impl->wash) { impl->wash.Brush(nullptr); impl->wash.Children().RemoveAll(); }
        if (impl->rim) impl->rim.Brush(nullptr);
        impl->captureExclusion.Restore();
        impl->glass.Release();
        impl->glassControls.Release();
        impl->glassControlsAttached = false;
        impl->glassActive = false;
        impl->rim = nullptr;
        impl->wash = nullptr;
        impl->motion = nullptr;
        impl->root = nullptr;
        impl->corner = nullptr;
        impl->target = nullptr;
        impl->compositor = nullptr;
        if (window)
        {
            DestroyWindow(window);
            window = nullptr;
        }
    }

    void CardWindow::ApplyEnvironment(const CardEnvironment& cardEnvironment)
    {
        ClearPeripheralNames();
        environment = cardEnvironment;
        impl->glassControlsFailed = false;
        ResetPageAnimation();
        if (environment.reducedMotion || environment.highContrast)
        {
            impl->hover.clear();
            if (visible && impl->hotRegion) impl->hover[impl->hotRegion] = { 1.0f, 1.0f, 0 };
            impl->transition.Set(visible, scale);
            if (!visible && window) ShowWindow(window, SW_HIDE);
            if (environment.graphics && impl->previousCoverData != snapshot.media.coverDataUrl)
                environment.graphics->ForgetImage(impl->previousCoverData);
            impl->coverFadeStart = 0;
            impl->previousCoverKey.clear();
            impl->previousCoverData.clear();
        }

        impl->effectsAllowed = !environment.highContrast && LiquidGlassBrush::EffectsEnabled();
        const bool useMica = environment.material.kind == MaterialKind::Mica && impl->effectsAllowed;
        if (!useMica && impl->mica)
        {
            try { impl->mica.RemoveAllSystemBackdropTargets(); } catch (...) { }
            try { impl->mica.Close(); } catch (...) { }
            impl->mica = nullptr;
            impl->micaConfiguration = nullptr;
            impl->micaReady = false;
        }
        // One controller per window: a theme or tuning change only reconfigures it.
        if (useMica && !impl->mica && window && impl->target)
        {
            try
            {
                if (muxc::MicaController::IsSupported())
                {
                    auto controller = muxc::MicaController();
                    auto configuration = muxc::SystemBackdropConfiguration();
                    impl->micaConfiguration = configuration;
                    configuration.IsInputActive(true);
                    configuration.Theme(environment.dark ? muxc::SystemBackdropTheme::Dark
                        : muxc::SystemBackdropTheme::Light);
                    const winrt::Microsoft::UI::WindowId windowId{static_cast<uint64_t>(reinterpret_cast<INT_PTR>(window))};
                    if (controller.SetTarget(windowId, impl->target))
                    {
                        // SetTarget installs HWND policy; apply the group policy afterwards.
                        controller.SetSystemBackdropConfiguration(configuration);
                        impl->mica = controller;
                        impl->micaReady = true;
                    }
                    else
                    {
                        controller.Close();
                    }
                }
            }
            catch (const winrt::hresult_error&)
            {
                impl->mica = nullptr;
                impl->micaReady = false;
            }
        }

        if (impl->mica)
        {
            try
            {
                const auto& tuning = environment.mica;
                impl->micaConfiguration.IsInputActive(true);
                impl->mica.SetSystemBackdropConfiguration(impl->micaConfiguration);
                impl->micaConfiguration.Theme(environment.dark ? muxc::SystemBackdropTheme::Dark : muxc::SystemBackdropTheme::Light);
                const auto fallback = FallbackColor(environment.dark);
                if (tuning.vivid)
                {
                    impl->mica.Kind(MicaKindFor(environment.dark));
                    impl->mica.TintColor(fallback);
                    impl->mica.FallbackColor(fallback);
                    impl->mica.TintOpacity(static_cast<float>(tuning.TintFor(environment.dark)));
                    impl->mica.LuminosityOpacity(static_cast<float>(tuning.LuminosityFor(environment.dark)));
                }
                else
                {
                    // Custom Mica off: the system default look, which is what
                    // ResetProperties() restores for the WinUI path.
                    impl->mica.Kind(muxc::MicaKind::Base);
                    impl->mica.ResetProperties();
                }
                impl->micaReady = true;
            }
            catch (const winrt::hresult_error&)
            {
                impl->micaReady = false;
            }
        }

        UpdateMaterial();
        impl->dirty = true;
        impl->mediaStaticDirty = true;
        impl->mediaRegionsDirty = true;
        UpdateTimer();
        if (visible) Paint();
    }

    void CardWindow::UpdateMaterial()
    {
        if (!impl->wash || !impl->compositor) return;
        // Create the material only once final placement/DPI are known. Create()
        // has not populated bounds yet and would otherwise build it twice.
        impl->glassActive = environment.material.kind == MaterialKind::LiquidGlass && impl->effectsAllowed &&
            bounds.right > bounds.left && bounds.bottom > bounds.top &&
            environment.graphics && environment.device && impl->glass.Update(*environment.graphics,
                impl->compositor, environment.device, static_cast<float>(bounds.right - bounds.left),
                static_cast<float>(bounds.bottom - bounds.top), scale, environment.material.glassBlur, environment.dark);
        if (impl->glassActive)
        {
            impl->wash.Brush(nullptr);
            if (!impl->wash.Children().Count()) impl->wash.Children().InsertAtTop(impl->glass.Visual());
            impl->rim.Brush(impl->glass.Rim());
            return;
        }
        impl->rim.Brush(nullptr);
        EnableLiveBackdrop(false);
        impl->wash.Children().RemoveAll();
        impl->glass.Release();
        const COLORREF system = GetSysColor(COLOR_WINDOW);
        const auto color = environment.highContrast
            ? winrt::Windows::UI::Color{255, GetRValue(system), GetGValue(system), GetBValue(system)}
            : impl->micaReady ? winrt::Windows::UI::Color{10, 255, 255, 255} : FallbackColor(environment.dark);
        impl->wash.Brush(impl->compositor.CreateColorBrush(color));
    }

    HRESULT CardWindow::MaterialResult() const { return impl->glass.LastResult(); }
    const std::wstring& CardWindow::MaterialDiagnostic() const { return impl->glass.Diagnostic(); }
    bool CardWindow::HasLiquidGlass() const { return impl->glassActive; }
    bool CardWindow::LiveBackdropActive() const { return impl->glassActive && impl->glass.LiveActive(); }
    uint64_t CardWindow::BackdropFrameCount() const { return impl->glass.RenderedFrames(); }

    bool CardWindow::EnableLiveBackdrop(bool enabled)
    {
        if (enabled && impl->glassActive && environment.allowLiveCapture)
        {
            if (!impl->captureSuspended) return impl->captureExclusion.Enable(window);
            if (!impl->captureExclusion.Enable(window)) return false;
            impl->captureSuspended = false;
            impl->resumePending = true;
            impl->resumeSerial = 0;
            return true;
        }
        impl->captureSuspended = impl->resumePending = false;
        impl->resumeSerial = 0;
        impl->glassControls.ClearBackdrop();
        impl->glass.ClearBackdrop();
        impl->captureExclusion.Restore();
        return !enabled;
    }

    bool CardWindow::SuspendCaptureExclusion()
    {
        if (!impl->glassActive || !environment.allowLiveCapture) return false;
        if (!impl->captureSuspended)
        {
            // Keep the rendered glass; only let the capture see this HWND.
            impl->captureExclusion.Restore();
            impl->captureSuspended = true;
            impl->resumePending = false;
            impl->resumeSerial = 0;
        }
        return true;
    }

    bool CardWindow::RenderBackdrop(const BackdropFrame& frame, bool refreshControls)
    {
        if (!impl->glassActive) return false;
        // Frozen while captured under a menu: rendering now would refract itself.
        if (impl->captureSuspended) return true;
        if (!impl->captureExclusion.Active()) return false;
        if (impl->resumePending)
        {
            // The first frame seen after re-exclusion may predate it.
            if (!impl->resumeSerial) impl->resumeSerial = frame.serial + 2;
            if (frame.serial < impl->resumeSerial) return true;
            impl->resumePending = false;
        }
        try
        {
            if (!impl->glass.RenderBackdrop(*environment.graphics, impl->compositor, environment.device, frame, bounds)) return false;
            // A skipped refresh keeps each control's previous lens; the next
            // refresh sees a newer scene revision and catches up.
            if (!refreshControls) return true;
            return impl->glassControlsFailed || impl->glassControls.RenderBackdrop(*environment.graphics, impl->compositor,
                environment.device, impl->glass.Scene(), impl->glass.SceneRevision());
        }
        catch (...) { return false; }
    }

    void CardWindow::SetSnapshot(const Snapshot& value)
    {
        if (kind == Kind::PeripheralBattery)
        {
            const auto& incoming = value.peripheralBattery;
            const auto& previous = snapshot.peripheralBattery;
            // A 400-ms global media snapshot must not repaint this independent card.
            // Revision ordering belongs to Protocol, including reconnect counter resets.
            const bool changed = !previous.SameAppearance(incoming, peripheralPage);
            const bool modeChanged = incoming.mode != previous.mode;
            bool valuesChanged = false;
            if (changed && !modeChanged)
            {
                const auto a = previous.Selected(), b = incoming.Selected();
                const int first = peripheralPage * previous.PageSize();
                for (int i = first; i < first + previous.PageSize() && i < static_cast<int>(a.size()) && i < static_cast<int>(b.size()); ++i)
                    if (a[i]->id == b[i]->id && a[i]->batteries != b[i]->batteries) valuesChanged = true;
            }
            if (modeChanged || valuesChanged) CapturePage();
            else if (changed) ResetPageAnimation();
            const bool statusChanged = snapshot.statusText != value.statusText;
            snapshot = value;
            peripheralPage = snapshot.peripheralBattery.ClampPage(modeChanged ? 0 : peripheralPage);
            impl->dirty |= changed || statusChanged;
            if (visible) Paint();
            if (modeChanged || valuesChanged) AnimatePageContent(0);
            return;
        }
        const auto now = GetTickCount64();
        const bool stableChanged = kind == Kind::Media &&
            (StableMedia(value.media) != StableMedia(snapshot.media) || !SameSources(value.media, snapshot.media));
        const bool regionsChanged = kind == Kind::Media &&
            (value.media.canSeek != snapshot.media.canSeek || value.media.canPrevious != snapshot.media.canPrevious ||
                value.media.canNext != snapshot.media.canNext || value.media.canPlayPause != snapshot.media.canPlayPause ||
                value.media.sources.empty() != snapshot.media.sources.empty());
        const bool lyricsChanged = kind == Kind::Media && VisibleLyrics(value.lyrics) != VisibleLyrics(snapshot.lyrics);
        const bool mediaChanged = stableChanged || (kind == Kind::Media &&
            (lyricsChanged ||
                value.lyrics.wordScan != impl->scanProgress ||
                value.media.coverDataUrl != snapshot.media.coverDataUrl || value.media.coverKey != snapshot.media.coverKey ||
                value.media.position != impl->mediaPosition || value.media.duration != snapshot.media.duration ||
                value.media.canSeek != snapshot.media.canSeek || value.statusText != snapshot.statusText));
        // Snapshots also carry lyrics, audio and icons. Repeated positions must
        // not rewind the local clock between real timeline updates.
        const bool newPosition = !impl->mediaPositionTick || value.media.position != impl->mediaPosition ||
            value.media.isPlaying != snapshot.media.isPlaying || value.media.duration != snapshot.media.duration ||
            value.media.title != snapshot.media.title || value.media.artist != snapshot.media.artist ||
            value.media.selectedSourceId != snapshot.media.selectedSourceId;
        const bool sameLine = value.lyrics.trackId == snapshot.lyrics.trackId &&
            value.lyrics.currentLineIndex == snapshot.lyrics.currentLineIndex && value.lyrics.current == snapshot.lyrics.current;
        const bool samePlayback = value.lyrics.isPlaying == snapshot.lyrics.isPlaying;
        const bool forwardScan = sameLine && samePlayback && value.lyrics.isPlaying &&
            value.lyrics.wordScan >= 0 && value.lyrics.wordScan >= impl->scanProgress;
        const double previousPaintedPosition = snapshot.media.position;
        const double previousPaintedScan = snapshot.lyrics.wordScan;
        const bool newLyrics = value.lyrics.revision != impl->lyricsRevision || !sameLine || !samePlayback ||
            value.lyrics.wordScan != impl->scanProgress;
        if (visible && kind == Kind::Media && !sameLine && value.lyrics.animateTransition && !environment.reducedMotion &&
            !snapshot.lyrics.current.empty())
        {
            impl->lyricPrevious = snapshot.lyrics.current;
            impl->lyricPreviousSecondary = snapshot.lyrics.currentTranslation.empty() ? snapshot.lyrics.next : snapshot.lyrics.currentTranslation;
            impl->lyricWrapStart = now;
        }
        if (newLyrics)
        {
            if (!sameLine || !samePlayback || value.lyrics.wordScan != impl->scanProgress)
            {
                const double elapsed = impl->scanTick ? (now - impl->scanTick) / 1000.0 : 0.0;
                const double advance = value.lyrics.wordScan - impl->scanProgress;
                impl->scanRate = sameLine && samePlayback && value.lyrics.isPlaying &&
                    impl->scanProgress >= 0 && elapsed > 0.05 && advance > 0 && advance < 0.9
                    ? advance / elapsed : 0.0;
                impl->scanProgress = value.lyrics.wordScan;
                impl->scanTick = now;
            }
            if (sameLine && impl->lyricsRevision >= 0 && impl->lyricsTick != 0 && value.lyrics.isPlaying)
            {
                const double delta = static_cast<double>(now - impl->lyricsTick) / 1000.0;
                const double advance = value.lyrics.progress - impl->lyricsProgress;
                // Only a forward move inside one line teaches a usable rate.
                impl->lyricsRate = delta > 0.05 && advance >= 0.0 && advance < 0.9 ? advance / delta : 0.0;
            }
            else if (value.lyrics.revision != impl->lyricsRevision)
            {
                impl->lyricsRate = 0.0;
            }
            impl->lyricsRevision = value.lyrics.revision;
            impl->lyricsProgress = value.lyrics.progress;
            impl->lyricsTick = now;
        }
        if (!sameLine)
            impl->lyricLineStarted = GetTickCount64();
        impl->lyricLineIndex = value.lyrics.currentLineIndex;
        if (kind == Kind::Media && value.media.coverKey != impl->coverKey)
        {
            if (environment.graphics && impl->previousCoverData != value.media.coverDataUrl)
                environment.graphics->ForgetImage(impl->previousCoverData);
            const bool fade = visible && !environment.reducedMotion && !environment.highContrast &&
                !snapshot.media.coverDataUrl.empty() && snapshot.media.coverDataUrl != value.media.coverDataUrl;
            if (!fade && environment.graphics && snapshot.media.coverDataUrl != value.media.coverDataUrl)
                environment.graphics->ForgetImage(snapshot.media.coverDataUrl);
            impl->previousCoverKey = fade ? impl->coverKey : L"";
            impl->previousCoverData = fade ? snapshot.media.coverDataUrl : L"";
            impl->coverKey = value.media.coverKey;
            impl->coverFadeStart = fade ? now : 0;
        }
        if (kind == Kind::Media)
        {
            // Other cards' history, weather and audio lists never enter the media display copy.
            if (stableChanged)
            {
                snapshot.media.title = value.media.title;
                snapshot.media.artist = value.media.artist;
                snapshot.media.source = value.media.source;
                snapshot.media.selectedSourceId = value.media.selectedSourceId;
                if (!SameSources(value.media, snapshot.media)) snapshot.media.sources = value.media.sources;
                snapshot.media.isPlaying = value.media.isPlaying;
                snapshot.media.canPrevious = value.media.canPrevious;
                snapshot.media.canNext = value.media.canNext;
                snapshot.media.canPlayPause = value.media.canPlayPause;
            }
            if (snapshot.media.coverDataUrl != value.media.coverDataUrl) snapshot.media.coverDataUrl = value.media.coverDataUrl;
            if (snapshot.media.coverKey != value.media.coverKey) snapshot.media.coverKey = value.media.coverKey;
            snapshot.media.position = value.media.position;
            snapshot.media.duration = value.media.duration;
            snapshot.media.canSeek = value.media.canSeek;
            snapshot.media.revision = value.media.revision;
            if (lyricsChanged) snapshot.lyrics = value.lyrics;
            snapshot.lyrics.revision = value.lyrics.revision;
            snapshot.lyrics.progress = value.lyrics.progress;
            snapshot.lyrics.wordScan = value.lyrics.wordScan;
            if (snapshot.statusText != value.statusText) snapshot.statusText = value.statusText;
            impl->mediaStaticDirty |= stableChanged || regionsChanged;
            impl->mediaRegionsDirty |= regionsChanged;
        }
        else if (kind == Kind::Weather)
        {
            const bool forecast = snapshot.weather.forecastView;
            const int offset = snapshot.weather.forecastOffset;
            snapshot = value;
            snapshot.weather.forecastView = forecast;
            snapshot.weather.forecastOffset = snapshot.weather.ClampOffset(offset);
        }
        else snapshot = value;
        if (forwardScan) snapshot.lyrics.wordScan = std::max(snapshot.lyrics.wordScan, previousPaintedScan);
        if (newPosition)
        {
            impl->mediaPosition = value.media.position;
            impl->mediaPositionTick = now;
        }
        else if (kind == Kind::Media && snapshot.media.isPlaying)
            snapshot.media.position = previousPaintedPosition;
        if (kind == Kind::Media && impl->sliderId && impl->sliderSeeking && snapshot.media.canSeek)
            snapshot.media.position = impl->sliderRatio * snapshot.media.duration;
        UpdateTimer();
        impl->dirty |= kind != Kind::Media || mediaChanged;
        if (visible) Paint();
    }

    void CardWindow::Invalidate()
    {
        impl->dirty = true;
        impl->mediaStaticDirty = true;
        impl->mediaRegionsDirty = true;
        if (window) InvalidateRect(window, nullptr, FALSE);
    }

    POINT CardWindow::ToDip(POINT pixels) const
    {
        const auto point = Layout::ToDip(POINT{pixels.x - bounds.left, pixels.y - bounds.top}, scale);
        return POINT{point.x, static_cast<LONG>(std::lround(point.y - impl->transition.OffsetDip()))};
    }

    float CardWindow::ContentWidthDip() const { return Layout::WidthDip(kind); }

    float CardWindow::ContentHeightDip() const
    {
        return measuredHeightDip > 0.0f ? measuredHeightDip : Layout::DefaultHeightDip(kind);
    }

    void CardWindow::Place(const RECT& targetBounds, float targetScale)
    {
        targetScale = targetScale > 0.0f ? targetScale : 1.0f;
        const bool reflowChanged = impl->reflowSurfaceSize.cx &&
            (impl->reflowSurfaceSize.cx != targetBounds.right - targetBounds.left ||
                impl->reflowSurfaceSize.cy != targetBounds.bottom - targetBounds.top);
        if (impl->reflowSurfaceSize.cx)
        {
            impl->reflowSurfaceSize = {};
            impl->content.Size({ 0, 0 });
            impl->content.RelativeSizeAdjustment({ 1, 1 });
            if (impl->dynamicContent)
            {
                impl->dynamicContent.Size({ 0, 0 });
                impl->dynamicContent.RelativeSizeAdjustment({ 1, 1 });
            }
        }
        if (!reflowChanged && EqualRect(&bounds, &targetBounds) && scale == targetScale)
        {
            Paint();
            return;
        }
        if (scale != targetScale)
        {
            ResetPageAnimation();
            impl->transition.Set(visible, targetScale);
            if (!visible && window) ShowWindow(window, SW_HIDE);
        }
        const bool contentChanged = reflowChanged || scale != targetScale ||
            bounds.right - bounds.left != targetBounds.right - targetBounds.left ||
            bounds.bottom - bounds.top != targetBounds.bottom - targetBounds.top;
        bounds = targetBounds;
        scale = targetScale > 0.0f ? targetScale : 1.0f;
        measuredHeightDip = static_cast<float>(targetBounds.bottom - targetBounds.top) / scale;
        if (impl->corner)
        {
            impl->corner.Size({ static_cast<float>(targetBounds.right - targetBounds.left), static_cast<float>(targetBounds.bottom - targetBounds.top) });
            impl->corner.CornerRadius({ Layout::CornerRadius * scale, Layout::CornerRadius * scale });
        }
        if (window)
        {
            SetWindowPos(window, HWND_TOPMOST, bounds.left, bounds.top,
                bounds.right - bounds.left, bounds.bottom - bounds.top,
                SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOZORDER);
        }
        impl->dirty |= contentChanged;
        impl->mediaStaticDirty |= contentChanged;
        impl->mediaRegionsDirty |= contentChanged;
        UpdateMaterial();
        // Resident prewarm must produce actual pixels before the first show.
        Paint();
    }

    void CardWindow::CommitGroupBounds(const RECT& targetBounds)
    {
        const bool resized = bounds.right - bounds.left != targetBounds.right - targetBounds.left ||
            bounds.bottom - bounds.top != targetBounds.bottom - targetBounds.top;
        bounds = targetBounds;
        if (resized && impl->corner)
            impl->corner.Size({ static_cast<float>(bounds.right - bounds.left),
                static_cast<float>(bounds.bottom - bounds.top) });
        if (resized) UpdateMaterial();
    }

    void CardWindow::PrepareGroupReflow(const RECT& targetBounds, float targetScale)
    {
        if (scale != targetScale) ResetPageAnimation();
        const auto actualBounds = bounds;
        const bool contentChanged = scale != targetScale ||
            bounds.right - bounds.left != targetBounds.right - targetBounds.left ||
            bounds.bottom - bounds.top != targetBounds.bottom - targetBounds.top;
        bounds = targetBounds;
        scale = targetScale;
        measuredHeightDip = static_cast<float>(bounds.bottom - bounds.top) / scale;
        impl->reflowSurfaceSize = { bounds.right - bounds.left, bounds.bottom - bounds.top };
        const winrt::Windows::Foundation::Numerics::float2 size{
            static_cast<float>(impl->reflowSurfaceSize.cx), static_cast<float>(impl->reflowSurfaceSize.cy) };
        impl->content.RelativeSizeAdjustment({ 0, 0 });
        impl->content.Size(size);
        if (impl->dynamicContent)
        {
            impl->dynamicContent.RelativeSizeAdjustment({ 0, 0 });
            impl->dynamicContent.Size(size);
        }
        impl->dirty |= contentChanged;
        impl->mediaStaticDirty |= contentChanged;
        impl->mediaRegionsDirty |= contentChanged;
        Paint();
        bounds = actualBounds;
    }

    void CardWindow::SetWeatherView(bool forecast, int offset)
    {
        if (kind != Kind::Weather) return;
        auto target = snapshot.weather;
        target.forecastView = forecast;
        offset = target.ClampOffset(offset);
        if (snapshot.weather.forecastView == forecast && snapshot.weather.forecastOffset == offset) return;
        const float direction = forecast != snapshot.weather.forecastView ? (forecast ? 1.f : -1.f) :
            (offset > snapshot.weather.forecastOffset ? 1.f : -1.f);
        CapturePage();
        snapshot.weather.forecastView = forecast;
        snapshot.weather.forecastOffset = offset;
        impl->dirty = true;
        if (visible) Paint();
        AnimatePageContent(direction);
    }

    void CardWindow::SetPeripheralPage(int page)
    {
        if (kind != Kind::PeripheralBattery) return;
        page = snapshot.peripheralBattery.ClampPage(page);
        if (page == peripheralPage) return;
        const float direction = page > peripheralPage ? 1.f : -1.f;
        CapturePage();
        peripheralPage = page;
        impl->dirty = true;
        host.OnPageLayoutChanged();
        if (visible) Paint();
        AnimatePageContent(direction);
    }

    void CardWindow::TurnPage(int direction)
    {
        if (kind == Kind::PeripheralBattery) SetPeripheralPage(peripheralPage + direction);
        else if (kind == Kind::Weather)
            SetWeatherView(snapshot.weather.forecastView,
                snapshot.weather.ClampOffset(snapshot.weather.forecastOffset) + direction * snapshot.weather.PageSize());
    }

    bool CardWindow::HasPageContent() const { return kind == Kind::Weather || kind == Kind::PeripheralBattery; }

    D2D1_RECT_F CardWindow::PageContentBounds() const
    {
        return kind == Kind::Weather ? Draw::Rect(0, 212, ContentWidthDip(), 324) :
            Draw::Rect(0, 44, ContentWidthDip(), ContentHeightDip() - 24 - Layout::PeripheralFooterBottomGap);
    }

    bool CardWindow::PageTransitionRunning() const { return impl->pageBatch != nullptr; }

    void CardWindow::ResetPageAnimation()
    {
        impl->pageToken = ++pageGeneration;
        if (impl->pageBatch) { impl->pageBatch.Completed(impl->pageCompleted); impl->pageBatch = nullptr; }
        if (!HasPageContent()) return;
        for (auto visual : {impl->dynamicContent, impl->outgoingPage})
        {
            if (!visual) continue;
            visual.StopAnimation(L"Opacity");
            visual.StopAnimation(L"Offset.X");
            visual.Opacity(1);
            visual.Offset({0, 0, 0});
        }
        for (auto& name : impl->outgoingNames)
        {
            if (name.text) { name.text.StopAnimation(L"Offset.X"); name.text.Brush(nullptr); }
            if (impl->outgoingPage && name.viewport) impl->outgoingPage.Children().Remove(name.viewport);
            name.surface.Release();
        }
        impl->outgoingNames.clear();
        if (impl->outgoingPage) impl->outgoingPage.Brush(nullptr);
        impl->outgoingSurface.Release();
    }

    void CardWindow::CapturePage()
    {
        ResetPageAnimation();
        if (!visible || environment.reducedMotion || environment.highContrast || !impl->pageViewport) return;
        const int width = std::max(1, static_cast<int>(std::ceil(ContentWidthDip() * scale)));
        const int height = std::max(1, static_cast<int>(std::ceil(ContentHeightDip() * scale)));
        impl->outgoingSurface.Ensure(environment.device, width, height);
        {
            DrawScope scope(impl->outgoingSurface, scale);
            if (!scope) { host.OnGraphicsFailure(impl->outgoingSurface.LastResult()); return; }
            auto* context = scope.Get();
            context->Clear(D2D1::ColorF(0, 0.f));
            context->PushAxisAlignedClip(PageContentBounds(), D2D1_ANTIALIAS_MODE_ALIASED);
            Layout::Table unused;
            if (kind == Kind::Weather)
                Paint::Weather(context, snapshot, environment, unused, ContentWidthDip(), ContentHeightDip());
            else
            {
                std::vector<Paint::PeripheralName> names;
                Paint::PeripheralBattery(context, snapshot, environment, unused, ContentWidthDip(), ContentHeightDip(),
                    {}, peripheralPage, &names);
            }
            context->PopAxisAlignedClip();
        }
        if (!impl->outgoingSurface.Ready() || FAILED(impl->outgoingSurface.LastResult()))
        {
            host.OnGraphicsFailure(impl->outgoingSurface.LastResult());
            ResetPageAnimation();
            return;
        }
        // Transfer the existing name visuals so a scrolling name retains its actual
        // compositor offset while the outgoing page fades, instead of snapping to its start.
        for (auto& name : impl->peripheralNames)
        {
            impl->dynamicContent.Children().Remove(name.viewport);
            impl->outgoingPage.Children().InsertAtTop(name.viewport);
        }
        impl->outgoingNames = std::move(impl->peripheralNames);
        impl->peripheralNames.clear();
        impl->peripheralNameScale = 0;
        impl->outgoingPage.Size({static_cast<float>(width), static_cast<float>(height)});
        impl->outgoingPage.Brush(impl->compositor.CreateSurfaceBrush(impl->outgoingSurface.Handle()));
    }

    void CardWindow::AnimatePageContent(float direction)
    {
        if (!visible || environment.reducedMotion || environment.highContrast || !impl->outgoingPage ||
            !impl->outgoingPage.Brush()) { ResetPageAnimation(); return; }
        try
        {
            const auto ease = impl->compositor.CreateCubicBezierEasingFunction({0.2f, 0}, {0, 1});
            impl->pageBatch = impl->compositor.CreateScopedBatch(CompositionBatchTypes::Animation);
            const auto token = impl->pageToken;
            const auto handle = window;
            impl->pageCompleted = impl->pageBatch.Completed([handle, token](auto const&, auto const&)
                { PostMessageW(handle, PageMotionCompleted, token, 0); });
            for (bool incoming : {false, true})
            {
                auto visual = incoming ? impl->dynamicContent : impl->outgoingPage;
                auto fade = impl->compositor.CreateScalarKeyFrameAnimation();
                fade.Duration(std::chrono::milliseconds(180));
                fade.InsertKeyFrame(0, incoming ? 0.f : 1.f);
                fade.InsertKeyFrame(1, incoming ? 1.f : 0.f, ease);
                auto slide = impl->compositor.CreateScalarKeyFrameAnimation();
                slide.Duration(std::chrono::milliseconds(180));
                slide.InsertKeyFrame(0, incoming ? direction * 12.f * scale : 0.f);
                slide.InsertKeyFrame(1, incoming ? 0.f : -direction * 12.f * scale, ease);
                visual.StartAnimation(L"Opacity", fade);
                visual.StartAnimation(L"Offset.X", slide);
            }
            impl->pageBatch.End();
        }
        catch (winrt::hresult_error const&) { ResetPageAnimation(); }
    }

    void CardWindow::OnPageMotionCompleted(UINT_PTR generation)
    {
        if (generation == impl->pageToken) ResetPageAnimation();
    }

    void CardWindow::OnMotionCompleted(UINT_PTR generation)
    {
        if (impl->transition.Complete(generation) && !visible && window)
            ShowWindow(window, SW_HIDE);
    }

    void CardWindow::Show(bool animate)
    {
        if (!window) return;
        // Snapshot/layout refreshes must not restart an entrance or cover a menu.
        if (visible) { Paint(); return; }
        visible = true;
        // Placement and resident warmup already committed unchanged content.
        Paint();
        SetPeripheralNamesRunning(true);
        EnableLiveBackdrop(environment.allowLiveCapture && impl->glassActive);
        impl->transition.Start(true, scale, animate && !environment.reducedMotion && !environment.highContrast,
            window, AnimationMilliseconds);
        ShowWindow(window, SW_SHOWNOACTIVATE);
        SetWindowPos(window, HWND_TOPMOST, bounds.left, bounds.top,
            bounds.right - bounds.left, bounds.bottom - bounds.top,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
        UpdateTimer();
    }

    void CardWindow::Hide(bool animate)
    {
        visible = false;
        SetPeripheralNamesRunning(false);
        ResetPageAnimation();
        if (!window) return;
        CancelPointer();
        const bool hadFeedback = impl->hotRegion || !impl->hover.empty();
        impl->dirty |= hadFeedback || impl->coverFadeStart || impl->lyricWrapStart;
        impl->mediaStaticDirty |= hadFeedback;
        impl->hotRegion = 0;
        impl->hover.clear();
        impl->trackingLeave = false;
        impl->coverFadeStart = impl->lyricWrapStart = 0;
        if (environment.graphics && impl->previousCoverData != snapshot.media.coverDataUrl)
            environment.graphics->ForgetImage(impl->previousCoverData);
        impl->previousCoverData.clear();
        impl->previousCoverKey.clear();
        impl->transition.Start(false, scale, animate && !environment.reducedMotion && !environment.highContrast,
            window, AnimationMilliseconds);
        if (!impl->transition.Running()) ShowWindow(window, SW_HIDE);
        UpdateTimer();
    }

    void CardWindow::ClearPeripheralNames()
    {
        SetPeripheralNamesRunning(false);
        for (auto& name : impl->peripheralNames)
        {
            if (impl->dynamicContent && name.viewport) impl->dynamicContent.Children().Remove(name.viewport);
            if (name.text) name.text.Brush(nullptr);
            name.surface.Release();
        }
        impl->peripheralNames.clear();
        impl->peripheralNameScale = 0;
    }

    void CardWindow::SetPeripheralNamesRunning(bool running)
    {
        running = running && !environment.reducedMotion && !environment.highContrast;
        for (auto& name : impl->peripheralNames)
        {
            if (!name.text || !name.animation || name.running == running) continue;
            if (running) name.text.StartAnimation(L"Offset.X", name.animation);
            else
            {
                name.text.StopAnimation(L"Offset.X");
                name.text.Offset({0, 0, 0});
            }
            name.running = running;
        }
    }

    size_t CardWindow::PeripheralNameAnimationCount() const
    {
        return std::count_if(impl->peripheralNames.begin(), impl->peripheralNames.end(),
            [](const auto& name) { return name.running && name.text.TryGetAnimationController(L"Offset.X"); });
    }

    bool CardWindow::UpdatePeripheralNames(const std::vector<Paint::PeripheralName>& names)
    {
        if (impl->peripheralNameScale == scale && names.size() == impl->peripheralNames.size() &&
            std::equal(names.begin(), names.end(), impl->peripheralNames.begin(),
                [](const auto& layout, const auto& layer) { return layout == layer.layout; })) return true;
        ClearPeripheralNames();
        for (const auto& layout : names)
        {
            auto& name = impl->peripheralNames.emplace_back();
            name.layout = layout;
            const float height = layout.bounds.bottom - layout.bounds.top;
            const float width = layout.bounds.right - layout.bounds.left;
            name.surface.Ensure(environment.device, static_cast<int>(std::ceil(layout.width * scale)),
                static_cast<int>(std::ceil(height * scale)));
            {
                DrawScope scope(name.surface, scale);
                if (!scope) { impl->dirty = true; host.OnGraphicsFailure(name.surface.LastResult()); return false; }
                auto* context = scope.Get();
                context->Clear(D2D1::ColorF(0, 0.0f));
                const auto text = environment.graphics->TextLayout(layout.text, environment.fontFamily,
                    layout.size, 600, layout.width, height, false, false, false, false, 1,
                    DWRITE_TEXT_ALIGNMENT_LEADING, DWRITE_PARAGRAPH_ALIGNMENT_NEAR);
                const auto brush = environment.graphics->Brush(context, Draw::Colors(environment).primary);
                if (text && brush) context->DrawTextLayout({0, 0}, text.Get(), brush.Get(), D2D1_DRAW_TEXT_OPTIONS_CLIP);
            }
            if (!name.surface.Ready()) { impl->dirty = true; host.OnGraphicsFailure(name.surface.LastResult()); return false; }
            name.viewport = impl->compositor.CreateContainerVisual();
            name.viewport.Size({width * scale, height * scale});
            name.viewport.Offset({layout.bounds.left * scale, layout.bounds.top * scale, 0});
            name.viewport.Clip(impl->compositor.CreateInsetClip());
            name.text = impl->compositor.CreateSpriteVisual();
            name.text.Size({static_cast<float>(name.surface.Width()), static_cast<float>(name.surface.Height())});
            const auto brush = impl->compositor.CreateSurfaceBrush(name.surface.Handle());
            name.text.Brush(brush);
            name.viewport.Children().InsertAtTop(name.text);
            impl->dynamicContent.Children().InsertAtTop(name.viewport);

            // Hold both ends for readability; only the prepainted name moves.
            const float travel = (layout.width - width) / 22.f;
            constexpr float pause = 1.2f;
            const float duration = 2 * (travel + pause);
            const float distance = -(layout.width - width) * scale;
            const auto linear = impl->compositor.CreateLinearEasingFunction();
            name.animation = impl->compositor.CreateScalarKeyFrameAnimation();
            name.animation.Duration(std::chrono::milliseconds(static_cast<int>(duration * 1000)));
            name.animation.IterationBehavior(AnimationIterationBehavior::Forever);
            name.animation.InsertKeyFrame(0, 0, linear);
            name.animation.InsertKeyFrame(pause / duration, 0, linear);
            name.animation.InsertKeyFrame((pause + travel) / duration, distance, linear);
            name.animation.InsertKeyFrame((2 * pause + travel) / duration, distance, linear);
            name.animation.InsertKeyFrame(1, 0, linear);
        }
        impl->peripheralNameScale = scale;
        SetPeripheralNamesRunning(visible);
        return true;
    }

    void CardWindow::UpdateTimer()
    {
        if (!window) return;
        const bool smoothLyrics = kind == Kind::Media && snapshot.media.isPlaying && snapshot.lyrics.isPlaying &&
            impl->scanRate > 0 && impl->scanProgress >= 0 && snapshot.lyrics.wordScan < 1 &&
            GetTickCount64() - impl->scanTick < 500;
        const bool playback = kind == Kind::Media && snapshot.media.isPlaying && snapshot.media.duration > 0 &&
            snapshot.media.position < snapshot.media.duration && !impl->dragging;
        const bool dynamic = visible && (impl->coverFadeStart || impl->lyricWrapStart ||
            impl->sliderPendingUntil || playback || smoothLyrics ||
            (kind == Kind::Weather && snapshot.weather.alerts.size() > 1));
        const bool hoverAnimating = visible && std::any_of(impl->hover.begin(), impl->hover.end(),
            [](const auto& item) { return item.second.started != 0; });
        const UINT interval = hoverAnimating ? 16 :
            dynamic ? (kind == Kind::Weather ? 1000 :
                impl->coverFadeStart || impl->lyricWrapStart || smoothLyrics ? 33 : 250) : 0;
        if (interval == impl->timerInterval) return;
        if (interval)
        {
            if (SetTimer(window, AnimationTimer, interval, nullptr)) impl->timerInterval = interval;
        }
        else { KillTimer(window, AnimationTimer); impl->timerInterval = 0; }
    }

    bool CardWindow::Tick()
    {
        const auto now = GetTickCount64();
        bool repaint = false;
        if (impl->sliderPendingUntil && now >= impl->sliderPendingUntil)
        {
            impl->sliderPendingUntil = 0; impl->sliderId = 0; impl->sliderAction.clear(); repaint = true;
        }
        if (visible)
        {
            for (auto item = impl->hover.begin(); item != impl->hover.end();)
            {
                auto& fade = item->second;
                if (fade.started)
                {
                    repaint = true;
                    if (item->first != 4) impl->mediaStaticDirty = true;
                    if (now - fade.started >= HoverMilliseconds) fade.started = 0;
                }
                if (!fade.started && fade.to == 0.0f) item = impl->hover.erase(item);
                else ++item;
            }
            if (kind == Kind::Weather && snapshot.weather.alerts.size() > 1 && now / 4000 != impl->lastWeatherAlert)
            { impl->lastWeatherAlert = now / 4000; repaint = true; }
            if (impl->coverFadeStart)
            {
                repaint = true;
                if (environment.reducedMotion || now - impl->coverFadeStart >= CoverFadeMilliseconds)
                {
                    if (environment.graphics && impl->previousCoverData != snapshot.media.coverDataUrl)
                        environment.graphics->ForgetImage(impl->previousCoverData);
                    impl->previousCoverKey.clear(); impl->previousCoverData.clear(); impl->coverFadeStart = 0;
                }
            }
            if (impl->lyricWrapStart)
            {
                repaint = true;
                if (environment.reducedMotion || now - impl->lyricWrapStart >= LyricWrapMilliseconds)
                { impl->lyricPrevious.clear(); impl->lyricPreviousSecondary.clear(); impl->lyricWrapStart = 0; }
            }
            const bool scanAdvancing = snapshot.lyrics.isPlaying && impl->scanRate > 0 &&
                impl->scanProgress >= 0 && now - impl->scanTick <= 533 && snapshot.lyrics.wordScan < 1;
            const bool timelineAdvancing = snapshot.media.duration > 0 && !impl->dragging &&
                snapshot.media.position < snapshot.media.duration;
            if (kind == Kind::Media && snapshot.media.isPlaying && (scanAdvancing || timelineAdvancing) &&
                now - impl->lastLyricPaint >= (scanAdvancing ? 33 : 250))
            { impl->lastLyricPaint = now; repaint = true; }
            if (repaint) { impl->dirty = true; Paint(); }
        }
        UpdateTimer();
        return impl->transition.Running();
    }

    void CardWindow::PaintContent(bool rebuildRegions)
    {
        if (!impl->compositor || !environment.device || !environment.graphics) return;
        const int widthPixels = (std::max)(1, static_cast<int>(impl->reflowSurfaceSize.cx ?
            impl->reflowSurfaceSize.cx : bounds.right - bounds.left));
        const int heightPixels = (std::max)(1, static_cast<int>(impl->reflowSurfaceSize.cy ?
            impl->reflowSurfaceSize.cy : bounds.bottom - bounds.top));
        impl->surface.Ensure(environment.device, widthPixels, heightPixels);
        if (!impl->surface.Ready()) return;

        if (kind == Kind::Media || HasPageContent()) impl->dynamicSurface.Ensure(environment.device, widthPixels, heightPixels);
        if (rebuildRegions) { regions.Clear(); ++regionBuildCount; }
        const bool paintStable = kind != Kind::Media || impl->mediaStaticDirty || !impl->content.Brush();
        std::vector<Paint::PeripheralName> peripheralNames;
        for (int layer = 0; layer < (kind == Kind::Media || HasPageContent() ? 2 : 1); ++layer)
        {
            if (layer == 0 && !paintStable) continue;
            impl->controlDescriptions[layer].clear();
            auto& surface = layer == 0 ? impl->surface : impl->dynamicSurface;
            {
                DrawScope scope(surface, scale);
                if (!scope) { impl->dirty = true; host.OnGraphicsFailure(surface.LastResult()); return; }
                ID2D1DeviceContext* context = scope.Get();
                context->Clear(D2D1::ColorF(0, 0.0f));

                const float widthDip = ContentWidthDip();
                const float heightDip = ContentHeightDip();
                // Decode covers at the actual content resolution.
                const float fit = (bounds.right - bounds.left) / std::max(1.0f, widthDip * scale);

                // A dragged slider is painted from the pointer until the gesture ends,
                // so an older snapshot can never drag the knob back mid-drag.
                // This is the window's display copy. The host keeps authoritative
                // snapshots; updating scalar presentation values avoids copying image
                // strings and all four cards' models on every animation tick.
                auto& painted = snapshot;
                if (painted.media.isPlaying && impl->mediaPositionTick)
                    painted.media.position = std::clamp(impl->mediaPosition +
                        (GetTickCount64() - impl->mediaPositionTick) / 1000.0, 0.0, std::max(0.0, painted.media.duration));
                if (painted.lyrics.isPlaying && impl->lyricsRate > 0.0 && impl->lyricsTick != 0)
                {
                    const double elapsed = static_cast<double>(GetTickCount64() - impl->lyricsTick) / 1000.0;
                    painted.lyrics.progress = std::clamp(
                        impl->lyricsProgress + impl->lyricsRate * elapsed, 0.0, 1.0);
                }
                if (painted.lyrics.isPlaying && impl->scanProgress >= 0 && impl->scanRate > 0)
                    painted.lyrics.wordScan = std::max(painted.lyrics.wordScan,
                        std::clamp(impl->scanProgress + impl->scanRate *
                            std::min(0.5, (GetTickCount64() - impl->scanTick) / 1000.0), 0.0, 1.0));
                if (impl->sliderId != 0 && !impl->sliderSeeking)
                {
                    auto& endpoints = impl->sliderAction == L"setVolume:input" ? painted.audio.inputs : painted.audio.outputs;
                    for (auto& endpoint : endpoints)
                        if (endpoint.isDefault && endpoint.canControl) endpoint.volume = impl->sliderRatio;
                }
                else if (impl->sliderId != 0 && impl->sliderSeeking && painted.media.canSeek)
                {
                    painted.media.position = impl->sliderRatio * painted.media.duration;
                }
                Paint::LyricTransition lyricTransition;
                if (kind == Kind::Media && impl->lyricLineStarted != 0)
                {
                    // A long line waits a moment, then scrolls over six seconds.
                    const double age = static_cast<double>(GetTickCount64() - impl->lyricLineStarted) / 1000.0;
                    lyricTransition.marquee = static_cast<float>(
                        std::clamp((age - 0.8) / 6.0, 0.0, 1.0));
                }
                if (kind == Kind::Media && impl->lyricWrapStart != 0)
                {
                    const ULONGLONG elapsed = GetTickCount64() - impl->lyricWrapStart;
                    if (elapsed < LyricWrapMilliseconds)
                    {
                        lyricTransition.previous = impl->lyricPrevious;
                        lyricTransition.previousSecondary = impl->lyricPreviousSecondary;
                        lyricTransition.progress = static_cast<float>(elapsed) /
                            static_cast<float>(LyricWrapMilliseconds);
                    }
                    else
                    {
                        impl->lyricPrevious.clear();
                        impl->lyricPreviousSecondary.clear();
                        impl->lyricWrapStart = 0;
                    }
                }
                Paint::CoverTransition cover;
                ComPtr<ID2D1Bitmap1> previousCover;
                if (kind == Kind::Media && !impl->previousCoverKey.empty() && impl->coverFadeStart != 0)
                {
                    const ULONGLONG elapsed = GetTickCount64() - impl->coverFadeStart;
                    if (elapsed < CoverFadeMilliseconds)
                    {
                        previousCover = environment.graphics->DecodeImage(impl->previousCoverData,
                            static_cast<int>(64 * scale * fit), static_cast<int>(64 * scale * fit));
                        cover.previous = previousCover.Get();
                        cover.previousOpacity = 1.0f - static_cast<float>(elapsed) /
                            static_cast<float>(CoverFadeMilliseconds);
                    }
                    else
                    {
                        // The old cover is dropped the moment the transition ends.
                        if (impl->previousCoverData != snapshot.media.coverDataUrl)
                            environment.graphics->ForgetImage(impl->previousCoverData);
                        impl->previousCoverData.clear();
                        impl->previousCoverKey.clear();
                        impl->coverFadeStart = 0;
                    }
                }
                auto interaction = CurrentInteraction();
                interaction.compositedGlass = impl->glassActive && !impl->glassControlsFailed;
                if (!HasPageContent() || layer == 0)
                    interaction.glassControls = &impl->controlDescriptions[layer];
                Layout::Table discardedRegions;
                auto& layerRegions = HasPageContent() && layer == 1 ? discardedRegions : regions;
                if (HasPageContent() && layer == 1)
                    context->PushAxisAlignedClip(PageContentBounds(), D2D1_ANTIALIAS_MODE_ALIASED);
                switch (kind)
                {
                case Kind::Media:
                    Paint::Media(context, painted, cover, lyricTransition, environment, regions, widthDip,
                        heightDip, interaction, layer == 0 ? Paint::MediaLayer::Stable : Paint::MediaLayer::Dynamic,
                        rebuildRegions);
                    break;
                case Kind::Device:
                    Paint::Device(context, painted, environment, regions, widthDip, heightDip, interaction);
                    break;
                case Kind::Traffic:
                    Paint::Traffic(context, painted, environment, regions, widthDip, heightDip, interaction);
                    break;
                case Kind::Weather:
                    Paint::Weather(context, painted, environment, layerRegions, widthDip, heightDip, interaction);
                    break;
                case Kind::PeripheralBattery:
                    Paint::PeripheralBattery(context, painted, environment, layerRegions, widthDip, heightDip, interaction, peripheralPage, layer == 1 ? &peripheralNames : nullptr);
                    break;
                case Kind::Update:
                    Paint::Update(context, painted, environment, regions, widthDip, heightDip, interaction);
                    break;
                }
                if (HasPageContent())
                {
                    if (layer == 0)
                    {
                        // Keep the controls stationary; only the list lives on the second surface.
                        context->PushAxisAlignedClip(PageContentBounds(), D2D1_ANTIALIAS_MODE_ALIASED);
                        context->Clear(D2D1::ColorF(0, 0.f));
                    }
                    context->PopAxisAlignedClip();
                }
                if (!snapshot.statusText.empty() && (HasPageContent() ? layer == 0 : kind != Kind::Media || layer == 1))
                {
                    const auto area = Draw::Rect(8, heightDip - 25, widthDip - 8, heightDip - 3);
                    Draw::FillRound(context, area, 4, environment.dark ? Color{0.15f, 0.1f, 0.1f, 1} : Color{1, 0.94f, 0.92f, 1});
                    Draw::Text(context, *environment.graphics, snapshot.statusText, environment.fontFamily, 10, 400,
                        Draw::Colors(environment).primary, area);
                }
            }
            if (!surface.Ready()) { impl->dirty = true; host.OnGraphicsFailure(surface.LastResult()); return; }
            if (layer == 0) ++staticPaintCount; else ++dynamicPaintCount;
        }
        if (!impl->surface.Ready() || ((kind == Kind::Media || HasPageContent()) && !impl->dynamicSurface.Ready()))
        { impl->dirty = true; host.OnGraphicsFailure(kind == Kind::Media || HasPageContent() ? impl->dynamicSurface.LastResult() : impl->surface.LastResult()); return; }
        if (kind == Kind::PeripheralBattery && !UpdatePeripheralNames(peripheralNames)) return;
        if (impl->glassActive && !impl->glassControlsFailed)
        {
            auto descriptions = impl->controlDescriptions[0];
            descriptions.insert(descriptions.end(), impl->controlDescriptions[1].begin(), impl->controlDescriptions[1].end());
            try
            {
                impl->glassControls.Update(*environment.graphics, impl->compositor, environment.device,
                    descriptions, scale, environment.dark, environment.reducedMotion);
                if (impl->glass.Scene() && environment.allowLiveCapture && impl->captureExclusion.Active())
                    impl->glassControls.RenderBackdrop(*environment.graphics, impl->compositor,
                        environment.device, impl->glass.Scene(), impl->glass.SceneRevision());
                if (!impl->glassControlsAttached)
                {
                    impl->motion.Children().InsertAbove(impl->glassControls.Visual(), impl->rim);
                    impl->glassControlsAttached = true;
                }
            }
            catch (const winrt::hresult_error& error)
            {
                // No exception may cross the Win32 callback. Repaint controls
                // through the ordinary painter so a failed graph leaves no holes.
                if (impl->glassControlsAttached)
                    try { impl->motion.Children().Remove(impl->glassControls.Visual()); } catch (...) {}
                impl->glassControlsAttached = false;
                impl->glassControls.Release();
                impl->glassControlsFailed = true;
                impl->mediaStaticDirty = true;
                host.OnGraphicsFailure(error.code());
                PaintContent(rebuildRegions);
                return;
            }
        }
        else if (impl->glassControlsAttached)
        {
            impl->motion.Children().Remove(impl->glassControls.Visual());
            impl->glassControls.Release();
            impl->glassControlsAttached = false;
        }
        if (rebuildRegions) PruneInteraction();
        if (impl->surface.Handle() && impl->boundSurface != impl->surface.Handle())
        {
            auto brush = impl->compositor.CreateSurfaceBrush(impl->surface.Handle());
            brush.Stretch(CompositionStretch::Fill);
            impl->content.Brush(brush);
            impl->boundSurface = impl->surface.Handle();
        }
        if ((kind == Kind::Media || HasPageContent()) && impl->boundDynamicSurface != impl->dynamicSurface.Handle())
        {
            auto brush = impl->compositor.CreateSurfaceBrush(impl->dynamicSurface.Handle());
            brush.Stretch(CompositionStretch::Fill);
            impl->dynamicContent.Brush(brush);
            impl->boundDynamicSurface = impl->dynamicSurface.Handle();
        }
        if (HasPageContent())
        {
            const auto area = PageContentBounds();
            auto clip = impl->compositor.CreateInsetClip(0, area.top * scale, 0,
                std::max(0.f, heightPixels - area.bottom * scale));
            impl->pageViewport.Size({static_cast<float>(widthPixels), static_cast<float>(heightPixels)});
            impl->pageViewport.RelativeSizeAdjustment({0, 0});
            impl->pageViewport.Clip(clip);
        }
        impl->dirty = false;
        impl->mediaStaticDirty = false;
        impl->mediaRegionsDirty = false;
        ++contentPaintCount;
    }

    void CardWindow::Paint()
    {
        if (!impl->compositor) return;
        const bool rebuild = impl->dirty || !impl->surface.Ready();
        if (rebuild) PaintContent(kind != Kind::Media || impl->mediaRegionsDirty);
    }

    bool CardWindow::HasRenderedContent() const
    {
        return impl->surface.Ready() && impl->content && impl->content.Brush() && !impl->dirty &&
            ((kind != Kind::Media && !HasPageContent()) || (impl->dynamicSurface.Ready() && impl->dynamicContent && impl->dynamicContent.Brush()));
    }

    CompositionBrush CardWindow::ContentBrush() const
    {
        return impl->content ? impl->content.Brush() : nullptr;
    }

    bool CardWindow::HasActiveBackdropConfiguration() const
    {
        return impl->micaReady && impl->micaConfiguration && impl->micaConfiguration.IsInputActive() &&
            impl->mica.State() == muxc::SystemBackdropState::Active;
    }

    void CardWindow::RefreshBackdropConfiguration()
    {
        if (!impl->mica || !impl->micaConfiguration) return;
        try
        {
            impl->micaConfiguration.IsInputActive(true);
            impl->mica.SetSystemBackdropConfiguration(impl->micaConfiguration);
        }
        catch (const winrt::hresult_error&) { impl->micaReady = false; }
    }

    void CardWindow::SetHotRegion(int id)
    {
        if (id == impl->hotRegion) return;
        const int previous = impl->hotRegion;
        impl->hotRegion = id;
        if ((previous && previous != 4) || (id && id != 4)) impl->mediaStaticDirty = true;
        if (!visible || environment.reducedMotion || environment.highContrast)
        {
            impl->hover.clear();
            if (visible && id) impl->hover[id] = { 1.0f, 1.0f, 0 };
        }
        else
        {
            const auto now = GetTickCount64();
            for (const int changed : { previous, id })
            {
                if (!changed) continue;
                auto& fade = impl->hover[changed];
                const float current = fade.Value(now);
                const float target = changed == id ? 1.0f : 0.0f;
                fade = { current, target, current == target ? 0 : now };
            }
        }
        impl->dirty = true;
        UpdateTimer();
    }

    Paint::Interaction CardWindow::CurrentInteraction() const
    {
        Paint::Interaction interaction;
        interaction.glass = impl->glassActive && !environment.highContrast;
        interaction.dark = environment.dark;
        interaction.reducedMotion = environment.reducedMotion;
        interaction.pressed = impl->dragging || impl->hotRegion == impl->capturedRegion ? impl->capturedRegion : 0;
        interaction.focused = impl->keyboardFocus ? impl->focusedRegion : 0;
        const auto now = GetTickCount64();
        for (const auto& [id, fade] : impl->hover) interaction.hover.emplace(id, fade.Value(now));
        return interaction;
    }

    void CardWindow::PruneInteraction()
    {
        for (auto item = impl->hover.begin(); item != impl->hover.end();)
        {
            const auto* region = regions.Find(item->first);
            if (!region || !region->enabled || (!item->second.started && item->second.to == 0.0f))
            {
                if (impl->hotRegion == item->first) impl->hotRegion = 0;
                item = impl->hover.erase(item);
            }
            else ++item;
        }
        const auto* captured = regions.Find(impl->capturedRegion);
        if (impl->capturedRegion && (!captured || !captured->enabled))
        {
            // A disconnected/disabled control cancels the gesture without
            // committing its last slider value to a different source/device.
            impl->dragging = false;
            impl->capturedRegion = 0;
            impl->sliderId = 0;
            impl->sliderAction.clear();
            impl->sliderPendingUntil = 0;
            impl->lastSliderSent = 0;
            if (GetCapture() == window) ReleaseCapture();
        }
        UpdateTimer();
    }

    bool CardWindow::OnPointer(UINT message, POINT pixels, bool leftDown)
    {
        UNREFERENCED_PARAMETER(leftDown);
        const POINT dip = ToDip(pixels);
        if (!impl->trackingLeave)
        {
            TRACKMOUSEEVENT event{ sizeof(event), TME_LEAVE, window, 0 };
            TrackMouseEvent(&event); impl->trackingLeave = true;
        }
        const auto* hit = regions.Hit(dip);
        const GlassControl* thumbHit = nullptr;
        if (impl->glassActive && !impl->glassControlsFailed)
            for (const auto& layer : impl->controlDescriptions)
                for (const auto& control : layer)
                {
                    if (control.kind != GlassControl::Kind::Thumb) continue;
                    const auto& b = control.bounds;
                    if (dip.x < b.left || dip.x > b.right || dip.y < b.top || dip.y > b.bottom) continue;
                    const auto* slider = regions.Find(control.id);
                    if (!slider || !slider->enabled) continue;
                    hit = slider; thumbHit = &control;
                }
        const int id = hit ? hit->id : 0;
        const bool actionable = hit && hit->enabled && hit->type != Layout::RegionType::Text;
        SetHotRegion(actionable ? id : 0);
        if (message == WM_LBUTTONDOWN || message == WM_LBUTTONDBLCLK)
        {
            if (!actionable) return false;
            const auto region = *hit;
            SetFocus(window); SetCapture(window);
            impl->capturedRegion = region.id;
            impl->focusedRegion = region.id;
            impl->keyboardFocus = false;
            accessibility.NotifyFocus(&region);
            if (region.type == Layout::RegionType::Slider)
            {
                impl->dragging = true;
                impl->sliderPointerOffset = thumbHit ? dip.x - (thumbHit->bounds.left + thumbHit->bounds.right) / 2 : 0;
                const float ratio = std::clamp((dip.x - impl->sliderPointerOffset - region.bounds.left) /
                    std::max(1.0f, static_cast<float>(region.bounds.right - region.bounds.left)), 0.0f, 1.0f);
                UpdateSlider(region, ratio);
            }
            impl->dirty = true;
            impl->mediaStaticDirty = true;
            if (visible) Paint();
            return true;
        }
        if (message == WM_MOUSEMOVE && impl->dragging)
        {
            if (const auto* found = regions.Find(impl->capturedRegion))
            {
                const auto region = *found;
                const float ratio = std::clamp((dip.x - impl->sliderPointerOffset - region.bounds.left) /
                    std::max(1.0f, static_cast<float>(region.bounds.right - region.bounds.left)), 0.0f, 1.0f);
                UpdateSlider(region, ratio);
            }
            return true;
        }
        if (message == WM_LBUTTONUP)
        {
            const auto* found = regions.Find(impl->capturedRegion);
            const std::optional<Layout::Region> region = found ? std::optional<Layout::Region>(*found) : std::nullopt;
            const bool dragged = impl->dragging;
            impl->dragging = false; impl->capturedRegion = 0;
            if (dragged) FinishSlider();
            if (GetCapture() == window) ReleaseCapture();
            if (!dragged && region && region->enabled && region->id == id) Activate(*region);
            impl->dirty = true;
            impl->mediaStaticDirty = true;
            if (visible) Paint();
            return true;
        }
        if (impl->dirty && visible) Paint();
        return false;
    }

    void CardWindow::CancelPointer()
    {
        if (!impl->dragging && !impl->capturedRegion) return;
        const bool dragged = impl->dragging;
        impl->dragging = false; impl->capturedRegion = 0;
        if (dragged) FinishSlider();
        if (GetCapture() == window) ReleaseCapture();
        impl->dirty = true;
        impl->mediaStaticDirty = true;
        if (visible) Paint();
    }

    void CardWindow::AcknowledgeSlider(const std::wstring& action)
    {
        if (impl->dragging || action != impl->sliderAction) return;
        impl->sliderPendingUntil = 0; impl->sliderId = 0; impl->sliderAction.clear();
        UpdateTimer();
    }

    void CardWindow::UpdateSlider(const Layout::Region& region, float ratio)
    {
        impl->sliderId = region.id;
        impl->sliderAction = region.action;
        impl->sliderRatio = ratio;
        impl->sliderSeeking = region.action == L"seek";
        impl->dirty = true;
        if (visible) Paint();
        if (impl->sliderSeeking) return; // progress commits on release only
        const ULONGLONG now = GetTickCount64();
        if (impl->lastSliderSent != 0 && now - impl->lastSliderSent < 120) return;
        impl->lastSliderSent = now;
        host.OnRequest(impl->sliderAction, std::to_wstring(ratio));
    }

    void CardWindow::FinishSlider()
    {
        if (impl->sliderId == 0) return;
        const std::wstring action = impl->sliderAction;
        const float ratio = impl->sliderRatio;
        impl->lastSliderSent = 0;
        impl->sliderPendingUntil = GetTickCount64() + 3000;
        if (!action.empty()) host.OnRequest(action, std::to_wstring(ratio));
        UpdateTimer();
    }

    void CardWindow::OnMouseLeave()
    {
        impl->trackingLeave = false;
        SetHotRegion(0);
        if (visible) Paint();
    }

    bool CardWindow::OnKey(UINT virtualKey, bool control, bool shift)
    {
        if (host.OnMenuKey(virtualKey)) return true;
        UNREFERENCED_PARAMETER(control);
        UNREFERENCED_PARAMETER(shift);
        if (virtualKey == VK_ESCAPE)
        {
            host.OnRequest(L"escape", L"");
            return true;
        }
        if (virtualKey == VK_TAB)
        {
            impl->keyboardFocus = true;
            const auto* current = regions.Find(impl->focusedRegion);
            const auto* next = regions.NextFocus(current, (GetKeyState(VK_SHIFT) & 0x8000) ? -1 : 1);
            impl->focusedRegion = next ? next->id : 0;
            accessibility.NotifyFocus(next);
            impl->dirty = true;
            impl->mediaStaticDirty = true;
            if (visible) Paint();
            return true;
        }
        if (virtualKey == VK_RETURN || virtualKey == VK_SPACE)
        {
            if (const auto* region = regions.Find(impl->focusedRegion))
            {
                if (region->enabled) { Activate(*region); return true; }
            }
            return false;
        }
        if (virtualKey == VK_LEFT || virtualKey == VK_RIGHT)
        {
            if (const auto* region = regions.Find(impl->focusedRegion))
            {
                if (region->type == Layout::RegionType::Slider && region->enabled)
                {
                    const auto copy = *region;
                    double value = 0;
                    if (copy.action == L"seek") value = snapshot.media.duration > 0 ? snapshot.media.position / snapshot.media.duration : 0;
                    else
                    {
                        const auto& endpoints = copy.action == L"setVolume:input" ? snapshot.audio.inputs : snapshot.audio.outputs;
                        for (const auto& endpoint : endpoints) if (endpoint.isDefault) value = endpoint.volume;
                    }
                    UpdateSlider(copy, static_cast<float>(std::clamp(value + (virtualKey == VK_LEFT ? -0.05 : 0.05), 0.0, 1.0)));
                    FinishSlider();
                    return true;
                }
            }
        }
        return false;
    }

    void CardWindow::Activate(const Layout::Region& region)
    {
        // Text regions exist for UI Automation; clicking a reading is not a command.
        if (!region.enabled || region.type == Layout::RegionType::Text || region.action.empty()) return;
        if (kind == Kind::PeripheralBattery && region.action.starts_with(L"peripheralPage:"))
        {
            SetPeripheralPage(peripheralPage + (region.action == L"peripheralPage:next" ? 1 : -1));
            return;
        }
        host.OnRequest(region.action, L"");
    }

    LRESULT CardWindow::OnGetObject(WPARAM keys, LPARAM position)
    {
        return accessibility.OnGetObject(keys, position);
    }

    std::wstring CardWindow::AccessibleSummary() const
    {
        const wchar_t* name = L"";
        switch (kind)
        {
        case Kind::Weather: name = L"天气"; break;
        case Kind::Traffic: name = L"流量"; break;
        case Kind::Device: name = L"音频设备"; break;
        case Kind::Media: name = L"媒体"; break;
        case Kind::Update: name = L"更新"; break;
        case Kind::PeripheralBattery: name = L"外设电量"; break;
        }
        return name;
    }

    std::wstring CardWindow::AccessibleName(const Layout::Region& region) const
    {
        return region.name;
    }
}
