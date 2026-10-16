#include "../Builtins/Weather/pch.h"
#include "../Builtins/Weather/Common.h"
#include "../Builtins/Weather/DataManager.h"
#include "../Builtins/Weather/WeatherService.h"
#include <atomic>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <stdexcept>

CWinApp testApplication;
namespace {
    std::atomic<bool> badRealtime{}, optionalFailure{}, invalidAuthorization{}, blockRealtime{};
    std::atomic<int> qweatherCode{};
    HANDLE requestEntered{}, releaseRequest{};
    void Require(bool condition, const char* message) { if (!condition) throw std::runtime_error(message); }
    bool Transport(const wchar_t* address, const wchar_t* header, std::wstring& content, bool redirects)
    {
        const std::wstring url(address), headers(header ? header : L"");
        if (url.find(L"qweatherapi.com") != std::wstring::npos) {
            if (redirects || headers.find(L"X-QW-Api-Key: fixture-key") == std::wstring::npos || url.find(L"fixture-key") != std::wstring::npos || url.find(L"https://test.qweatherapi.com/") != 0) invalidAuthorization = true;
            if (qweatherCode) { content = L"{\"code\":\"" + std::to_wstring(qweatherCode.load()) + L"\"}"; return true; }
            if (url.find(L"/geo/v2/city/lookup") != std::wstring::npos) content = LR"({"code":"200","location":[{"name":"南京","id":"101190101","adm2":"南京","adm1":"江苏"}]})";
            else if (url.find(L"/v7/weather/now") != std::wstring::npos) content = LR"({"code":"200","now":{"temp":"23","feelsLike":"22","obsTime":"bad","text":"晴","icon":"100"}})";
            else if (url.find(L"/v7/weather/3d") != std::wstring::npos) content = LR"({"code":"200","daily":[{"tempMin":"19","tempMax":"25"},{"tempMin":"20","tempMax":"26"},{"tempMin":"21","tempMax":"27"}]})";
            else content = LR"({"code":"200","warning":[],"now":{}})";
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
        content = LR"(var alarmDZ={"w":[]};var ignored={};)";
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
        const auto ini = directory / L"WeatherPro.ini";
        { std::ofstream file(ini, std::ios::binary); file << "\xEF\xBB\xBF; preserved comment\r\n[config]\r\napi_type=0\r\ncity_no=101010100\r\ncity_name=北京\r\nwit=0\r\nshow_weather_icon=1\r\nshow_weather_alerts=0\r\n[hfw]\r\nAppKey=fixture-key\r\nunknown=x\r\n[custom]\r\nkeep=unchanged\r\n"; }
        auto& data = WeatherCDataManager::InstanceRef(); data.LoadConfigs(directory.wstring());
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
        for (const auto* host : {L"qweatherapi.com", L"test.qweatherapi.com.evil.test", L"test.qweatherapi.com/path", L"test.qweatherapi.com:443", L"user@test.qweatherapi.com", L"http://test.qweatherapi.com", L".qweatherapi.com"}) Require(!WeatherService::NormalizeApiHost(host), "unsafe host accepted");
        auto& service = WeatherService::Instance();
        const auto cities = Wait(service.StartSearch(0, L"南京"));
        Require(cities.state == L"success" && cities.cities.size() == 1 && cities.cities[0].id == L"101190101", "safe JSONP city parsing");
        WeatherService::ConfigPatch credentials; credentials.credentialsOnly = true; credentials.provider = 1; credentials.cityId = L"101190101"; credentials.apiHost = L"https://TEST.qweatherapi.com"; credentials.apiKey = L"";
        const auto saved = Wait(service.StartConfigure(credentials));
        Require(saved.state == L"success" && saved.status.provider == 0 && saved.status.cityId == L"101010100" && saved.status.hasApiKey && saved.status.lastAttemptUnix == 0, "credentials-only changed city or refreshed");
        const auto bytes = Read(ini);
        Require(bytes.find("; preserved comment\r\n") != std::string::npos && bytes.find("AppKey=fixture-key\r\nunknown=x") != std::string::npos && bytes.find("keep=unchanged") != std::string::npos, "INI preservation");
        WeatherService::ConfigPatch city; city.provider = 0; city.cityId = L"101190101"; city.cityName = L"南京"; city.cityRegion = L"南京-江苏";
        auto result = Wait(service.StartConfigure(city));
        Require(result.state == L"success" && result.status.temperature == L"22.6℃" && result.status.lastSuccessCityId == L"101190101" && result.status.cityRegion == L"南京-江苏", "configure/refresh snapshot");
        Require(data.m_api_wccs->GetTemprature(EWeatherInfoType::WEATHER_TOMMROW) == L"21℃~29℃" && data.m_api_wccs->GetWeatherText(EWeatherInfoType::WEATHER_TOMMROW) == L"多云转小雨" && data.m_api_wccs->GetTemprature(EWeatherInfoType::WEATHER_DAY_AFTER_TOMMROW) == L"18℃~26℃", "current forecast HTML");
        optionalFailure = true; result = Wait(service.StartRefresh()); Require(result.state == L"success", "optional failure discarded realtime"); optionalFailure = false;
        badRealtime = true; city.cityId = L"101010100"; city.cityName = L"北京"; result = Wait(service.StartConfigure(city));
        Require(result.state == L"error" && result.status.temperature == L"22.6℃" && result.status.lastSuccessCityId == L"101190101" && result.message.find(L"南京") != std::wstring::npos && result.message.find(L"城市设置已保存") != std::wstring::npos, "failed city switch mislabeled retained cache"); badRealtime = false;
        blockRealtime = true; auto pending = service.StartRefresh(); Require(WaitForSingleObject(requestEntered, 2000) == WAIT_OBJECT_0, "request did not enter transport");
        const auto start = GetTickCount64(); const auto snapshot = service.Status(); Require(GetTickCount64() - start < 100 && snapshot.updating, "status blocked on network");
        const auto same = service.StartRefresh(); Require(same.jobId == pending.jobId, "refresh not coalesced"); SetEvent(releaseRequest); Require(Wait(pending).state == L"success", "blocked refresh failed"); blockRealtime = false;
        const auto qCities = Wait(service.StartSearch(1, L"南京")); Require(qCities.state == L"success" && qCities.cities.size() == 1 && !invalidAuthorization, "QWeather header/host contract");
        qweatherCode = 401; result = Wait(service.StartSearch(1, L"南京")); Require(result.state == L"error" && result.message.find(L"401") != std::wstring::npos && result.message.find(L"fixture-key") == std::wstring::npos, "QWeather JSON auth error missing"); qweatherCode = 0;
        credentials.clearApiKey = true; result = Wait(service.StartConfigure(credentials)); Require(result.state == L"success" && !result.status.hasApiKey, "explicit key clearing");
        city.provider = 1; result = Wait(service.StartConfigure(city)); Require(result.state == L"error" && result.status.lastAttemptUnix > 0 && result.message.find(L"API Key") != std::wstring::npos, "missing credential status");
        Require(!invalidAuthorization, "unsafe request transport");
        std::cout << "Weather worker, city/config preservation, secure QWeather host/header, parser, cache and nonblocking status regressions passed.\n";
        return 0;
    }
    catch (const std::exception& error) { std::cerr << error.what() << "\n"; return 1; }
}
