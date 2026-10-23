#include "pch.h"
#include "WeatherService.h"
#include "DataManager.h"
#include "WeatherCredentialStore.h"
#include "Common.h"
#include "../../TrafficMonitor/PluginIniOptions.h"
#include <filesystem>
#include <ctime>
#include <cwctype>
#include <stdexcept>

namespace
{
    struct Failure { std::wstring message; };
    bool SafeText(const std::wstring& value, size_t maximum)
    { return value.size() <= maximum && value.find_first_of(L"\r\n\0", 0, 3) == std::wstring::npos; }
    std::string ReadIni(const std::wstring& path)
    {
        HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
        if (file == INVALID_HANDLE_VALUE) throw Failure{L"无法读取天气配置，请检查文件权限。"};
        BY_HANDLE_FILE_INFORMATION information{}; LARGE_INTEGER size{};
        const bool valid = GetFileInformationByHandle(file, &information) && !(information.dwFileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY))
            && GetFileSizeEx(file, &size) && size.QuadPart >= 0 && size.QuadPart <= 256 * 1024;
        if (!valid) { CloseHandle(file); throw Failure{L"天气配置文件的位置或格式不受支持。"}; }
        std::string bytes(static_cast<size_t>(size.QuadPart), '\0'); DWORD count{};
        const bool read = ReadFile(file, bytes.data(), static_cast<DWORD>(bytes.size()), &count, nullptr) && count == bytes.size();
        CloseHandle(file);
        if (!read || bytes.find('\0') != std::string::npos) throw Failure{L"天气配置编码或读取失败，原文件未更改。"};
        return bytes;
    }
    void SaveIni(const std::wstring& path, const std::string& before, const std::string& after)
    {
        if (ReadIni(path) != before) throw Failure{L"天气配置已被其他操作更改，请重试。"};
        const auto temporary = path + L".weather-" + std::to_wstring(GetCurrentProcessId()) + L".tmp";
        HANDLE file = CreateFileW(temporary.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) throw Failure{L"无法创建天气配置临时文件。"};
        DWORD count{}; const bool written = WriteFile(file, after.data(), static_cast<DWORD>(after.size()), &count, nullptr) && count == after.size() && FlushFileBuffers(file);
        CloseHandle(file);
        if (!written || !MoveFileExW(temporary.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
        { DeleteFileW(temporary.c_str()); throw Failure{L"天气配置保存失败，原文件已保留。"}; }
        if (ReadIni(path) != after) throw Failure{L"天气配置保存后的校验失败，请重新读取。"};
    }
    std::wstring NetworkMessage(DataAPI* api = nullptr)
    {
        if (api) { const auto message = api->GetLastError(); if (!message.empty()) return message; }
        const auto http = WeatherCCommon::LastHttpStatus();
        if (http && http != 200) return L"天气服务返回 HTTP " + std::to_wstring(http) + L"，请检查来源和服务配置。";
        if (WeatherCCommon::LastTransportError()) return L"天气服务连接失败或超时，请稍后重试。";
        return L"天气服务未返回可用数据，已保留上次成功天气。";
    }
}

WeatherService& WeatherService::Instance() { static WeatherService service; return service; }
std::mutex& WeatherService::ConfigurationMutex() { static std::mutex mutex; return mutex; }
WeatherService::WeatherService()
{
    // Construct before this service so the worker's configuration lock outlives shutdown.
    (void)ConfigurationMutex();
    UpdateStatus();
    m_worker = std::thread([this] { Work(); });
}
WeatherService::~WeatherService()
{
    { std::lock_guard<std::mutex> lock(m_mutex); m_stopping = true; m_jobs.clear(); }
    WeatherCCommon::StopRequests(); m_ready.notify_one();
    if (m_worker.joinable()) m_worker.join();
}
std::optional<std::wstring> WeatherService::NormalizeApiHost(const std::wstring& value)
{
    std::wstring host = value;
    std::transform(host.begin(), host.end(), host.begin(), [](wchar_t ch) { return static_cast<wchar_t>(std::towlower(ch)); });
    if (host.rfind(L"https://", 0) == 0) host.erase(0, 8);
    const std::wstring suffix = L".qweatherapi.com";
    if (host.size() <= suffix.size() || host.size() > 253 || host.compare(host.size() - suffix.size(), suffix.size(), suffix) != 0
        || host.find_first_not_of(L"abcdefghijklmnopqrstuvwxyz0123456789-.") != std::wstring::npos) return std::nullopt;
    size_t start = 0;
    while (start < host.size()) {
        const auto end = host.find(L'.', start); const auto length = (end == std::wstring::npos ? host.size() : end) - start;
        if (!length || length > 63 || host[start] == L'-' || host[start + length - 1] == L'-') return std::nullopt;
        if (end == std::wstring::npos) break; start = end + 1;
    }
    return host;
}
void WeatherService::Validate(const ConfigPatch& patch)
{
    if (patch.provider < -1 || patch.provider > 1) throw Failure{L"请选择有效的天气来源。"};
    if (patch.cityId && (!SafeText(*patch.cityId, 64) || patch.cityId->empty() || patch.cityId->find_first_not_of(L"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789.,-_") != std::wstring::npos)) throw Failure{L"城市代码无效，请从搜索结果中选择城市。"};
    if ((patch.cityName && (!SafeText(*patch.cityName, 128) || patch.cityName->empty())) || (patch.cityRegion && !SafeText(*patch.cityRegion, 256))) throw Failure{L"城市名称或区域无效。"};
    if (patch.apiHost && !patch.apiHost->empty() && !NormalizeApiHost(*patch.apiHost)) throw Failure{L"请输入专属 qweatherapi.com 域名；仅支持 HTTPS，不接受路径、端口或账户信息。"};
    if (patch.apiKey && (!SafeText(*patch.apiKey, 512) || patch.apiKey->find_first_of(L" \t") != std::wstring::npos)) throw Failure{L"API Key 格式无效。"};
    if (patch.clearApiKey && patch.apiKey && !patch.apiKey->empty()) throw Failure{L"清除密钥与设置新密钥不能同时进行。"};
}
WeatherService::StatusData WeatherService::Status() const { std::lock_guard<std::mutex> lock(m_mutex); return m_status; }
WeatherService::JobResult WeatherService::Result(uint64_t id) const
{
    std::lock_guard<std::mutex> lock(m_mutex);
    const auto found = m_results.find(id);
    if (found == m_results.end()) return {id, L"notFound", L"该天气任务已失效，请重新操作。", {}, m_status};
    auto result = found->second; result.status = m_status; return result;
}
WeatherService::JobResult WeatherService::Submit(Job job)
{
    std::lock_guard<std::mutex> lock(m_mutex);
    if (m_stopping || m_jobs.size() >= 8) return {0, L"error", L"天气任务繁忙，请稍后重试。", {}, m_status};
    if (job.action == Action::Refresh && m_refreshJob) {
        const auto found = m_results.find(m_refreshJob);
        if (found != m_results.end() && (found->second.state == L"queued" || found->second.state == L"running")) { auto result = found->second; result.status = m_status; return result; }
    }
    job.id = ++m_next;
    if (job.action == Action::Refresh) m_refreshJob = job.id;
    m_results[job.id] = {job.id, L"queued", L"天气任务已排队。", {}, m_status};
    m_jobs.push_back(std::move(job)); m_ready.notify_one();
    return m_results[m_next];
}
WeatherService::JobResult WeatherService::StartSearch(int provider, const std::wstring& query)
{
    if (provider < 0 || provider > 1 || query.empty() || !SafeText(query, 80)) return {0, L"error", L"请输入城市名称（最多 80 字）。", {}, Status()};
    Job job; job.action = Action::Search; job.provider = provider; job.query = query; return Submit(std::move(job));
}
WeatherService::JobResult WeatherService::StartConfigure(const ConfigPatch& patch)
{
    try { Validate(patch); } catch (const Failure& failure) { return {0, L"error", failure.message, {}, Status()}; }
    Job job; job.action = Action::Configure; job.patch = patch; return Submit(std::move(job));
}
WeatherService::JobResult WeatherService::StartRefresh() { Job job; job.action = Action::Refresh; return Submit(std::move(job)); }
void WeatherService::UpdateStatus()
{
    auto& data = WeatherCDataManager::InstanceRef(); const auto config = data.GetConfig(); const auto city = data.GetCurrentCityInfo();
    const auto host = NormalizeApiHost(data.m_api_hfw->config.ApiHost);
    std::lock_guard<std::mutex> lock(m_mutex);
    m_status.provider = static_cast<int>(config.m_api_type); m_status.cityId = city.CityNO; m_status.cityName = city.CityName;
    m_status.cityRegion = city.CityAdministrativeOwnership; m_status.apiHost = host.value_or(L""); m_status.hasApiKey = !data.m_api_hfw->config.AppKey.empty();
    m_status.temperature = data.GetWeatherTemperature();
    WeatherDetails details;
    if (data.GetWeatherDetails(details)) m_status.details = std::move(details);
    m_status.stale = m_status.stale ||
        !m_status.lastSuccessCityId.empty() && m_status.lastSuccessCityId != m_status.cityId;
}
void WeatherService::Configure(const ConfigPatch& patch)
{
    auto& data = WeatherCDataManager::InstanceRef(); const auto path = data.ConfigPath();
    if (patch.credentialsOnly && !patch.clearApiKey) {
        data.ReloadConfigs();
        DataApiHefengWeather candidate;
        candidate.config = data.m_api_hfw->config;
        if (patch.apiHost && !patch.apiHost->empty()) candidate.config.ApiHost = *NormalizeApiHost(*patch.apiHost);
        if (patch.apiKey && !patch.apiKey->empty()) candidate.config.AppKey = *patch.apiKey;
        CityInfoList cities;
        if (!candidate.QueryCity(L"101010100", cities) || cities.empty())
            throw Failure{L"连接验证失败，未保存连接配置：" + NetworkMessage(&candidate)};
    }
    {
    std::lock_guard<std::mutex> configuration_lock(ConfigurationMutex());
    const auto before = ReadIni(path); auto after = before;
    auto set = [&](const char* section, const char* key, const std::wstring& text) {
        const auto value = WeatherCCommon::UnicodeToStr(text.c_str(), true);
        if (!PluginIniOptions::EnsureValue(after, section, key, value) || !PluginIniOptions::SetValue(after, section, key, value)) throw Failure{L"天气配置存在重复或不支持的选项，原文件未更改。"};
    };
    if (!patch.credentialsOnly) {
        const int provider = patch.provider < 0 ? static_cast<int>(data.GetConfig().m_api_type) : patch.provider;
        if (patch.cityId && provider == 0 && (patch.cityId->size() < 9 || patch.cityId->find_first_not_of(L"0123456789") != std::wstring::npos)) throw Failure{L"中国天气网城市代码无效，请从搜索结果中选择城市。"};
        if (patch.provider >= 0) set("config", "api_type", std::to_wstring(patch.provider));
        if (patch.cityId) set("config", "city_no", *patch.cityId);
        if (patch.cityName) set("config", "city_name", *patch.cityName);
        if (patch.cityRegion) set("config", "city_region", *patch.cityRegion);
    }
    if (patch.apiHost && !patch.apiHost->empty()) set("hfw", "ApiHost", *NormalizeApiHost(*patch.apiHost));
    const bool credentialOperation = patch.clearApiKey || (patch.apiKey && !patch.apiKey->empty());
    if (patch.clearApiKey)
    {
        if (!WeatherCredentials::Clear(data.CredentialPath())) throw Failure{L"天气密钥清除失败，原配置未更改。"};
    }
    else if (patch.apiKey && !patch.apiKey->empty())
    {
        if (!WeatherCredentials::Write(data.CredentialPath(), *patch.apiKey))
            throw Failure{L"天气密钥保存失败，原配置未更改。"};
    }
    // The INI never carries credentials. Keep a blank AppKey in legacy copies
    // so an interrupted transition cannot leave a readable secret behind.
    if (before.find("AppKey") != std::string::npos &&
        (credentialOperation || std::filesystem::exists(data.CredentialPath())))
        set("hfw", "AppKey", L"");
    if (after != before) SaveIni(path, before, after);
    }
    data.ReloadConfigs(); UpdateStatus();
}
void WeatherService::Refresh()
{
    auto& data = WeatherCDataManager::InstanceRef(); data.ReloadConfigs(); UpdateStatus();
    { std::lock_guard<std::mutex> lock(m_mutex); m_status.lastAttemptUnix = std::time(nullptr); }
    if (data.GetConfig().m_api_type == DataApiType::API_HefengWeather) {
        if (!NormalizeApiHost(data.m_api_hfw->config.ApiHost)) throw Failure{L"请先保存和风天气专属 API Host。"};
        if (data.m_api_hfw->config.AppKey.empty()) throw Failure{L"请先保存和风天气 API Key。"};
    }
    if (!data.RefreshWeather()) {
        auto message = NetworkMessage(data.GetCurrentApi().get());
        const auto city = data.GetCurrentCityInfo();
        { std::lock_guard<std::mutex> lock(m_mutex);
          m_status.stale = true; ++m_status.revision;
          if (!m_status.lastSuccessCityId.empty() && m_status.lastSuccessCityId != city.CityNO) message += L" 当前保留的是上次成功城市（" + m_status.lastSuccessCityName + L"）的数据。"; }
        throw Failure{message};
    }
    UpdateStatus();
    { std::lock_guard<std::mutex> lock(m_mutex); m_status.lastSuccessUnix = std::time(nullptr); m_status.lastSuccessCityId = m_status.cityId; m_status.lastSuccessCityName = m_status.cityName;
      m_status.stale = false; ++m_status.revision; }
}
void WeatherService::Work()
{
    while (true) {
        Job job;
        { std::unique_lock<std::mutex> lock(m_mutex); m_ready.wait(lock, [&] { return m_stopping || !m_jobs.empty(); });
          if (m_stopping) return; job = std::move(m_jobs.front()); m_jobs.pop_front();
          m_results[job.id].state = L"running"; m_status.updating = true; m_status.state = L"updating"; m_status.message = L"正在处理天气任务…"; ++m_status.revision; }
        JobResult result{job.id, L"success", L"天气已更新。"};
        try {
            if (job.action == Action::Search) {
                auto& data = WeatherCDataManager::InstanceRef(); data.ReloadConfigs();
                CityInfoList cities;
                if (job.provider == 0) { DataApiWeatherComCnSpider api; if (!api.QueryCity(job.query, cities)) throw Failure{NetworkMessage(&api)}; }
                else {
                    DataApiHefengWeather api; api.config = data.m_api_hfw->config;
                    if (!NormalizeApiHost(api.config.ApiHost)) throw Failure{L"请先保存和风天气专属 API Host。"};
                    if (api.config.AppKey.empty()) throw Failure{L"请先保存和风天气 API Key。"};
                    if (!api.QueryCity(job.query, cities)) throw Failure{NetworkMessage(&api)};
                }
                for (const auto& city : cities) { if (result.cities.size() >= 20) break; if (SafeText(city.CityNO,64) && SafeText(city.CityName,128) && SafeText(city.CityAdministrativeOwnership,256)) result.cities.push_back({city.CityNO,city.CityName,city.CityAdministrativeOwnership}); }
                result.message = result.cities.empty() ? L"未找到匹配城市，请更换关键词。" : L"请选择搜索结果中的城市。";
            }
            else if (job.action == Action::Configure) {
                Configure(job.patch);
                if (!job.patch.credentialsOnly) {
                    try { Refresh(); }
                    catch (const Failure& failure) { throw Failure{L"城市设置已保存，但刷新失败：" + failure.message}; }
                    catch (...) { throw Failure{L"城市设置已保存，但刷新失败，请稍后重试。"}; }
                }
                else result.message = job.patch.clearApiKey ? L"已清除保存的密钥。" : L"和风天气连接已验证并保存；当前生效来源和城市未改变。";
            }
            else Refresh();
        }
        catch (const Failure& failure) { result.state = L"error"; result.message = failure.message; }
        catch (...) { result.state = L"error"; result.message = L"天气服务响应或配置处理失败，已保留上次成功天气。"; }
        UpdateStatus();
        { std::lock_guard<std::mutex> lock(m_mutex); m_status.updating = false; m_status.state = result.state == L"success" ? L"ready" : L"error"; m_status.message = result.message; ++m_status.revision;
          result.status = m_status; m_results[job.id] = std::move(result);
          while (m_results.size() > 24) m_results.erase(m_results.begin()); }
    }
}
