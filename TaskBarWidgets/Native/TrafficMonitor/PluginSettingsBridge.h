#pragma once
#include "PluginIniOptions.h"
#include "../Builtins/Weather/WeatherService.h"
#include "TrafficMonitor.h"
#include <winrt/Windows.Data.Json.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <filesystem>
#include <algorithm>
#include <cwctype>
#include <stdexcept>
#include <cmath>

// Source-integrated monitoring features retain their migrated INI files.
// Only allowlisted settings enter IPC; service credentials remain local.
class PluginSettingsBridge
{
    using JsonObject = winrt::Windows::Data::Json::JsonObject;
    using JsonArray = winrt::Windows::Data::Json::JsonArray;
    using JsonValue = winrt::Windows::Data::Json::JsonValue;
    struct File
    {
        std::wstring path;
        std::string original, pending;
        bool saved{}, dirty{};
    };
    struct Option { std::wstring field; size_t file; std::string section, key; int minimum{}, maximum{}; bool number{}, text{}; };
    std::vector<File> files;
    std::vector<Option> options;
    JsonArray plugins;
    JsonArray descriptors;
    static JsonValue Text(const std::wstring& value) { return JsonValue::CreateStringValue(value); }
    static std::wstring FinalPath(HANDLE handle)
    {
        wchar_t path[32768];
        DWORD count = GetFinalPathNameByHandleW(handle, path, _countof(path), FILE_NAME_NORMALIZED);
        if (!count || count >= _countof(path)) throw std::runtime_error("Could not verify the plugin configuration path.");
        return std::wstring(path, count);
    }
    static std::string Read(const std::wstring& path)
    {
        HANDLE input = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
        if (input == INVALID_HANDLE_VALUE) throw std::runtime_error("The plugin configuration cannot be read.");
        HANDLE directory = INVALID_HANDLE_VALUE;
        try
        {
            BY_HANDLE_FILE_INFORMATION information{};
            LARGE_INTEGER size{};
            if (!GetFileInformationByHandle(input, &information) || (information.dwFileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY))
                || !GetFileSizeEx(input, &size) || size.QuadPart < 0 || size.QuadPart > 256 * 1024)
                throw std::runtime_error("The plugin configuration format or size is unsupported.");
            const auto parent = std::filesystem::path(path).parent_path();
            directory = CreateFileW(parent.c_str(), 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr);
            if (directory == INVALID_HANDLE_VALUE || _wcsicmp(std::filesystem::path(FinalPath(input)).parent_path().c_str(), FinalPath(directory).c_str()) != 0)
                throw std::runtime_error("The plugin configuration is outside its allowed directory.");
            std::string bytes(static_cast<size_t>(size.QuadPart), '\0');
            DWORD read{};
            if (!ReadFile(input, bytes.data(), static_cast<DWORD>(bytes.size()), &read, nullptr) || read != bytes.size())
                throw std::runtime_error("The plugin configuration could not be read completely.");
            CloseHandle(directory); CloseHandle(input);
            return bytes;
        }
        catch (...)
        {
            if (directory != INVALID_HANDLE_VALUE) CloseHandle(directory);
            CloseHandle(input); throw;
        }
    }
    static void Replace(const std::wstring& path, const std::string& expected, const std::string& bytes)
    {
        std::unique_lock<std::mutex> weatherLock(WeatherService::ConfigurationMutex(), std::defer_lock);
        if (_wcsicmp(std::filesystem::path(path).filename().c_str(), L"WeatherPro.ini") == 0) weatherLock.lock();
        if (Read(path) != expected) throw std::runtime_error("Plugin options changed outside this page. Reload settings and try again.");
        static unsigned sequence{};
        const std::wstring temporary = path + L".TaskBarWidgets-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(++sequence) + L".tmp";
        HANDLE output = CreateFileW(temporary.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (output == INVALID_HANDLE_VALUE) throw std::runtime_error("Could not create the plugin settings temporary file.");
        DWORD written{};
        bool good = WriteFile(output, bytes.data(), static_cast<DWORD>(bytes.size()), &written, nullptr) && written == bytes.size() && FlushFileBuffers(output);
        CloseHandle(output);
        if (!good || !MoveFileExW(temporary.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
        {
            DeleteFileW(temporary.c_str());
            throw std::runtime_error("Could not save plugin options. Check the configuration file permissions.");
        }
    }
    void Boolean(JsonArray& list, size_t file, const std::wstring& id, const char* section, const char* key, const wchar_t* label)
    {
        PluginIniOptions::BooleanToken token;
        if (!PluginIniOptions::FindBoolean(files[file].pending, section, key, token)) return;
        const std::wstring field = L"builtins." + id + L"." + winrt::to_hstring(section).c_str() + L"." + winrt::to_hstring(key).c_str();
        JsonObject descriptor;
        descriptor.SetNamedValue(L"key", Text(field)); descriptor.SetNamedValue(L"label", Text(label));
        descriptor.SetNamedValue(L"group", Text(id == L"battery.dll" ? L"builtins.battery" : id == L"powermonplugin.dll" ? L"builtins.power" : L"builtins.weather")); descriptor.SetNamedValue(L"type", Text(L"boolean"));
        descriptor.SetNamedValue(L"value", JsonValue::CreateBooleanValue(token.value));
        descriptor.SetNamedValue(L"restartRequired", JsonValue::CreateBooleanValue(id != L"weatherpro.dll"));
        list.Append(descriptor); descriptors.Append(descriptor); options.push_back({field, file, section, key});
    }
    void Number(JsonArray& list, size_t file, const std::wstring& id, const char* section, const char* key, const wchar_t* label, int minimum, int maximum)
    {
        PluginIniOptions::ValueToken token;
        if (!PluginIniOptions::FindValue(files[file].pending, section, key, token)) return;
        size_t parsed{}; int number{};
        try { number = std::stoi(token.value, &parsed); } catch (...) { return; }
        if (parsed != token.value.size() || number < minimum || number > maximum) return;
        const std::wstring field = L"builtins." + id + L"." + winrt::to_hstring(section).c_str() + L"." + winrt::to_hstring(key).c_str();
        JsonObject descriptor;
        descriptor.SetNamedValue(L"key", Text(field)); descriptor.SetNamedValue(L"label", Text(label));
        descriptor.SetNamedValue(L"group", Text(id == L"battery.dll" ? L"builtins.battery" : id == L"powermonplugin.dll" ? L"builtins.power" : L"builtins.weather"));
        descriptor.SetNamedValue(L"type", Text(L"number")); descriptor.SetNamedValue(L"value", JsonValue::CreateNumberValue(number));
        descriptor.SetNamedValue(L"min", JsonValue::CreateNumberValue(minimum)); descriptor.SetNamedValue(L"max", JsonValue::CreateNumberValue(maximum));
        descriptor.SetNamedValue(L"restartRequired", JsonValue::CreateBooleanValue(id != L"weatherpro.dll"));
        list.Append(descriptor); descriptors.Append(descriptor); options.push_back({field, file, section, key, minimum, maximum, true});
    }
    void String(JsonArray& list, size_t file, const std::wstring& id, const char* section, const char* key, const wchar_t* label, int limit)
    {
        PluginIniOptions::ValueToken token;
        if (!PluginIniOptions::FindValue(files[file].pending, section, key, token)) return;
        const std::wstring field = L"builtins." + id + L"." + winrt::to_hstring(section).c_str() + L"." + winrt::to_hstring(key).c_str();
        JsonObject descriptor;
        descriptor.SetNamedValue(L"key", Text(field)); descriptor.SetNamedValue(L"label", Text(label));
        descriptor.SetNamedValue(L"group", Text(id == L"powermonplugin.dll" ? L"builtins.power" : L"builtins.weather"));
        descriptor.SetNamedValue(L"type", Text(L"text")); descriptor.SetNamedValue(L"value", Text(winrt::to_hstring(token.value).c_str()));
        descriptor.SetNamedValue(L"restartRequired", JsonValue::CreateBooleanValue(id != L"weatherpro.dll"));
        list.Append(descriptor); descriptors.Append(descriptor); options.push_back({field, file, section, key, 0, limit, false, true});
    }
public:
    static std::wstring Id(const std::wstring& path)
    {
        std::wstring name = std::filesystem::path(path).filename().wstring();
        std::transform(name.begin(), name.end(), name.begin(), [](wchar_t c) { return static_cast<wchar_t>(towlower(c)); });
        return name;
    }
    PluginSettingsBridge()
    {
        for (const auto& plugin : theApp.m_plugins.GetPlugins())
        {
            const auto id = Id(plugin.file_path);
            const auto filename = std::filesystem::path(plugin.file_path).filename().wstring();
            JsonObject entry;
            entry.SetNamedValue(L"id", Text(id)); entry.SetNamedValue(L"fileName", Text(filename)); entry.SetNamedValue(L"filePath", Text(plugin.file_path));
            auto name = plugin.Property(ITMPlugin::TMI_NAME); if (name.empty()) name = filename;
            entry.SetNamedValue(L"name", Text(name)); entry.SetNamedValue(L"description", Text(plugin.Property(ITMPlugin::TMI_DESCRIPTION)));
            entry.SetNamedValue(L"author", Text(plugin.Property(ITMPlugin::TMI_AUTHOR))); entry.SetNamedValue(L"copyright", Text(plugin.Property(ITMPlugin::TMI_COPYRIGHT)));
            entry.SetNamedValue(L"version", Text(plugin.Property(ITMPlugin::TMI_VERSION))); entry.SetNamedValue(L"url", Text(plugin.Property(ITMPlugin::TMI_URL)));
            entry.SetNamedValue(L"apiVersion", JsonValue::CreateNumberValue(plugin.plugin ? plugin.plugin->GetAPIVersion() : 0));
            const wchar_t* state = L"loaded";
            switch (plugin.state)
            {
            case CPluginManager::PluginState::PS_DISABLE: state = L"disabled"; break;
            case CPluginManager::PluginState::PS_MUDULE_LOAD_FAILED: state = L"loadFailed"; break;
            case CPluginManager::PluginState::PS_FUNCTION_GET_FAILED: state = L"entryPointMissing"; break;
            case CPluginManager::PluginState::PS_VERSION_NOT_SUPPORT: state = L"unsupportedApi"; break;
            default: if (!plugin.plugin) state = L"unavailable"; break;
            }
            entry.SetNamedValue(L"state", Text(state)); entry.SetNamedValue(L"errorCode", JsonValue::CreateNumberValue(plugin.error_code));
            JsonArray items;
            for (auto* item : plugin.plugin_items)
            {
                JsonObject detail; detail.SetNamedValue(L"id", Text(item->GetItemId())); detail.SetNamedValue(L"name", Text(item->GetItemName())); items.Append(detail);
            }
            entry.SetNamedValue(L"items", items);
            JsonArray optionFields;
            const wchar_t* iniName = id == L"battery.dll" ? L"Battery.dll.ini" : id == L"powermonplugin.dll" ? L"PowerMonPlugin.dll.ini" : id == L"weatherpro.dll" ? L"WeatherPro.ini" : nullptr;
            std::wstring note = L"内置监测功能，保存后重启应用生效。";
            if (iniName)
            {
                try
                {
                    auto path = std::filesystem::path(theApp.m_config_dir) / L"plugins" / iniName;
                    auto bytes = Read(path.wstring());
                    size_t file = files.size(); files.push_back({path.wstring(), bytes, bytes, false});
                    entry.SetNamedValue(L"configPath", Text(path.wstring()));
                    // Defaults are staged only; reading settings never changes a file.
                    auto ensure = [&](const char* section, const char* key, const char* value)
                    { PluginIniOptions::EnsureValue(files[file].pending, section, key, value); };
                    if (id == L"battery.dll")
                    {
                        ensure("config", "battery_type", "2");
                        ensure("config", "show_battery_in_tooltip", "1");
                        ensure("config", "show_percent", "1");
                        ensure("config", "show_charging_animation", "0");
                    }
                    else if (id == L"powermonplugin.dll")
                    {
                        PluginIniOptions::ValueToken first;
                        const bool initial = !PluginIniOptions::FindValue(bytes, "config", "settings_altered_counter", first) || first.value == "0";
                        ensure("config", "enable_cpu_monitor", initial ? "1" : "0");
                        ensure("config", "enable_gpu_monitor", initial ? "1" : "0");
                        ensure("config", "default_max_adaptive_decimal_places", "2");
                        ensure("config", "default_value_unit_space", "1");
                        ensure("config", "pwr_unit_str", "W");
                        ensure("config", "electric_capacity_unit_str", "Wh");
                        ensure("config", "electric_voltage_unit_str", "V");
                        ensure("config", "hour_unit_str", "h");
                        ensure("config", "minute_unit_str", "m");
                        ensure("config", "nan_str", "NaN");
                    }
                    else
                    {
                        ensure("config", "city_no", "101010100"); ensure("config", "city_name", "Beijing");
                        ensure("config", "api_type", "1"); ensure("config", "wit", "0");
                        ensure("config", "update_freq", "1"); ensure("config", "icon_type", "0");
                        for (const auto* key : {"show_weather_icon", "show_weather_alerts", "show_brief_weather_alert_info"}) ensure("config", key, "1");
                        for (const auto* key : {"show_brief_rt_weather_info", "show_error_info"}) ensure("config", key, "0");
                        PluginIniOptions::DeleteValue(files[file].pending, "config", "show_weather_in_tooltips");
                        for (const auto* key : {"show_rt_wind", "show_rt_wind_scale", "show_rt_humidity", "show_rt_air_quality", "show_rt_air_quality_aqi", "show_rt_air_quality_pm2p5", "show_weather_alert"}) ensure("hfw", key, "1");
                        for (const auto* key : {"show_rt_temp_feels_like", "show_fc_uv_index", "show_fc_humidity", "show_rt_air_quality_pm10"}) ensure("hfw", key, "0");
                    }

                    if (id == L"battery.dll")
                    {
                        Number(optionFields, file, id, "config", "battery_type", L"电池样式：0 数字 / 1 图标 / 2 图标与数字", 0, 2);
                        Boolean(optionFields, file, id, "config", "show_battery_in_tooltip", L"悬停提示显示电池信息");
                        Boolean(optionFields, file, id, "config", "show_percent", L"显示电量百分比");
                        Boolean(optionFields, file, id, "config", "show_charging_animation", L"显示充电动画");
                    }
                    else if (id == L"powermonplugin.dll")
                    {
                        String(optionFields, file, id, "config", "pwr_unit_str", L"功率单位", 11);
                        String(optionFields, file, id, "config", "electric_capacity_unit_str", L"电池容量单位", 11);
                        String(optionFields, file, id, "config", "electric_voltage_unit_str", L"电池电压单位", 11);
                        String(optionFields, file, id, "config", "hour_unit_str", L"小时单位", 11);
                        String(optionFields, file, id, "config", "minute_unit_str", L"分钟单位", 11);
                        String(optionFields, file, id, "config", "nan_str", L"无数据文字", 11);
                        Boolean(optionFields, file, id, "config", "enable_cpu_monitor", L"监测 CPU 功耗");
                        Boolean(optionFields, file, id, "config", "enable_gpu_monitor", L"监测 GPU 功耗");
                        Number(optionFields, file, id, "config", "default_max_adaptive_decimal_places", L"功耗小数位上限", 0, 6);
                        Number(optionFields, file, id, "config", "default_value_unit_space", L"数值与单位间空格", 0, 4);
                    }
                    else
                    {
                        Number(optionFields, file, id, "config", "update_freq", L"更新频率：0 每小时三次 / 1 两次 / 2 一次 / 3 每两小时 / 4 每三小时", 0, 4);
                    }
                    note = optionFields.Size() ? (id == L"weatherpro.dll" ? L"保存后自动刷新。城市与和风连接在天气专用设置中配置，已有密钥不会返回界面。" : L"保存后重启应用生效。") : L"当前配置没有已知选项。";
                }
                catch (...) { note = L"当前配置缺失或格式不支持编辑。"; }
            }
            entry.SetNamedValue(L"optionFields", optionFields); entry.SetNamedValue(L"optionsNote", Text(note)); plugins.Append(entry);
        }
    }
    JsonArray Describe() const { return plugins; }
    JsonArray Fields() const { return descriptors; }
    void Set(const std::wstring& field, const winrt::Windows::Data::Json::IJsonValue& value)
    {
        const auto option = std::find_if(options.begin(), options.end(), [&](const Option& item) { return item.field == field; });
        if (option == options.end()) throw std::invalid_argument("Unknown built-in option. Reload settings.");
        const auto before = files[option->file].pending;
        bool success{};
        if (option->text)
        {
            if (value.ValueType() != winrt::Windows::Data::Json::JsonValueType::String) throw std::invalid_argument("Invalid setting type.");
            std::wstring text = value.GetString().c_str();
            if (text.size() > static_cast<size_t>(option->maximum) || text.find_first_of(L"\r\n\0", 0, 3) != std::wstring::npos)
                throw std::invalid_argument("Invalid built-in setting text.");
            if (option->key == "city_no" && (text.empty() || text.find_first_not_of(L"0123456789.,-ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz") != std::wstring::npos))
                throw std::invalid_argument("Invalid weather location code.");
            success = PluginIniOptions::SetValue(files[option->file].pending, option->section, option->key, winrt::to_string(text));
        }
        else if (option->number)
        {
            if (value.ValueType() != winrt::Windows::Data::Json::JsonValueType::Number) throw std::invalid_argument("Invalid setting type.");
            const double number = value.GetNumber();
            if (!std::isfinite(number) || number != std::floor(number) || number < option->minimum || number > option->maximum)
                throw std::invalid_argument("Setting is outside its allowed range.");
            success = PluginIniOptions::SetValue(files[option->file].pending, option->section, option->key, std::to_string(static_cast<int>(number)));
        }
        else if (value.ValueType() == winrt::Windows::Data::Json::JsonValueType::Boolean)
            success = PluginIniOptions::SetBoolean(files[option->file].pending, option->section, option->key, value.GetBoolean());
        if (!success) throw std::invalid_argument("Invalid built-in setting value. Reload settings.");
        if (files[option->file].pending != before) files[option->file].dirty = true;
    }

    void Rollback()
    {
        bool failed = false;
        for (auto iterator = files.rbegin(); iterator != files.rend(); ++iterator)
        {
            if (!iterator->saved) continue;
            try { Replace(iterator->path, iterator->pending, iterator->original); iterator->saved = false; }
            catch (...) { failed = true; }
        }
        if (failed) throw std::runtime_error("Plugin settings rollback failed. Reload settings to inspect the saved values before retrying.");
    }
    void Save()
    {
        try
        {
            for (auto& file : files)
            {
                if (!file.dirty || file.pending == file.original) continue;
                Replace(file.path, file.original, file.pending); file.saved = true;
                if (Read(file.path) != file.pending) throw std::runtime_error("Plugin options readback failed.");
            }
        }
        catch (...) { Rollback(); throw; }
    }
    static ITMPlugin::OptionReturn OpenOptions(const std::wstring&, HWND)
    { return ITMPlugin::OR_OPTION_UNCHANGED; }
};
