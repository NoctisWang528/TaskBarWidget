#include "../Builtins/Weather/pch.h"
#include "../Builtins/Weather/Common.h"
#include "../Builtins/Weather/DataManager.h"
#include "../Builtins/Weather/WeatherService.h"
#include "../TrafficMonitor/PluginIniOptions.h"
#include <atomic>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <stdexcept>

CWinApp testApplication;
namespace {
    std::atomic<bool> badRealtime{}, optionalFailure{}, invalidAuthorization{}, blockRealtime{};
    std::atomic<int> qweatherCode{}, alertMode{}, chinaAlertMode{};
    std::wstring lastAlertUrl;
    HANDLE requestEntered{}, releaseRequest{};
    void Require(bool condition, const char* message) { if (!condition) throw std::runtime_error(message); }
    bool Transport(const wchar_t* address, const wchar_t* header, std::wstring& content, bool redirects)
    {
        const std::wstring url(address), headers(header ? header : L"");
        if (url.find(L"qweatherapi.com") != std::wstring::npos) {
            if (redirects || headers.find(L"X-QW-Api-Key: fixture-key") == std::wstring::npos || url.find(L"fixture-key") != std::wstring::npos || url.find(L"https://test.qweatherapi.com/") != 0) invalidAuthorization = true;
            if (qweatherCode) { content = L"{\"code\":\"" + std::to_wstring(qweatherCode.load()) + L"\"}"; return true; }
            if (url.find(L"/geo/v2/city/lookup") != std::wstring::npos) {
                const auto cityId = url.find(L"location=101010100") != std::wstring::npos ? L"101010100" : L"101190101";
                content = L"{\"code\":\"200\",\"location\":[{\"name\":\"南京\",\"id\":\"" + std::wstring(cityId) +
                    L"\",\"adm2\":\"南京\",\"adm1\":\"江苏\",\"lat\":\"32.05838\",\"lon\":\"118.79647\"}]}";
                if (alertMode == 6) content = LR"({"code":"200","location":[{"id":"101010100","lat":"NaN","lon":"181"}]})";
            }
            else if (url.find(L"/v7/weather/now") != std::wstring::npos) content = LR"({"code":"200","now":{"temp":"23","feelsLike":"22","obsTime":"bad","text":"晴","icon":"100"}})";
            else if (url.find(L"/v7/weather/3d") != std::wstring::npos) content = LR"({"code":"200","daily":[{"tempMin":"19","tempMax":"25"},{"tempMin":"20","tempMax":"26"},{"tempMin":"21","tempMax":"27"}]})";
            else if (url.find(L"/weatheralert/v1/current/") != std::wstring::npos) {
                lastAlertUrl = url;
                if (alertMode == 1) return false;
                if (alertMode == 2) content = LR"({"metadata":{"zeroResult":true},"alerts":[]})";
                else if (alertMode == 3) content = LR"({"metadata":{},"alerts":[]})";
                else if (alertMode == 4) content = LR"({"metadata":{"zeroResult":false},"alerts":[]})";
                else if (alertMode == 5) content = LR"({"metadata":{"zeroResult":false}})";
                else content = LR"({"metadata":{"zeroResult":false,"attributions":["QWeather synthetic attribution"]},"alerts":[{"id":"active","eventType":{"code":"1006","name":"大风"},"messageType":{"code":"alert"},"color":{"code":"yellow"},"headline":"大风黄色预警","description":"注意防范大风。","severity":"moderate","issuedTime":"2026-10-23T10:00+08:00"},{"id":"cancelled","messageType":{"code":"cancel"},"eventType":{"code":"1006","name":"大风"},"headline":"解除大风预警","description":"预警已解除。"}]})";
            }
            else if (url.find(L"/v7/warning/now") != std::wstring::npos) throw std::runtime_error("retired warning endpoint used");
            else content = LR"({"code":"200","now":{}})";
            return true;
        }
        if (url.find(L"https://") != 0 || !headers.empty() && headers.find(L"fixture-key") != std::wstring::npos) invalidAuthorization = true;
        if (url.find(L"toy1.weather.com.cn") != std::wstring::npos) {
            content = LR"(success_jsonpCallback([{"ref":"bad"},{"ref":"101190101~jiangsu~南京~Nanjing~南京~Nanjing~25~210000~NJ~江苏"},{"ref":"nonnumeric~a~spot"}]);)";
            return true;
        }
        if (url.find(L"/sk_2d/") != std::wstring::npos) {
            if (blockRealtime) { SetEvent(requestEntered); WaitForSingleObject(releaseRequest, 5000); }
            content = badRealtime ? LR"(var dataSK={"temp":"","time":"","weathercode":""};)" : LR"(var dataSK={"temp":"22.6","time":"--","weather":"多云","weathercode":"d01","aqi_pm25":"15"}; var ignored={};)";
            return true;
        }
        if (url.find(L"/weather/") != std::wstring::npos) {
            if (optionalFailure) return false;
            content = LR"(<ul class="t clearfix"><li class="sky"><big class="png40 n07"></big><p title="小雨" class="wea">小雨</p><p class="tem"><i>20℃</i></p></li><li class="sky"><big class="png40 d01"></big><big class="png40 n07"></big><p title="多云转小雨" class="wea">多云转小雨</p><p class="tem"><span>29℃</span>/<i>21℃</i></p></li><li class="sky"><big class="png40 d00"></big><p class="wea">晴</p><p class="tem"><span>26℃</span>/<i>18℃</i></p></li></ul>)";
            return true;
        }
        if (chinaAlertMode == 1) return false;
        if (chinaAlertMode == 2) content = LR"(var alarmDZ={"unexpected":[]};)";
        else if (chinaAlertMode == 3) content = LR"(var alarmDZ={"w":[{"w5":"大风","w7":"黄色","w13":"大风黄色预警","w9":"注意防范大风。","w8":"2026-10-23 10:00"}]};)";
        else content = LR"(var alarmDZ={"w":[]};var ignored={};)";
        return true;
    }
    WeatherService::JobResult Wait(WeatherService::JobResult job)
    {
        Require(job.jobId != 0, "job rejected");
        for (int i = 0; i < 600; ++i) {
            job = WeatherService::Instance().Result(job.jobId);
            if (job.state != L"queued" && job.state != L"running") return job;
            Sleep(10);
        }
        throw std::runtime_error("weather job timeout");
    }
    std::string Read(const std::filesystem::path& path) { std::ifstream file(path, std::ios::binary); return {std::istreambuf_iterator<char>(file), {}}; }
}
int wmain(int argc, wchar_t** argv)
{
    try {
        Require(argc >= 2, "isolated fixture directory required");
        Require(AfxWinInit(GetModuleHandleW(nullptr), nullptr, GetCommandLineW(), SW_HIDE), "MFC init failed");
        AfxSetResourceHandle(GetModuleHandleW(nullptr));
        const std::filesystem::path directory(argv[1]); std::filesystem::create_directories(directory);
        const auto configDirectory = directory / L"config";
        const auto ini = configDirectory / L"weather.ini";
        std::filesystem::create_directories(configDirectory);
        { std::ofstream file(ini, std::ios::binary); file << "\xEF\xBB\xBF; preserved comment\r\n[config]\r\napi_type=0\r\ncity_no=101010100\r\ncity_name=北京\r\nwit=0\r\nshow_weather_icon=1\r\nshow_weather_alerts=0\r\n[hfw]\r\nAppKey=fixture-key\r\nunknown=x\r\n[custom]\r\nkeep=unchanged\r\n"; }
        auto& data = WeatherCDataManager::InstanceRef(); data.LoadConfigs(configDirectory.wstring());
        if (argc > 2 && std::wstring(argv[2]) == L"--live-nanjing") {
            auto& service = WeatherService::Instance();
            const auto query = Wait(service.StartSearch(0, L"南京"));
            Require(query.state == L"success" && !query.cities.empty(), "live Nanjing search failed");
            const auto found = std::find_if(query.cities.begin(), query.cities.end(), [](const auto& city) { return city.id == L"101190101"; });
            Require(found != query.cities.end(), "live Nanjing exact city missing");
            WeatherService::ConfigPatch patch; patch.provider = 0; patch.cityId = found->id; patch.cityName = found->name; patch.cityRegion = found->region;
            const auto result = Wait(service.StartConfigure(patch));
            Require(result.state == L"success" && !result.status.temperature.empty() && result.status.lastSuccessCityId == L"101190101", "live Nanjing configure/refresh failed");
            std::cout << "Live HTTPS Nanjing search -> isolated configure -> refresh passed; no user configuration used.\n";
            return 0;
        }
        requestEntered = CreateEventW(nullptr, TRUE, FALSE, nullptr); releaseRequest = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        WeatherCCommon::SetTestTransport(Transport);
        Require(WeatherCCommon::URLEncode(L"\x0001") == L"%01", "URI byte padding");
        Require(WeatherService::NormalizeApiHost(L"https://TEST.qweatherapi.com") == L"test.qweatherapi.com", "host normalization");
        Require(WeatherCondition::FromWeatherComCn(L"d03") == L"rain" && WeatherCondition::FromWeatherComCn(L"d04") == L"thunder" &&
            WeatherCondition::FromWeatherComCn(L"d07") == L"snow" && WeatherCondition::FromWeatherComCn(L"d54") == L"haze" &&
            WeatherCondition::FromQWeather(L"200") == L"thunder" && WeatherCondition::FromQWeather(L"503") == L"dust",
            "provider-neutral condition mapping");
        for (const auto* host : {L"qweatherapi.com", L"test.qweatherapi.com.evil.test", L"test.qweatherapi.com/path", L"test.qweatherapi.com:443", L"user@test.qweatherapi.com", L"http://test.qweatherapi.com", L".qweatherapi.com"}) Require(!WeatherService::NormalizeApiHost(host), "unsafe host accepted");
        auto& service = WeatherService::Instance();
        const auto cities = Wait(service.StartSearch(0, L"南京"));
        Require(cities.state == L"success" && cities.cities.size() == 1 && cities.cities[0].id == L"101190101", "safe JSONP city parsing");
        WeatherService::ConfigPatch credentials; credentials.credentialsOnly = true; credentials.provider = 1; credentials.cityId = L"101190101"; credentials.apiHost = L"https://TEST.qweatherapi.com"; credentials.apiKey = L"";
        const auto saved = Wait(service.StartConfigure(credentials));
        Require(saved.state == L"success" && saved.status.provider == 0 && saved.status.cityId == L"101010100" && saved.status.hasApiKey && saved.status.lastAttemptUnix == 0, "credentials-only changed city or refreshed");
        const auto bytes = Read(ini);
        Require(bytes.find("; preserved comment\r\n") != std::string::npos && bytes.find("AppKey") == std::string::npos &&
            bytes.find("unknown") != std::string::npos && bytes.find("keep") != std::string::npos &&
            std::filesystem::exists(directory / L"secrets" / L"weather-appkey.bin"),
            "INI preservation and protected credential import");
        const auto verifiedHost = saved.status.apiHost;
        qweatherCode = 401; credentials.apiHost = L"other.qweatherapi.com";
        const auto rejectedConnection = Wait(service.StartConfigure(credentials));
        Require(rejectedConnection.state == L"error" && rejectedConnection.status.apiHost == verifiedHost && Read(ini) == bytes &&
            rejectedConnection.message.find(L"未保存") != std::wstring::npos, "failed connection verification changed configuration");
        qweatherCode = 0; credentials.apiHost = verifiedHost; invalidAuthorization = false;
        WeatherService::ConfigPatch city; city.provider = 0; city.cityId = L"101190101"; city.cityName = L"南京"; city.cityRegion = L"南京-江苏";
        auto result = Wait(service.StartConfigure(city));
        Require(result.state == L"success" && result.status.temperature == L"22.6℃" && result.status.lastSuccessCityId == L"101190101" && result.status.cityRegion == L"南京-江苏", "configure/refresh snapshot");
        Require(result.status.revision > 0 && !result.status.stale && !result.status.details.Current.Temperature.empty() &&
            result.status.details.Forecast.size() == 3 && !result.status.details.Current.ConditionKind.empty(),
            "structured weather snapshot");
        Require(data.m_api_wccs->GetTemprature(EWeatherInfoType::WEATHER_TOMMROW) == L"21℃~29℃" && data.m_api_wccs->GetWeatherText(EWeatherInfoType::WEATHER_TOMMROW) == L"多云转小雨" && data.m_api_wccs->GetTemprature(EWeatherInfoType::WEATHER_DAY_AFTER_TOMMROW) == L"18℃~26℃", "current forecast HTML");
        optionalFailure = true; result = Wait(service.StartRefresh()); Require(result.state == L"success", "optional failure discarded realtime"); optionalFailure = false;
        badRealtime = true; city.cityId = L"101010100"; city.cityName = L"北京"; result = Wait(service.StartConfigure(city));
        Require(result.state == L"error" && result.status.temperature == L"22.6℃" && result.status.lastSuccessCityId == L"101190101" && result.message.find(L"南京") != std::wstring::npos && result.message.find(L"城市设置已保存") != std::wstring::npos, "failed city switch mislabeled retained cache"); badRealtime = false;
        blockRealtime = true; auto pending = service.StartRefresh(); Require(WaitForSingleObject(requestEntered, 2000) == WAIT_OBJECT_0, "request did not enter transport");
        const auto start = GetTickCount64(); const auto snapshot = service.Status(); Require(GetTickCount64() - start < 100 && snapshot.updating, "status blocked on network");
        const auto same = service.StartRefresh(); Require(same.jobId == pending.jobId, "refresh not coalesced"); SetEvent(releaseRequest); Require(Wait(pending).state == L"success", "blocked refresh failed"); blockRealtime = false;
        const auto qCities = Wait(service.StartSearch(1, L"南京")); Require(qCities.state == L"success" && qCities.cities.size() == 1 && !invalidAuthorization, "QWeather header/host contract");
        qweatherCode = 401; result = Wait(service.StartSearch(1, L"南京")); Require(result.state == L"error" && result.message.find(L"401") != std::wstring::npos && result.message.find(L"fixture-key") == std::wstring::npos, "QWeather JSON auth error missing"); qweatherCode = 0;
        {
            auto enabled = Read(ini);
            Require(PluginIniOptions::SetBoolean(enabled, "config", "show_weather_alerts", true), "alert fixture setting missing");
            std::ofstream file(ini, std::ios::binary); file << enabled;
        }
        chinaAlertMode = 3; result = Wait(service.StartRefresh());
        Require(result.state == L"success" && result.status.details.Alerts.size() == 1 && result.status.details.AlertsError.empty(), "China warning snapshot missing");
        chinaAlertMode = 1; result = Wait(service.StartRefresh());
        Require(result.state == L"success" && result.status.details.Alerts.size() == 1 && !result.status.details.AlertsError.empty(), "China warning transport failure hidden or cache discarded");
        chinaAlertMode = 2; result = Wait(service.StartRefresh());
        Require(!result.status.details.AlertsError.empty(), "China malformed warning treated as no warning");
        chinaAlertMode = 0; result = Wait(service.StartRefresh());
        Require(result.status.details.Alerts.empty() && result.status.details.AlertsError.empty(), "China empty warning response did not clear warning/error");
        city.provider = 1; city.cityId = L"101190101"; city.cityName = L"南京";
        result = Wait(service.StartConfigure(city));
        Require(result.state == L"success" && result.status.provider == 1 && result.status.details.Alerts.size() == 1 &&
            result.status.details.Alerts[0].TypeName == L"大风" && result.status.details.Alerts[0].Title == L"大风黄色预警" &&
            result.status.details.Alerts[0].Text == L"注意防范大风。" && result.status.details.Alerts[0].Severity == L"moderate" &&
            result.status.details.Alerts[0].Level == L"黄色" && result.status.details.AlertsAttribution.find(L"QWeather synthetic attribution") != std::wstring::npos &&
            lastAlertUrl.find(L"/weatheralert/v1/current/32.06/118.80") != std::wstring::npos, "new QWeather warning URL, mapping or cancellation filtering failed");
        alertMode = 1; result = Wait(service.StartRefresh());
        Require(result.state == L"success" && result.status.details.Alerts.size() == 1 && !result.status.details.AlertsError.empty() &&
            !result.status.details.AlertsAttribution.empty(), "QWeather warning failure hidden or cache discarded");
        for (int mode : {3, 4, 5}) {
            alertMode = mode; result = Wait(service.StartRefresh());
            Require(!result.status.details.AlertsError.empty() && result.status.details.Alerts.size() == 1, "malformed QWeather warning treated as no warning");
        }
        alertMode = 2; result = Wait(service.StartRefresh());
        Require(result.status.details.Alerts.empty() && result.status.details.AlertsError.empty(), "QWeather zero-result did not clear warning/error");
        alertMode = 1; result = Wait(service.StartRefresh());
        Require(result.status.details.Alerts.empty() && !result.status.details.AlertsError.empty(), "first warning failure silently treated as empty success");
        alertMode = 0; result = Wait(service.StartRefresh());
        Require(result.status.details.Alerts.size() == 1 && result.status.details.AlertsError.empty(), "QWeather warning failure did not recover");
        data.m_api_hfw->config.ShowWeatherAlert = false;
        Require(data.m_api_hfw->QueryWeatherAlerts(city.cityId.value()), "disabled warning query failed");
        const auto disabled = data.m_api_hfw->GetWeatherDetails();
        Require(disabled && disabled->Alerts.empty() && disabled->AlertsError.empty() && disabled->AlertsAttribution.empty(), "disabled warnings retained data or error");
        alertMode = 6; city.cityId = L"101010100"; city.cityName = L"北京"; result = Wait(service.StartConfigure(city));
        Require(result.state == L"success" && result.status.details.Alerts.empty() && !result.status.details.AlertsError.empty(), "invalid coordinates or old-city warnings accepted");
        alertMode = 0;
        Require(data.m_api_hfw->QueryWeatherAlerts(L"120.12,30.34") &&
            lastAlertUrl.find(L"/weatheralert/v1/current/30.34/120.12") != std::wstring::npos,
            "coordinate warning query used a nearby city's coordinates");
        credentials.clearApiKey = true; result = Wait(service.StartConfigure(credentials)); Require(result.state == L"success" && !result.status.hasApiKey, "explicit key clearing");
        city.provider = 1; result = Wait(service.StartConfigure(city)); Require(result.state == L"error" && result.status.lastAttemptUnix > 0 && result.message.find(L"API Key") != std::wstring::npos, "missing credential status");
        Require(!invalidAuthorization, "unsafe request transport");
        std::cout << "Weather worker, city/config preservation, secure QWeather host/header, parser, cache and nonblocking status regressions passed.\n";
        return 0;
    }
    catch (const std::exception& error) { std::cerr << error.what() << "\n"; return 1; }
}
