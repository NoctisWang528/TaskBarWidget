#pragma once
// One self-drawn popup list for a card.
//
// A popup is an independent top-level tool window on the card STA thread. It
// borrows the shared graphics device and paints with the same Direct2D/DirectWrite
// helpers as the cards, so a device or source list looks like the card that
// opened it. It belongs to the card group: an outside click that lands inside a
// popup must never dismiss the group.

#include "CardGraphics.h"
#include "CardLayout.h"
#include "CardTypes.h"

#include <Windows.h>

#include <functional>
#include <memory>
#include <string>
#include <vector>

namespace Cards
{
    struct CardEnvironment;

    class CardPopup
    {
    public:
        CardPopup();
        ~CardPopup();

        CardPopup(const CardPopup&) = delete;
        CardPopup& operator=(const CardPopup&) = delete;

        // Creates the window once. Returns false when the platform refuses it.
        bool Create(HINSTANCE instance, const CardEnvironment& environment);
        void Destroy();
        void ApplyEnvironment(const CardEnvironment& environment);

        // Shows the list next to \`anchor\` (physical pixels). The popup picks its own
        // monitor and DPI; \`menu\` only names the owner for later correlation.
        void Show(const std::wstring& menu, const std::vector<Layout::Region>& items, RECT anchor);
        void Hide();
        bool Visible() const;
        const std::wstring& Menu() const;
        RECT Bounds() const;

        // Hit testing and keyboard selection. \`Activate\` returns the index the user
        // accepted, or -1.
        int HitTest(POINT pixels) const;
        bool Contains(POINT pixels) const;
        void MoveSelection(int delta);
        int ActivateSelected() const;
        int Selected() const;
        const std::vector<Layout::Region>& Items() const;
        void Wheel(int delta);
        // Invoked when the user clicks an item; the host reads Selected() and
        // turns it into a command.
        void SetActivation(std::function<void()> callback);

    private:
        struct Impl;
        std::unique_ptr<Impl> impl;
    };
}
