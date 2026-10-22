#pragma once
#include "../Updater/UpdateLock.h"
#include <Windows.h>
#include <ShlObj.h>
#include <shellapi.h>
#include <filesystem>
#include <fstream>
#include <cmath>
#include <bcrypt.h>
#include <winrt/Windows.Data.Json.h>
#pragma comment(lib, "bcrypt.lib")

namespace UpdateHealth
{
    using Json = winrt::Windows::Data::Json::JsonObject;
    using Value = winrt::Windows::Data::Json::JsonValue;
    struct Job { std::filesystem::path path, work; std::wstring nonce, version; bool reported{}; };
    inline Job& Current() { static Job job; return job; }
    inline std::wstring Normal(const std::filesystem::path& path)
    {
        auto normalized = std::filesystem::absolute(path).lexically_normal();
        auto value = normalized.make_preferred().wstring();
        while (value.size() > 3 && (value.back() == L'\\' || value.back() == L'/')) value.pop_back();
        return value;
    }
    inline void SafePath(std::filesystem::path path)
    {
        while (!path.empty())
        {
            auto attributes = GetFileAttributesW(path.c_str());
            if (attributes == INVALID_FILE_ATTRIBUTES) {
                const auto error = GetLastError();
                if (error != ERROR_FILE_NOT_FOUND && error != ERROR_PATH_NOT_FOUND) throw std::runtime_error("Cannot inspect update path.");
            } else if (attributes & FILE_ATTRIBUTE_REPARSE_POINT) throw std::runtime_error("Linked update path rejected.");
            auto parent = path.parent_path(); if (parent == path) break; path = parent;
        }
    }
    inline Json Read(const std::filesystem::path& path, size_t maximum = 128 * 1024)
    {
        SafePath(path);
        std::ifstream file(path, std::ios::binary | std::ios::ate);
        const auto length = file.tellg();
        if (!file || length <= 0 || static_cast<size_t>(length) > maximum) throw std::runtime_error("Invalid update status file.");
        std::string bytes(static_cast<size_t>(length), '\0'); file.seekg(0); file.read(bytes.data(), length);
        if (!file) throw std::runtime_error("Cannot read update status file.");
        if (bytes.starts_with("\xEF\xBB\xBF")) bytes.erase(0, 3);
        return Json::Parse(winrt::to_hstring(bytes));
    }
    inline void Write(const std::filesystem::path& path, const Json& value)
    {
        SafePath(path);
        const auto temporary = path.wstring() + L".native-" + std::to_wstring(GetCurrentProcessId());
        SafePath(temporary);
        const auto bytes = winrt::to_string(value.Stringify());
        HANDLE file = CreateFileW(temporary.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) throw std::runtime_error("Cannot create update acknowledgement.");
        DWORD written{};
        const bool saved = WriteFile(file, bytes.data(), static_cast<DWORD>(bytes.size()), &written, nullptr) && written == bytes.size() && FlushFileBuffers(file);
        CloseHandle(file);
        if (!saved || !MoveFileExW(temporary.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
        { DeleteFileW(temporary.c_str()); throw std::runtime_error("Cannot save update acknowledgement."); }
    }
    inline std::filesystem::path CacheRoot()
    {
        PWSTR local{};
        if (FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &local))) throw std::runtime_error("Update cache unavailable.");
        auto cache = std::filesystem::path(local) / L"TaskBarWidgets/Updates"; CoTaskMemFree(local);
        return cache;
    }
    inline std::wstring InstallationCacheKey(const std::filesystem::path& root)
    {
        // Match Media's SHA256(UTF8(Path.GetFullPath(root).ToUpperInvariant())).
        auto normalized = Normal(root);
        const auto length = LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_UPPERCASE, normalized.data(),
            static_cast<int>(normalized.size()), nullptr, 0, nullptr, nullptr, 0);
        if (!length) throw std::runtime_error("Cannot normalize update installation.");
        std::wstring upper(length, L'\0');
        if (!LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_UPPERCASE, normalized.data(), static_cast<int>(normalized.size()),
            upper.data(), length, nullptr, nullptr, 0)) throw std::runtime_error("Cannot normalize update installation.");
        auto bytes = winrt::to_string(upper);
        UCHAR hash[32]{};
        if (BCryptHash(BCRYPT_SHA256_ALG_HANDLE, nullptr, 0, reinterpret_cast<PUCHAR>(bytes.data()),
            static_cast<ULONG>(bytes.size()), hash, sizeof(hash)) < 0) throw std::runtime_error("Cannot identify update cache.");
        constexpr wchar_t hex[] = L"0123456789abcdef";
        std::wstring key;
        for (auto byte : hash) { key += hex[byte >> 4]; key += hex[byte & 15]; }
        return key;
    }
    inline Job VerifyInCache(const std::filesystem::path& path, const std::wstring& nonce,
        const std::filesystem::path& root, const std::filesystem::path& cacheRoot)
    {
        if (nonce.size() != 64 || nonce.find_first_not_of(L"0123456789abcdef") != std::wstring::npos) throw std::runtime_error("Invalid update nonce.");
        const std::filesystem::path full = Normal(path);
        const auto work = full.parent_path();
        const auto jobs = std::filesystem::path(Normal(cacheRoot)) / InstallationCacheKey(root) / L"jobs";
        if (full.filename() != L"job.json" || _wcsicmp(Normal(work.parent_path()).c_str(), Normal(jobs).c_str()) != 0)
            throw std::runtime_error("Unexpected update job location.");
        auto job = Read(full);
        if (job.GetNamedNumber(L"protocolVersion", 0) != 2 || job.GetNamedString(L"nonce", L"") != nonce ||
            _wcsicmp(Normal(job.GetNamedString(L"installRoot").c_str()).c_str(), Normal(root).c_str()) != 0 ||
            _wcsicmp(Normal(job.GetNamedString(L"workRoot").c_str()).c_str(), work.c_str()) != 0)
            throw std::runtime_error("Update job does not match this installation.");
        auto status = Read(work / L"status.json");
        const auto number = status.GetNamedNumber(L"helperPid", 0);
        if (number <= 0 || number > MAXDWORD || number != std::floor(number)) throw std::runtime_error("Updater identity missing.");
        HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, static_cast<DWORD>(number));
        wchar_t image[32768]{}; DWORD length = ARRAYSIZE(image);
        const bool valid = process && WaitForSingleObject(process, 0) == WAIT_TIMEOUT && QueryFullProcessImageNameW(process, 0, image, &length) &&
            _wcsicmp(Normal(image).c_str(), Normal(work / L"TaskBarWidgets.Updater.exe").c_str()) == 0;
        if (process) CloseHandle(process);
        if (!valid) throw std::runtime_error("Updater process is no longer valid.");
        return {full, work, nonce, job.GetNamedString(L"version").c_str(), false};
    }
    inline Job Verify(const std::filesystem::path& path, const std::wstring& nonce, const std::filesystem::path& root)
    {
        return VerifyInCache(path, nonce, root, CacheRoot());
    }
    inline std::filesystem::path InterruptedJob(const std::filesystem::path& cacheRoot, const std::filesystem::path& root)
    {
        const auto jobs = std::filesystem::path(Normal(cacheRoot)) / InstallationCacheKey(root) / L"jobs";
        SafePath(jobs);
        if (!std::filesystem::exists(jobs)) return {};
        for (const auto& directory : std::filesystem::directory_iterator(jobs))
        {
            SafePath(directory.path());
            if (!directory.is_directory()) continue;
            const auto journalPath = directory.path() / L"journal.json";
            if (!std::filesystem::exists(journalPath))
            {
                const auto statusPath = directory.path() / L"status.json";
                if (!std::filesystem::exists(statusPath)) continue;
                try {
                    const auto phase = Read(statusPath).GetNamedString(L"phase", L"");
                    if (phase != L"installing" && phase != L"starting" && phase != L"recoveryRequired") continue;
                } catch (...) { }
                return directory.path();
            }
            try {
                const auto job = Read(directory.path() / L"job.json");
                const auto nonce = job.GetNamedString(L"nonce");
                if (job.GetNamedNumber(L"protocolVersion", 0) != 2 ||
                    nonce.size() != 64 || std::wstring(nonce).find_first_not_of(L"0123456789abcdef") != std::wstring::npos ||
                    _wcsicmp(Normal(job.GetNamedString(L"installRoot").c_str()).c_str(), Normal(root).c_str()) != 0 ||
                    _wcsicmp(Normal(job.GetNamedString(L"workRoot").c_str()).c_str(), Normal(directory.path()).c_str()) != 0)
                    return directory.path();
                const auto journal = Read(journalPath, 16 * 1024 * 1024);
                if (journal.GetNamedNumber(L"protocolVersion", 0) != 1 ||
                    journal.GetNamedString(L"nonce", L"") != nonce) return directory.path();
                const auto phase = journal.GetNamedString(L"phase", L"");
                if (phase == L"complete" || phase == L"rolledBack") continue;
            } catch (...) { }
            return directory.path();
        }
        return {};
    }
    inline bool Initialize(const std::filesystem::path& root, const wchar_t* version)
    {
        int count{}; LPWSTR* arguments = CommandLineToArgvW(GetCommandLineW(), &count);
        if (!arguments) return false;
        const bool health = count > 1 && std::wstring(arguments[1]) == L"--update-health";
        try
        {
            if (health)
            {
                if (count != 4) throw std::runtime_error("Invalid update startup arguments.");
                Current() = Verify(arguments[2], arguments[3], root);
                if (Current().version != version) throw std::runtime_error("Update startup version mismatch.");
            }
            else
            {
                HANDLE mutex = OpenMutexW(SYNCHRONIZE, FALSE, TaskBarWidgetsUpdateMutexName(root.wstring()).c_str());
                const bool busy = mutex != nullptr || GetLastError() == ERROR_ACCESS_DENIED;
                if (mutex) CloseHandle(mutex);
                if (busy) throw std::runtime_error("An update is in progress. Please wait before starting the application.");
                // A dead helper releases its mutex. Check durable journals before
                // loading managed/UI components from a potentially mixed install.
                const auto interrupted = InterruptedJob(CacheRoot(), root);
                if (!interrupted.empty()) {
                    const auto message = L"An interrupted update needs recovery before TaskBarWidgets can start.\n\nRun the recovery entry in:\n" + interrupted.wstring();
                    MessageBoxW(nullptr, message.c_str(), L"TaskBarWidgets recovery", MB_OK | MB_ICONWARNING);
                    LocalFree(arguments); return false;
                }
            }
            LocalFree(arguments); return true;
        }
        catch (...)
        {
            LocalFree(arguments);
            MessageBoxW(nullptr, L"TaskBarWidgets is updating, or update startup validation failed. Please wait or use the updater recovery entry.", L"TaskBarWidgets", MB_OK | MB_ICONINFORMATION);
            return false;
        }
    }
    inline void Report(const wchar_t* version)
    {
        auto& current = Current(); if (current.path.empty() || current.reported) return;
        Json health; health.SetNamedValue(L"nonce", Value::CreateStringValue(current.nonce));
        health.SetNamedValue(L"version", Value::CreateStringValue(version)); health.SetNamedValue(L"ready", Value::CreateBooleanValue(true));
        Write(current.work / L"health.json", health); current.reported = true;
    }
    inline bool AwaitingHealth() { return !Current().path.empty() && !Current().reported; }
}
