#include "stdafx.h"
#include "auto_start_helper.h"
#include <comdef.h>
#include <taskschd.h>
#include <sddl.h>
#include <wrl/client.h>
#include <filesystem>
#pragma comment(lib, "taskschd.lib")
#pragma comment(lib, "advapi32.lib")

namespace
{
    using Microsoft::WRL::ComPtr;
    constexpr auto TaskFolder = L"\\TaskBarWidgets";
    constexpr auto RunKey = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    constexpr auto RunValue = L"TaskBarWidgets";

    bool Missing(HRESULT result)
    {
        return result == HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND) || result == HRESULT_FROM_WIN32(ERROR_PATH_NOT_FOUND);
    }
    HRESULT CurrentUser(std::wstring& sid, std::wstring& name)
    {
        wchar_t user[257]{}; DWORD size = _countof(user);
        if (!GetUserNameW(user, &size)) return HRESULT_FROM_WIN32(GetLastError());
        name = L"Autorun for "; name += user;
        HANDLE token{};
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return HRESULT_FROM_WIN32(GetLastError());
        DWORD bytes{}; GetTokenInformation(token, TokenUser, nullptr, 0, &bytes);
        std::vector<BYTE> data(bytes);
        const BOOL read = GetTokenInformation(token, TokenUser, data.data(), bytes, &bytes);
        const DWORD error = read ? ERROR_SUCCESS : GetLastError(); CloseHandle(token);
        if (!read) return HRESULT_FROM_WIN32(error);
        LPWSTR value{};
        if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(data.data())->User.Sid, &value)) return HRESULT_FROM_WIN32(GetLastError());
        sid = value; LocalFree(value); return S_OK;
    }
    HRESULT Connect(ComPtr<ITaskService>& service)
    {
        HRESULT result = CoCreateInstance(CLSID_TaskScheduler, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&service));
        return FAILED(result) ? result : service->Connect(_variant_t(), _variant_t(), _variant_t(), _variant_t());
    }
    std::wstring ReadString(BSTR value) { return value ? std::wstring(value, SysStringLen(value)) : std::wstring(); }
    bool SamePath(const std::wstring& left, const std::wstring& right) { return _wcsicmp(left.c_str(), right.c_str()) == 0; }
    bool SameUser(const std::wstring& user, const std::wstring& sid)
    {
        if (user == sid) return true;
        // Task Scheduler's getter resolves a stored SID to an account name on some Windows versions.
        DWORD bytes{}, domainSize{}; SID_NAME_USE use{};
        LookupAccountNameW(nullptr, user.c_str(), nullptr, &bytes, nullptr, &domainSize, &use);
        if (GetLastError() != ERROR_INSUFFICIENT_BUFFER) return false;
        std::vector<BYTE> accountSid(bytes); std::vector<wchar_t> domain(domainSize);
        if (!LookupAccountNameW(nullptr, user.c_str(), accountSid.data(), &bytes, domain.data(), &domainSize, &use)) return false;
        LPWSTR value{};
        if (!ConvertSidToStringSidW(accountSid.data(), &value)) return false;
        const bool match = sid == value; LocalFree(value); return match;
    }

    // Build and inspect definitions separately from registration: tests never write startup entries.
    HRESULT BuildDefinition(ITaskService* service, const std::wstring& sid, const std::wstring& path,
        const std::wstring& directory, ComPtr<ITaskDefinition>& task)
    {
#define STARTUP_CHECK(call) do { const HRESULT result = (call); if (FAILED(result)) return result; } while (false)
        STARTUP_CHECK(service->NewTask(0, &task));
        ComPtr<IRegistrationInfo> registration; STARTUP_CHECK(task->get_RegistrationInfo(&registration));
        STARTUP_CHECK(registration->put_Author(_bstr_t(L"TaskBarWidgets")));
        ComPtr<ITaskSettings> settings; STARTUP_CHECK(task->get_Settings(&settings));
        STARTUP_CHECK(settings->put_Enabled(VARIANT_TRUE));
        STARTUP_CHECK(settings->put_StartWhenAvailable(VARIANT_TRUE));
        STARTUP_CHECK(settings->put_StopIfGoingOnBatteries(VARIANT_FALSE));
        STARTUP_CHECK(settings->put_DisallowStartIfOnBatteries(VARIANT_FALSE));
        STARTUP_CHECK(settings->put_ExecutionTimeLimit(_bstr_t(L"PT0S")));
        STARTUP_CHECK(settings->put_MultipleInstances(TASK_INSTANCES_IGNORE_NEW));
        ComPtr<IPrincipal> principal; STARTUP_CHECK(task->get_Principal(&principal));
        STARTUP_CHECK(principal->put_UserId(_bstr_t(sid.c_str())));
        STARTUP_CHECK(principal->put_LogonType(TASK_LOGON_INTERACTIVE_TOKEN));
        STARTUP_CHECK(principal->put_RunLevel(TASK_RUNLEVEL_HIGHEST));
        ComPtr<ITriggerCollection> triggers; STARTUP_CHECK(task->get_Triggers(&triggers));
        ComPtr<ITrigger> trigger; STARTUP_CHECK(triggers->Create(TASK_TRIGGER_LOGON, &trigger));
        ComPtr<ILogonTrigger> logon; STARTUP_CHECK(trigger.As(&logon));
        STARTUP_CHECK(logon->put_UserId(_bstr_t(sid.c_str())));
        STARTUP_CHECK(logon->put_Enabled(VARIANT_TRUE));
        STARTUP_CHECK(logon->put_Delay(_bstr_t(L"PT10S")));
        ComPtr<IActionCollection> actions; STARTUP_CHECK(task->get_Actions(&actions));
        ComPtr<IAction> action; STARTUP_CHECK(actions->Create(TASK_ACTION_EXEC, &action));
        ComPtr<IExecAction> exec; STARTUP_CHECK(action.As(&exec));
        STARTUP_CHECK(exec->put_Path(_bstr_t(path.c_str())));
        STARTUP_CHECK(exec->put_Arguments(_bstr_t(L"")));
        STARTUP_CHECK(exec->put_WorkingDirectory(_bstr_t(directory.c_str())));
        return S_OK;
    }
    HRESULT InspectDefinition(ITaskDefinition* task, const std::wstring& sid, StartupRegistrationState& state)
    {
        state.taskValid = false;
        ComPtr<IActionCollection> actions; STARTUP_CHECK(task->get_Actions(&actions));
        LONG count{}; STARTUP_CHECK(actions->get_Count(&count));
        if (count < 1) return S_OK;
        ComPtr<IAction> action; STARTUP_CHECK(actions->get_Item(1, &action));
        ComPtr<IExecAction> exec;
        if (FAILED(action.As(&exec))) return S_OK;
        _bstr_t path, directory, arguments;
        STARTUP_CHECK(exec->get_Path(path.GetAddress()));
        STARTUP_CHECK(exec->get_WorkingDirectory(directory.GetAddress()));
        STARTUP_CHECK(exec->get_Arguments(arguments.GetAddress()));
        state.taskPath = ReadString(path); state.workingDirectory = ReadString(directory);
        if (count != 1) return S_OK;
        if (!SamePath(state.taskPath, state.currentPath) || !SamePath(state.workingDirectory, state.currentDirectory) || arguments.length()) return S_OK;
        ComPtr<IPrincipal> principal; STARTUP_CHECK(task->get_Principal(&principal));
        TASK_RUNLEVEL_TYPE level{}; TASK_LOGON_TYPE type{}; _bstr_t user;
        STARTUP_CHECK(principal->get_RunLevel(&level)); STARTUP_CHECK(principal->get_LogonType(&type));
        STARTUP_CHECK(principal->get_UserId(user.GetAddress()));
        if (level != TASK_RUNLEVEL_HIGHEST || type != TASK_LOGON_INTERACTIVE_TOKEN || !SameUser(ReadString(user), sid)) return S_OK;
        ComPtr<ITriggerCollection> triggers; STARTUP_CHECK(task->get_Triggers(&triggers));
        STARTUP_CHECK(triggers->get_Count(&count)); if (count != 1) return S_OK;
        ComPtr<ITrigger> trigger; STARTUP_CHECK(triggers->get_Item(1, &trigger));
        ComPtr<ILogonTrigger> logon; if (FAILED(trigger.As(&logon))) return S_OK;
        _bstr_t triggerUser, delay; VARIANT_BOOL enabled{};
        STARTUP_CHECK(logon->get_UserId(triggerUser.GetAddress())); STARTUP_CHECK(logon->get_Delay(delay.GetAddress()));
        STARTUP_CHECK(logon->get_Enabled(&enabled));
        if (!SameUser(ReadString(triggerUser), sid) || ReadString(delay) != L"PT10S" || enabled != VARIANT_TRUE) return S_OK;
        ComPtr<ITaskSettings> settings; STARTUP_CHECK(task->get_Settings(&settings));
        VARIANT_BOOL startsAvailable{}, stopsOnBattery{}, batteryBlocked{}, settingsEnabled{};
        TASK_INSTANCES_POLICY instances{}; _bstr_t limit;
        STARTUP_CHECK(settings->get_StartWhenAvailable(&startsAvailable));
        STARTUP_CHECK(settings->get_StopIfGoingOnBatteries(&stopsOnBattery));
        STARTUP_CHECK(settings->get_DisallowStartIfOnBatteries(&batteryBlocked));
        STARTUP_CHECK(settings->get_Enabled(&settingsEnabled));
        STARTUP_CHECK(settings->get_ExecutionTimeLimit(limit.GetAddress()));
        STARTUP_CHECK(settings->get_MultipleInstances(&instances));
        state.taskValid = startsAvailable == VARIANT_TRUE && stopsOnBattery == VARIANT_FALSE && batteryBlocked == VARIANT_FALSE
            && settingsEnabled == VARIANT_TRUE && ReadString(limit) == L"PT0S" && instances == TASK_INSTANCES_IGNORE_NEW;
        return S_OK;
#undef STARTUP_CHECK
    }
}

StartupRegistrationState get_startup_registration_for_this_user()
{
    StartupRegistrationState state;
    wchar_t module[32768]{};
    const DWORD length = GetModuleFileNameW(nullptr, module, _countof(module));
    if (!length || length >= _countof(module)) { state.error = HRESULT_FROM_WIN32(length ? ERROR_INSUFFICIENT_BUFFER : GetLastError()); return state; }
    state.currentPath.assign(module, length);
    state.currentDirectory = std::filesystem::path(state.currentPath).parent_path().wstring();
    DWORD type{}, bytes{};
    LSTATUS registry = RegGetValueW(HKEY_CURRENT_USER, RunKey, RunValue, RRF_RT_REG_SZ, &type, nullptr, &bytes);
    if (registry == ERROR_SUCCESS)
    {
        std::vector<wchar_t> value(bytes / sizeof(wchar_t) + 1);
        registry = RegGetValueW(HKEY_CURRENT_USER, RunKey, RunValue, RRF_RT_REG_SZ, &type, value.data(), &bytes);
        if (registry == ERROR_SUCCESS) { state.registryExists = true; state.legacyPath = value.data(); }
    }
    if (registry != ERROR_SUCCESS && registry != ERROR_FILE_NOT_FOUND && registry != ERROR_PATH_NOT_FOUND) state.error = HRESULT_FROM_WIN32(registry);
    std::wstring sid, name; HRESULT result = CurrentUser(sid, name);
    ComPtr<ITaskService> service; if (SUCCEEDED(result)) result = Connect(service);
    ComPtr<ITaskFolder> folder; if (SUCCEEDED(result)) result = service->GetFolder(_bstr_t(TaskFolder), &folder);
    ComPtr<IRegisteredTask> registered; if (SUCCEEDED(result)) result = folder->GetTask(_bstr_t(name.c_str()), &registered);
    if (Missing(result)) return state;
    if (SUCCEEDED(result))
    {
        state.taskExists = true;
        VARIANT_BOOL enabled{}; result = registered->get_Enabled(&enabled); state.taskEnabled = enabled == VARIANT_TRUE;
        ComPtr<ITaskDefinition> definition;
        if (SUCCEEDED(result)) result = registered->get_Definition(&definition);
        if (SUCCEEDED(result)) result = InspectDefinition(definition.Get(), sid, state);
    }
    if (FAILED(result)) state.error = result;
    return state;
}

bool create_auto_start_task_for_this_user(bool /*runElevated*/)
{
    const auto current = get_startup_registration_for_this_user();
    if (FAILED(current.error)) { SetLastError(HRESULT_CODE(current.error)); return false; }
    std::wstring sid, name; HRESULT result = CurrentUser(sid, name);
    ComPtr<ITaskService> service; if (SUCCEEDED(result)) result = Connect(service);
    ComPtr<ITaskFolder> folder;
    if (SUCCEEDED(result))
    {
        result = service->GetFolder(_bstr_t(TaskFolder), &folder);
        if (Missing(result))
        {
            ComPtr<ITaskFolder> root; result = service->GetFolder(_bstr_t(L"\\"), &root);
            if (SUCCEEDED(result)) result = root->CreateFolder(_bstr_t(TaskFolder), _variant_t(), &folder);
        }
    }
    ComPtr<ITaskDefinition> definition;
    if (SUCCEEDED(result)) result = BuildDefinition(service.Get(), sid, current.currentPath, current.currentDirectory, definition);
    ComPtr<IRegisteredTask> registered;
    // Correct the former Everyone full-access DACL during upgrades as well as new registrations.
    const std::wstring security = L"D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;" + sid + L")";
    if (SUCCEEDED(result)) result = folder->RegisterTaskDefinition(_bstr_t(name.c_str()), definition.Get(), TASK_CREATE_OR_UPDATE,
        _variant_t(sid.c_str()), _variant_t(), TASK_LOGON_INTERACTIVE_TOKEN, _variant_t(security.c_str()), &registered);
    if (SUCCEEDED(result))
    {
        const auto readback = get_startup_registration_for_this_user();
        result = FAILED(readback.error) ? readback.error : (readback.Enabled() ? S_OK : HRESULT_FROM_WIN32(ERROR_INVALID_DATA));
    }
    SetLastError(SUCCEEDED(result) ? ERROR_SUCCESS : HRESULT_CODE(result));
    return SUCCEEDED(result);
}

bool delete_auto_start_task_for_this_user()
{
    std::wstring sid, name; HRESULT result = CurrentUser(sid, name);
    ComPtr<ITaskService> service; if (SUCCEEDED(result)) result = Connect(service);
    ComPtr<ITaskFolder> folder; if (SUCCEEDED(result)) result = service->GetFolder(_bstr_t(TaskFolder), &folder);
    if (SUCCEEDED(result)) result = folder->DeleteTask(_bstr_t(name.c_str()), 0);
    if (Missing(result)) result = S_OK;
    SetLastError(SUCCEEDED(result) ? ERROR_SUCCESS : HRESULT_CODE(result));
    return SUCCEEDED(result);
}

bool delete_legacy_auto_start_for_this_user()
{
    const LSTATUS result = RegDeleteKeyValueW(HKEY_CURRENT_USER, RunKey, RunValue);
    const bool success = result == ERROR_SUCCESS || result == ERROR_FILE_NOT_FOUND || result == ERROR_PATH_NOT_FOUND;
    SetLastError(success ? ERROR_SUCCESS : result); return success;
}

bool is_auto_start_task_active_for_this_user(std::wstring* path)
{
    const auto state = get_startup_registration_for_this_user();
    if (path) *path = state.taskPath;
    return SUCCEEDED(state.error) && state.Enabled();
}
