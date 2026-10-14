#pragma once
#include <chrono>
#include <cstdint>
#include <map>
#include <vector>

namespace TrafficHistorySnapshot
{
    struct Day
    {
        std::chrono::sys_days date;
        uint64_t totalKiB;
    };

    inline std::chrono::sys_days FirstDay(std::chrono::sys_days today)
    {
        using namespace std::chrono;
        return today - days{ weekday{ today }.iso_encoding() - 1 } - weeks{ 11 };
    }

    template<typename Record>
    std::chrono::year_month_day Date(const Record& record)
    {
        return std::chrono::year{ record.year } / std::chrono::month{ static_cast<unsigned>(record.month) }
            / std::chrono::day{ static_cast<unsigned>(record.day) };
    }

    template<typename Record>
    uint64_t Total(const Record& record)
    {
        // Legacy mixed records already store the entire total in down_kBytes.
        return record.mixed ? record.down_kBytes : record.up_kBytes + record.down_kBytes;
    }

    template<typename Records, typename Record>
    std::vector<Day> Build(std::chrono::sys_days today, const Records& history, const Record& current)
    {
        std::map<std::chrono::sys_days, uint64_t> totals;
        const auto first = FirstDay(today);
        for (const auto& record : history)
        {
            const auto date = Date(record);
            if (date.ok() && std::chrono::sys_days{ date } >= first && std::chrono::sys_days{ date } <= today)
                totals[std::chrono::sys_days{ date }] += Total(record);
        }
        const auto currentDate = Date(current);
        if (currentDate.ok() && std::chrono::sys_days{ currentDate } >= first && std::chrono::sys_days{ currentDate } <= today)
            totals[std::chrono::sys_days{ currentDate }] = Total(current);
        std::vector<Day> result;
        for (const auto& [date, total] : totals) result.push_back({ date, total });
        return result;
    }
}
