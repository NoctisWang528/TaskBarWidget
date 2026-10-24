#include "CardPch.h"
#include "CardDraw.h"

namespace Cards::Paint
{
    namespace
    {
        constexpr float SectionGap{ 8.0f };
        constexpr float LabelHeight{ 16.0f };
        constexpr float SelectorHeight{ 32.0f };

        const AudioEndpoint* Default(const std::vector<AudioEndpoint>& endpoints)
        {
            for (const auto& endpoint : endpoints)
                if (endpoint.isDefault) return &endpoint;
            return endpoints.empty() ? nullptr : &endpoints.front();
        }

        std::wstring VolumeText(const AudioEndpoint* endpoint)
        {
            if (!endpoint || !endpoint->canControl) return L"\u2014";
            return FormatPercent(static_cast<int>(std::lround(endpoint->volume * 100.0)));
        }

        float Section(float top, const std::wstring& title, const std::vector<AudioEndpoint>& endpoints,
            const std::wstring& flow, int baseId, ID2D1DeviceContext* context,
            const CardEnvironment& environment, Layout::Table& regions, float left, float width,
            const Draw::Palette& palette, bool busy)
        {
            Graphics& graphics = *environment.graphics;
            Draw::Text(context, graphics, title, environment.fontFamily, 10.0f, 400, palette.secondary,
                Draw::Rect(left, top, left + width, top + LabelHeight));
            top += LabelHeight + 3.0f;

            const auto* current = Default(endpoints);
            const D2D1_RECT_F selector = Draw::Rect(left, top, left + width, top + SelectorHeight);
            Draw::FillRound(context, selector, 8.0f, WithAlpha(palette.primary, 0.06f));
            const std::wstring name = current ? current->name : L"没有可用设备";
            Draw::Text(context, graphics, Draw::Ellipsize(context, graphics, name, environment.fontFamily,
                12.0f, 400, width - 44.0f), environment.fontFamily, 12.0f, 400, palette.primary,
                Draw::Rect(selector.left + 10.0f, selector.top + 7.0f, selector.right - 28.0f,
                    selector.bottom - 5.0f));
            DrawGlyph(context, Glyph::Chevron, palette.secondary, selector.right - 24.0f,
                selector.top + 9.0f, 12.0f);
            Draw::AddButton(regions, baseId, selector, L"showDevices:" + flow, title + L"选择设备",
                !endpoints.empty() && !busy);
            top += SelectorHeight + 4.0f;

            const float muteSize = 32.0f;
            const D2D1_RECT_F mute = Draw::Rect(left, top, left + muteSize, top + muteSize);
            const bool muted = current && current->isMuted;
            const bool controllable = current && current->canControl;
            Draw::FillRound(context, mute, muteSize / 2.0f,
                WithAlpha(palette.primary, muted ? 0.18f : 0.06f));
            const bool input = flow == L"input";
            DrawGlyph(context, input ? (muted ? Glyph::MicrophoneMuted : Glyph::Microphone)
                : (muted ? Glyph::SpeakerMuted : Glyph::Speaker),
                WithAlpha(palette.primary, controllable ? 0.9f : 0.4f),
                mute.left + 8.0f, mute.top + 8.0f, 16.0f);
            Draw::AddButton(regions, baseId + 1, mute, L"toggleMute:" + flow,
                title + (muted ? L"取消静音" : L"静音"), controllable && !busy);

            const float sliderLeft = left + muteSize + 8.0f;
            const float sliderRight = left + width - 34.0f;
            const float railTop = top + muteSize / 2.0f - 1.5f;
            const float ratio = controllable ? static_cast<float>(current->volume) : 0.0f;
            Draw::Progress(context, Draw::Rect(sliderLeft, railTop, sliderRight, railTop + 3.0f), ratio,
                palette.track, palette.accent, true);
            Draw::AddSlider(regions, baseId + 2,
                Draw::Rect(sliderLeft, railTop - 9.0f, sliderRight, railTop + 12.0f),
                L"setVolume:" + flow, title + L"音量", controllable && !busy);
            Draw::Text(context, graphics, VolumeText(current), environment.fontFamily, 11.0f, 400,
                palette.secondary, Draw::Rect(sliderRight + 4.0f, top + 7.0f, left + width, top + 24.0f),
                DWRITE_TEXT_ALIGNMENT_TRAILING);
            top += muteSize + SectionGap;
            return top;
        }
    }

    void Device(ID2D1DeviceContext* context, const Snapshot& snapshot, const CardEnvironment& environment,
        Layout::Table& regions, float widthDip, float heightDip)
    {
        UNREFERENCED_PARAMETER(heightDip);
        if (!context || !environment.graphics) return;
        Graphics& graphics = *environment.graphics;
        const Draw::Palette palette = Draw::Colors(environment);
        const float left = Layout::Padding;
        const float width = widthDip - Layout::Padding * 2.0f;
        const bool busy = !snapshot.audio.message.empty();

        float top = Layout::Padding;
        top = Section(top, L"输出设备", snapshot.audio.outputs, L"output", 200, context, environment,
            regions, left, width, palette, busy);
        top = Section(top, L"输入设备", snapshot.audio.inputs, L"input", 210, context, environment,
            regions, left, width, palette, busy);

        if (busy)
        {
            Draw::Text(context, graphics, snapshot.audio.message, environment.fontFamily, 10.0f, 400,
                palette.secondary, Draw::Rect(left, top, left + width, top + 22.0f));
        }
        else
        {
            const auto* output = Default(snapshot.audio.outputs);
            const auto* input = Default(snapshot.audio.inputs);
            if ((output && !output->canControl) || (input && !input->canControl))
            {
                Draw::Text(context, graphics, L"设备音量暂不可用，仍可选择其他设备。",
                    environment.fontFamily, 10.0f, 400, palette.secondary,
                    Draw::Rect(left, top, left + width, top + 22.0f));
            }
        }
    }
}
