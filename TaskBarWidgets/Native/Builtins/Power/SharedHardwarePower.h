#pragma once
#include "DataManager.h"
#include "ValueUnitStringFormatter.h"
#include <cmath>
#include "../../include/OpenHardwareMonitor/PowerSensorTotals.h"

// Share the product's already-running hardware monitor; never start another CLR host.
double BuiltinHardwarePower(bool gpu, bool enable, bool smart = false);
namespace InterOpLibreHWMon
{
    class HardwareSensorDataProvider
    {
        bool m_cpu{}, m_gpu{};
        double m_cpuPower{}, m_gpuPower{};
        double m_smartPower{};
    public:
        int SyncFromSettingData(const PowerSettingData& settings)
        { m_cpu = settings.enable_cpu_monitor; m_gpu = settings.enable_gpu_monitor; return 0; }
        int CallUpdateInfo()
        {
            m_cpuPower = BuiltinHardwarePower(false, m_cpu); m_gpuPower = BuiltinHardwarePower(true, m_gpu);
            m_smartPower = PowerSensorTotals::Combine(BuiltinHardwarePower(false, m_cpu, true), BuiltinHardwarePower(true, m_gpu, true));
            return 0;
        }
        double GetCPUTotalPower(bool = false) const { return m_cpuPower; }
        double GetGPUTotalPower(bool = false) const { return m_gpuPower; }
        double GetSmartCaculatePowerMeter() const
        { return m_smartPower; }
        std::wstring GetPowerSummaryStr(const ValueUnitStringFormatter&) const { return {}; }
    };
}
