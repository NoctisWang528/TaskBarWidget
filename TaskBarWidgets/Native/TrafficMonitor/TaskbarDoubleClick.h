#pragma once
#include <windows.h>
#include <cstdint>
#include <filesystem>
#include <string>

// Serialized INI values: append actions without renumbering the legacy entries.
enum class DoubleClickAction
{
    CONNECTION_INFO = 0,
    HISTORY_TRAFFIC = 1,
    SHOW_MORE_INFO = 2,
    OPTIONS = 3,
    TASK_MANAGER = 4,
    SEPCIFIC_APP = 5,
    RESERVED_DESKTOP_ACTION = 6,
    NONE = 7,
    RUN = 8
};

namespace TaskbarDoubleClick
{
    constexpr UINT Interval(UINT systemInterval)
    {
        // Share one short window for single-click dispatch and double-click recognition.
        return systemInterval < 250 ? systemInterval : 250;
    }

    inline DoubleClickAction EffectiveAction(DoubleClickAction action)
    {
        // Legacy 0 is also the loader's default. Do not enable a new gesture for it.
        return action == DoubleClickAction::TASK_MANAGER ||
            action == DoubleClickAction::SEPCIFIC_APP || action == DoubleClickAction::RUN
            ? action : DoubleClickAction::NONE;
    }

    inline bool Matches(DWORD firstTime, POINT first, DWORD secondTime, POINT second,
        UINT interval, int width, int height)
    {
        const auto dx = static_cast<std::int64_t>(second.x) - first.x;
        const auto dy = static_cast<std::int64_t>(second.y) - first.y;
        return static_cast<DWORD>(secondTime - firstTime) <= interval &&
            dx >= -width / 2 && dx < (width + 1) / 2 &&
            dy >= -height / 2 && dy < (height + 1) / 2;
    }

    inline std::wstring NormalizeApplicationPath(std::wstring path)
    {
        const auto first = path.find_first_not_of(L" \t");
        if (first == std::wstring::npos) return {};
        path = path.substr(first, path.find_last_not_of(L" \t") - first + 1);
        if (path.size() >= 2 && path.front() == L'"' && path.back() == L'"')
            path = path.substr(1, path.size() - 2);
        return path;
    }

    inline const wchar_t* ApplicationPathError(const std::wstring& path)
    {
        if (path.empty()) return L"请在“设置 > 任务栏 > 任务栏双击”中选择应用程序。";
        if (path.find_first_of(L"\r\n\"") != std::wstring::npos ||
            path.find(L'\0') != std::wstring::npos || !std::filesystem::path(path).is_absolute())
            return L"请输入完整的应用程序路径，不要附加命令行参数。";
        const auto extension = std::filesystem::path(path).extension().wstring();
        if (_wcsicmp(extension.c_str(), L".exe") != 0 && _wcsicmp(extension.c_str(), L".com") != 0)
            return L"请选择 Windows 应用程序（.exe 或 .com）。";
        const DWORD attributes = GetFileAttributesW(path.c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY))
            return L"应用程序不存在或无法访问，请重新选择有效的应用程序。";
        return nullptr;
    }
}
