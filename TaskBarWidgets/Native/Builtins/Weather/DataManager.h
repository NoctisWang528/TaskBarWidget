#pragma once
#include <map>
#include <functional>
#include <memory>
#include <thread>
#include <atomic>
#include <mutex>

#include "DataApiWeatherComCnSpider.h"
#include "DataApiHefengWeather.h"

enum class UpdateFrequency
{
    UF_3T1H,
    UF_2T1H,
    UF_1T1H,
    UF_1T2H,
    UF_1T3H,
};

enum class DataApiType
{
    API_WeatherComCnSpider,
    API_HefengWeather,
};

enum class IconType
{
    IT_WCC_BULE,
    IT_WCC_WHITE,
    IT_HFW_FILL,
    IT_HFW_HOLLOW,
};

struct SConfiguration
{
    SConfiguration();

    DataApiType m_api_type;
    EWeatherInfoType m_wit;
    UpdateFrequency m_update_frequency;
    IconType m_icon_type;
    bool m_show_weather_icon;
    bool m_show_brief_rt_weather_info;
    bool m_show_weather_alerts;
    bool m_show_brief_weather_alert_info;
    bool m_show_error_info;
    int m_double_click_action;
};

using WeatherInfoUpdatedCallback = std::function<void(const std::wstring & info)>;

class WeatherCDataManager
{
private:
    WeatherCDataManager();
    ~WeatherCDataManager();
public:
    static const WeatherCDataManager& Instance();
    static WeatherCDataManager& InstanceRef();

    const CString& StringRes(UINT id) const;

    int DPI(int pixel) const;
    float DPIF(float pixel) const;
    int RDPI(int pixel) const;

    void SetCurrentCityInfo(SCityInfo info);
    SCityInfo GetCurrentCityInfo() const;

    void UpdateWeather(WeatherInfoUpdatedCallback callback = nullptr);

    std::wstring GetWeatherTemperature() const;
    bool GetWeatherDetails(WeatherDetails& details) const;

    SConfiguration GetConfig() const;
    std::wstring ConfigPath() const;
    void ReloadConfigs();
    bool RefreshWeather();

    void LoadConfigs(const std::wstring &cfg_dir);
    void SaveConfigs() const;

    HICON GetIcon();

    void RefreshWeatherInfoCache();
    
    DataApiPtr GetCurrentApi() const;

    std::shared_ptr<DataApiWeatherComCnSpider> m_api_wccs;
    std::shared_ptr<DataApiHefengWeather> m_api_hfw;
private:
    HICON _getIcon();

    std::wstring _getWeatherTemperature() const;

    void _setLangID(const std::wstring &cfg_dir);

    mutable std::mutex m_state_mutex;
    mutable std::recursive_mutex m_cache_mutex;
    int m_dpi;
    mutable std::map<UINT, CString> m_string_resources;

    SConfiguration m_config;
    std::wstring m_config_file_path;

    SCityInfo m_currentCityInfo;

    struct SWeatherInfoCache
    {
        std::wstring WeatherTemperature;
        WeatherDetails Details;
        bool HasDetails{};

        IconType CurrentIconType{ IconType::IT_WCC_BULE };
        HICON Icon{ nullptr };
    };

    SWeatherInfoCache m_weather_info_cache;

    std::atomic<unsigned short> m_lang_id;
};
