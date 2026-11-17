#include "../TrafficMonitor/TaskbarDoubleClick.h"
#include <cassert>
#include <limits>

int main()
{
    using namespace TaskbarDoubleClick;
    static_assert(static_cast<int>(DoubleClickAction::CONNECTION_INFO) == 0);
    static_assert(static_cast<int>(DoubleClickAction::HISTORY_TRAFFIC) == 1);
    static_assert(static_cast<int>(DoubleClickAction::SHOW_MORE_INFO) == 2);
    static_assert(static_cast<int>(DoubleClickAction::OPTIONS) == 3);
    static_assert(static_cast<int>(DoubleClickAction::TASK_MANAGER) == 4);
    static_assert(static_cast<int>(DoubleClickAction::SEPCIFIC_APP) == 5);
    static_assert(static_cast<int>(DoubleClickAction::RESERVED_DESKTOP_ACTION) == 6);
    static_assert(static_cast<int>(DoubleClickAction::NONE) == 7);
    static_assert(static_cast<int>(DoubleClickAction::RUN) == 8);
    for (int value : {0, 1, 2, 3, 6, 7, -1, 9})
        assert(EffectiveAction(static_cast<DoubleClickAction>(value)) == DoubleClickAction::NONE);
    for (int value : {4, 5, 8})
        assert(EffectiveAction(static_cast<DoubleClickAction>(value)) == static_cast<DoubleClickAction>(value));

    // Down-to-down timing; a WM_LBUTTONDBLCLK outside these bounds is not enough.
    const POINT origin{ -1920, -100 };
    const UINT interval = Interval(500);
    assert(Matches(100, origin, 350, origin, interval, 4, 4));
    assert(!Matches(100, origin, 351, origin, interval, 4, 4));
    assert(!Matches(100, origin, 600, origin, interval, 4, 4));
    assert(Matches(100, origin, 300, origin, Interval(200), 4, 4));
    assert(!Matches(100, origin, 301, origin, Interval(200), 4, 4));
    assert(Matches(100, origin, 200, {-1922, -102}, interval, 4, 4));
    assert(Matches(100, origin, 200, {-1919, -99}, interval, 4, 4));
    assert(!Matches(100, origin, 200, {-1918, -100}, interval, 4, 4));
    assert(!Matches(100, origin, 200, {-1920, -98}, interval, 4, 4));
    assert(!Matches(100, origin, 200, {-1923, -100}, interval, 4, 4));
    assert(!Matches(100, origin, 200, {-1920, -103}, interval, 4, 4));
    assert(Matches((std::numeric_limits<DWORD>::max)() - 100, origin, 49, origin, 150, 4, 4));
    assert(!Matches((std::numeric_limits<DWORD>::max)() - 100, origin, 50, origin, 150, 4, 4));

    wchar_t executable[32768]{};
    assert(GetModuleFileNameW(nullptr, executable, _countof(executable)) > 0);
    assert(ApplicationPathError(executable) == nullptr);
    assert(NormalizeApplicationPath(L" \t\"" + std::wstring(executable) + L"\" \t") == executable);
    assert(NormalizeApplicationPath(L" \t").empty());
    assert(ApplicationPathError(L"") != nullptr);
    assert(ApplicationPathError(L"Taskmgr.exe") != nullptr);
    assert(ApplicationPathError(L"C:Taskmgr.exe") != nullptr);
    assert(ApplicationPathError(L"https://example.com/app.exe") != nullptr);
    assert(ApplicationPathError(std::wstring(executable) + L" --argument") != nullptr);
    assert(ApplicationPathError(L"\"" + std::wstring(executable) + L"\"") != nullptr);
    assert(ApplicationPathError(std::wstring(executable) + L"\n") != nullptr);
    assert(ApplicationPathError(std::wstring(executable) + std::wstring(1, L'\0')) != nullptr);
    assert(ApplicationPathError(std::filesystem::path(executable).parent_path().wstring()) != nullptr);
    assert(ApplicationPathError(std::wstring(executable) + L".missing.exe") != nullptr);
    assert(ApplicationPathError(std::wstring(executable) + L".cmd") != nullptr);
}
