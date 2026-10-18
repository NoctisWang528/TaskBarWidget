#include "stdafx.h"
#include "NativeMediaHost.h"
#include "MediaDisplayState.h"
#include "MediaNavigationPolicy.h"
#include "ManagedRuntimeEnvironment.h"
#include "TrafficMonitor.h"
#include "NativeSettingsBridge.h"
#include "NativeWeatherBridge.h"
#include "PluginSettingsBridge.h"
#include <WebView2.h>
#include <dcomp.h>
#include "CompositionFade.h"
#pragma comment(lib, "dcomp.lib")
#include <wrl.h>
#include <winrt/Windows.Data.Json.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <atomic>
#include <mutex>
#include <thread>
#include <deque>
#include <filesystem>
#include <chrono>
#include <utility>

using Microsoft::WRL::Callback;
using Microsoft::WRL::ComPtr;
using namespace winrt::Windows::Data::Json;

namespace
{
    constexpr size_t MaximumMessage = 4 * 1024 * 1024;
    std::string Utf8(const std::wstring& value)
    {
        return winrt::to_string(value);
    }
    JsonObject Command(const wchar_t* action)
    {
        JsonObject result;
        result.SetNamedValue(L"version", JsonValue::CreateNumberValue(1));
        result.SetNamedValue(L"type", JsonValue::CreateStringValue(L"command"));
        result.SetNamedValue(L"action", JsonValue::CreateStringValue(action));
        return result;
    }

}

struct NativeMediaHost::Impl : std::enable_shared_from_this<NativeMediaHost::Impl>
{
    HWND owner{};
    std::atomic<bool> stop{};
    std::atomic<bool> reconnect{};
    std::thread worker;
    HANDLE helper{};
    std::atomic<DWORD> helperPid{};
    std::mutex mutex;
    std::deque<std::string> incoming, outgoing;
    size_t incomingBytes{};
    MediaDisplayState state;
    MediaNavigationPolicy navigationPolicy;
    ComPtr<ICoreWebView2Controller> controller;
    ComPtr<ICoreWebView2CompositionController> compositionController;
    ComPtr<IDCompositionDevice> compositionDevice;
    ComPtr<IDCompositionTarget> compositionTarget;
    ComPtr<IDCompositionVisual> mediaVisual;
    HWND compositionOwner{};
    CompositionFade mediaFade;
    std::wstring lastAppearance;
    ComPtr<ICoreWebView2> web;
    std::wstring html;
    std::vector<std::wstring> pendingScripts;
    size_t pendingScriptBytes{};
    bool documentReady{};
    ULONGLONG navigationStarted{};
    bool visible{};
    bool browserFrame{}, pointerDown{}, pointerBrowser{};
    POINT pointerOrigin{};
    unsigned long long gestureSerial{}, pendingBrowserGesture{};
    ULONGLONG browserGestureStarted{};
    std::wstring pointerSource;
    unsigned clickLogs{};
    bool lastAnnounced{};
    bool announcePending{};
    bool creating{};
    ULONGLONG nextWebRetry{};
    std::wstring diagnostic;
    ULONGLONG lastFailureLog{};
    std::mutex logMutex;
    std::filesystem::path appDirectory;

    explicit Impl(HWND hwnd) : owner(hwnd)
    {
        wchar_t path[32768]{};
        GetModuleFileNameW(nullptr, path, _countof(path));
        appDirectory = std::filesystem::path(path).parent_path();
    }
    void Log(const std::wstring& message, HRESULT result = S_OK)
    {
        std::lock_guard lock(logMutex);
        const std::wstring path=theApp.m_appdata_dir+L"native-media.log";
        WIN32_FILE_ATTRIBUTE_DATA attributes{};
        if(GetFileAttributesExW(path.c_str(),GetFileExInfoStandard,&attributes) && (attributes.nFileSizeHigh || attributes.nFileSizeLow>512*1024))
            MoveFileExW(path.c_str(),(path+L".1").c_str(),MOVEFILE_REPLACE_EXISTING);
        SYSTEMTIME time{}; GetLocalTime(&time); wchar_t prefix[96];
        swprintf_s(prefix,L"%04u-%02u-%02u %02u:%02u:%02u HR=0x%08X ",time.wYear,time.wMonth,time.wDay,time.wHour,time.wMinute,time.wSecond,static_cast<unsigned>(result));
        const auto boundedMessage = message.size() <= 1024 ? message : message.substr(0, 1024) + L" [truncated]";
        const auto line=Utf8(prefix+boundedMessage+L"\r\n");
        HANDLE file=CreateFileW(path.c_str(),FILE_APPEND_DATA,FILE_SHARE_READ|FILE_SHARE_WRITE,nullptr,OPEN_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr);
        if(file!=INVALID_HANDLE_VALUE) {DWORD written{}; WriteFile(file,line.data(),static_cast<DWORD>(line.size()),&written,nullptr); CloseHandle(file);}
    }
    void Failure(const std::wstring& message, HRESULT result)
    { bool changed=diagnostic!=message;diagnostic=message;if(changed || GetTickCount64()-lastFailureLog>=5000){lastFailureLog=GetTickCount64();Log(message,result);} }
    void NativeSettings(const JsonObject& request)
    {
        auto reply=Command(L"nativeSettings"); reply.SetNamedValue(L"requestId",JsonValue::CreateStringValue(request.GetNamedString(L"requestId",L"")));
        try
        {
            const auto now=std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
            const auto expires=request.GetNamedNumber(L"expiresAt",0);
            if(!NativeSettingsValidation::Unexpired(expires,now)) throw std::invalid_argument("Native settings request expired; retry the operation.");
            NativeSettingsBridge bridge;
            if(request.GetNamedString(L"action")==L"applyNativeSettings") bridge.Apply(request.GetNamedObject(L"payload"));
            reply.SetNamedValue(L"payload",NativeSettingsBridge().Describe());
            reply.SetNamedValue(L"success",JsonValue::CreateBooleanValue(true)); reply.SetNamedValue(L"error",JsonValue::CreateStringValue(L""));
            Log(L"Native settings read/apply succeeded.");
        }
        catch(const std::exception& error)
        { reply.SetNamedValue(L"success",JsonValue::CreateBooleanValue(false)); reply.SetNamedValue(L"error",JsonValue::CreateStringValue(winrt::to_hstring(error.what()))); Log(L"Native settings rejected: "+std::wstring(winrt::to_hstring(error.what()))); }
        catch(const winrt::hresult_error& error)
        { reply.SetNamedValue(L"success",JsonValue::CreateBooleanValue(false)); reply.SetNamedValue(L"error",JsonValue::CreateStringValue(error.message())); Log(L"Native settings failed.",error.code()); }
        if(!reply.GetNamedBoolean(L"success",false))
        {try {reply.SetNamedValue(L"payload",NativeSettingsBridge().Describe());} catch(...) {Log(L"Could not read native settings after failure.",E_FAIL);}}
        Send(reply);
    }
    void WeatherCommand(const JsonObject& request)
    {
        auto reply = Command(L"weatherResult");
        reply.SetNamedValue(L"requestId", JsonValue::CreateStringValue(request.GetNamedString(L"requestId", L"")));
        try {
            const auto now = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
            if (!NativeSettingsValidation::Unexpired(request.GetNamedNumber(L"expiresAt", 0), now)) throw std::invalid_argument("Weather request expired; retry.");
            reply.SetNamedValue(L"payload", NativeWeatherBridge::Handle(request.GetNamedObject(L"payload")));
            reply.SetNamedValue(L"success", JsonValue::CreateBooleanValue(true));
        }
        catch (const std::exception& error) {
            reply.SetNamedValue(L"success", JsonValue::CreateBooleanValue(false));
            reply.SetNamedValue(L"error", JsonValue::CreateStringValue(winrt::to_hstring(error.what())));
        }
        catch (...) {
            reply.SetNamedValue(L"success", JsonValue::CreateBooleanValue(false));
            reply.SetNamedValue(L"error", JsonValue::CreateStringValue(L"Weather request could not be processed."));
        }
        Send(reply);
    }
    void TrafficHistory(const JsonObject& request)
    {
        auto reply = Command(L"trafficHistory");
        reply.SetNamedValue(L"requestId", JsonValue::CreateStringValue(request.GetNamedString(L"requestId", L"")));
        try
        {
            const auto now = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
            if (!NativeSettingsValidation::Unexpired(request.GetNamedNumber(L"expiresAt", 0), now))
                throw std::invalid_argument("Traffic history request expired; retry the operation.");
            const auto* monitor = CTrafficMonitorDlg::Instance();
            if (!monitor) throw std::runtime_error("Traffic monitor is unavailable.");
            SYSTEMTIME local{};
            GetLocalTime(&local);
            const auto today = std::chrono::year{ local.wYear } / local.wMonth / local.wDay;
            const auto first = TrafficHistorySnapshot::FirstDay(std::chrono::sys_days{ today });
            const auto format = [](std::chrono::year_month_day date)
            {
                wchar_t text[16]{};
                swprintf_s(text, L"%04d-%02u-%02u", static_cast<int>(date.year()), static_cast<unsigned>(date.month()), static_cast<unsigned>(date.day()));
                return JsonValue::CreateStringValue(text);
            };
            JsonArray days;
            for (const auto& day : monitor->GetTrafficHistorySnapshot())
            {
                if (day.date < first || day.date > std::chrono::sys_days{ today }) continue;
                JsonObject item;
                item.SetNamedValue(L"date", format(std::chrono::year_month_day{ day.date }));
                item.SetNamedValue(L"totalKiB", JsonValue::CreateNumberValue(static_cast<double>(day.totalKiB)));
                days.Append(item);
            }
            JsonObject payload;
            payload.SetNamedValue(L"today", format(today));
            payload.SetNamedValue(L"days", days);
            const bool chinese = theApp.m_str_table.IsSimplifiedChinese() || theApp.m_str_table.GetLanguageInfo().bcp_47 == L"zh-TW";
            payload.SetNamedValue(L"scopeLabel", JsonValue::CreateStringValue(chinese
                ? L"\u5df2\u8bb0\u5f55\u7684\u76d1\u6d4b\u7f51\u5361\uff08\u4e0a\u4f20 + \u4e0b\u8f7d\uff09"
                : L"Recorded monitored adapters (upload + download)"));
            reply.SetNamedValue(L"payload", payload);
            reply.SetNamedValue(L"success", JsonValue::CreateBooleanValue(true));
            reply.SetNamedValue(L"error", JsonValue::CreateStringValue(L""));
        }
        catch (const std::exception& error)
        {
            reply.SetNamedValue(L"success", JsonValue::CreateBooleanValue(false));
            reply.SetNamedValue(L"error", JsonValue::CreateStringValue(winrt::to_hstring(error.what())));
        }
        catch (const winrt::hresult_error& error)
        {
            reply.SetNamedValue(L"success", JsonValue::CreateBooleanValue(false));
            reply.SetNamedValue(L"error", JsonValue::CreateStringValue(error.message()));
        }
        Send(reply);
    }

    ~Impl()
    {
        stop = true;
        if (worker.joinable()) worker.join();
        if (controller) controller->Close();
        if (helper)
        {
            // This handle belongs exclusively to the helper launched by this product.
            if (WaitForSingleObject(helper, 1500) == WAIT_TIMEOUT)
                TerminateProcess(helper, 0);
            CloseHandle(helper);
        }
    }
    void Send(const JsonObject& message)
    {
        const auto action = message.GetNamedString(L"action", L"");
        if (action == L"togglePanel" || action == L"openSettings")
        {
            const DWORD pid = helperPid;
            if (pid) AllowSetForegroundWindow(pid);
        }
        std::lock_guard lock(mutex);
        if (outgoing.size() < 64) outgoing.push_back(Utf8(message.Stringify().c_str()) + "\n");
        else Log(L"Command queue full: " + std::wstring(action));
    }
    void LaunchHelper()
    {
        if (helper)
        {
            if (WaitForSingleObject(helper, 0) == WAIT_TIMEOUT) return;
            CloseHandle(helper);
            helper = nullptr;
            helperPid = 0;
        }
        std::wstring executable = (appDirectory / L"Media" / L"TaskBarWidgets.Media.exe").wstring();
        std::wstring command = L"\"" + executable + L"\" --host-pid " + std::to_wstring(GetCurrentProcessId())
            + L" --pipe-name TaskBarWidgets." + std::to_wstring(GetCurrentProcessId())
            + L" --portable-data " + (theApp.m_general_data.portable_mode ? L"1" : L"0");
        STARTUPINFOW startup{ sizeof(startup) };
        startup.dwFlags = STARTF_USESHOWWINDOW;
        startup.wShowWindow = SW_HIDE;
        PROCESS_INFORMATION process{};
        const auto runtime = SystemDotnetRoot();
        if (runtime.empty()) { Log(L"System .NET Desktop Runtime x64 is missing.", HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)); return; }
        auto environment = ManagedEnvironment(runtime);
        if (environment.empty()) { Log(L"Cannot prepare the managed process environment.", E_FAIL); return; }
        if (CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT,
            environment.data(), appDirectory.c_str(), &startup, &process))
        {
            helper = process.hProcess;
            helperPid = process.dwProcessId;
            CloseHandle(process.hThread);
            Log(L"Media helper launched: PID "+std::to_wstring(helperPid.load()));
        }
        else Log(L"Media helper launch failed.",HRESULT_FROM_WIN32(GetLastError()));
    }
    // All pipe I/O runs off the UI thread. Overlapped writes have a bounded deadline.
    bool Transfer(HANDLE pipe, void* buffer, DWORD length, bool write, DWORD& transferred)
    {
        OVERLAPPED operation{};
        operation.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        BOOL ok = write ? WriteFile(pipe, buffer, length, &transferred, &operation)
                        : ReadFile(pipe, buffer, length, &transferred, &operation);
        if (!ok && GetLastError() == ERROR_IO_PENDING)
        {
            DWORD wait = WaitForSingleObject(operation.hEvent, 500);
            if (wait != WAIT_OBJECT_0)
            {
                CancelIoEx(pipe, &operation);
                GetOverlappedResult(pipe, &operation, &transferred, TRUE);
                ok = FALSE;
            }
            else ok = GetOverlappedResult(pipe, &operation, &transferred, FALSE);
        }
        CloseHandle(operation.hEvent);
        return ok != FALSE;
    }
    void Run()
    {
        std::wstring name = L"\\\\.\\pipe\\TaskBarWidgets." + std::to_wstring(GetCurrentProcessId());
        ULONGLONG nextLaunch{};
        while (!stop)
        {
            if (GetTickCount64() >= nextLaunch)
            {
                LaunchHelper();
                nextLaunch = GetTickCount64() + 5000;
            }
            HANDLE pipe = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                OPEN_EXISTING, FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
            if (pipe == INVALID_HANDLE_VALUE)
            {
                for (int i = 0; i < 10 && !stop; ++i) Sleep(50);
                continue;
            }
            ULONG serverPid{};
            if (!GetNamedPipeServerProcessId(pipe, &serverPid) || serverPid != helperPid.load())
            {
                CloseHandle(pipe);
                Sleep(100);
                continue;
            }
            std::string partial;
            Log(L"Media helper pipe connected.");
            bool alive = true;
            reconnect = false;
            while (!stop && alive && !reconnect)
            {
                std::deque<std::string> writes;
                { std::lock_guard lock(mutex); writes.swap(outgoing); }
                for (auto& message : writes)
                {
                    size_t offset{};
                    while (offset < message.size())
                    {
                        DWORD sent{};
                        if (!Transfer(pipe, message.data() + offset, static_cast<DWORD>(message.size() - offset), true, sent) || sent == 0)
                        { alive = false; break; }
                        offset += sent;
                    }
                    if (!alive) break;
                }
                DWORD available{};
                if (!alive || !PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr)) break;
                if (available > 0)
                {
                    char buffer[65536];
                    DWORD received{};
                    if (!Transfer(pipe, buffer, (std::min)(available, static_cast<DWORD>(sizeof buffer)), false, received) || received == 0) break;
                    partial.append(buffer, received);
                    size_t line;
                    while ((line = partial.find('\n')) != std::string::npos)
                    {
                        if (line > MaximumMessage) { alive = false; break; }
                        std::string message = partial.substr(0, line);
                        partial.erase(0, line + 1);
                        std::lock_guard lock(mutex);
                        if (incoming.size() >= 128 || incomingBytes + message.size() > 8 * 1024 * 1024) { alive = false; break; }
                        incomingBytes += message.size();
                        incoming.push_back(std::move(message));
                    }
                    if (partial.size() > MaximumMessage) break;
                }
                else Sleep(20);
            }
            if (stop)
            {
                char shutdown[] = "{\"version\":1,\"type\":\"command\",\"action\":\"shutdown\"}\n";
                DWORD sent{};
                Transfer(pipe, shutdown, sizeof shutdown - 1, true, sent);
            }
            CancelIoEx(pipe, nullptr);
            CloseHandle(pipe);
            { std::lock_guard lock(mutex); incoming.clear(); incomingBytes=0; incoming.push_back("{\"version\":1,\"type\":\"disconnected\"}"); outgoing.clear(); }
        }
    }
    void ExecutePending()
    {
        if (!documentReady || !web) return;
        // Preserve order: setLyrics and its timing updates are a single renderer stream.
        std::wstring script;
        for (const auto& entry : pendingScripts) script += entry + L";\n";
        pendingScripts.clear();
        pendingScriptBytes=0;
        if (!script.empty())
        {
            auto weak=weak_from_this();
            HRESULT result=web->ExecuteScript(script.c_str(),Callback<ICoreWebView2ExecuteScriptCompletedHandler>([weak](HRESULT error,LPCWSTR) -> HRESULT
            {if(auto self=weak.lock();self && FAILED(error)) self->Failure(L"Media frame script failed.",error); return S_OK;}).Get());
            if(FAILED(result)) Failure(L"Media frame script could not be queued.",result);
        }
    }
    HRESULT BindComposition()
    {
        HRESULT result = S_OK;
        if (!compositionDevice)
            result = DCompositionCreateDevice(nullptr, IID_PPV_ARGS(&compositionDevice));
        if (SUCCEEDED(result) && !mediaVisual) result = compositionDevice->CreateVisual(&mediaVisual);
        if (SUCCEEDED(result) && compositionOwner != owner)
        {
            compositionTarget.Reset();
            result = compositionDevice->CreateTargetForHwnd(owner, TRUE, &compositionTarget);
            if (SUCCEEDED(result)) compositionOwner = owner;
        }
        if (SUCCEEDED(result)) result = compositionTarget->SetRoot(mediaVisual.Get());
        if (SUCCEEDED(result)) result = compositionController->put_RootVisualTarget(mediaVisual.Get());
        if (SUCCEEDED(result)) result = mediaFade.Set(compositionDevice.Get(),mediaVisual.Get(),false,false);
        if (SUCCEEDED(result)) result = compositionDevice->Commit();
        return result;
    }
    void ApplyAppearance()
    {
        if (!documentReady || !web) return;
        const auto& style = theApp.m_taskbar_data;
        JsonObject payload;
        payload.SetNamedValue(L"fontFamily", JsonValue::CreateStringValue(style.font.name.GetString()));
        payload.SetNamedValue(L"fontSize", JsonValue::CreateNumberValue(style.font.size * 96.0 / 72.0));
        payload.SetNamedValue(L"fontWeight", JsonValue::CreateNumberValue(style.font.bold ? 700 : 400));
        payload.SetNamedValue(L"fontItalic", JsonValue::CreateBooleanValue(style.font.italic));
        payload.SetNamedValue(L"fontUnderline", JsonValue::CreateBooleanValue(style.font.underline));
        payload.SetNamedValue(L"fontStrikeOut", JsonValue::CreateBooleanValue(style.font.strike_out));
        const double alpha = style.IsTaskbarTransparent() ? 0.0 : style.backgroundOpacity / 100.0;
        const std::wstring color = L"rgba(" + std::to_wstring(GetRValue(style.back_color)) + L"," + std::to_wstring(GetGValue(style.back_color)) + L"," + std::to_wstring(GetBValue(style.back_color)) + L"," + std::to_wstring(alpha) + L")";
        payload.SetNamedValue(L"surfaceColor", JsonValue::CreateStringValue(color));
        const std::wstring value = payload.Stringify().c_str();
        if (lastAppearance == value) return;
        lastAppearance = value;
        auto appearance = Command(L"taskbarAppearance");
        appearance.SetNamedValue(L"payload",payload);
        Send(appearance);
        const auto script = L"window.taskbarLyrics?.receive({version:1,type:'nativeAppearance',payload:" + value + L"});";
        web->ExecuteScript(script.c_str(), nullptr);
    }
    void CreateWebView()
    {
        if (creating || controller || html.empty() || GetTickCount64() < nextWebRetry) return;
        creating = true;
        nextWebRetry = GetTickCount64() + 10000;
        auto weak = weak_from_this();
        std::wstring data = theApp.m_appdata_dir + L"WebView2";
        HRESULT result = CreateCoreWebView2EnvironmentWithOptions(nullptr, data.c_str(), nullptr,
            Callback<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>([weak](HRESULT error, ICoreWebView2Environment* environment) -> HRESULT
            {
                auto self = weak.lock();
                if (!self || self->stop) return S_OK;
                if (FAILED(error) || !environment) { self->creating = false; self->Failure(L"WebView2 Runtime unavailable; monitoring remains active.",error); return S_OK; }
                self->Log(L"WebView2 environment created.");
                ComPtr<ICoreWebView2Environment3> compositionEnvironment;
                HRESULT result = environment->QueryInterface(IID_PPV_ARGS(&compositionEnvironment));
                if (FAILED(result)) { self->creating=false; self->Failure(L"WebView2 composition unavailable.",result); return S_OK; }
                result=compositionEnvironment->CreateCoreWebView2CompositionController(self->owner,
                    Callback<ICoreWebView2CreateCoreWebView2CompositionControllerCompletedHandler>([weak](HRESULT error, ICoreWebView2CompositionController* composition) -> HRESULT
                    {
                        ComPtr<ICoreWebView2Controller> controller;
                        if (composition) composition->QueryInterface(IID_PPV_ARGS(&controller));
                        auto self = weak.lock();
                        if (!self || self->stop) { if (controller) controller->Close(); return S_OK; }
                        self->creating = false;
                        if (FAILED(error) || !controller) { self->Failure(L"Could not create taskbar media view.",error); return S_OK; }
                        self->Log(L"Taskbar WebView2 controller created.");
                        self->controller = controller;
                        self->compositionController = composition;
                        controller->put_ParentWindow(self->owner);
                        const auto bindResult = self->BindComposition();
                        if (FAILED(bindResult)) {self->Failure(L"Could not attach taskbar media composition.",bindResult);controller->Close();self->controller.Reset();self->compositionTarget.Reset();self->mediaVisual.Reset();self->compositionDevice.Reset();self->compositionOwner=nullptr;return S_OK;}
                        HRESULT coreResult=controller->get_CoreWebView2(&self->web);
                        if(FAILED(coreResult) || !self->web) {self->Failure(L"Could not get taskbar media WebView.",coreResult); controller->Close();self->controller.Reset();return S_OK;}
                        ComPtr<ICoreWebView2Settings> settings;
                        self->web->get_Settings(&settings);
                        ComPtr<ICoreWebView2_3> web3;
                        if (SUCCEEDED(self->web.As(&web3)))
                            web3->SetVirtualHostNameToFolderMapping(L"taskbarwidgets.local", (self->appDirectory / L"Media").c_str(), COREWEBVIEW2_HOST_RESOURCE_ACCESS_KIND_ALLOW);
                        settings->put_AreDefaultContextMenusEnabled(FALSE);
                        settings->put_AreDevToolsEnabled(FALSE);
                        settings->put_AreDefaultScriptDialogsEnabled(FALSE);
                        settings->put_IsStatusBarEnabled(FALSE);
                        ComPtr<ICoreWebView2Controller2> controller2;
                        if (SUCCEEDED(controller->QueryInterface(IID_PPV_ARGS(&controller2))))
                            controller2->put_DefaultBackgroundColor(COREWEBVIEW2_COLOR{0,0,0,0});
                        EventRegistrationToken token{};
                        self->web->add_NavigationStarting(Callback<ICoreWebView2NavigationStartingEventHandler>([weak](ICoreWebView2*, ICoreWebView2NavigationStartingEventArgs* args) -> HRESULT
                        {
                            LPWSTR uri{}; args->get_Uri(&uri);
                            BOOL userInitiated{}, redirected{};
                            args->get_IsUserInitiated(&userInitiated);
                            args->get_IsRedirected(&redirected);
                            if(auto self=weak.lock())
                            {
                                const std::wstring_view address = uri ? uri : L"";
                                const bool allowed = self->navigationPolicy.Allow(address, userInitiated != FALSE, redirected != FALSE);
                                args->put_Cancel(!allowed);
                                if(allowed) {self->documentReady=false;self->lastAppearance.clear();self->navigationStarted=GetTickCount64();}
                                const auto description = address.starts_with(L"data:")
                                    ? L"data: host document (characters=" + std::to_wstring(address.size()) + L")"
                                    : std::wstring(address.substr(0, 160));
                                self->Log(L"Media navigation " + std::wstring(allowed ? L"allowed: " : L"blocked: ") + description
                                    + L" userInitiated=" + std::to_wstring(userInitiated) + L" redirected=" + std::to_wstring(redirected));
                            }
                            else args->put_Cancel(TRUE);
                            CoTaskMemFree(uri); return S_OK;
                        }).Get(), &token);
                        self->web->add_NewWindowRequested(Callback<ICoreWebView2NewWindowRequestedEventHandler>([](ICoreWebView2*, ICoreWebView2NewWindowRequestedEventArgs* args) -> HRESULT
                        { args->put_Handled(TRUE); return S_OK; }).Get(), &token);
                        self->web->add_NavigationCompleted(Callback<ICoreWebView2NavigationCompletedEventHandler>([weak](ICoreWebView2*, ICoreWebView2NavigationCompletedEventArgs* args) -> HRESULT
                        {
                            auto self=weak.lock(); if (!self || self->stop) return S_OK;
                            BOOL success{}; args->get_IsSuccess(&success); COREWEBVIEW2_WEB_ERROR_STATUS status{};args->get_WebErrorStatus(&status);
                            self->Log(L"Media navigation completed: success="+std::to_wstring(success)+L" status="+std::to_wstring(status));
                            if(!success) {self->Failure(L"Media document navigation failed.",E_FAIL);return S_OK;}
                            HRESULT result=self->web->ExecuteScript(LR"JS((()=>{const ready=!!(window.taskbarLyrics&&typeof window.taskbarLyrics.receive==='function'&&window.taskbarMedia&&typeof window.taskbarMedia.receive==='function');chrome.webview.postMessage({action:'rendererReady',ready,detail:document.readyState+' '+innerWidth+'x'+innerHeight});return ready;})())JS",
                                Callback<ICoreWebView2ExecuteScriptCompletedHandler>([weak](HRESULT error,LPCWSTR result) -> HRESULT
                                {if(auto self=weak.lock()) {self->Log(L"Media renderer readiness result: "+std::wstring(result?result:L"(null)"),error);if(FAILED(error)) self->Failure(L"Media renderer readiness script failed.",error);}return S_OK;}).Get());
                            if(FAILED(result)) self->Failure(L"Media renderer readiness could not be queued.",result);
                            return S_OK;
                        }).Get(), &token);
                        self->web->add_ProcessFailed(Callback<ICoreWebView2ProcessFailedEventHandler>([weak](ICoreWebView2*, ICoreWebView2ProcessFailedEventArgs*) -> HRESULT
                        {
                            auto self=weak.lock(); if (!self || self->stop) return S_OK;
                            self->documentReady=false; self->state.Disconnect(); self->reconnect=true;
                            self->Failure(L"Media renderer failed; retrying.",E_FAIL);
                            if(self->controller) self->controller->Close();
                            self->controller.Reset(); self->web.Reset(); return S_OK;
                        }).Get(), &token);
                        self->web->add_WebMessageReceived(Callback<ICoreWebView2WebMessageReceivedEventHandler>([weak](ICoreWebView2*, ICoreWebView2WebMessageReceivedEventArgs* args) -> HRESULT
                        {
                            auto self=weak.lock(); if (!self || self->stop) return S_OK;
                            LPWSTR json{}; if (FAILED(args->get_WebMessageAsJson(&json))) return S_OK;
                            try
                            {
                                auto value=JsonValue::Parse(json);
                                auto message=value.ValueType()==JsonValueType::String ? JsonObject::Parse(value.GetString()) : value.GetObject();
                                auto action=message.GetNamedString(L"action", L"");
                                if(action==L"rendererReady")
                                {
                                    self->documentReady=message.GetNamedBoolean(L"ready",false);
                                    self->Log(L"Media renderer handshake: ready="+std::to_wstring(self->documentReady)+L" "+std::wstring(message.GetNamedString(L"detail",L"")));
                                    if(self->documentReady) {self->navigationStarted=0;self->diagnostic.clear();self->ExecutePending();}
                                    else self->Failure(L"Media renderer JavaScript API is unavailable.",E_FAIL);
                                }
                                else if(action==L"rendererError") self->Failure(L"Media renderer JavaScript error: "+std::wstring(message.GetNamedString(L"message",L"unknown")),E_FAIL);
                                // Native pointer gestures own panel toggles, including the cover.
                                else if(action==L"wheel") self->state.Wheel(static_cast<int>(message.GetNamedNumber(L"delta",0)));
                                else if(action==L"menu") self->ShowMenu();
                                else if(action==L"playPause" || action==L"previous" || action==L"next") self->Send(Command(action.c_str()));
                            }
                            catch (...) {self->Log(L"Invalid WebView message ignored.",E_INVALIDARG);}
                            CoTaskMemFree(json); return S_OK;
                        }).Get(), &token);
                        self->web->AddScriptToExecuteOnDocumentCreated(LR"JS(
                            (() => {
                              window.addEventListener('error',e=>chrome.webview.postMessage({action:'rendererError',message:String(e.message||e.error||'script error')}));
                              window.addEventListener('unhandledrejection',e=>chrome.webview.postMessage({action:'rendererError',message:String(e.reason||'promise rejected')}));
                              let wheelRemainder=0;
                              document.addEventListener('wheel', e => {
                                e.preventDefault();
                                wheelRemainder -= e.deltaY * (e.deltaMode===1 ? 40 : e.deltaMode===2 ? 120 : 1.2);
                                const delta=Math.trunc(wheelRemainder);
                                wheelRemainder-=delta;
                                if(delta)chrome.webview.postMessage({action:'wheel',delta});
                              }, {passive:false});
                              document.addEventListener('contextmenu', e => {e.preventDefault();chrome.webview.postMessage({action:'menu'});});
                            })();
                        )JS", nullptr);
                        self->navigationStarted=GetTickCount64();
                        self->navigationPolicy.ExpectHostDocument();
                        HRESULT navigateResult=self->web->NavigateToString(self->html.c_str());
                        if(FAILED(navigateResult)) self->Failure(L"Media HTML navigation could not be queued.",navigateResult);
                        controller->put_IsVisible(FALSE);
                        return S_OK;
                    }).Get());
                if(FAILED(result)) {self->creating=false;self->Failure(L"WebView2 controller creation could not be queued.",result);}
                return S_OK;
            }).Get());
        if (FAILED(result)) { creating = false; Failure(L"WebView2 initialization failed; retrying.",result); }
    }
    void ClickLog(const std::wstring& stage, unsigned long long id)
    {
        if (clickLogs++ < 192) Log(L"Panel gesture=" + std::to_wstring(id) + L" " + stage);
    }
    void FinishBrowserGesture(unsigned long long id, const std::wstring& action)
    {
        if (pendingBrowserGesture != id) return;
        pendingBrowserGesture = 0;
        ClickLog(L"browser-result " + action, id);
        if (action == L"disabled") return;
        if (pointerSource == state.source && (action == L"playPause" || action == L"previous" || action == L"next")) Send(Command(action.c_str()));
        else Panel(id);
    }
    void ActivatePointer(POINT point, unsigned long long id)
    {
        if (!pointerBrowser || !visible || !documentReady || !web || pointerSource != state.source) { Panel(id); return; }
        pendingBrowserGesture = id;
        browserGestureStarted = GetTickCount64();
        // Native coordinates are physical pixels; DOM hit testing uses CSS pixels.
        RECT bounds{}; GetClientRect(owner, &bounds);
        auto script = L"(()=>{const e=document.elementFromPoint(" + std::to_wstring(point.x) + L"*innerWidth/" + std::to_wstring((std::max)(1L,bounds.right)) +
            L"," + std::to_wstring(point.y) + L"*innerHeight/" + std::to_wstring((std::max)(1L,bounds.bottom)) +
            L");const b=e?.closest('[data-media-action]');return b?(b.disabled?'disabled':b.dataset.mediaAction):'panel';})()";
        auto weak = weak_from_this();
        const HRESULT result = web->ExecuteScript(script.c_str(), Callback<ICoreWebView2ExecuteScriptCompletedHandler>([weak,id](HRESULT result,LPCWSTR json)->HRESULT {
            if (auto self=weak.lock(); self && !self->stop) {
                std::wstring action=L"panel";
                if (SUCCEEDED(result) && json) try { action=JsonValue::Parse(json).GetString(); } catch (...) {}
                self->FinishBrowserGesture(id,action);
            }
            return S_OK;
        }).Get());
        if (FAILED(result)) FinishBrowserGesture(id,L"panel");
    }
    void Panel(unsigned long long gesture = 0)
    {
        ClickLog(L"send-toggle", gesture);
        RECT bounds{}; GetWindowRect(owner, &bounds);
        auto message=Command(L"togglePanel");
        message.SetNamedValue(L"x",JsonValue::CreateNumberValue((bounds.left+bounds.right)/2));
        message.SetNamedValue(L"y",JsonValue::CreateNumberValue(bounds.top));
        message.SetNamedValue(L"entryWindow",JsonValue::CreateNumberValue(static_cast<double>(reinterpret_cast<INT_PTR>(owner))));
        message.SetNamedValue(L"gestureId",JsonValue::CreateNumberValue(static_cast<double>(gesture)));
        Send(message);
    }
    void ShowMenu()
    {
        HMENU menu=CreatePopupMenu();
        const bool chinese=theApp.m_str_table.IsSimplifiedChinese() || theApp.m_str_table.GetLanguageInfo().bcp_47==L"zh-TW";
        AppendMenuW(menu,MF_STRING,1,chinese ? L"\u8bbe\u7f6e..." : L"Settings...");
        AppendMenuW(menu,MF_STRING | (state.manual==0 ? MF_CHECKED : 0),3,chinese ? L"\u81ea\u52a8\u5207\u6362" : L"Automatic display");
        AppendMenuW(menu,MF_STRING | (state.manual==1 ? MF_CHECKED : 0),4,chinese ? L"\u663e\u793a\u7cfb\u7edf\u76d1\u6d4b" : L"Show monitoring");
        AppendMenuW(menu,MF_STRING | (state.manual==2 ? MF_CHECKED : 0),5,chinese ? L"\u663e\u793a\u5a92\u4f53" : L"Show media");
        AppendMenuW(menu,MF_SEPARATOR,0,nullptr);
        AppendMenuW(menu,MF_STRING,7,chinese ? L"\u8bca\u65ad / \u91cd\u8bd5\u5a92\u4f53" : L"Diagnostics / retry media");
        AppendMenuW(menu,MF_STRING,8,chinese ? L"\u9000\u51fa" : L"Exit");
        POINT point{}; GetCursorPos(&point); SetForegroundWindow(owner);
        UINT action=TrackPopupMenu(menu,TPM_RETURNCMD|TPM_RIGHTBUTTON,point.x,point.y,0,owner,nullptr);
        DestroyMenu(menu);
        switch(action)
        {
        case 1: Send(Command(L"openSettings")); break;
        case 3: state.manual=0; break;
        case 4: state.manual=1; break;
        case 5: state.manual=2; break;
        case 7:
            nextWebRetry=0;
            documentReady=false; navigationStarted=0;
            if(controller) controller->Close(); controller.Reset(); web.Reset(); reconnect=true;
            if(!diagnostic.empty()) MessageBoxW(owner,diagnostic.c_str(),L"TaskBarWidgets",MB_OK|MB_ICONINFORMATION);
            else MessageBoxW(owner,L"Media helper connects locally. Renderer and helper retry automatically after failure.",L"TaskBarWidgets",MB_OK|MB_ICONINFORMATION);
            break;
        case 8: PostMessageW(theApp.m_pMainWnd->GetSafeHwnd(),WM_CLOSE,0,0); break;
        }
    }
    void Tick()
    {
        std::deque<std::string> messages;
        { std::lock_guard lock(mutex); messages.swap(incoming); incomingBytes=0; }
        for (const auto& entry:messages)
        {
            try
            {
                auto message=JsonObject::Parse(winrt::to_hstring(entry));
                if(message.GetNamedNumber(L"version",0)!=1) continue;
                auto type=message.GetNamedString(L"type",L"");
                if(type==L"initialize")
                {
                    html=message.GetNamedString(L"html",L"").c_str();
                    Log(L"Media helper initialized: HTML characters="+std::to_wstring(html.size()));
                    documentReady=false; pendingScripts.clear(); pendingScriptBytes=0; state.Disconnect();
                    if(web) {navigationStarted=GetTickCount64();navigationPolicy.ExpectHostDocument();HRESULT result=web->NavigateToString(html.c_str());if(FAILED(result)) Failure(L"Media reconnect navigation failed.",result);}
                    announcePending=true;
                }
                else if(type==L"frame")
                {
                    std::wstring source=message.GetNamedString(L"sourceId",L"").c_str();
                    bool playing=message.GetNamedBoolean(L"isPlaying",false),hasMedia=message.GetNamedBoolean(L"hasMedia",false);
                    bool changed=source!=state.source || playing!=state.playing || hasMedia!=state.hasMedia;
                    state.Frame(source,playing,hasMedia);
                    browserFrame=message.GetNamedBoolean(L"isBrowser",false);
                    if(changed) Log(L"Media state: source="+source+L" playing="+std::to_wstring(playing)+L" hasMedia="+std::to_wstring(hasMedia)+L" wantsMedia="+std::to_wstring(state.MediaVisible()));
                    auto scripts=message.GetNamedArray(L"scripts",JsonArray());
                    for(const auto& script:scripts)
                    {
                        pendingScriptBytes+=script.GetString().size()*sizeof(wchar_t);
                        if(pendingScripts.size()>=4096 || pendingScriptBytes>4*1024*1024) throw std::runtime_error("Renderer backlog limit");
                        pendingScripts.emplace_back(script.GetString().c_str());
                    }
                }
                else if(type==L"disconnected") { state.Disconnect(); documentReady=false; pendingScripts.clear(); pendingScriptBytes=0; Failure(L"Media helper disconnected; monitoring remains active while reconnecting.",HRESULT_FROM_WIN32(ERROR_BROKEN_PIPE)); }
                else if(type==L"error") Failure(message.GetNamedString(L"message",L"").c_str(),E_FAIL);
                else if(type==L"command" && (message.GetNamedString(L"action",L"")==L"getNativeSettings" || message.GetNamedString(L"action",L"")==L"applyNativeSettings")) NativeSettings(message);
                else if(type==L"command" && message.GetNamedString(L"action",L"")==L"getTrafficHistory") TrafficHistory(message);
                else if(type==L"command" && message.GetNamedString(L"action",L"")==L"weatherCommand") WeatherCommand(message);
                else if(type==L"command" && message.GetNamedString(L"action",L"")==L"openMonitorSettings")
                    Send(Command(L"openSettings"));
                else if(type==L"command" && message.GetNamedString(L"action",L"")==L"showMedia") state.manual=2;
                else if(type==L"command" && message.GetNamedString(L"action",L"")==L"toggleMedia") state.manual=state.MediaVisible() ? 1 : 2;
            }
            catch (...) { state.Disconnect(); reconnect=true; Failure(L"Invalid or oversized media message.",E_INVALIDARG); pendingScripts.clear(); pendingScriptBytes=0; }
        }
        if(navigationStarted && GetTickCount64()-navigationStarted>10000)
        {
            navigationStarted=0; Failure(L"Media renderer readiness timed out; retrying.",HRESULT_FROM_WIN32(ERROR_TIMEOUT));
            if(controller) controller->Close();controller.Reset();web.Reset();nextWebRetry=0;reconnect=true;
        }
        CreateWebView();
        if (pendingBrowserGesture && GetTickCount64()-browserGestureStarted>750) FinishBrowserGesture(pendingBrowserGesture,L"panel");
        ExecutePending();
        ApplyAppearance();
        bool nextVisible=state.MediaVisible() && controller && documentReady;
        if(visible!=nextVisible)
        {
            // A mode transition must not cancel an in-progress native click.
            if(auto* taskbar=dynamic_cast<CTaskBarDlg*>(CWnd::FromHandlePermanent(owner))) if (!taskbar->SetMediaPresentation(nextVisible)) nextVisible=false;
            Log(L"Media visibility="+std::to_wstring(nextVisible)+L" connected="+std::to_wstring(state.connected)+L" documentReady="+std::to_wstring(documentReady)+L" manual="+std::to_wstring(state.manual));
        }
        if(controller)
        {
            RECT bounds{}; GetClientRect(owner,&bounds);
            controller->put_Bounds(bounds);
            controller->NotifyParentWindowPositionChanged();
            controller->put_IsVisible(nextVisible || mediaFade.Active());
            if (compositionTarget && visible != nextVisible)
            {
                HRESULT result = mediaFade.Set(compositionDevice.Get(),mediaVisual.Get(),nextVisible);
                if (SUCCEEDED(result)) result = compositionTarget->SetRoot(mediaVisual.Get());
                controller->put_IsVisible(nextVisible || mediaFade.Active());
                if (SUCCEEDED(result)) result = compositionDevice->Commit();
                if (FAILED(result))
                {
                    Failure(L"Media composition failed; restoring monitoring.", result);
                    nextVisible = false;
                    if (auto* taskbar = dynamic_cast<CTaskBarDlg*>(CWnd::FromHandlePermanent(owner))) taskbar->SetMediaPresentation(false);
                    controller->Close(); controller.Reset(); web.Reset(); compositionController.Reset();
                    compositionTarget.Reset(); mediaVisual.Reset(); compositionDevice.Reset(); compositionOwner=nullptr;
                    documentReady=false; state.Disconnect(); reconnect=true; nextWebRetry=0;
                }
            }
        }
        if(visible!=nextVisible) { visible=nextVisible; InvalidateRect(owner,nullptr,FALSE); }
        if((announcePending || lastAnnounced!=visible) && state.connected)
        {
            auto command=Command(L"mediaVisible"); command.SetNamedValue(L"visible",JsonValue::CreateBooleanValue(visible)); Send(command); lastAnnounced=visible; announcePending=false;
        }
    }
};

std::shared_ptr<NativeMediaHost::Impl>& NativeMediaHost::Shared()
{
    static std::shared_ptr<Impl> instance;
    return instance;
}
NativeMediaHost::NativeMediaHost(HWND owner)
{
    auto& instance=Shared();
    if (!instance)
    {
        instance=std::make_shared<Impl>(owner);
        auto impl=instance.get();
        impl->worker=std::thread([impl] { impl->Run(); });
    }
    m_impl=instance;
    m_impl->owner=owner;
    m_impl->visible=false;
    m_impl->announcePending=true;
    if(m_impl->controller)
    {
        m_impl->controller->put_ParentWindow(owner);
        const auto result = m_impl->BindComposition();
        if (FAILED(result)) m_impl->Failure(L"Could not reattach taskbar media composition.",result);
    }
}
NativeMediaHost::~NativeMediaHost()
{
    // Explorer/settings recreation keeps the one application-owned helper and renderer alive.
    if(m_impl)
    {
        if (m_impl->controller) m_impl->controller->put_IsVisible(FALSE);
        if (m_impl->compositionTarget) m_impl->compositionTarget->SetRoot(nullptr);
        if (m_impl->compositionDevice) m_impl->compositionDevice->Commit();
        m_impl->compositionTarget.Reset(); m_impl->compositionOwner = nullptr;
        m_impl->owner=theApp.m_pMainWnd->GetSafeHwnd();
        if (m_impl->controller) m_impl->controller->put_ParentWindow(m_impl->owner);
    }
}
void NativeMediaHost::Shutdown() { auto& instance=Shared(); if(instance) instance->stop=true; instance.reset(); }
void NativeMediaHost::OpenSettings(const wchar_t* page)
{
    if(auto instance=Shared())
    {
        auto command = Command(L"openSettings");
        if (page) command.SetNamedValue(L"page", JsonValue::CreateStringValue(page));
        instance->Send(command);
    }
}
void NativeMediaHost::Tick() { m_impl->Tick(); }
void NativeMediaHost::Wheel(int delta) { m_impl->state.Wheel(delta); m_impl->Tick(); }
void NativeMediaHost::TogglePanel() { m_impl->Panel(); }
void NativeMediaHost::Menu() { m_impl->ShowMenu(); }
bool NativeMediaHost::IsVisible() const { return m_impl->visible; }

bool NativeMediaHost::MouseInput(UINT message, WPARAM keys, LPARAM position)
{
    POINT point{ static_cast<short>(LOWORD(position)), static_cast<short>(HIWORD(position)) };
    if (message == WM_CANCELMODE || message == WM_CAPTURECHANGED)
    {
        m_impl->pointerDown = false;
        if (message == WM_CANCELMODE)
        {
            m_impl->pendingBrowserGesture = 0;
            if (::GetCapture() == m_impl->owner) ::ReleaseCapture();
        }
        return false;
    }
    if (message == WM_LBUTTONDOWN || message == WM_LBUTTONDBLCLK)
    {
        // Finish a prior unresponsive browser query before accepting the next gesture.
        if (m_impl->pendingBrowserGesture) m_impl->FinishBrowserGesture(m_impl->pendingBrowserGesture,L"panel");
        m_impl->pointerDown = true;
        m_impl->pointerOrigin = point;
        m_impl->pointerBrowser = m_impl->visible && m_impl->browserFrame;
        m_impl->pointerSource = m_impl->state.source;
        ++m_impl->gestureSerial;
        m_impl->ClickLog(L"down",m_impl->gestureSerial);
        ::SetCapture(m_impl->owner);
        return true;
    }
    if (message == WM_LBUTTONUP)
    {
        bool activate = m_impl->pointerDown && ::GetCapture() == m_impl->owner;
        m_impl->pointerDown = false;
        if (::GetCapture() == m_impl->owner) ::ReleaseCapture();
        RECT bounds{}; GetClientRect(m_impl->owner,&bounds);
        activate = activate && PtInRect(&bounds,point) &&
            abs(point.x-m_impl->pointerOrigin.x) <= GetSystemMetrics(SM_CXDRAG) && abs(point.y-m_impl->pointerOrigin.y) <= GetSystemMetrics(SM_CYDRAG);
        m_impl->ClickLog(activate ? L"release" : L"cancel",m_impl->gestureSerial);
        if (activate) m_impl->ActivatePointer(point,m_impl->gestureSerial);
        return true;
    }
    if (!m_impl->visible || !m_impl->compositionController) return false;
    if (message != WM_MOUSEMOVE && message != WM_MOUSELEAVE) return false;
    if (message == WM_MOUSEMOVE)
    {
        TRACKMOUSEEVENT tracking{ sizeof(tracking), TME_LEAVE, m_impl->owner, 0 };
        ::TrackMouseEvent(&tracking);
    }
    if (message == WM_MOUSELEAVE) point = {};
    const HRESULT result=m_impl->compositionController->SendMouseInput(static_cast<COREWEBVIEW2_MOUSE_EVENT_KIND>(message),
        static_cast<COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS>(keys), 0, point);
    if (FAILED(result) && m_impl->clickLogs < 192) m_impl->ClickLog(L"hover-input-failed",m_impl->gestureSerial);
    return true;
}
