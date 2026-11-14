#pragma once

#include "CardGraphics.h"
#include "CardRefraction.h"

#include <memory>

namespace Cards
{
    struct BackdropFrame;
    // A slight white lift in both themes; clear glass must not become a black panel.
    inline Color GlassTint(bool dark) { return {1.f, 1.f, 1.f, dark ? .025f : .06f}; }
    // Private captured-pixel refraction, with supported backdrop blur as fallback.
    class LiquidGlassBrush
    {
    public:
        bool Update(Graphics& graphics,
            const winrt::Windows::UI::Composition::Compositor& compositor,
            const winrt::Windows::UI::Composition::CompositionGraphicsDevice& device,
            float width, float height, float scale, double blur, bool dark);
        void Release();
        bool RenderBackdrop(Graphics& graphics,
            const winrt::Windows::UI::Composition::Compositor& compositor,
            const winrt::Windows::UI::Composition::CompositionGraphicsDevice& device,
            const BackdropFrame& frame, RECT bounds);
        void ClearBackdrop();
        ID2D1Bitmap1* Scene() const { return refraction.Scene(); }
        uint64_t SceneRevision() const { return sceneRevision; }
        bool LiveActive() const { return liveActive; }
        uint64_t RenderedFrames() const { return renderedFrames; }
        winrt::Windows::UI::Composition::ContainerVisual Visual() const { return visual; }
        winrt::Windows::UI::Composition::CompositionBrush Brush() const { return backdrop; }
        winrt::Windows::UI::Composition::CompositionBrush Rim() const { return rim; }
        HRESULT LastResult() const { return result; }
        const std::wstring& Diagnostic() const { return diagnostic; }
        static bool EffectsEnabled();

    private:
        bool MakeMasks(Graphics& graphics,
            const winrt::Windows::UI::Composition::Compositor& compositor,
            const winrt::Windows::UI::Composition::CompositionGraphicsDevice& device,
            float scale, bool dark);
        void MakeEffect(const winrt::Windows::UI::Composition::Compositor& compositor);

        DrawingSurface rimSurface;
        RefractionSurface refraction;
        winrt::Windows::UI::Composition::SpriteVisual base{nullptr};
        RECT frameBounds{};
        uint64_t frameSerial{}, sceneRevision{};
        float cachedBlur{};
        DrawingSurface tintSurface;
        winrt::Windows::UI::Composition::CompositionNineGridBrush tintMask{ nullptr };
        winrt::Windows::UI::Composition::CompositionMaskBrush scrim{ nullptr };
        winrt::Windows::UI::Composition::CompositionNineGridBrush rim{ nullptr };
        winrt::Windows::UI::Composition::ContainerVisual visual{ nullptr };
        winrt::Windows::UI::Composition::CompositionColorBrush tint{ nullptr };
        winrt::Windows::UI::Composition::CompositionEffectBrush backdrop{ nullptr };
        float cachedScale{};
        bool cachedDark{};
        bool liveActive{};
        uint64_t renderedFrames{};
        HRESULT result{ S_OK };
        std::wstring diagnostic;
    };

    // Labels stay crisp above the app-owned, refracted control surfaces.
    struct GlassControl
    {
        enum class Kind { Button, Thumb, Segment, Toggle };
        int id{};
        Kind kind{ Kind::Button };
        D2D1_RECT_F bounds{};
        float radius{}, hover{};
        bool pressed{}, focused{}, on{};
        bool enabled{true};
        D2D1_RECT_F track{};
        float ratio{};
        Color trackColor{}, fillColor{}, ink{};
        int glyph{ -1 };
    };

    class GlassControls
    {
    public:
        GlassControls();
        ~GlassControls();
        void Update(Graphics& graphics, const winrt::Windows::UI::Composition::Compositor& compositor,
            const winrt::Windows::UI::Composition::CompositionGraphicsDevice& device,
            const std::vector<GlassControl>& controls, float scale, bool dark, bool reducedMotion);
        void Release();
        bool RenderBackdrop(Graphics& graphics, const winrt::Windows::UI::Composition::Compositor& compositor,
            const winrt::Windows::UI::Composition::CompositionGraphicsDevice& device,
            ID2D1Bitmap1* source, uint64_t revision);
        void ClearBackdrop();
        winrt::Windows::UI::Composition::ContainerVisual Visual() const;
    private:
        struct Impl;
        std::unique_ptr<Impl> impl;
    };
}
