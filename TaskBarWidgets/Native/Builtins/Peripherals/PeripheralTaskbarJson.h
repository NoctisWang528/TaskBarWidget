#pragma once
#include "PeripheralTaskbar.h"
#include <cmath>
#include <stdexcept>
#include <winrt/Windows.Data.Json.h>
#include <winrt/Windows.Foundation.Collections.h>

namespace PeripheralTaskbar
{
    inline Snapshot ReadSnapshot(const winrt::Windows::Data::Json::JsonObject& object)
    {
        using namespace winrt::Windows::Data::Json;
        if (object.GetNamedNumber(L"version") != 1) throw std::invalid_argument("Peripheral snapshot version.");
        const auto revision = object.GetNamedNumber(L"revision");
        if (!std::isfinite(revision) || revision < 0 || revision > 9007199254740991.0 || std::floor(revision) != revision)
            throw std::invalid_argument("Peripheral snapshot revision.");
        Snapshot result;
        result.revision = static_cast<std::uint64_t>(revision);
        result.hideDisconnected = object.GetNamedBoolean(L"hideDisconnected");
        const auto devices = object.GetNamedArray(L"devices");
        if (devices.Size() > 1024) throw std::invalid_argument("Too many peripheral devices.");
        std::set<std::wstring> ids;
        for (const auto& value : devices)
        {
            const auto input = value.GetObject();
            Device device;
            device.id = input.GetNamedString(L"id");
            if (!ValidId(device.id) || !ids.insert(device.id).second)
                throw std::invalid_argument("Invalid or duplicate peripheral ID.");
            device.name = input.GetNamedString(L"name");
            device.state = input.GetNamedString(L"state");
            if (device.name.size() > 512 || (device.state != L"online" && device.state != L"disconnected" &&
                device.state != L"sleeping" && device.state != L"unavailable" && device.state != L"unsupported"))
                throw std::invalid_argument("Invalid peripheral device.");
            device.stale = input.GetNamedBoolean(L"stale");
            const auto batteries = input.GetNamedArray(L"batteries");
            if (batteries.Size() > 16) throw std::invalid_argument("Too many peripheral components.");
            for (const auto& component : batteries)
            {
                const auto battery = component.GetObject();
                Battery b;
                b.role = battery.GetNamedString(L"role");
                const auto percentage = battery.GetNamedValue(L"percentage");
                if (percentage.ValueType() != JsonValueType::Null)
                {
                    const double number = percentage.GetNumber();
                    if (!std::isfinite(number) || number < 0 || number > 100 || std::floor(number) != number)
                        throw std::invalid_argument("Invalid peripheral percentage.");
                    b.percentage = static_cast<int>(number);
                }
                const auto level = battery.GetNamedValue(L"levelText");
                if (level.ValueType() != JsonValueType::Null) b.levelText = level.GetString();
                b.estimated = battery.GetNamedBoolean(L"estimated");
                const auto charging = battery.GetNamedValue(L"charging");
                if (charging.ValueType() != JsonValueType::Null) b.charging = charging.GetBoolean();
                if (b.role.size() > 128 || b.levelText.size() > 128)
                    throw std::invalid_argument("Invalid peripheral component text.");
                device.batteries.push_back(std::move(b));
            }
            result.devices.push_back(std::move(device));
        }
        return result;
    }
}
