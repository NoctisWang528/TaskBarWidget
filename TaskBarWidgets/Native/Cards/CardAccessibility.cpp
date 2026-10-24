#include "CardPch.h"
#include "CardAccessibility.h"

// UIAutomationCore.h declares the provider interfaces; the control types, property
// ids and pattern ids come from the client header.
#include <UIAutomationCoreApi.h>
#include <UIAutomationClient.h>

#include <algorithm>
#include <cmath>

namespace Cards
{
    namespace
    {
        // Runtime ids combine a constant with the region id, exactly the identifiers
        // the interaction table already uses.
        constexpr int RootRuntimeId = -1;

        long ControlTypeOf(Layout::RegionType type)
        {
            switch (type)
            {
            case Layout::RegionType::Slider: return UIA_SliderControlTypeId;
            case Layout::RegionType::Checkbox: return UIA_CheckBoxControlTypeId;
            case Layout::RegionType::Cell: return UIA_DataItemControlTypeId;
            case Layout::RegionType::MenuItem: return UIA_ListItemControlTypeId;
            case Layout::RegionType::Text: return UIA_TextControlTypeId;
            default: return UIA_ButtonControlTypeId;
            }
        }

        bool IsExpander(const Layout::Region& region)
        {
            return region.type == Layout::RegionType::Button &&
                (region.action.rfind(L"showDevices:", 0) == 0 || region.action == L"showSources");
        }
    }

    // UseComThreading honors the COM apartment only when the provider opts out
    // of C++/WinRT's default free-threaded marshaler. All callbacks stay on STA.
    struct CardAccessibility::Root : winrt::implements<Root, IRawElementProviderSimple,
        IRawElementProviderFragment, IRawElementProviderFragmentRoot, winrt::non_agile>
    {
        HWND window{};
        int focusedId{};
        std::wstring name;
        TableSource table;
        ScaleSource scale;
        SliderSource slider;
        ActionSink action;
        ExpandedSource expanded;
        std::vector<winrt::com_ptr<IRawElementProviderFragment>> children;
        // Children hold this token weakly: a screen reader may keep an element
        // alive after the card window is gone, and every call must then no-op
        // instead of touching a destroyed host.
        std::shared_ptr<int> lifetime{ std::make_shared<int>(0) };

        float Factor() const { return scale ? scale() : 1.0f; }

        RECT ScreenRect(const Layout::Region& region) const
        {
            RECT bounds{};
            if (window) GetWindowRect(window, &bounds);
            const float factor = Factor();
            RECT result{};
            result.left = bounds.left + static_cast<LONG>(std::lround(region.bounds.left * factor));
            result.top = bounds.top + static_cast<LONG>(std::lround(region.bounds.top * factor));
            result.right = bounds.left + static_cast<LONG>(std::lround(region.bounds.right * factor));
            result.bottom = bounds.top + static_cast<LONG>(std::lround(region.bounds.bottom * factor));
            return result;
        }

        winrt::com_ptr<IRawElementProviderFragment> CreateChild(int id);

        double SliderValue(int id) const { return slider ? slider(id) : 0.0; }

        void Rebuild()
        {
            children.clear();
            if (!table) return;
            for (const auto& region : table().All())
            {
                if (region.type == Layout::RegionType::Text) continue;
                if (auto child = CreateChild(region.id)) children.push_back(child);
            }
        }

        HRESULT __stdcall get_ProviderOptions(ProviderOptions* value) override
        {
            if (!value) return E_INVALIDARG;
            *value = static_cast<ProviderOptions>(ProviderOptions_ServerSideProvider |
                ProviderOptions_ProviderOwnsSetFocus | ProviderOptions_UseComThreading);
            return S_OK;
        }

        HRESULT __stdcall GetPatternProvider(PATTERNID patternId, IUnknown** value) override
        {
            UNREFERENCED_PARAMETER(patternId);
            if (!value) return E_INVALIDARG;
            *value = nullptr;
            return S_OK;
        }

        HRESULT __stdcall GetPropertyValue(PROPERTYID propertyId, VARIANT* value) override
        {
            if (!value) return E_INVALIDARG;
            VariantInit(value);
            switch (propertyId)
            {
            case UIA_NamePropertyId:
                value->vt = VT_BSTR;
                value->bstrVal = SysAllocString(name.c_str());
                return S_OK;
            case UIA_AutomationIdPropertyId:
                value->vt = VT_BSTR;
                value->bstrVal = SysAllocString(L"card-root");
                return S_OK;
            case UIA_ControlTypePropertyId:
                value->vt = VT_I4;
                value->lVal = UIA_PaneControlTypeId;
                return S_OK;
            case UIA_IsControlElementPropertyId:
            case UIA_IsContentElementPropertyId:
            case UIA_IsKeyboardFocusablePropertyId:
            case UIA_IsEnabledPropertyId:
                value->vt = VT_BOOL;
                value->boolVal = VARIANT_TRUE;
                return S_OK;
            case UIA_FrameworkIdPropertyId:
                value->vt = VT_BSTR;
                value->bstrVal = SysAllocString(L"TaskBarWidgets");
                return S_OK;
            default:
                return S_OK;
            }
        }

        HRESULT __stdcall get_HostRawElementProvider(IRawElementProviderSimple** value) override
        {
            if (!value) return E_INVALIDARG;
            *value = nullptr;
            return window ? UiaHostProviderFromHwnd(window, value) : UIA_E_ELEMENTNOTAVAILABLE;
        }

        HRESULT __stdcall ElementProviderFromPoint(double x, double y,
            IRawElementProviderFragment** value) override
        {
            if (!value) return E_INVALIDARG;
            *value = nullptr;
            Rebuild();
            const POINT point{ static_cast<LONG>(x), static_cast<LONG>(y) };
            for (const auto& child : children)
            {
                UiaRect rect{};
                if (FAILED(child->get_BoundingRectangle(&rect))) continue;
                if (point.x >= rect.left && point.x < rect.left + rect.width &&
                    point.y >= rect.top && point.y < rect.top + rect.height)
                {
                    child.copy_to(value);
                    return S_OK;
                }
            }
            return S_OK;
        }

        HRESULT __stdcall GetFocus(IRawElementProviderFragment** value) override
        {
            if (!value) return E_INVALIDARG;
            *value = nullptr;
            if (!window) return UIA_E_ELEMENTNOTAVAILABLE;
            if (focusedId) CreateChild(focusedId).copy_to(value);
            return S_OK;
        }

        HRESULT __stdcall Navigate(NavigateDirection direction,
            IRawElementProviderFragment** value) override
        {
            if (!value) return E_INVALIDARG;
            *value = nullptr;
            Rebuild();
            if (children.empty()) return S_OK;
            if (direction == NavigateDirection_FirstChild) children.front().copy_to(value);
            else if (direction == NavigateDirection_LastChild) children.back().copy_to(value);
            return S_OK;
        }

        HRESULT __stdcall GetRuntimeId(SAFEARRAY** value) override
        {
            if (!value) return E_INVALIDARG;
            *value = SafeArrayCreateVector(VT_I4, 0, 2);
            if (!*value) return E_OUTOFMEMORY;
            LONG index = 0;
            LONG first = UiaAppendRuntimeId;
            LONG second = RootRuntimeId;
            SafeArrayPutElement(*value, &index, &first);
            index = 1;
            SafeArrayPutElement(*value, &index, &second);
            return S_OK;
        }

        HRESULT __stdcall get_BoundingRectangle(UiaRect* value) override
        {
            if (!value) return E_INVALIDARG;
            RECT bounds{};
            if (window) GetWindowRect(window, &bounds);
            value->left = bounds.left;
            value->top = bounds.top;
            value->width = bounds.right - bounds.left;
            value->height = bounds.bottom - bounds.top;
            return S_OK;
        }

        HRESULT __stdcall GetEmbeddedFragmentRoots(SAFEARRAY** value) override
        {
            if (value) *value = nullptr;
            return S_OK;
        }

        HRESULT __stdcall SetFocus() override
        {
            return window ? S_OK : UIA_E_ELEMENTNOTAVAILABLE;
        }

        HRESULT __stdcall get_FragmentRoot(IRawElementProviderFragmentRoot** value) override
        {
            if (!value) return E_INVALIDARG;
            *value = nullptr;
            auto* self = static_cast<IRawElementProviderFragmentRoot*>(this);
            self->AddRef();
            *value = self;
            return S_OK;
        }
    };

    // One element per interactive region. It is created on demand from the
    // interaction table, so the automation tree cannot drift from the painted one.
    struct RegionProvider : winrt::implements<RegionProvider, IRawElementProviderSimple,
        IRawElementProviderFragment, IInvokeProvider, IRangeValueProvider,
        IExpandCollapseProvider, ISelectionItemProvider, winrt::non_agile>
    {
        CardAccessibility::Root* root{};
        std::weak_ptr<int> lifetime;
        int id{};

        bool Alive() const { return root != nullptr && !lifetime.expired(); }

        Layout::Region Current() const
        {
            Layout::Region region;
            region.id = id;
            region.enabled = false;
            region.focusable = false;
            if (Alive() && root->table)
            {
                for (const auto& candidate : root->table().All())
                    if (candidate.id == id) return candidate;
            }
            return region;
        }

        bool Enabled() const { return Current().enabled; }

        void Fire(const std::wstring& payload)
        {
            if (!Alive() || !root->action) return;
            const auto region = Current();
            if (!region.enabled) return;
            // Copy before calling out: the action may close this card.
            const auto action = root->action;
            action(region, payload.empty() && region.action == L"selectDay" ? region.name : payload);
        }

        HRESULT __stdcall get_ProviderOptions(ProviderOptions* value) override
        {
            if (!value) return E_INVALIDARG;
            *value = static_cast<ProviderOptions>(ProviderOptions_ServerSideProvider |
                ProviderOptions_ProviderOwnsSetFocus | ProviderOptions_UseComThreading);
            return S_OK;
        }

        HRESULT __stdcall GetPatternProvider(PATTERNID patternId, IUnknown** value) override
        {
            if (!value) return E_INVALIDARG;
            *value = nullptr;
            const auto region = Current();
            IUnknown* match = nullptr;
            if (region.type == Layout::RegionType::Slider && patternId == UIA_RangeValuePatternId)
                match = static_cast<IRangeValueProvider*>(this);
            else if (region.type == Layout::RegionType::Cell && patternId == UIA_SelectionItemPatternId)
                match = static_cast<ISelectionItemProvider*>(this);
            else if (IsExpander(region) && Alive() && root->expanded &&
                patternId == UIA_ExpandCollapsePatternId)
                match = static_cast<IExpandCollapseProvider*>(this);
            else if ((region.type == Layout::RegionType::Button ||
                region.type == Layout::RegionType::MenuItem) && patternId == UIA_InvokePatternId)
                match = static_cast<IInvokeProvider*>(this);
            if (match)
            {
                match->AddRef();
                *value = match;
            }
            return S_OK;
        }

        HRESULT __stdcall GetPropertyValue(PROPERTYID propertyId, VARIANT* value) override
        {
            if (!value) return E_INVALIDARG;
            VariantInit(value);
            const auto region = Current();
            switch (propertyId)
            {
            case UIA_NamePropertyId:
                value->vt = VT_BSTR;
                value->bstrVal = SysAllocString(region.name.c_str());
                return S_OK;
            case UIA_AutomationIdPropertyId:
            {
                value->vt = VT_BSTR;
                const auto text = L"card-region-" + std::to_wstring(id);
                value->bstrVal = SysAllocString(text.c_str());
                return S_OK;
            }
            case UIA_ControlTypePropertyId:
                value->vt = VT_I4;
                value->lVal = ControlTypeOf(region.type);
                return S_OK;
            case UIA_IsEnabledPropertyId:
            case UIA_IsKeyboardFocusablePropertyId:
                value->vt = VT_BOOL;
                value->boolVal = region.enabled && (propertyId != UIA_IsKeyboardFocusablePropertyId ||
                    region.focusable) ? VARIANT_TRUE : VARIANT_FALSE;
                return S_OK;
            case UIA_IsControlElementPropertyId:
            case UIA_IsContentElementPropertyId:
                value->vt = VT_BOOL;
                value->boolVal = VARIANT_TRUE;
                return S_OK;
            case UIA_IsOffscreenPropertyId:
                value->vt = VT_BOOL;
                value->boolVal = Alive() && IsWindowVisible(root->window) ? VARIANT_FALSE : VARIANT_TRUE;
                return S_OK;
            case UIA_HasKeyboardFocusPropertyId:
                value->vt = VT_BOOL;
                value->boolVal = Alive() && root->focusedId == id ? VARIANT_TRUE : VARIANT_FALSE;
                return S_OK;
            case UIA_SelectionItemIsSelectedPropertyId:
                value->vt = VT_BOOL;
                value->boolVal = region.selected ? VARIANT_TRUE : VARIANT_FALSE;
                return S_OK;
            case UIA_FrameworkIdPropertyId:
                value->vt = VT_BSTR;
                value->bstrVal = SysAllocString(L"TaskBarWidgets");
                return S_OK;
            default:
                return S_OK;
            }
        }

        HRESULT __stdcall get_HostRawElementProvider(IRawElementProviderSimple** value) override
        {
            if (!value) return E_INVALIDARG;
            *value = nullptr;
            return S_OK;
        }

        // ---- fragment ----------------------------------------------------
        HRESULT __stdcall Navigate(NavigateDirection direction,
            IRawElementProviderFragment** value) override
        {
            if (!value) return E_INVALIDARG;
            *value = nullptr;
            if (!Alive()) return S_OK;
            if (direction == NavigateDirection_Parent)
            {
                if (!Alive()) return S_OK;
                auto* parent = static_cast<IRawElementProviderFragment*>(root);
                parent->AddRef();
                *value = parent;
                return S_OK;
            }
            if (!root->table) return UIA_E_ELEMENTNOTAVAILABLE;
            const auto& all = root->table().All();
            const auto current = std::find_if(all.begin(), all.end(),
                [this](const Layout::Region& region) { return region.id == id; });
            if (current == all.end()) return UIA_E_ELEMENTNOTAVAILABLE;
            if (direction == NavigateDirection_NextSibling)
            {
                for (auto next = current + 1; next != all.end(); ++next)
                    if (next->type != Layout::RegionType::Text)
                    { root->CreateChild(next->id).copy_to(value); break; }
            }
            else if (direction == NavigateDirection_PreviousSibling)
            {
                for (auto previous = current; previous != all.begin(); )
                    if ((--previous)->type != Layout::RegionType::Text)
                    { root->CreateChild(previous->id).copy_to(value); break; }
            }
            return S_OK;
        }

        HRESULT __stdcall GetRuntimeId(SAFEARRAY** value) override
        {
            if (!value) return E_INVALIDARG;
            *value = SafeArrayCreateVector(VT_I4, 0, 2);
            if (!*value) return E_OUTOFMEMORY;
            LONG index = 0;
            LONG first = UiaAppendRuntimeId;
            LONG second = id;
            SafeArrayPutElement(*value, &index, &first);
            index = 1;
            SafeArrayPutElement(*value, &index, &second);
            return S_OK;
        }

        HRESULT __stdcall get_BoundingRectangle(UiaRect* value) override
        {
            if (!value) return E_INVALIDARG;
            const RECT bounds = Alive() ? root->ScreenRect(Current()) : RECT{};
            value->left = bounds.left;
            value->top = bounds.top;
            value->width = bounds.right - bounds.left;
            value->height = bounds.bottom - bounds.top;
            return S_OK;
        }

        HRESULT __stdcall GetEmbeddedFragmentRoots(SAFEARRAY** value) override
        {
            if (value) *value = nullptr;
            return S_OK;
        }

        // The card never takes foreground activation, so automation focus is
        // reported without SetForegroundWindow.
        HRESULT __stdcall SetFocus() override
        {
            if (!Alive()) return UIA_E_ELEMENTNOTAVAILABLE;
            if (!Enabled()) return UIA_E_ELEMENTNOTENABLED;
            root->focusedId = id;
            return UiaRaiseAutomationEvent(static_cast<IRawElementProviderSimple*>(this),
                UIA_AutomationFocusChangedEventId);
        }

        HRESULT __stdcall get_FragmentRoot(IRawElementProviderFragmentRoot** value) override
        {
            if (!value) return E_INVALIDARG;
            *value = nullptr;
            if (!Alive()) return E_INVALIDARG;
            auto* self = static_cast<IRawElementProviderFragmentRoot*>(root);
            self->AddRef();
            *value = self;
            return S_OK;
        }

        // ---- invoke ------------------------------------------------------
        HRESULT __stdcall Invoke() override
        {
            if (!Enabled()) return UIA_E_ELEMENTNOTENABLED;
            Fire(L"");
            return S_OK;
        }

        // ---- range value -------------------------------------------------
        HRESULT __stdcall SetValue(double value) override
        {
            if (!Enabled()) return UIA_E_ELEMENTNOTENABLED;
            // The same 0..1 ratio the pointer sends, so both paths share one action.
            if (!std::isfinite(value) || value < 0.0 || value > 1.0) return E_INVALIDARG;
            Fire(std::to_wstring(value));
            return S_OK;
        }

        HRESULT __stdcall get_Value(double* value) override
        {
            if (!value) return E_INVALIDARG;
            // The ratio is derived from the current endpoint position by the card,
            // so automation reports what the next paint will show.
            *value = Alive() ? root->SliderValue(id) : 0.0;
            return S_OK;
        }

        HRESULT __stdcall get_Maximum(double* value) override
        {
            if (value) *value = 1.0;
            return S_OK;
        }

        HRESULT __stdcall get_Minimum(double* value) override
        {
            if (value) *value = 0.0;
            return S_OK;
        }

        HRESULT __stdcall get_LargeChange(double* value) override
        {
            if (value) *value = 0.1;
            return S_OK;
        }

        HRESULT __stdcall get_SmallChange(double* value) override
        {
            if (value) *value = 0.01;
            return S_OK;
        }

        HRESULT __stdcall get_IsReadOnly(BOOL* value) override
        {
            if (value) *value = Enabled() ? VARIANT_FALSE : VARIANT_TRUE;
            return S_OK;
        }

        // ---- expand collapse ---------------------------------------------
        HRESULT __stdcall Expand() override
        {
            if (!Alive()) return UIA_E_ELEMENTNOTAVAILABLE;
            if (!Enabled()) return UIA_E_ELEMENTNOTENABLED;
            if (!root->expanded) return UIA_E_NOTSUPPORTED;
            if (!root->expanded(Current())) Fire(L"");
            return S_OK;
        }

        HRESULT __stdcall Collapse() override
        {
            if (!Alive()) return UIA_E_ELEMENTNOTAVAILABLE;
            if (!Enabled()) return UIA_E_ELEMENTNOTENABLED;
            if (!root->expanded) return UIA_E_NOTSUPPORTED;
            if (root->expanded(Current())) Fire(L"collapse");
            return S_OK;
        }

        HRESULT __stdcall get_ExpandCollapseState(ExpandCollapseState* value) override
        {
            if (!value) return E_INVALIDARG;
            if (!Alive()) return UIA_E_ELEMENTNOTAVAILABLE;
            if (!root->expanded) return UIA_E_NOTSUPPORTED;
            *value = root->expanded(Current()) ? ExpandCollapseState_Expanded : ExpandCollapseState_Collapsed;
            return S_OK;
        }

        // ---- selection item ----------------------------------------------
        HRESULT __stdcall Select() override
        {
            if (!Alive()) return UIA_E_ELEMENTNOTAVAILABLE;
            if (!Enabled()) return UIA_E_ELEMENTNOTENABLED;
            Fire(L"");
            return S_OK;
        }

        HRESULT __stdcall AddToSelection() override { return S_OK; }
        HRESULT __stdcall RemoveFromSelection() override { return S_OK; }

        HRESULT __stdcall get_IsSelected(BOOL* value) override
        {
            if (value) *value = Current().selected ? VARIANT_TRUE : VARIANT_FALSE;
            return S_OK;
        }

        HRESULT __stdcall get_SelectionContainer(IRawElementProviderSimple** value) override
        {
            if (value) *value = nullptr;
            return S_OK;
        }
    };

    winrt::com_ptr<IRawElementProviderFragment> CardAccessibility::Root::CreateChild(int id)
    {
        auto provider = winrt::make_self<RegionProvider>();
        provider->id = id;
        provider->root = this;
        provider->lifetime = lifetime;
        return provider;
    }

    CardAccessibility::CardAccessibility() = default;
    CardAccessibility::~CardAccessibility() { Detach(); }

    void CardAccessibility::Attach(HWND window, std::wstring name, TableSource table,
        ScaleSource scale, SliderSource slider, ActionSink action, ExpandedSource expanded)
    {
        Detach();
        root = winrt::make_self<Root>();
        root->window = window;
        root->name = std::move(name);
        root->table = std::move(table);
        root->scale = std::move(scale);
        root->slider = std::move(slider);
        root->action = std::move(action);
        root->expanded = std::move(expanded);
    }

    void CardAccessibility::Detach()
    {
        if (!root) return;
        root->lifetime.reset();
        root->window = nullptr;
        root->children.clear();
        root->table = nullptr;
        root->scale = nullptr;
        root->slider = nullptr;
        root->action = nullptr;
        root->expanded = nullptr;
        root = nullptr;
    }

    void CardAccessibility::NotifyFocus(const Layout::Region* region)
    {
        if (!root) return;
        root->focusedId = region ? region->id : 0;
        if (region)
        {
            const auto child = root->CreateChild(region->id);
            const auto simple = child.as<IRawElementProviderSimple>();
            UiaRaiseAutomationEvent(simple.get(), UIA_AutomationFocusChangedEventId);
        }
    }

    void CardAccessibility::NotifyStructureChanged()
    {
        if (!root) return;
        const auto simple = root.as<IRawElementProviderSimple>();
        UiaRaiseStructureChangedEvent(simple.get(), StructureChangeType_ChildrenInvalidated, nullptr, 0);
    }

    LRESULT CardAccessibility::OnGetObject(WPARAM keys, LPARAM position)
    {
        if (!root || static_cast<LONG>(position) != static_cast<LONG>(UiaRootObjectId)) return 0;
        winrt::com_ptr<IRawElementProviderSimple> simple;
        root->get_strong().as(simple);
        if (!simple) return 0;
        return UiaReturnRawElementProvider(root->window, keys, position, simple.get());
    }
}
