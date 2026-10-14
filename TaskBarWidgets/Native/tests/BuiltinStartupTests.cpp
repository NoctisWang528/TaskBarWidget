#include <afxwin.h>
#include "../Builtins/BuiltinFeatures.h"
#include "../TrafficMonitor/resource.h"
#include <cmath>
#include <iostream>
#include <set>
#include <string>

// Link the production provider objects into a console process. No taskbar,
// settings files, network requests, audio changes or hardware monitor starts.
CWinApp testApplication;
double BuiltinHardwarePower(bool, bool, bool) { return NAN; }

int wmain()
{
    if (!AfxWinInit(GetModuleHandleW(nullptr), nullptr, GetCommandLineW(), SW_HIDE)) return 1;
    AfxSetResourceHandle(GetModuleHandleW(nullptr));
    const auto battery = BatteryTMPluginGetInstance();
    const auto power = PowerTMPluginGetInstance();
    const auto weather = WeatherTMPluginGetInstance();
    if (battery != BatteryTMPluginGetInstance() || power != PowerTMPluginGetInstance() ||
        weather != WeatherTMPluginGetInstance()) return 2;
    std::set<std::wstring> ids;
    for (auto* feature : {battery, power, weather})
    {
        if (!feature || !feature->GetInfo(ITMPlugin::TMI_NAME) || !*feature->GetInfo(ITMPlugin::TMI_NAME))
        {
            std::cerr << "Missing feature name: " << (feature == battery ? "battery" : feature == power ? "power" : "weather") << "\n";
            wchar_t name[256]{};
            std::cerr << "Direct Battery resource length=" << LoadStringW(GetModuleHandleW(nullptr),40103,name,256) << "\n";
            return 3;
        }
        for (int index = 0; auto* item = feature->GetItem(index); ++index)
        {
            if (index > 16 || !item->GetItemId() || !ids.insert(item->GetItemId()).second) return 4;
            if (!item->GetItemName() || !*item->GetItemName()) return 5;
            (void)item->GetItemValueSampleText();
        }
    }
    if (ids.size() != 10 || !ids.contains(L"b5R30ITQ") || !ids.contains(L"FxOP34Lm")) return 6;
    // Also prove the generated timestamp resource no longer resolves to the
    // legacy placeholder or drops its last character.
    HRSRC resource = FindResourceW(nullptr, MAKEINTRESOURCEW(IDR_COMPILE_TIME), L"TEXT");
    if (!resource) return 7;
    auto* data = static_cast<const char*>(LockResource(LoadResource(nullptr, resource)));
    if (!data) return 8;
    std::string timestamp(data, SizeofResource(nullptr, resource));
    std::cerr << "Timestamp resource bytes=" << timestamp.size() << " value=[" << timestamp << "]\n";
    if (timestamp.size() != 26 || timestamp[4] != '-' || timestamp[7] != '-' || timestamp[10] != ' ' || timestamp[19] != ' ') return 9;
    std::cout << "Embedded build timestamp: " << timestamp << "\n";
    std::cout << "Production built-in construction, MFC resources and display IDs passed.\n";
    return 0;
}
