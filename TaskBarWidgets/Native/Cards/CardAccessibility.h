#pragma once
// Native UI Automation provider for a card window.
//
// The interaction table already describes every control, so accessibility reads
// the same table the pointer uses: a region's rectangle, enabled state and action
// are identical for hit testing, painting and automation. Buttons expose Invoke,
// sliders expose RangeValue and heatmap cells expose SelectionItem. Menu buttons
// also expose ExpandCollapse when their host supplies real menu state.

#include "CardLayout.h"

#include <Windows.h>
#include <UIAutomationCore.h>
#include <wrl/implements.h>

#include <functional>
#include <string>

#include <unknwn.h>
#include <winrt/base.h>

namespace Cards
{
    class CardAccessibility
    {
    public:
        // The provider objects need each other; the nested root type stays part of
        // the class contract but is reachable from the translation unit's helpers.
        struct Root;
        using TableSource = std::function<const Layout::Table&()>;
        // Invoked for Invoke/Selection/Expand (empty payload) and for a RangeValue
        // change (the ratio the pointer would send). With ExpandedSource attached,
        // a menu Collapse sends the same region with payload "collapse".
        using ActionSink = std::function<void(const Layout::Region&, const std::wstring&)>;
        using ScaleSource = std::function<float()>;
        using SliderSource = std::function<double(int)>;
        using ExpandedSource = std::function<bool(const Layout::Region&)>;

        CardAccessibility();
        ~CardAccessibility();

        CardAccessibility(const CardAccessibility&) = delete;
        CardAccessibility& operator=(const CardAccessibility&) = delete;

        void Attach(HWND window, std::wstring name, TableSource table, ScaleSource scale,
            SliderSource slider, ActionSink action, ExpandedSource expanded = {});
        void Detach();
        void NotifyFocus(const Layout::Region* region);
        void NotifyStructureChanged();

        // WM_GETOBJECT entry point; returns 0 when the request is not ours.
        LRESULT OnGetObject(WPARAM keys, LPARAM position);

    private:
        winrt::com_ptr<Root> root;
    };
}
