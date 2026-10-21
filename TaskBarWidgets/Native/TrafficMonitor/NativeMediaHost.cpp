#include "stdafx.h"
#include "NativeMediaHost.h"
#include "MediaAnimation.h"
#include "MediaDisplayState.h"
#include "MediaFrameContract.h"
#include "NativeMediaRenderer.h"
#include "ManagedRuntimeEnvironment.h"
#include "UpdateHealth.h"
#include "TrafficMonitor.h"
#include "NativeSettingsBridge.h"
#include "NativeWeatherBridge.h"
#include <winrt/Windows.Data.Json.h>
#include <atomic>
#include <chrono>
#include <deque>
#include <filesystem>
#include <mutex>
#include <thread>
#include <utility>

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

    bool HasValue(const JsonObject& object, const wchar_t* key)
    {
        return object.HasKey(key);
    }

    bool IsNullOrMissing(const JsonObject& object, const wchar_t* key)
    {
        if (!object.HasKey(key)) return true;
        try
        {
            return object.GetNamedValue(key).ValueType() == JsonValueType::Null;
        }
        catch (...) { return true; }
    }

    std::wstring Text(const JsonObject& object, const wchar_t* key,
        const std::wstring& fallback = L"")
    {
        if (!object.HasKey(key)) return fallback;
        try { return object.GetNamedString(key).c_str(); }
        catch (...) { return fallback; }
    }

    double Number(const JsonObject& object, const wchar_t* key, double fallback = 0)
    {
        if (!object.HasKey(key)) return fallback;
        try
        {
            const double value = object.GetNamedNumber(key);
            return std::isfinite(value) ? value : fallback;
        }
        catch (...) { return fallback; }
    }

    bool Boolean(const JsonObject& object, const wchar_t* key, bool fallback = false)
    {
        if (!object.HasKey(key)) return fallback;
        try { return object.GetNamedBoolean(key); }
        catch (...) { return fallback; }
    }

    bool ObjectValue(const JsonObject& object, const wchar_t* key, JsonObject& result)
    {
        if (!object.HasKey(key)) return false;
        try
        {
            const auto value = object.GetNamedValue(key);
            if (value.ValueType() != JsonValueType::Object) return false;
            result = value.GetObject();
            return true;
        }
        catch (...) { return false; }
    }
}

struct NativeMediaHost::Impl
{
    HWND owner{};
    std::atomic<HWND> wakeOwner{};
    std::atomic<bool> stop{};
    std::atomic<bool> reconnect{};
    std::thread worker;
    HANDLE helper{};
    std::atomic<DWORD> helperPid{};
    HANDLE workerWake{};
    std::mutex mutex;
    std::deque<std::string> incoming, outgoing;
    size_t incomingBytes{};
    MediaDisplayState state;
    NativeMedia::FrameStore store;
    NativeMedia::NativeMediaRenderer renderer;
    bool visible{};
    bool mediaPresented{};
    bool announcePending{true};
    bool lastAnnounced{};
    bool firstFramePending{};
    bool pointerDown{};
    POINT pointerOrigin{};
    std::wstring pointerSource;
    unsigned long long gestureSerial{};
    unsigned clickLogs{};
    std::wstring diagnostic;
    std::wstring lastAppearance;
    bool appearancePending{ true };
    ULONGLONG lastFailureLog{};
    std::mutex logMutex;
    std::filesystem::path appDirectory;
    UINT timerInterval{};
    std::atomic<bool> uiWakePosted{};
    bool stateDirty{};
    bool rendererNeedsState{ true };
    bool coverDirty{ true };
    std::uint64_t coverVersion{};

    explicit Impl(HWND hwnd) : owner(hwnd)
    {
        wakeOwner = hwnd;
        wchar_t path[32768]{};
        GetModuleFileNameW(nullptr, path, _countof(path));
        appDirectory = std::filesystem::path(path).parent_path();
        workerWake = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        renderer.SetLogger([this](const std::wstring& message, HRESULT result)
        {
            Log(message, result);
        });
    }

    ~Impl()
    {
        stop = true;
        if (workerWake) SetEvent(workerWake);
        if (worker.joinable()) worker.join();
        if (helper)
        {
            // This handle belongs exclusively to the helper launched by this product.
            if (WaitForSingleObject(helper, 1500) == WAIT_TIMEOUT)
                TerminateProcess(helper, 0);
            CloseHandle(helper);
        }
        if (timerInterval && owner) KillTimer(owner, MEDIA_ANIMATION_TIMER);
        if (workerWake) CloseHandle(workerWake);
    }

    void Log(const std::wstring& message, HRESULT result = S_OK)
    {
        std::lock_guard lock(logMutex);
        const std::wstring path = theApp.m_appdata_dir + L"native-media.log";
        WIN32_FILE_ATTRIBUTE_DATA attributes{};
        if (GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &attributes) &&
            (attributes.nFileSizeHigh || attributes.nFileSizeLow > 512 * 1024))
            MoveFileExW(path.c_str(), (path + L".1").c_str(), MOVEFILE_REPLACE_EXISTING);
        SYSTEMTIME time{}; GetLocalTime(&time);
        wchar_t prefix[96];
        swprintf_s(prefix, L"%04u-%02u-%02u %02u:%02u:%02u HR=0x%08X ",
            time.wYear, time.wMonth, time.wDay, time.wHour, time.wMinute, time.wSecond,
            static_cast<unsigned>(result));
        const auto bounded = message.size() <= 2048 ? message : message.substr(0, 2048) + L" [truncated]";
        const auto line = Utf8(prefix + bounded + L"\r\n");
        HANDLE file = CreateFileW(path.c_str(), FILE_APPEND_DATA,
            FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS,
            FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file != INVALID_HANDLE_VALUE)
        {
            DWORD written{};
            WriteFile(file, line.data(), static_cast<DWORD>(line.size()), &written, nullptr);
            CloseHandle(file);
        }
    }

    void Failure(const std::wstring& message, HRESULT result)
    {
        const bool changed = diagnostic != message;
        diagnostic = message;
        if (changed || GetTickCount64() - lastFailureLog >= 5000)
        {
            lastFailureLog = GetTickCount64();
            Log(message, result);
        }
    }

    void PostWake()
    {
        const HWND hwnd = wakeOwner.load();
        if (!hwnd) return;
        bool expected = false;
        if (!uiWakePosted.compare_exchange_strong(expected, true)) return;
        if (!PostMessageW(hwnd, WM_MEDIA_HOST_WAKE, 0, 0)) uiWakePosted = false;
    }

    void WakeWorker()
    {
        if (workerWake) SetEvent(workerWake);
    }

    void ApplyAppearance()
    {
        if (!state.connected) return;
        const auto& style = theApp.m_taskbar_data;
        JsonObject payload;
        payload.SetNamedValue(L"fontFamily",
            JsonValue::CreateStringValue(style.font.name.GetString()));
        payload.SetNamedValue(L"fontSize",
            JsonValue::CreateNumberValue(style.font.size * 96.0 / 72.0));
        payload.SetNamedValue(L"fontWeight",
            JsonValue::CreateNumberValue(style.font.bold ? 700 : 400));
        payload.SetNamedValue(L"fontItalic",
            JsonValue::CreateBooleanValue(style.font.italic));
        payload.SetNamedValue(L"fontUnderline",
            JsonValue::CreateBooleanValue(style.font.underline));
        payload.SetNamedValue(L"fontStrikeOut",
            JsonValue::CreateBooleanValue(style.font.strike_out));
        const double alpha = style.IsTaskbarTransparent()
            ? 0.0 : style.backgroundOpacity / 100.0;
        const std::wstring color = L"rgba(" +
            std::to_wstring(GetRValue(style.back_color)) + L"," +
            std::to_wstring(GetGValue(style.back_color)) + L"," +
            std::to_wstring(GetBValue(style.back_color)) + L"," +
            std::to_wstring(alpha) + L")";
        payload.SetNamedValue(L"surfaceColor", JsonValue::CreateStringValue(color));
        const std::wstring value = payload.Stringify().c_str();
        if (!appearancePending && value == lastAppearance) return;
        lastAppearance = value;
        appearancePending = false;
        auto message = Command(L"taskbarAppearance");
        message.SetNamedValue(L"payload", payload);
        Send(message);
    }

    void EnqueueIncoming(std::string message)
    {
        {
            std::lock_guard lock(mutex);
            if (incoming.size() >= 128 || incomingBytes + message.size() > 8 * 1024 * 1024)
            {
                reconnect = true;
                WakeWorker();
                return;
            }
            incomingBytes += message.size();
            incoming.push_back(std::move(message));
        }
        PostWake();
    }

    void Send(const JsonObject& message)
    {
        const auto action = message.GetNamedString(L"action", L"");
        if (action == L"togglePanel" || action == L"openSettings")
        {
            if (UpdateHealth::AwaitingHealth()) return;
            const DWORD pid = helperPid;
            if (pid) AllowSetForegroundWindow(pid);
        }
        std::lock_guard lock(mutex);
        if (outgoing.size() < 64)
            outgoing.push_back(Utf8(message.Stringify().c_str()) + "\n");
        else
            Log(L"Command queue full: " + std::wstring(action));
        if (workerWake) SetEvent(workerWake);
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
        std::wstring command = L"\"" + executable + L"\" --host-pid " +
            std::to_wstring(GetCurrentProcessId()) + L" --pipe-name TaskBarWidgets." +
            std::to_wstring(GetCurrentProcessId()) + L" --portable-data " +
            (theApp.m_general_data.portable_mode ? L"1" : L"0");
        STARTUPINFOW startup{ sizeof(startup) };
        startup.dwFlags = STARTF_USESHOWWINDOW;
        startup.wShowWindow = SW_HIDE;
        PROCESS_INFORMATION process{};
        const auto runtime = SystemDotnetRoot();
        if (runtime.empty())
        {
            Failure(L"System .NET Desktop Runtime x64 is missing.",
                HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND));
            return;
        }
        auto environment = ManagedEnvironment(runtime);
        if (environment.empty())
        {
            Failure(L"Cannot prepare the managed process environment.", E_FAIL);
            return;
        }
        if (CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE,
            CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT, environment.data(),
            appDirectory.c_str(), &startup, &process))
        {
            helper = process.hProcess;
            helperPid = process.dwProcessId;
            CloseHandle(process.hThread);
            Log(L"Media helper launched: PID " + std::to_wstring(helperPid.load()));
        }
        else
        {
            Failure(L"Media helper launch failed.", HRESULT_FROM_WIN32(GetLastError()));
        }
    }

    bool Transfer(HANDLE pipe, void* buffer, DWORD length, bool write, DWORD& transferred)
    {
        OVERLAPPED operation{};
        operation.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!operation.hEvent) return false;
        BOOL ok = write ? WriteFile(pipe, buffer, length, &transferred, &operation)
                        : ReadFile(pipe, buffer, length, &transferred, &operation);
        if (!ok && GetLastError() == ERROR_IO_PENDING)
        {
            const DWORD wait = WaitForSingleObject(operation.hEvent, 500);
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

    bool DrainWrites(HANDLE pipe)
    {
        std::deque<std::string> writes;
        {
            std::lock_guard lock(mutex);
            writes.swap(outgoing);
        }
        for (auto& message : writes)
        {
            size_t offset{};
            while (offset < message.size())
            {
                DWORD sent{};
                if (!Transfer(pipe, message.data() + offset,
                    static_cast<DWORD>(message.size() - offset), true, sent) || sent == 0)
                    return false;
                offset += sent;
            }
        }
        return true;
    }

    void Run()
    {
        const std::wstring name = L"\\\\.\\pipe\\TaskBarWidgets." +
            std::to_wstring(GetCurrentProcessId());
        ULONGLONG nextLaunch{};
        while (!stop)
        {
            if (GetTickCount64() >= nextLaunch)
            {
                LaunchHelper();
                nextLaunch = GetTickCount64() + 5000;
            }
            HANDLE pipe = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                OPEN_EXISTING, FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT |
                    SECURITY_IDENTIFICATION, nullptr);
            if (pipe == INVALID_HANDLE_VALUE)
            {
                if (workerWake)
                    WaitForSingleObject(workerWake, 250);
                continue;
            }
            ULONG serverPid{};
            if (!GetNamedPipeServerProcessId(pipe, &serverPid) || serverPid != helperPid.load())
            {
                CloseHandle(pipe);
                if (workerWake) WaitForSingleObject(workerWake, 250);
                continue;
            }
            Log(L"Media helper pipe connected.");
            reconnect = false;
            bool alive = true;
            std::string partial;
            std::vector<char> readBuffer(64 * 1024);
            HANDLE readEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            OVERLAPPED readOperation{};
            readOperation.hEvent = readEvent;
            bool readPending = false;
            const auto handleChunk = [&](const char* data, size_t length)
            {
                partial.append(data, length);
                size_t line;
                while ((line = partial.find('\n')) != std::string::npos)
                {
                    if (line > MaximumMessage) { alive = false; return; }
                    std::string message = partial.substr(0, line);
                    partial.erase(0, line + 1);
                    EnqueueIncoming(std::move(message));
                }
                if (partial.size() > MaximumMessage) alive = false;
            };
            const auto issueRead = [&]()
            {
                if (readPending || !alive) return;
                ResetEvent(readEvent);
                DWORD received{};
                const BOOL ok = ReadFile(pipe, readBuffer.data(),
                    static_cast<DWORD>(readBuffer.size()), &received, &readOperation);
                if (ok)
                {
                    if (!received) alive = false;
                    else handleChunk(readBuffer.data(), received);
                }
                else if (GetLastError() == ERROR_IO_PENDING)
                {
                    readPending = true;
                }
                else
                {
                    alive = false;
                }
            };
            while (!stop && alive && !reconnect)
            {
                if (!DrainWrites(pipe)) { alive = false; break; }
                issueRead();
                HANDLE waits[3]{ readEvent, workerWake, nullptr };
                DWORD waitCount = workerWake ? 2 : 1;
                const DWORD wait = WaitForMultipleObjects(waitCount, waits, FALSE, INFINITE);
                if (stop) break;
                if (wait == WAIT_OBJECT_0)
                {
                    DWORD received{};
                    if (!GetOverlappedResult(pipe, &readOperation, &received, FALSE))
                    {
                        alive = false;
                        break;
                    }
                    readPending = false;
                    if (!received) { alive = false; break; }
                    handleChunk(readBuffer.data(), received);
                }
                else if (wait == WAIT_OBJECT_0 + 1)
                {
                    ResetEvent(workerWake);
                    continue;
                }
                else
                {
                    alive = false;
                }
            }
            if (readPending) CancelIoEx(pipe, &readOperation);
            if (readEvent) CloseHandle(readEvent);
            if (stop)
            {
                const char shutdown[] = "{\"version\":1,\"type\":\"command\",\"action\":\"shutdown\"}\n";
                DWORD sent{};
                Transfer(pipe, const_cast<char*>(shutdown), sizeof(shutdown) - 1, true, sent);
            }
            CancelIoEx(pipe, nullptr);
            CloseHandle(pipe);
            {
                std::lock_guard lock(mutex);
                incoming.clear();
                incomingBytes = 0;
                outgoing.clear();
            }
            EnqueueIncoming("{\"version\":1,\"type\":\"disconnected\"}");
        }
    }

    void NativeSettings(const JsonObject& request)
    {
        auto reply = Command(L"nativeSettings");
        reply.SetNamedValue(L"requestId",
            JsonValue::CreateStringValue(request.GetNamedString(L"requestId", L"")));
        try
        {
            const auto now = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::system_clock::now().time_since_epoch()).count();
            const auto expires = request.GetNamedNumber(L"expiresAt", 0);
            if (!NativeSettingsValidation::Unexpired(expires, now))
                throw std::invalid_argument("Native settings request expired; retry the operation.");
            NativeSettingsBridge bridge;
            if (request.GetNamedString(L"action") == L"applyNativeSettings")
                bridge.Apply(request.GetNamedObject(L"payload"));
            reply.SetNamedValue(L"payload", NativeSettingsBridge().Describe());
            reply.SetNamedValue(L"success", JsonValue::CreateBooleanValue(true));
            reply.SetNamedValue(L"error", JsonValue::CreateStringValue(L""));
            Log(L"Native settings read/apply succeeded.");
        }
        catch (const std::exception& error)
        {
            reply.SetNamedValue(L"success", JsonValue::CreateBooleanValue(false));
            reply.SetNamedValue(L"error",
                JsonValue::CreateStringValue(winrt::to_hstring(error.what())));
            Log(L"Native settings rejected: " + std::wstring(winrt::to_hstring(error.what()).c_str()));
        }
        catch (const winrt::hresult_error& error)
        {
            reply.SetNamedValue(L"success", JsonValue::CreateBooleanValue(false));
            reply.SetNamedValue(L"error", JsonValue::CreateStringValue(error.message()));
            Log(L"Native settings failed.", error.code());
        }
        if (!reply.GetNamedBoolean(L"success", false))
        {
            try { reply.SetNamedValue(L"payload", NativeSettingsBridge().Describe()); }
            catch (...) { Log(L"Could not read native settings after failure.", E_FAIL); }
        }
        Send(reply);
    }

    void UpdateCommand(const JsonObject& request)
    {
        auto reply = Command(L"updateResult");
        reply.SetNamedValue(L"requestId",
            JsonValue::CreateStringValue(request.GetNamedString(L"requestId", L"")));
        try
        {
            const auto now = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::system_clock::now().time_since_epoch()).count();
            if (!NativeSettingsValidation::Unexpired(request.GetNamedNumber(L"expiresAt", 0), now))
                throw std::runtime_error("Update request expired.");
            const auto action = request.GetNamedString(L"action");
            JsonObject payload;
            if (action == L"getUpdateContext")
            {
                payload.SetNamedValue(L"installRoot", JsonValue::CreateStringValue(theApp.m_module_dir));
                payload.SetNamedValue(L"nativeDataRoot", JsonValue::CreateStringValue(theApp.m_config_dir));
                payload.SetNamedValue(L"portableMode",
                    JsonValue::CreateBooleanValue(theApp.m_general_data.portable_mode));
            }
            else if (action == L"updateUiReady") UpdateHealth::Report(VERSION);
            else if (action == L"exitForUpdate")
            {
                auto values = request.GetNamedObject(L"payload");
                auto job = UpdateHealth::Verify(values.GetNamedString(L"jobPath").c_str(),
                    values.GetNamedString(L"nonce").c_str(), theApp.m_module_dir);
                auto monitor = CTrafficMonitorDlg::Instance();
                if (!monitor || !monitor->FlushForUpdate())
                    throw std::runtime_error("Cannot save settings or traffic history; update cancelled.");
                JsonObject approval;
                approval.SetNamedValue(L"nonce", JsonValue::CreateStringValue(job.nonce));
                UpdateHealth::Write(job.work / L"shutdown-approved.json", approval);
                if (!SetTimer(nullptr, 0, 250, [](HWND, UINT, UINT_PTR timer, DWORD)
                {
                    KillTimer(nullptr, timer);
                    if (theApp.m_pMainWnd)
                        PostMessageW(theApp.m_pMainWnd->GetSafeHwnd(), WM_CLOSE, 0, 0);
                }))
                {
                    DeleteFileW((job.work / L"shutdown-approved.json").c_str());
                    throw std::runtime_error("Cannot schedule update shutdown.");
                }
            }
            reply.SetNamedValue(L"payload", payload);
            reply.SetNamedValue(L"success", JsonValue::CreateBooleanValue(true));
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

    void WeatherCommand(const JsonObject& request)
    {
        auto reply = Command(L"weatherResult");
        reply.SetNamedValue(L"requestId",
            JsonValue::CreateStringValue(request.GetNamedString(L"requestId", L"")));
        try
        {
            const auto now = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::system_clock::now().time_since_epoch()).count();
            if (!NativeSettingsValidation::Unexpired(request.GetNamedNumber(L"expiresAt", 0), now))
                throw std::invalid_argument("Weather request expired; retry.");
            reply.SetNamedValue(L"payload",
                NativeWeatherBridge::Handle(request.GetNamedObject(L"payload")));
            reply.SetNamedValue(L"success", JsonValue::CreateBooleanValue(true));
        }
        catch (const std::exception& error)
        {
            reply.SetNamedValue(L"success", JsonValue::CreateBooleanValue(false));
            reply.SetNamedValue(L"error", JsonValue::CreateStringValue(winrt::to_hstring(error.what())));
        }
        catch (...)
        {
            reply.SetNamedValue(L"success", JsonValue::CreateBooleanValue(false));
            reply.SetNamedValue(L"error",
                JsonValue::CreateStringValue(L"Weather request could not be processed."));
        }
        Send(reply);
    }

    void TrafficHistory(const JsonObject& request)
    {
        auto reply = Command(L"trafficHistory");
        reply.SetNamedValue(L"requestId",
            JsonValue::CreateStringValue(request.GetNamedString(L"requestId", L"")));
        try
        {
            const auto now = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::system_clock::now().time_since_epoch()).count();
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
                swprintf_s(text, L"%04d-%02u-%02u", static_cast<int>(date.year()),
                    static_cast<unsigned>(date.month()), static_cast<unsigned>(date.day()));
                return JsonValue::CreateStringValue(text);
            };
            JsonArray days;
            for (const auto& day : monitor->GetTrafficHistorySnapshot())
            {
                if (day.date < first || day.date > std::chrono::sys_days{ today }) continue;
                JsonObject item;
                item.SetNamedValue(L"date", format(std::chrono::year_month_day{ day.date }));
                item.SetNamedValue(L"totalKiB",
                    JsonValue::CreateNumberValue(static_cast<double>(day.totalKiB)));
                days.Append(item);
            }
            JsonObject payload;
            payload.SetNamedValue(L"today", format(today));
            payload.SetNamedValue(L"days", days);
            const bool chinese = theApp.m_str_table.IsSimplifiedChinese() ||
                theApp.m_str_table.GetLanguageInfo().bcp_47 == L"zh-TW";
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

    NativeMedia::Style ReadStyle(const JsonObject& object)
    {
        NativeMedia::Style style;
        style.fontFamily = Text(object, L"fontFamily", style.fontFamily);
        style.layoutScalePercent = Number(object, L"layoutScalePercent", style.layoutScalePercent);
        style.fontSize = Number(object, L"fontSize", style.fontSize);
        style.showCover = Boolean(object, L"showCover", style.showCover);
        style.coverPosition = Text(object, L"coverPosition", style.coverPosition);
        style.enableControlPanel = Boolean(object, L"enableControlPanel", style.enableControlPanel);
        style.coverSize = Number(object, L"coverSize", style.coverSize);
        style.coverGap = Number(object, L"coverGap", style.coverGap);
        style.coverCornerRadius = Number(object, L"coverCornerRadius", style.coverCornerRadius);
        style.viewportDescenderBuffer = Number(object, L"viewportDescenderBuffer",
            style.viewportDescenderBuffer);
        style.layoutHorizontalPadding = Number(object, L"layoutHorizontalPadding",
            style.layoutHorizontalPadding);
        style.lyricsPaneTopPadding = Number(object, L"lyricsPaneTopPadding", style.lyricsPaneTopPadding);
        style.lyricsPaneRightPadding = Number(object, L"lyricsPaneRightPadding", style.lyricsPaneRightPadding);
        style.lyricsPaneLeftPadding = Number(object, L"lyricsPaneLeftPadding", style.lyricsPaneLeftPadding);
        style.primaryOffsetY = Number(object, L"primaryOffsetY", style.primaryOffsetY);
        style.secondaryOffsetY = Number(object, L"secondaryOffsetY", style.secondaryOffsetY);
        style.lineTextBottomPadding = Number(object, L"lineTextBottomPadding", style.lineTextBottomPadding);
        style.surfaceRadius = Number(object, L"surfaceRadius", style.surfaceRadius);
        style.layerTransitionOffset = Number(object, L"layerTransitionOffset", style.layerTransitionOffset);
        style.coverFallbackFontSize = Number(object, L"coverFallbackFontSize", style.coverFallbackFontSize);
        style.spectrumWidth = Number(object, L"spectrumWidth", style.spectrumWidth);
        style.spectrumHeight = Number(object, L"spectrumHeight", style.spectrumHeight);
        style.spectrumGap = Number(object, L"spectrumGap", style.spectrumGap);
        style.spectrumBarWidth = Number(object, L"spectrumBarWidth", style.spectrumBarWidth);
        style.spectrumBarHeight = Number(object, L"spectrumBarHeight", style.spectrumBarHeight);
        style.spectrumLowHeight = Number(object, L"spectrumLowHeight", style.spectrumLowHeight);
        style.spectrumHighHeight = Number(object, L"spectrumHighHeight", style.spectrumHighHeight);
        style.spectrumMiddleHeight = Number(object, L"spectrumMiddleHeight", style.spectrumMiddleHeight);
        style.fontWeight = static_cast<int>(Number(object, L"fontWeight", style.fontWeight));
        style.primaryColor = Text(object, L"primaryColor", style.primaryColor);
        style.secondaryColor = Text(object, L"secondaryColor", style.secondaryColor);
        style.translationColor = Text(object, L"translationColor", style.translationColor);
        style.wordScanOverlayColor = Text(object, L"wordScanOverlayColor", style.wordScanOverlayColor);
        style.surfaceColor = Text(object, L"surfaceColor", style.surfaceColor);
        style.surfaceShadow = Text(object, L"surfaceShadow", style.surfaceShadow);
        style.textShadow = Text(object, L"textShadow", style.textShadow);
        style.textAlignment = Text(object, L"textAlignment", style.textAlignment);
        return style;
    }

    NativeMedia::Cover ReadCover(const JsonObject& object, bool& emptyObject)
    {
        emptyObject = object.Size() == 0;
        NativeMedia::Cover cover;
        cover.cleared = emptyObject;
        cover.dataUri = Text(object, L"dataUri");
        cover.fallbackText = Text(object, L"fallbackText");
        cover.fallbackColor = Text(object, L"fallbackColor", L"rgba(67, 160, 71, 1)");
        cover.fallbackIconDataUri = Text(object, L"fallbackIconDataUri");
        cover.trackId = Text(object, L"trackId");
        return cover;
    }

    NativeMedia::Browser ReadBrowser(const JsonObject& object)
    {
        NativeMedia::Browser browser;
        browser.active = Boolean(object, L"active");
        browser.title = Text(object, L"title");
        browser.artist = Text(object, L"artist");
        browser.source = Text(object, L"source");
        browser.isPlaying = Boolean(object, L"isPlaying");
        browser.canPrevious = Boolean(object, L"canPrevious");
        browser.canNext = Boolean(object, L"canNext");
        browser.canPlayPause = Boolean(object, L"canPlayPause");
        return browser;
    }

    NativeMedia::SpectrumTuning ReadTuning(const JsonObject& object)
    {
        NativeMedia::SpectrumTuning tuning;
        tuning.rise = Number(object, L"rise", tuning.rise);
        tuning.fall = Number(object, L"fall", tuning.fall);
        tuning.minHeight = Number(object, L"minHeight", tuning.minHeight);
        tuning.heightRange = Number(object, L"heightRange", tuning.heightRange);
        tuning.opacity = Number(object, L"opacity", tuning.opacity);
        tuning.barCount = static_cast<int>(Number(object, L"barCount", tuning.barCount));
        tuning.Normalize();
        return tuning;
    }

    bool ParseFrame(const JsonObject& message, NativeMedia::Update& update,
        std::wstring& error)
    {
        NativeMedia::Frame frame;
        const auto& previous = store.HasFrame() ? store.CurrentFrame() : frame;
        frame.sourceId = Text(message, L"sourceId", previous.sourceId);
        frame.isPlaying = Boolean(message, L"isPlaying", previous.isPlaying);
        frame.hasMedia = Boolean(message, L"hasMedia", previous.hasMedia);
        frame.isBrowser = Boolean(message, L"isBrowser", previous.isBrowser);
        frame.trackId = Text(message, L"trackId", previous.trackId);
        const double revision = Number(message, L"revision",
            static_cast<double>(store.Revision() + 1));
        if (revision < 0 || revision > 9007199254740991.0)
        {
            error = L"Invalid media frame revision.";
            return false;
        }
        frame.revision = static_cast<std::uint64_t>(revision);
        frame.lyrics = previous.lyrics;
        JsonObject lyrics;
        if (ObjectValue(message, L"lyrics", lyrics))
        {
            frame.lyrics.current = Text(lyrics, L"current", frame.lyrics.current);
            frame.lyrics.next = Text(lyrics, L"next", frame.lyrics.next);
            frame.lyrics.currentTranslation = Text(lyrics, L"currentTranslation",
                frame.lyrics.currentTranslation);
            frame.lyrics.nextTranslation = Text(lyrics, L"nextTranslation",
                frame.lyrics.nextTranslation);
            frame.lyrics.trackId = Text(lyrics, L"trackId", frame.trackId);
            frame.lyrics.progress = NativeMedia::Clamp01(Number(lyrics, L"progress"));
            frame.lyrics.currentLineIndex = static_cast<int>(
                Number(lyrics, L"currentLineIndex", -1));
            frame.lyrics.isPureMusic = Boolean(lyrics, L"isPureMusic");
            frame.lyrics.isPlaying = Boolean(lyrics, L"isPlaying", frame.isPlaying);
            frame.lyrics.translationMode = Boolean(lyrics, L"translationMode");
            frame.lyrics.animateTransition = Boolean(lyrics, L"animateTransition", true);
            if (IsNullOrMissing(lyrics, L"wordScanProgress"))
            {
                frame.lyrics.hasWordScanProgress = false;
                frame.lyrics.wordScanProgress = 0;
            }
            else
            {
                frame.lyrics.hasWordScanProgress = true;
                frame.lyrics.wordScanProgress = NativeMedia::Clamp01(
                    Number(lyrics, L"wordScanProgress"));
            }
            frame.lyrics.scene = NativeMedia::SceneFromWire(Text(lyrics, L"scene"),
                frame.lyrics.isPureMusic, frame.lyrics.current,
                frame.lyrics.currentLineIndex);
        }
        if (frame.lyrics.trackId.empty()) frame.lyrics.trackId = frame.trackId;

        frame.browser = previous.browser;
        JsonObject browser;
        if (ObjectValue(message, L"browser", browser))
            frame.browser = ReadBrowser(browser);
        else if (message.HasKey(L"browser"))
        {
            frame.browser = {};
        }
        frame.browser.active = frame.browser.active && frame.isBrowser;

        update = {};
        update.hasFrame = true;
        update.frame = std::move(frame);
        JsonObject style;
        if (ObjectValue(message, L"style", style))
            update.style = ReadStyle(style);
        else if (message.HasKey(L"style") &&
            message.GetNamedValue(L"style").ValueType() == JsonValueType::Object)
            update.style = ReadStyle(style);

        JsonObject cover;
        if (ObjectValue(message, L"cover", cover))
        {
            bool empty{};
            update.cover = ReadCover(cover, empty);
        }
        JsonObject tuning;
        if (ObjectValue(message, L"spectrumTuning", tuning))
            update.tuning = ReadTuning(tuning);

        if (message.HasKey(L"spectrum"))
        {
            try
            {
                const auto value = message.GetNamedValue(L"spectrum");
                if (value.ValueType() == JsonValueType::Array)
                {
                    std::vector<double> bars;
                    for (const auto& bar : value.GetArray())
                    {
                        if (bars.size() >= 256) break;
                        if (bar.ValueType() == JsonValueType::Number)
                            bars.push_back(NativeMedia::Clamp01(bar.GetNumber()));
                        else
                            bars.push_back(0);
                    }
                    update.spectrum = std::move(bars);
                }
            }
            catch (...)
            {
                error = L"Invalid media spectrum payload.";
                return false;
            }
        }
        return true;
    }

    bool ParseMessage(const JsonObject& message, std::wstring& error)
    {
        if (message.GetNamedNumber(L"version", 0) != 1)
        {
            error = L"Unsupported media protocol version.";
            return false;
        }
        const auto type = message.GetNamedString(L"type", L"");
        if (type == L"initialize")
        {
            if (message.GetNamedNumber(L"renderVersion", 0) != 2)
            {
                error = L"Media helper does not support native renderVersion 2.";
                return false;
            }
            store.Reset();
            state.Disconnect();
            firstFramePending = true;
            stateDirty = true;
            announcePending = true;
            appearancePending = true;
            renderer.SetVisible(false, false);
            Log(L"Native media renderer initialized; waiting for the first full frame.");
            return true;
        }
        if (type == L"frame")
        {
            if (message.GetNamedNumber(L"renderVersion", 0) != 2)
            {
                error = L"Media frame is not renderVersion 2.";
                return false;
            }
            NativeMedia::Update update;
            if (!ParseFrame(message, update, error)) return false;
            if (!store.HasStyle() && !update.style)
            {
                error = L"The first media frame must include a complete style.";
                return false;
            }
            if (!update.cover && !store.CurrentCover() && !store.HasFrame())
            {
                error = L"The first media frame must include a cover object.";
                return false;
            }
            const auto result = store.Apply(update);
            if (result == NativeMedia::ApplyResult::Stale)
            {
                Log(L"Stale media frame ignored (revision " +
                    std::to_wstring(update.frame.revision) + L").");
                return true;
            }
            if (result == NativeMedia::ApplyResult::Invalid)
            {
                error = L"Media frame contains an invalid progress value.";
                return false;
            }
            firstFramePending = false;
            stateDirty = true;
            if (update.cover) coverDirty = true;
            state.Frame(update.frame.sourceId, update.frame.isPlaying,
                update.frame.hasMedia);
            if (update.style) Log(L"Media style applied.");
            if (update.cover) Log(update.cover->cleared ? L"Media cover cleared." :
                L"Media cover updated for track " + update.cover->trackId + L".");
            return true;
        }
        if (type == L"disconnected")
        {
            state.Disconnect();
            firstFramePending = true;
            stateDirty = true;
            announcePending = true;
            appearancePending = true;
            Failure(L"Media helper disconnected; monitoring remains active while reconnecting.",
                HRESULT_FROM_WIN32(ERROR_BROKEN_PIPE));
            return true;
        }
        if (type == L"error")
        {
            Failure(Text(message, L"message", L"Media helper reported an error."), E_FAIL);
            return true;
        }
        if (type == L"command")
        {
            const auto action = Text(message, L"action");
            if (action == L"getNativeSettings" || action == L"applyNativeSettings")
                NativeSettings(message);
            else if (action == L"getUpdateContext" || action == L"exitForUpdate" ||
                action == L"updateUiReady")
                UpdateCommand(message);
            else if (action == L"getTrafficHistory")
                TrafficHistory(message);
            else if (action == L"weatherCommand")
                WeatherCommand(message);
            else if (action == L"openMonitorSettings")
                Send(Command(L"openSettings"));
            else if (action == L"showMedia")
                state.manual = 2;
            else if (action == L"toggleMedia")
                state.manual = state.MediaVisible() ? 1 : 2;
            return true;
        }
        return true;
    }

    void ProcessIncoming()
    {
        std::deque<std::string> messages;
        {
            std::lock_guard lock(mutex);
            messages.swap(incoming);
            incomingBytes = 0;
        }
        for (const auto& entry : messages)
        {
            try
            {
                auto message = JsonObject::Parse(winrt::to_hstring(entry));
                std::wstring error;
                if (!ParseMessage(message, error))
                {
                    Failure(error, E_INVALIDARG);
                    reconnect = true;
                    WakeWorker();
                    state.Disconnect();
                    firstFramePending = true;
                    stateDirty = true;
                    renderer.SetVisible(false, false);
                    break;
                }
            }
            catch (...)
            {
                Failure(L"Invalid or oversized media message.", E_INVALIDARG);
                reconnect = true;
                WakeWorker();
                state.Disconnect();
                firstFramePending = true;
                stateDirty = true;
                renderer.SetVisible(false, false);
                break;
            }
        }
    }

    NativeMedia::NativeAppearance CurrentAppearance() const
    {
        NativeMedia::NativeAppearance appearance;
        const auto& style = theApp.m_taskbar_data;
        appearance.fontFamily = style.font.name.GetString();
        appearance.fontSizePx = style.font.size * 96.0 / 72.0;
        appearance.fontWeight = style.font.bold ? 700 : 400;
        appearance.italic = style.font.italic;
        appearance.underline = style.font.underline;
        appearance.strikeOut = style.font.strike_out;
        const double alpha = style.IsTaskbarTransparent() ? 0.0 :
            style.backgroundOpacity / 100.0;
        appearance.surfaceColor = L"rgba(" + std::to_wstring(GetRValue(style.back_color)) +
            L", " + std::to_wstring(GetGValue(style.back_color)) + L", " +
            std::to_wstring(GetBValue(style.back_color)) + L", " +
            std::to_wstring(alpha) + L")";
        appearance.valid = true;
        return appearance;
    }

    void EnsureTimer(bool animate, bool retrying)
    {
        if (!owner || !IsWindow(owner)) return;
        UINT interval{};
        if (animate) interval = 16;
        else if (retrying || !renderer.IsReady()) interval = 250;
        if (interval == timerInterval) return;
        if (timerInterval) KillTimer(owner, MEDIA_ANIMATION_TIMER);
        timerInterval = interval;
        if (interval) SetTimer(owner, MEDIA_ANIMATION_TIMER, interval, nullptr);
    }

    void ApplyPresentation(bool desired)
    {
        auto* taskbar = dynamic_cast<CTaskBarDlg*>(CWnd::FromHandlePermanent(owner));
        if (desired)
        {
            if (!mediaPresented && taskbar && !taskbar->SetMediaPresentation(true))
            {
                desired = false;
                renderer.SetVisible(false, false);
            }
            else
            {
                mediaPresented = true;
            }
        }
        if (renderer.IsRequestedVisible() != desired)
            renderer.SetVisible(desired, true);
        if (!desired && mediaPresented && renderer.IsFullyHidden())
        {
            if (taskbar) taskbar->SetMediaPresentation(false);
            mediaPresented = false;
        }
        visible = desired;
    }

    void Tick()
    {
        uiWakePosted = false;
        ProcessIncoming();
        const std::uint64_t now = GetTickCount64();
        if (!owner || !IsWindow(owner))
        {
            EnsureTimer(false, false);
            return;
        }
        renderer.SetHost(owner);
        RECT bounds{};
        GetClientRect(owner, &bounds);
        UINT dpi = GetDpiForWindow(owner);
        if (!dpi) dpi = 96;
        renderer.UpdateGeometry(static_cast<float>(bounds.right - bounds.left),
            static_cast<float>(bounds.bottom - bounds.top), dpi / 96.0f);
        renderer.SetAppearance(CurrentAppearance());
        BOOL animation = TRUE;
        HIGHCONTRASTW contrast{ sizeof(contrast) };
        SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0, &animation, 0);
        SystemParametersInfoW(SPI_GETHIGHCONTRAST, sizeof(contrast), &contrast, 0);
        renderer.SetOptions(!animation, (contrast.dwFlags & HCF_HIGHCONTRASTON) != 0);
        ApplyAppearance();

        const bool wantsMedia = state.MediaVisible() && store.HasFrame() &&
            !firstFramePending;
        const bool initialized = renderer.IsReady() ||
            (wantsMedia && renderer.EnsureInitialized(now));
        if (store.CoverVersion() != coverVersion)
        {
            coverVersion = store.CoverVersion();
            coverDirty = true;
        }
        bool desired = initialized && wantsMedia;
        if (initialized && store.HasFrame() && store.HasStyle() &&
            (rendererNeedsState || stateDirty))
        {
            NativeMedia::RenderUpdate update;
            update.frame = &store.CurrentFrame();
            update.style = &store.CurrentStyle();
            update.cover = store.CurrentCover() ? &*store.CurrentCover() : nullptr;
            update.tuning = &store.CurrentTuning();
            update.spectrum = &store.CurrentSpectrum();
            update.forceFull = rendererNeedsState;
            update.coverChanged = coverDirty;
            renderer.Apply(update);
            rendererNeedsState = false;
            stateDirty = false;
            coverDirty = false;
        }
        if (!initialized)
        {
            rendererNeedsState = true;
            if (mediaPresented)
            {
                auto* taskbar = dynamic_cast<CTaskBarDlg*>(CWnd::FromHandlePermanent(owner));
                if (taskbar) taskbar->SetMediaPresentation(false);
                mediaPresented = false;
            }
            visible = false;
            EnsureTimer(false, wantsMedia);
            return;
        }
        ApplyPresentation(desired);
        const bool animating = renderer.Present(now);
        // Present can finish a fade-out; enable the monitoring surface before
        // the animation timer is stopped.
        ApplyPresentation(desired);
        if (!animating && renderer.IsRequestedVisible() && !renderer.IsFullyHidden())
            EnsureTimer(false, false);
        else
            EnsureTimer(animating || renderer.FadeActive() || renderer.NeedsAnimation(),
                !renderer.IsReady());

        const bool announcedVisible = renderer.IsRequestedVisible();
        if ((state.connected && (announcePending || announcedVisible != lastAnnounced)) ||
            (!state.connected && lastAnnounced))
        {
            auto command = Command(L"mediaVisible");
            command.SetNamedValue(L"visible", JsonValue::CreateBooleanValue(announcedVisible));
            Send(command);
            lastAnnounced = announcedVisible;
            announcePending = false;
        }
        if (!renderer.Diagnostic().empty() && diagnostic.empty())
            Failure(renderer.Diagnostic(), E_FAIL);
    }

    void Wheel(int delta)
    {
        state.Wheel(delta);
        Tick();
    }

    void Panel(unsigned long long gesture = 0)
    {
        if (clickLogs++ < 192)
            Log(L"Panel gesture=" + std::to_wstring(gesture));
        RECT bounds{};
        GetWindowRect(owner, &bounds);
        auto message = Command(L"togglePanel");
        message.SetNamedValue(L"x", JsonValue::CreateNumberValue((bounds.left + bounds.right) / 2));
        message.SetNamedValue(L"y", JsonValue::CreateNumberValue(bounds.top));
        message.SetNamedValue(L"entryWindow",
            JsonValue::CreateNumberValue(static_cast<double>(reinterpret_cast<INT_PTR>(owner))));
        message.SetNamedValue(L"gestureId",
            JsonValue::CreateNumberValue(static_cast<double>(gesture)));
        Send(message);
    }

    void ShowMenu()
    {
        HMENU menu = CreatePopupMenu();
        const bool chinese = theApp.m_str_table.IsSimplifiedChinese() ||
            theApp.m_str_table.GetLanguageInfo().bcp_47 == L"zh-TW";
        AppendMenuW(menu, MF_STRING, 1, chinese ? L"\u8bbe\u7f6e..." : L"Settings...");
        AppendMenuW(menu, MF_STRING | (state.manual == 0 ? MF_CHECKED : 0), 3,
            chinese ? L"\u81ea\u52a8\u5207\u6362" : L"Automatic display");
        AppendMenuW(menu, MF_STRING | (state.manual == 1 ? MF_CHECKED : 0), 4,
            chinese ? L"\u663e\u793a\u7cfb\u7edf\u76d1\u6d4b" : L"Show monitoring");
        AppendMenuW(menu, MF_STRING | (state.manual == 2 ? MF_CHECKED : 0), 5,
            chinese ? L"\u663e\u793a\u5a92\u4f53" : L"Show media");
        AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
        AppendMenuW(menu, MF_STRING, 7,
            chinese ? L"\u8bca\u65ad / \u91cd\u8bd5\u5a92\u4f53" : L"Diagnostics / retry media");
        AppendMenuW(menu, MF_STRING, 8, chinese ? L"\u9000\u51fa" : L"Exit");
        POINT point{}; GetCursorPos(&point); SetForegroundWindow(owner);
        const UINT action = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON,
            point.x, point.y, 0, owner, nullptr);
        DestroyMenu(menu);
        switch (action)
        {
        case 1: Send(Command(L"openSettings")); break;
        case 3: state.manual = 0; Tick(); break;
        case 4: state.manual = 1; Tick(); break;
        case 5: state.manual = 2; Tick(); break;
        case 7:
            state.Disconnect();
            store.Reset();
            firstFramePending = true;
            reconnect = true;
            renderer.RetryNow();
            rendererNeedsState = true;
            renderer.SetVisible(false, false);
            EnsureTimer(false, false);
            if (!diagnostic.empty())
                MessageBoxW(owner, diagnostic.c_str(), L"TaskBarWidgets",
                    MB_OK | MB_ICONINFORMATION);
            else
                MessageBoxW(owner, L"Media helper connects locally. Renderer and helper retry automatically.",
                    L"TaskBarWidgets", MB_OK | MB_ICONINFORMATION);
            break;
        case 8:
            if (theApp.m_pMainWnd)
                PostMessageW(theApp.m_pMainWnd->GetSafeHwnd(), WM_CLOSE, 0, 0);
            break;
        default: break;
        }
    }

    void GeometryChanged()
    {
        if (owner && IsWindow(owner))
        {
            RECT bounds{};
            GetClientRect(owner, &bounds);
            UINT dpi = GetDpiForWindow(owner);
            if (!dpi) dpi = 96;
            renderer.UpdateGeometry(static_cast<float>(bounds.right - bounds.left),
                static_cast<float>(bounds.bottom - bounds.top), dpi / 96.0f);
        }
        PostWake();
    }

    void AppearanceChanged()
    {
        PostWake();
    }

    bool MouseInput(UINT message, WPARAM keys, LPARAM position)
    {
        (void)keys;
        POINT point{ static_cast<short>(LOWORD(position)), static_cast<short>(HIWORD(position)) };
        if (message == WM_CANCELMODE || message == WM_CAPTURECHANGED)
        {
            pointerDown = false;
            if (message == WM_CANCELMODE && ::GetCapture() == owner)
                ::ReleaseCapture();
            return false;
        }
        if (message == WM_LBUTTONDOWN || message == WM_LBUTTONDBLCLK)
        {
            pointerDown = true;
            pointerOrigin = point;
            pointerSource = state.source;
            ++gestureSerial;
            if (clickLogs++ < 192)
                Log(L"Panel gesture=" + std::to_wstring(gestureSerial) + L" down");
            ::SetCapture(owner);
            return true;
        }
        if (message == WM_LBUTTONUP)
        {
            bool activate = pointerDown && ::GetCapture() == owner;
            pointerDown = false;
            if (::GetCapture() == owner) ::ReleaseCapture();
            RECT bounds{}; GetClientRect(owner, &bounds);
            activate = activate && PtInRect(&bounds, point) &&
                std::abs(point.x - pointerOrigin.x) <= GetSystemMetrics(SM_CXDRAG) &&
                std::abs(point.y - pointerOrigin.y) <= GetSystemMetrics(SM_CYDRAG);
            if (activate)
            {
                NativeMedia::HitAction action{ NativeMedia::HitAction::Panel };
                const bool hit = renderer.HitTest(static_cast<float>(point.x),
                    static_cast<float>(point.y), action);
                if (hit && action != NativeMedia::HitAction::None &&
                    pointerSource == state.source)
                {
                    if (action == NativeMedia::HitAction::Previous) Send(Command(L"previous"));
                    else if (action == NativeMedia::HitAction::PlayPause) Send(Command(L"playPause"));
                    else if (action == NativeMedia::HitAction::Next) Send(Command(L"next"));
                    else Panel(gestureSerial);
                }
                else if (hit && action == NativeMedia::HitAction::None)
                {
                    // A disabled browser control consumes the gesture without opening the card.
                }
                else Panel(gestureSerial);
            }
            return true;
        }
        return false;
    }
};

std::shared_ptr<NativeMediaHost::Impl>& NativeMediaHost::Shared()
{
    static std::shared_ptr<Impl> instance;
    return instance;
}

NativeMediaHost::NativeMediaHost(HWND owner)
{
    auto& instance = Shared();
    if (!instance)
    {
        instance = std::make_shared<Impl>(owner);
        auto impl = instance.get();
        impl->worker = std::thread([impl] { impl->Run(); });
    }
    m_impl = instance;
    m_impl->owner = owner;
    m_impl->wakeOwner = owner;
    m_impl->renderer.SetHost(owner);
    m_impl->rendererNeedsState = true;
    m_impl->announcePending = true;
    m_impl->PostWake();
}

NativeMediaHost::~NativeMediaHost()
{
    if (!m_impl) return;
    m_impl->renderer.SetVisible(false, false);
    m_impl->renderer.DetachHost();
    m_impl->EnsureTimer(false, false);
    m_impl->visible = false;
    m_impl->mediaPresented = false;
    m_impl->owner = nullptr;
    m_impl->wakeOwner = nullptr;
}

void NativeMediaHost::Shutdown()
{
    auto& instance = Shared();
    if (instance)
    {
        instance->stop = true;
        if (instance->workerWake) SetEvent(instance->workerWake);
    }
    instance.reset();
}

void NativeMediaHost::OpenSettings(const wchar_t* page)
{
    if (auto instance = Shared())
    {
        auto command = Command(L"openSettings");
        if (page) command.SetNamedValue(L"page", JsonValue::CreateStringValue(page));
        instance->Send(command);
    }
}

void NativeMediaHost::Tick() { if (m_impl) m_impl->Tick(); }
void NativeMediaHost::Wheel(int delta) { if (m_impl) m_impl->Wheel(delta); }
void NativeMediaHost::TogglePanel() { if (m_impl) m_impl->Panel(); }
void NativeMediaHost::Menu() { if (m_impl) m_impl->ShowMenu(); }
void NativeMediaHost::GeometryChanged() { if (m_impl) m_impl->GeometryChanged(); }
void NativeMediaHost::AppearanceChanged() { if (m_impl) m_impl->AppearanceChanged(); }
bool NativeMediaHost::IsVisible() const { return m_impl && m_impl->visible; }

bool NativeMediaHost::MouseInput(UINT message, WPARAM keys, LPARAM position)
{
    return m_impl && m_impl->MouseInput(message, keys, position);
}
