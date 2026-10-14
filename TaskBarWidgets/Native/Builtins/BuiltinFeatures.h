#pragma once
#include "../include/PluginInterface.h"

// Fixed source-integrated providers. The interface preserves persisted item IDs.
extern "C" ITMPlugin* BatteryTMPluginGetInstance();
extern "C" ITMPlugin* PowerTMPluginGetInstance();
extern "C" ITMPlugin* WeatherTMPluginGetInstance();
bool BuiltinCpuPowerEnabled();
bool BuiltinGpuPowerEnabled();
