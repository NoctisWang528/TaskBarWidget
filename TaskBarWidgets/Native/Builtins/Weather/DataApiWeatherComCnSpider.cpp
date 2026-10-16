#include "pch.h"
#include "DataApiWeatherComCnSpider.h"

#include "Common.h"
#include <utilities/yyjson/yyjson.h>
#include <afxinet.h>
#include <regex>
#include <map>
#include <sstream>

#include "DataManager.h"

namespace wccs  // WeatherComCnSpider
{
    using wslist = std::vector<std::wstring>;
    using wsListList = std::vector<wslist>;

    wsListList _RegexExtractAll(const std::wstring &text, const std::wregex &pattern)
    {
        std::wsmatch matches;
        wsListList results;

        auto itr1 = text.cbegin();
        auto itr2 = text.cend();

        while (std::regex_search(itr1, itr2, matches, pattern))
        {
            wslist sub_matches;
            for (const auto &sm : matches)
                sub_matches.push_back(sm.str());

            results.push_back(sub_matches);

            itr1 = matches[0].second;
        }

        return results;
    }

    wslist _StringSplit(const std::wstring &text, wchar_t sp)
    {
        std::size_t pos1{ 0 }, pos2{ std::wstring::npos };

        wslist results;
        do
        {
            pos2 = text.find_first_of(sp, pos1);
            results.push_back(text.substr(pos1, pos2 - pos1));

            pos1 = pos2 + 1;
        } while (pos2 != std::wstring::npos);

        return results;
    }

    wslist _ExtractCityInfoList(const std::wstring &s)
    {
        static const std::wregex pattern{ L"\\{\"ref\":\"(.+?)\"\\}" };

        wslist results;
        try
        {
            auto match_results = _RegexExtractAll(s, pattern);
            for (const auto &m : match_results)
                if (m.size() > 1)
                    results.push_back(m[1]);
        }
        catch (const std::exception &)
        {
            results.clear();
        }

        return results;
    }

    bool _IsSpot(const std::wstring &code)
    {
        return code.empty() || code.find_first_not_of(L"0123456789") != std::wstring::npos;
    }

    SCityInfo _ExtractCityInfo(const std::wstring &s)
    {
        SCityInfo result;

        wslist infoFields = _StringSplit(s, L'~');
        if (infoFields.size() < 10 || infoFields[0].empty() || infoFields[2].empty()) return result;

        if (!_IsSpot(infoFields[0]))
        {
            result.CityNO = infoFields[0];
            result.CityName = infoFields[2];

            if (infoFields[2] == infoFields[4])
                result.CityAdministrativeOwnership = infoFields[2] + L"-" + infoFields[9];
            else
                result.CityAdministrativeOwnership = infoFields[2] + L"-" + infoFields[4] + L"-" + infoFields[9];
        }

        return result;
    }

    bool _CallInternet(const CString &url, const CString &headers,
        std::wstring &content)
    {
        return WeatherCCommon::GetUrl(url, headers, content);
}

    bool QueryCityInfo(const std::wstring &qName, CityInfoList &city_list)
    {
        city_list.clear();

        auto qNameEncoded = WeatherCCommon::URLEncode(qName);
        auto timeStamp = std::time(0);

        CString q_url;
        q_url.Format(L"https://toy1.weather.com.cn/search?cityname=%s&callback=success_jsonpCallback&_=%I64d",
            qNameEncoded.c_str(), timeStamp);

        CString qHeaders = L"Host: toy1.weather.com.cn\r\nReferer: https://www.weather.com.cn/";

        std::wstring content;
        bool succeed = _CallInternet(q_url, qHeaders, content);

        if (succeed && !content.empty())
        {
            if (content.find(L'[') == std::wstring::npos || content.find(L']') == std::wstring::npos) return false;
            // parse string
            auto infoList = _ExtractCityInfoList(content);

            for (const auto &info : infoList)
            {
                auto cityInfo = _ExtractCityInfo(info);
                if (!cityInfo.CityNO.empty())
                    city_list.push_back(cityInfo);
            }
        }

        return succeed;
    }

    using SRealTimeWeather = DataApiWeatherComCnSpider::SRealTimeWeather;

    bool _queryRealTimeWeatherCity(const std::wstring &city_code, SRealTimeWeather &rt_weather)
    {
        rt_weather = SRealTimeWeather();

        auto timeStamp = std::time(0);

        CString q_url;
        q_url.Format(L"https://d1.weather.com.cn/sk_2d/%s.html?_=%I64d", city_code.c_str(), timeStamp);

        CString qHeaders = L"Host: d1.weather.com.cn\r\nReferer: https://www.weather.com.cn/";

        std::wstring content;
        auto succeed = _CallInternet(q_url, qHeaders, content);

        if (content.find(L'{') == std::wstring::npos)
            succeed = false;

        if (succeed && !content.empty())
        {
            auto data = content.substr(content.find(L'{'));
            if (!data.empty())
            {
                auto data_utf8 = WeatherCCommon::UnicodeToStr(data.c_str(), true);

                yyjson_doc *doc = yyjson_read(data_utf8.c_str(), data_utf8.size(), YYJSON_READ_STOP_WHEN_DONE);
                if (doc != nullptr)
                {
                    auto *root = yyjson_doc_get_root(doc);

                    auto get_value = [root](const char *key) {
                        auto *obj = yyjson_obj_get(root, key);
                        return WeatherCCommon::StrToUnicode(yyjson_get_str(obj), true);
                    };

                    rt_weather.Temperature = get_value("temp");
                    rt_weather.UpdateTime = get_value("time");
                    rt_weather.Weather = get_value("weather");
                    rt_weather.WeatherCode = get_value("weathercode");
                    rt_weather.AqiPM25 = get_value("aqi_pm25");
                    rt_weather.WindDirection = get_value("WD");
                    rt_weather.WindStrength = get_value("WS");

                    // correct weather code to 'night' if time during 20:00 to 06:00
                    if (rt_weather.Temperature.empty() || rt_weather.Weather.empty() || rt_weather.WeatherCode.empty()) succeed = false;
                    if (rt_weather.UpdateTime.size() >= 2 && iswdigit(rt_weather.UpdateTime[0]) && iswdigit(rt_weather.UpdateTime[1])) {
                        const auto h = (rt_weather.UpdateTime[0] - L'0') * 10 + rt_weather.UpdateTime[1] - L'0';
                        if ((h >= 20 || h < 6) && !rt_weather.WeatherCode.empty()) rt_weather.WeatherCode[0] = L'n';
                    }
                }
                else
                    succeed = false;

                yyjson_doc_free(doc);
            }
            else
                succeed = false;
        }

        return succeed;
    }

    std::wstring _convertWeatherNameToCode(const std::wstring &w_name)
    {
        static const std::map<std::wstring, std::wstring> d_map{
            {L"晴", L"d00"},
            {L"多云", L"d01"},
            {L"阴", L"d02"},
            {L"阵雨", L"d03"},
            {L"雷阵雨", L"d04"},
            {L"雷阵雨伴有冰雹", L"d05"},
            {L"雨夹雪", L"d06"},
            {L"小雨", L"d07"},
            {L"中雨", L"d08"},
            {L"大雨", L"d09"},
            {L"暴雨", L"d10"},
            {L"大暴雨", L"d11"},
            {L"特大暴雨", L"d12"},
            {L"阵雪", L"d13"},
            {L"小雪", L"d14"},
            {L"中雪", L"d15"},
            {L"大雪", L"d16"},
            {L"暴雪", L"d17"},
            {L"雾", L"d18"},
            {L"冻雨", L"d19"},
            {L"沙尘暴", L"d20"},
            {L"小到中雨", L"d21"},
            {L"中到大雨", L"d22"},
            {L"大到暴雨", L"d23"},
            {L"暴雨到大暴雨", L"d24"},
            {L"大暴雨到特大暴雨", L"d25"},
            {L"小到中雪", L"d26"},
            {L"中到大雪", L"d27"},
            {L"大到暴雪", L"d28"},
            {L"浮尘", L"d29"},
            {L"扬沙", L"d30"},
            {L"强沙尘暴", L"d31"},
            {L"霾", L"d53"},
            {L"无", L"d99"},
            {L"浓雾", L"d32"},
            {L"强浓雾", L"d49"},
            {L"中度霾", L"d54"},
            {L"重度霾", L"d55"},
            {L"严重霾", L"d56"},
            {L"大雾", L"d57"},
            {L"特强浓雾", L"d58"},
            {L"雨", L"d97"},
            {L"雪", L"d98"},
        };

        auto itr = d_map.find(w_name);
        if (itr != d_map.end())
            return itr->second;
        else
            return L"d99";
    }

    bool _queryRealTimeWeatherTownOrStreet(const std::wstring &code, SRealTimeWeather &rt_weather)
    {
        rt_weather = SRealTimeWeather();

        CString q_url;
        q_url.Format(L"https://forecast.weather.com.cn/town/weather1dn/%s.shtml", code.c_str());

        CString qHeaders = L"Host: forecast.weather.com.cn\r\nReferer: https://www.weather.com.cn/";

        std::wstring content;
        auto succeed = _CallInternet(q_url, qHeaders, content);

        if (succeed && !content.empty())
        {
            static const std::wregex pattern{ L"var forecast_default.*?(?=;)" };

            auto data_list = _RegexExtractAll(content, pattern);
            if (!data_list.empty())
            {
                const auto &data_str = data_list[0][0];
                if (data_str.find(L'{') != std::wstring::npos)
                {
                    auto data = data_str.substr(data_str.find(L'{'));

                    auto data_utf8 = WeatherCCommon::UnicodeToStr(data.c_str(), true);

                    yyjson_doc *doc = yyjson_read(data_utf8.c_str(), data_utf8.size(), YYJSON_READ_STOP_WHEN_DONE);
                    if (doc != nullptr)
                    {
                        auto *root = yyjson_doc_get_root(doc);

                        auto get_str_value = [root](const char *key) {
                            auto *obj = yyjson_obj_get(root, key);
                            return WeatherCCommon::StrToUnicode(yyjson_get_str(obj), true);
                        };

                        auto get_int_value = [root](const char *key) {
                            auto *obj = yyjson_obj_get(root, key);
                            return yyjson_get_int(obj);
                        };

                        rt_weather.Temperature = std::to_wstring(get_int_value("temp"));
                        rt_weather.UpdateTime = get_str_value("time");
                        rt_weather.Weather = get_str_value("weather");

                        rt_weather.WeatherCode = _convertWeatherNameToCode(rt_weather.Weather);
                        // correct weather code to 'night' if time during 20:00 to 06:00
                        if (!yyjson_is_num(yyjson_obj_get(root, "temp")) || rt_weather.Weather.empty()) succeed = false;
                        if (rt_weather.UpdateTime.size() >= 2 && iswdigit(rt_weather.UpdateTime[0]) && iswdigit(rt_weather.UpdateTime[1])) {
                            const auto h = (rt_weather.UpdateTime[0] - L'0') * 10 + rt_weather.UpdateTime[1] - L'0';
                            if (h >= 20 || h < 6) rt_weather.WeatherCode[0] = L'n';
                        }

                        rt_weather.AqiPM25 = L"--";

                        auto wind_ds = get_str_value("wind");
                        auto idx = wind_ds.find(L' ');
                        if (idx != std::wstring::npos)
                        {
                            rt_weather.WindDirection = wind_ds.substr(0, idx);
                            rt_weather.WindStrength = wind_ds.substr(idx + 1);
                        }
                        else
                            rt_weather.WindDirection = wind_ds;
                    }
                    yyjson_doc_free(doc);
                    if (!doc)
                        succeed = false;
                }
                else
                    succeed = false;
            }
            else
                succeed = false;
        }

        return succeed;
    }

    bool QueryRealTimeWeather(const std::wstring &code, SRealTimeWeather &rt_weather)
    {
        if (code.size() == 9)
            return _queryRealTimeWeatherCity(code, rt_weather);
        else
            return _queryRealTimeWeatherTownOrStreet(code, rt_weather);
    }

    using SWeatherAlert = DataApiWeatherComCnSpider::SWeatherAlert;
    using WeatherAlertList = DataApiWeatherComCnSpider::WeatherAlertList;

    bool QueryWeatherAlerts(const std::wstring &code, WeatherAlertList &alerts)
    {
        alerts.clear();

        std::wstring target_code{ code };
        if (code.size() > 9)
            target_code = code.substr(0, 9);

        auto timeStamp = std::time(0);

        CString q_url;
        q_url.Format(L"https://d1.weather.com.cn/dingzhi/%s.html?_=%I64d", target_code.c_str(), timeStamp);

        CString qHeaders = L"Host: d1.weather.com.cn\r\nReferer: https://www.weather.com.cn/";

        std::wstring content;
        auto succeed = _CallInternet(q_url, qHeaders, content);

        auto data_idx = content.find(L"var alarmDZ");
        if (data_idx == std::wstring::npos)
            succeed = false;
        else
        {
            content = content.substr(data_idx);
            if (content.find(L'{') == std::wstring::npos)
                succeed = false;
        }

        if (succeed && !content.empty())
        {
            auto data = content.substr(content.find(L'{'));

            auto data_utf8 = WeatherCCommon::UnicodeToStr(data.c_str(), true);

            auto *jdoc = yyjson_read(data_utf8.c_str(), data_utf8.size(), YYJSON_READ_STOP_WHEN_DONE);
            if (jdoc != nullptr)
            {
                auto *jroot = yyjson_doc_get_root(jdoc);
                auto *jalert_arr = yyjson_obj_get(jroot, "w");

                auto num_alerts = yyjson_arr_size(jalert_arr);
                if (num_alerts > 0)
                {
                    auto get_value = [](yyjson_val *jval, const char *key) {
                        auto *obj = yyjson_obj_get(jval, key);
                        return WeatherCCommon::StrToUnicode(yyjson_get_str(obj), true);
                    };

                    auto get_alert = [get_value](yyjson_val *jval) {
                        SWeatherAlert alert;
                        alert.Type = get_value(jval, "w5");
                        alert.Level = get_value(jval, "w7");
                        alert.BriefMessage = get_value(jval, "w13");
                        alert.DetailedMessage = get_value(jval, "w9");
                        alert.PublishTime = get_value(jval, "w8");

                        return alert;
                    };

                    for (decltype(num_alerts) i = 0; i < num_alerts; ++i)
                        alerts.push_back(get_alert(yyjson_arr_get(jalert_arr, i)));
                }
            }
            else
                succeed = false;

            yyjson_doc_free(jdoc);
        }

        return succeed;
    }

    using SWeatherInfo = DataApiWeatherComCnSpider::SWeatherInfo;

    bool QueryForecastWeather(const std::wstring &code, SWeatherInfo &weather_td, SWeatherInfo &weather_tm, SWeatherInfo &weather_datm)
    {
        weather_td = SWeatherInfo();
        weather_tm = SWeatherInfo();
        weather_datm = SWeatherInfo();
        if (code.size() == 9) {
            CString url; url.Format(L"https://www.weather.com.cn/weather/%s.shtml", code.c_str());
            std::wstring html;
            if (!_CallInternet(url, L"Referer: https://www.weather.com.cn/", html)) return false;
            const auto begin = html.find(L"<ul class=\"t clearfix\"");
            const auto end = begin == std::wstring::npos ? begin : html.find(L"</ul>", begin);
            if (end == std::wstring::npos) return false;
            const auto list = html.substr(begin, end - begin);
            wslist items;
            for (size_t cursor = 0; items.size() < 3;) {
                const auto start = list.find(L"<li", cursor);
                const auto finish = start == std::wstring::npos ? start : list.find(L"</li>", start);
                if (finish == std::wstring::npos) break;
                items.push_back(list.substr(start, finish - start)); cursor = finish + 5;
            }
            if (items.size() < 3) return false;
            SWeatherInfo* days[] = {&weather_td, &weather_tm, &weather_datm};
            auto extract = [](const std::wstring& value, const wchar_t* pattern) {
                std::wsmatch match; return std::regex_search(value, match, std::wregex(pattern)) ? match[1].str() : L"";
            };
            for (size_t i = 0; i < 3; ++i) {
                auto& day = *days[i]; const auto& item = items[i];
                auto element = [&item](const wchar_t* marker) {
                    auto start = item.find(marker); if (start == std::wstring::npos) return std::wstring{};
                    start = item.find(L'>', start); if (start == std::wstring::npos) return std::wstring{};
                    const auto end = item.find(L"</p>", start);
                    return end == std::wstring::npos ? std::wstring{} : item.substr(start + 1, end - start - 1);
                };
                const auto temperature = element(L"class=\"tem\"");
                day.TemperatureDay = extract(temperature, LR"(<span>(-?\d+))");
                day.TemperatureNight = extract(temperature, LR"(<i>(-?\d+))");
                if (day.TemperatureDay.empty()) day.TemperatureDay = day.TemperatureNight; // Today's daytime may already be over.
                const auto weather = element(L"class=\"wea\"");
                const auto split = weather.find(L'转');
                day.WeatherDay = weather.substr(0, split);
                day.WeatherNight = split == std::wstring::npos ? weather : weather.substr(split + 1);
                const auto icons = _RegexExtractAll(item, std::wregex(LR"icon(class="png40 ([dn]\d+)")icon"));
                day.WeatherCodeDay = _convertWeatherNameToCode(day.WeatherDay);
                day.WeatherCodeNight = _convertWeatherNameToCode(day.WeatherNight);
                if (!icons.empty()) day.WeatherCodeDay = icons.front()[1];
                if (icons.size() > 1) day.WeatherCodeNight = icons.back()[1];
                if (day.TemperatureNight.empty() || weather.empty()) return false;
            }
            return true;
        }

        CString q_url;
        CString qHeaders;

        if (code.size() == 9)
        {
            q_url.Format(L"https://www.weather.com.cn/weathern/%s.shtml", code.c_str());
            qHeaders.Format(L"Host: www.weather.com.cn\r\nReferer: https://www.weather.com.cn/weather1dn/%s.shtml", code.c_str());
        }
        else
        {
            q_url.Format(L"https://forecast.weather.com.cn/town/weathern/%s.shtml", code.c_str());
            qHeaders.Format(L"Host: forecast.weather.com.cn\r\nReferer: https://forecast.weather.com.cn/town/weather1dn/%s.shtml", code.c_str());
        }

        std::wstring content;
        auto succeed = _CallInternet(q_url, qHeaders, content);

        if (succeed && !content.empty())
        {
            // 解析温度数据
            static const std::wregex pattern_temp_day{ L"var eventDay.*?(?=;)" };
            static const std::wregex pattern_temp_night{ L"var eventNight.*?(?=;)" };
            static const std::wregex pattern_temp_values{ LR"((-?\d+))" };

            {
                auto match_results = _RegexExtractAll(content, pattern_temp_day);
                if (!match_results.empty())
                {
                    const auto &temp_day_str = match_results[0][0];
                    auto temp_values = _RegexExtractAll(temp_day_str, pattern_temp_values);
                    if (temp_values.size() >= 3)
                    {
                        weather_td.TemperatureDay = temp_values[1][1];
                        weather_tm.TemperatureDay = temp_values[2][1];
                        if (temp_values.size() > 3) weather_datm.TemperatureDay = temp_values[3][1];
                    }
                    else
                        succeed = false;
                }
                else
                    succeed = false;
            }

            if (succeed)
            {
                auto match_results = _RegexExtractAll(content, pattern_temp_night);
                if (!match_results.empty())
                {
                    const auto &temp_night_str = match_results[0][0];
                    auto temp_values = _RegexExtractAll(temp_night_str, pattern_temp_values);
                    if (temp_values.size() >= 3)
                    {
                        weather_td.TemperatureNight = temp_values[1][1];
                        weather_tm.TemperatureNight = temp_values[2][1];
                        if (temp_values.size() > 3) weather_datm.TemperatureNight = temp_values[3][1];
                    }
                    else
                        succeed = false;
                }
                else
                    succeed = false;
            }

            // 解析天气
            static const std::wregex pattern_weather{ LR"(<i class=\"item-icon ([dn]\d+) icons_bg" title=\"(.+?)\">)" };

            if (succeed)
            {
                auto match_results = _RegexExtractAll(content, pattern_weather);
                if (match_results.size() >= 4)
                {
                    weather_td.WeatherCodeDay = match_results[0][1];
                    weather_td.WeatherDay = match_results[0][2];

                    weather_td.WeatherCodeNight = match_results[1][1];
                    weather_td.WeatherNight = match_results[1][2];

                    weather_tm.WeatherCodeDay = match_results[2][1];
                    weather_tm.WeatherDay = match_results[2][2];

                    weather_tm.WeatherCodeNight = match_results[3][1];
                    weather_tm.WeatherNight = match_results[3][2];
                    if (match_results.size() >= 6) { weather_datm.WeatherCodeDay = match_results[4][1]; weather_datm.WeatherDay = match_results[4][2]; weather_datm.WeatherCodeNight = match_results[5][1]; weather_datm.WeatherNight = match_results[5][2]; }
                }
                else
                    succeed = false;
            }
        }

        return succeed;
    }
}

bool DataApiWeatherComCnSpider::QueryCity(const std::wstring &query, CityInfoList &info)
{
    return wccs::QueryCityInfo(query, info);
}

std::wstring DataApiWeatherComCnSpider::GetWeatherInfoSummary()
{
    const auto &app_config = WeatherCDataManager::Instance().GetConfig();

    std::wostringstream oss;

    // format realtime weather information
    oss << GetWeatherText(EWeatherInfoType::WEATHER_REALTIME) << L" "
        << GetTemprature(EWeatherInfoType::WEATHER_REALTIME) << L" "
        << L"(" << _realtimeWeather.UpdateTime << L")";
    if (!app_config.m_show_brief_rt_weather_info)
    {
        oss << L" PM2.5: " << _realtimeWeather.AqiPM25
            << "  " << _realtimeWeather.WindDirection << _realtimeWeather.WindStrength;
    }
    oss << std::endl;

    // format weather alerts information
    if (app_config.m_show_weather_alerts && !_weatherAlerts.empty())
    {
        if (app_config.m_show_brief_weather_alert_info)
            for (const auto &alert : _weatherAlerts)
                oss << L"[!] " << alert.BriefMessage << L"(" << alert.PublishTime << L")" << std::endl;
        else
            for (const auto &alert : _weatherAlerts)
                oss << L"[!] " << alert.DetailedMessage << std::endl;
    }

    // format forcast weather information
    // - today
    oss << L"今天: " << GetWeatherText(EWeatherInfoType::WEATHER_TODAY)
        << L" " << GetTemprature(EWeatherInfoType::WEATHER_TODAY) << std::endl;
    // - tommorow
    oss << L"明天: " << GetWeatherText(EWeatherInfoType::WEATHER_TOMMROW)
        << L" " << GetTemprature(EWeatherInfoType::WEATHER_TOMMROW);

    // end

    return oss.str();
}

std::wstring DataApiWeatherComCnSpider::GetTemprature(EWeatherInfoType type)
{
    std::wostringstream oss;

    switch (type)
    {
    case EWeatherInfoType::WEATHER_REALTIME:
        oss << _realtimeWeather.Temperature << L"℃";
        break;

    case EWeatherInfoType::WEATHER_TODAY:
        oss << _weatherTD.TemperatureNight << L"℃";
        if (_weatherTD.TemperatureDay != _weatherTD.TemperatureNight) oss << L"~" << _weatherTD.TemperatureDay << L"℃";
        break;

    case EWeatherInfoType::WEATHER_TOMMROW:
        oss << _weatherTM.TemperatureNight << L"℃~" << _weatherTM.TemperatureDay << L"℃";
        break;

    case EWeatherInfoType::WEATHER_DAY_AFTER_TOMMROW:
        if (!_weatherDATM.TemperatureNight.empty() && !_weatherDATM.TemperatureDay.empty()) oss << _weatherDATM.TemperatureNight << L"℃~" << _weatherDATM.TemperatureDay << L"℃";
        break;
    default:
        break;
    }

    return oss.str();
}

std::wstring DataApiWeatherComCnSpider::GetWeatherText(EWeatherInfoType type)
{
    std::wostringstream oss;

    switch (type)
    {
    case EWeatherInfoType::WEATHER_REALTIME:
        oss << _realtimeWeather.Weather;
        break;

    case EWeatherInfoType::WEATHER_TODAY:
        if (_weatherTD.WeatherDay == _weatherTD.WeatherNight)
            oss << _weatherTD.WeatherDay;
        else
            oss << _weatherTD.WeatherDay << L"转" << _weatherTD.WeatherNight;
        break;

    case EWeatherInfoType::WEATHER_TOMMROW:
        if (_weatherTM.WeatherDay == _weatherTM.WeatherNight)
            oss << _weatherTM.WeatherDay;
        else
            oss << _weatherTM.WeatherDay << L"转" << _weatherTM.WeatherNight;
        break;

    case EWeatherInfoType::WEATHER_DAY_AFTER_TOMMROW:
        if (_weatherDATM.WeatherDay == _weatherDATM.WeatherNight) oss << _weatherDATM.WeatherDay;
        else oss << _weatherDATM.WeatherDay << L"转" << _weatherDATM.WeatherNight;
        break;
    default:
        break;
    }

    return oss.str();
}

std::wstring DataApiWeatherComCnSpider::GetWeatherCode(EWeatherInfoType type)
{
    switch (type)
    {
    case EWeatherInfoType::WEATHER_REALTIME:
        return _realtimeWeather.WeatherCode;
    case EWeatherInfoType::WEATHER_TODAY:
        return _weatherTD.WeatherCodeDay;
    case EWeatherInfoType::WEATHER_TOMMROW:
        return _weatherTM.WeatherCodeDay;
    case EWeatherInfoType::WEATHER_DAY_AFTER_TOMMROW:
        return _weatherDATM.WeatherCodeDay;
    default:
        return L"";
    }
}

bool DataApiWeatherComCnSpider::UpdateWeather()
{
    const auto city = WeatherCDataManager::Instance().GetCurrentCityInfo();
    const auto type = WeatherCDataManager::Instance().GetConfig().m_wit;
    if (_dataCity != city.CityNO) { _realtimeWeather = {}; _weatherTD = {}; _weatherTM = {}; _weatherDATM = {}; _weatherAlerts.clear(); _dataCity = city.CityNO; }
    const bool realtime = QueryRealtimeWeather(city.CityNO);
    const bool forecast = QueryForecastWeather(city.CityNO);
    if (WeatherCDataManager::Instance().GetConfig().m_show_weather_alerts) QueryWeatherAlerts(city.CityNO);
    const bool success = type == EWeatherInfoType::WEATHER_REALTIME ? realtime : forecast;
    _lastError = success ? L"" : L"中国天气网未返回完整的天气数据。";
    return success;
}

std::wstring DataApiWeatherComCnSpider::GetLastError() { return _lastError; }

bool DataApiWeatherComCnSpider::QueryRealtimeWeather(const std::wstring &query)
{
    SRealTimeWeather data;
    if (!wccs::QueryRealTimeWeather(query, data)) return false;
    _realtimeWeather = std::move(data); return true;
}

bool DataApiWeatherComCnSpider::QueryForecastWeather(const std::wstring &query)
{
    SWeatherInfo td, tm, datm;
    if (!wccs::QueryForecastWeather(query, td, tm, datm)) return false;
    _weatherTD = std::move(td); _weatherTM = std::move(tm); _weatherDATM = std::move(datm); return true;
}

bool DataApiWeatherComCnSpider::QueryWeatherAlerts(const std::wstring &query)
{
    WeatherAlertList alerts;
    if (!wccs::QueryWeatherAlerts(query, alerts)) return false;
    _weatherAlerts = std::move(alerts); return true;
}
