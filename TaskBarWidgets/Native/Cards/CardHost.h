#pragma once
// Card host: the native card STA thread.
//
// Owns the card group (positioning, lifecycle, show/hide), the card pipe client,
// the shared graphics device and the Mica/theme environment. Nothing here touches
// MFC, the taskbar window or the hardware managers; the native main thread talks
// to this object only through the small API below, which marshals to the card
// thread.

#include "CardTypes.h"

#include <Windows.h>

#include <memory>
#include <string>

namespace Cards
{
    class CardHost
    {
    public:
        CardHost();
        ~CardHost();

        CardHost(const CardHost&) = delete;
        CardHost& operator=(const CardHost&) = delete;

        // Creates the card STA thread and its message loop. \`owner\` is the native
        // taskbar window, used only for monitor and DPI queries.
        bool Start(HWND owner, HINSTANCE instance);
        void Stop();

        // Opens or closes the card group at the taskbar click point. Thread-safe:
        // the request is marshalled to the card thread.
        void Toggle(POINT anchor);
        void Show(POINT anchor);
        void Hide(const wchar_t* reason);
        bool Visible() const;

        // A fresh quick-card configuration (order, columns, enabled, resource
        // mode, font, Mica tuning). Thread-safe.
        void ApplyConfig(const Config& config);

        // Device or monitor geometry changed: re-place the group.
        void Refresh();
        void SetOwner(HWND owner);

        // Draft appearance from the settings process. \`preview\` may be null to
        // drop the preview and fall back to the committed configuration.
        void PreviewMica(bool vivid, const MicaTuning& tuning);
        void PreviewTheme(bool dark, bool enabled);
        void ClearPreview();

        // Called on the native main thread when the process shuts down.
        static void Shutdown();

    private:
        struct Impl;
        std::shared_ptr<Impl> impl;
    };

    // True when the native card front-end can run at all (graphics available).
    bool CardsAvailable();
}
