#include "pch.h"
#include "DataApiHefengWeather.h"

#include "Common.h"
#include "WeatherService.h"
#include <afxinet.h>
#include <utilities/yyjson/yyjson.h>
#include <unordered_map>
#include <functional>
#include <sstream>

#include "DataManager.h"

#define CHECK_KEY const auto normalized_host = WeatherService::NormalizeApiHost(config.ApiHost);\
if (config.AppKey.empty() || !normalized_host || config.AppKey.find_first_of(L"\r\n \t") != std::wstring::npos) {\
    _lastError = L"和风天气 API Host 或 API Key 配置无效。";\
    return false;\
}\
config.ApiHost = *normalized_host

namespace hf
{
    std::wstring short_time(const std::wstring& value) { return value.size() >= 16 ? value.substr(11, 5) : L""; }
    bool call_internet(const CString &url, const std::wstring& key, std::wstring &content)
    {
        return WeatherCCommon::GetUrl(url, (L"X-QW-Api-Key: " + key + L"\r\n").c_str(), content, false);
}

    std::wstring get_json_str_value(yyjson_val *j_val, const char *key)
    {
        auto *obj = yyjson_obj_get(j_val, key);
        if (obj == nullptr) return L"";
        else return WeatherCCommon::StrToUnicode(yyjson_get_str(obj), true);
    }

    std::wstring query_func_frame(const CString &url, const std::wstring& key, std::function<void(yyjson_val*)> func)
    {
        const auto &dm = WeatherCDataManager::Instance();

        std::wstring error;

        std::wstring content;
        auto succeed = call_internet(url, key, content);

        if (succeed && !content.empty())
        {
            auto json_utf8 = WeatherCCommon::UnicodeToStr(content.c_str(), true);
            yyjson_doc *doc = yyjson_read(json_utf8.c_str(), json_utf8.size(), 0);
            if (doc != nullptr)
            {
                auto *root = yyjson_doc_get_root(doc);

                auto code = get_json_str_value(root, "code");
                if (code == L"200")
                {
                    func(root);
                }
                else {
                    if (code.size() == 3 && code.find_first_not_of(L"0123456789") == std::wstring::npos)
                        error = L"和风天气服务返回代码 " + code + L"。";
                    else error = L"和风天气响应格式无效。";
                }
            }
            else
                //error = L"Invalid json contents.";
                error = dm.StringRes(IDS_HFW_INVALID_JSON);

            yyjson_doc_free(doc);
        }
        else
            //error = L"Failed to access the Internet.";
            error = WeatherCCommon::LastHttpStatus() ? L"和风天气返回 HTTP " + std::to_wstring(WeatherCCommon::LastHttpStatus()) + L"。" : L"和风天气连接失败或超时。";

        return error;
    }
}

bool DataApiHefengWeather::QueryCity(const std::wstring &query, CityInfoList &info)
{
    CHECK_KEY;

    const auto &dm = WeatherCDataManager::Instance();

    info.clear();

    auto queryEncoded = WeatherCCommon::URLEncode(query);

    CString url;
    url.Format(L"https://%s/geo/v2/city/lookup?location=%s&lang=%s",
               config.ApiHost.c_str(),
               queryEncoded.c_str(),
               dm.StringRes(IDS_HFW_LANG).GetString());

    CityInfoList cities;
    auto func = [&cities](yyjson_val *j_val) {
        auto *loc_arr = yyjson_obj_get(j_val, "location");
        auto num_cities = yyjson_arr_size(loc_arr);

        auto get_city_info = [](yyjson_val *j_val) {
            SCityInfo city;
            city.CityName = hf::get_json_str_value(j_val, "name");
            city.CityNO = hf::get_json_str_value(j_val, "id");
            city.CityAdministrativeOwnership = hf::get_json_str_value(j_val, "adm2") + L"-" + hf::get_json_str_value(j_val, "adm1");

            return city;
        };

        for (decltype(num_cities) i = 0; i < num_cities; ++i)
            cities.push_back(get_city_info(yyjson_arr_get(loc_arr, i)));
    };

    auto err = hf::query_func_frame(url, config.AppKey, func);
    if (err.empty())
        info = cities;
    _lastError = err;
    return err.empty();
}

std::wstring DataApiHefengWeather::GetWeatherInfoSummary()
{
    const auto &app_config = WeatherCDataManager::Instance().GetConfig();
    const auto &city_info = WeatherCDataManager::Instance().GetCurrentCityInfo();

    const auto &dm = WeatherCDataManager::Instance();
    CString tmp_str;

    std::wostringstream oss;

    // format realtime weather information
    oss << GetWeatherText(EWeatherInfoType::WEATHER_REALTIME) << L" "
        << GetTemprature(EWeatherInfoType::WEATHER_REALTIME) << L" "
        << L"(" << _realtimeWeather.UpdateTime << L")";
    if (!app_config.m_show_brief_rt_weather_info)
    {
        if (config.ShowRealtimeWind)
        {
            tmp_str.Format(dm.StringRes(IDS_HFW_FMT_WIND_DIRECTION), _realtimeWeather.WindDirection.c_str());
            oss << L" " << tmp_str.GetString();
            if (config.ShowRealtimeWindScale)
            {
                tmp_str.Format(dm.StringRes(IDS_HFW_FMT_WIND_SCALE), _realtimeWeather.WindScale.c_str());
                oss << tmp_str.GetString();
            }
            else
                oss << _realtimeWeather.WindSpeed << L"km/h";
        }

        if (config.ShowRealtimeHumidity)
            oss << L" " << dm.StringRes(IDS_HFW_FMT_HUMIDITY).GetString() << _realtimeWeather.Humidity << L"%";

        // format realtime air quality information
        if (config.ShowAirQuality)
        {
            oss << std::endl << dm.StringRes(IDS_HFW_FMT_AIR_QUALITY).GetString() << _realtimeAirQuality.Category;

            if (config.ShowAirQualityAQI)
                oss << L" AQI: " << _realtimeAirQuality.AQI;

            if (config.ShowAirQualityPM2p5)
                oss << L" PM2.5: " << _realtimeAirQuality.PM2p5;

            if (config.ShowAirQualityPM10)
                oss << L" PM10: " << _realtimeAirQuality.PM10;
        }
    }
    oss << std::endl;

    // format weather alerts information
    if (app_config.m_show_weather_alerts && config.ShowWeatherAlert && !_weatherAlerts.empty())
    {
        if (app_config.m_show_brief_weather_alert_info)
            for (const auto &alert : _weatherAlerts)
                oss << L"[!] " << alert.Title << L"(" << alert.PublishTime << L")" << std::endl;
        else
            for (const auto &alert : _weatherAlerts)
                oss << L"[!] " << alert.Text << std::endl;
    }

    // format forcast weather information
    auto forcast_formatter = [&dm, &oss, this](EWeatherInfoType weather_type, ForcastWeather &weather, UINT date_res_id) {
        oss << dm.StringRes(date_res_id).GetString() << L": " << GetWeatherText(weather_type)
            << L" " << GetTemprature(weather_type);
        if (config.ShowForecastUVIdex)
            oss << L" " << dm.StringRes(IDS_HFW_FMT_UVI).GetString() << weather.UVIndex;
        if (config.showForecastHumidity)
            oss << L" " << dm.StringRes(IDS_HFW_FMT_HUMIDITY).GetString() << weather.Humidity << L"%";
        oss << std::endl;
        };

    forcast_formatter(EWeatherInfoType::WEATHER_TODAY, _forcastWeatherTD, IDS_TODAY);
    forcast_formatter(EWeatherInfoType::WEATHER_TOMMROW, _forcastWeatherTM, IDS_TOMORROW);
    forcast_formatter(EWeatherInfoType::WEATHER_DAY_AFTER_TOMMROW, _forcastWeatherDATM, IDS_DAY_AFTER_TOMORROW);

    // end

    return oss.str();
}

std::wstring DataApiHefengWeather::GetTemprature(EWeatherInfoType type)
{
    std::wostringstream oss;

    switch (type)
    {
    case EWeatherInfoType::WEATHER_REALTIME:
        if (config.ShowRealtimeTemperatureFeelsLike)
            oss << _realtimeWeather.TemperatureFeelsLike << L"℃";
        else
            oss << _realtimeWeather.Temperature << L"℃";
        break;

    case EWeatherInfoType::WEATHER_TODAY:
        oss << _forcastWeatherTD.TemperatureMin << L"℃~" << _forcastWeatherTD.TemperatureMax << L"℃";
        break;

    case EWeatherInfoType::WEATHER_TOMMROW:
        oss << _forcastWeatherTM.TemperatureMin << L"℃~" << _forcastWeatherTM.TemperatureMax << L"℃";
        break;

    case EWeatherInfoType::WEATHER_DAY_AFTER_TOMMROW:
        oss << _forcastWeatherDATM.TemperatureMin << L"℃~" << _forcastWeatherDATM.TemperatureMax << L"℃";
        break;

    default:
        break;
    }

    return oss.str();
}

std::wstring DataApiHefengWeather::GetWeatherText(EWeatherInfoType type)
{
    std::wostringstream oss;

    switch (type)
    {
    default:
    case EWeatherInfoType::WEATHER_REALTIME:
        oss << _realtimeWeather.WeatherText;
        break;

    case EWeatherInfoType::WEATHER_TODAY:
        if (_forcastWeatherTD.WeatherDay == _forcastWeatherTD.WeatherNight)
            oss << _forcastWeatherTD.WeatherDay;
        else
            oss << _forcastWeatherTD.WeatherDay << L"~" << _forcastWeatherTD.WeatherNight;
        break;

    case EWeatherInfoType::WEATHER_TOMMROW:
        if (_forcastWeatherTM.WeatherDay == _forcastWeatherTM.WeatherNight)
            oss << _forcastWeatherTM.WeatherDay;
        else
            oss << _forcastWeatherTM.WeatherDay << L"~" << _forcastWeatherTM.WeatherNight;
        break;
    case EWeatherInfoType::WEATHER_DAY_AFTER_TOMMROW:
        if (_forcastWeatherDATM.WeatherDay == _forcastWeatherDATM.WeatherNight)
            oss << _forcastWeatherDATM.WeatherDay;
        else
            oss << _forcastWeatherDATM.WeatherDay << L"~" << _forcastWeatherDATM.WeatherNight;
        break;
    }

    return oss.str();
}

std::wstring DataApiHefengWeather::GetWeatherCode(EWeatherInfoType type)
{
    switch (type)
    {
    default:
    case EWeatherInfoType::WEATHER_REALTIME:
        return _realtimeWeather.WeatherCode;
    case EWeatherInfoType::WEATHER_TODAY:
        return _forcastWeatherTD.WeatherCodeDay;
    case EWeatherInfoType::WEATHER_TOMMROW:
        return _forcastWeatherTM.WeatherCodeDay;
    case EWeatherInfoType::WEATHER_DAY_AFTER_TOMMROW:
        return _forcastWeatherDATM.WeatherCodeDay;
    }
}

std::optional<WeatherDetails> DataApiHefengWeather::GetWeatherDetails() const
{
    WeatherDetails details;
    details.Current.Temperature = _realtimeWeather.Temperature;
    details.Current.TemperatureFeelsLike = _realtimeWeather.TemperatureFeelsLike;
    details.Current.WeatherText = _realtimeWeather.WeatherText;
    details.Current.WeatherCode = _realtimeWeather.WeatherCode;
    details.Current.ConditionKind = WeatherCondition::FromQWeather(_realtimeWeather.WeatherCode);
    details.Current.Humidity = _realtimeWeather.Humidity;
    details.Current.WindDirection = _realtimeWeather.WindDirection;
    details.Current.WindScale = _realtimeWeather.WindScale;
    details.Current.WindSpeed = _realtimeWeather.WindSpeed;
    details.Current.ObservedAt = _realtimeWeather.UpdateTime;

    details.AirQuality.AQI = _realtimeAirQuality.AQI;
    details.AirQuality.Level = _realtimeAirQuality.Level;
    details.AirQuality.Category = _realtimeAirQuality.Category;
    details.AirQuality.PM2p5 = _realtimeAirQuality.PM2p5;
    details.AirQuality.PM10 = _realtimeAirQuality.PM10;
    details.AirQuality.PublishTime = _realtimeAirQuality.PublishTime;

    const ForcastWeather* days[] = { &_forcastWeatherTD, &_forcastWeatherTM, &_forcastWeatherDATM };
    for (const auto* source : days) {
        WeatherForecastDay day;
        day.TemperatureMin = source->TemperatureMin;
        day.TemperatureMax = source->TemperatureMax;
        day.WeatherDay = source->WeatherDay;
        day.WeatherNight = source->WeatherNight;
        day.WeatherCodeDay = source->WeatherCodeDay;
        day.WeatherCodeNight = source->WeatherCodeNight;
        day.ConditionDayKind = WeatherCondition::FromQWeather(source->WeatherCodeDay);
        day.ConditionNightKind = WeatherCondition::FromQWeather(source->WeatherCodeNight);
        if (!day.TemperatureMin.empty() || !day.TemperatureMax.empty() ||
            !day.WeatherDay.empty() || !day.WeatherNight.empty()) details.Forecast.push_back(std::move(day));
    }

    for (const auto& source : _weatherAlerts) details.Alerts.push_back({
        source.Type, source.TypeName, source.Level, source.Title, source.Text, source.Severity, source.PublishTime });
    return details;
}

bool DataApiHefengWeather::UpdateWeather()
{
    CHECK_KEY;

    _airQualityDataOutdated = true;
    _alertsDataOutdated = true;

    std::wostringstream oss;

    const auto &currunt_city = WeatherCDataManager::Instance().GetCurrentCityInfo();

    if (_dataCity != currunt_city.CityNO) { _realtimeWeather = {}; _forcastWeatherTD = {}; _forcastWeatherTM = {}; _forcastWeatherDATM = {}; _realtimeAirQuality = {}; _weatherAlerts.clear(); _dataCity = currunt_city.CityNO; }
    const bool realtime = QueryRealtimeWeather(currunt_city.CityNO);
    if (!realtime)
        oss << L"[RealtimeWeather]" << _lastError << std::endl;
    const bool forecast = QueryForecastWeather(currunt_city.CityNO);
    if (!forecast)
        oss << L"[ForcastWeather]" << _lastError << std::endl;
    if (config.ShowAirQuality && !QueryRealtimeAirQuality(currunt_city.CityNO))
        oss << L"[RealtimeAirQuality]" << _lastError << std::endl;
    if (config.ShowWeatherAlert && !QueryWeatherAlerts(currunt_city.CityNO))
        oss << L"[WeatherAlerts]" << _lastError;

    _lastError = oss.str();

    return WeatherCDataManager::Instance().GetConfig().m_wit == EWeatherInfoType::WEATHER_REALTIME ? realtime : forecast;
}

std::wstring DataApiHefengWeather::GetLastError()
{
    return _lastError;
}

bool DataApiHefengWeather::QueryRealtimeWeather(const std::wstring &query)
{
    CHECK_KEY;

    const auto &dm = WeatherCDataManager::Instance();

    CString url;
    url.Format(L"https://%s/v7/weather/now?location=%s&lang=%s",
               config.ApiHost.c_str(),
               query.c_str(),
               dm.StringRes(IDS_HFW_LANG).GetString());

    RealtimeWeather data;
    auto func = [&data](yyjson_val *j_val) {
        auto *now_obj = yyjson_obj_get(j_val, "now");

        data.Temperature = hf::get_json_str_value(now_obj, "temp");
        data.TemperatureFeelsLike = hf::get_json_str_value(now_obj, "feelsLike");
        data.UpdateTime = hf::short_time(hf::get_json_str_value(now_obj, "obsTime"));
        data.WeatherText = hf::get_json_str_value(now_obj, "text");
        data.WeatherCode = hf::get_json_str_value(now_obj, "icon");
        data.WindDirection = hf::get_json_str_value(now_obj, "windDir");
        data.WindScale = hf::get_json_str_value(now_obj, "windScale");
        data.WindSpeed = hf::get_json_str_value(now_obj, "windSpeed");
        data.Humidity = hf::get_json_str_value(now_obj, "humidity");
    };

    auto err = hf::query_func_frame(url, config.AppKey, func);
    if (err.empty() && (data.Temperature.empty() || data.WeatherCode.empty())) err = L"和风天气实时数据不完整。";
    if (err.empty())
        _realtimeWeather = data;
    _lastError = err;
    return err.empty();
}

bool DataApiHefengWeather::QueryRealtimeAirQuality(const std::wstring &query)
{
    CHECK_KEY;

    const auto &dm = WeatherCDataManager::Instance();

    CString url;
    url.Format(L"https://%s/v7/air/now?location=%s&lang=%s",
               config.ApiHost.c_str(),
               query.c_str(),
               dm.StringRes(IDS_HFW_LANG).GetString());

    RealtimeAirQuality data;
    auto func = [&data](yyjson_val *j_val) {
        auto *now_obj = yyjson_obj_get(j_val, "now");

        data.PublishTime = hf::short_time(hf::get_json_str_value(now_obj, "pubTime"));
        data.AQI = hf::get_json_str_value(now_obj, "aqi");
        data.Level = hf::get_json_str_value(now_obj, "level");
        data.Category = hf::get_json_str_value(now_obj, "category");
        data.PM2p5 = hf::get_json_str_value(now_obj, "pm2p5");
        data.PM10 = hf::get_json_str_value(now_obj, "pm10");
    };

    auto err = hf::query_func_frame(url, config.AppKey, func);
    if (err.empty())
    {
        _realtimeAirQuality = data;
        _airQualityDataOutdated = false;
    }
    _lastError = err;
    return err.empty();
}

bool DataApiHefengWeather::QueryForecastWeather(const std::wstring &query)
{
    CHECK_KEY;

    const auto &dm = WeatherCDataManager::Instance();

    CString url;
    url.Format(L"https://%s/v7/weather/3d?location=%s&lang=%s",
               config.ApiHost.c_str(),
               query.c_str(),
               dm.StringRes(IDS_HFW_LANG).GetString());

    ForcastWeather td, tm, datm;
    auto func = [&td, &tm, &datm](yyjson_val *j_val) {
        auto *daily_arr = yyjson_obj_get(j_val, "daily");

        auto get_daily_info = [](yyjson_val *j_val, ForcastWeather &w) {
            w.TemperatureMax = hf::get_json_str_value(j_val, "tempMax");
            w.TemperatureMin = hf::get_json_str_value(j_val, "tempMin");
            w.WeatherDay = hf::get_json_str_value(j_val, "textDay");
            w.WeatherNight = hf::get_json_str_value(j_val, "textNight");
            w.WeatherCodeDay = hf::get_json_str_value(j_val, "iconDay");
            w.WeatherCodeNight = hf::get_json_str_value(j_val, "iconNight");
            w.UVIndex = hf::get_json_str_value(j_val, "uvIndex");
            w.Humidity = hf::get_json_str_value(j_val, "humidity");
        };

        get_daily_info(yyjson_arr_get(daily_arr, 0), td);
        get_daily_info(yyjson_arr_get(daily_arr, 1), tm);
        get_daily_info(yyjson_arr_get(daily_arr, 2), datm);
    };

    auto err = hf::query_func_frame(url, config.AppKey, func);
    if (err.empty() && (td.TemperatureMin.empty() || td.TemperatureMax.empty() || tm.TemperatureMin.empty() || tm.TemperatureMax.empty() || datm.TemperatureMin.empty() || datm.TemperatureMax.empty())) err = L"和风天气三日预报不完整。";
    if (err.empty())
    {
        _forcastWeatherTD = td;
        _forcastWeatherTM = tm;
        _forcastWeatherDATM = datm;
    }
    _lastError = err;
    return err.empty();
}

bool DataApiHefengWeather::QueryWeatherAlerts(const std::wstring &query)
{
    CHECK_KEY;

    const auto &dm = WeatherCDataManager::Instance();

    CString url;
    url.Format(L"https://%s/v7/warning/now?location=%s&lang=%s",
               config.ApiHost.c_str(),
               query.c_str(),
               dm.StringRes(IDS_HFW_LANG).GetString());

    WeatherAlertList data;
    auto func = [&data](yyjson_val *j_val) {
        auto *alert_arr = yyjson_obj_get(j_val, "warning");
        auto num_alerts = yyjson_arr_size(alert_arr);

        auto get_alert_info = [](yyjson_val *j_val) {
            WeatherAlert alert;
            alert.Type = hf::get_json_str_value(j_val, "type");
            alert.TypeName = hf::get_json_str_value(j_val, "typeName");
            alert.Level = hf::get_json_str_value(j_val, "level");
            alert.Title = hf::get_json_str_value(j_val, "title");
            alert.Text = hf::get_json_str_value(j_val, "text");
            alert.Severity = hf::get_json_str_value(j_val, "severity");
            alert.SeverityColor = hf::get_json_str_value(j_val, "severityColor");
            alert.PublishTime = hf::get_json_str_value(j_val, "pubTime");
            if (alert.PublishTime.size() >= 16) alert.PublishTime = alert.PublishTime.substr(0, 16).replace(10, 1, L" ");
            return alert;
        };

        for (decltype(num_alerts) i = 0; i < num_alerts; ++i)
            data.push_back(get_alert_info(yyjson_arr_get(alert_arr, i)));
    };

    auto err = hf::query_func_frame(url, config.AppKey, func);
    if (err.empty())
    {
        _weatherAlerts = data;
        _alertsDataOutdated = false;
    }
    _lastError = err;
    return err.empty();
}
