#include "CardPch.h"
#include <winreg.h>
#include "CardHost.h"
#include "CardPopup.h"
#include "CardClient.h"
#include "CardDraw.h"
#include "CardProtocol.h"
#include "CardWindow.h"

#include <future>

namespace Cards
{
    namespace
    {
        constexpr UINT WM_CARD_TASK = WM_APP + 0x211;
        constexpr UINT_PTR AnimationTimer = 0xCD02;
        constexpr UINT_PTR IdleTimer = 0xCD03;
        constexpr UINT IdleMilliseconds = 10 * 60 * 1000;

        constexpr UINT COMMAND_SHOW = 1;
        constexpr UINT COMMAND_HIDE = 2;
        constexpr UINT COMMAND_CONFIG = 3;
        constexpr UINT COMMAND_PLACE = 4;
        constexpr UINT COMMAND_STOP = 5;
        constexpr UINT COMMAND_STATUS = 6;
        constexpr UINT COMMAND_ESCAPE = 7;
        constexpr UINT COMMAND_PREVIEW = 8;
        constexpr UINT COMMAND_CLEAR_PREVIEW = 9;
        constexpr UINT COMMAND_MENU = 10;
        constexpr UINT COMMAND_MENU_ACTIVATE = 11;
        constexpr UINT COMMAND_MENU_CLOSE = 12;

        struct Command
        {
            UINT kind{};
            POINT anchor{};
            Config config;
            std::wstring reason;
            bool previewMica{};
            bool previewTheme{};
            bool previewVivid{};
            bool previewDark{};
            MicaTuning previewTuning{};
            std::wstring menu;
            std::vector<Layout::Region> items;
            RECT menuAnchor{};
        };
    }


    struct CardHost::Impl : public CardHostServices
    {
        // The low-level hooks are process-wide, so the live instance is stored in
        // one atomic slot that the callbacks read.
        static inline std::atomic<Impl*> activeHook{ nullptr };

        HWND owner{};
        HWND window{};
        HINSTANCE instance{};
        std::thread thread;
        DWORD threadId{};
        Graphics graphics;
        winrt::Windows::UI::Composition::Compositor compositor{ nullptr };
        winrt::Windows::UI::Composition::CompositionGraphicsDevice device{ nullptr };
        std::map<Kind, std::unique_ptr<CardWindow>> cards;
        Snapshot snapshot;
        Config config;
        Client client;
        std::atomic<bool> visible{ false };
        bool started{ false };
        HHOOK mouseHook{};
        HHOOK keyboardHook{};
        bool shuttingDown{ false };
        bool dark{ true };
        bool highContrast{ false };
        bool reducedMotion{ false };
        POINT anchor{};
        std::wstring lastError;
        int menuDepth{ 0 };
        std::chrono::steady_clock::time_point lastInteraction{};
        bool resident{ true };
        std::unique_ptr<CardPopup> popup;
        // The popup list remembers what an accepted index means.
        std::wstring popupMenu;
        std::vector<std::wstring> popupIds;
        // The settings process previews draft Mica and theme on the real cards.
        // A preview never replaces the committed configuration.
        bool previewMica{ false };
        bool previewVivid{ true };
        MicaTuning previewTuning{};
        bool previewTheme{ false };
        bool previewDark{ false };

        // ---- command queue ------------------------------------------------

        std::mutex queueGate;
        std::deque<std::unique_ptr<Command>> queue;

        void Post(std::unique_ptr<Command> command)
        {
            {
                std::lock_guard lock(queueGate);
                if (queue.size() >= 32) queue.pop_front();
                queue.push_back(std::move(command));
            }
            if (window) PostMessageW(window, WM_CARD_TASK, 0, 0);
        }

        std::vector<std::unique_ptr<Command>> TakeCommands()
        {
            std::deque<std::unique_ptr<Command>> taken;
            {
                std::lock_guard lock(queueGate);
                taken.swap(queue);
            }
            return std::vector<std::unique_ptr<Command>>(std::make_move_iterator(taken.begin()),
                std::make_move_iterator(taken.end()));
        }

        // ---- CardHostServices ----------------------------------------------

        void OnRequest(const std::wstring& action, const std::wstring& jsonPayload) override;
        void OnHideCards(const std::wstring& reason) override;
        void OnOpenSettings() override;
        void OnMenuOpened(bool open) override;
        void OnLayoutInvalidated() override;

        // ---- lifecycle ------------------------------------------------------

        bool Start(HWND ownerWindow, HINSTANCE application)
        {
            if (started) return true;
            owner = ownerWindow;
            instance = application;
            std::promise<bool> ready;
            auto future = ready.get_future();
            thread = std::thread([this, &ready] { Run(std::move(ready)); });
            const bool ok = future.get();
            started = ok;
            return ok;
        }

        void Stop()
        {
            if (!started) return;
            auto command = std::make_unique<Command>();
            command->kind = COMMAND_STOP;
            Post(std::move(command));
            if (thread.joinable()) thread.join();
            UninstallHooks();
            started = false;
        }

        void Run(std::promise<bool> ready);
        void CreateCards();
        void PlaceGroup(bool animate, bool showAll);
        void UpdateEnvironment(bool force);
        void RefreshSnapshot(const Protocol::Changes& changes);
        void OnTask();
        void OnPipeMessages();
        void OnIdle();
        CardWindow* Card(Kind kind) const;
        bool AnyMenuOpen() const { return menuDepth > 0; }
        void HideAll(const std::wstring& reason, bool animate);
        void ShowCards();
        void DestroyIdleCards();
        void RequestInitialState();
        void ApplyConfig(const Config& value);
        void Status(const wchar_t* message);
        void SendSnapshotRequest();
        void DetectEnvironment();
        void OpenMenu(const std::wstring& menu, const std::vector<Layout::Region>& items, RECT anchor);
        void ShowDeviceMenu(const std::wstring& flow);
        void ShowSourceMenu();
        void OnCardAction(const std::wstring& action, const std::wstring& name);
        void ActivateMenu();
        void CloseMenu();
        bool MenuContains(POINT point) const;
        void InstallHooks();
        void UninstallHooks();
        void OnGlobalPointer(POINT point, bool leftButton);
        bool EscapeCard();
    };

    void CardHost::Impl::Status(const wchar_t* message)
    {
        snapshot.statusText = message ? message : L"";
    }

    void CardHost::Impl::SendSnapshotRequest()
    {
        // cardSnapshot arrives unsolicited on connect. The explicit request keeps
        // a reconnect deterministic even when the first push is coalesced away.
        client.Request(L"cardSnapshot");
    }

    void CardHost::Impl::RequestInitialState()
    {
        client.Request(L"getAudio");
        client.Request(L"getTrafficHistory");
        client.Request(L"weatherCommand", "{\"operation\":\"status\"}");
        client.Request(L"getState");
        client.Request(L"getUpdateState");
        client.Request(L"subscribeCardLyrics", "{\"visible\":true,\"session\":1}");
    }

    void CardHost::Impl::CreateCards()
    {
        CardEnvironment environment;
        environment.graphics = &graphics;
        environment.compositor = compositor;
        environment.device = device;
        environment.dark = dark;
        environment.highContrast = highContrast;
        environment.reducedMotion = reducedMotion;
        environment.mica = config.mica;
        environment.fontFamily = config.font.empty() ? L"Microsoft YaHei UI" : config.font;
        for (Kind kind : { Kind::Weather, Kind::Traffic, Kind::Device, Kind::Media, Kind::Update })
        {
            if (cards.find(kind) != cards.end()) continue;
            auto card = std::make_unique<CardWindow>(kind, *this);
            if (!card->Create(environment, instance))
            {
                lastError = L"card window creation failed";
                continue;
            }
            card->ApplyEnvironment(environment);
            card->SetSnapshot(snapshot);
            cards[kind] = std::move(card);
        }
    }

    CardWindow* CardHost::Impl::Card(Kind kind) const
    {
        const auto found = cards.find(kind);
        return found == cards.end() ? nullptr : found->second.get();
    }

    // The card thread never asks MFC for theme state: it reads the same system
    // signals WinUI used, so light/dark, contrast and motion stay consistent.
    void CardHost::Impl::DetectEnvironment()
    {
        bool appsLight = false;
        DWORD size = sizeof(appsLight);
        if (RegGetValueW(HKEY_CURRENT_USER,
            L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize",
            L"AppsUseLightTheme", RRF_RT_REG_DWORD, nullptr, &appsLight, &size) == ERROR_SUCCESS)
            dark = !appsLight;
        HIGHCONTRASTW contrast{ sizeof(contrast) };
        highContrast = SystemParametersInfoW(SPI_GETHIGHCONTRAST, sizeof(contrast), &contrast, 0) &&
            (contrast.dwFlags & HCF_HIGHCONTRASTON) != 0;
        BOOL animations = TRUE;
        reducedMotion = !SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0, &animations, 0) || !animations;
    }

    void CardHost::Impl::UpdateEnvironment(bool force)
    {
        CardEnvironment environment;
        environment.graphics = &graphics;
        environment.compositor = compositor;
        environment.device = device;
        environment.dark = previewTheme ? previewDark : dark;
        environment.highContrast = highContrast;
        environment.reducedMotion = reducedMotion;
        environment.mica = previewMica ? previewTuning : config.mica;
        if (previewMica) environment.mica.vivid = previewVivid;
        environment.fontFamily = config.font.empty() ? L"Microsoft YaHei UI" : config.font;
        for (auto& [kind, card] : cards)
        {
            UNREFERENCED_PARAMETER(kind);
            card->ApplyEnvironment(environment);
            if (force) card->SetSnapshot(snapshot);
        }
    }

    void CardHost::Impl::PlaceGroup(bool animate, bool showAll)
    {
        CardEnvironment environment;
        environment.graphics = &graphics;
        environment.compositor = compositor;
        environment.device = device;
        environment.dark = dark;
        environment.highContrast = highContrast;
        environment.reducedMotion = reducedMotion;
        environment.mica = config.mica;
        environment.fontFamily = config.font.empty() ? L"Microsoft YaHei UI" : config.font;

        std::map<Kind, float> heights;
        for (const auto& [kind, card] : cards)
        {
            if (Layout::AutoHeight(kind))
                heights[kind] = Paint::MeasureHeight(kind, snapshot, environment, Layout::WidthDip(kind));
        }
        const bool updateVisible = snapshot.update.Visible();
        const auto placement = Layout::PlaceGroup(config, heights, anchor, owner, updateVisible,
            Paint::MeasureHeight(Kind::Update, snapshot, environment, Layout::UpdateCardWidth));
        if (!placement.valid)
        {
            Status(placement.diagnostic.c_str());
            return;
        }
        lastInteraction = std::chrono::steady_clock::now();
        for (const auto& slot : placement.slots)
        {
            auto* card = Card(slot.kind);
            if (!card) continue;
            if (slot.kind == Kind::Update && !updateVisible) continue;
            card->Place(slot.bounds, slot.scale);
            if (showAll) card->Show(animate);
        }
        // Cards that are no longer part of the layout must hide, not linger.
        for (auto& [kind, card] : cards)
        {
            if (kind == Kind::Update) { if (!updateVisible) card->Hide(animate); continue; }
            if (!config.IsEnabled(kind)) card->Hide(animate);
        }
    }

    void CardHost::Impl::HideAll(const std::wstring& reason, bool animate)
    {
        UNREFERENCED_PARAMETER(reason);
        CloseMenu();
        visible = false;
        UninstallHooks();
        for (auto& [kind, card] : cards)
        {
            UNREFERENCED_PARAMETER(kind);
            card->Hide(animate);
        }
        // Lyrics subscription stops with the cards so a hidden panel never keeps
        // lyric work alive in the media service.
        client.Request(L"subscribeCardLyrics", "{\"visible\":false,\"session\":1}");
        if (window) SetTimer(window, IdleTimer, IdleMilliseconds, nullptr);
    }

    void CardHost::Impl::DestroyIdleCards()
    {
        if (visible || resident) return;
        for (auto& [kind, card] : cards)
        {
            UNREFERENCED_PARAMETER(kind);
            card->Destroy();
        }
        cards.clear();
    }

    void CardHost::Impl::ShowCards()
    {
        if (!graphics.Ready())
        {
            if (!graphics.Initialize())
            {
                Status(graphics.Diagnostic().c_str());
                return;
            }
        }
        if (!device)
        {
            std::wstring error;
            // A graphics device belongs to a compositor, so create one per card
            // window lazily and hand it over; the shared D3D device is reused.
            device = nullptr;
            UNREFERENCED_PARAMETER(error);
        }
        visible = true;
        if (window) KillTimer(window, IdleTimer);
        if (cards.empty()) CreateCards();
        PlaceGroup(true, true);
        InstallHooks();
        RequestInitialState();
    }

    void CardHost::Impl::ApplyConfig(const Config& value)
    {
        config = value;
        resident = !config.OnDemand();
        UpdateEnvironment(true);
        if (visible) PlaceGroup(false, false);
    }

    void CardHost::Impl::RefreshSnapshot(const Protocol::Changes& changes)
    {
        for (auto& [kind, card] : cards)
        {
            UNREFERENCED_PARAMETER(kind);
            card->SetSnapshot(snapshot);
        }
        if (changes.config)
        {
            resident = !config.OnDemand();
            UpdateEnvironment(true);
        }
        if (visible && (changes.config || changes.update)) PlaceGroup(false, false);
        // A ready update must never open the group by itself: the update card only
        // joins the layout while the user already has cards open.
        if (changes.update && !snapshot.update.Visible() && visible)
        {
            for (const auto& slot : std::vector<Kind>{ Kind::Update })
            {
                auto* card = Card(slot);
                if (card) card->Hide(true);
            }
        }
    }

    void CardHost::Impl::OnPipeMessages()
    {
        const auto messages = client.TakeMessages();
        bool changed = false;
        Protocol::Changes accumulated;
        for (const auto& raw : messages)
        {
            const auto message = Protocol::Parse(raw);
            if (message.kind == Protocol::Kind::Snapshot)
            {
                Protocol::Changes changes;
                if (Protocol::ApplySnapshot(snapshot, message.payloadJson, changes))
                {
                    changed = true;
                    accumulated.config |= changes.config;
                    accumulated.media |= changes.media;
                    accumulated.audio |= changes.audio;
                    accumulated.lyrics |= changes.lyrics;
                    accumulated.traffic |= changes.traffic;
                    accumulated.weather |= changes.weather;
                    accumulated.update |= changes.update;
                    accumulated.status |= changes.status;
                }
            }
            else if (message.kind == Protocol::Kind::Reply)
            {
                const auto reply = Protocol::ParseReply(message);
                const auto colon = reply.requestId.find(L':');
                if (reply.payloadJson.empty()) continue;
                if (reply.requestId.rfind(L"audio", 0) == 0)
                {
                    if (Protocol::ApplyAudioReply(snapshot.audio, reply.payloadJson))
                    {
                        accumulated.audio = true;
                        changed = true;
                    }
                }
                else if (reply.requestId.rfind(L"traffic", 0) == 0)
                {
                    if (Protocol::ApplyTrafficReply(snapshot.traffic, reply.payloadJson))
                    {
                        accumulated.traffic = true;
                        changed = true;
                    }
                }
                else if (reply.requestId.rfind(L"weather", 0) == 0)
                {
                    if (Protocol::ApplyWeatherReply(snapshot.weather, reply.payloadJson))
                    {
                        accumulated.weather = true;
                        changed = true;
                    }
                }
                else if (reply.requestId.rfind(L"media", 0) == 0)
                {
                    if (Protocol::ApplyMediaReply(snapshot.media, reply.payloadJson))
                    {
                        accumulated.media = true;
                        changed = true;
                    }
                }
                UNREFERENCED_PARAMETER(colon);
            }
            else if (message.kind == Protocol::Kind::Event && message.action == L"shutdown")
            {
                HideAll(L"shutdown", false);
            }
        }
        if (changed) RefreshSnapshot(accumulated);
    }

    void CardHost::Impl::OnTask()
    {
        for (auto& command : TakeCommands())
        {
            switch (command->kind)
            {
            case COMMAND_SHOW:
                anchor = command->anchor;
                ShowCards();
                break;
            case COMMAND_HIDE:
                HideAll(command->reason, true);
                break;
            case COMMAND_CONFIG:
                ApplyConfig(command->config);
                break;
            case COMMAND_PLACE:
                if (visible) PlaceGroup(false, false);
                break;
            case COMMAND_ESCAPE:
                // Escape closes an open menu first; a second Escape closes cards.
                if (!EscapeCard()) HideAll(L"escape", true);
                break;
            case COMMAND_PREVIEW:
                // A preview only changes what the card thread draws; the
                // committed configuration stays untouched until it is republished.
                if (command->previewMica)
                {
                    previewMica = true;
                    // A non-vivid preview is the committed fallback: Mica off.
                    previewVivid = command->previewVivid;
                    previewTuning = command->previewTuning;
                }
                if (command->previewTheme)
                {
                    previewTheme = true;
                    previewDark = command->previewDark;
                }
                UpdateEnvironment(true);
                break;
            case COMMAND_CLEAR_PREVIEW:
                previewMica = false;
                previewTheme = false;
                UpdateEnvironment(true);
                break;
            case COMMAND_MENU:
                OpenMenu(command->menu, command->items, command->menuAnchor);
                break;
            case COMMAND_MENU_ACTIVATE:
                ActivateMenu();
                break;
            case COMMAND_MENU_CLOSE:
                CloseMenu();
                break;
            case COMMAND_STATUS:
                break;
            case COMMAND_STOP:
                shuttingDown = true;
                break;
            default:
                break;
            }
        }
        if (shuttingDown)
        {
            HideAll(L"shutdown", false);
            PostMessageW(window, WM_CLOSE, 0, 0);
        }
    }

    void CardHost::Impl::OnIdle()
    {
        if (window) KillTimer(window, IdleTimer);
        if (!visible) DestroyIdleCards();
    }

    void CardHost::Impl::InstallHooks()
    {
        if (mouseHook || keyboardHook) return;
        Impl::activeHook = this;
        mouseHook = SetWindowsHookExW(WH_MOUSE_LL, [](int code, WPARAM keys, LPARAM data) -> LRESULT
        {
            if (code == HC_ACTION && (keys == WM_LBUTTONDOWN || keys == WM_RBUTTONDOWN))
            {
                auto* self = Impl::activeHook.load();
                if (self && self->visible) self->OnGlobalPointer(
                    reinterpret_cast<MSLLHOOKSTRUCT*>(data)->pt, keys == WM_LBUTTONDOWN);
            }
            return CallNextHookEx(nullptr, code, keys, data);
        }, nullptr, 0);
        keyboardHook = SetWindowsHookExW(WH_KEYBOARD_LL, [](int code, WPARAM keys, LPARAM data) -> LRESULT
        {
            if (code == HC_ACTION && (keys == WM_KEYDOWN || keys == WM_SYSKEYDOWN))
            {
                auto* self = Impl::activeHook.load();
                const auto* info = reinterpret_cast<KBDLLHOOKSTRUCT*>(data);
                if (self && self->visible && info->vkCode == VK_ESCAPE)
                    PostMessageW(self->window, WM_CARD_TASK, COMMAND_ESCAPE, 0);
            }
            return CallNextHookEx(nullptr, code, keys, data);
        }, nullptr, 0);
    }

    void CardHost::Impl::UninstallHooks()
    {
        Impl::activeHook = nullptr;
        if (mouseHook) { UnhookWindowsHookEx(mouseHook); mouseHook = nullptr; }
        if (keyboardHook) { UnhookWindowsHookEx(keyboardHook); keyboardHook = nullptr; }
    }

    void CardHost::Impl::OnGlobalPointer(POINT point, bool leftButton)
    {
        // A click inside any card or inside an open menu keeps the group open.
        // Anything else dismisses it, exactly like the WinUI outside-click monitor.
        if (MenuContains(point)) return;
        for (const auto& [kind, card] : cards)
        {
            UNREFERENCED_PARAMETER(kind);
            if (!card->Visible()) continue;
            RECT bounds = card->Bounds();
            if (point.x >= bounds.left && point.x < bounds.right &&
                point.y >= bounds.top && point.y < bounds.bottom)
                return;
        }
        UNREFERENCED_PARAMETER(leftButton);
        HideAll(L"outside", true);
    }

    // ---- services ------------------------------------------------------------

    void CardHost::Impl::OnRequest(const std::wstring& action, const std::wstring& jsonPayload)
    {
        // Card actions map onto the small set of card-channel requests.
        std::wstring request = action;
        std::wstring payload;
        const auto quote = [](const std::wstring& value) { return Cards::Protocol::JsonString(value); };

        if (action == L"previous" || action == L"playPause" || action == L"next")
        {
            payload = L"{\"action\":" + quote(action) + L"}";
            request = L"mediaCommand";
        }
        else if (action == L"seek")
        {
            payload = L"{\"action\":\"seek\",\"positionSeconds\":" + jsonPayload + L"}";
            request = L"mediaCommand";
        }
        else if (action == L"refreshWeather")
        {
            request = L"weatherCommand";
            payload = L"{\"operation\":\"refresh\"}";
        }
        else if (action == L"openSettings")
        {
            OnOpenSettings();
            return;
        }
        else if (action == L"installUpdate" || action == L"downloadUpdate" ||
            action == L"cancelUpdateDownload")
        {
            request = action;
        }
        else if (action.rfind(L"toggleMute:", 0) == 0)
        {
            const std::wstring flow = action.substr(11);
            const auto& endpoints = flow == L"input" ? snapshot.audio.inputs : snapshot.audio.outputs;
            const AudioEndpoint* endpoint = nullptr;
            for (const auto& item : endpoints) if (item.isDefault) { endpoint = &item; break; }
            if (!endpoint && !endpoints.empty()) endpoint = &endpoints.front();
            if (!endpoint) return;
            payload = L"{\"muted\":" + std::wstring(endpoint->isMuted ? L"false" : L"true") +
                L",\"flow\":" + quote(flow) + L",\"deviceId\":" + quote(endpoint->id) + L"}";
            request = L"setMute";
        }
        else if (action.rfind(L"setVolume:", 0) == 0)
        {
            const std::wstring flow = action.substr(10);
            const auto& endpoints = flow == L"input" ? snapshot.audio.inputs : snapshot.audio.outputs;
            const AudioEndpoint* endpoint = nullptr;
            for (const auto& item : endpoints) if (item.isDefault) { endpoint = &item; break; }
            if (!endpoint && !endpoints.empty()) endpoint = &endpoints.front();
            if (!endpoint) return;
            payload = L"{\"volume\":" + jsonPayload + L",\"flow\":" + quote(flow) +
                L",\"deviceId\":" + quote(endpoint->id) + L"}";
            request = L"setVolume";
        }
        else if (action.rfind(L"showDevices:", 0) == 0)
        {
            request = L"openMenu";
            payload = L"{\"menu\":\"devices\",\"flow\":" + quote(action.substr(12)) + L"}";
        }
        else if (action == L"selectDay")
        {
            snapshot.traffic.Select(jsonPayload);
            if (auto* card = Card(Kind::Traffic)) card->SetSnapshot(snapshot);
            return;
        }
        else if (action == L"showSources")
        {
            return;
        }
        if (!request.empty()) client.Request(request, Cards::WideToUtf8(payload));
    }
    void CardHost::Impl::OnHideCards(const std::wstring& reason) { HideAll(reason, true); }

    // The menu is drawn by the shared popup window; selection is turned back into
    // the same cardRequest a card widget would send.
    // A device list is built from the current audio snapshot, so the menu always
    // shows what the engine just reported.
    void CardHost::Impl::ShowDeviceMenu(const std::wstring& flow)
    {
        auto* card = Card(Kind::Device);
        if (!card || !card->Visible()) return;
        const auto& endpoints = flow == L"input" ? snapshot.audio.inputs : snapshot.audio.outputs;
        std::vector<Layout::Region> items;
        for (const auto& endpoint : endpoints)
        {
            Layout::Region item;
            item.id = static_cast<int>(items.size()) + 1;
            item.type = Layout::RegionType::MenuItem;
            item.enabled = true;
            item.selected = endpoint.isDefault;
            item.focusable = true;
            item.action = L"selectDevice:" + flow + L":" + endpoint.id;
            item.name = endpoint.isDefault ? endpoint.name + L"（默认）" : endpoint.name;
            items.push_back(std::move(item));
        }
        if (items.empty())
        {
            Layout::Region item;
            item.id = 1;
            item.type = Layout::RegionType::MenuItem;
            item.enabled = false;
            item.name = L"没有可用设备";
            items.push_back(std::move(item));
        }
        OpenMenu(L"devices", items, card->Bounds());
    }

    void CardHost::Impl::ShowSourceMenu()
    {
        auto* card = Card(Kind::Media);
        if (!card || !card->Visible()) return;
        std::vector<Layout::Region> items;
        for (const auto& source : snapshot.media.sources)
        {
            Layout::Region item;
            item.id = static_cast<int>(items.size()) + 1;
            item.type = Layout::RegionType::MenuItem;
            item.enabled = true;
            item.selected = source.id == snapshot.media.selectedSourceId;
            item.focusable = true;
            item.action = L"selectSource:" + source.id;
            item.name = source.label.empty() ? source.id : source.label;
            items.push_back(std::move(item));
        }
        if (items.empty())
        {
            Layout::Region item;
            item.id = 1;
            item.type = Layout::RegionType::MenuItem;
            item.enabled = false;
            item.name = L"没有可切换的播放源";
            items.push_back(std::move(item));
        }
        OpenMenu(L"sources", items, card->Bounds());
    }

    // A menu selection re-enters the ordinary card action path.
    void CardHost::Impl::OnCardAction(const std::wstring& action, const std::wstring& name)
    {
        UNREFERENCED_PARAMETER(name);
        if (action.rfind(L"selectDevice:", 0) == 0)
        {
            const std::wstring rest = action.substr(13);
            const auto split = rest.find(L':');
            if (split == std::wstring::npos) return;
            const std::wstring flow = rest.substr(0, split);
            const std::wstring id = rest.substr(split + 1);
            const std::wstring payload = L"{\"flow\":" + Protocol::JsonString(flow) +
                L",\"deviceId\":" + Protocol::JsonString(id) + L"}";
            client.Request(L"selectDevice", Cards::WideToUtf8(payload));
            return;
        }
        if (action.rfind(L"selectSource:", 0) == 0)
        {
            const std::wstring id = action.substr(13);
            const std::wstring payload = L"{\"action\":\"selectSource\",\"sourceId\":" +
                Protocol::JsonString(id) + L"}";
            client.Request(L"mediaCommand", Cards::WideToUtf8(payload));
        }
    }

    void CardHost::Impl::OpenMenu(const std::wstring& menu, const std::vector<Layout::Region>& items,
        RECT anchor)
    {
        if (items.empty()) return;
        if (!popup) popup = std::make_unique<CardPopup>();
        CardEnvironment environment;
        environment.graphics = &graphics;
        environment.compositor = compositor;
        environment.device = device;
        environment.dark = previewTheme ? previewDark : dark;
        environment.highContrast = highContrast;
        environment.reducedMotion = reducedMotion;
        environment.mica = config.mica;
        environment.fontFamily = config.font.empty() ? L"Microsoft YaHei UI" : config.font;
        if (!popup->Create(instance, environment))
        {
            popup.reset();
            return;
        }
        popup->SetActivation([this]
        {
            auto command = std::make_unique<Command>();
            command->kind = COMMAND_MENU_ACTIVATE;
            Post(std::move(command));
        });
        popupMenu = menu;
        popup->Show(menu, items, anchor);
    }

    void CardHost::Impl::ActivateMenu()
    {
        if (!popup || !popup->Visible()) return;
        const int index = popup->ActivateSelected();
        const auto items = popup->Items();
        const std::wstring menu = popupMenu;
        CloseMenu();
        if (index < 0 || index >= static_cast<int>(items.size())) return;
        const std::wstring& action = items[static_cast<size_t>(index)].action;
        OnCardAction(action, items[static_cast<size_t>(index)].name);
    }

    void CardHost::Impl::CloseMenu()
    {
        if (popup) popup->Hide();
        popupMenu.clear();
        if (visible) PlaceGroup(false, false);
    }

    bool CardHost::Impl::MenuContains(POINT point) const
    {
        return popup && popup->Contains(point);
    }

    bool CardHost::Impl::EscapeCard()
    {
        // A menu belongs to the owning card, so Escape closes the menu first and
        // only a second Escape closes the whole group.
        if (popup && popup->Visible())
        {
            CloseMenu();
            return true;
        }
        bool handled = false;
        for (auto& [kind, card] : cards)
        {
            UNREFERENCED_PARAMETER(kind);
            if (card->Visible() && card->OnEscape()) handled = true;
        }
        return handled;
    }

    void CardHost::Impl::OnOpenSettings()
    {
        // Media owns the settings window; the card asks it to start WinUI on demand.
        client.Request(L"openSettings");
    }

    void CardHost::Impl::OnMenuOpened(bool open)
    {
        menuDepth = std::max(0, menuDepth + (open ? 1 : -1));
    }

    void CardHost::Impl::OnLayoutInvalidated()
    {
        if (visible) PlaceGroup(false, false);
    }

    void CardHost::Impl::Run(std::promise<bool> ready)
    {
        threadId = GetCurrentThreadId();
        // The card thread owns its own STA apartment. RoInitialize is idempotent
        // per apartment and this thread never initialized MFC.
        const HRESULT apartment = RoInitialize(RO_INIT_SINGLETHREADED);
        const bool apartmentOk = SUCCEEDED(apartment) || apartment == RPC_E_CHANGED_MODE;

        DetectEnvironment();
        const bool graphicsOk = graphics.Initialize();
        lastError = graphics.Diagnostic();

        WNDCLASSEXW definition{ sizeof(definition) };
        definition.lpfnWndProc = [](HWND window, UINT message, WPARAM keys, LPARAM position) -> LRESULT
        {
            auto* self = reinterpret_cast<CardHost::Impl*>(GetWindowLongPtrW(window, GWLP_USERDATA));
            if (!self) return DefWindowProcW(window, message, keys, position);
            switch (message)
            {
            case WM_CARD_PIPE_WAKE:
                self->OnPipeMessages();
                return 0;
            case WM_CARD_TASK:
                self->OnTask();
                return 0;
            case WM_SETTINGCHANGE:
            case WM_THEMECHANGED:
                self->DetectEnvironment();
                self->UpdateEnvironment(true);
                if (self->visible) self->PlaceGroup(false, false);
                return 0;
            case WM_DISPLAYCHANGE:
                if (self->visible) self->PlaceGroup(false, false);
                return 0;
            case WM_TIMER:
                if (keys == AnimationTimer)
                {
                    bool animating = false;
                    for (auto& [kind, card] : self->cards)
                    {
                        UNREFERENCED_PARAMETER(kind);
                        animating |= card->Tick();
                    }
                    if (!animating) KillTimer(window, AnimationTimer);
                }
                else if (keys == IdleTimer)
                {
                    self->OnIdle();
                }
                return 0;
            default:
                return DefWindowProcW(window, message, keys, position);
            }
        };
        definition.hInstance = instance;
        definition.lpszClassName = L"TaskBarWidgetsCardHost";
        RegisterClassExW(&definition);

        window = CreateWindowExW(0, definition.lpszClassName, L"", 0, 0, 0, 0, 0, HWND_MESSAGE,
            nullptr, instance, nullptr);
        if (!window)
        {
            if (apartmentOk) RoUninitialize();
            ready.set_value(false);
            return;
        }
        SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(this));

        if (graphicsOk)
        {
            try
            {
                // One compositor and one composition graphics device serve every
                // card, menu and popup on this thread.
                compositor = winrt::Windows::UI::Composition::Compositor();
                std::wstring error;
                device = graphics.CreateGraphicsDevice(compositor, error);
                if (!device) lastError = error;
            }
            catch (const winrt::hresult_error& error)
            {
                lastError = std::wstring(error.message().c_str());
            }
        }
        {
            std::lock_guard lock(queueGate);
            started = true;
        }
        ready.set_value(graphicsOk);

        client.Start(window);
        SendSnapshotRequest();

        MSG message{};
        while (GetMessageW(&message, nullptr, 0, 0) > 0)
        {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }

        // Teardown: stop producers first, then release device-bound resources.
        client.Stop();
        for (auto& [kind, card] : cards)
        {
            UNREFERENCED_PARAMETER(kind);
            card->Destroy();
        }
        cards.clear();
        device = nullptr;
        graphics.Release();
        if (window)
        {
            SetWindowLongPtrW(window, GWLP_USERDATA, 0);
            DestroyWindow(window);
            window = nullptr;
        }
        if (apartmentOk && SUCCEEDED(apartment)) RoUninitialize();
    }

    // ---- CardHost facade ----------------------------------------------------

    CardHost::CardHost() : impl(std::make_shared<Impl>()) {}
    CardHost::~CardHost() { Stop(); }

    bool CardHost::Start(HWND owner, HINSTANCE instance)
    {
        return impl->Start(owner, instance);
    }

    void CardHost::Stop() { impl->Stop(); }

    void CardHost::Toggle(POINT anchor)
    {
        if (impl->visible) Hide(L"toggle");
        else Show(anchor);
    }

    void CardHost::Show(POINT anchor)
    {
        auto command = std::make_unique<Command>();
        command->kind = COMMAND_SHOW;
        command->anchor = anchor;
        impl->Post(std::move(command));
    }

    void CardHost::Hide(const wchar_t* reason)
    {
        auto command = std::make_unique<Command>();
        command->kind = COMMAND_HIDE;
        command->reason = reason ? reason : L"";
        impl->Post(std::move(command));
    }

    bool CardHost::Visible() const { return impl->visible; }

    void CardHost::ApplyConfig(const Config& config)
    {
        auto command = std::make_unique<Command>();
        command->kind = COMMAND_CONFIG;
        command->config = config;
        impl->Post(std::move(command));
    }

    void CardHost::PreviewMica(bool vivid, const MicaTuning& tuning)
    {
        auto command = std::make_unique<Command>();
        command->kind = COMMAND_PREVIEW;
        command->previewMica = true;
        command->previewTuning = tuning;
        command->previewVivid = vivid;
        impl->Post(std::move(command));
    }

    bool CardHost::HasPreview() const { return impl->previewMica || impl->previewTheme; }

    MicaTuning CardHost::CommittedMica() const { return impl->config.mica; }

    void CardHost::PreviewTheme(bool darkMode, bool enabled)
    {
        auto command = std::make_unique<Command>();
        command->kind = COMMAND_PREVIEW;
        command->previewTheme = enabled;
        command->previewDark = darkMode;
        impl->Post(std::move(command));
    }

    void CardHost::ClearPreview()
    {
        auto command = std::make_unique<Command>();
        command->kind = COMMAND_CLEAR_PREVIEW;
        impl->Post(std::move(command));
    }

    void CardHost::Refresh()
    {
        auto command = std::make_unique<Command>();
        command->kind = COMMAND_PLACE;
        impl->Post(std::move(command));
    }

    bool CardsAvailable() { return true; }
}
