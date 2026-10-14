#include "../TrafficMonitor/NativeSettingsValidation.h"
#include "../TrafficMonitor/MediaDisplayState.h"
#include "../TrafficMonitor/TrafficHistorySnapshot.h"
#include <cassert>
#include <limits>

void TestTrafficHistory()
{
    using namespace std::chrono;
    struct Record { int year, month, day; uint64_t up_kBytes, down_kBytes; bool mixed; };
    const sys_days today{ year{ 2026 } / 10 / 13 };
    assert(TrafficHistorySnapshot::FirstDay(today) == sys_days{ year{ 2026 } / 7 / 27 });
    const std::vector<Record> history{
        {2026, 7, 26, 1, 1, false}, // Before the 12 calendar weeks.
        {2026, 7, 27, 0, 0, false}, // A known zero must survive.
        {2026, 10, 11, 0, 123, true}, // A legacy total, counted once.
        {2026, 10, 12, 10, 20, false},
        {2026, 10, 13, 100, 200, false}, // Live today replaces this record.
        {2026, 10, 14, 10, 20, false}, // Future records are omitted.
        {2026, 9, 31, 10, 20, false} // Invalid dates cannot become calendar cells.
    };
    const Record current{2026, 10, 13, 150, 250, false};
    const auto days = TrafficHistorySnapshot::Build(today, history, current);
    assert(days.size() == 4);
    assert(days[0].totalKiB == 0 && days[1].totalKiB == 123 && days[2].totalKiB == 30 && days[3].totalKiB == 400);
    assert(days[3].date == today);
    assert(TrafficHistorySnapshot::Total(Record{2026, 10, 11, 999, 123, true}) == 123);
    assert(TrafficHistorySnapshot::FirstDay(sys_days{year{2024}/3/3}) == sys_days{year{2023}/12/11});
    const auto leap = TrafficHistorySnapshot::Build(sys_days{year{2024}/3/3},
        std::vector<Record>{{2024, 2, 29, 1, 2, false}}, Record{2024, 3, 3, 0, 0, false});
    assert(leap.size() == 2 && leap[0].totalKiB == 3 && leap[1].totalKiB == 0);
}

int main()
{
    using namespace NativeSettingsValidation;
    assert(Integer(200, 200, 30000) && Integer(30000, 200, 30000));
    assert(!Integer(199, 200, 30000) && !Integer(30001, 200, 30000));
    assert(!Integer(1000.5, 200, 30000));
    assert(!Integer(std::numeric_limits<double>::quiet_NaN(), 0, 100));
    assert(!Integer(std::numeric_limits<double>::infinity(), 0, 100));
    assert(Integer(-800, -800, 800) && !Integer(-801, -800, 800));
    assert(Color(L"#00aBff") && Color(L"#000000") && Color(L"#FFFFFF"));
    assert(!Color(L"#fff") && !Color(L"000000") && !Color(L"#ffgg00") && !Color(L"#ffffff00"));
    assert(Unexpired(1700000012000.0, 1700000000000LL));
    assert(!Unexpired(1700000000000.0, 1700000000000LL));
    assert(!Unexpired(1699999999999.0, 1700000000000LL));
    assert(!Unexpired(std::numeric_limits<double>::quiet_NaN(), 1700000000000LL));
    TestMediaDisplayState();
    TestTrafficHistory();
}
