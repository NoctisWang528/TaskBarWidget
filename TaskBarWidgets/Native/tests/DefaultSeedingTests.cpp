// Verifies that factory defaults are compiled into the packaged executable and that
// seeding only ever creates a missing file. The packaged executable is opened as a data
// file, so this test never starts the product.
#include <Windows.h>

#include <cstdio>
#include <string>
#include <vector>

#include "../TrafficMonitor/DefaultConfigSeeder.h"

namespace
{
    int failures = 0;

    void Check(bool condition, const char* what)
    {
        if (condition)
            return;
        std::printf("FAIL: %s\n", what);
        ++failures;
    }

    std::wstring Widen(const char* text)
    {
        wchar_t buffer[1024]{};
        ::MultiByteToWideChar(CP_UTF8, 0, text, -1, buffer, 1023);
        return buffer;
    }

    std::vector<unsigned char> ReadBytes(const std::wstring& path)
    {
        std::vector<unsigned char> data;
        const HANDLE file = ::CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE)
            return data;
        const DWORD size = ::GetFileSize(file, nullptr);
        data.resize(size);
        DWORD done = 0;
        if (size != 0 && !::ReadFile(file, data.data(), size, &done, nullptr))
            data.clear();
        ::CloseHandle(file);
        if (done != size)
            data.clear();
        return data;
    }

    bool WriteBytes(const std::wstring& path, const std::string& text)
    {
        const HANDLE file = ::CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
            FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE)
            return false;
        DWORD done = 0;
        const bool ok = ::WriteFile(file, text.data(), static_cast<DWORD>(text.size()), &done, nullptr) != FALSE &&
            done == text.size();
        ::CloseHandle(file);
        return ok;
    }

    bool DumpResource(unsigned int id, const std::wstring& path, HMODULE module)
    {
        std::vector<unsigned char> bytes;
        if (!DefaultConfigSeeder::ReadResourceBytes(id, bytes, module) || bytes.empty())
            return false;
        return WriteBytes(path, std::string(bytes.begin(), bytes.end()));
    }

    bool HasTemporaryFile(const std::wstring& directory)
    {
        WIN32_FIND_DATAW found{};
        const HANDLE search = ::FindFirstFileW((directory + L"\\*.tmp").c_str(), &found);
        if (search == INVALID_HANDLE_VALUE)
            return false;
        ::FindClose(search);
        return true;
    }
}

int main(int argc, char** argv)
{
    if (argc < 3)
    {
        std::printf("usage: DefaultSeedingTests <packaged-executable> <work-directory>\n");
        return 2;
    }

    const std::wstring executable = Widen(argv[1]);
    std::wstring work = Widen(argv[2]);
    while (!work.empty() && (work.back() == L'\\' || work.back() == L'/'))
        work.pop_back();
    ::CreateDirectoryW(work.c_str(), nullptr);

    const HMODULE module = ::LoadLibraryExW(executable.c_str(), nullptr, LOAD_LIBRARY_AS_DATAFILE);
    Check(module != nullptr, "the packaged executable could not be opened as a data file");
    if (module == nullptr)
    {
        std::printf("Default config seeding tests failed.\n");
        return 1;
    }

    std::vector<unsigned char> config;
    Check(DefaultConfigSeeder::ReadResourceBytes(IDR_DEFAULT_CONFIG, config, module),
        "the factory config resource is missing");
    Check(config.size() > 3 && config[0] == 0xEF && config[1] == 0xBB && config[2] == 0xBF,
        "the factory config resource lost its UTF-8 BOM");

    const std::wstring extracted = work + L"\\extracted";
    ::CreateDirectoryW(extracted.c_str(), nullptr);
    Check(DumpResource(IDR_DEFAULT_CONFIG, extracted + L"\\config.default.ini", module),
        "the factory config resource could not be dumped for comparison");

    const std::wstring target = work + L"\\config.ini";
    ::DeleteFileW(target.c_str());
    Check(DefaultConfigSeeder::SeedOne(IDR_DEFAULT_CONFIG, target, module) == DefaultConfigSeeder::Result::Created,
        "a missing config was not created");
    Check(ReadBytes(target) == config, "the seeded config differs from the packaged resource");

    Check(DefaultConfigSeeder::SeedOne(IDR_DEFAULT_CONFIG, target, module) == DefaultConfigSeeder::Result::AlreadyPresent,
        "an existing config was treated as missing");
    Check(ReadBytes(target) == config, "the seeded config changed on the second pass");

    const std::string sentinel = "; user edited\n[general]\nhardware_monitor_item = 0\n";
    Check(WriteBytes(target, sentinel), "the sentinel config could not be written");
    Check(DefaultConfigSeeder::SeedOne(IDR_DEFAULT_CONFIG, target, module) == DefaultConfigSeeder::Result::AlreadyPresent,
        "an edited user config was reseeded");
    Check(ReadBytes(target) == std::vector<unsigned char>(sentinel.begin(), sentinel.end()),
        "an edited user config was overwritten");

    for (const auto id : {IDR_DEFAULT_BATTERY_INI, IDR_DEFAULT_POWERMON_INI, IDR_DEFAULT_WEATHER_INI})
    {
        std::vector<unsigned char> bytes;
        Check(DefaultConfigSeeder::ReadResourceBytes(id, bytes, module) && !bytes.empty(),
            "a built-in module default resource is missing");
    }
    Check(DumpResource(IDR_DEFAULT_BATTERY_INI, extracted + L"\\Battery.dll.default.ini", module) &&
        DumpResource(IDR_DEFAULT_POWERMON_INI, extracted + L"\\PowerMonPlugin.dll.default.ini", module) &&
        DumpResource(IDR_DEFAULT_WEATHER_INI, extracted + L"\\WeatherPro.default.ini", module),
        "the built-in module defaults could not be dumped for comparison");

    const std::wstring plugin = work + L"\\nested-plugins\\Battery.dll.ini";
    ::DeleteFileW(plugin.c_str());
    Check(DefaultConfigSeeder::SeedOne(IDR_DEFAULT_BATTERY_INI, plugin, module) == DefaultConfigSeeder::Result::Created,
        "a built-in module default was not created in a missing directory");
    Check(::GetFileAttributesW(plugin.c_str()) != INVALID_FILE_ATTRIBUTES,
        "the seeded built-in module default is absent");

    Check(!HasTemporaryFile(work) && !HasTemporaryFile(work + L"\\nested-plugins"),
        "a seeding temporary file was left behind");

    ::FreeLibrary(module);
    if (failures != 0)
    {
        std::printf("Default config seeding tests failed (%d).\n", failures);
        return 1;
    }
    std::printf("Factory defaults are embedded and never overwrite an existing configuration.\n");
    return 0;
}
