// Isolated Win32/OS WinRT fixture: never links or starts the product, hardware or audio.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include "../TrafficMonitor/UpdateHealth.h"
#include <iostream>
namespace fs = std::filesystem;
using namespace UpdateHealth;
void Check(bool condition, const char* message) { if (!condition) throw std::runtime_error(message); }
template<typename F> void Reject(F action, const char* message)
{
    bool rejected = false;
    try { action(); } catch (...) { rejected = true; }
    Check(rejected, message);
}
void Text(const fs::path& path, const std::string& value) { std::ofstream file(path, std::ios::binary); file << value; }
struct Helper
{
    HANDLE event{}, process{};
    explicit Helper(const fs::path& path)
    {
        const auto name = L"Local\\TaskBarWidgets.UpdateHealth.Test." + std::to_wstring(GetCurrentProcessId());
        event = CreateEventW(nullptr, TRUE, FALSE, name.c_str());
        Check(event && GetLastError() != ERROR_ALREADY_EXISTS, "Cannot create isolated helper event");
        std::wstring command = L"\"" + path.wstring() + L"\" --helper " + name;
        STARTUPINFOW startup{sizeof(startup)}; PROCESS_INFORMATION info{};
        Check(CreateProcessW(path.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr,
            path.parent_path().c_str(), &startup, &info), "Cannot start isolated helper fixture");
        process = info.hProcess; CloseHandle(info.hThread);
    }
    ~Helper() { if (event) SetEvent(event); if (process) { WaitForSingleObject(process, 3000); CloseHandle(process); } if (event) CloseHandle(event); }
};
int wmain(int count, wchar_t** arguments)
{
    if (count == 3 && std::wstring(arguments[1]) == L"--helper")
    {
        HANDLE event = OpenEventW(SYNCHRONIZE, FALSE, arguments[2]);
        if (!event) return 2;
        const auto result = WaitForSingleObject(event, 15000); CloseHandle(event); return result == WAIT_OBJECT_0 ? 0 : 3;
    }
    try
    {
        Check(count == 3, "Expected isolated directory and managed cache-key vector");
        winrt::init_apartment();
        const fs::path directory = Normal(arguments[1]);
        const fs::path root = directory / L"install-é-ı";
        fs::create_directories(root);
        Check(InstallationCacheKey(root) == arguments[2], "Native cache key differs from managed ToUpperInvariant/SHA256");
        const auto cache = directory / L"cache";
        const auto work = cache / InstallationCacheKey(root) / L"jobs" / L"0123456789abcdef0123456789abcdef";
        fs::create_directories(work);
        const auto helperPath = work / L"TaskBarWidgets.Updater.exe";
        wchar_t own[32768]{}; Check(GetModuleFileNameW(nullptr, own, ARRAYSIZE(own)) != 0, "Cannot locate fixture");
        fs::copy_file(own, helperPath);
        Helper helper(helperPath);
        const std::wstring nonce(64, L'a');
        Json job; job.SetNamedValue(L"protocolVersion", Value::CreateNumberValue(1));
        job.SetNamedValue(L"installRoot", Value::CreateStringValue(root.wstring()));
        job.SetNamedValue(L"workRoot", Value::CreateStringValue(work.wstring()));
        job.SetNamedValue(L"version", Value::CreateStringValue(L"261018-1700"));
        job.SetNamedValue(L"nonce", Value::CreateStringValue(nonce));
        Write(work / L"job.json", job);
        Json status; status.SetNamedValue(L"phase", Value::CreateStringValue(L"readyToExit"));
        status.SetNamedValue(L"helperPid", Value::CreateNumberValue(GetProcessId(helper.process)));
        Write(work / L"status.json", status);
        fs::current_path(directory);
        const auto relative = (work / L"job.json").lexically_relative(directory);
        Current() = VerifyInCache(relative, nonce, root, cache);
        Check(Current().path.is_absolute() && Current().work.is_absolute(), "Verified job retained relative path");
        Check(Current().work == work, "Verified job retained original parent instead of canonical parent");
        fs::current_path(root);
        Check(AwaitingHealth(), "Health should wait for UI readiness");
        Report(L"261018-1700");
        Check(!AwaitingHealth(), "Health should be reported once");
        const auto health = Read(work / L"health.json");
        Check(health.GetNamedString(L"nonce") == nonce && health.GetNamedString(L"version") == L"261018-1700" &&
            health.GetNamedBoolean(L"ready"), "Health acknowledgement is incomplete");
        Reject([&] { VerifyInCache(work / L"job.json", std::wstring(64, L'b'), root, cache); }, "Wrong nonce accepted");
        status.SetNamedValue(L"helperPid", Value::CreateNumberValue(GetCurrentProcessId()));
        Write(work / L"status.json", status);
        Reject([&] { VerifyInCache(work / L"job.json", nonce, root, cache); }, "Unrelated helper image accepted");
        status.SetNamedValue(L"helperPid", Value::CreateNumberValue(GetProcessId(helper.process)));
        Write(work / L"status.json", status);
        Reject([&] { VerifyInCache(work / L"job.json", nonce, directory / L"other-install", cache); }, "Wrong installation accepted");
        Check(InterruptedJob(cache, root).empty(), "Preflight-only job incorrectly blocks intact startup");
        Json journal; journal.SetNamedValue(L"protocolVersion", Value::CreateNumberValue(1));
        journal.SetNamedValue(L"nonce", Value::CreateStringValue(nonce));
        journal.SetNamedValue(L"phase", Value::CreateStringValue(L"installing"));
        Write(work / L"journal.json", journal);
        Check(InterruptedJob(cache, root) == work, "Interrupted installation not blocked");
        journal.SetNamedValue(L"phase", Value::CreateStringValue(L"complete")); Write(work / L"journal.json", journal);
        Check(InterruptedJob(cache, root).empty(), "Completed transaction blocks startup");
        journal.SetNamedValue(L"phase", Value::CreateStringValue(L"rolledBack")); Write(work / L"journal.json", journal);
        Check(InterruptedJob(cache, root).empty(), "Rolled back transaction blocks startup");
        Text(work / L"job.json", "broken job");
        Check(InterruptedJob(cache, root) == work, "Malformed job beside journal silently bypassed startup guard");
        Write(work / L"job.json", job);
        journal.SetNamedValue(L"nonce", Value::CreateStringValue(std::wstring(64, L'b'))); Write(work / L"journal.json", journal);
        Check(InterruptedJob(cache, root) == work, "Mismatched journal nonce bypassed startup guard");
        fs::remove(work / L"journal.json");
        status.SetNamedValue(L"phase", Value::CreateStringValue(L"installing")); Write(work / L"status.json", status);
        Check(InterruptedJob(cache, root) == work, "Missing journal during installation bypassed guard");
        Check(InterruptedJob(cache, directory / L"other-install").empty(), "Other installation cache affects this startup");
        std::cout << "PASS canonical job/helper/nonce identity, health writer, managed cache key and interrupted startup guard\n";
        return 0;
    }
    catch (const winrt::hresult_error& error) { std::cerr << winrt::to_string(error.message()) << '\n'; return 1; }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
