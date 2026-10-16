#pragma once
#include "../Builtins/Weather/WeatherService.h"
#include <winrt/Windows.Data.Json.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <optional>
#include <stdexcept>
#include <cmath>
#include <ctime>
#include <string_view>

// Called on the native UI thread. Provider work is queued; no HTTP or secret
// readback occurs here. Job/status payloads are intentionally small and typed.
namespace NativeWeatherBridge
{
    using namespace winrt::Windows::Data::Json;
    inline IJsonValue Text(const std::wstring& value) { return JsonValue::CreateStringValue(value); }
    inline std::wstring Time(int64_t seconds)
    {
        if (seconds <= 0) return L"";
        const time_t time = static_cast<time_t>(seconds);
        tm local{}; if (localtime_s(&local, &time) != 0) return L"";
        wchar_t result[40]{};
        wcsftime(result, _countof(result), L"%Y-%m-%d %H:%M:%S", &local);
        return result;
    }
    inline JsonObject Status(const WeatherService::StatusData& status)
    {
        JsonObject city; city.SetNamedValue(L"id", Text(status.cityId)); city.SetNamedValue(L"name", Text(status.cityName)); city.SetNamedValue(L"region", Text(status.cityRegion));
        JsonObject result;
        result.SetNamedValue(L"provider", JsonValue::CreateNumberValue(status.provider));
        result.SetNamedValue(L"city", city);
        result.SetNamedValue(L"apiHost", Text(status.apiHost));
        result.SetNamedValue(L"hasApiKey", JsonValue::CreateBooleanValue(status.hasApiKey));
        result.SetNamedValue(L"updating", JsonValue::CreateBooleanValue(status.updating));
        result.SetNamedValue(L"state", Text(status.state)); result.SetNamedValue(L"message", Text(status.message));
        result.SetNamedValue(L"lastAttempt", Text(Time(status.lastAttemptUnix)));
        result.SetNamedValue(L"lastSuccess", Text(Time(status.lastSuccessUnix)));
        JsonObject lastCity; lastCity.SetNamedValue(L"id", Text(status.lastSuccessCityId)); lastCity.SetNamedValue(L"name", Text(status.lastSuccessCityName));
        result.SetNamedValue(L"lastSuccessCity", lastCity);
        result.SetNamedValue(L"temperature", Text(status.temperature));
        return result;
    }
    inline JsonObject Job(const WeatherService::JobResult& job)
    {
        JsonObject result;
        const bool pending = job.state == L"queued" || job.state == L"running";
        result.SetNamedValue(L"jobId", JsonValue::CreateNumberValue(static_cast<double>(job.jobId)));
        result.SetNamedValue(L"pending", JsonValue::CreateBooleanValue(pending));
        result.SetNamedValue(L"success", JsonValue::CreateBooleanValue(pending || job.state == L"success"));
        result.SetNamedValue(L"error", Text(pending || job.state == L"success" ? L"" : job.message));
        result.SetNamedValue(L"message", Text(job.message));
        JsonArray cities;
        for (const auto& item : job.cities) {
            JsonObject city; city.SetNamedValue(L"id", Text(item.id)); city.SetNamedValue(L"name", Text(item.name)); city.SetNamedValue(L"region", Text(item.region)); cities.Append(city);
        }
        result.SetNamedValue(L"cities", cities); result.SetNamedValue(L"status", Status(job.status));
        return result;
    }
    inline std::optional<std::wstring> String(const JsonObject& object, const wchar_t* name, size_t maximum)
    {
        if (!object.HasKey(name)) return std::nullopt;
        auto value = object.GetNamedString(name);
        if (value.size() > maximum || std::wstring_view(value).find_first_of(L"\r\n") != std::wstring_view::npos || std::wstring_view(value).find(L'\0') != std::wstring_view::npos)
            throw std::invalid_argument("Invalid weather setting length or characters.");
        return std::wstring(value);
    }
    inline int Provider(const JsonObject& object)
    {
        const double value = object.GetNamedNumber(L"provider");
        if (value != 0 && value != 1) throw std::invalid_argument("Invalid weather provider.");
        return static_cast<int>(value);
    }
    inline JsonObject Handle(const JsonObject& payload)
    {
        auto& service = WeatherService::Instance();
        const auto operation = payload.GetNamedString(L"operation");
        if (operation == L"status") return Status(service.Status());
        if (operation == L"search") {
            const auto query = String(payload, L"query", 80);
            if (!query || query->empty()) throw std::invalid_argument("Enter a city or district name.");
            return Job(service.StartSearch(Provider(payload), *query));
        }
        if (operation == L"refresh") return Job(service.StartRefresh());
        if (operation == L"result") {
            const double id = payload.GetNamedNumber(L"jobId");
            if (!std::isfinite(id) || id < 1 || id > 9007199254740991.0 || std::floor(id) != id) throw std::invalid_argument("Invalid weather request identifier.");
            return Job(service.Result(static_cast<uint64_t>(id)));
        }
        if (operation == L"configure") {
            WeatherService::ConfigPatch patch;
            patch.credentialsOnly = payload.GetNamedBoolean(L"credentialsOnly", false);
            patch.clearApiKey = payload.GetNamedBoolean(L"clearApiKey", false);
            patch.apiKey = String(payload, L"apiKey", 1024); // Write-only; never included in Status/Job.
            patch.apiHost = String(payload, L"apiHost", 255);
            if (payload.HasKey(L"provider")) patch.provider = Provider(payload);
            if (payload.HasKey(L"city")) {
                const auto city = payload.GetNamedObject(L"city");
                patch.cityId = String(city, L"id", 64); patch.cityName = String(city, L"name", 128); patch.cityRegion = String(city, L"region", 256);
            }
            return Job(service.StartConfigure(std::move(patch)));
        }
        throw std::invalid_argument("Unsupported weather operation.");
    }
}
