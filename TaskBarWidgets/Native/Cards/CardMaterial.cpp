// Rounded-rectangle distance and highlight profile adapted from
// AndroidLiquidGlass / Backdrop, Copyright 2025 Kyant (Apache-2.0).
// Windows adaptation: private Direct2D refraction, theme scrim and cached rim,
// with one supported backdrop blur as fallback.
// Never transform a backdrop or feed it through a zero-radius sampling workaround.
// See licenses/AndroidLiquidGlass-NOTICE.md and AndroidLiquidGlass-LICENSE.
#include "CardPch.h"
#include "CardMaterial.h"
#include "CardBackdropCapture.h"
#include "CardLayout.h"
#include "CardIcons.h"

#include <d2d1effects_2.h>
#include <windows.graphics.effects.interop.h>
#include <winrt/Windows.Graphics.Effects.h>
#include <winrt/Windows.UI.ViewManagement.h>

using namespace winrt::Windows::UI::Composition;
using winrt::Windows::Foundation::IPropertyValue;
using winrt::Windows::Foundation::PropertyValue;
using namespace winrt::Windows::Foundation::Numerics;
using namespace winrt::Windows::Graphics::Effects;
using EffectInterop = ABI::Windows::Graphics::Effects::IGraphicsEffectD2D1Interop;
using Mapping = ABI::Windows::Graphics::Effects::GRAPHICS_EFFECT_PROPERTY_MAPPING;

namespace Cards
{
    namespace
    {
        // SDK effect identifiers (the import library does not export the GUIDs).
        constexpr GUID BlurId{0x1feb6d69, 0x2fe6, 0x4ac9, {0x8c, 0x58, 0x1d, 0x7f, 0x93, 0xe7, 0xa6, 0xa5}};
        // Only the built-in Direct2D effects supported by Composition are exposed.
        // This small adapter avoids adding Win2D and another runtime dependency.
        struct Effect : winrt::implements<Effect, IGraphicsEffect, IGraphicsEffectSource, EffectInterop>
        {
            GUID id{};
            winrt::hstring name;
            std::vector<IPropertyValue> properties;
            std::vector<IGraphicsEffectSource> sources;
            std::vector<std::wstring> propertyNames;

            winrt::hstring Name() const { return name; }
            void Name(const winrt::hstring& value) { name = value; }
            HRESULT __stdcall GetEffectId(GUID* value) noexcept override { *value = id; return S_OK; }
            HRESULT __stdcall GetNamedPropertyMapping(LPCWSTR key, UINT* index, Mapping* mapping) noexcept override
            {
                for (UINT i = 0; i < propertyNames.size(); ++i)
                    if (propertyNames[i] == key)
                    {
                        *index = i;
                        *mapping = ABI::Windows::Graphics::Effects::GRAPHICS_EFFECT_PROPERTY_MAPPING_DIRECT;
                        return S_OK;
                    }
                return E_INVALIDARG;
            }
            HRESULT __stdcall GetPropertyCount(UINT* value) noexcept override
            { *value = static_cast<UINT>(properties.size()); return S_OK; }
            HRESULT __stdcall GetProperty(UINT index, ABI::Windows::Foundation::IPropertyValue** value) noexcept override
            {
                if (index >= properties.size()) return E_INVALIDARG;
                return properties[index].as(__uuidof(**value), reinterpret_cast<void**>(value));
            }
            HRESULT __stdcall GetSourceCount(UINT* value) noexcept override
            { *value = static_cast<UINT>(sources.size()); return S_OK; }
            HRESULT __stdcall GetSource(UINT index, ABI::Windows::Graphics::Effects::IGraphicsEffectSource** value) noexcept override
            {
                if (index >= sources.size()) return E_INVALIDARG;
                return sources[index].as(__uuidof(**value), reinterpret_cast<void**>(value));
            }
        };

        IGraphicsEffectSource Node(const GUID& id, const wchar_t* name,
            std::initializer_list<IPropertyValue> properties,
            std::initializer_list<IGraphicsEffectSource> sources,
            std::initializer_list<std::wstring> propertyNames = {})
        {
            auto effect = winrt::make_self<Effect>();
            effect->id = id;
            effect->name = name;
            effect->properties = properties;
            effect->sources = sources;
            effect->propertyNames = propertyNames;
            return effect.as<IGraphicsEffectSource>();
        }

        IPropertyValue Scalar(float value) { return PropertyValue::CreateSingle(value).as<IPropertyValue>(); }
        IPropertyValue Integer(UINT value) { return PropertyValue::CreateUInt32(value).as<IPropertyValue>(); }

        CompositionNineGridBrush NineGrid(const Compositor& compositor, const DrawingSurface& surface, float inset)
        {
            const auto image = compositor.CreateSurfaceBrush(surface.Handle());
            // None leaves the tiny source asset centered in a card-sized target.
            // The nine-grid preserves the corners; Fill stretches its center/edges.
            image.Stretch(CompositionStretch::Fill);
            auto nine = compositor.CreateNineGridBrush();
            nine.Source(image);
            nine.SetInsets(inset);
            return nine;
        }

        // Round-rect distance and gradient from upstream Shaders.kt. The CPU
        // evaluates this only for the small cached rim, never for desktop pixels.
        float Distance(float x, float y, float half, float radius)
        {
            const float qx = std::abs(x) - (half - radius), qy = std::abs(y) - (half - radius);
            return std::hypot(std::max(qx, 0.f), std::max(qy, 0.f)) - radius + std::min(std::max(qx, qy), 0.f);
        }

        float2 Normal(float x, float y, float half, float radius)
        {
            const float qx = std::abs(x) - (half - radius), qy = std::abs(y) - (half - radius);
            float nx = std::max(qx, 0.f), ny = std::max(qy, 0.f);
            if (qx < 0 && qy < 0) { nx = qx >= qy ? 1.f : 0.f; ny = 1.f - nx; }
            const float length = std::max(std::hypot(nx, ny), 0.0001f);
            return { std::copysign(nx / length, x), std::copysign(ny / length, y) };
        }
    }

    bool LiquidGlassBrush::EffectsEnabled()
    {
        try { return winrt::Windows::UI::ViewManagement::UISettings().AdvancedEffectsEnabled(); }
        catch (...) { return false; }
    }

    void LiquidGlassBrush::Release()
    {
        ClearBackdrop();
        base = nullptr;
        visual = nullptr;
        tint = nullptr;
        scrim = nullptr;
        tintMask = nullptr;
        tintSurface.Release();
        backdrop = nullptr;
        rim = nullptr;
        rimSurface.Release();
        cachedScale = 0;
        liveActive = false;
        diagnostic.clear();
    }

    bool LiquidGlassBrush::MakeMasks(Graphics& graphics, const Compositor& compositor,
        const CompositionGraphicsDevice& device, float scale, bool dark)
    {
        const float inset = std::ceil(std::max(Layout::CornerRadius * 1.5f + 2, 26.f) * scale);
        const int pixels = static_cast<int>(inset * 2 + 2);
        const float size = static_cast<float>(pixels) / scale;
        // A directional white reflection plus a subtle inner shade. Its alpha
        // follows the SDF, so highlights join continuously around rounded corners.
        std::vector<unsigned char> pixelsData(static_cast<size_t>(pixels) * pixels * 4);
        for (int y = 0; y < pixels; ++y)
            for (int x = 0; x < pixels; ++x)
            {
                const float px = (x + .5f) / scale - size / 2, py = (y + .5f) / scale - size / 2;
                const float d = -Distance(px, py, size / 2, Layout::CornerRadius);
                const auto normal = Normal(px, py, size / 2, Layout::CornerRadius * 1.5f);
                const float lighting = std::pow(std::abs((normal.x + normal.y) * .70710678f), 1.6f);
                const float line = std::clamp(1.f - std::abs(d - .7f) * scale, 0.f, 1.f);
                const float inner = d > 1.5f ? .07f * std::exp(-(d - 1.5f) / 2.f) : 0.f;
                const float alpha = d >= 0 ? std::clamp(line * (.14f + .46f * lighting) + inner, 0.f, 1.f) : 0.f;
                const bool white = dark || normal.x + normal.y < 0;
                const unsigned char a = static_cast<unsigned char>(std::lround(alpha * 255));
                const auto index = (static_cast<size_t>(y) * pixels + x) * 4;
                pixelsData[index] = pixelsData[index + 1] = pixelsData[index + 2] = white ? a : 0;
                pixelsData[index + 3] = a;
            }
        rimSurface.Ensure(device, pixels, pixels);
        {
            DrawScope draw(rimSurface);
            if (!draw) winrt::throw_hresult(FAILED(rimSurface.LastResult()) ? rimSurface.LastResult() : E_FAIL);
            draw->Clear(D2D1::ColorF(0, 0.f));
            Microsoft::WRL::ComPtr<ID2D1Bitmap> bitmap;
            winrt::check_hresult(draw->CreateBitmap(D2D1::SizeU(pixels, pixels), pixelsData.data(), pixels * 4,
                D2D1::BitmapProperties(D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM,
                    D2D1_ALPHA_MODE_PREMULTIPLIED)), &bitmap));
            draw->DrawBitmap(bitmap.Get());
        }
        winrt::check_hresult(rimSurface.LastResult());
        rim = NineGrid(compositor, rimSurface, inset);
        // Keep the reading scrim in the body; a lighter edge preserves rim contrast
        // and lets the 24-DIP optical lens edge stay clear and luminous.
        for (int y = 0; y < pixels; ++y)
            for (int x = 0; x < pixels; ++x)
            {
                const float d = -Distance((x + .5f) / scale - size / 2,
                    (y + .5f) / scale - size / 2, size / 2, Layout::CornerRadius);
                const float t = std::clamp(d / 24.f, 0.f, 1.f);
                const float smooth = t * t * (3.f - 2.f * t);
                const auto alpha = static_cast<unsigned char>(std::lround(255 * smooth));
                const auto index = (static_cast<size_t>(y) * pixels + x) * 4;
                for (int channel = 0; channel < 4; ++channel) pixelsData[index + channel] = alpha;
            }
        tintSurface.Ensure(device, pixels, pixels);
        {
            DrawScope draw(tintSurface);
            if (!draw) winrt::throw_hresult(tintSurface.LastResult());
            draw->Clear(D2D1::ColorF(0, 0.f));
            Microsoft::WRL::ComPtr<ID2D1Bitmap> bitmap;
            winrt::check_hresult(draw->CreateBitmap(D2D1::SizeU(pixels, pixels), pixelsData.data(), pixels * 4,
                D2D1::BitmapProperties(D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED)), &bitmap));
            draw->DrawBitmap(bitmap.Get());
        }
        winrt::check_hresult(tintSurface.LastResult());
        tintMask = NineGrid(compositor, tintSurface, inset);
        cachedScale = scale;
        cachedDark = dark;
        return true;
    }

    void LiquidGlassBrush::MakeEffect(const Compositor& compositor)
    {
        visual = compositor.CreateContainerVisual();
        visual.RelativeSizeAdjustment({ 1, 1 });
        const auto add = [&](const CompositionBrush& brush)
        {
            auto layer = compositor.CreateSpriteVisual();
            layer.RelativeSizeAdjustment({ 1, 1 });
            layer.Brush(brush);
            visual.Children().InsertAtTop(layer);
            return layer;
        };
        const auto blur = Node(BlurId, L"Blur",
            { Scalar(0), Integer(D2D1_GAUSSIANBLUR_OPTIMIZATION_BALANCED), Integer(D2D1_BORDER_MODE_HARD) },
            { CompositionEffectSourceParameter(L"Backdrop") }, { L"Amount", L"Optimization", L"BorderMode" });
        backdrop = compositor.CreateEffectFactory(blur.as<IGraphicsEffect>(), {L"Blur.Amount"}).CreateBrush();
        backdrop.SetSourceParameter(L"Backdrop", compositor.CreateBackdropBrush());
        base = add(backdrop);
        // A single, untransformed backdrop is the entire background effect graph.
        tint = compositor.CreateColorBrush();
        scrim = compositor.CreateMaskBrush();
        scrim.Source(tint);
        scrim.Mask(tintMask);
        add(scrim);
    }

    bool LiquidGlassBrush::Update(Graphics& graphics, const Compositor& compositor,
        const CompositionGraphicsDevice& device, float width, float height, float scale, double blur, bool dark)
    {
        try
        {
            scale = std::max(.5f, scale);
            width = std::max(width, 1.f);
            height = std::max(height, 1.f);
            const float amount = std::isfinite(blur) ? static_cast<float>(std::clamp(blur, 0., 1.)) : .35f;
            if (cachedScale != scale || cachedDark != dark || cachedBlur != amount) frameSerial = 0;
            cachedBlur = amount;
            if (cachedScale != scale || cachedDark != dark || !rim)
                if (!MakeMasks(graphics, compositor, device, scale, dark))
                    winrt::throw_hresult(E_FAIL);
            if (!visual) MakeEffect(compositor);
            scrim.Mask(tintMask);
            backdrop.Properties().InsertScalar(L"Blur.Amount", 24.f * amount * scale);
            // Keep the base clear. Theme tint is shared with the control input,
            // so neither path adds a fixed dark panel over the captured scene.
            const auto glassTint = GlassTint(dark);
            tint.Color({static_cast<uint8_t>(std::lround(glassTint.a * 255)), 255, 255, 255});
            result = S_OK;
            diagnostic = liveActive ? L"live-refraction" : L"fallback-blur";
            return true;
        }
        catch (const winrt::hresult_error& error) { result = error.code(); diagnostic = error.message().c_str(); }
        catch (...) { result = E_FAIL; }
        Release();
        return false;
    }

    void LiquidGlassBrush::ClearBackdrop()
    {
        try { if (base) base.Brush(backdrop); } catch (...) {}
        refraction.Release(); frameSerial = 0;
        liveActive = false;
        if (SUCCEEDED(result)) diagnostic = L"fallback-blur";
    }

    bool LiquidGlassBrush::RenderBackdrop(Graphics& graphics, const Compositor& compositor,
        const CompositionGraphicsDevice& device, const BackdropFrame& frame, RECT bounds)
    {
        if (!frame.bitmap || !base) return false;
        if (frameSerial == frame.serial && EqualRect(&frameBounds, &bounds) && refraction.Scene())
        {
            liveActive = true;
            diagnostic = L"live-refraction";
            return true;
        }
        const auto size = frame.bitmap->GetPixelSize();
        auto rotate = D2D1::Matrix3x2F::Identity();
        switch (frame.rotation)
        {
        case DXGI_MODE_ROTATION_ROTATE90: rotate = D2D1::Matrix3x2F(0, 1, -1, 0, static_cast<float>(size.height), 0); break;
        case DXGI_MODE_ROTATION_ROTATE180: rotate = D2D1::Matrix3x2F(-1, 0, 0, -1, static_cast<float>(size.width), static_cast<float>(size.height)); break;
        case DXGI_MODE_ROTATION_ROTATE270: rotate = D2D1::Matrix3x2F(0, -1, 1, 0, 0, static_cast<float>(size.width)); break;
        default: break;
        }
        const auto position = rotate * D2D1::Matrix3x2F::Translation(
            static_cast<float>(frame.screenBounds.left - bounds.left), static_cast<float>(frame.screenBounds.top - bounds.top));
        if (!refraction.Render(graphics, device, frame.bitmap.Get(), position, bounds.right - bounds.left,
            bounds.bottom - bounds.top, Layout::CornerRadius * cachedScale, 24.f * cachedScale, 48.f * cachedScale,
            24.f * cachedBlur * cachedScale, cachedScale, cachedDark, nullptr, 1.f, 1.f))
        {
            result = refraction.LastResult();
            diagnostic = L"refraction/" + std::wstring(refraction.LastStage());
            ClearBackdrop();
            return false;
        }
        base.Brush(refraction.Brush(compositor));
        frameBounds = bounds; frameSerial = frame.serial; ++sceneRevision; ++renderedFrames;
        liveActive = true;
        result = S_OK;
        diagnostic = L"live-refraction";
        return true;
    }

    namespace
    {
        winrt::Windows::UI::Color UiColor(Color color)
        {
            const auto channel = [](float v) { return static_cast<uint8_t>(std::lround(std::clamp(v, 0.f, 1.f) * 255)); };
            return { channel(color.a), channel(color.r), channel(color.g), channel(color.b) };
        }

        void RoundedSprite(const Compositor& compositor, const SpriteVisual& visual,
            D2D1_RECT_F rect, float radius, float scale, Color color)
        {
            const float width = std::max(0.f, rect.right - rect.left) * scale;
            const float height = std::max(0.f, rect.bottom - rect.top) * scale;
            // A zero-volume fill is hidden; do not submit a degenerate clip.
            visual.IsVisible(width > 0 && height > 0);
            if (width <= 0 || height <= 0) return;
            visual.Offset({rect.left * scale, rect.top * scale, 0});
            visual.Size({width, height});
            if (!visual.Clip())
                visual.Clip(compositor.CreateGeometricClip(compositor.CreateRoundedRectangleGeometry()));
            auto geometry = visual.Clip().as<CompositionGeometricClip>().Geometry().as<CompositionRoundedRectangleGeometry>();
            geometry.Size({width, height});
            const float corner = std::clamp(radius * scale, 0.f, std::min(width, height) / 2);
            geometry.CornerRadius({corner, corner});
            if (!visual.Brush()) visual.Brush(compositor.CreateColorBrush());
            visual.Brush().as<CompositionColorBrush>().Color(UiColor(color));
        }

        void ImplicitMotion(const Compositor& compositor, const Visual& visual, bool slide, bool enabled)
        {
            if (!enabled)
            {
                visual.ImplicitAnimations(nullptr);
                visual.StopAnimation(L"Offset"); visual.StopAnimation(L"Scale");
                return;
            }
            auto animations = compositor.CreateImplicitAnimationCollection();
            for (const auto* property : {L"Offset", L"Scale"})
            {
                if (!slide && std::wcscmp(property, L"Offset") == 0) continue;
                auto animation = compositor.CreateVector3KeyFrameAnimation();
                animation.Target(property);
                animation.InsertExpressionKeyFrame(1.f, L"this.FinalValue",
                    compositor.CreateCubicBezierEasingFunction({.2f, .8f}, {.2f, 1.f}));
                animation.Duration(std::chrono::milliseconds(slide ? 180 : 130));
                animations.Insert(property, animation);
            }
            visual.ImplicitAnimations(animations);
        }
    }

    struct GlassControls::Impl
    {
        struct Control
        {
            ContainerVisual root{nullptr}, panel{nullptr}, pulse{nullptr};
            SpriteVisual track{nullptr}, fill{nullptr}, lens{nullptr}, icon{nullptr};
            ShapeVisual rim{nullptr};
            ContainerVisual sample{nullptr};
            SpriteVisual sampleTrack{nullptr}, sampleFill{nullptr};
            CompositionRoundedRectangleGeometry geometry{nullptr};
            CompositionSpriteShape shape{nullptr};
            CompositionColorBrush tint{nullptr}, focus{nullptr};
            CompositionLinearGradientBrush highlight{nullptr};
            DrawingSurface iconSurface;
            RefractionSurface refraction;
            uint64_t sourceRevision{};
            GlassControl previous;
            bool hasPrevious{};
            int glyph{-1};
            float scale{};
            bool dark{}, motion{};
        };
        ContainerVisual root{nullptr};
        std::map<int, std::unique_ptr<Control>> controls;
    };

    GlassControls::GlassControls() : impl(std::make_unique<Impl>()) {}
    GlassControls::~GlassControls() = default;
    ContainerVisual GlassControls::Visual() const { return impl->root; }
    void GlassControls::Release() { impl = std::make_unique<Impl>(); }

    void GlassControls::Update(Graphics& graphics, const Compositor& compositor,
        const CompositionGraphicsDevice& device, const std::vector<GlassControl>& descriptions,
        float scale, bool dark, bool reducedMotion)
    {
        const auto appearance = [](const GlassControl& c)
        {
            return std::make_tuple(c.kind, c.bounds.left, c.bounds.top, c.bounds.right, c.bounds.bottom,
                c.radius, c.hover, c.pressed, c.focused, c.on, c.enabled, c.track.left, c.track.top, c.track.right, c.track.bottom,
                c.ratio, UiColor(c.trackColor), UiColor(c.fillColor), UiColor(c.ink), c.glyph);
        };
        if (!impl->root)
        {
            impl->root = compositor.CreateContainerVisual();
            impl->root.RelativeSizeAdjustment({1, 1});
        }
        const auto validBounds = [](const GlassControl& c)
        { return c.bounds.right - c.bounds.left > 1 && c.bounds.bottom - c.bounds.top > 1; };
        for (auto it = impl->controls.begin(); it != impl->controls.end();)
        {
            if (std::none_of(descriptions.begin(), descriptions.end(), [&](const auto& d) { return d.id == it->first && validBounds(d); }))
            { impl->root.Children().Remove(it->second->root); it = impl->controls.erase(it); }
            else ++it;
        }
        for (const auto& description : descriptions)
        {
            if (!validBounds(description)) continue;
            const bool thumb = description.kind == GlassControl::Kind::Thumb || description.kind == GlassControl::Kind::Toggle;
            const bool slide = description.kind == GlassControl::Kind::Segment || description.kind == GlassControl::Kind::Toggle;
            const bool fresh = !impl->controls.contains(description.id);
            if (fresh)
            {
                auto control = std::make_unique<Impl::Control>();
                control->root = compositor.CreateContainerVisual();
                control->track = compositor.CreateSpriteVisual();
                control->fill = compositor.CreateSpriteVisual();
                control->panel = compositor.CreateContainerVisual();
                control->pulse = compositor.CreateContainerVisual();
                control->panel.Children().InsertAtTop(control->pulse);
                control->lens = compositor.CreateSpriteVisual();
                // The colour-only rail is the fallback until a private refracted
                // surface is available. No control uses a backdrop brush.
                control->sample = compositor.CreateContainerVisual();
                control->sampleTrack = compositor.CreateSpriteVisual();
                control->sampleFill = compositor.CreateSpriteVisual();
                control->sample.Children().InsertAtTop(control->sampleTrack);
                control->sample.Children().InsertAtTop(control->sampleFill);
                control->lens.Children().InsertAtTop(control->sample);
                control->geometry = compositor.CreateRoundedRectangleGeometry();
                control->lens.Clip(compositor.CreateGeometricClip(control->geometry));
                control->rim = compositor.CreateShapeVisual();
                control->shape = compositor.CreateSpriteShape(control->geometry);
                control->tint = compositor.CreateColorBrush();
                control->focus = compositor.CreateColorBrush();
                control->shape.FillBrush(control->tint);
                control->highlight = compositor.CreateLinearGradientBrush();
                control->highlight.StartPoint({0, 0}); control->highlight.EndPoint({1, 1});
                for (const auto& stop : {std::pair{0.f, .85f}, {.3f, .15f}, {.65f, .12f}, {1.f, .65f}})
                    control->highlight.ColorStops().Append(compositor.CreateColorGradientStop(stop.first,
                        UiColor({1, 1, 1, stop.second})));
                control->rim.Shapes().Append(control->shape);
                control->pulse.Children().InsertAtTop(control->lens);
                control->pulse.Children().InsertAtTop(control->rim);
                control->icon = compositor.CreateSpriteVisual();
                control->pulse.Children().InsertAtTop(control->icon);
                control->root.Children().InsertAtTop(control->track);
                control->root.Children().InsertAtTop(control->fill);
                control->root.Children().InsertAtTop(control->panel);
                impl->root.Children().InsertAtTop(control->root);
                impl->controls.emplace(description.id, std::move(control));
            }
            auto& control = *impl->controls.at(description.id);
            if (control.hasPrevious && control.scale == scale && control.dark == dark &&
                control.motion == !reducedMotion && appearance(control.previous) == appearance(description)) continue;
            const auto& bounds = description.bounds;
            const float width = (bounds.right - bounds.left) * scale, height = (bounds.bottom - bounds.top) * scale;
            const float hover = std::clamp(description.hover, 0.f, 1.f);
            const float press = description.pressed ? 1.f : 0.f;
            control.sourceRevision = 0;
            control.root.Opacity(description.enabled ? 1.f : .45f);
            if (fresh || control.motion == reducedMotion)
            {
                // Set initial geometry before enabling implicit animation.
                if (fresh) control.panel.Offset({bounds.left * scale, bounds.top * scale, 0});
                ImplicitMotion(compositor, control.panel, slide, !reducedMotion);
                control.motion = !reducedMotion;
            }
            control.panel.Offset({bounds.left * scale, bounds.top * scale, 0});
            control.panel.CenterPoint({width / 2, height / 2, 0});
            const float magnify = reducedMotion ? 1.f : thumb ? 1.f + .22f * press + .04f * hover :
                1.f + (description.kind == GlassControl::Kind::Segment ? .12f : .07f) * press + .025f * hover;
            control.panel.Scale({magnify, magnify, 1});
            control.pulse.CenterPoint({width / 2, height / 2, 0});
            if (reducedMotion)
            { control.pulse.StopAnimation(L"Scale"); control.pulse.Scale({1, 1, 1}); }
            else if (!fresh && description.enabled && description.on && description.kind == GlassControl::Kind::Segment &&
                (control.previous.bounds.left != bounds.left || control.previous.bounds.top != bounds.top))
            {
                auto pulse = compositor.CreateVector3KeyFrameAnimation();
                pulse.InsertKeyFrame(0.f, float3{1, 1, 1});
                pulse.InsertKeyFrame(.35f, float3{1.18f, 1.26f, 1});
                pulse.InsertKeyFrame(1.f, float3{1, 1, 1}, compositor.CreateCubicBezierEasingFunction({.2f, .8f}, {.2f, 1.f}));
                pulse.Duration(std::chrono::milliseconds(280));
                control.pulse.StartAnimation(L"Scale", pulse);
            }
            control.lens.Size({width, height});
            control.rim.Size({width, height});
            control.geometry.Offset({.5f * scale, .5f * scale});
            control.geometry.Size({std::max(0.f, width - scale), std::max(0.f, height - scale)});
            const float corner = std::clamp(description.radius * scale, 0.f, std::min(width - scale, height - scale) / 2);
            control.geometry.CornerRadius({corner, corner});
            // Preserve the upstream thumb press proportions. The enlargement
            // below affects ordinary colour visuals only, never a backdrop.
            const float amount = thumb ? 14.f * press : 0.f;
            const float sx = 1.f + amount * scale / std::max(width * .5f, 1.f);
            const float sy = 1.f + amount * scale / std::max(height * .5f, 1.f);
            control.sample.CenterPoint({width / 2, height / 2, 0});
            control.sample.Scale({sx, sy, 1});
            control.sample.IsVisible(!control.refraction.Scene() && thumb && description.pressed && description.track.bottom > description.track.top);
            const float tint = thumb ? (.94f * (1.f - press) + (dark ? .12f : .24f) * press) :
                (description.kind == GlassControl::Kind::Segment
                    ? ((dark ? .22f : .34f) * (1.f - .35f * press))
                    : ((dark ? .09f : .20f) * (1.f - .25f * press))) + hover * .06f * (1.f - press);
            control.tint.Color(UiColor({1, 1, 1, tint}));
            if (fresh || control.dark != dark)
            {
                const float alpha[]{.85f, dark ? .15f : .10f, dark ? .12f : .14f, .65f};
                for (uint32_t i = 0; i < 4; ++i)
                {
                    const float shade = !dark && (i == 1 || i == 2) ? 0.f : 1.f;
                    control.highlight.ColorStops().GetAt(i).Color(UiColor({shade, shade, shade, alpha[i]}));
                }
            }
            control.focus.Color(UiColor(description.ink));
            control.shape.StrokeBrush(description.focused ? control.focus.as<CompositionBrush>() : control.highlight.as<CompositionBrush>());
            control.shape.StrokeThickness((description.focused ? 1.5f : 1.f) * scale);
            const float trackHeight = description.track.bottom - description.track.top;
            control.track.IsVisible(trackHeight > 0);
            control.fill.IsVisible(trackHeight > 0);
            if (trackHeight > 0)
            {
                RoundedSprite(compositor, control.track, description.track, trackHeight / 2, scale, description.trackColor);
                auto fill = description.track;
                fill.right = fill.left + (fill.right - fill.left) * std::clamp(description.ratio, 0.f, 1.f);
                RoundedSprite(compositor, control.fill, fill, trackHeight / 2, scale, description.fillColor);
                auto localTrack = description.track;
                localTrack.left -= bounds.left; localTrack.right -= bounds.left;
                localTrack.top -= bounds.top; localTrack.bottom -= bounds.top;
                auto localFill = fill;
                localFill.left -= bounds.left; localFill.right -= bounds.left;
                localFill.top -= bounds.top; localFill.bottom -= bounds.top;
                RoundedSprite(compositor, control.sampleTrack, localTrack, trackHeight / 2, scale, description.trackColor);
                RoundedSprite(compositor, control.sampleFill, localFill, trackHeight / 2, scale, description.fillColor);
            }
            control.icon.IsVisible(description.glyph >= 0);
            if (description.glyph >= 0)
            {
                if (control.glyph != description.glyph || control.scale != scale || control.dark != dark ||
                    control.previous.pressed != description.pressed)
                {
                    control.iconSurface.Ensure(device, static_cast<int>(std::ceil(16 * scale)), static_cast<int>(std::ceil(16 * scale)));
                    {
                        DrawScope draw(control.iconSurface, scale);
                        if (!draw) winrt::throw_hresult(control.iconSurface.LastResult());
                        draw->Clear(D2D1::ColorF(0, 0.f));
                        DrawGlyph(draw.Get(), static_cast<Glyph>(description.glyph),
                            dark && description.pressed ? Color{1, 1, 1, 1} : Color{.12f, .12f, .12f, 1}, 0, 0, 16);
                    }
                    winrt::check_hresult(control.iconSurface.LastResult());
                    control.icon.Brush(compositor.CreateSurfaceBrush(control.iconSurface.Handle()));
                    control.glyph = description.glyph; control.scale = scale; control.dark = dark;
                }
                control.icon.Size({16 * scale, 16 * scale});
                control.icon.Offset({(width - 16 * scale) / 2, (height - 16 * scale) / 2, 0});
            }
            control.previous = description; control.hasPrevious = true;
            control.scale = scale; control.dark = dark;
        }
    }

    bool GlassControls::RenderBackdrop(Graphics& graphics, const Compositor& compositor,
        const CompositionGraphicsDevice& device, ID2D1Bitmap1* source, uint64_t revision)
    {
        if (!source) return false;
        for (auto& [id, pointer] : impl->controls)
        {
            auto& control = *pointer;
            if (control.sourceRevision == revision && control.refraction.Scene()) continue;
            const auto& d = control.previous;
            const float scale = control.scale;
            const bool thumb = d.kind == GlassControl::Kind::Thumb || d.kind == GlassControl::Kind::Toggle;
            const float hover = std::clamp(d.hover, 0.f, 1.f);
            const float press = d.pressed ? 1.f : 0.f;
            const float blur = thumb ? (8.f * (1.f - press) * (1.f - .35f * hover)) : (2.f * (1.f - .5f * press));
            const float height = thumb ? (10.f + 2.f * press) : (d.kind == GlassControl::Kind::Segment ? 24.f : 12.f);
            const float amount = thumb ? (4.f + 4.f * hover + 10.f * press) : (24.f + 10.f * press);
            const float depth = thumb ? (.4f + .6f * press) : (.5f + .5f * press);
            const float dispersion = thumb ? (.35f * hover + press) : (.65f + .35f * press);
            if (!control.refraction.Render(graphics, device, source,
                D2D1::Matrix3x2F::Translation(-d.bounds.left * scale, -d.bounds.top * scale),
                static_cast<int>(std::ceil((d.bounds.right - d.bounds.left) * scale)),
                static_cast<int>(std::ceil((d.bounds.bottom - d.bounds.top) * scale)),
                d.radius * scale, height * scale, amount * scale, blur * scale, scale, control.dark, &d,
                depth, dispersion)) return false;
            control.lens.Brush(control.refraction.Brush(compositor));
            control.sample.IsVisible(false);
            control.sourceRevision = revision;
        }
        return true;
    }

    void GlassControls::ClearBackdrop()
    {
        for (auto& [id, pointer] : impl->controls)
        {
            auto& control = *pointer;
            try
            {
                control.lens.Brush(nullptr);
                control.sample.IsVisible(control.previous.pressed && control.previous.track.bottom > control.previous.track.top);
            }
            catch (...) {}
            control.refraction.Release(); control.sourceRevision = 0;
        }
    }
}
