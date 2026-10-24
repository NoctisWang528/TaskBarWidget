#pragma once

#include <string>
#include <vector>
#include <memory>
#include <optional>
#include <cstdlib>

enum class EWeatherInfoType
{
    WEATHER_REALTIME,
    WEATHER_TODAY,
    WEATHER_TOMMROW,
    WEATHER_DAY_AFTER_TOMMROW
};

struct SCityInfo
{
    std::wstring CityNO;
    std::wstring CityName;
    std::wstring CityAdministrativeOwnership;
};

using CityInfoList = std::vector<SCityInfo>;

struct WeatherCurrent
{
    std::wstring Temperature;
    std::wstring TemperatureFeelsLike;
    std::wstring WeatherText;
    std::wstring WeatherCode;
    std::wstring ConditionKind;
    std::wstring Humidity;
    std::wstring WindDirection;
    std::wstring WindScale;
    std::wstring WindSpeed;
    std::wstring ObservedAt;
};

struct WeatherForecastDay
{
    std::wstring TemperatureMin;
    std::wstring TemperatureMax;
    std::wstring WeatherDay;
    std::wstring WeatherNight;
    std::wstring WeatherCodeDay;
    std::wstring WeatherCodeNight;
    std::wstring ConditionDayKind;
    std::wstring ConditionNightKind;
};

struct WeatherAirQuality
{
    std::wstring AQI;
    std::wstring Level;
    std::wstring Category;
    std::wstring PM2p5;
    std::wstring PM10;
    std::wstring PublishTime;
    std::wstring IndexCode;
    std::wstring IndexName;
    std::wstring PM2p5Unit;
    std::wstring PM10Unit;
    std::wstring Error;
    std::wstring Attribution;
};

struct WeatherAlert
{
    std::wstring Type;
    std::wstring TypeName;
    std::wstring Level;
    std::wstring Title;
    std::wstring Text;
    std::wstring Severity;
    std::wstring PublishTime;
};

struct WeatherDetails
{
    WeatherCurrent Current;
    std::vector<WeatherForecastDay> Forecast;
    WeatherAirQuality AirQuality;
    std::vector<WeatherAlert> Alerts;
    std::wstring AlertsError;
    std::wstring AlertsAttribution;
};

namespace WeatherCondition
{
    inline std::wstring FromWeatherComCn(const std::wstring& code)
    {
        if (code == L"d00" || code == L"n00") return L"clear";
        if (code == L"d01" || code == L"n01") return L"partly-cloudy";
        if (code == L"d02" || code == L"n02") return L"cloudy";
        if (code == L"d03" || code == L"n03" || code == L"d05" || code == L"d06" ||
            code == L"d08" || code == L"d09" || code == L"d10" || code == L"d11" ||
            code == L"d12" || code == L"d19" || code == L"d21" || code == L"d22" ||
            code == L"d23" || code == L"d24" || code == L"d25" || code == L"d32" ||
            code == L"d97") return L"rain";
        if (code == L"d04" || code == L"n04") return L"thunder";
        if (code == L"d07" || code == L"d13" || code == L"n13" || code == L"d14" ||
            code == L"d15" || code == L"d16" || code == L"d17" || code == L"d26" ||
            code == L"d27" || code == L"d28" || code == L"d98") return L"snow";
        if (code == L"d29" || code == L"d49" || code == L"d54" || code == L"d55" ||
            code == L"d56") return L"haze";
        if (code == L"d30" || code == L"d31" || code == L"d53" || code == L"d57" ||
            code == L"d58") return L"fog";
        return L"unknown";
    }
    inline std::wstring FromQWeather(const std::wstring& code)
    {
        const auto numeric = _wtoi(code.c_str());
        if (numeric == 100 || numeric == 150) return L"clear";
        if (numeric >= 101 && numeric <= 103 || numeric >= 151 && numeric <= 153) return L"partly-cloudy";
        if (numeric == 104 || numeric == 154) return L"overcast";
        if (numeric >= 200 && numeric <= 213) return L"thunder";
        if (numeric >= 300 && numeric <= 318 || numeric == 399) return L"rain";
        if (numeric >= 314 && numeric <= 318 || numeric == 350 || numeric == 351 ||
            numeric == 456 || numeric == 457 || numeric == 499) return L"sleet";
        if (numeric >= 400 && numeric <= 410) return L"snow";
        if (numeric == 503 || numeric == 504 || numeric == 507 || numeric == 508 || numeric == 509) return L"dust";
        if (numeric >= 500 && numeric <= 502 || numeric >= 510 && numeric <= 515) return L"fog";
        if (numeric == 999) return L"unknown";
        return L"unknown";
    }
}

class DataAPI
{
public:
    virtual bool QueryCity(const std::wstring &query, CityInfoList &info) = 0;

    virtual std::wstring GetWeatherInfoSummary() = 0;
    virtual std::wstring GetTemprature(EWeatherInfoType type) = 0;
    virtual std::wstring GetWeatherText(EWeatherInfoType type) = 0;
    virtual std::wstring GetWeatherCode(EWeatherInfoType type) = 0;
    virtual std::optional<WeatherDetails> GetWeatherDetails() const = 0;

    virtual bool UpdateWeather() = 0;

    virtual std::wstring GetLastError() = 0;
};

using DataApiPtr = std::shared_ptr<DataAPI>;
