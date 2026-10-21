#pragma once
#include <dcomp.h>
#include <wrl/client.h>
#include <algorithm>

// Native and WebView surfaces share one short, reversible opacity transition.
class CompositionFade
{
    float m_from{1}, m_to{1};
    ULONGLONG m_started{};
    double m_duration{};
public:
    static bool Enabled()
    {
        BOOL animation=TRUE;
        HIGHCONTRASTW contrast{sizeof(contrast)};
        SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION,0,&animation,0);
        SystemParametersInfoW(SPI_GETHIGHCONTRAST,sizeof(contrast),&contrast,0);
        return animation && !(contrast.dwFlags & HCF_HIGHCONTRASTON);
    }
    bool Active() const { return m_duration>0 && (GetTickCount64()-m_started)/1000.0<m_duration; }
    bool Finished() const { return !Active(); }
    HRESULT Set(IDCompositionDevice* device, IDCompositionVisual* visual, bool show, bool animate=true)
    {
        const auto now=GetTickCount64();
        const double progress=m_duration>0 ? (std::min)(1.0,(now-m_started)/1000.0/m_duration) : 1.0;
        m_from=static_cast<float>(m_from+(m_to-m_from)*progress);
        m_to=show?1.f:0.f; m_started=now;
        m_duration=animate && Enabled()?0.18:0.0;
        Microsoft::WRL::ComPtr<IDCompositionEffectGroup> effect;
        HRESULT result=device->CreateEffectGroup(&effect);
        if (FAILED(result)) return result;
        if (!m_duration)
        {
            m_from=m_to;
            result=effect->SetOpacity(m_to);
            return SUCCEEDED(result)?visual->SetEffect(effect.Get()):result;
        }
        Microsoft::WRL::ComPtr<IDCompositionAnimation> curve;
        result=device->CreateAnimation(&curve);
        if (SUCCEEDED(result)) result=curve->AddCubic(0,m_from,static_cast<float>((m_to-m_from)/m_duration),0,0);
        if (SUCCEEDED(result)) result=curve->End(m_duration,m_to);
        if (SUCCEEDED(result)) result=effect->SetOpacity(curve.Get());
        if (SUCCEEDED(result)) result=visual->SetEffect(effect.Get());
        return result;
    }
};
