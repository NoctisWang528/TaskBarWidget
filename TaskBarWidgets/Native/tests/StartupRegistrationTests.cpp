// Constructs task definitions in memory. Never registers tasks, changes Run, or launches the product.
#include "../TrafficMonitor/auto_start_helper.cpp"
#include <cassert>
#include <iostream>
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "oleaut32.lib")
#pragma comment(lib, "comsuppw.lib")

int main()
{
    assert(SUCCEEDED(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED)));
    {
        ComPtr<ITaskService> service;
        assert(SUCCEEDED(Connect(service))); // Read-only connection; NewTask creates an in-memory object.
        std::wstring sid, name;
        assert(SUCCEEDED(CurrentUser(sid, name)));
        StartupRegistrationState state;
        state.currentPath = L"C:\\startup test\\TaskBarWidgets.exe";
        state.currentDirectory = L"C:\\startup test";
        state.taskExists = state.taskEnabled = true;
        ComPtr<ITaskDefinition> definition;
        assert(SUCCEEDED(BuildDefinition(service.Get(), sid, state.currentPath, state.currentDirectory, definition)));
        auto inspect = [&] { assert(SUCCEEDED(InspectDefinition(definition.Get(), sid, state))); return state.Enabled(); };
        assert(inspect()); assert(!state.NeedsRepair());

        ComPtr<IActionCollection> actions; assert(SUCCEEDED(definition->get_Actions(&actions)));
        ComPtr<IAction> action; assert(SUCCEEDED(actions->get_Item(1, &action)));
        ComPtr<IExecAction> exec; assert(SUCCEEDED(action.As(&exec)));
        assert(SUCCEEDED(exec->put_Path(_bstr_t(L"C:\\old copy\\TaskBarWidgets.exe"))));
        assert(!inspect()); assert(state.NeedsRepair());
        assert(state.taskPath == L"C:\\old copy\\TaskBarWidgets.exe");
        assert(SUCCEEDED(exec->put_Path(_bstr_t(state.currentPath.c_str()))));
        assert(SUCCEEDED(exec->put_WorkingDirectory(_bstr_t(L"C:\\old copy")))); assert(!inspect());
        assert(SUCCEEDED(exec->put_WorkingDirectory(_bstr_t(state.currentDirectory.c_str()))));
        assert(SUCCEEDED(exec->put_Arguments(_bstr_t(L"--unexpected")))); assert(!inspect());
        assert(SUCCEEDED(exec->put_Arguments(_bstr_t(L"")))); assert(inspect());

        ComPtr<IPrincipal> principal; assert(SUCCEEDED(definition->get_Principal(&principal)));
        assert(SUCCEEDED(principal->put_RunLevel(TASK_RUNLEVEL_LUA))); assert(!inspect());
        assert(SUCCEEDED(principal->put_RunLevel(TASK_RUNLEVEL_HIGHEST)));
        assert(SUCCEEDED(principal->put_LogonType(TASK_LOGON_SERVICE_ACCOUNT))); assert(!inspect());
        assert(SUCCEEDED(principal->put_LogonType(TASK_LOGON_INTERACTIVE_TOKEN))); assert(inspect());

        ComPtr<ITriggerCollection> triggers; assert(SUCCEEDED(definition->get_Triggers(&triggers)));
        ComPtr<ITrigger> trigger; assert(SUCCEEDED(triggers->get_Item(1, &trigger)));
        ComPtr<ILogonTrigger> logon; assert(SUCCEEDED(trigger.As(&logon)));
        assert(SUCCEEDED(logon->put_Delay(_bstr_t(L"PT0S")))); assert(!inspect());
        assert(SUCCEEDED(logon->put_Delay(_bstr_t(L"PT10S"))));
        assert(SUCCEEDED(logon->put_Enabled(VARIANT_FALSE))); assert(!inspect());
        assert(SUCCEEDED(logon->put_Enabled(VARIANT_TRUE))); assert(inspect());

        ComPtr<ITaskSettings> settings; assert(SUCCEEDED(definition->get_Settings(&settings)));
        assert(SUCCEEDED(settings->put_DisallowStartIfOnBatteries(VARIANT_TRUE))); assert(!inspect());
        assert(SUCCEEDED(settings->put_DisallowStartIfOnBatteries(VARIANT_FALSE))); assert(inspect());
        assert(SUCCEEDED(settings->put_Enabled(VARIANT_FALSE))); assert(!inspect());
        assert(SUCCEEDED(settings->put_Enabled(VARIANT_TRUE))); assert(inspect());

        state.taskEnabled = false; assert(!state.Enabled()); assert(state.NeedsRepair()); state.taskEnabled = true;
        state.registryExists = true; assert(state.Enabled()); assert(state.NeedsRepair()); state.registryExists = false;
        ComPtr<IAction> extra; assert(SUCCEEDED(actions->Create(TASK_ACTION_EXEC, &extra))); assert(!inspect());
        assert(!Missing(E_ACCESSDENIED)); assert(!Missing(RPC_E_DISCONNECTED));
        assert(Missing(HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)));
    }
    CoUninitialize();
    std::cout << "In-memory startup task definition regressions passed; no startup entries changed.\n";
}
