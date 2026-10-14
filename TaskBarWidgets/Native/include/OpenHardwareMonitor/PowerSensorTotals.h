#pragma once
#include <string>
#include <cmath>
#include <limits>

// PowerMon selection: platform when allowed, then package, then individual rails.
struct PowerSensorTotals
{
    double rails{}, package{}, platform{};
    bool found{};
    void Add(const std::wstring& name, double value, bool allowPlatform)
    {
        if (!std::isfinite(value) || value < 0) return;
        found = true;
        if (allowPlatform && name.find(L"Platform") != std::wstring::npos) platform = value;
        else if (name.find(L"Package") != std::wstring::npos) package = value;
        else rails += value;
    }
    double Value() const
    {
        if (!found) return std::numeric_limits<double>::quiet_NaN();
        if (platform > 0 && platform >= package) return platform;
        return package > 0 ? package : rails;
    }
    static double Combine(double cpu, double gpu)
    { return std::isfinite(cpu) ? cpu + (std::isfinite(gpu) ? gpu : 0) : gpu; }
};
