// Exercise the production loader/normalizer/writer without creating an application or UI.
#include "../TrafficMonitor/HistoryTrafficFile.cpp"
#include "../TrafficMonitor/TrafficHistorySnapshot.h"
#include <cassert>
#include <filesystem>
#include <tuple>
#include <cstdlib>

bool CCommon::FileExist(LPCTSTR path) { return GetFileAttributes(path) != INVALID_FILE_ATTRIBUTES; }
bool Date::DateGreater(const Date& a, const Date& b) { return std::tie(a.year, a.month, a.day) > std::tie(b.year, b.month, b.day); }
bool Date::DateEqual(const Date& a, const Date& b) { return std::tie(a.year, a.month, a.day) == std::tie(b.year, b.month, b.day); }
unsigned __int64 HistoryTraffic::kBytes() const { return up_kBytes + down_kBytes; }

int main(int argc, char** argv)
{
    _set_error_mode(_OUT_TO_STDERR);
    _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
    assert(argc == 2);
    const auto path = std::filesystem::path{ argv[1] } / "history-roundtrip.dat";
    SYSTEMTIME local{};
    GetLocalTime(&local);
    const std::chrono::sys_days today{std::chrono::year{local.wYear}/local.wMonth/local.wDay};
    const auto previous = std::chrono::year_month_day{today - std::chrono::days{1}};
    auto write = [](std::ofstream& file, std::chrono::year_month_day date, const char* value)
    {
        char line[96]{};
        sprintf_s(line, "%04d/%02u/%02u %s\n", static_cast<int>(date.year()), static_cast<unsigned>(date.month()), static_cast<unsigned>(date.day()), value);
        file << line;
    };
    {
        std::ofstream file{path};
        file << "lines: \"3\"\n";
        write(file, std::chrono::year_month_day{today}, "0/0");
        write(file, previous, "123");
        write(file, previous, "10/20");
    }
    CHistoryTrafficFile history{path.wstring()};
    history.Load();
    assert(history.Size() == 2 && history.GetTodayTraffic().kBytes() == 0);
    assert(history.GetHistoryTraffics().front().kBytes() == 153);
    assert(!history.GetHistoryTraffics().front().mixed);
    history.Save();
    history.Load();
    const auto snapshot = TrafficHistorySnapshot::Build(today, history.GetHistoryTraffics(), history.GetTodayTraffic());
    assert(snapshot.size() == 2 && snapshot.front().totalKiB == 153 && snapshot.back().totalKiB == 0);
    // A previous first-line zero day remains a known record when today is elsewhere.
    {
        std::ofstream file{path};
        file << "lines: \"2\"\n";
        write(file, previous, "0/0");
        write(file, std::chrono::year_month_day{today}, "1/2");
    }
    history.Load();
    assert(history.Size() == 2 && history.GetHistoryTraffics().front().kBytes() == 0);
    assert(history.GetTodayTraffic().kBytes() == 3);
    std::filesystem::remove(path);
}
