#include "stdafx.h"
#include "TrafficMonitor.h"
#include <limits>

double BuiltinHardwarePower(bool gpu, bool enable, bool smart)
{
#ifndef WITHOUT_TEMPERATURE
    CSingleLock lock(&theApp.m_minitor_lib_critical, TRUE);
    if (enable && theApp.m_pMonitor)
    {
        return OpenHardwareMonitorApi::ReadPower(gpu, smart);
    }
#endif
    return std::numeric_limits<double>::quiet_NaN();
}
