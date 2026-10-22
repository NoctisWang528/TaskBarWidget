#pragma once

#include "ManagedRuntimeEnvironment.h"

#include <Windows.h>
#include <winrt/base.h>
#include <algorithm>
#include <filesystem>
#include <string>
#include <vector>

namespace NativeDataPaths
{
    struct Layout
    {
        std::filesystem::path DataRoot;
        std::filesystem::path ConfigDirectory;
        std::filesystem::path HistoryDirectory;
        std::filesystem::path LyricsDirectory;
        std::filesystem::path CacheDirectory;
        std::filesystem::path SecretsDirectory;
        std::filesystem::path LogsDirectory;
        std::filesystem::path MigrationDirectory;
        std::filesystem::path BackupsDirectory;
    };

    inline Layout ForInstallRoot(const std::filesystem::path& installRoot)
    {
        Layout paths;
        paths.DataRoot = installRoot / L"Data";
        paths.ConfigDirectory = paths.DataRoot / L"config";
        paths.HistoryDirectory = paths.DataRoot / L"history";
        paths.LyricsDirectory = paths.DataRoot / L"lyrics";
        paths.CacheDirectory = paths.DataRoot / L"cache";
        paths.SecretsDirectory = paths.DataRoot / L"secrets";
        paths.LogsDirectory = paths.DataRoot / L"logs";
        paths.MigrationDirectory = paths.DataRoot / L"migration";
        paths.BackupsDirectory = paths.DataRoot / L"backups";
        return paths;
    }
}

namespace NativeDataStartup
{
    struct Result
    {
        bool Success{};
        DWORD Error{};
        std::wstring Message;
    };

    inline std::wstring Normalize(const std::filesystem::path& path)
    {
        auto value = std::filesystem::absolute(path).lexically_normal().wstring();
        while (value.size() > 3 && (value.back() == L'\\' || value.back() == L'/'))
            value.pop_back();
        return value;
    }

    inline std::wstring ReadPipe(HANDLE pipe)
    {
        std::string bytes;
        char buffer[4096];
        DWORD read = 0;
        while (ReadFile(pipe, buffer, sizeof(buffer), &read, nullptr) && read > 0)
        {
            if (bytes.size() < 64 * 1024)
            {
                const auto remaining = 64 * 1024 - bytes.size();
                bytes.append(buffer, (std::min)(static_cast<size_t>(read), remaining));
            }
        }
        if (bytes.empty()) return {};
        auto wide = winrt::to_hstring(bytes);
        std::wstring text(wide.c_str());
        while (!text.empty() && (text.back() == L'\r' || text.back() == L'\n'))
            text.pop_back();
        const auto line = text.find_last_of(L"\r\n");
        if (line != std::wstring::npos) text.erase(0, line + 1);
        if (text.size() > 2000) text = text.substr(0, 2000) + L"...";
        return text;
    }

    inline std::wstring Win32Message(DWORD error)
    {
        wchar_t* text = nullptr;
        const DWORD length = FormatMessageW(
            FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
            nullptr, error, 0, reinterpret_cast<wchar_t*>(&text), 0, nullptr);
        std::wstring result = length && text ? std::wstring(text, length) : L"Windows error " + std::to_wstring(error);
        if (text) LocalFree(text);
        while (!result.empty() && (result.back() == L'\r' || result.back() == L'\n' || result.back() == L' '))
            result.pop_back();
        return result;
    }

    inline Result RunMigration(const std::filesystem::path& installRoot, const std::filesystem::path& dataRoot)
    {
        const auto data = Normalize(dataRoot);
        if (_wcsicmp(data.c_str(), Normalize(installRoot / L"Data").c_str()) != 0)
            return { false, ERROR_INVALID_PARAMETER, L"The data directory must be the Data directory under the install root." };

        const DWORD attributes = GetFileAttributesW(data.c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES || !(attributes & FILE_ATTRIBUTE_DIRECTORY))
            return { false, GetLastError(), L"The data directory is missing or is not a directory." };
        if (attributes & FILE_ATTRIBUTE_REPARSE_POINT)
            return { false, ERROR_CANT_ACCESS_FILE, L"The data directory cannot be a filesystem link." };

        const auto executable = installRoot / L"Media" / L"TaskBarWidgets.Media.exe";
        if (!std::filesystem::is_regular_file(executable))
            return { false, ERROR_FILE_NOT_FOUND, L"The media component is missing; data migration cannot run." };

        const auto runtime = SystemDotnetRoot();
        if (runtime.empty())
            return { false, ERROR_FILE_NOT_FOUND, L"The system .NET 8 Desktop Runtime x64 was not found." };
        auto environment = ManagedEnvironment(runtime);
        if (environment.empty())
            return { false, ERROR_OUTOFMEMORY, L"The media runtime environment could not be prepared." };

        SECURITY_ATTRIBUTES security{ sizeof(security), nullptr, TRUE };
        HANDLE read = nullptr;
        HANDLE write = nullptr;
        if (!CreatePipe(&read, &write, &security, 0))
        {
            const DWORD error = GetLastError();
            return { false, error, L"Cannot create the migration output pipe: " + Win32Message(error) };
        }
        SetHandleInformation(read, HANDLE_FLAG_INHERIT, 0);

        std::wstring command = L"\"" + executable.wstring() + L"\" --migrate-data \"" +
            std::filesystem::path(data).wstring() + L"\"";
        STARTUPINFOW startup{ sizeof(startup) };
        startup.dwFlags = STARTF_USESHOWWINDOW | STARTF_USESTDHANDLES;
        startup.wShowWindow = SW_HIDE;
        startup.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
        startup.hStdOutput = write;
        startup.hStdError = write;
        PROCESS_INFORMATION process{};
        if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, TRUE,
            CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT, environment.data(), installRoot.c_str(),
            &startup, &process))
        {
            const DWORD error = GetLastError();
            CloseHandle(write);
            CloseHandle(read);
            return { false, error, L"Cannot start the data migration component: " + Win32Message(error) };
        }
        CloseHandle(write);

        const DWORD wait = WaitForSingleObject(process.hProcess, 30 * 60 * 1000);
        if (wait == WAIT_TIMEOUT)
        {
            TerminateProcess(process.hProcess, ERROR_TIMEOUT);
            WaitForSingleObject(process.hProcess, 5000);
        }
        DWORD exitCode = ERROR_GEN_FAILURE;
        GetExitCodeProcess(process.hProcess, &exitCode);
        const auto detail = ReadPipe(read);
        CloseHandle(read);
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);

        if (wait == WAIT_TIMEOUT)
            return { false, ERROR_TIMEOUT, L"Data migration timed out and was stopped. Check Data\\migration, the source files, disk space and permissions before retrying." };
        if (exitCode != 0)
            return { false, exitCode, detail.empty()
                ? L"Data migration failed. See Data\\logs\\error.log for details."
                : detail };
        return { true, ERROR_SUCCESS, L"" };
    }
}
