#include "CardPch.h"
#include "CardWindow.h"
#include "CardDraw.h"
#include "CardText.h"

using Microsoft::WRL::ComPtr;
using namespace winrt::Windows::UI::Composition;

namespace Cards
{
    namespace
    {
        constexpr wchar_t ClassName[] = L"TaskBarWidgetsCardWindow";
        constexpr int AnimationMilliseconds = 150;
        constexpr float AnimationOffsetDip = 6.0f;
        constexpr UINT_PTR AnimationTimer = 0xCD01;
        constexpr ULONGLONG CoverFadeMilliseconds = 200;
        constexpr ULONGLONG LyricWrapMilliseconds = 180;

        LRESULT CALLBACK CardProc(HWND window, UINT message, WPARAM keys, LPARAM position)
        {
            auto* card = reinterpret_cast<CardWindow*>(GetWindowLongPtrW(window, GWLP_USERDATA));
            if (!card) return DefWindowProcW(window, message, keys, position);
            switch (message)
            {
            case WM_ERASEBKGND:
                return 1;
            case WM_PAINT:
            {
                PAINTSTRUCT paint{};
                BeginPaint(window, &paint);
                EndPaint(window, &paint);
                card->Paint();
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
        ContainerVisual motion{ nullptr };
        SpriteVisual wash{ nullptr };
        SpriteVisual content{ nullptr };
        DrawingSurface surface;
        muxc::MicaController mica{ nullptr };
        bool micaReady{ false };
        bool micaFailed{ false };
        bool dirty{ true };
        RECT damage{};                 // DIP; empty means the whole card
        int hotRegion{ 0 };
        int focusedRegion{ 0 };
        int capturedRegion{ 0 };
        bool trackingLeave{ false };
        bool dragging{ false };
        // A dragged slider shows the pointer value immediately; the snapshot only
        // wins again after the gesture ends, so a stale reply cannot pull it back.
        int sliderId{};
        std::wstring sliderAction;
        float sliderRatio{};
        bool sliderSeeking{};
        ULONGLONG lastSliderSent{};
        // Lyric scanning is interpolated on the card thread: a line frame carries
        // the position within that line, so the card learns the rate from two
        // consecutive frames and advances the highlight until the next one.
        long long lyricsRevision{ -1 };
        double lyricsProgress{};
        double lyricsRate{};
        ULONGLONG lastLyricPaint{};
        // Cover cross-fade: the outgoing bitmap is released as soon as the fade ends.
        std::wstring coverKey;
        std::wstring previousCoverKey;
        ULONGLONG coverFadeStart{};
        // Wrap transition state for the lyric rows.
        std::wstring lyricPrevious;
        std::wstring lyricPreviousSecondary;
        ULONGLONG lyricWrapStart{};
        int lyricLineIndex{ -1 };
        ULONGLONG lyricLineStarted{};
        ULONGLONG lyricsTick{};
        std::wstring draggingFlow;
        std::wstring openMenu;         // "output" / "input" / "source"
        int openMenuSelected{ 0 };
        std::vector<Layout::Region> menuItems;
        float opacity{ 1.0f };
        float offset{ 0.0f };
        float fromOpacity{ 1.0f };
        float fromOffset{ 0.0f };
        bool animating{ false };
        std::chrono::steady_clock::time_point started{};
    };

    CardWindow::CardWindow(Kind cardKind, CardHostServices& services)
        : impl(std::make_unique<Impl>()), kind(cardKind), host(services)
    {
    }

    CardWindow::~CardWindow() { Destroy(); }

    bool CardWindow::Create(const CardEnvironment& cardEnvironment, HINSTANCE instance)
    {
        environment = cardEnvironment;
        EnsureClass(instance);
        const DWORD style = WS_POPUP;
        const DWORD extended = WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE |
            WS_EX_NOREDIRECTIONBITMAP;
        window = CreateWindowExW(extended, ClassName, L"", style, 0, 0,
            static_cast<int>(Layout::CardWidth), static_cast<int>(Layout::HeightMedia),
            nullptr, nullptr, instance, nullptr);
        if (!window) return false;
        SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(this));
        SetTimer(window, AnimationTimer, 16, nullptr);
        BuildVisualTree(instance);
        ApplyEnvironment(cardEnvironment);
        if (!impl->compositor) return false;
        accessibility.Attach(window, AccessibleSummary(), [this] { return std::cref(regions); },
            [this] { return scale; },
            [this](int id)
            {
                // Slider ratios mirror what the painter shows.
                if (snapshot.audio.outputs.size() + snapshot.audio.inputs.size() == 0) return 0.0;
                double value = 0.0;
                for (const auto& endpoint : snapshot.audio.outputs) value = (std::max)(value, endpoint.volume);
                for (const auto& endpoint : snapshot.audio.inputs) value = (std::max)(value, endpoint.volume);
                UNREFERENCED_PARAMETER(id);
                return value;
            },
            [this](const Layout::Region& region, const std::wstring& payload)
            {
                if (auto* host = Host()) host->OnRequest(region.action, payload);
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

            // Motion container: the show/hide animation changes only its opacity
            // and offset. The HWND geometry never moves with the animation.
            impl->motion = impl->compositor.CreateContainerVisual();
            impl->motion.RelativeSizeAdjustment(winrt::Windows::Foundation::Numerics::float2{ 1.0f, 1.0f });
            impl->root.Children().InsertAtTop(impl->motion);

            impl->wash = MakeLayer(impl->compositor);
            impl->content = MakeLayer(impl->compositor);
            impl->motion.Children().InsertAtTop(impl->wash);
            impl->motion.Children().InsertAtTop(impl->content);
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
        accessibility.Detach();
        if (window)
        {
            KillTimer(window, AnimationTimer);
            SetWindowLongPtrW(window, GWLP_USERDATA, 0);
        }
        if (impl->mica)
        {
            try { impl->mica.RemoveAllSystemBackdropTargets(); } catch (...) { }
            impl->mica = nullptr;
        }
        impl->surface.Release();
        if (impl->content) impl->content.Brush(nullptr);
        impl->content = nullptr;
        impl->wash = nullptr;
        impl->motion = nullptr;
        impl->root = nullptr;
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
        const bool themeChanged = environment.dark != cardEnvironment.dark;
        environment = cardEnvironment;

        // One controller per window: a theme or tuning change only reconfigures it.
        if (!impl->mica && !impl->micaFailed && window && impl->target)
        {
            try
            {
                if (!muxc::MicaController::IsSupported())
                {
                    impl->micaFailed = true;
                }
                else
                {
                    auto controller = muxc::MicaController();
                    auto configuration = muxc::SystemBackdropConfiguration();
                    configuration.IsInputActive(true);
                    configuration.Theme(environment.dark ? muxc::SystemBackdropTheme::Dark
                        : muxc::SystemBackdropTheme::Light);
                    controller.SetSystemBackdropConfiguration(configuration);
                    const winrt::Microsoft::UI::WindowId windowId{
                        static_cast<uint64_t>(reinterpret_cast<INT_PTR>(window)) };
                    if (controller.SetTarget(windowId, impl->target))
                    {
                        impl->mica = controller;
                        impl->micaReady = true;
                    }
                    else
                    {
                        impl->micaFailed = true;
                    }
                }
            }
            catch (const winrt::hresult_error&)
            {
                impl->mica = nullptr;
                impl->micaReady = false;
                impl->micaFailed = true;
            }
        }

        if (impl->mica)
        {
            try
            {
                const auto& tuning = environment.mica;
                const auto fallback = FallbackColor(environment.dark);
                impl->mica.TintColor(fallback);
                impl->mica.FallbackColor(fallback);
                if (tuning.vivid)
                {
                    impl->mica.Kind(MicaKindFor(environment.dark));
                    impl->mica.TintOpacity(static_cast<float>(tuning.TintFor(environment.dark)));
                    impl->mica.LuminosityOpacity(static_cast<float>(tuning.LuminosityFor(environment.dark)));
                }
                else
                {
                    // Custom Mica off: the system default look, which is what
                    // ResetProperties() restores for the WinUI path.
                    impl->mica.Kind(muxc::MicaKind::Base);
                    impl->mica.TintOpacity(0.0f);
                    impl->mica.LuminosityOpacity(1.0f);
                }
                impl->micaReady = true;
            }
            catch (const winrt::hresult_error&)
            {
                impl->micaFailed = true;
            }
        }

        // The wash keeps content readable when Mica is unavailable, transparency
        // is off, or high contrast is on.
        if (impl->wash && impl->compositor)
        {
            const uint8_t alpha = environment.highContrast ? 255 : (impl->micaReady ? 18 : 235);
            const auto color = environment.dark
                ? winrt::Windows::UI::Color{ alpha, 32, 32, 32 }
                : winrt::Windows::UI::Color{ alpha, 250, 250, 250 };
            impl->wash.Brush(impl->compositor.CreateColorBrush(color));
        }
        if (themeChanged) { impl->dirty = true; impl->damage = RECT{}; }
    }

    void CardWindow::SetSnapshot(const Snapshot& value)
    {
        const auto now = GetTickCount64();
        if (value.lyrics.revision != impl->lyricsRevision)
        {
            if (impl->lyricsRevision >= 0 && impl->lyricsTick != 0 && value.lyrics.isPlaying)
            {
                const double delta = static_cast<double>(now - impl->lyricsTick) / 1000.0;
                const double advance = value.lyrics.progress - impl->lyricsProgress;
                // Only a forward move inside one line teaches a usable rate.
                if (delta > 0.05 && advance >= 0.0 && advance < 0.9) impl->lyricsRate = advance / delta;
            }
            else if (value.lyrics.revision != impl->lyricsRevision)
            {
                impl->lyricsRate = 0.0;
            }
            impl->lyricsRevision = value.lyrics.revision;
            impl->lyricsProgress = value.lyrics.progress;
            impl->lyricsTick = now;
        }
        if (kind == Kind::Media && value.lyrics.revision != impl->lyricsRevision &&
            value.lyrics.currentLineIndex != impl->lyricLineIndex &&
            value.lyrics.animateTransition && !snapshot.lyrics.current.empty())
        {
            // The outgoing line stays only for the transition; it is released as
            // soon as the animation ends.
            impl->lyricPrevious = snapshot.lyrics.current;
            impl->lyricPreviousSecondary = snapshot.lyrics.currentTranslation.empty()
                ? snapshot.lyrics.next : snapshot.lyrics.currentTranslation;
            impl->lyricWrapStart = GetTickCount64();
        }
        if (value.lyrics.currentLineIndex != impl->lyricLineIndex)
            impl->lyricLineStarted = GetTickCount64();
        impl->lyricLineIndex = value.lyrics.currentLineIndex;
        if (kind == Kind::Media && value.media.coverKey != impl->coverKey)
        {
            impl->previousCoverKey = impl->coverKey;
            impl->coverKey = value.media.coverKey;
            impl->coverFadeStart = GetTickCount64();
        }
        snapshot = value;
        impl->dirty = true;
        impl->damage = RECT{};
        if (visible) Paint();
    }

    void CardWindow::Invalidate()
    {
        impl->dirty = true;
        if (window) InvalidateRect(window, nullptr, FALSE);
    }

    POINT CardWindow::ToDip(POINT pixels) const
    {
        return Layout::ToDip(pixels, scale);
    }

    float CardWindow::ContentWidthDip() const { return Layout::WidthDip(kind); }

    float CardWindow::ContentHeightDip() const
    {
        return measuredHeightDip > 0.0f ? measuredHeightDip : Layout::DefaultHeightDip(kind);
    }

    void CardWindow::Place(const RECT& targetBounds, float targetScale)
    {
        bounds = targetBounds;
        scale = targetScale > 0.0f ? targetScale : 1.0f;
        measuredHeightDip = static_cast<float>(targetBounds.bottom - targetBounds.top) / scale;
        if (window)
        {
            SetWindowPos(window, HWND_TOPMOST, bounds.left, bounds.top,
                bounds.right - bounds.left, bounds.bottom - bounds.top,
                SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }
        impl->dirty = true;
        impl->damage = RECT{};
    }

    void CardWindow::ApplyMotion()
    {
        if (!impl->motion) return;
        impl->motion.Opacity(impl->opacity);
        impl->motion.Offset(winrt::Windows::Foundation::Numerics::float3{ 0.0f,
            -impl->offset * scale, 0.0f });
    }

    void CardWindow::Show(bool animate)
    {
        if (!window) return;
        visible = true;
        ShowWindow(window, SW_SHOWNOACTIVATE);
        SetWindowPos(window, HWND_TOPMOST, bounds.left, bounds.top,
            bounds.right - bounds.left, bounds.bottom - bounds.top,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
        if (animate && !environment.reducedMotion)
        {
            impl->fromOpacity = 0.0f;
            impl->fromOffset = AnimationOffsetDip;
            impl->started = std::chrono::steady_clock::now();
            impl->animating = true;
            impl->opacity = 0.0f;
            impl->offset = AnimationOffsetDip;
        }
        else
        {
            impl->animating = false;
            impl->opacity = 1.0f;
            impl->offset = 0.0f;
        }
        ApplyMotion();
        impl->dirty = true;
        impl->damage = RECT{};
        Paint();
    }

    void CardWindow::Hide(bool animate)
    {
        visible = false;
        if (!window) return;
        ClosePopup();
        if (animate && !environment.reducedMotion && impl->opacity > 0.01f)
        {
            impl->fromOpacity = impl->opacity;
            impl->fromOffset = impl->offset;
            impl->started = std::chrono::steady_clock::now();
            impl->animating = true;
        }
        else
        {
            impl->animating = false;
            impl->opacity = 0.0f;
            impl->offset = 0.0f;
            ApplyMotion();
            ShowWindow(window, SW_HIDE);
        }
    }

    bool CardWindow::Tick()
    {
        if (!impl->animating && visible && kind == Kind::Media && impl->lyricLineStarted != 0 &&
            snapshot.lyrics.isPlaying)
        {
            const double age = static_cast<double>(GetTickCount64() - impl->lyricLineStarted) / 1000.0;
            if (age > 0.8 && age < 7.0)
            {
                impl->dirty = true;
                Paint();
                return true;
            }
        }
        if (!impl->animating && visible && impl->lyricWrapStart != 0)
        {
            if (GetTickCount64() - impl->lyricWrapStart < LyricWrapMilliseconds)
            {
                impl->dirty = true;
                Paint();
            }
            else
            {
                impl->lyricPrevious.clear();
                impl->lyricPreviousSecondary.clear();
                impl->lyricWrapStart = 0;
                Paint();
            }
            return true;
        }
        if (!impl->animating && visible && impl->coverFadeStart != 0)
        {
            if (GetTickCount64() - impl->coverFadeStart < CoverFadeMilliseconds)
            {
                impl->dirty = true;
                Paint();
            }
            else
            {
                impl->previousCoverKey.clear();
                impl->coverFadeStart = 0;
                Paint();
            }
            return true;
        }
        // Lyrics advance between snapshots, so the media card keeps a timer while
        // it is visible and playing even when no motion animation is running.
        if (!impl->animating && visible && kind == Kind::Media && snapshot.lyrics.isPlaying &&
            impl->lyricsRate > 0.0)
        {
            // The scan advances between frames; repaint at roughly 30 Hz instead of
            // the 60 Hz timer so a playing card never redraws at full rate.
            const ULONGLONG now = GetTickCount64();
            if (now - impl->lastLyricPaint >= 33)
            {
                impl->lastLyricPaint = now;
                const double position = impl->lyricsProgress + impl->lyricsRate *
                    (static_cast<double>(now - impl->lyricsTick) / 1000.0);
                if (position < 1.0)
                {
                    impl->dirty = true;
                    Paint();
                }
            }
            return true;
        }
        if (!impl->animating) return false;
        const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now() - impl->started).count();
        float progress = static_cast<float>(elapsed) / static_cast<float>(AnimationMilliseconds);
        progress = std::clamp(progress, 0.0f, 1.0f);
        const float target = visible ? 1.0f : 0.0f;
        impl->opacity = impl->fromOpacity + (target - impl->fromOpacity) * progress;
        impl->offset = impl->fromOffset * (1.0f - progress);
        ApplyMotion();
        if (progress >= 1.0f)
        {
            impl->animating = false;
            impl->opacity = target;
            impl->offset = 0.0f;
            ApplyMotion();
            if (!visible) ShowWindow(window, SW_HIDE);
        }
        return impl->animating;
    }

    void CardWindow::PaintContent(bool rebuildRegions)
    {
        if (!visible && impl->opacity <= 0.01f) return;
        if (!impl->compositor || !environment.device || !environment.graphics) return;
        const int widthPixels = (std::max)(1, static_cast<int>(bounds.right - bounds.left));
        const int heightPixels = (std::max)(1, static_cast<int>(bounds.bottom - bounds.top));
        impl->surface.Ensure(environment.device, widthPixels, heightPixels);
        if (!impl->surface.Ready()) return;

        if (rebuildRegions) regions.Clear();
        {
            DrawScope scope(impl->surface);
            if (!scope) { impl->dirty = true; return; }
            ID2D1DeviceContext* context = scope.Get();
            context->SetDpi(96.0f * scale, 96.0f * scale);
            context->SetTransform(D2D1::Matrix3x2F::Identity());
            context->Clear(D2D1::ColorF(0, 0.0f));

            const float widthDip = ContentWidthDip();
            const float heightDip = ContentHeightDip();
            // The surface is painted at physical size; scaling by the fit factor
            // keeps the DIP layout above identical to the WinUI card.
            const float fit = (bounds.right - bounds.left) / std::max(1.0f, widthDip * scale);
            context->SetTransform(D2D1::Matrix3x2F::Scale(scale * fit, scale * fit));

            // A dragged slider is painted from the pointer until the gesture ends,
            // so an older snapshot can never drag the knob back mid-drag.
            Snapshot painted = snapshot;
            if (painted.lyrics.isPlaying && impl->lyricsRate > 0.0 && impl->lyricsTick != 0)
            {
                const double elapsed = static_cast<double>(GetTickCount64() - impl->lyricsTick) / 1000.0;
                painted.lyrics.progress = std::clamp(
                    impl->lyricsProgress + impl->lyricsRate * elapsed, 0.0, 1.0);
            }
            if (impl->sliderId != 0 && !impl->sliderSeeking)
            {
                for (auto* list : { &painted.audio.outputs, &painted.audio.inputs })
                    for (auto& endpoint : *list) endpoint.volume = impl->sliderRatio;
            }
            else if (impl->sliderId != 0 && impl->sliderSeeking)
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
            if (kind == Kind::Media && !impl->previousCoverKey.empty() && impl->coverFadeStart != 0)
            {
                const ULONGLONG elapsed = GetTickCount64() - impl->coverFadeStart;
                if (elapsed < CoverFadeMilliseconds)
                {
                    cover.previous = environment.graphics->DecodeImage(impl->previousCoverKey,
                        static_cast<int>(64 * scale * fit), static_cast<int>(64 * scale * fit));
                    cover.previousOpacity = 1.0f - static_cast<float>(elapsed) /
                        static_cast<float>(CoverFadeMilliseconds);
                }
                else
                {
                    // The old cover is dropped the moment the transition ends.
                    impl->previousCoverKey.clear();
                    impl->coverFadeStart = 0;
                }
            }
            switch (kind)
            {
            case Kind::Media:
                Paint::Media(context, painted, cover, lyricTransition, environment, regions, widthDip,
                    heightDip);
                break;
            case Kind::Device:
                Paint::Device(context, painted, environment, regions, widthDip, heightDip);
                break;
            case Kind::Traffic:
                Paint::Traffic(context, painted, environment, regions, widthDip, heightDip);
                break;
            case Kind::Weather:
                Paint::Weather(context, painted, environment, regions, widthDip, heightDip);
                break;
            case Kind::Update:
                Paint::Update(context, painted, environment, regions, widthDip, heightDip);
                break;
            }
        }
        if (!impl->content.Brush() && impl->surface.Handle())
        {
            auto brush = impl->compositor.CreateSurfaceBrush(impl->surface.Handle());
            brush.Stretch(CompositionStretch::Fill);
            impl->content.Brush(brush);
        }
        impl->dirty = false;
        impl->damage = RECT{};
    }

    void CardWindow::PaintPopup()
    {
        // The menus are drawn into the same surface as the card content so they
        // follow it during the open/close animation and never steal focus.
    }

    void CardWindow::Paint()
    {
        if (!impl->compositor) return;
        const bool rebuild = impl->dirty || !impl->surface.Ready();
        PaintContent(rebuild);
    }

    void CardWindow::ClosePopup()
    {
        if (impl->openMenu.empty()) return;
        impl->openMenu.clear();
        impl->menuItems.clear();
        impl->openMenuSelected = 0;
        host.OnMenuOpened(false);
        impl->dirty = true;
        if (visible) Paint();
    }

    void CardWindow::OpenMenu(const std::wstring& menu, const std::vector<Layout::Region>& items)
    {
        impl->openMenu = menu;
        impl->menuItems = items;
        impl->openMenuSelected = 0;
        host.OnMenuOpened(true);
    }

    const Layout::Region* CardWindow::HitRegion(POINT dip, bool includePopup) const
    {
        UNREFERENCED_PARAMETER(includePopup);
        if (const auto* region = regions.Hit(dip)) return region;
        return nullptr;
    }

    bool CardWindow::OnPointer(UINT message, POINT pixels, bool leftDown)
    {
        const POINT dip = ToDip(pixels);
        if (!impl->trackingLeave)
        {
            TRACKMOUSEEVENT event{ sizeof(event) };
            event.dwFlags = TME_LEAVE;
            event.hwndTrack = window;
            TrackMouseEvent(&event);
            impl->trackingLeave = true;
        }

        const auto* region = HitRegion(dip, true);
        const int id = region ? region->id : 0;
        if (id != impl->hotRegion)
        {
            impl->hotRegion = id;
            impl->dirty = true;
        }

        if (message == WM_MOUSEMOVE)
        {
            if (impl->dragging && region)
            {
                if (region->type == Layout::RegionType::Slider && region->enabled)
                {
                    const float width = static_cast<float>(region->bounds.right - region->bounds.left);
                    const float ratio = std::clamp(
                        static_cast<float>(dip.x - region->bounds.left) / std::max(1.0f, width), 0.0f, 1.0f);
                    UpdateSlider(*region, ratio);
                }
            }
            if (impl->dirty && visible) Paint();
            return false;
        }

        if (message == WM_LBUTTONDOWN)
        {
            if (!region) return false;
            if (!region->enabled) return true;
            if (region->type == Layout::RegionType::Slider)
            {
                impl->dragging = true;
                impl->capturedRegion = region->id;
                const float width = static_cast<float>(region->bounds.right - region->bounds.left);
                const float ratio = std::clamp(
                    static_cast<float>(dip.x - region->bounds.left) / std::max(1.0f, width), 0.0f, 1.0f);
                UpdateSlider(*region, ratio);
                return true;
            }
            impl->capturedRegion = region->id;
            return true;
        }

        if (message == WM_LBUTTONUP)
        {
            const bool dragging = impl->dragging;
            impl->dragging = false;
            const int captured = impl->capturedRegion;
            impl->capturedRegion = 0;
            if (!region || !region->enabled) return dragging;
            if (region->type == Layout::RegionType::Slider)
            {
                // The gesture always commits its final value, even if the drag was
                // shorter than the coalescing interval.
                FinishSlider();
                return true;
            }
            if (captured != region->id) return true;
            Activate(*region);
            return true;
        }
        return false;
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
        host.OnRequest(region.action, std::to_wstring(ratio));
    }

    void CardWindow::FinishSlider()
    {
        if (impl->sliderId == 0) return;
        const std::wstring action = impl->sliderAction;
        const float ratio = impl->sliderRatio;
        impl->sliderId = 0;
        impl->sliderAction.clear();
        impl->lastSliderSent = 0;
        if (!action.empty()) host.OnRequest(action, std::to_wstring(ratio));
    }

    void CardWindow::OnMouseLeave()
    {
        impl->trackingLeave = false;
        if (impl->hotRegion)
        {
            impl->hotRegion = 0;
            impl->dirty = true;
            if (visible) Paint();
        }
    }

    bool CardWindow::OnEscape()
    {
        if (!impl->openMenu.empty())
        {
            ClosePopup();
            return true;
        }
        return false;
    }

    bool CardWindow::OnKey(UINT virtualKey, bool control, bool shift)
    {
        UNREFERENCED_PARAMETER(control);
        UNREFERENCED_PARAMETER(shift);
        if (virtualKey == VK_ESCAPE)
        {
            if (OnEscape()) return true;
            host.OnHideCards(L"escape");
            return true;
        }
        if (virtualKey == VK_TAB)
        {
            const auto* current = regions.Find(impl->focusedRegion);
            const auto* next = regions.NextFocus(current, (GetKeyState(VK_SHIFT) & 0x8000) ? -1 : 1);
            impl->focusedRegion = next ? next->id : 0;
            impl->dirty = true;
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
                    const float step = (virtualKey == VK_LEFT ? -1.0f : 1.0f) * 0.05f;
                    host.OnRequest(region->action + L".step", std::to_wstring(step));
                    return true;
                }
            }
        }
        return false;
    }

    void CardWindow::Activate(const Layout::Region& region)
    {
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
        }
        return name;
    }

    std::wstring CardWindow::AccessibleName(const Layout::Region& region) const
    {
        return region.name;
    }
}
