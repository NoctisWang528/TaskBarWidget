#pragma once
#include "DotnetDependencyCheck.h"
#include "WinUiDependencyCheck.h"
#include <commctrl.h>
#include <shellapi.h>
#include <string>

inline bool EnsureSharedDependencies(const std::filesystem::path& root)
{
    const auto runtime = SystemDotnetRoot();
    auto dotnet = CheckDotnetRuntime(runtime, root / L"Media/TaskBarWidgets.Media.runtimeconfig.json");
    if (dotnet.success) dotnet = CheckDotnetRuntime(runtime, root / L"UI/TaskBarWidgets.UI.runtimeconfig.json");
    const auto bootstrap = root / L"UI/Microsoft.WindowsAppRuntime.Bootstrap.dll";
    const HRESULT winui = CheckWinUiRuntime(bootstrap);
    if (dotnet.success && SUCCEEDED(winui)) return true;

    const bool chinese = PRIMARYLANGID(GetUserDefaultUILanguage()) == LANG_CHINESE;
    std::wstring message;
    wchar_t code[48]{};
    if (!dotnet.success)
    {
        swprintf_s(code, L" (0x%08X)\n", static_cast<unsigned>(dotnet.errorCode));
        message += L".NET 8 Desktop Runtime x64";
        message += code;
        message += dotnet.error + L"\n\n";
    }
    if (FAILED(winui))
    {
        swprintf_s(code, L" (0x%08X)\n", static_cast<unsigned>(winui));
        message += L"Windows App Runtime 2.5.1 x64";
        message += code;
        std::error_code error;
        message += std::filesystem::is_regular_file(bootstrap, error)
            ? (chinese ? L"未找到可加载的兼容运行库，请安装或修复运行库。\n\n" : L"A compatible runtime could not be loaded. Install or repair it.\n\n")
            : (chinese ? L"应用的 WinUI 引导文件缺失，请重新完整解压应用包。\n\n" : L"The application's WinUI bootstrap file is missing. Extract the complete application package again.\n\n");
    }
    message += chinese ? L"安装后重新启动本程序。下载按钮只打开微软官网，不会自动安装。\n详细环境要求见 README.txt。"
        : L"Restart the app after installation. Download buttons open Microsoft websites only; nothing is installed automatically.\nSee README.txt for requirements.";
    const TASKDIALOG_BUTTON buttons[] = {
        {100, chinese ? L".NET 官方下载" : L"Download .NET"},
        {101, chinese ? L"WinUI 运行库官方下载" : L"Download Windows App Runtime"}};
    TASKDIALOGCONFIG dialog{sizeof(dialog)};
    dialog.dwFlags = TDF_ALLOW_DIALOG_CANCELLATION | TDF_SIZE_TO_CONTENT;
    dialog.dwCommonButtons = TDCBF_CLOSE_BUTTON;
    dialog.pszWindowTitle = L"TaskBarWidgets";
    dialog.pszMainIcon = TD_WARNING_ICON;
    dialog.pszMainInstruction = chinese ? L"运行环境缺失或无法加载" : L"Required runtime is missing or could not be loaded";
    dialog.pszContent = message.c_str();
    dialog.cButtons = ARRAYSIZE(buttons); dialog.pButtons = buttons;
    dialog.nDefaultButton = IDCLOSE;
    for (;;)
    {
        int selected{};
        if (FAILED(TaskDialogIndirect(&dialog, &selected, nullptr, nullptr)))
        {
            MessageBoxW(nullptr, message.c_str(), L"TaskBarWidgets", MB_OK | MB_ICONERROR);
            break;
        }
        if (selected != 100 && selected != 101) break;
        ShellExecuteW(nullptr, L"open", selected == 100
            ? L"https://dotnet.microsoft.com/download/dotnet/8.0"
            : L"https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads", nullptr, nullptr, SW_SHOWNORMAL);
    }
    return false;
}
