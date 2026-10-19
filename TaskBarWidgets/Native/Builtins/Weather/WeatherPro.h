#pragma once

#include "PluginInterface.h"
#include <string>
#include <ctime>

#include "WeatherProItem.h"

class CWeatherPro : public ITMPlugin
{
private:
    CWeatherPro();

public:
    static CWeatherPro& Instance();

    IPluginItem* GetItem(int index) override;
    void DataRequired() override;
    const wchar_t* GetInfo(PluginInfoIndex index) override;
    OptionReturn ShowOptionsDialog(void* hParent) override;
    void OnExtenedInfo(ExtendedInfoIndex index, const wchar_t* data) override;

    void UpdateWeatherInfo(bool force = false);

    std::time_t GetLastUpdateTimestamp() const;

private:

    CWeatherProItem m_item;
    std::time_t m_last_update_timestamp;
    std::time_t m_next_update_time_span;
};

#ifdef __cplusplus
extern "C" {
#endif
     ITMPlugin* WeatherTMPluginGetInstance();

#ifdef __cplusplus
}
#endif
