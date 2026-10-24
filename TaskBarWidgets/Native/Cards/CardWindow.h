#pragma once
// One native card window.
//
// A card owns its HWND, its compositor, its visual tree, its drawing surfaces,
// its layout table and its interaction state. It never owns a D2D or DWrite
// factory: those come from the shared Graphics of the card STA thread.
//
// Drawing structure (bottom to top):
//   CardRoot
//   - system Mica background (CardHost supplies it through MicaController)
//   - background wash and border visual
//   - content drawing surface (static + dynamic layers painted together)
//   - popup drawing surface (device / source menus)
//   - motion container (opacity and DIP offset only; never moves the HWND)

#include "CardAccessibility.h"
#include "CardGraphics.h"
#include "CardIcons.h"
#include "CardLayout.h"
#include "CardTypes.h"

#include <Windows.h>

#include <chrono>
#include <memory>
#include <string>
#include <vector>

namespace Cards
{
    // Host services a card reports to. The host owns the pipe, the group and
    // the lifetime; a card only says what the user did.
    class CardHostServices
    {
    public:
        virtual ~CardHostServices() = default;
        virtual void OnRequest(const std::wstring& action, const std::wstring& jsonPayload) = 0;
        virtual void OnHideCards(const std::wstring& reason) = 0;
        virtual void OnOpenSettings() = 0;
        virtual void OnMenuOpened(bool open) = 0;
        virtual void OnLayoutInvalidated() = 0;
        virtual void OnGraphicsFailure(HRESULT) {}
        virtual bool IsMenuExpanded(const std::wstring&) const { return false; }
        virtual bool OnMenuKey(UINT) { return false; }
        virtual bool OnMenuWheel(int) { return false; }
    };

    struct CardEnvironment
    {
        Graphics* graphics{ nullptr };
        // One compositor and one composition graphics device are shared by every
        // card and menu; each card only owns its own visuals and surface.
        winrt::Windows::UI::Composition::Compositor compositor{ nullptr };
        winrt::Windows::UI::Composition::CompositionGraphicsDevice device{ nullptr };
        bool dark{ true };
        bool highContrast{ false };
        bool reducedMotion{ false };
        MicaTuning mica{};
        std::wstring fontFamily{ L"Microsoft YaHei UI" };
    };

    class CardWindow
    {
    public:
        CardWindow(Kind kind, CardHostServices& host);
        ~CardWindow();
        CardWindow(const CardWindow&) = delete;
        CardWindow& operator=(const CardWindow&) = delete;

        // Creates the HWND, the compositor and the empty visual tree.
        bool Create(const CardEnvironment& environment, HINSTANCE instance);
        void Destroy();

        // Theme, tuning and font changes. Never recreates the window.
        void ApplyEnvironment(const CardEnvironment& environment);
        // A new whole snapshot. Repaints only when something the card shows changed.
        void SetSnapshot(const Snapshot& snapshot);

        void Place(const RECT& bounds, float scale);
        void Show(bool animate);
        void Hide(bool animate);
        bool Visible() const { return visible; }
        RECT Bounds() const { return bounds; }

        Kind CardKind() const { return kind; }
        float DpiScale() const { return scale; }
        float MeasuredHeightDip() const { return measuredHeightDip; }
        const Layout::Table& Regions() const { return regions; }

        // Content painting runs on the card thread only.
        void Paint();
        void Invalidate();

        // Input. Returning true consumes the message.
        bool OnPointer(UINT message, POINT pixels, bool leftDown);
        bool OnKey(UINT virtualKey, bool control, bool shift);
        bool OnEscape();
        void OnMouseLeave();
        // Slider gestures: immediate local paint, coalesced commit, final value on
        // release.
        void UpdateSlider(const Layout::Region& region, float ratio);
        void FinishSlider();
        void CancelPointer();
        void AcknowledgeSlider(const std::wstring& action);

        // Window-procedure hook for the native UI Automation provider.
        LRESULT OnGetObject(WPARAM keys, LPARAM position);

        std::wstring AccessibleSummary() const;
        std::wstring AccessibleName(const Layout::Region& region) const;

        // Advances the show/hide animation. Returns true while it is running.
        bool Tick();

        const Snapshot& CurrentSnapshot() const { return snapshot; }
        const CardEnvironment& Environment() const { return environment; }
        HWND Handle() const { return window; }
        bool HasRenderedContent() const;
        bool HasActiveBackdropConfiguration() const;
        void RefreshBackdropConfiguration();
        uint64_t ContentPaintCount() const { return contentPaintCount; }

    private:
        friend class CardHost;
        struct Impl;

        void BuildVisualTree(HINSTANCE instance);
        void ApplyMotion();
        void UpdateTimer();
        void PaintContent(bool rebuildRegions);
        void PaintPopup();
        void ClosePopup();
        void OpenMenu(const std::wstring& menu, const std::vector<Layout::Region>& items);
        const Layout::Region* HitRegion(POINT dip, bool includePopup) const;
        POINT ToDip(POINT pixels) const;
        void PaintAll();
        void Activate(const Layout::Region& region);
        float ContentWidthDip() const;
        float ContentHeightDip() const;

        std::unique_ptr<Impl> impl;
        CardAccessibility accessibility;
        Kind kind;
        CardHostServices& host;

    public:
        CardHostServices* Host() { return &host; }

    private:
        CardEnvironment environment;
        Layout::Table regions;
        Layout::Table menuTable;
        Snapshot snapshot;
        RECT bounds{};
        uint64_t contentPaintCount{};
        float scale{ 1.0f };
        float measuredHeightDip{ 0.0f };
        bool visible{ false };
        HWND window{};
    };

    // Feature painters live in their own translation units; each one rebuilds
    // the interaction table and paints a card from the current snapshot.
    namespace Paint
    {
        // Cross-fade state for the media cover: the outgoing bitmap under the
        // incoming one while a track change settles.
        struct CoverTransition
        {
            ID2D1Bitmap1* previous{ nullptr };
            float previousOpacity{ 0.0f };
        };

        // Wrap transition: the outgoing lyric line slides up and fades while the
        // incoming one settles. Only two content sets exist, and the outgoing text
        // is dropped when the transition ends.
        struct LyricTransition
        {
            std::wstring previous;
            std::wstring previousSecondary;
            float progress{ 1.0f };
            // 0..1 progress of the overlong main line scroll; 0 means no scroll.
            float marquee{ 0.0f };
        };

        void Media(ID2D1DeviceContext* context, const Snapshot& snapshot,
            const CoverTransition& cover, const LyricTransition& lyrics,
            const CardEnvironment& environment, Layout::Table& regions, float widthDip, float heightDip);
        void Device(ID2D1DeviceContext* context, const Snapshot& snapshot,
            const CardEnvironment& environment, Layout::Table& regions, float widthDip, float heightDip);
        void Traffic(ID2D1DeviceContext* context, const Snapshot& snapshot,
            const CardEnvironment& environment, Layout::Table& regions, float widthDip, float heightDip);
        void Weather(ID2D1DeviceContext* context, const Snapshot& snapshot,
            const CardEnvironment& environment, Layout::Table& regions, float widthDip, float heightDip);
        void Update(ID2D1DeviceContext* context, const Snapshot& snapshot,
            const CardEnvironment& environment, Layout::Table& regions, float widthDip, float heightDip);

        // Auto-height measurement for the weather and device cards.
        float MeasureHeight(Kind kind, const Snapshot& snapshot, const CardEnvironment& environment,
            float widthDip);
    }
}
