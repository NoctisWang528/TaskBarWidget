#pragma once
#include "CardGraphics.h"

namespace Cards
{
    struct GlassControl;

    // Private Direct2D rendering. Composition receives only a completed surface,
    // never a displacement effect or a transformed backdrop brush.
    class RefractionSurface
    {
    public:
        bool Render(Graphics& graphics,
            const winrt::Windows::UI::Composition::CompositionGraphicsDevice& device,
            ID2D1Image* source, const D2D1_MATRIX_3X2_F& sourceToLocal,
            int width, int height, float radius, float lensHeight, float lensAmount,
            float blur, float scale, bool dark, const GlassControl* control = nullptr,
            float depthEffect = 0.f, float chromaticAberration = 0.f);
        winrt::Windows::UI::Composition::CompositionBrush Brush(
            const winrt::Windows::UI::Composition::Compositor& compositor);
        ID2D1Bitmap1* Scene() const { return scene.Get(); }
        bool LiveActive() const { return scene != nullptr; }
        void Release();
        HRESULT LastResult() const { return result; }
        const wchar_t* LastStage() const { return stage; }

    private:
        DrawingSurface surface;
        SurfaceHandle bound{nullptr};
        winrt::Windows::UI::Composition::CompositionSurfaceBrush brush{nullptr};
        Microsoft::WRL::ComPtr<ID2D1Bitmap1> scene, input;
        Microsoft::WRL::ComPtr<ID2D1Effect> transform, colorMatrix, gaussian, lens;
        HRESULT result{S_OK};
        const wchar_t* stage{L"inactive"};
    };
}
