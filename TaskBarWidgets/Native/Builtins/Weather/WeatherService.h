#pragma once
#include "DataAPI.h"
#include <cstdint>
#include <optional>
#include <string>
#include <vector>
#include <mutex>
#include <condition_variable>
#include <deque>
#include <map>
#include <thread>

class WeatherService
{
public:
    struct City { std::wstring id, name, region; };
    struct ConfigPatch
    {
        int provider{-1};
        std::optional<std::wstring> cityId, cityName, cityRegion, apiHost, apiKey;
        bool clearApiKey{}, credentialsOnly{};
    };
    struct StatusData
    {
        int provider{};
        std::wstring cityId, cityName, cityRegion, apiHost, temperature;
        std::wstring lastSuccessCityId, lastSuccessCityName;
        bool hasApiKey{}, updating{};
        std::wstring state{L"idle"}, message;
        int64_t lastAttemptUnix{}, lastSuccessUnix{};
        uint64_t revision{};
        bool stale{};
        WeatherDetails details;
    };
    struct JobResult
    {
        uint64_t jobId{};
        std::wstring state, message;
        std::vector<City> cities;
        StatusData status;
    };
    static WeatherService& Instance();
    static std::mutex& ConfigurationMutex();
    StatusData Status() const;
    JobResult StartSearch(int provider, const std::wstring& query);
    JobResult StartConfigure(const ConfigPatch& patch);
    JobResult StartRefresh();
    JobResult Result(uint64_t jobId) const;
    static std::optional<std::wstring> NormalizeApiHost(const std::wstring& value);
private:
    WeatherService();
    ~WeatherService();
    enum class Action { Search, Configure, Refresh };
    struct Job { uint64_t id{}; Action action{}; int provider{}; std::wstring query; ConfigPatch patch; };
    JobResult Submit(Job job);
    void Work();
    void Refresh();
    void Configure(const ConfigPatch& patch);
    void UpdateStatus();
    static void Validate(const ConfigPatch& patch);
    mutable std::mutex m_mutex;
    std::condition_variable m_ready;
    std::deque<Job> m_jobs;
    std::map<uint64_t, JobResult> m_results;
    StatusData m_status;
    std::thread m_worker;
    uint64_t m_next{}, m_refreshJob{};
    bool m_stopping{};
};
