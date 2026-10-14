#pragma once
#include "../TrafficMonitor/PluginIniOptions.h"
#include "../include/OpenHardwareMonitor/PowerSensorTotals.h"
#include <cassert>

inline void TestBuiltinIniOptions()
{
    using namespace PluginIniOptions;
    std::string bytes = "\xef\xbb\xbf[config]\r\nwit=0\r\nshow_weather_icon=true ; keep\r\ncity_name=Taipei\r\n[hfw]\r\nAppKey=private-test-token\r\n";
    const auto secret = bytes.substr(bytes.find("[hfw]"));
    assert(SetValue(bytes, "config", "wit", "3"));
    assert(SetValue(bytes, "config", "city_name", "New Taipei"));
    assert(SetBoolean(bytes, "config", "show_weather_icon", false));
    assert(bytes.find("wit=3\r\n") != std::string::npos);
    assert(bytes.find("show_weather_icon=false ; keep\r\n") != std::string::npos);
    assert(bytes.substr(bytes.find("[hfw]")) == secret);
    const auto saved = bytes;
    assert(!SetValue(bytes, "config", "city_name", "bad\r\nAppKey=overwrite"));
    assert(bytes == saved);
    ValueToken token;
    assert(!FindValue("[config]\nwit=0\nwit=1\n", "config", "wit", token));
    assert(!FindValue(std::string("[config]\nwit=\0", 14), "config", "wit", token));
    assert(!SetValue(bytes, "config", "missing", "1"));
    assert(EnsureValue(bytes, "config", "missing", "2"));
    assert(FindValue(bytes, "config", "missing", token) && token.value == "2");
    assert(bytes.substr(bytes.find("[hfw]")) == secret);
    assert(EnsureValue(bytes, "new", "enabled", "1"));
    assert(FindValue(bytes, "new", "enabled", token) && token.value == "1");
    std::string duplicate = "[config]\nwit=0\nwit=1\n";
    const auto unchanged = duplicate;
    assert(!EnsureValue(duplicate, "config", "wit", "2") && duplicate == unchanged);
    PowerSensorTotals power;
    assert(std::isnan(power.Value()));
    power.Add(L"CPU Cores", 10, false); power.Add(L"CPU SoC", 5, false);
    assert(power.Value() == 15);
    power.Add(L"CPU Package", 20, false);
    assert(power.Value() == 20);
    power.Add(L"CPU Platform", 30, true);
    assert(power.Value() == 30);
    const auto missing = std::numeric_limits<double>::quiet_NaN();
    assert(PowerSensorTotals::Combine(12, missing) == 12);
    assert(PowerSensorTotals::Combine(missing, 8) == 8);
    assert(std::isnan(PowerSensorTotals::Combine(missing, missing)));
}
