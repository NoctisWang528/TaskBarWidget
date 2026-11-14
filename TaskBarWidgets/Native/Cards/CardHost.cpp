#include "CardPch.h"
#include <winreg.h>
#include "CardHost.h"
#include "CardPopup.h"
#include "CardClient.h"
#include "CardDraw.h"
#include "CardProtocol.h"
#include "CardWindow.h"
#include "CardRuntime.h"
#include "CardBackdropCapture.h"

#include <future>
#include <cwchar>
#include <filesystem>
#include <fstream>
#include <shlobj.h>

namespace Cards
{
    namespace
    {
        void AppendBackdropLog(const wchar_t* message) noexcept
        {
            struct LastError { DWORD value{ GetLastError() }; ~LastError() { SetLastError(value); } } lastError;
            try
            {
                PWSTR rawPath{};
                const HRESULT result = SHGetKnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_DEFAULT, nullptr, &rawPath);
                std::unique_ptr<wchar_t, decltype(&CoTaskMemFree)> folder(rawPath, &CoTaskMemFree);
                if (FAILED(result) || !folder) return;
                const auto directory = std::filesystem::path(folder.get()) / L"TaskBarWidgets" / L"Logs";
                std::error_code error;
                std::filesystem::create_directories(directory, error);
                if (error) return;
                const auto path = directory / L"card-backdrop.log";
                const auto size = std::filesystem::file_size(path, error);
                const auto mode = !error && size >= 64 * 1024 ? std::ios::trunc : std::ios::app;
                std::ofstream log(path, std::ios::binary | mode);
                if (!log) return;
                SYSTEMTIME now{};
                GetLocalTime(&now);
                char prefix[96]{};
                sprintf_s(prefix, "%04u-%02u-%02u %02u:%02u:%02u.%03u pid=%lu ",
                    now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond,
                    now.wMilliseconds, GetCurrentProcessId());
                log << prefix << WideToUtf8(message);
            }
            catch (...) {} // Diagnostics must never interrupt card lifecycle or rendering.
        }

        constexpr UINT WM_CARD_WHEEL = WM_APP + 0x218;
        constexpr UINT WM_CARD_TASK = WM_APP + 0x211;
        constexpr UINT_PTR IdleTimer = 0xCD03;
        constexpr UINT_PTR WarmupTimer = 0xCD04;
        constexpr UINT IdleMilliseconds = 10 * 60 * 1000;
        constexpr UINT WarmupMilliseconds = 250;

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
        constexpr UINT COMMAND_OWNER = 13;
        constexpr UINT COMMAND_TOGGLE = 14;
        constexpr UINT COMMAND_RECOVER = 15;

        struct Command
        {
            UINT kind{};
            POINT anchor{};
            Config config;
            std::wstring reason;
            bool previewMica{};
            bool previewTheme{};
            bool setTheme{};
            bool previewVivid{};
            bool previewDark{};
            MicaTuning previewTuning{};
            MaterialSettings previewMaterial{};
            std::wstring menu;
            std::vector<Layout::Region> items;
            RECT menuAnchor{};
            HWND owner{};
        };
    }


    static RECT MenuAnchor(const CardWindow& card, const std::wstring& action);

    struct CardHost::Impl : public CardHostServices
    {
        // The low-level hooks are process-wide, so the live instance is stored in
        // one atomic slot that the callbacks read.
        static inline std::atomic<Impl*> activeHook{ nullptr };

        HWND owner{};
        std::atomic<HWND> window{};
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
        bool diagnosticOffscreen{ false };
        HHOOK mouseHook{};
        Layout::PageWheel pageWheel;
        HWND observedWheelTarget{};
        HHOOK keyboardHook{};
        bool shuttingDown{ false };
        bool dark{ true };
        bool highContrast{ false };
        bool reducedMotion{ false };
        std::map<Kind, RECT> groupRestBounds;
        std::map<Kind, RECT> groupReflowFrom;
        float groupReflowProgress{1};
        HANDLE groupTimer{};
        DesktopBackdrop backdrop;
        HANDLE backdropTimer{};
        bool backdropTimerArmed{};
        HMONITOR backdropMonitor{};
        ULONGLONG backdropRetryAt{};
        unsigned backdropRetries{};
        bool backdropBlocked{};
        mutable std::mutex backdropMutex;
        BackdropDiagnostics backdropDiagnostics;
        uint64_t backdropRenderedFrames{};
        std::wstring backdropStage{ L"inactive" };
        HRESULT backdropLastResult{ S_OK };
        bool groupMoving{};
        float groupOffset{}, groupFrom{}, groupTo{}, groupDistance{};
        int groupDuration{240};
        std::chrono::steady_clock::time_point groupStarted, groupNextFrame;
        std::chrono::nanoseconds groupFrameInterval{};
        POINT anchor{};
        std::wstring lastError;
        ULONGLONG errorUntil{};
        std::chrono::steady_clock::time_point lastInteraction{};
        bool resident{ true };
        bool warmupPending{};
        bool startupWarmup{ true };
        bool warmupConfigReady{};
        bool warmupSnapshotReady{};
        bool startupDataRequested{};
        ULONGLONG startupWarmupDeadline{};
        std::vector<std::wstring> startupRequests;
        std::unique_ptr<CardPopup> popup;
        std::wstring popupMenu;
        RECT popupAnchor{};
        std::map<std::wstring, std::wstring> pendingSliders;
        // The settings process previews draft Mica and theme on the real cards.
        // A preview never replaces the committed configuration.
        bool previewMica{ false };
        bool previewVivid{ true };
        MicaTuning previewTuning{};
        MaterialSettings previewMaterial{};
        bool previewTheme{ false };
        bool previewDark{ false };

        // ---- command queue ------------------------------------------------

        std::mutex queueGate;
        std::deque<std::unique_ptr<Command>> queue;

        void Post(std::unique_ptr<Command> command)
        {
            {
                std::lock_guard lock(queueGate);
                if (command->kind != COMMAND_TOGGLE)
                    std::erase_if(queue, [&](const auto& pending) { return pending->kind == command->kind; });
                if (queue.size() >= 32 && command->kind == COMMAND_TOGGLE) return;
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
        void OnLayoutInvalidated() override;
        void OnPageLayoutChanged() override { if (visible) PlaceGroup(false, false, true); }
        void OnGraphicsFailure(HRESULT result) override
        {
            if (result != D2DERR_RECREATE_TARGET && result != DXGI_ERROR_DEVICE_REMOVED &&
                result != DXGI_ERROR_DEVICE_RESET && result != DXGI_ERROR_DEVICE_HUNG) return;
            auto command = std::make_unique<Command>(); command->kind = COMMAND_RECOVER; Post(std::move(command));
        }
        bool IsMenuExpanded(const std::wstring& action) const override
        {
            if (!popup || !popup->Visible()) return false;
            return (action == L"showSources" && popupMenu == L"sources") ||
                (action.starts_with(L"showDevices:") && popupMenu == action.substr(12));
        }
        bool OnMenuKey(UINT key) override
        {
            if (!popup || !popup->Visible()) return false;
            switch (key)
            {
            case VK_UP: popup->MoveSelection(-1); return true;
            case VK_DOWN: popup->MoveSelection(1); return true;
            case VK_RETURN: case VK_SPACE: ActivateMenu(); return true;
            case VK_ESCAPE: CloseMenu(); return true;
            case VK_TAB: CloseMenu(); return false;
            case VK_LEFT: case VK_RIGHT: return true;
            default: return false;
            }
        }
        bool OnMenuWheel(int delta) override
        {
            if (!popup || !popup->Visible()) return false;
            popup->Wheel(delta);
            return true;
        }

        HWND WheelTarget(POINT point) const
        {
            if (!visible) return nullptr;
            // Do not act through another topmost window that obscures a card.
            const auto hit = WindowFromPoint(point);
            if (popup && popup->Contains(point))
            {
                DWORD process{};
                GetWindowThreadProcessId(hit, &process);
                return process == GetCurrentProcessId() ? window.load() : nullptr;
            }
            for (const auto& [kind, card] : cards)
                if (card->Visible() && hit == card->Handle() &&
                    (kind == Kind::Weather || kind == Kind::PeripheralBattery)) return hit;
            return nullptr;
        }

        void RouteWheel(HWND target, int delta)
        {
            if (!visible) { pageWheel.Reset(); return; }
            const int pages = pageWheel.Consume(target, delta);
            if (!pages) return;
            if (target == window.load())
            {
                if (!popup || !popup->Visible()) { pageWheel.Reset(); return; }
                for (int i = 0; i < std::abs(pages); ++i) popup->Wheel(pages > 0 ? -WHEEL_DELTA : WHEEL_DELTA);
                return;
            }
            for (const auto& [kind, card] : cards)
                if (card->Visible() && card->Handle() == target)
                {
                    card->TurnPage(pages);
                    break;
                }
        }

        bool OnWheel(POINT point, int delta) override
        {
            // With the scoped hook installed, its posted event is the sole dispatcher.
            // Native wheel delivery may target the focused HWND instead of the hovered one.
            if (!mouseHook) RouteWheel(WheelTarget(point), delta);
            return true;
        }

        // ---- lifecycle ------------------------------------------------------

        bool Start(HWND ownerWindow, HINSTANCE application)
        {
            if (started) return true;
            owner = ownerWindow;
            instance = application;
            std::promise<bool> ready;
            auto future = ready.get_future();
            thread = std::thread([this, ready = std::move(ready)]() mutable { Run(std::move(ready)); });
            const bool ok = future.get();
            started = ok;
            if (!ok && thread.joinable()) thread.join();
            return ok;
        }

        void Stop()
        {
            if (!thread.joinable()) return;
            auto command = std::make_unique<Command>();
            command->kind = COMMAND_STOP;
            Post(std::move(command));
            if (thread.joinable()) thread.join();
            UninstallHooks();
            started = false;
        }

        void Run(std::promise<bool> ready);
        void CreateCards();
        void PlaceGroup(bool animate, bool showAll, bool reflow = false);
        void MoveGroup(float offset);
        void AnimateGroup(bool showing, bool animate);
        void StartGroupTimer();
        void StopGroupMotion(bool settle);
        void GroupFrame();
        void RestoreBackdropWindows();
        void StopBackdrop(bool resetRetry = false, bool preserveDiagnostic = false);
        bool ArmBackdropTimer(LONGLONG delay);
        void UpdateBackdropDiagnostics(const wchar_t* stage, HRESULT result = S_OK);
        void RecordBackdropFailure(const wchar_t* operation, HRESULT result);
        void FailBackdrop(const wchar_t* operation, HRESULT result);
        void ConfigureBackdrop(bool scheduleTick = true);
        void BackdropTick();
        void UpdateEnvironment(bool force);
        void RefreshSnapshot(const Protocol::Changes& changes);
        void OnTask();
        void OnPipeMessages();
        void OnIdle();
        CardWindow* Card(Kind kind) const;
        void HideAll(const std::wstring& reason, bool animate);
        void ShowCards();
        void DestroyIdleCards();
        void ScheduleWarmup();
        void CancelWarmup();
        void WarmHiddenCards();
        void UpdateIdleTimer();
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
        void CloseMenu(bool animate = true);
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
        client.Request(L"cardSnapshot", "{\"artworkDelta\":true}", L"state");
    }

    void CardHost::Impl::RequestInitialState()
    {
        // Resident caches are refreshed by Media before the group opens. The
        // visibility subscription revalidates them without duplicate requests.
        if (!resident)
        {
            client.Request(L"getAudio", {}, L"audio");
            client.Request(L"getTrafficHistory", {}, L"traffic");
            client.Request(L"weatherCommand", "{\"operation\":\"status\"}", L"weather");
        }
        client.Request(L"getState", "{\"artworkDelta\":true}", L"state");
        client.Request(L"getUpdateState", {}, L"update");
        client.Request(L"getPeripheralBatteries", {}, L"peripheral");
        client.Request(L"subscribeCardLyrics", "{\"visible\":true,\"session\":1}", L"lyrics");
    }

    void CardHost::Impl::CreateCards()
    {
        if (popup && popup->Visible() && !config.IsEnabled(popupMenu == L"sources" ? Kind::Media : Kind::Device))
            CloseMenu();
        CardEnvironment environment;
        environment.allowLiveCapture = !diagnosticOffscreen;
        environment.graphics = &graphics;
        environment.compositor = compositor;
        environment.device = device;
        environment.dark = previewTheme ? previewDark : snapshot.toolWindowTheme == 1 ? false : snapshot.toolWindowTheme == 2 ? true : dark;
        environment.highContrast = highContrast;
        environment.reducedMotion = reducedMotion;
        environment.mica = previewMica ? previewTuning : config.mica;
        environment.material = previewMica ? previewMaterial : config.material;
        if (previewMica) environment.mica.vivid = previewVivid;
        environment.fontFamily = config.font.empty() ? L"Microsoft YaHei UI" : config.font;
        for (Kind kind : { Kind::Weather, Kind::Traffic, Kind::Device, Kind::Media, Kind::Update, Kind::PeripheralBattery })
        {
            const bool enabled = kind == Kind::Update ? snapshot.update.Visible() : config.IsEnabled(kind) &&
                (kind != Kind::PeripheralBattery || snapshot.peripheralBattery.Visible());
            if (!enabled) { cards.erase(kind); continue; }
            if (cards.find(kind) != cards.end()) continue;
            auto card = std::make_unique<CardWindow>(kind, *this);
            if (!card->Create(environment, instance))
            {
                lastError = L"card window creation failed";
                continue;
            }
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
        DWORD appsLight = 1;
        DWORD size = sizeof(appsLight);
        if (RegGetValueW(HKEY_CURRENT_USER,
            L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize",
            L"AppsUseLightTheme", RRF_RT_REG_DWORD, nullptr, &appsLight, &size) == ERROR_SUCCESS)
            dark = appsLight == 0;
        HIGHCONTRASTW contrast{ sizeof(contrast) };
        highContrast = SystemParametersInfoW(SPI_GETHIGHCONTRAST, sizeof(contrast), &contrast, 0) &&
            (contrast.dwFlags & HCF_HIGHCONTRASTON) != 0;
        BOOL animations = TRUE;
        reducedMotion = !SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0, &animations, 0) || !animations;
    }

    void CardHost::Impl::UpdateEnvironment(bool force)
    {
        if ((reducedMotion || highContrast) && groupMoving) StopGroupMotion(true);
        CardEnvironment environment;
        environment.allowLiveCapture = !diagnosticOffscreen;
        environment.graphics = &graphics;
        environment.compositor = compositor;
        environment.device = device;
        environment.dark = previewTheme ? previewDark : snapshot.toolWindowTheme == 1 ? false : snapshot.toolWindowTheme == 2 ? true : dark;
        environment.highContrast = highContrast;
        environment.reducedMotion = reducedMotion;
        environment.mica = previewMica ? previewTuning : config.mica;
        environment.material = previewMica ? previewMaterial : config.material;
        if (previewMica) environment.mica.vivid = previewVivid;
        environment.fontFamily = config.font.empty() ? L"Microsoft YaHei UI" : config.font;
        for (auto& [kind, card] : cards)
        {
            UNREFERENCED_PARAMETER(kind);
            card->ApplyEnvironment(environment);
            if (force) card->SetSnapshot(snapshot);
        }
        if (popup) popup->ApplyEnvironment(environment);
        // Appearance changes invalidate renderer caches, not a healthy desktop
        // duplication session. A new environment may retry a blocked session.
        backdropRetries = 0;
        backdropBlocked = false;
        if (!backdrop.Running()) StopBackdrop();
        ConfigureBackdrop();
    }

    void CardHost::Impl::RestoreBackdropWindows()
    {
        for (auto& [kind, card] : cards) card->EnableLiveBackdrop(false);
        if (popup) popup->EnableLiveBackdrop(false);
    }

    void CardHost::Impl::StopBackdrop(bool resetRetry, bool preserveDiagnostic)
    {
        if (backdropTimer) CancelWaitableTimer(backdropTimer);
        backdropTimerArmed = false;
        backdropRetryAt = 0;
        // Stop acquiring before restoring any HWND to the captured desktop.
        backdrop.Stop();
        RestoreBackdropWindows();
        if (resetRetry)
        {
            backdropMonitor = nullptr;
            backdropRetries = 0;
            backdropBlocked = false;
        }
        if (!preserveDiagnostic) UpdateBackdropDiagnostics(L"inactive");
    }

    bool CardHost::Impl::ArmBackdropTimer(LONGLONG delay)
    {
        if (!backdropTimer)
        {
            backdropTimer = CreateWaitableTimerExW(nullptr, nullptr,
                CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_MODIFY_STATE | SYNCHRONIZE);
            if (!backdropTimer) backdropTimer = CreateWaitableTimerExW(nullptr, nullptr,
                0, TIMER_MODIFY_STATE | SYNCHRONIZE);
        }
        LARGE_INTEGER due{}; due.QuadPart = -std::max(1LL, delay);
        backdropTimerArmed = backdropTimer &&
            SetWaitableTimer(backdropTimer, &due, 0, nullptr, nullptr, FALSE);
        return backdropTimerArmed;
    }

    void CardHost::Impl::UpdateBackdropDiagnostics(const wchar_t* stage, HRESULT result)
    {
        const bool stageChanged = stage && backdropStage != stage;
        const bool errorChanged = FAILED(result) && backdropLastResult != result;
        if (stage) backdropStage = stage;
        backdropLastResult = result;
        if (stageChanged || errorChanged)
        {
            wchar_t message[256]{};
            swprintf_s(message, L"TaskBarWidgets backdrop: stage=%ls hr=0x%08X frames=%llu blocked=%d running=%d\n",
                backdropStage.c_str(), static_cast<unsigned>(backdropLastResult),
                static_cast<unsigned long long>(backdropRenderedFrames),
                backdropBlocked ? 1 : 0, backdrop.Running() ? 1 : 0);
            OutputDebugStringW(message);
            if (!diagnosticOffscreen) AppendBackdropLog(message);
        }
        std::lock_guard lock(backdropMutex);
        backdropDiagnostics.stage = backdropStage;
        backdropDiagnostics.lastResult = backdropLastResult;
        backdropDiagnostics.renderedFrames = backdropRenderedFrames;
        backdropDiagnostics.blocked = backdropBlocked;
        backdropDiagnostics.running = backdrop.Running();
    }

    void CardHost::Impl::RecordBackdropFailure(const wchar_t* operation, HRESULT result)
    {
        // Boolean-only render APIs may not expose a more specific HRESULT.
        if (SUCCEEDED(result)) result = E_FAIL;
        wchar_t diagnostic[128]{};
        swprintf_s(diagnostic, L"live backdrop %ls failed (HRESULT 0x%08X)",
            operation, static_cast<unsigned>(result));
        lastError = diagnostic;
        UpdateBackdropDiagnostics(operation, result);
    }

    void CardHost::Impl::FailBackdrop(const wchar_t* operation, HRESULT result)
    {
        RecordBackdropFailure(operation, result);
        StopBackdrop(false, true);
        // At most three attempts (initial plus two delayed retries) per open,
        // environment or display generation, even after a brief successful retry.
        if (backdropRetries >= 2)
        {
            backdropBlocked = true;
            UpdateBackdropDiagnostics(L"fallback-blocked", backdropLastResult);
            return;
        }
        ++backdropRetries;
        backdropRetryAt = GetTickCount64() + 1000;
        if (!ArmBackdropTimer(10'000'000))
        {
            RecordBackdropFailure(L"retry timer", HRESULT_FROM_WIN32(GetLastError()));
            backdropRetryAt = 0;
            backdropBlocked = true;
            UpdateBackdropDiagnostics(L"fallback-blocked", backdropLastResult);
            return;
        }
        UpdateBackdropDiagnostics(L"retry-backoff", backdropLastResult);
    }

    void CardHost::Impl::ConfigureBackdrop(bool scheduleTick)
    {
        const bool popupGlass = popup && IsWindowVisible(popup->Handle()) && popup->HasLiquidGlass();
        const bool cardGlass = std::any_of(cards.begin(), cards.end(), [](const auto& entry) {
            return entry.second->Visible() && entry.second->HasLiquidGlass();
        });
        if (!visible || diagnosticOffscreen || highContrast || shuttingDown ||
            !graphics.Ready() || !device || (!cardGlass && !popupGlass))
        {
            StopBackdrop();
            return;
        }

        // Entrance HWNDs may be temporarily below the monitor. Choose the final
        // layout instead, falling back to the taskbar entry's anchor.
        RECT restBounds{};
        for (const auto& [kind, bounds] : groupRestBounds) UnionRect(&restBounds, &restBounds, &bounds);
        const HMONITOR monitor = IsRectEmpty(&restBounds)
            ? MonitorFromPoint(anchor, MONITOR_DEFAULTTONEAREST)
            : MonitorFromRect(&restBounds, MONITOR_DEFAULTTONEAREST);
        if (monitor != backdropMonitor)
        {
            StopBackdrop(true);
            backdropMonitor = monitor;
        }
        if (backdropBlocked)
        {
            // Show() may have excluded a newly opened HWND during fallback.
            RestoreBackdropWindows();
            UpdateBackdropDiagnostics(L"fallback-blocked", backdropLastResult);
            return;
        }
        const ULONGLONG now = GetTickCount64();
        if (backdropRetryAt && now < backdropRetryAt)
        {
            RestoreBackdropWindows();
            UpdateBackdropDiagnostics(L"retry-backoff", backdropLastResult);
            if (!ArmBackdropTimer(static_cast<LONGLONG>(backdropRetryAt - now) * 10000))
            {
                RecordBackdropFailure(L"retry timer", HRESULT_FROM_WIN32(GetLastError()));
                backdropRetryAt = 0;
                backdropBlocked = true;
                UpdateBackdropDiagnostics(L"fallback-blocked", backdropLastResult);
            }
            return;
        }
        backdropRetryAt = 0;

        // All desired windows must be excluded before the first acquisition,
        // including a popup whose logical Visible() is false during its exit.
        HRESULT exclusionResult = S_OK;
        const auto enable = [&exclusionResult](auto& surface, bool desired)
        {
            if (!desired) { surface.EnableLiveBackdrop(false); return; }
            SetLastError(ERROR_SUCCESS);
            if (!surface.EnableLiveBackdrop(true))
            {
                const DWORD error = GetLastError();
                if (SUCCEEDED(exclusionResult)) exclusionResult = error ? HRESULT_FROM_WIN32(error) : E_FAIL;
            }
        };
        for (auto& [kind, card] : cards)
        {
            const bool desired = card->Visible() && card->HasLiquidGlass();
            enable(*card, desired);
        }
        if (popup) enable(*popup, popupGlass);
        if (FAILED(exclusionResult)) { FailBackdrop(L"capture exclusion", exclusionResult); return; }
        // Output duplication/device initialization can take time in the driver.
        // Keep the entrance on the existing blur until its motion finishes.
        if (!backdrop.Running() && groupMoving)
        {
            UpdateBackdropDiagnostics(L"entrance-delay");
            if (!backdropTimerArmed && !ArmBackdropTimer(166'667))
                FailBackdrop(L"startup timer", HRESULT_FROM_WIN32(GetLastError()));
            return;
        }
        if (!backdrop.Running())
        {
            if (!backdrop.Start(graphics.DxgiDevice(), graphics.ResourceContext(), monitor))
            {
                FailBackdrop(backdrop.Diagnostic(), backdrop.LastResult());
                return;
            }
            UpdateBackdropDiagnostics(backdrop.Diagnostic());
            UpdateBackdropDiagnostics(L"waiting-first-frame");
        }
        if (scheduleTick && !backdropTimerArmed && !ArmBackdropTimer(1))
            FailBackdrop(L"timer", HRESULT_FROM_WIN32(GetLastError()));
    }

    void CardHost::Impl::BackdropTick()
    {
        if (!backdropTimerArmed) return;
        // The one-shot signal was consumed by the wait. In particular, a retry
        // that starts successfully but has no frame yet must arm its next tick.
        backdropTimerArmed = false;
        const auto tickStarted = std::chrono::steady_clock::now();
        // Reconcile popup animation completion and any changed glass eligibility
        // without querying settings or repainting foreground content.
        ConfigureBackdrop(false);
        if (!backdrop.Running()) return;
        const auto interval = std::chrono::nanoseconds(1'000'000'000 / (backdrop.CrossAdapter() ? 30 : 60));
        const auto next = tickStarted + interval;
        backdrop.Poll();
        if (!backdrop.Running()) { FailBackdrop(backdrop.Diagnostic(), backdrop.LastResult()); return; }
        const auto& frame = backdrop.Frame();
        if (frame.bitmap)
        {
            for (auto& [kind, card] : cards)
                if (card->Visible() && card->HasLiquidGlass() && !card->RenderBackdrop(frame))
                {
                    FailBackdrop(FAILED(card->MaterialResult()) ? card->MaterialDiagnostic().c_str() : L"card controls",
                        card->MaterialResult());
                    return;
                }
            if (popup && IsWindowVisible(popup->Handle()) && popup->HasLiquidGlass() &&
                !popup->RenderBackdrop(frame))
            {
                FailBackdrop(FAILED(popup->MaterialResult()) ? popup->MaterialDiagnostic().c_str() : L"popup controls",
                    popup->MaterialResult());
                return;
            }
            ++backdropRenderedFrames;
            UpdateBackdropDiagnostics(L"live-refraction");
        }
        else
        {
            UpdateBackdropDiagnostics(L"waiting-first-frame", backdrop.LastResult());
        }
        // Render the cached frame even without a new desktop present: bounds,
        // control hover and popup animation can change independently of pixels.
        const auto now = std::chrono::steady_clock::now();
        // Recover late frames after at least 1 ms, without adding another full
        // interval after the work or spinning on an immediately signaled timer.
        const LONGLONG delay = std::max(10'000LL,
            std::chrono::duration_cast<std::chrono::nanoseconds>(next - now).count() / 100);
        if (!ArmBackdropTimer(delay)) FailBackdrop(L"timer", HRESULT_FROM_WIN32(GetLastError()));
    }

    void CardHost::Impl::StopGroupMotion(bool settle)
    {
        const bool wasMoving = groupMoving;
        if (groupTimer) CancelWaitableTimer(groupTimer);
        groupMoving = false;
        if (!groupReflowFrom.empty())
        {
            if (settle) { groupReflowProgress = 1; MoveGroup(0); }
            // End the content-size pin once, never on an animation frame. An
            // interrupted resize continues show/hide from its current geometry.
            for (auto& [kind, rest] : groupRestBounds)
                if (auto* card = Card(kind))
                {
                    if (!settle) rest = card->Bounds();
                    card->Place(rest, card->DpiScale());
                }
            groupReflowFrom.clear();
        }
        if (settle)
        {
            if (!visible) for (auto& [kind, card] : cards) card->Hide(false);
            MoveGroup(0);
            if (!visible && wasMoving) ScheduleWarmup();
        }
    }

    void CardHost::Impl::MoveGroup(float offset)
    {
        groupOffset = offset;
        std::vector<std::pair<CardWindow*, RECT>> moves;
        for (const auto& [kind, rest] : groupRestBounds)
        {
            if (auto* card = Card(kind); card && card->Handle())
            {
                RECT bounds = rest;
                if (const auto from = groupReflowFrom.find(kind); from != groupReflowFrom.end())
                {
                    const auto mix = [this](LONG a, LONG b) {
                        return a + static_cast<LONG>(std::lround((b - a) * groupReflowProgress));
                    };
                    bounds = {mix(from->second.left, rest.left), mix(from->second.top, rest.top),
                        mix(from->second.right, rest.right), mix(from->second.bottom, rest.bottom)};
                }
                OffsetRect(&bounds, 0, static_cast<int>(std::lround(offset)));
                moves.emplace_back(card, bounds);
            }
        }
        if (moves.empty()) return;
        const UINT flags = SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER |
            (groupReflowFrom.empty() ? SWP_NOSIZE : 0);
        HDWP transaction = BeginDeferWindowPos(static_cast<int>(moves.size()));
        for (const auto& [card, bounds] : moves)
        {
            if (!transaction) break;
            transaction = DeferWindowPos(transaction, card->Handle(), nullptr,
                bounds.left, bounds.top, bounds.right - bounds.left, bounds.bottom - bounds.top, flags);
        }
        const bool committed = transaction && EndDeferWindowPos(transaction);
        for (const auto& [card, bounds] : moves)
        {
            if (committed || SetWindowPos(card->Handle(), nullptr, bounds.left, bounds.top,
                bounds.right - bounds.left, bounds.bottom - bounds.top, flags))
                card->CommitGroupBounds(bounds);
        }
    }

    void CardHost::Impl::AnimateGroup(bool showing, bool animate)
    {
        StopGroupMotion(false);
        groupFrom = groupOffset;
        groupTo = showing ? 0.0f : groupDistance;
        if (!animate || reducedMotion || highContrast || groupRestBounds.empty() ||
            std::abs(groupTo - groupFrom) < 0.5f)
        {
            if (!showing) for (auto& [kind, card] : cards) card->Hide(false);
            MoveGroup(0);
            return;
        }
        groupDuration = std::max(1, static_cast<int>((showing ? 280 : 240) *
            (100.0f / std::clamp(config.animationSpeedPercent, 50, 300)) *
            std::min(1.0f, std::abs(groupTo - groupFrom) / std::max(1.0f, groupDistance))));
        StartGroupTimer();
    }

    void CardHost::Impl::StartGroupTimer()
    {
        // The card STA waits on a high-resolution timer alongside its messages.
        // No worker callback or continuously running background frame loop.
        MONITORINFOEXW monitor{}; monitor.cbSize = sizeof(monitor);
        DEVMODEW mode{}; mode.dmSize = sizeof(mode);
        int fps = config.animationFps;
        if (fps == 0 && GetMonitorInfoW(MonitorFromPoint(anchor, MONITOR_DEFAULTTONEAREST), &monitor) &&
            EnumDisplaySettingsW(monitor.szDevice, ENUM_CURRENT_SETTINGS, &mode) && mode.dmDisplayFrequency > 1)
            fps = static_cast<int>(mode.dmDisplayFrequency);
        groupFrameInterval = std::chrono::nanoseconds(1'000'000'000 / std::clamp(fps > 0 ? fps : 60, 30, 240));
        if (!groupTimer) groupTimer = CreateWaitableTimerExW(nullptr, nullptr,
            CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_MODIFY_STATE | SYNCHRONIZE);
        groupStarted = groupNextFrame = std::chrono::steady_clock::now();
        groupMoving = true;
        LARGE_INTEGER due{}; due.QuadPart = -1;
        if (!groupTimer || !SetWaitableTimer(groupTimer, &due, 0, nullptr, nullptr, FALSE)) StopGroupMotion(true);
    }

    void CardHost::Impl::GroupFrame()
    {
        if (!groupMoving) return;
        const float elapsed = std::chrono::duration<float, std::milli>(
            std::chrono::steady_clock::now() - groupStarted).count();
        const float t = std::clamp(elapsed / groupDuration, 0.0f, 1.0f);
        const float remaining = 1 - t;
        const float progress = visible ? 1 - remaining * remaining * remaining : t * t;
        groupReflowProgress = progress;
        MoveGroup(groupFrom + (groupTo - groupFrom) * progress);
        if (t >= 1) { StopGroupMotion(true); return; }
        const auto now = std::chrono::steady_clock::now();
        groupNextFrame += groupFrameInterval;
        // A late frame has already consumed its budget. Waiting a full interval
        // again halves the cadence around a refresh boundary (e.g. 120 -> 60).
        if (groupNextFrame < now) groupNextFrame = now;
        LARGE_INTEGER due{};
        due.QuadPart = -std::max(1LL, std::chrono::duration_cast<std::chrono::nanoseconds>(groupNextFrame - now).count() / 100);
        if (!SetWaitableTimer(groupTimer, &due, 0, nullptr, nullptr, FALSE)) StopGroupMotion(true);
    }

    void CardHost::Impl::PlaceGroup(bool animate, bool showAll, bool reflow)
    {
        const bool continuing = groupMoving && groupReflowFrom.empty();
        StopGroupMotion(false);
        const bool resize = reflow && visible && !animate && !continuing && !reducedMotion && !highContrast;
        CardEnvironment environment;
        environment.allowLiveCapture = !diagnosticOffscreen;
        environment.graphics = &graphics;
        environment.compositor = compositor;
        environment.device = device;
        environment.dark = previewTheme ? previewDark : snapshot.toolWindowTheme == 1 ? false : snapshot.toolWindowTheme == 2 ? true : dark;
        environment.highContrast = highContrast;
        environment.reducedMotion = reducedMotion;
        environment.mica = previewMica ? previewTuning : config.mica;
        environment.material = previewMica ? previewMaterial : config.material;
        if (previewMica) environment.mica.vivid = previewVivid;
        environment.fontFamily = config.font.empty() ? L"Microsoft YaHei UI" : config.font;

        std::map<Kind, float> heights;
        for (const auto& [kind, card] : cards)
        {
            if (Layout::AutoHeight(kind))
                heights[kind] = Paint::MeasureHeight(kind, snapshot, environment, Layout::WidthDip(kind), card->PeripheralPage());
        }
        const bool updateVisible = snapshot.update.Visible();
        auto visibleConfig = config;
        visibleConfig.enabled[L"peripheralBattery"] = config.IsEnabled(Kind::PeripheralBattery) && snapshot.peripheralBattery.Visible();
        const auto placement = Layout::PlaceGroup(visibleConfig, heights, anchor, owner, updateVisible,
            Paint::MeasureHeight(Kind::Update, snapshot, environment, Layout::UpdateCardWidth));
        if (!placement.valid)
        {
            if (visible && placement.diagnostic == L"no visible card")
            {
                HideAll(L"no-selected-cards", false);
                groupRestBounds.clear();
                return;
            }
            StopGroupMotion(true);
            Status(placement.diagnostic.c_str());
            return;
        }
        lastInteraction = std::chrono::steady_clock::now();
        groupRestBounds.clear();
        RECT groupBounds{};
        for (const auto& slot : placement.slots) UnionRect(&groupBounds, &groupBounds, &slot.bounds);
        MONITORINFO monitor{sizeof(monitor)};
        const LONG bottom = GetMonitorInfoW(MonitorFromPoint(anchor, MONITOR_DEFAULTTONEAREST), &monitor)
            ? monitor.rcMonitor.bottom : groupBounds.bottom;
        // Move the highest card (including its shadow) beyond the physical screen,
        // not just the work area above the taskbar. Use the real layout before the
        // diagnostic-only offscreen translation below.
        groupDistance = static_cast<float>(std::max(0L, bottom - groupBounds.top)) +
            Layout::EdgeReserve * placement.dpiScale;
        if (!continuing) groupOffset = animate && !reducedMotion && !highContrast ? groupDistance : 0;
        for (const auto& slot : placement.slots)
        {
            auto* card = Card(slot.kind);
            if (!card) continue;
            if (slot.kind == Kind::Update && !updateVisible) continue;
            RECT bounds = slot.bounds;
            if (diagnosticOffscreen) OffsetRect(&bounds,
                GetSystemMetrics(SM_XVIRTUALSCREEN) - groupBounds.right - 1000,
                GetSystemMetrics(SM_YVIRTUALSCREEN) - groupBounds.bottom - 1000);
            groupRestBounds[slot.kind] = bounds;
            OffsetRect(&bounds, 0, static_cast<int>(std::lround(groupOffset)));
            const auto previous = card->Bounds();
            if (resize && card->Visible() && card->DpiScale() == slot.scale && !EqualRect(&previous, &bounds))
            {
                groupReflowFrom[slot.kind] = previous;
                card->PrepareGroupReflow(bounds, slot.scale);
            }
            else card->Place(bounds, slot.scale);
            if (showAll && visible)
            {
                EnableWindow(card->Handle(), TRUE);
                if (!card->Visible()) card->Show(false);
            }
        }
        // Cards that are no longer part of the layout must hide, not linger.
        for (auto& [kind, card] : cards)
        {
            if (!groupRestBounds.contains(kind)) card->Hide(false);
        }
        if (popup && popup->Visible())
        {
            const auto* ownerCard = Card(popupMenu == L"sources" ? Kind::Media : Kind::Device);
            const auto current = ownerCard ? MenuAnchor(*ownerCard,
                popupMenu == L"sources" ? L"showSources" : L"showDevices:" + popupMenu) : RECT{};
            if (!ownerCard || !ownerCard->Visible() || !EqualRect(&current, &popupAnchor)) CloseMenu();
        }
        if (!groupReflowFrom.empty())
        {
            CloseMenu(false);
            groupFrom = groupTo = groupOffset = 0;
            groupReflowProgress = 0;
            MoveGroup(0);
            groupDuration = std::max(1, static_cast<int>(180 *
                (100.0f / std::clamp(config.animationSpeedPercent, 50, 300))));
            StartGroupTimer();
        }
        else if (animate || continuing) AnimateGroup(visible, true);
        ConfigureBackdrop();
    }

    void CardHost::Impl::HideAll(const std::wstring& reason, bool animate)
    {
        UNREFERENCED_PARAMETER(reason);
        visible = false;
        StopBackdrop();
        CloseMenu(animate);
        UninstallHooks();
        for (auto& [kind, card] : cards)
        {
            UNREFERENCED_PARAMETER(kind);
            EnableWindow(card->Handle(), FALSE);
        }
        AnimateGroup(false, animate);
        // Lyrics subscription stops with the cards so a hidden panel never keeps
        // lyric work alive in the media service.
        client.Request(L"subscribeCardLyrics", "{\"visible\":false,\"session\":1}");
        UpdateIdleTimer();
        ScheduleWarmup();
    }

    void CardHost::Impl::UpdateIdleTimer()
    {
        if (!window) return;
        if (visible || resident) KillTimer(window, IdleTimer);
        else SetTimer(window, IdleTimer, IdleMilliseconds, nullptr);
    }

    void CardHost::Impl::CancelWarmup()
    {
        if (window) KillTimer(window, WarmupTimer);
        warmupPending = false;
    }

    void CardHost::Impl::ScheduleWarmup()
    {
        if (!window || visible || (!resident && !startupWarmup) || shuttingDown || groupMoving || warmupPending) return;
        if (!warmupConfigReady || (!diagnosticOffscreen && !warmupSnapshotReady)) return;
        // Coalesce incoming snapshots without postponing work on every update.
        // A hidden group has no animation or continuous frame timer.
        warmupPending = SetTimer(window, WarmupTimer, WarmupMilliseconds, nullptr) != 0;
    }

    void CardHost::Impl::WarmHiddenCards()
    {
        CancelWarmup();
        if (visible || (!resident && !startupWarmup) || shuttingDown || groupMoving) return;
        if (!warmupConfigReady || (!diagnosticOffscreen && !warmupSnapshotReady)) return;
        if (startupWarmup && !startupDataRequested)
        {
            startupDataRequested = true;
            startupWarmupDeadline = GetTickCount64() + 10000;
            // Read-only cache preparation: never mark hidden cards visible or refresh weather.
            if (!diagnosticOffscreen)
            {
                auto history = client.Request(L"getTrafficHistory", {}, L"traffic");
                if (!history.empty()) startupRequests.push_back(std::move(history));
                if (!resident)
                    for (auto id : { client.Request(L"getAudio", {}, L"audio"),
                        client.Request(L"weatherCommand", "{\"operation\":\"status\"}", L"weather") })
                        if (!id.empty()) startupRequests.push_back(std::move(id));
            }
        }
        // On-demand may already have released the shared drawing resources.
        if (!graphics.Ready() && !graphics.Initialize()) return;
        if (!device) { std::wstring error; device = graphics.CreateGraphicsDevice(compositor, error); }
        if (!device) return;
        RECT entry{};
        if (owner && GetWindowRect(owner, &entry))
            anchor = { entry.left + (entry.right - entry.left) / 2, entry.top };
        CreateCards();
        PlaceGroup(false, false);
        if (startupWarmup)
        {
            if (startupRequests.empty() || GetTickCount64() >= startupWarmupDeadline)
            {
                startupWarmup = false;
                startupRequests.clear();
                UpdateIdleTimer();
            }
            else ScheduleWarmup();
        }
    }

    void CardHost::Impl::DestroyIdleCards()
    {
        if (visible || resident) return;
        startupWarmup = false;
        startupRequests.clear();
        CancelWarmup();
        StopGroupMotion(false);
        StopBackdrop();
        groupRestBounds.clear();
        for (auto& [kind, card] : cards)
        {
            UNREFERENCED_PARAMETER(kind);
            card->Destroy();
        }
        cards.clear();
        popup.reset();
        device = nullptr;
        graphics.Release();
    }

    void CardHost::Impl::ShowCards()
    {
        if (!visible) StopBackdrop(true);
        startupWarmup = false;
        startupRequests.clear();
        CancelWarmup();
        if (!graphics.Ready())
        {
            if (!graphics.Initialize())
            {
                Status(graphics.Diagnostic().c_str());
                return;
            }
        }
        if (!device) { std::wstring error; device = graphics.CreateGraphicsDevice(compositor, error); }
        if (!device) return;
        visible = true;
        if (window) KillTimer(window, IdleTimer);
        CreateCards();
        PlaceGroup(true, true);
        visible = std::any_of(cards.begin(), cards.end(), [](const auto& entry) { return entry.second->Visible(); });
        if (!visible) { StopBackdrop(); return; }
        if (!diagnosticOffscreen)
            if (auto* card = Card(Kind::Media); card && card->Visible()) SetForegroundWindow(card->Handle());
        InstallHooks();
        RequestInitialState();
    }

    void CardHost::Impl::ApplyConfig(const Config& value)
    {
        warmupConfigReady = true;
        config = value;
        snapshot.config = value;
        resident = !config.OnDemand();
        if (!resident && !startupWarmup) CancelWarmup();
        UpdateIdleTimer();
        UpdateEnvironment(true);
        if (visible)
        {
            CreateCards();
            PlaceGroup(false, true);
            return;
        }
        ScheduleWarmup();
    }

    void CardHost::Impl::RefreshSnapshot(const Protocol::Changes& changes)
    {
        const auto* peripheral = Card(Kind::PeripheralBattery);
        const bool peripheralLayout = changes.peripheralBattery &&
            ((peripheral != nullptr) != (config.IsEnabled(Kind::PeripheralBattery) && snapshot.peripheralBattery.Visible()) ||
                (peripheral && (peripheral->CurrentSnapshot().peripheralBattery.mode != snapshot.peripheralBattery.mode ||
                    peripheral->CurrentSnapshot().peripheralBattery.Selected().size() != snapshot.peripheralBattery.Selected().size())));
        for (auto& [kind, card] : cards)
        {
            const bool changed = changes.config || changes.theme || changes.status ||
                (kind == Kind::Media && (changes.media || changes.lyrics)) ||
                (kind == Kind::Device && changes.audio) || (kind == Kind::Traffic && changes.traffic) ||
                (kind == Kind::Weather && changes.weather) || (kind == Kind::Update && changes.update) ||
                (kind == Kind::PeripheralBattery && changes.peripheralBattery);
            if (!changed) continue;
            card->SetSnapshot(snapshot);
        }
        if (changes.config)
        {
            config = snapshot.config;
            resident = !config.OnDemand();
            if (!resident && !startupWarmup) CancelWarmup();
            UpdateIdleTimer();
            UpdateEnvironment(true);
        }
        else if (changes.theme) UpdateEnvironment(true);
        if (visible && (changes.config || changes.update || peripheralLayout))
        {
            if (!device) { std::wstring error; device = graphics.CreateGraphicsDevice(compositor, error); }
            if (device) CreateCards();
        }
        if (visible && (changes.config || changes.update || changes.weather || changes.audio || peripheralLayout))
            PlaceGroup(false, true, peripheralLayout || !groupReflowFrom.empty());
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
        ScheduleWarmup();
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
                    warmupSnapshotReady = true;
                    changed = true;
                    accumulated.config |= changes.config;
                    accumulated.theme |= changes.theme;
                    accumulated.media |= changes.media;
                    accumulated.audio |= changes.audio;
                    accumulated.lyrics |= changes.lyrics;
                    accumulated.traffic |= changes.traffic;
                    accumulated.weather |= changes.weather;
                    accumulated.update |= changes.update;
                    accumulated.peripheralBattery |= changes.peripheralBattery;
                    accumulated.status |= changes.status;
                }
            }
            else if (message.kind == Protocol::Kind::Reply)
            {
                const auto reply = Protocol::ParseReply(message);
                if (std::erase(startupRequests, reply.requestId) != 0) ScheduleWarmup();
                for (auto iter = pendingSliders.begin(); iter != pendingSliders.end();)
                {
                    if (iter->second != reply.requestId) { ++iter; continue; }
                    if (auto* card = Card(iter->first == L"seek" ? Kind::Media : Kind::Device))
                        card->AcknowledgeSlider(iter->first);
                    iter = pendingSliders.erase(iter);
                }
                if (!reply.success)
                {
                    snapshot.statusText = reply.error;
                    lastError = reply.error; errorUntil = GetTickCount64() + 5000;
                    accumulated.status = true; changed = true; continue;
                }
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
                else if (reply.requestId.rfind(L"peripheral", 0) == 0)
                {
                    if (Protocol::ApplyPeripheralBatteryReply(snapshot.peripheralBattery, reply.payloadJson))
                    { accumulated.peripheralBattery = true; changed = true; }
                }
                else if (reply.requestId.rfind(L"media", 0) == 0)
                {
                    if (Protocol::ApplyMediaReply(snapshot.media, reply.payloadJson))
                    {
                        accumulated.media = true;
                        changed = true;
                    }
                }
                else if (reply.requestId.rfind(L"state", 0) == 0)
                {
                    Protocol::Changes stateChanges;
                    if (Protocol::ApplySnapshot(snapshot, reply.payloadJson, stateChanges))
                    {
                        warmupSnapshotReady = true;
                        accumulated.config |= stateChanges.config;
                        accumulated.theme |= stateChanges.theme;
                        accumulated.media |= stateChanges.media;
                        accumulated.audio |= stateChanges.audio;
                        accumulated.lyrics |= stateChanges.lyrics;
                        accumulated.traffic |= stateChanges.traffic;
                        accumulated.weather |= stateChanges.weather;
                        accumulated.update |= stateChanges.update;
                        accumulated.peripheralBattery |= stateChanges.peripheralBattery;
                        accumulated.status |= stateChanges.status;
                        changed = true;
                    }
                }
                else if (reply.requestId.rfind(L"update", 0) == 0)
                {
                    if (Protocol::ApplyUpdateReply(snapshot.update, reply.payloadJson))
                    {
                        accumulated.update = true;
                        changed = true;
                    }
                }
                UNREFERENCED_PARAMETER(colon);
            }
            else if (message.kind == Protocol::Kind::Event && message.action == L"connectionStarted")
            {
                Protocol::ResetRevisions(snapshot);
                pendingSliders.clear();
                if (visible) RequestInitialState();
                // History belongs to the native host and needs no player or
                // device discovery. Warm its cache once per connection so the
                // first click can use it instead of starting from an empty grid.
                else if (resident && !startupWarmup) client.Request(L"getTrafficHistory", {}, L"traffic");
            }
            else if (message.kind == Protocol::Kind::Event && message.action == L"shutdown")
            {
                HideAll(L"shutdown", false);
            }
        }
        if (errorUntil > GetTickCount64()) snapshot.statusText = lastError;
        if (changed) RefreshSnapshot(accumulated);
    }

    void CardHost::Impl::OnTask()
    {
        for (auto& command : TakeCommands())
        {
            switch (command->kind)
            {
            case COMMAND_RECOVER:
            {
                const bool restore = visible;
                HideAll(L"device-lost", false);
                popup.reset(); cards.clear(); device = nullptr;
                graphics.OnDeviceLost();
                if (restore) ShowCards();
                else ScheduleWarmup();
                break;
            }
            case COMMAND_TOGGLE:
                if (visible) HideAll(L"toggle", true);
                else { anchor = command->anchor; ShowCards(); }
                break;
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
                else ScheduleWarmup();
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
                    previewVivid = command->previewVivid;
                    previewTuning = command->previewTuning;
                    previewMaterial = command->previewMaterial;
                }
                if (command->setTheme)
                {
                    previewTheme = command->previewTheme;
                    previewDark = command->previewDark;
                }
                UpdateEnvironment(true);
                ScheduleWarmup();
                break;
            case COMMAND_OWNER:
                owner = command->owner;
                ScheduleWarmup();
                break;
            case COMMAND_CLEAR_PREVIEW:
                previewMica = false;
                previewTheme = false;
                UpdateEnvironment(true);
                ScheduleWarmup();
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
            PostQuitMessage(0);
        }
    }

    void CardHost::Impl::OnIdle()
    {
        if (window) KillTimer(window, IdleTimer);
        if (!visible) DestroyIdleCards();
    }

    void CardHost::Impl::InstallHooks()
    {
        if (diagnosticOffscreen) return;
        if (mouseHook || keyboardHook) return;
        Impl::activeHook = this;
        mouseHook = SetWindowsHookExW(WH_MOUSE_LL, [](int code, WPARAM keys, LPARAM data) -> LRESULT
        {
            if (code == HC_ACTION && (keys == WM_MOUSEWHEEL || keys == WM_MOUSEMOVE))
            {
                auto* self = Impl::activeHook.load();
                const auto* info = reinterpret_cast<MSLLHOOKSTRUCT*>(data);
                if (self && self->visible)
                {
                    const auto target = self->WheelTarget(info->pt);
                    if (keys == WM_MOUSEWHEEL || target != self->observedWheelTarget)
                    {
                        self->observedWheelTarget = target;
                        PostMessageW(self->window, WM_CARD_WHEEL,
                            keys == WM_MOUSEWHEEL ? info->mouseData : 0,
                            reinterpret_cast<LPARAM>(target));
                    }
                }
            }
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
                if (self && self->visible && info->vkCode == VK_ESCAPE &&
                    GetWindowThreadProcessId(GetForegroundWindow(), nullptr) != self->threadId)
                {
                    auto command = std::make_unique<Command>();
                    command->kind = COMMAND_ESCAPE;
                    self->Post(std::move(command));
                }
            }
            return CallNextHookEx(nullptr, code, keys, data);
        }, nullptr, 0);
    }

    void CardHost::Impl::UninstallHooks()
    {
        Impl::activeHook = nullptr;
        pageWheel.Reset();
        observedWheelTarget = nullptr;
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
            {
                // Clicking a card dismisses its flyout, without dismissing cards.
                // Selector clicks are handled below in OnRequest as a toggle.
                const POINT dip = Layout::ToDip(POINT{point.x - bounds.left, point.y - bounds.top}, card->DpiScale());
                const auto* hit = card->Regions().Hit(dip);
                if (!hit || !IsMenuExpanded(hit->action)) CloseMenu();
                return;
            }
        }
        // The taskbar entry owns its left-click toggle; dismissing here first
        // would make that same click reopen the group.
        RECT entry{};
        if (leftButton && IsWindow(owner) && GetWindowRect(owner, &entry) && PtInRect(&entry, point)) return;
        auto command = std::make_unique<Command>();
        command->kind = COMMAND_HIDE;
        command->reason = L"outside";
        Post(std::move(command));
    }

    // ---- services ------------------------------------------------------------

    void CardHost::Impl::OnRequest(const std::wstring& action, const std::wstring& jsonPayload)
    {
        // Card actions map onto the small set of card-channel requests. The request
        // `kind` tags the reply so it lands in the right state block.
        std::wstring request = action;
        std::wstring payload;
        const wchar_t* kind = L"misc";
        const auto quote = [](const std::wstring& value) { return Cards::Protocol::JsonString(value); };

        if (action == L"escape")
        {
            if (!EscapeCard()) HideAll(L"escape", true);
            return;
        }
        if (action == L"weatherView:today" || action == L"weatherView:forecast" ||
            action == L"weatherPage:next" || action == L"weatherPage:previous")
        {
            if (auto* card = Card(Kind::Weather))
            {
                const auto& weather = card->CurrentSnapshot().weather;
                bool forecast = weather.forecastView;
                int offset = weather.forecastOffset;
                if (action.starts_with(L"weatherView:")) { forecast = action == L"weatherView:forecast"; offset = 0; }
                else
                {
                    offset = weather.ClampOffset(weather.ClampOffset(offset) +
                        (action == L"weatherPage:next" ? weather.PageSize() : -weather.PageSize()));
                }
                card->SetWeatherView(forecast, offset);
            }
            return;
        }
        if (action == L"previous" || action == L"playPause" || action == L"next")
        {
            payload = L"{\"action\":" + quote(action) + L"}";
            request = L"mediaCommand";
            kind = L"media";
        }
        else if (action == L"seek")
        {
            // The slider reports a 0..1 ratio; Media needs seconds.
            const double ratio = std::clamp(_wtof(jsonPayload.c_str()), 0.0, 1.0);
            const double seconds = ratio * (std::max)(0.0, snapshot.media.duration);
            payload = L"{\"action\":\"seek\",\"positionSeconds\":" + std::to_wstring(seconds) + L"}";
            request = L"mediaCommand";
            kind = L"media";
        }
        else if (action == L"refreshPeripheralBatteries")
        {
            request = action;
            kind = L"peripheral";
        }
        else if (action == L"peripheralMode:compact" || action == L"peripheralMode:detailed")
        {
            request = L"setPeripheralBatteryMode";
            payload = L"{\"mode\":" + quote(action.substr(15)) + L"}";
            kind = L"peripheral";
        }
        else if (action == L"refreshWeather")
        {
            request = L"weatherCommand";
            payload = L"{\"operation\":\"refresh\"}";
            kind = L"weather";
        }
        else if (action == L"openSettings")
        {
            OnOpenSettings();
            return;
        }
        else if (action == L"openWeatherSettings")
        {
            HideAll(L"weather-settings", false);
            client.Request(L"openSettings", "{\"page\":\"hardware\"}");
            return;
        }
        else if (action == L"openWeatherProvider" || action == L"openWeatherAttribution")
        {
            const wchar_t* url = action == L"openWeatherProvider" ? L"https://www.qweather.com/"
                : L"https://developer.qweather.com/attribution.html";
            ShellExecuteW(window, L"open", url, nullptr, nullptr, SW_SHOWNORMAL);
            return;
        }
        else if (action == L"installUpdate" || action == L"downloadUpdate" ||
            action == L"cancelUpdateDownload")
        {
            request = action;
            kind = L"update";
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
            kind = L"audio";
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
            kind = L"audio";
        }
        else if (action.rfind(L"showDevices:", 0) == 0)
        {
            if (jsonPayload == L"collapse" || IsMenuExpanded(action)) { CloseMenu(); return; }
            ShowDeviceMenu(action.substr(12));
            return;
        }
        else if (action == L"selectDay" || action.starts_with(L"selectDay:"))
        {
            snapshot.traffic.Select(action.starts_with(L"selectDay:") ? action.substr(10) : jsonPayload);
            if (auto* card = Card(Kind::Traffic)) card->SetSnapshot(snapshot);
            return;
        }
        else if (action == L"showSources")
        {
            if (jsonPayload == L"collapse" || IsMenuExpanded(action)) { CloseMenu(); return; }
            ShowSourceMenu();
            return;
        }
        if (!request.empty())
        {
            const auto id = client.Request(request, Cards::WideToUtf8(payload), kind);
            if (action == L"seek" || action.rfind(L"setVolume:", 0) == 0)
                pendingSliders[action] = id;
        }
    }

    void CardHost::Impl::OnHideCards(const std::wstring& reason) { HideAll(reason, true); }

    static RECT MenuAnchor(const CardWindow& card, const std::wstring& action)
    {
        const auto bounds = card.Bounds();
        for (const auto& region : card.Regions().All())
            if (region.action == action)
                return RECT{ bounds.left + static_cast<LONG>(std::lround(region.bounds.left * card.DpiScale())),
                    bounds.top + static_cast<LONG>(std::lround(region.bounds.top * card.DpiScale())),
                    bounds.left + static_cast<LONG>(std::lround(region.bounds.right * card.DpiScale())),
                    bounds.top + static_cast<LONG>(std::lround(region.bounds.bottom * card.DpiScale())) };
        return bounds;
    }

    // The menu is drawn by the shared popup window; selection is turned back into
    // the same cardRequest a card widget would send.
    // A device list is built from the current audio snapshot, so the menu always
    // shows what the engine just reported.
    void CardHost::Impl::ShowDeviceMenu(const std::wstring& flow)
    {
        auto* card = Card(Kind::Device);
        if (!card || !card->Visible()) return;
        if (groupMoving) StopGroupMotion(true);
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
        OpenMenu(flow, items, MenuAnchor(*card, L"showDevices:" + flow));
    }

    void CardHost::Impl::ShowSourceMenu()
    {
        auto* card = Card(Kind::Media);
        if (!card || !card->Visible()) return;
        if (groupMoving) StopGroupMotion(true);
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
        OpenMenu(L"sources", items, MenuAnchor(*card, L"showSources"));
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
            client.Request(L"selectDevice", Cards::WideToUtf8(payload), L"audio");
            return;
        }
        if (action.rfind(L"selectSource:", 0) == 0)
        {
            const std::wstring id = action.substr(13);
            const std::wstring payload = L"{\"action\":\"selectSource\",\"sourceId\":" +
                Protocol::JsonString(id) + L"}";
            client.Request(L"mediaCommand", Cards::WideToUtf8(payload), L"media");
        }
    }

    void CardHost::Impl::OpenMenu(const std::wstring& menu, const std::vector<Layout::Region>& items,
        RECT anchor)
    {
        if (items.empty()) return;
        if (!popup) popup = std::make_unique<CardPopup>();
        CardEnvironment environment;
        environment.allowLiveCapture = !diagnosticOffscreen;
        environment.graphics = &graphics;
        environment.compositor = compositor;
        environment.device = device;
        environment.dark = previewTheme ? previewDark : snapshot.toolWindowTheme == 1 ? false : snapshot.toolWindowTheme == 2 ? true : dark;
        environment.highContrast = highContrast;
        environment.reducedMotion = reducedMotion;
        environment.mica = previewMica ? previewTuning : config.mica;
        environment.material = previewMica ? previewMaterial : config.material;
        if (previewMica) environment.mica.vivid = previewVivid;
        environment.fontFamily = config.font.empty() ? L"Microsoft YaHei UI" : config.font;
        if (!popup->Create(instance, environment))
        {
            popup.reset();
            return;
        }
        popup->SetWheelHandler([this](POINT point, int delta) { return OnWheel(point, delta); });
        popup->SetActivation([this]
        {
            auto command = std::make_unique<Command>();
            command->kind = COMMAND_MENU_ACTIVATE;
            Post(std::move(command));
        });
        popupMenu = menu;
        popupAnchor = anchor;
        popup->Show(menu, items, anchor, diagnosticOffscreen);
        ConfigureBackdrop();
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

    void CardHost::Impl::CloseMenu(bool animate)
    {
        if (popup) popup->Hide(animate);
        popupMenu.clear();
        ConfigureBackdrop();
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
        return false;
    }

    void CardHost::Impl::OnOpenSettings()
    {
        HideAll(L"settings", false);
        // Media owns the settings window; the card asks it to start WinUI on demand.
        client.Request(L"openSettings");
    }

    void CardHost::Impl::OnLayoutInvalidated()
    {
        if (!visible) { ScheduleWarmup(); return; }
        // WM_DPICHANGED can arrive synchronously inside DeferWindowPos. Finish
        // that transaction before applying a new group layout and cached bounds.
        auto command = std::make_unique<Command>();
        command->kind = COMMAND_PLACE;
        Post(std::move(command));
    }

    void CardHost::Impl::Run(std::promise<bool> ready)
    {
        threadId = GetCurrentThreadId();
        // The card thread owns its own STA apartment. RoInitialize is idempotent
        // per apartment and this thread never initialized MFC.
        const HRESULT apartment = RoInitialize(RO_INIT_SINGLETHREADED);
        if (FAILED(apartment)) { ready.set_value(false); return; }
        // Destroy Runtime (including its dispatcher) before uninitializing COM.
        struct Apartment { ~Apartment() { RoUninitialize(); } } apartmentLifetime;
        Runtime runtime;
        if (FAILED(runtime.Initialize())) { ready.set_value(false); return; }

        DetectEnvironment();
        const bool graphicsOk = graphics.Initialize();
        if (!graphicsOk) { graphics.Release(); ready.set_value(false); return; }
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
            case WM_CARD_WHEEL:
                self->RouteWheel(reinterpret_cast<HWND>(position), GET_WHEEL_DELTA_WPARAM(keys));
                return 0;
            case WM_CARD_TASK:
                self->OnTask();
                return 0;
            case WM_SETTINGCHANGE:
            case WM_THEMECHANGED:
                self->DetectEnvironment();
                self->UpdateEnvironment(true);
                if (self->visible) self->PlaceGroup(false, false);
                else self->ScheduleWarmup();
                return 0;
            case WM_DISPLAYCHANGE:
                self->StopBackdrop(true);
                if (self->visible) self->PlaceGroup(false, false);
                else self->ScheduleWarmup();
                return 0;
            case WM_TIMER:
                if (keys == IdleTimer)
                {
                    self->OnIdle();
                }
                else if (keys == WarmupTimer) self->WarmHiddenCards();
                return 0;
            default:
                return DefWindowProcW(window, message, keys, position);
            }
        };
        definition.hInstance = instance;
        definition.lpszClassName = L"TaskBarWidgetsCardHost";
        RegisterClassExW(&definition);

        window = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, definition.lpszClassName, L"", WS_POPUP, 0, 0, 0, 0, nullptr,
            nullptr, instance, nullptr);
        if (!window)
        {
            graphics.Release();
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
        if (!device)
        {
            compositor = nullptr; graphics.Release(); DestroyWindow(window); window = nullptr;
            ready.set_value(false); return;
        }
        ready.set_value(true);

        if (!diagnosticOffscreen)
        {
            client.Start(window);
            SendSnapshotRequest();
        }

        MSG message{};
        for (;;)
        {
            HANDLE timers[2]{};
            DWORD count = 0, groupIndex = MAXIMUM_WAIT_OBJECTS, backdropIndex = MAXIMUM_WAIT_OBJECTS;
            if (groupMoving && groupTimer) { groupIndex = count; timers[count++] = groupTimer; }
            if (backdropTimerArmed && backdropTimer) { backdropIndex = count; timers[count++] = backdropTimer; }
            const DWORD result = MsgWaitForMultipleObjectsEx(count, timers,
                INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            if (result == WAIT_FAILED) break;
            // Bound message draining so both timers progress under input, while
            // servicing input first even when a timer is continuously ready.
            bool quit = false;
            for (int dispatched = 0; dispatched < 16 && PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE); ++dispatched)
            {
                if (message.message == WM_QUIT) { quit = true; break; }
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }
            if (quit) break;
            if (groupMoving && (result == WAIT_OBJECT_0 + groupIndex ||
                WaitForSingleObject(groupTimer, 0) == WAIT_OBJECT_0)) GroupFrame();
            if (backdropTimerArmed && (result == WAIT_OBJECT_0 + backdropIndex ||
                WaitForSingleObject(backdropTimer, 0) == WAIT_OBJECT_0)) BackdropTick();
        }

        // Teardown: stop producers first, then release device-bound resources.
        StopBackdrop(true);
        if (backdropTimer) { CloseHandle(backdropTimer); backdropTimer = nullptr; }
        CancelWarmup();
        StopGroupMotion(false);
        if (groupTimer) { CloseHandle(groupTimer); groupTimer = nullptr; }
        client.Stop();
        UninstallHooks();
        popup.reset();
        for (auto& [kind, card] : cards)
        {
            UNREFERENCED_PARAMETER(kind);
            card->Destroy();
        }
        cards.clear();
        device = nullptr;
        graphics.Release();
        compositor = nullptr;
        if (window)
        {
            SetWindowLongPtrW(window, GWLP_USERDATA, 0);
            DestroyWindow(window);
            window = nullptr;
        }
    }

    // ---- CardHost facade ----------------------------------------------------

    CardHost::CardHost() : impl(std::make_shared<Impl>()) {}
    CardHost::~CardHost() { Stop(); }

    bool CardHost::Start(HWND owner, HINSTANCE instance, bool diagnosticOffscreen)
    {
        impl->diagnosticOffscreen = diagnosticOffscreen;
        return impl->Start(owner, instance);
    }

    void CardHost::Stop() { impl->Stop(); }

    void CardHost::Toggle(POINT anchor)
    {
        auto command = std::make_unique<Command>();
        command->kind = COMMAND_TOGGLE;
        command->anchor = anchor;
        impl->Post(std::move(command));
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

    BackdropDiagnostics CardHost::BackdropStatus() const
    {
        std::lock_guard lock(impl->backdropMutex);
        return impl->backdropDiagnostics;
    }

    void CardHost::ApplyConfig(const Config& config)
    {
        auto command = std::make_unique<Command>();
        command->kind = COMMAND_CONFIG;
        command->config = config;
        impl->Post(std::move(command));
    }

    void CardHost::PreviewMica(bool vivid, const MicaTuning& tuning, const MaterialSettings& material)
    {
        auto command = std::make_unique<Command>();
        command->kind = COMMAND_PREVIEW;
        command->previewMica = true;
        command->previewTuning = tuning;
        command->previewMaterial = material;
        command->previewVivid = vivid;
        impl->Post(std::move(command));
    }

    void CardHost::PreviewTheme(bool darkMode, bool enabled)
    {
        auto command = std::make_unique<Command>();
        command->kind = COMMAND_PREVIEW;
        command->setTheme = true;
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

    void CardHost::SetOwner(HWND owner)
    {
        auto command = std::make_unique<Command>();
        command->kind = COMMAND_OWNER;
        command->owner = owner;
        impl->Post(std::move(command));
    }

    bool CardsAvailable() { return true; }
}
