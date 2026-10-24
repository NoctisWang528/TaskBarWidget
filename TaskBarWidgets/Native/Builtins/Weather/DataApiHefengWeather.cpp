#include "pch.h"
#include "DataApiHefengWeather.h"

#include "Common.h"
#include "WeatherService.h"
#include <afxinet.h>
#include <utilities/yyjson/yyjson.h>
#include <unordered_map>
#include <functional>
#include <sstream>
#include <iomanip>
#include <locale>
#include <cmath>
#include <algorithm>

#include "DataManager.h"

#define CHECK_KEY if (!ValidateConfiguration()) return false

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
        if (!yyjson_is_str(obj)) return L"";
        else return WeatherCCommon::StrToUnicode(yyjson_get_str(obj), true);
    }

    std::wstring query_func_frame(const CString &url, const std::wstring& key, std::function<void(yyjson_val*)> func, bool alert_schema = false, bool* shared = nullptr)
    {
        const auto &dm = WeatherCDataManager::Instance();

        std::wstring error;

        std::wstring content;
        auto succeed = call_internet(url, key, content);

        const auto http = WeatherCCommon::LastHttpStatus();
        if (shared) *shared = !succeed && !http;
        if (WeatherCCommon::LastTransportError() || (!succeed && (http == 200 || !http ||
            WeatherCCommon::LastRequestStage() != WeatherCCommon::RequestStage::None))) {
            if (shared) *shared = true;
            auto reason = WeatherCCommon::LastTransportErrorMessage();
            if (reason.empty()) reason = L"和风天气连接失败或响应读取不完整。";
            return http && http != 200 ? L"和风天气返回 HTTP " + std::to_wstring(http) + L"；" + reason : reason;
        }
        if (!content.empty())
        {
            auto json_utf8 = WeatherCCommon::UnicodeToStr(content.c_str(), true);
            yyjson_doc *doc = yyjson_read(json_utf8.c_str(), json_utf8.size(), 0);
            if (doc != nullptr)
            {
                auto *root = yyjson_doc_get_root(doc);

                auto code = get_json_str_value(root, "code");
                const auto type = get_json_str_value(yyjson_obj_get(root, "error"), "type");
                static const std::unordered_map<std::wstring, std::wstring> reasons = {
                    {L"unauthorized", L"鉴权失败 (401)，请检查 API Key。"},
                    {L"invalid-host", L"API Host 无效，请检查控制台配置。"},
                    {L"security-restriction", L"请求受到安全限制，请检查请求限制设置。"},
                    {L"no-credit", L"账户可用额度不足。"}, {L"overdue", L"账户存在逾期账单。"},
                    {L"account-suspension", L"账户已被冻结。"}, {L"deprecated", L"此接口已弃用。"},
                    {L"forbidden", L"此接口无访问权限。"}, {L"too-many-requests", L"请求过于频繁 (429)。"},
                    {L"over-monthly-limit", L"请求超过月度限额 (429)。"}
                };
                const std::wstring prefix = L"https://dev.qweather.com/docs/resource/error-code/#";
                const auto kind = type.compare(0, prefix.size(), prefix) == 0 ? type.substr(prefix.size()) : L"";
                const auto reason = reasons.find(kind);
                if (shared && (http == 401 || http == 429 || code == L"401" || code == L"429" ||
                    kind == L"unauthorized" || kind == L"invalid-host" || kind == L"security-restriction" ||
                    kind == L"no-credit" || kind == L"overdue" || kind == L"account-suspension" ||
                    kind == L"too-many-requests" || kind == L"over-monthly-limit")) *shared = true;
                if (reason != reasons.end()) error = L"和风天气：" + reason->second;
                else if (!succeed || yyjson_obj_get(root, "error"))
                    error = http ? L"和风天气返回 HTTP " + std::to_wstring(http) + L"。" : L"和风天气服务返回错误。";
                else if (yyjson_is_obj(root) && (alert_schema ? !yyjson_obj_get(root, "code") : code == L"200"))
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
                error = http && !succeed ? L"和风天气返回 HTTP " + std::to_wstring(http) + L"。" : std::wstring(dm.StringRes(IDS_HFW_INVALID_JSON).GetString());

            yyjson_doc_free(doc);
        }
        else
            //error = L"Failed to access the Internet.";
            error = !succeed && http ? L"和风天气返回 HTTP " + std::to_wstring(http) + L"。" : L"和风天气响应为空。";

        if (!succeed && http && error.empty()) error = L"和风天气返回 HTTP " + std::to_wstring(http) + L"。";
        if (!succeed && !http) error = WeatherCCommon::LastTransportErrorMessage();
        if (!succeed && error.empty()) error = L"和风天气连接失败或超时。";
        if (shared && (http == 401 || http == 429)) *shared = true;

        return error;
    }
}

bool DataApiHefengWeather::ValidateConfiguration()
{
    _lastError = WeatherService::ApiHostError(config.ApiHost);
    if (_lastError.empty() && (config.AppKey.empty() || std::any_of(config.AppKey.begin(), config.AppKey.end(),
        [](wchar_t ch) { return ch <= 32 || ch > 126; })))
        _lastError = L"和风天气 API Key 配置无效。";
    if (!_lastError.empty()) { _sharedFailure = true; return false; }
    config.ApiHost = *WeatherService::NormalizeApiHost(config.ApiHost);
    return true;
}

bool DataApiHefengWeather::ResolveCoordinates(const std::wstring& query, std::wstring& coordinates)
{
    if (_updating && _coordinatesResolved) {
        coordinates = _coordinates;
        _lastError = _coordinateError;
        return _lastError.empty();
    }
    const bool success = [&]() {
        const auto &dm = WeatherCDataManager::Instance();

        CString url;
        url.Format(L"https://%s/geo/v2/city/lookup?location=%s&lang=%s",
                   config.ApiHost.c_str(),
                   WeatherCCommon::URLEncode(query).c_str(),
                   dm.StringRes(IDS_HFW_LANG).GetString());

        auto parse_coordinate = [](const std::wstring& text, double limit, double& value) {
            std::wistringstream input(text);
            input.imbue(std::locale::classic());
            input >> std::noskipws >> value;
            return !text.empty() && !input.fail() && input.eof() && std::isfinite(value) && value >= -limit && value <= limit;
        };
        const auto separator = query.find(L',');
        double query_longitude = 0, query_latitude = 0;
        const bool coordinate_query = separator != std::wstring::npos &&
            parse_coordinate(query.substr(0, separator), 180, query_longitude) &&
            parse_coordinate(query.substr(separator + 1), 90, query_latitude);
        if (separator != std::wstring::npos && !coordinate_query) { _lastError = L"城市经纬度格式或范围无效。"; return false; }
        auto coordinate_path = [](double latitude, double longitude) {
            latitude = std::round(latitude * 100) / 100;
            longitude = std::round(longitude * 100) / 100;
            std::wostringstream path;
            path.imbue(std::locale::classic());
            path << std::fixed << std::setprecision(2) << (latitude == 0 ? 0 : latitude) << L"/" << (longitude == 0 ? 0 : longitude);
            return path.str();
        };
        auto geo_func = [&coordinates, &query, &parse_coordinate, &coordinate_path](yyjson_val* root) {
            auto* locations = yyjson_obj_get(root, "location");
            if (!yyjson_is_arr(locations)) return;
            for (size_t index = 0; index < yyjson_arr_size(locations); ++index) {
                auto* location = yyjson_arr_get(locations, index);
                if (hf::get_json_str_value(location, "id") != query) continue;
                double latitude = 0, longitude = 0;
                if (!parse_coordinate(hf::get_json_str_value(location, "lat"), 90, latitude) ||
                    !parse_coordinate(hf::get_json_str_value(location, "lon"), 180, longitude)) return;
                coordinates = coordinate_path(latitude, longitude);
                return;
            }
        };
        std::wstring err;
        if (coordinate_query) coordinates = coordinate_path(query_latitude, query_longitude);
        else err = hf::query_func_frame(url, config.AppKey, geo_func, false, &_sharedFailure);
        if (!err.empty()) { _lastError = L"城市坐标解析失败：" + err; return false; }
        if (coordinates.empty()) { _lastError = L"城市坐标缺失、无效或与当前城市不匹配。"; return false; }
        return true;
    }();
    if (_updating) {
        _coordinatesResolved = true;
        _coordinates = coordinates;
        _coordinateError = success ? L"" : _lastError;
    }
    return success;
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

    auto err = hf::query_func_frame(url, config.AppKey, func, false, &_sharedFailure);
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
    if (app_config.m_show_weather_alerts && config.ShowWeatherAlert && !_alertsError.empty())
        oss << _alertsError << std::endl;
    if (app_config.m_show_weather_alerts && config.ShowWeatherAlert && !_weatherAlerts.empty())
    {
        if (app_config.m_show_brief_weather_alert_info)
            for (const auto &alert : _weatherAlerts)
                oss << L"[!] " << alert.Title << L"(" << alert.PublishTime << L")" << std::endl;
        else
            for (const auto &alert : _weatherAlerts)
                oss << L"[!] " << alert.Text << std::endl;
    }

    if (app_config.m_show_weather_alerts && config.ShowWeatherAlert && !_alertsAttribution.empty())
        oss << _alertsAttribution << std::endl;

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

    if (config.ShowAirQuality) details.AirQuality = _realtimeAirQuality;

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

    details.AlertsError = config.ShowWeatherAlert ? _alertsError : L"";
    details.AlertsAttribution = config.ShowWeatherAlert ? _alertsAttribution : L"";
    if (config.ShowWeatherAlert) for (const auto& source : _weatherAlerts) details.Alerts.push_back({
        source.Type, source.TypeName, source.Level, source.Title, source.Text, source.Severity, source.PublishTime });
    return details;
}

bool DataApiHefengWeather::UpdateWeather()
{
    _airQualityDataOutdated = true;
    _alertsDataOutdated = true;

    std::wostringstream oss;

    const auto &currunt_city = WeatherCDataManager::Instance().GetCurrentCityInfo();

    if (_dataCity != currunt_city.CityNO) { _realtimeWeather = {}; _forcastWeatherTD = {}; _forcastWeatherTM = {}; _forcastWeatherDATM = {}; _realtimeAirQuality = {}; _weatherAlerts.clear(); _alertsError.clear(); _alertsAttribution.clear(); _dataCity = currunt_city.CityNO; }
    if (!config.ShowWeatherAlert) { _weatherAlerts.clear(); _alertsError.clear(); _alertsAttribution.clear(); }
    if (!config.ShowAirQuality) _realtimeAirQuality = {};
    _sharedFailure = false;
    _coordinatesResolved = false;
    _coordinates.clear();
    _coordinateError.clear();
    _updating = true;
    struct EndUpdate { bool& value; ~EndUpdate() { value = false; } } endUpdate{ _updating };
    auto sharedFail = [this]() {
        if (config.ShowAirQuality) _realtimeAirQuality.Error = L"空气质量获取失败：" + _lastError +
            (_realtimeAirQuality.AQI.empty() && _realtimeAirQuality.PM2p5.empty() && _realtimeAirQuality.PM10.empty() ?
                L"；无可用的上次信息。" : L"；上次数据可能过时。");
        if (config.ShowWeatherAlert) _alertsError = L"预警获取失败：" + _lastError +
            (_weatherAlerts.empty() ? L"；无可用的上次信息。" : L"；以下为上次信息，可能已过时。");
    };
    if (!ValidateConfiguration()) { sharedFail(); return false; }
    const bool realtime = QueryRealtimeWeather(currunt_city.CityNO);
    if (!realtime)
        oss << L"[RealtimeWeather]" << _lastError << std::endl;
    if (_sharedFailure) { sharedFail(); return false; }
    const bool forecast = QueryForecastWeather(currunt_city.CityNO);
    if (!forecast)
        oss << L"[ForcastWeather]" << _lastError << std::endl;
    if (_sharedFailure) { sharedFail(); return false; }
    if (config.ShowAirQuality && !QueryRealtimeAirQuality(currunt_city.CityNO))
        oss << L"[RealtimeAirQuality]" << _lastError << std::endl;
    if (_sharedFailure) { sharedFail(); return WeatherCDataManager::Instance().GetConfig().m_wit == EWeatherInfoType::WEATHER_REALTIME ? realtime : forecast; }
    if (config.ShowWeatherAlert && !QueryWeatherAlerts(currunt_city.CityNO))
        oss << L"[WeatherAlerts]" << _lastError;

    if (!_sharedFailure) _lastError = oss.str();

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

    auto err = hf::query_func_frame(url, config.AppKey, func, false, &_sharedFailure);
    if (err.empty() && (data.Temperature.empty() || data.WeatherCode.empty())) err = L"和风天气实时数据不完整。";
    if (err.empty())
        _realtimeWeather = data;
    _lastError = err;
    return err.empty();
}

bool DataApiHefengWeather::QueryRealtimeAirQuality(const std::wstring &query)
{
    if (_airCity != query || !config.ShowAirQuality) { _realtimeAirQuality = {}; _airCity = query; }
    if (!config.ShowAirQuality) { _lastError.clear(); return true; }
    auto fail = [this](std::wstring reason) {
        _airQualityDataOutdated = true;
        _realtimeAirQuality.Error = L"空气质量获取失败：" + reason +
            (_realtimeAirQuality.AQI.empty() && _realtimeAirQuality.PM2p5.empty() && _realtimeAirQuality.PM10.empty() ?
                L"；无可用的上次信息。" : L"；上次数据可能过时。");
        _lastError = std::move(reason);
        return false;
    };
    if (!ValidateConfiguration()) return fail(_lastError);
    std::wstring coordinates;
    if (!ResolveCoordinates(query, coordinates)) return fail(_lastError);
    CString url;
    url.Format(L"https://%s/airquality/v1/current/%s?lang=%s", config.ApiHost.c_str(),
        coordinates.c_str(), WeatherCDataManager::Instance().StringRes(IDS_HFW_LANG).GetString());
    RealtimeAirQuality data;
    bool valid = false;
    auto func = [&data, &valid](yyjson_val* root) {
        auto* metadata = yyjson_obj_get(root, "metadata");
        if (!yyjson_is_obj(metadata)) return;
        auto* attributions = yyjson_obj_get(metadata, "attributions");
        if (attributions && !yyjson_is_arr(attributions)) return;
        for (size_t index = 0; index < yyjson_arr_size(attributions); ++index) {
            auto* item = yyjson_arr_get(attributions, index);
            if (!yyjson_is_str(item)) return;
            if (!data.Attribution.empty()) data.Attribution += L"\n";
            data.Attribution += WeatherCCommon::StrToUnicode(yyjson_get_str(item), true);
        }
        if (yyjson_is_true(yyjson_obj_get(metadata, "zeroResult"))) { valid = true; return; }
        auto* indexes = yyjson_obj_get(root, "indexes");
        auto* pollutants = yyjson_obj_get(root, "pollutants");
        if (!yyjson_is_arr(indexes) || !yyjson_arr_size(indexes) || !yyjson_is_arr(pollutants)) return;
        yyjson_val* selected = nullptr;
        for (size_t index = 0; index < yyjson_arr_size(indexes); ++index) {
            auto* item = yyjson_arr_get(indexes, index);
            const auto code = hf::get_json_str_value(item, "code");
            if (code.empty()) return;
            if (!selected || (hf::get_json_str_value(selected, "code") == L"qaqi" && code != L"qaqi")) selected = item;
            if (code == L"cn-mee") { selected = item; break; }
        }
        data.IndexCode = hf::get_json_str_value(selected, "code");
        data.IndexName = hf::get_json_str_value(selected, "name");
        data.AQI = hf::get_json_str_value(selected, "aqiDisplay");
        data.Level = hf::get_json_str_value(selected, "level");
        data.Category = hf::get_json_str_value(selected, "category");
        if (data.AQI.empty() || data.IndexName.empty() || data.Level.empty() || data.Category.empty()) return;
        for (size_t index = 0; index < yyjson_arr_size(pollutants); ++index) {
            auto* item = yyjson_arr_get(pollutants, index);
            const auto code = hf::get_json_str_value(item, "code");
            if (code != L"pm2p5" && code != L"pm10") continue;
            auto* concentration = yyjson_obj_get(item, "concentration");
            auto* value = yyjson_obj_get(concentration, "value");
            if (!yyjson_is_num(value)) continue;
            const double concentration_value = yyjson_is_real(value) ? yyjson_get_real(value) :
                yyjson_is_sint(value) ? static_cast<double>(yyjson_get_sint(value)) : static_cast<double>(yyjson_get_uint(value));
            if (!std::isfinite(concentration_value) || concentration_value < 0) continue;
            std::wostringstream number;
            number.imbue(std::locale::classic());
            number << std::setprecision(15) << concentration_value;
            (code == L"pm2p5" ? data.PM2p5 : data.PM10) = number.str();
            (code == L"pm2p5" ? data.PM2p5Unit : data.PM10Unit) = hf::get_json_str_value(concentration, "unit");
        }
        valid = true;
    };
    const auto err = hf::query_func_frame(url, config.AppKey, func, true, &_sharedFailure);
    if (!err.empty()) return fail(err);
    if (!valid) return fail(L"和风天气空气质量响应格式无效或数据不完整。");
    _realtimeAirQuality = std::move(data);
    _airQualityDataOutdated = false;
    _lastError.clear();
    return true;
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

    auto err = hf::query_func_frame(url, config.AppKey, func, false, &_sharedFailure);
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
    auto fail = [this](std::wstring reason) {
        _alertsDataOutdated = true;
        _alertsError = L"预警获取失败：" + reason +
            (_weatherAlerts.empty() ? L"；无可用的上次信息。" : L"；以下为上次信息，可能已过时。");
        _lastError = std::move(reason);
        return false;
    };
    if (!config.ShowWeatherAlert) {
        _weatherAlerts.clear();
        _alertsError.clear();
        _alertsAttribution.clear();
        _alertsDataOutdated = false;
        _lastError.clear();
        return true;
    }
    if (_alertsCity != query) {
        _weatherAlerts.clear();
        _alertsError.clear();
        _alertsAttribution.clear();
        _alertsCity = query;
    }
    if (!ValidateConfiguration()) return fail(_lastError);
    const auto &dm = WeatherCDataManager::Instance();
    CString url;
    std::wstring coordinates;
    if (!ResolveCoordinates(query, coordinates)) return fail(_lastError);
    url.Format(L"https://%s/weatheralert/v1/current/%s?localTime=true&lang=%s",
               config.ApiHost.c_str(), coordinates.c_str(), dm.StringRes(IDS_HFW_LANG).GetString());

    WeatherAlertList data;
    std::wstring attribution;
    bool valid = false;
    auto func = [&data, &attribution, &valid](yyjson_val *j_val) {
        auto* metadata = yyjson_obj_get(j_val, "metadata");
        auto* zero_result = yyjson_obj_get(metadata, "zeroResult");
        auto *alert_arr = yyjson_obj_get(j_val, "alerts");
        if (!yyjson_is_obj(metadata) || !yyjson_is_bool(zero_result) || !yyjson_is_arr(alert_arr)) return;
        auto* attributions = yyjson_obj_get(metadata, "attributions");
        if (attributions) {
            if (!yyjson_is_arr(attributions)) return;
            for (size_t index = 0; index < yyjson_arr_size(attributions); ++index) {
                auto* item = yyjson_arr_get(attributions, index);
                if (!yyjson_is_str(item)) return;
                if (!attribution.empty()) attribution += L"\n";
                attribution += WeatherCCommon::StrToUnicode(yyjson_get_str(item), true);
            }
        }
        auto num_alerts = yyjson_arr_size(alert_arr);
        if (yyjson_get_bool(zero_result) != (num_alerts == 0)) return;
        for (size_t index = 0; index < num_alerts; ++index) {
            auto* item = yyjson_arr_get(alert_arr, index);
            if (!yyjson_is_obj(item)) return;
            auto message = hf::get_json_str_value(yyjson_obj_get(item, "messageType"), "code");
            if (message == L"cancel") continue;
            if (message != L"alert" && message != L"update") return;
            auto* event = yyjson_obj_get(item, "eventType");
            auto* color = yyjson_obj_get(item, "color");
            if (!yyjson_is_obj(event) || !yyjson_is_str(yyjson_obj_get(event, "code")) ||
                !yyjson_is_str(yyjson_obj_get(event, "name")) || !yyjson_is_obj(color) ||
                !yyjson_is_str(yyjson_obj_get(color, "code")) || !yyjson_is_str(yyjson_obj_get(item, "headline")) ||
                !yyjson_is_str(yyjson_obj_get(item, "description")) || !yyjson_is_str(yyjson_obj_get(item, "severity")) ||
                !yyjson_is_str(yyjson_obj_get(item, "issuedTime"))) return;
            WeatherAlert alert;
            alert.Type = hf::get_json_str_value(event, "code");
            alert.TypeName = hf::get_json_str_value(event, "name");
            alert.SeverityColor = hf::get_json_str_value(color, "code");
            static const std::unordered_map<std::wstring, std::wstring> color_names = {
                {L"white", L"白色"}, {L"gray", L"灰色"}, {L"green", L"绿色"},
                {L"blue", L"蓝色"}, {L"yellow", L"黄色"}, {L"amber", L"琥珀色"},
                {L"orange", L"橙色"}, {L"red", L"红色"}, {L"purple", L"紫色"}, {L"black", L"黑色"}
            };
            const auto color_name = color_names.find(alert.SeverityColor);
            alert.Level = color_name == color_names.end() ? alert.SeverityColor : color_name->second;
            alert.Title = hf::get_json_str_value(item, "headline");
            alert.Text = hf::get_json_str_value(item, "description");
            alert.Severity = hf::get_json_str_value(item, "severity");
            alert.PublishTime = hf::get_json_str_value(item, "issuedTime");
            if (alert.Type.empty() || alert.TypeName.empty() || alert.Title.empty() || alert.PublishTime.empty()) return;
            if (alert.PublishTime.size() >= 16) alert.PublishTime = alert.PublishTime.substr(0, 16).replace(10, 1, L" ");
            data.push_back(std::move(alert));
        }
        valid = true;
    };

    auto err = hf::query_func_frame(url, config.AppKey, func, true, &_sharedFailure);
    if (!err.empty()) return fail(err);
    if (!valid) return fail(L"和风天气预警响应格式无效或数据不完整。");
    _weatherAlerts = std::move(data);
    _alertsAttribution = std::move(attribution);
    _alertsDataOutdated = false;
    _alertsError.clear();
    _lastError.clear();
    return true;
}
