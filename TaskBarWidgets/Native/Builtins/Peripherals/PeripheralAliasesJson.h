#pragma once
#include "PeripheralAliases.h"
#include <cmath>
#include <winrt/Windows.Data.Json.h>
#include <winrt/Windows.Foundation.Collections.h>

namespace PeripheralTaskbar
{
    inline AliasMap ReadAliases(const winrt::Windows::Data::Json::JsonObject& object)
    {
        const auto version = object.GetNamedNumber(L"version");
        if (version != 1 && version != 2) throw std::invalid_argument("Peripheral snapshot version.");
        if (version == 1)
        {
            if (object.HasKey(L"aliases")) throw std::invalid_argument("Unexpected v1 peripheral aliases.");
            return {};
        }
        const auto input = object.GetNamedObject(L"aliases");
        if (input.Size() > MaxAliases) throw std::invalid_argument("Too many peripheral aliases.");
        AliasMap aliases;
        for (const auto& pair : input) aliases.emplace(pair.Key().c_str(), pair.Value().GetString().c_str());
        return MergeAliases({}, aliases);
    }

    inline winrt::Windows::Data::Json::JsonObject CanonicalSettingsValues(const AliasMap& aliases,
        const winrt::Windows::Data::Json::JsonObject& values, size_t maximumRank)
    {
        using namespace winrt::Windows::Data::Json;
        JsonObject result;
        for (const auto& pair : values)
        {
            const std::wstring key = pair.Key().c_str();
            if (CanonicalSettingKey(aliases, key) == key) result.SetNamedValue(key, pair.Value());
        }
        for (const auto& pair : values)
        {
            const std::wstring original = pair.Key().c_str();
            const auto key = CanonicalSettingKey(aliases, original);
            if (key == original) continue;
            if (!result.HasKey(key)) result.SetNamedValue(key, pair.Value());
            else if (key.ends_with(L".show"))
            {
                if (pair.Value().ValueType() != JsonValueType::Boolean ||
                    result.GetNamedValue(key).ValueType() != JsonValueType::Boolean)
                    throw std::invalid_argument("Invalid peripheral selection.");
                result.SetNamedValue(key, JsonValue::CreateBooleanValue(result.GetNamedBoolean(key) || pair.Value().GetBoolean()));
            }
            else if (key.ends_with(L".order"))
            {
                const auto rank = pair.Value().GetNumber(), current = result.GetNamedNumber(key);
                for (const auto value : {rank, current})
                    if (!std::isfinite(value) || std::floor(value) != value || value < 1 || value > maximumRank)
                        throw std::invalid_argument("Invalid peripheral rank.");
                result.SetNamedValue(key, JsonValue::CreateNumberValue((std::min)(rank, current)));
            }
        }
        return result;
    }
}
