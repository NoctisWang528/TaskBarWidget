#pragma once
#include "resource.h"

#include <Windows.h>

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
            if (::GetFileAttributesW(parent.c_str()) == INVALID_FILE_ATTRIBUTES &&
                !::CreateDirectoryW(parent.c_str(), nullptr) && ::GetLastError() != ERROR_ALREADY_EXISTS)
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

    // Failures are intentionally silent: a read-only installation falls back to the
    // built-in defaults instead of blocking startup.
    inline void SeedMissingDefaults(const std::wstring& config_dir)
    {
        if (config_dir.empty())
            return;
        std::wstring base = config_dir;
        if (base.back() != L'\\' && base.back() != L'/')
            base += L'\\';

        SeedOne(IDR_DEFAULT_CONFIG, base + L"config.ini");

        const std::wstring plugins = base + L"plugins\\";
        if (::GetFileAttributesW(plugins.c_str()) == INVALID_FILE_ATTRIBUTES)
            ::CreateDirectoryW(plugins.c_str(), nullptr);
        SeedOne(IDR_DEFAULT_BATTERY_INI, plugins + L"Battery.dll.ini");
        SeedOne(IDR_DEFAULT_POWERMON_INI, plugins + L"PowerMonPlugin.dll.ini");
        SeedOne(IDR_DEFAULT_WEATHER_INI, plugins + L"WeatherPro.ini");
    }
}
