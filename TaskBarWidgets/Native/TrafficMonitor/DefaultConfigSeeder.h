#pragma once
#include "resource.h"

#include <Windows.h>

#include <filesystem>
#include <string>
#include <vector>

// Factory defaults are compiled into the executable as TEXT resources and copied
// byte-for-byte into the configuration directory the first time the application
// runs. An existing file is never modified: the presence of the target file is the
// only signal, so a user configuration is always authoritative.
namespace DefaultConfigSeeder
{
    enum class Result
    {
        Created,
        AlreadyPresent,
        Failed
    };

    inline bool ReadResourceBytes(unsigned int id, std::vector<unsigned char>& bytes, HMODULE module = nullptr)
    {
        bytes.clear();
        const HRSRC resource = ::FindResourceW(module, MAKEINTRESOURCEW(id), L"TEXT");
        if (resource == nullptr)
            return false;
        const DWORD size = ::SizeofResource(module, resource);
        if (size == 0)
            return false;
        const HGLOBAL handle = ::LoadResource(module, resource);
        if (handle == nullptr)
            return false;
        const void* data = ::LockResource(handle);
        if (data == nullptr)
            return false;
        const auto* first = static_cast<const unsigned char*>(data);
        bytes.assign(first, first + size);
        return true;
    }

    // Creates the target only when it does not exist yet. The payload is written to a
    // sibling temporary file first and then renamed without MOVEFILE_REPLACE_EXISTING,
    // so a crash or a concurrent launch can never leave a truncated configuration and
    // can never overwrite an existing one.
    inline Result WriteIfAbsent(const std::wstring& target, const unsigned char* data, size_t size)
    {
        if (target.empty())
            return Result::Failed;
        if (::GetFileAttributesW(target.c_str()) != INVALID_FILE_ATTRIBUTES)
            return Result::AlreadyPresent;

        const size_t slash = target.find_last_of(L"\\/");
        if (slash != std::wstring::npos && slash > 0)
        {
            const std::wstring parent = target.substr(0, slash);
            std::error_code error;
            std::filesystem::create_directories(parent, error);
            if (error && ::GetFileAttributesW(parent.c_str()) == INVALID_FILE_ATTRIBUTES)
                return Result::Failed;
        }

        const std::wstring temp = target + L".seed-" + std::to_wstring(::GetCurrentProcessId()) + L"-" +
            std::to_wstring(::GetTickCount64()) + L".tmp";
        const HANDLE file = ::CreateFileW(temp.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW,
            FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE)
            return Result::Failed;

        bool written = true;
        size_t offset = 0;
        while (offset < size)
        {
            const size_t remaining = size - offset;
            const DWORD chunk = static_cast<DWORD>(remaining > 0x10000000u ? 0x10000000u : remaining);
            DWORD done = 0;
            if (!::WriteFile(file, data + offset, chunk, &done, nullptr) || done == 0)
            {
                written = false;
                break;
            }
            offset += done;
        }
        if (written)
            written = ::FlushFileBuffers(file) != FALSE;
        ::CloseHandle(file);

        if (!written)
        {
            ::DeleteFileW(temp.c_str());
            return Result::Failed;
        }
        if (::MoveFileExW(temp.c_str(), target.c_str(), MOVEFILE_WRITE_THROUGH))
            return Result::Created;
        const DWORD error = ::GetLastError();
        ::DeleteFileW(temp.c_str());
        return error == ERROR_ALREADY_EXISTS || error == ERROR_FILE_EXISTS ? Result::AlreadyPresent : Result::Failed;
    }

    inline Result SeedOne(unsigned int resource_id, const std::wstring& target, HMODULE module = nullptr)
    {
        std::vector<unsigned char> bytes;
        if (!ReadResourceBytes(resource_id, bytes, module))
            return Result::Failed;
        return WriteIfAbsent(target, bytes.data(), bytes.size());
    }

    inline bool EnsureDirectory(const std::wstring& directory)
    {
        if (directory.empty()) return false;
        if (::GetFileAttributesW(directory.c_str()) != INVALID_FILE_ATTRIBUTES)
            return (::GetFileAttributesW(directory.c_str()) & FILE_ATTRIBUTE_DIRECTORY) != 0;
        return ::CreateDirectoryW(directory.c_str(), nullptr) != FALSE || ::GetLastError() == ERROR_ALREADY_EXISTS;
    }

    // Filesystem preparation is fatal in the caller; factory files are still
    // best-effort so a temporarily unwritable config keeps using embedded defaults.
    inline void SeedMissingDefaults(const std::wstring& config_dir)
    {
        if (config_dir.empty())
            return;
        std::wstring base = config_dir;
        if (base.back() != L'\\' && base.back() != L'/')
            base += L'\\';

        const std::wstring config = base + L"config\\";
        const std::wstring history = base + L"history\\";
        const std::wstring lyrics = base + L"lyrics\\";
        const std::wstring cache = base + L"cache\\lyrics\\";
        const std::wstring secrets = base + L"secrets\\";
        const std::wstring logs = base + L"logs\\";
        const std::wstring migration = base + L"migration\\";
        const std::wstring archive = migration + L"archive\\";
        const std::wstring backups = base + L"backups\\";
        for (const auto& directory : {config, history, lyrics, cache, secrets, logs, migration, archive, backups})
            if (!EnsureDirectory(directory)) return;

        SeedOne(IDR_DEFAULT_CONFIG, config + L"app.ini");
        SeedOne(IDR_DEFAULT_BATTERY_INI, config + L"battery.ini");
        SeedOne(IDR_DEFAULT_POWERMON_INI, config + L"power.ini");
        SeedOne(IDR_DEFAULT_WEATHER_INI, config + L"weather.ini");
    }
}
