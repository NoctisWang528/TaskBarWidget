// Distance/normal, circular lens profile and 7-tap spectral chromatic dispersion
// adapted from AndroidLiquidGlass, Copyright 2025 Kyant (Apache-2.0).
// See licenses/AndroidLiquidGlass-NOTICE.md.
#include "CardPch.h"
#include "CardRefraction.h"
#include "CardMaterial.h"

#include <d2d1effectauthor.h>
#include <d2d1effecthelpers.h>
#include <d2d1effects_2.h>
#include <d3dcompiler.h>

#include <cstring>
#include <limits>

namespace Cards
{
    namespace
    {
        using Microsoft::WRL::ComPtr;

        // {8D47D8B6-6F09-4C7A-A16E-2E47A0E3F101}
        constexpr CLSID ClsidLiquidLensEffect = {
            0x8d47d8b6, 0x6f09, 0x4c7a, { 0xa1, 0x6e, 0x2e, 0x47, 0xa0, 0xe3, 0xf1, 0x01 }
        };
        // {8D47D8B6-6F09-4C7A-A16E-2E47A0E3F102}
        constexpr GUID GuidLiquidLensShader = {
            0x8d47d8b6, 0x6f09, 0x4c7a, { 0xa1, 0x6e, 0x2e, 0x47, 0xa0, 0xe3, 0xf1, 0x02 }
        };

        struct alignas(16) LensParams
        {
            float width{};
            float height{};
            float offsetX{};
            float offsetY{};
            float cornerRadius{};
            float refractionHeight{};
            float refractionAmount{};
            float depthEffect{};
            float chromaticAberration{};
            float pad[3]{};

            bool operator==(const LensParams&) const = default;
        };
        static_assert(sizeof(LensParams) == 48);

        constexpr wchar_t kLensEffectXml[] = LR"(<?xml version='1.0'?>
<Effect>
    <Property name='DisplayName' type='string' value='LiquidLens'/>
    <Property name='Author' type='string' value='TaskBarWidgets'/>
    <Property name='Category' type='string' value='Stylize'/>
    <Property name='Description' type='string' value='Rounded rectangle lens refraction and spectral dispersion'/>
    <Inputs>
        <Input name='Source'/>
    </Inputs>
    <Property name='Params' type='blob'>
        <Property name='DisplayName' type='string' value='Params'/>
    </Property>
</Effect>)";

        constexpr char kLensShaderSource[] = R"(
Texture2D<float4> InputTexture0 : register(t0);
SamplerState InputSampler0 : register(s0);

cbuffer constants : register(b0)
{
    float2 size;
    float2 offset;
    float cornerRadius;
    float refractionHeight;
    float refractionAmount;
    float depthEffect;
    float chromaticAberration;
    float3 _pad;
};

float2 safeNormalize(float2 v, float2 fallback)
{
    float lenSq = dot(v, v);
    return lenSq > 1e-8 ? v * rsqrt(lenSq) : fallback;
}

float sdRoundedRect(float2 coord, float2 halfSize, float radius)
{
    float2 cornerCoord = abs(coord) - (halfSize - float2(radius, radius));
    float outside = length(max(cornerCoord, 0.0));
    float inside = min(max(cornerCoord.x, cornerCoord.y), 0.0);
    return outside + inside - radius;
}

float2 gradSdRoundedRect(float2 coord, float2 halfSize, float radius)
{
    float2 cornerCoord = abs(coord) - (halfSize - float2(radius, radius));
    float2 signs = coord >= 0.0 ? float2(1.0, 1.0) : float2(-1.0, -1.0);
    if (cornerCoord.x >= 0.0 || cornerCoord.y >= 0.0)
    {
        return signs * safeNormalize(max(cornerCoord, 0.0), float2(1.0, 0.0));
    }
    else
    {
        float gradX = step(cornerCoord.y, cornerCoord.x);
        return signs * float2(gradX, 1.0 - gradX);
    }
}

float circleMap(float x)
{
    return 1.0 - sqrt(max(0.0, 1.0 - x * x));
}

float4 sampleAt(float4 scenePos, float4 uv0, float2 p)
{
    float2 texCoord = uv0.xy + uv0.zw * (p - scenePos.xy);
    return InputTexture0.SampleLevel(InputSampler0, texCoord, 0.0);
}

float4 main(
    float4 pos : SV_POSITION,
    float4 scenePos : SCENE_POSITION,
    float4 uv0 : TEXCOORD0) : SV_TARGET
{
    float2 coord = scenePos.xy;
    float2 halfSize = max(size * 0.5, float2(1.0, 1.0));
    float2 centeredCoord = coord - offset - halfSize;
    float clampedRadius = clamp(cornerRadius, 0.0, min(halfSize.x, halfSize.y));

    float sd = sdRoundedRect(centeredCoord, halfSize, clampedRadius);
    if (sd >= 0.0 || refractionHeight <= 0.01 || abs(refractionAmount) <= 0.001)
    {
        return InputTexture0.SampleLevel(InputSampler0, uv0.xy, 0.0);
    }

    float t = 1.0 - min(-sd / max(refractionHeight, 0.01), 1.0);
    if (t <= 0.0)
    {
        return InputTexture0.SampleLevel(InputSampler0, uv0.xy, 0.0);
    }

    // Negative sign matches upstream Lens(-refractionAmount) inward convex magnification.
    float d = -circleMap(t) * refractionAmount;
    float gradRadius = min(max(clampedRadius * 1.5, refractionHeight), min(halfSize.x, halfSize.y));
    float2 normal = gradSdRoundedRect(centeredCoord, halfSize, gradRadius);
    if (depthEffect > 0.001)
    {
        float lenC = length(centeredCoord);
        float2 radial = lenC > 1e-4 ? (centeredCoord / lenC) : float2(0.0, 0.0);
        float radialFade = smoothstep(0.0, 4.0, lenC);
        normal = safeNormalize(normal + (depthEffect * radialFade) * radial, normal);
    }

    float2 refractedCoord = coord + d * normal;
    if (chromaticAberration <= 0.001)
    {
        return sampleAt(scenePos, uv0, refractedCoord);
    }

    float cornerFactor = (centeredCoord.x * centeredCoord.y) / max(halfSize.x * halfSize.y, 1.0);
    float intensity = chromaticAberration * (0.35 + 0.35 * abs(cornerFactor));
    float2 dispersedCoord = d * normal * intensity;

    float4 red = sampleAt(scenePos, uv0, refractedCoord + dispersedCoord);
    float4 orange = sampleAt(scenePos, uv0, refractedCoord + dispersedCoord * (2.0 / 3.0));
    float4 yellow = sampleAt(scenePos, uv0, refractedCoord + dispersedCoord * (1.0 / 3.0));
    float4 green = sampleAt(scenePos, uv0, refractedCoord);
    float4 cyan = sampleAt(scenePos, uv0, refractedCoord - dispersedCoord * (1.0 / 3.0));
    float4 blue = sampleAt(scenePos, uv0, refractedCoord - dispersedCoord * (2.0 / 3.0));
    float4 purple = sampleAt(scenePos, uv0, refractedCoord - dispersedCoord);

    float4 color = float4(0.0, 0.0, 0.0, 0.0);
    color.r += red.r / 3.5;    color.a += red.a / 7.0;
    color.r += orange.r / 3.5; color.g += orange.g / 7.0; color.a += orange.a / 7.0;
    color.r += yellow.r / 3.5; color.g += yellow.g / 3.5; color.a += yellow.a / 7.0;
    color.g += green.g / 3.5;  color.a += green.a / 7.0;
    color.g += cyan.g / 3.5;   color.b += cyan.b / 3.0;   color.a += cyan.a / 7.0;
    color.b += blue.b / 3.0;   color.a += blue.a / 7.0;
    color.r += purple.r / 7.0; color.b += purple.b / 3.0; color.a += purple.a / 7.0;
    color = saturate(color);
    color.rgb = min(color.rgb, color.a);
    return color;
}
)";

        const std::vector<BYTE>& CompiledLensShader(HRESULT& compileResult)
        {
            static std::vector<BYTE> bytecode;
            static HRESULT cachedResult = E_FAIL;
            static std::once_flag once;
            std::call_once(once, []()
            {
                HMODULE compiler = LoadLibraryExW(L"d3dcompiler_47.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
                if (!compiler) compiler = LoadLibraryW(L"d3dcompiler_47.dll");
                if (!compiler)
                {
                    cachedResult = HRESULT_FROM_WIN32(GetLastError());
                    if (SUCCEEDED(cachedResult)) cachedResult = E_FAIL;
                    return;
                }
                auto compile = reinterpret_cast<pD3DCompile>(GetProcAddress(compiler, "D3DCompile"));
                if (!compile)
                {
                    cachedResult = HRESULT_FROM_WIN32(GetLastError());
                    if (SUCCEEDED(cachedResult)) cachedResult = E_FAIL;
                    FreeLibrary(compiler);
                    return;
                }
                ComPtr<ID3DBlob> code, errors;
                cachedResult = compile(
                    kLensShaderSource, std::strlen(kLensShaderSource),
                    "LiquidLens.hlsl", nullptr, nullptr,
                    "main", "ps_4_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0,
                    &code, &errors);
                if (FAILED(cachedResult))
                {
                    if (errors && errors->GetBufferPointer())
                        OutputDebugStringA(static_cast<const char*>(errors->GetBufferPointer()));
                    FreeLibrary(compiler);
                    return;
                }
                const auto* bytes = static_cast<const BYTE*>(code->GetBufferPointer());
                bytecode.assign(bytes, bytes + code->GetBufferSize());
                FreeLibrary(compiler);
            });
            compileResult = cachedResult;
            return bytecode;
        }

        D2D1_RECT_L ExpandRectSafe(const D2D1_RECT_L& rect, LONG pad)
        {
            constexpr LONGLONG kMin = (std::numeric_limits<LONG>::min)() / 2;
            constexpr LONGLONG kMax = (std::numeric_limits<LONG>::max)() / 2;
            const auto clampAdd = [kMin, kMax](LONG value, LONGLONG delta) -> LONG
            {
                return static_cast<LONG>(std::clamp(static_cast<LONGLONG>(value) + delta, kMin, kMax));
            };
            return {
                clampAdd(rect.left, -static_cast<LONGLONG>(pad)),
                clampAdd(rect.top, -static_cast<LONGLONG>(pad)),
                clampAdd(rect.right, static_cast<LONGLONG>(pad)),
                clampAdd(rect.bottom, static_cast<LONGLONG>(pad))
            };
        }

        class LensEffect final : public ID2D1EffectImpl, public ID2D1DrawTransform
        {
        public:
            static HRESULT CALLBACK CreateEffect(_Outptr_ IUnknown** effectImpl)
            {
                if (!effectImpl) return E_INVALIDARG;
                auto* instance = new (std::nothrow) LensEffect();
                if (!instance)
                {
                    *effectImpl = nullptr;
                    return E_OUTOFMEMORY;
                }
                *effectImpl = static_cast<ID2D1EffectImpl*>(instance);
                return S_OK;
            }

            IFACEMETHODIMP QueryInterface(REFIID riid, _COM_Outptr_ void** output) override
            {
                if (!output) return E_INVALIDARG;
                *output = nullptr;
                if (riid == __uuidof(ID2D1EffectImpl)) *output = static_cast<ID2D1EffectImpl*>(this);
                else if (riid == __uuidof(ID2D1DrawTransform)) *output = static_cast<ID2D1DrawTransform*>(this);
                else if (riid == __uuidof(ID2D1Transform)) *output = static_cast<ID2D1Transform*>(this);
                else if (riid == __uuidof(ID2D1TransformNode)) *output = static_cast<ID2D1TransformNode*>(this);
                else if (riid == __uuidof(IUnknown)) *output = static_cast<ID2D1EffectImpl*>(this);
                else return E_NOINTERFACE;
                AddRef();
                return S_OK;
            }

            IFACEMETHODIMP_(ULONG) AddRef() override
            {
                return refCount.fetch_add(1, std::memory_order_relaxed) + 1;
            }

            IFACEMETHODIMP_(ULONG) Release() override
            {
                const ULONG count = refCount.fetch_sub(1, std::memory_order_acq_rel) - 1;
                if (count == 0) delete this;
                return count;
            }

            IFACEMETHODIMP Initialize(
                _In_ ID2D1EffectContext* effectContext,
                _In_ ID2D1TransformGraph* transformGraph) override
            {
                if (!effectContext || !transformGraph) return E_INVALIDARG;
                HRESULT compileHr = S_OK;
                const auto& bytecode = CompiledLensShader(compileHr);
                if (FAILED(compileHr) || bytecode.empty()) return FAILED(compileHr) ? compileHr : E_FAIL;
                if (!effectContext->IsShaderLoaded(GuidLiquidLensShader))
                {
                    const HRESULT hr = effectContext->LoadPixelShader(
                        GuidLiquidLensShader, bytecode.data(), static_cast<UINT32>(bytecode.size()));
                    if (FAILED(hr)) return hr;
                }
                return transformGraph->SetSingleTransformNode(static_cast<ID2D1DrawTransform*>(this));
            }

            IFACEMETHODIMP PrepareForRender(D2D1_CHANGE_TYPE changeType) override
            {
                UNREFERENCED_PARAMETER(changeType);
                return UpdateConstants();
            }

            IFACEMETHODIMP SetGraph(_In_ ID2D1TransformGraph* transformGraph) override
            {
                UNREFERENCED_PARAMETER(transformGraph);
                return E_NOTIMPL;
            }

            IFACEMETHODIMP_(UINT32) GetInputCount() const override { return 1; }

            IFACEMETHODIMP SetDrawInfo(_In_ ID2D1DrawInfo* info) override
            {
                if (!info) return E_INVALIDARG;
                drawInfo = info;
                const D2D1_INPUT_DESCRIPTION inputDesc{ D2D1_FILTER_MIN_MAG_MIP_LINEAR, 0 };
                HRESULT hr = drawInfo->SetInputDescription(0, inputDesc);
                if (FAILED(hr)) return hr;
                hr = drawInfo->SetPixelShader(GuidLiquidLensShader, D2D1_PIXEL_OPTIONS_NONE);
                if (FAILED(hr)) return hr;
                drawInfo->SetInstructionCountHint(96);
                return UpdateConstants();
            }

            IFACEMETHODIMP MapOutputRectToInputRects(
                _In_ const D2D1_RECT_L* outputRect,
                _Out_writes_(inputRectsCount) D2D1_RECT_L* inputRects,
                UINT32 inputRectsCount) const override
            {
                if (inputRectsCount != 1 || !outputRect || !inputRects) return E_INVALIDARG;
                inputRects[0] = ExpandRectSafe(*outputRect, SampleMargin());
                return S_OK;
            }

            IFACEMETHODIMP MapInputRectsToOutputRect(
                _In_reads_(inputRectCount) const D2D1_RECT_L* inputRects,
                _In_reads_(inputRectCount) const D2D1_RECT_L* inputOpaqueSubRects,
                UINT32 inputRectCount,
                _Out_ D2D1_RECT_L* outputRect,
                _Out_ D2D1_RECT_L* outputOpaqueSubRect) override
            {
                UNREFERENCED_PARAMETER(inputOpaqueSubRects);
                if (inputRectCount != 1 || !inputRects || !outputRect || !outputOpaqueSubRect) return E_INVALIDARG;
                *outputRect = inputRects[0];
                *outputOpaqueSubRect = {};
                return S_OK;
            }

            IFACEMETHODIMP MapInvalidRect(
                UINT32 inputIndex,
                D2D1_RECT_L invalidInputRect,
                _Out_ D2D1_RECT_L* invalidOutputRect) const override
            {
                if (inputIndex != 0 || !invalidOutputRect) return E_INVALIDARG;
                *invalidOutputRect = ExpandRectSafe(invalidInputRect, SampleMargin());
                return S_OK;
            }

            HRESULT SetParams(const BYTE* data, UINT32 dataSize)
            {
                if (!data || dataSize != sizeof(LensParams)) return E_INVALIDARG;
                std::memcpy(&params, data, sizeof(params));
                return S_OK;
            }

            HRESULT GetParams(BYTE* data, UINT32 dataSize, UINT32* actualSize) const
            {
                if (actualSize) *actualSize = sizeof(params);
                if (!data) return S_OK;
                if (dataSize < sizeof(params)) return E_NOT_SUFFICIENT_BUFFER;
                std::memcpy(data, &params, sizeof(params));
                return S_OK;
            }

        private:
            LensEffect() = default;
            ~LensEffect() = default;

            LONG SampleMargin() const
            {
                const float dispersion = std::max(0.f, params.chromaticAberration) * 0.7f;
                const float spread = std::abs(params.refractionAmount) * (1.f + dispersion);
                return static_cast<LONG>(std::ceil(std::max(spread, std::abs(params.refractionHeight)) + 4.f));
            }

            HRESULT UpdateConstants()
            {
                if (!drawInfo) return S_OK;
                return drawInfo->SetPixelShaderConstantBuffer(
                    reinterpret_cast<const BYTE*>(&params), static_cast<UINT32>(sizeof(params)));
            }

            std::atomic<ULONG> refCount{ 1 };
            ComPtr<ID2D1DrawInfo> drawInfo;
            LensParams params{};
        };

        void CreateLensEffect(ID2D1DeviceContext* context, ComPtr<ID2D1Effect>& effect,
            const wchar_t*& stage)
        {
            static std::mutex registrationMutex;
            HRESULT hr = context->CreateEffect(ClsidLiquidLensEffect, &effect);
            // CreateEffect documents both results for an unregistered effect.
            if (hr == D2DERR_EFFECT_IS_NOT_REGISTERED || hr == HRESULT_FROM_WIN32(ERROR_NOT_FOUND))
            {
                std::lock_guard lock(registrationMutex);
                hr = context->CreateEffect(ClsidLiquidLensEffect, &effect);
                if (hr == D2DERR_EFFECT_IS_NOT_REGISTERED || hr == HRESULT_FROM_WIN32(ERROR_NOT_FOUND))
                {
                    stage = L"compile-lens";
                    HRESULT compileHr = S_OK;
                    if (CompiledLensShader(compileHr).empty())
                        winrt::throw_hresult(FAILED(compileHr) ? compileHr : E_FAIL);
                    stage = L"register-lens";
                    ComPtr<ID2D1Factory> baseFactory;
                    context->GetFactory(&baseFactory);
                    ComPtr<ID2D1Factory1> factory;
                    winrt::check_hresult(baseFactory.As(&factory));
                    static const D2D1_PROPERTY_BINDING bindings[] = {
                        D2D1_BLOB_TYPE_BINDING(L"Params", &LensEffect::SetParams, &LensEffect::GetParams),
                    };
                    winrt::check_hresult(factory->RegisterEffectFromString(
                        ClsidLiquidLensEffect,
                        kLensEffectXml,
                        bindings,
                        static_cast<UINT32>(std::size(bindings)),
                        &LensEffect::CreateEffect));
                    stage = L"instantiate-lens";
                    hr = context->CreateEffect(ClsidLiquidLensEffect, &effect);
                }
            }
            winrt::check_hresult(hr);
        }

        void EnsureBitmap(ID2D1DeviceContext* context, ComPtr<ID2D1Bitmap1>& bitmap,
            int width, int height, DXGI_FORMAT format = DXGI_FORMAT_B8G8R8A8_UNORM)
        {
            const auto previous = bitmap ? bitmap->GetPixelSize() : D2D1_SIZE_U{};
            if (previous.width >= static_cast<UINT>(width) && previous.height >= static_cast<UINT>(height)) return;
            const auto size = D2D1::SizeU((std::max)(previous.width, static_cast<UINT>(width)),
                (std::max)(previous.height, static_cast<UINT>(height)));
            bitmap.Reset();
            winrt::check_hresult(context->CreateBitmap(size, nullptr, 0,
                D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET,
                    D2D1::PixelFormat(format, D2D1_ALPHA_MODE_PREMULTIPLIED), 96, 96), &bitmap));
        }

        // Always detach the target, including on errors. All calls are on the
        // card STA and this is never the context returned by Composition BeginDraw.
        class TargetDraw
        {
        public:
            TargetDraw(ID2D1DeviceContext* value, ID2D1Bitmap1* target) : context(value)
            {
                context->GetTarget(&previous);
                context->GetTransform(&transform);
                context->SetTarget(target);
                context->SetTransform(D2D1::Matrix3x2F::Identity());
                context->BeginDraw();
            }
            ~TargetDraw() { if (active) End(); }
            HRESULT End()
            {
                const HRESULT result = context->EndDraw();
                context->SetTarget(previous.Get());
                context->SetTransform(transform);
                active = false;
                return result;
            }
        private:
            ID2D1DeviceContext* context;
            ComPtr<ID2D1Image> previous;
            D2D1_MATRIX_3X2_F transform{};
            bool active{ true };
        };
    }

    bool RefractionSurface::Render(Graphics& graphics,
        const winrt::Windows::UI::Composition::CompositionGraphicsDevice& device,
        ID2D1Image* source, const D2D1_MATRIX_3X2_F& sourceToLocal,
        int width, int height, float radius, float lensHeight, float lensAmount,
        float blur, float scale, bool dark, const GlassControl* control,
        float depthEffect, float chromaticAberration)
    {
        try
        {
            stage = L"validate";
            if (!source || width < 2 || height < 2 || width > 8192 || height > 8192)
                winrt::throw_hresult(E_INVALIDARG);
            const float minDim = static_cast<float>((std::min)(width, height));
            radius = std::clamp(radius, 0.f, minDim / 2.f);
            lensHeight = std::clamp(lensHeight, .01f, minDim / 2.f);
            if (!control) lensAmount = std::clamp(lensAmount, 0.f, minDim * 0.45f);
            auto* context = graphics.ResourceContext();
            if (!transform)
            {
                stage = L"create-effects";
                winrt::check_hresult(context->CreateEffect(CLSID_D2D12DAffineTransform, &transform));
                winrt::check_hresult(context->CreateEffect(CLSID_D2D1ColorMatrix, &colorMatrix));
                winrt::check_hresult(context->CreateEffect(CLSID_D2D1GaussianBlur, &gaussian));
                stage = L"create-lens";
                CreateLensEffect(context, lens, stage);
                stage = L"connect-effects";
                winrt::check_hresult(colorMatrix->SetValue(D2D1_COLORMATRIX_PROP_CLAMP_OUTPUT, TRUE));
                winrt::check_hresult(gaussian->SetValue(D2D1_GAUSSIANBLUR_PROP_BORDER_MODE, D2D1_BORDER_MODE_HARD));
                colorMatrix->SetInputEffect(0, transform.Get());
                gaussian->SetInputEffect(0, colorMatrix.Get());
                lens->SetInputEffect(0, gaussian.Get());
            }
            stage = L"source-transform";
            if (control) transform->SetInput(0, source, TRUE);
            else
            {
                // The monitor capture ends at the physical edge. A card entering
                // or leaving across it would otherwise blur and refract toward
                // transparent; clamp extends the edge pixels instead.
                if (!border)
                {
                    winrt::check_hresult(context->CreateEffect(CLSID_D2D1Border, &border));
                    winrt::check_hresult(border->SetValue(D2D1_BORDER_PROP_EDGE_MODE_X, D2D1_BORDER_EDGE_MODE_CLAMP));
                    winrt::check_hresult(border->SetValue(D2D1_BORDER_PROP_EDGE_MODE_Y, D2D1_BORDER_EDGE_MODE_CLAMP));
                }
                border->SetInput(0, source, TRUE);
                transform->SetInputEffect(0, border.Get(), TRUE);
            }
            winrt::check_hresult(transform->SetValue(D2D1_2DAFFINETRANSFORM_PROP_TRANSFORM_MATRIX, sourceToLocal));
            if (control)
            {
                stage = L"control-input";
                const float dispersionMargin = 1.f + std::max(0.f, chromaticAberration) * 0.7f;
                const int padding = static_cast<int>(std::ceil(3 * blur + std::abs(lensAmount) * dispersionMargin + 4));
                EnsureBitmap(context, input, width + 2 * padding, height + 2 * padding);
                {
                    TargetDraw draw(context, input.Get());
                    // Padding outside the parent scene stays transparent, avoiding
                    // a dark halo when the lens samples across a card boundary.
                    context->Clear(D2D1::ColorF(0, 0.f));
                    const auto area = D2D1::RectF(-static_cast<float>(padding), -static_cast<float>(padding),
                        static_cast<float>(width + padding), static_cast<float>(height + padding));
                    context->DrawImage(transform.Get(), D2D1::Point2F(0, 0), area);
                    auto tint = graphics.Brush(context, GlassTint(dark));
                    context->FillRectangle(D2D1::RectF(0, 0, static_cast<float>(width + 2 * padding),
                        static_cast<float>(height + 2 * padding)), tint.Get());
                    if (control->track.bottom > control->track.top)
                    {
                        auto rail = control->track;
                        rail.left = (rail.left - control->bounds.left) * scale + padding;
                        rail.right = (rail.right - control->bounds.left) * scale + padding;
                        rail.top = (rail.top - control->bounds.top) * scale + padding;
                        rail.bottom = (rail.bottom - control->bounds.top) * scale + padding;
                        auto track = graphics.Brush(context, control->trackColor);
                        auto fill = graphics.Brush(context, control->fillColor);
                        const float corner = (rail.bottom - rail.top) / 2;
                        context->FillRoundedRectangle(D2D1::RoundedRect(rail, corner, corner), track.Get());
                        rail.right = rail.left + (rail.right - rail.left) * std::clamp(control->ratio, 0.f, 1.f);
                        if (rail.right > rail.left)
                            context->FillRoundedRectangle(D2D1::RoundedRect(rail,
                                (std::min)(corner, (rail.right - rail.left) / 2), corner), fill.Get());
                    }
                    winrt::check_hresult(draw.End());
                }
                transform->SetInput(0, input.Get(), TRUE);
                winrt::check_hresult(transform->SetValue(D2D1_2DAFFINETRANSFORM_PROP_TRANSFORM_MATRIX,
                    D2D1::Matrix3x2F::Translation(-static_cast<float>(padding), -static_cast<float>(padding))));
            }
            stage = L"color-matrix";
            // D2D's Saturation property accepts only [0, 1]. A colour matrix
            // supports the upstream 1.5x saturation without rejecting the frame.
            const float vibrancy = control ? 1.f : 1.5f;
            const float inverse = 1.f - vibrancy;
            const float r = .2126f * inverse, g = .7152f * inverse, b = .0722f * inverse;
            const auto matrix = D2D1::Matrix5x4F(
                r + vibrancy, r, r, 0,
                g, g + vibrancy, g, 0,
                b, b, b + vibrancy, 0,
                0, 0, 0, 1,
                0, 0, 0, 0);
            winrt::check_hresult(colorMatrix->SetValue(D2D1_COLORMATRIX_PROP_COLOR_MATRIX, matrix));
            stage = L"blur";
            winrt::check_hresult(gaussian->SetValue(D2D1_GAUSSIANBLUR_PROP_STANDARD_DEVIATION, blur));

            stage = L"lens-parameters";
            LensParams params{};
            params.width = static_cast<float>(width);
            params.height = static_cast<float>(height);
            params.offsetX = 0.f;
            params.offsetY = 0.f;
            params.cornerRadius = radius;
            params.refractionHeight = lensHeight;
            params.refractionAmount = lensAmount;
            params.depthEffect = depthEffect;
            params.chromaticAberration = chromaticAberration;
            winrt::check_hresult(lens->SetValue(
                0u, reinterpret_cast<const BYTE*>(&params), static_cast<UINT32>(sizeof(params))));

            stage = L"draw-lens";
            EnsureBitmap(context, scene, width, height);
            const auto area = D2D1::RectF(0, 0, static_cast<float>(width), static_cast<float>(height));
            {
                TargetDraw draw(context, scene.Get());
                context->Clear(D2D1::ColorF(0, 0.f));
                context->DrawImage(lens.Get(), D2D1::Point2F(0, 0), area);
                winrt::check_hresult(draw.End());
            }
            stage = L"surface-upload";
            surface.Ensure(device, (std::max)(width, surface.Width()), (std::max)(height, surface.Height()));
            {
                DrawScope draw(surface);
                if (!draw) winrt::throw_hresult(FAILED(surface.LastResult()) ? surface.LastResult() : E_FAIL);
                draw->Clear(D2D1::ColorF(0, 0.f));
                draw->DrawBitmap(scene.Get(), area, 1, D2D1_INTERPOLATION_MODE_LINEAR, area);
            }
            winrt::check_hresult(surface.LastResult());
            result = S_OK;
            stage = L"ready";
            return true;
        }
        catch (const winrt::hresult_error& error) { result = error.code(); }
        catch (...) { result = E_FAIL; }
        Release();
        return false;
    }

    winrt::Windows::UI::Composition::CompositionBrush RefractionSurface::Brush(
        const winrt::Windows::UI::Composition::Compositor& compositor)
    {
        if (bound != surface.Handle())
        {
            bound = surface.Handle();
            brush = bound ? compositor.CreateSurfaceBrush(bound) : nullptr;
            if (brush)
            {
                brush.Stretch(winrt::Windows::UI::Composition::CompositionStretch::None);
                brush.HorizontalAlignmentRatio(0); brush.VerticalAlignmentRatio(0);
            }
        }
        return brush;
    }

    void RefractionSurface::Release()
    {
        brush = nullptr; bound = nullptr; surface.Release();
        border.Reset(); transform.Reset(); colorMatrix.Reset(); gaussian.Reset(); lens.Reset();
        scene.Reset(); input.Reset();
    }
}
