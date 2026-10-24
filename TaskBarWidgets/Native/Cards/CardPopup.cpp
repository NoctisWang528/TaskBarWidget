#include "CardPch.h"
#include "CardPopup.h"

#include "CardDraw.h"
#include "CardAccessibility.h"
#include "CardWindow.h"

#include <windows.ui.composition.interop.h>

#include <algorithm>
#include <functional>
#include <cmath>

namespace Cards
{
    namespace
    {
        constexpr wchar_t ClassName[] = L"TaskBarWidgetsCardPopup";
        constexpr float RowHeight = 30.0f;
        constexpr float Padding = 6.0f;
        constexpr float TextSize = 12.0f;
        constexpr float MaximumHeight = 300.0f;
        constexpr float MinimumWidth = 180.0f;
        constexpr float MaximumWidth = 320.0f;
    }

    struct CardPopup::Impl
    {
        HWND window{};
        HINSTANCE instance{};
        Graphics* graphics{};
        winrt::Windows::UI::Composition::Compositor compositor{ nullptr };
        winrt::Windows::UI::Composition::CompositionGraphicsDevice device{ nullptr };
        winrt::Windows::UI::Composition::Desktop::DesktopWindowTarget target{ nullptr };
        winrt::Windows::UI::Composition::SpriteVisual root{ nullptr };
        winrt::Windows::UI::Composition::ContainerVisual motion{ nullptr };
        winrt::Windows::UI::Composition::SpriteVisual content{ nullptr };
        DrawingSurface surface;
        SurfaceHandle boundSurface{ nullptr };
        CardAccessibility accessibility;
        Layout::Table regions;
        CardEnvironment environment{};
        std::wstring menu;
        std::function<void()> activate;
        std::vector<Layout::Region> items;
        int selected{ -1 };
        int hot{ -1 };
        int pressed{ -1 };
        int firstVisible{};
        float scale{ 1.0f };
        bool visible{};
        bool dark{ true };

        int VisibleRows() const
        {
            const int rows = static_cast<int>(items.size());
            const int fit = static_cast<int>(std::floor((MaximumHeight - Padding * 2) / RowHeight));
            return (std::max)(1, (std::min)(rows, fit));
        }

        float WidthDip() const
        {
            if (!graphics) return MinimumWidth;
            float widest = MinimumWidth;
            const auto family = environment.fontFamily.empty() ? L"Microsoft YaHei UI" : environment.fontFamily;
            const auto format = graphics->TextFormat(family, TextSize, 400, DWRITE_TEXT_ALIGNMENT_LEADING, false);
            for (const auto& item : items)
            {
                if (!format || !graphics->DWrite()) continue;
                winrt::com_ptr<IDWriteTextLayout> layout;
                if (FAILED(graphics->DWrite()->CreateTextLayout(item.name.c_str(),
                    static_cast<UINT32>(item.name.size()), format, 1000.0f, RowHeight, layout.put())))
                    continue;
                DWRITE_TEXT_METRICS metrics{};
                if (SUCCEEDED(layout->GetMetrics(&metrics)))
                    widest = (std::max)(widest, metrics.widthIncludingTrailingWhitespace + 60.0f);
            }
            return (std::min)(widest, MaximumWidth);
        }

        float HeightDip() const
        {
            return Padding * 2 + RowHeight * static_cast<float>(VisibleRows());
        }

        int ItemAt(float y) const
        {
            if (y < Padding || y >= Padding + VisibleRows() * RowHeight) return -1;
            const int index = firstVisible + static_cast<int>((y - Padding) / RowHeight);
            return index >= 0 && index < static_cast<int>(items.size()) ? index : -1;
        }

        void Paint();
        void Place(POINT anchorPoint, RECT anchor, bool diagnosticOffscreen);
    };

    void CardPopup::Impl::Paint()
    {
        if (!visible || !compositor || !device || !graphics) return;
        const int widthPixels = (std::max)(1, static_cast<int>(std::lround(WidthDip() * scale)));
        const int heightPixels = (std::max)(1, static_cast<int>(std::lround(HeightDip() * scale)));
        root.Size({ static_cast<float>(widthPixels), static_cast<float>(heightPixels) });
        surface.Ensure(device, widthPixels, heightPixels);
        if (!surface.Ready()) return;
        if (boundSurface != surface.Handle())
        {
            const auto brush = compositor.CreateSurfaceBrush(surface.Handle());
            brush.Stretch(winrt::Windows::UI::Composition::CompositionStretch::Fill);
            content.Brush(brush);
            boundSurface = surface.Handle();
        }
        DrawScope scope(surface);
        if (!scope) return;
        ID2D1DeviceContext* context = scope.Get();
        D2D1_MATRIX_3X2_F surfaceTransform{};
        context->GetTransform(&surfaceTransform);
        context->SetDpi(96.0f, 96.0f);
        context->Clear(D2D1::ColorF(0, 0.0f));
        context->SetTransform(D2D1::Matrix3x2F::Scale(scale, scale) * surfaceTransform);

        const Draw::Palette palette = Draw::Colors(environment);
        const float width = WidthDip();
        const float height = HeightDip();
        // A popup is a floating surface over arbitrary content, so it paints its
        // own opaque rounded panel instead of relying on Mica.
        Draw::FillRound(context, Draw::Rect(0, 0, width, height), 8.0f,
            environment.highContrast ? palette.panel : environment.dark ? Color{0.125f, 0.125f, 0.125f, 1} : Color{0.953f, 0.953f, 0.953f, 1});
        Draw::StrokeRound(context, Draw::Rect(0.5f, 0.5f, width - 0.5f, height - 0.5f), 8.0f,
            WithAlpha(palette.border, environment.dark ? 0.24f : 0.16f), 1.0f);
        const int rows = VisibleRows();
        regions.Clear();
        for (int index = 0; index < rows; ++index)
        {
            const int itemIndex = firstVisible + index;
            if (itemIndex >= static_cast<int>(items.size())) break;
            const auto& item = items[static_cast<size_t>(itemIndex)];
            const float top = Padding + RowHeight * static_cast<float>(index);
            auto region = item;
            region.id = itemIndex + 1;
            region.bounds = RECT{ 0, static_cast<LONG>(top), static_cast<LONG>(width),
                static_cast<LONG>(top + RowHeight) };
            regions.Add(std::move(region));
            if (itemIndex == selected || itemIndex == hot)
            {
                const auto highlight = WithAlpha(palette.accent,
                    itemIndex == selected ? 0.23f : 0.125f);
                Draw::FillRound(context, Draw::Rect(4.0f, top + 1.0f, width - 4.0f, top + RowHeight - 1.0f),
                    5.0f, highlight);
            }
            Draw::Text(context, *graphics, item.name, environment.fontFamily, TextSize,
                item.selected ? 600 : 400, item.enabled ? palette.primary : palette.secondary,
                Draw::Rect(14.0f, top, width - 24.0f, top + RowHeight),
                DWRITE_TEXT_ALIGNMENT_LEADING, false, false, false, DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
            if (item.selected)
                Draw::FillRound(context, Draw::Rect(width - 18.0f, top + RowHeight / 2 - 3.0f,
                    width - 12.0f, top + RowHeight / 2 + 3.0f), 3.0f, palette.accent);
        }
        if (items.size() > static_cast<size_t>(rows))
        {
            // A simple scrollbar communicates that the list continues.
            const float trackTop = Padding + 2.0f;
            const float trackHeight = height - Padding * 2 - 4.0f;
            const float ratio = static_cast<float>(rows) / static_cast<float>(items.size());
            const float thumb = (std::max)(18.0f, trackHeight * ratio);
            const float start = trackTop + (trackHeight - thumb) *
                (static_cast<float>(firstVisible) / static_cast<float>((std::max)(1,
                    static_cast<int>(items.size()) - rows)));
            Draw::FillRound(context, Draw::Rect(width - 5.0f, start, width - 3.0f, start + thumb),
                1.0f, WithAlpha(palette.secondary, 0.70f));
        }
    }

    void CardPopup::Impl::Place(POINT anchorPoint, RECT anchor, bool diagnosticOffscreen)
    {
        const HMONITOR monitor = MonitorFromPoint(anchorPoint, MONITOR_DEFAULTTONEAREST);
        MONITORINFO info{ sizeof(info) };
        const RECT work = GetMonitorInfoW(monitor, &info) ? info.rcWork : RECT{ 0, 0, 1920, 1080 };
        // The popup picks its own monitor and DPI instead of inheriting the card's.
        // Move the hidden/no-activate HWND onto the target monitor before asking
        // for its DPI; the old monitor's DPI is wrong on a mixed-DPI desktop.
        SetWindowPos(window, nullptr, anchorPoint.x, anchorPoint.y, 0, 0,
            SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER);
        scale = static_cast<float>(GetDpiForWindow(window)) / 96.0f;
        if (scale <= 0.0f) scale = 1.0f;

        const int width = static_cast<int>(std::lround(WidthDip() * scale));
        const int height = static_cast<int>(std::lround(HeightDip() * scale));
        // Preference: below the anchor, left aligned with it, then flip up or left
        // when the work area has no room.
        int left = anchor.left;
        int top = anchor.bottom + static_cast<int>(std::lround(4 * scale));
        if (top + height > work.bottom) top = anchor.top - height - static_cast<int>(std::lround(4 * scale));
        top = (std::max)(static_cast<int>(work.top), (std::min)(top, static_cast<int>(work.bottom) - height));
        left = (std::max)(static_cast<int>(work.left), (std::min)(left, static_cast<int>(work.right) - width));
        if (diagnosticOffscreen)
        {
            left = GetSystemMetrics(SM_XVIRTUALSCREEN) - width - 1000;
            top = GetSystemMetrics(SM_YVIRTUALSCREEN) - height - 1000;
        }
        SetWindowPos(window, HWND_TOPMOST, left, top, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    CardPopup::CardPopup() : impl(std::make_unique<Impl>()) { }
    CardPopup::~CardPopup() { Destroy(); }

    bool CardPopup::Create(HINSTANCE instance, const CardEnvironment& environment)
    {
        impl->instance = instance;
        impl->environment = environment;
        impl->graphics = environment.graphics;
        impl->dark = environment.dark;
        if (impl->window) { ApplyEnvironment(environment); return true; }

        WNDCLASSEXW definition{ sizeof(definition) };
        definition.lpfnWndProc = [](HWND window, UINT message, WPARAM keys, LPARAM position) -> LRESULT
        {
            auto* self = reinterpret_cast<Impl*>(GetWindowLongPtrW(window, GWLP_USERDATA));
            if (!self) return DefWindowProcW(window, message, keys, position);
            switch (message)
            {
            case WM_ERASEBKGND: return 1;
            case WM_PAINT:
            {
                PAINTSTRUCT paint{};
                BeginPaint(window, &paint);
                EndPaint(window, &paint);
                self->Paint();
                return 0;
            }
            case WM_GETOBJECT:
                return self->accessibility.OnGetObject(keys, position);
            case WM_DPICHANGED:
            {
                self->scale = static_cast<float>(HIWORD(keys)) / 96.0f;
                const auto* suggested = reinterpret_cast<const RECT*>(position);
                SetWindowPos(window, nullptr, suggested->left, suggested->top,
                    static_cast<int>(std::lround(self->WidthDip() * self->scale)),
                    static_cast<int>(std::lround(self->HeightDip() * self->scale)),
                    SWP_NOACTIVATE | SWP_NOZORDER);
                self->Paint();
                return 0;
            }
            case WM_MOUSEACTIVATE:
                // The popup never takes activation away from the user's app.
                return MA_NOACTIVATE;
            case WM_MOUSEMOVE:
            {
                TRACKMOUSEEVENT tracking{ sizeof(tracking), TME_LEAVE, window, 0 };
                TrackMouseEvent(&tracking);
                const int index = self->ItemAt(GET_Y_LPARAM(position) / self->scale);
                if (index != self->hot) { self->hot = index; self->Paint(); }
                return 0;
            }
            case WM_MOUSELEAVE:
                self->hot = -1;
                self->Paint();
                return 0;
            case WM_LBUTTONDOWN:
            {
                const int index = self->ItemAt(GET_Y_LPARAM(position) / self->scale);
                if (index >= 0 && self->items[static_cast<size_t>(index)].enabled)
                {
                    SetCapture(window);
                    self->pressed = index;
                    self->selected = index;
                    self->Paint();
                }
                return 0;
            }
            case WM_LBUTTONUP:
            {
                const POINT point{GET_X_LPARAM(position), GET_Y_LPARAM(position)};
                RECT client{}; GetClientRect(window, &client);
                const int index = self->ItemAt(point.y / self->scale);
                const bool activate = GetCapture() == window && PtInRect(&client, point) &&
                    index >= 0 && index == self->pressed && self->items[static_cast<size_t>(index)].enabled;
                self->pressed = -1;
                if (GetCapture() == window) ReleaseCapture();
                if (activate && self->activate) self->activate();
                return 0;
            }
            case WM_CAPTURECHANGED:
            case WM_CANCELMODE:
                self->pressed = -1;
                if (message == WM_CANCELMODE && GetCapture() == window) ReleaseCapture();
                return 0;
            case WM_MOUSEWHEEL:
                self->firstVisible = (std::max)(0, (std::min)(
                    static_cast<int>(self->items.size()) - self->VisibleRows(),
                    self->firstVisible + (GET_WHEEL_DELTA_WPARAM(keys) > 0 ? -1 : 1)));
                self->Paint();
                return 0;
            default:
                return DefWindowProcW(window, message, keys, position);
            }
        };
        definition.hInstance = instance;
        definition.lpszClassName = ClassName;
        RegisterClassExW(&definition);
        impl->window = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP,
            ClassName, L"", WS_POPUP, 0, 0, 10, 10, nullptr, nullptr, instance, nullptr);
        if (!impl->window) return false;
        SetWindowLongPtrW(impl->window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(impl.get()));
        ApplyEnvironment(environment);
        return true;
    }

    void CardPopup::Destroy()
    {
        if (!impl) return;
        impl->accessibility.Detach();
        impl->visible = false;
        if (impl->target)
        {
            try { impl->target.Root(nullptr); impl->target.Close(); } catch (...) { }
            impl->target = nullptr;
        }
        impl->root = nullptr;
        impl->motion = nullptr;
        impl->content = nullptr;
        impl->boundSurface = nullptr;
        impl->device = nullptr;
        impl->compositor = nullptr;
        if (impl->window)
        {
            DestroyWindow(impl->window);
            impl->window = nullptr;
        }
        impl->surface.Release();
        impl->items.clear();
    }

    void CardPopup::ApplyEnvironment(const CardEnvironment& environment)
    {
        impl->environment = environment;
        impl->graphics = environment.graphics;
        impl->dark = environment.dark;
        if (!impl->window || !environment.compositor || !environment.device) return;
        if (impl->device != environment.device || impl->compositor != environment.compositor)
        {
            impl->surface.Release();
            impl->boundSurface = nullptr;
            if (impl->content) impl->content.Brush(nullptr);
        }
        if (impl->compositor != environment.compositor || !impl->root)
        {
            impl->compositor = environment.compositor;
            impl->device = environment.device;
            impl->root = impl->compositor.CreateSpriteVisual();
            impl->motion = impl->compositor.CreateContainerVisual();
            impl->motion.RelativeSizeAdjustment({ 1.0f, 1.0f });
            impl->content = impl->compositor.CreateSpriteVisual();
            impl->content.RelativeSizeAdjustment({ 1.0f, 1.0f });
            impl->motion.Children().InsertAtTop(impl->content);
            impl->root.Children().InsertAtTop(impl->motion);
            winrt::Windows::UI::Composition::Desktop::DesktopWindowTarget created{ nullptr };
            const auto interop = impl->compositor.as<ABI::Windows::UI::Composition::Desktop::ICompositorDesktopInterop>();
            if (impl->target)
            {
                impl->target.Root(nullptr);
                impl->target.Close();
                impl->target = nullptr;
            }
            winrt::check_hresult(interop->CreateDesktopWindowTarget(impl->window, TRUE,
                reinterpret_cast<ABI::Windows::UI::Composition::Desktop::IDesktopWindowTarget**>(
                    winrt::put_abi(created))));
            impl->target = created;
            impl->target.Root(impl->root);
        }
        impl->device = environment.device;
    }

    void CardPopup::Show(const std::wstring& menu, const std::vector<Layout::Region>& items, RECT anchor,
        bool diagnosticOffscreen)
    {
        if (!impl->window) return;
        impl->accessibility.Detach();
        impl->menu = menu;
        impl->items = items;
        impl->selected = -1;
        for (size_t index = 0; index < items.size(); ++index)
        {
            if (!items[index].enabled) continue;
            if (impl->selected < 0 || items[index].selected) impl->selected = static_cast<int>(index);
            if (items[index].selected) break;
        }
        impl->hot = -1;
        impl->firstVisible = (std::max)(0, impl->selected - impl->VisibleRows() + 1);
        impl->visible = true;
        const POINT anchorPoint{ anchor.left, anchor.top };
        impl->Place(anchorPoint, anchor, diagnosticOffscreen);
        impl->Paint();
        auto* self = impl.get();
        impl->accessibility.Attach(impl->window, menu,
            [self]() -> const Layout::Table& { return self->regions; },
            [self] { return self->scale; }, {},
            [self](const Layout::Region& region, const std::wstring&)
            {
                const int index = region.id - 1;
                if (index < 0 || index >= static_cast<int>(self->items.size()) ||
                    !self->items[static_cast<size_t>(index)].enabled) return;
                self->selected = index;
                if (self->activate) self->activate();
            }, {}, [self](int id)
            {
                const int index = id - 1;
                if (index < 0 || index >= static_cast<int>(self->items.size())) return;
                self->selected = index;
                self->firstVisible = (std::max)(0, index - self->VisibleRows() + 1);
                self->Paint();
            });
        ShowWindow(impl->window, SW_SHOWNOACTIVATE);
    }

    void CardPopup::Hide()
    {
        if (!impl->visible) return;
        impl->visible = false;
        impl->pressed = -1;
        if (GetCapture() == impl->window) ReleaseCapture();
        impl->accessibility.Detach();
        impl->regions.Clear();
        impl->items.clear();
        impl->selected = -1;
        impl->menu.clear();
        if (impl->window) ShowWindow(impl->window, SW_HIDE);
    }

    bool CardPopup::Visible() const { return impl->visible; }
    const std::wstring& CardPopup::Menu() const { return impl->menu; }

    RECT CardPopup::Bounds() const
    {
        RECT bounds{};
        if (impl->window) GetWindowRect(impl->window, &bounds);
        return bounds;
    }

    bool CardPopup::Contains(POINT pixels) const
    {
        if (!impl->visible) return false;
        const RECT bounds = Bounds();
        return pixels.x >= bounds.left && pixels.x < bounds.right &&
            pixels.y >= bounds.top && pixels.y < bounds.bottom;
    }

    int CardPopup::HitTest(POINT pixels) const
    {
        if (!impl->visible) return -1;
        const RECT bounds = Bounds();
        if (pixels.x < bounds.left || pixels.x >= bounds.right ||
            pixels.y < bounds.top || pixels.y >= bounds.bottom) return -1;
        return impl->ItemAt((pixels.y - bounds.top) / impl->scale);
    }

    void CardPopup::MoveSelection(int delta)
    {
        if (impl->items.empty()) return;
        const int count = static_cast<int>(impl->items.size());
        if (!delta) return;
        int next = impl->selected + (delta > 0 ? 1 : -1);
        while (next >= 0 && next < count && !impl->items[static_cast<size_t>(next)].enabled)
            next += delta > 0 ? 1 : -1;
        if (next < 0 || next >= count) return;
        impl->selected = next;
        const int rows = impl->VisibleRows();
        if (next < impl->firstVisible) impl->firstVisible = next;
        else if (next >= impl->firstVisible + rows) impl->firstVisible = next - rows + 1;
        impl->Paint();
    }

    int CardPopup::ActivateSelected() const
    {
        return impl->selected >= 0 && impl->selected < static_cast<int>(impl->items.size()) &&
            impl->items[static_cast<size_t>(impl->selected)].enabled ? impl->selected : -1;
    }

    int CardPopup::Selected() const { return impl->selected; }

    const std::vector<Layout::Region>& CardPopup::Items() const { return impl->items; }

    void CardPopup::SetActivation(std::function<void()> callback) { impl->activate = std::move(callback); }

    void CardPopup::Wheel(int delta)
    {
        if (impl->items.empty()) return;
        impl->firstVisible = (std::max)(0, (std::min)(
            static_cast<int>(impl->items.size()) - impl->VisibleRows(),
            impl->firstVisible + (delta > 0 ? -1 : 1)));
        impl->Paint();
    }
}
