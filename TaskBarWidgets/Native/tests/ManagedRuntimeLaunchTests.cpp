#include "../TrafficMonitor/DotnetDependencyCheck.h"
#include "../TrafficMonitor/WinUiDependencyCheck.h"
#include <fstream>
#include <roapi.h>
#include <cassert>
#include <iostream>

int wmain(int argc, wchar_t** argv)
{
    assert(argc == 2);
    const std::filesystem::path root(argv[1]);
    // Deliberately poison this test process's setting. Production must override it for its child only.
    SetEnvironmentVariableW(L"DOTNET_ROOT_X64", L"Z:\\not-a-runtime");
    const auto runtime = SystemDotnetRoot();
    SetEnvironmentVariableW(L"DOTNET_ROLL_FORWARD", L"LatestMajor");
    const auto config = root / L"Media/TaskBarWidgets.Media.runtimeconfig.json";
    auto checked = CheckDotnetRuntime(runtime, config);
    if (!checked.success) std::wcerr << checked.error << L"\n";
    assert(checked.success);
    assert(CheckDotnetRuntime(runtime, root / L"UI/TaskBarWidgets.UI.runtimeconfig.json").success);
    assert(!CheckDotnetRuntime(root / L"missing-dotnet-test", config).success);
    const auto unavailable = root / L"unavailable-runtime-check.json";
    { std::ofstream file(unavailable); file << R"({"runtimeOptions":{"tfm":"net99.0","rollForward":"LatestPatch","framework":{"name":"Microsoft.WindowsDesktop.App","version":"99.0.0"}}})"; }
    assert(!CheckDotnetRuntime(runtime, unavailable).success);
    std::filesystem::remove(unavailable);
    const auto broken = root / L"invalid-runtime-check.json";
    { std::ofstream file(broken); file << "invalid-json"; }
    assert(!CheckDotnetRuntime(runtime, broken).success);
    std::filesystem::remove(broken);
    assert(SUCCEEDED(RoInitialize(RO_INIT_SINGLETHREADED)));
    const auto bootstrap = root / L"UI/Microsoft.WindowsAppRuntime.Bootstrap.dll";
    const auto winui = CheckWinUiRuntime(bootstrap);
    std::cout << "WinUI bootstrap status: " << std::hex << winui << "\n";
    assert(SUCCEEDED(winui));
    assert(FAILED(CheckWinUiRuntime(root / L"missing-bootstrap.dll")));
    assert(FAILED(CheckWinUiRuntime(bootstrap, 0x00630000, 0x0063000000000000ULL)));
    RoUninitialize();
    auto environment = ManagedEnvironment(runtime);
    assert(!environment.empty());
    std::wstring executable = (root / L"Media" / L"TaskBarWidgets.Media.exe").wstring();
    std::wstring command = L"\"" + executable + L"\" --validate-package \"" + (root / L"native-runtime-check.json").wstring() + L"\"";
    STARTUPINFOW startup{sizeof(startup)};
    startup.dwFlags = STARTF_USESHOWWINDOW; startup.wShowWindow = SW_HIDE;
    PROCESS_INFORMATION child{};
    assert(CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW,
        environment.data(), root.c_str(), &startup, &child));
    CloseHandle(child.hThread);
    if (WaitForSingleObject(child.hProcess, 20000) != WAIT_OBJECT_0) { TerminateProcess(child.hProcess, 1); CloseHandle(child.hProcess); return 1; }
    DWORD result{}; GetExitCodeProcess(child.hProcess, &result); CloseHandle(child.hProcess);
    wchar_t value[256]{}; GetEnvironmentVariableW(L"DOTNET_ROOT_X64", value, 256);
    assert(std::wstring(value) == L"Z:\\not-a-runtime");
    assert(result == 0);
    GetEnvironmentVariableW(L"DOTNET_ROLL_FORWARD", value, 256);
    assert(std::wstring(value) == L"LatestMajor");
    std::cout << "Shared runtime checks, missing/version/config failures and child launch passed; environment restored.\n";
    return 0;
}
