#include "CardPch.h"
#include "CardDraw.h"

namespace Cards::Paint
{
    void Update(ID2D1DeviceContext* context, const Snapshot& snapshot, const CardEnvironment& environment,
        Layout::Table& regions, float widthDip, float heightDip, const Interaction& interaction)
    {
        if (!context || !environment.graphics) return;
        Graphics& graphics = *environment.graphics;
        const Draw::Palette palette = Draw::Colors(environment);
        const auto& update = snapshot.update;
        const float left = Layout::Padding;
        const float width = widthDip - Layout::Padding * 2.0f;

        const std::wstring title = update.phase == L"ready" ? L"更新已就绪"
            : update.phase == L"downloading" ? L"正在下载更新"
            : update.phase == L"verifying" ? L"正在校验更新" : L"有可用更新";
        Draw::Text(context, graphics, title, environment.fontFamily, 13.0f, 600, palette.primary,
            Draw::Rect(left, Layout::Padding, left + width, Layout::Padding + 20.0f));
        const std::wstring versions = update.latestVersion.empty()
            ? update.currentVersion : (update.currentVersion + L" \u2192 " + update.latestVersion);
        Draw::Text(context, graphics, versions, environment.fontFamily, 10.0f, 400, palette.secondary,
            Draw::Rect(left, Layout::Padding + 22.0f, left + width, Layout::Padding + 38.0f));

        const float progressTop = Layout::Padding + 48.0f;
        const bool downloading = update.phase == L"downloading" || update.phase == L"verifying";
        if (downloading)
            Draw::Progress(context, Draw::Rect(left, progressTop, left + width, progressTop + 4.0f),
                static_cast<float>(update.ProgressPercent()) / 100.0f, palette.track, palette.accent,
                false);
        else
            Draw::FillRound(context, Draw::Rect(left, progressTop, left + width, progressTop + 4.0f),
                2.0f, palette.track);
        if (!update.message.empty())
            Draw::Text(context, graphics, update.message, environment.fontFamily, 9.0f, 400,
                palette.secondary, Draw::Rect(left, progressTop + 8.0f, left + width,
                    progressTop + 26.0f));

        const bool ready = update.phase == L"ready" && update.canInstall;
        const bool downloadable = update.phase == L"available" && update.canDownload;
        const std::wstring action = update.phase == L"ready" ? L"installUpdate" : L"downloadUpdate";
        const std::wstring label = update.phase == L"ready" ? L"安装并重启"
            : update.phase == L"downloading" ? L"正在下载…"
            : update.phase == L"verifying" ? L"正在校验…" : L"下载更新";
        const float buttonTop = heightDip - Layout::Padding - 32.0f;
        const D2D1_RECT_F button = Draw::Rect(left, buttonTop, left + width, buttonTop + 32.0f);
        Draw::ControlBackground(context, button, 8.0f,
            WithAlpha(palette.accent, ready || downloadable ? 0.18f : 0.06f), palette.accent,
            interaction, 500, ready || downloadable);
        Draw::Text(context, graphics, label, environment.fontFamily, 12.0f, 600,
            ready || downloadable ? palette.primary : palette.secondary,
            Draw::Rect(button.left, button.top + 7.0f, button.right, button.bottom - 5.0f),
            DWRITE_TEXT_ALIGNMENT_CENTER);
        Draw::AddButton(regions, 500, button, action, label, ready || downloadable);
    }
}
