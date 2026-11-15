#pragma once
#include <algorithm>
#include <map>
#include <set>
#include <stdexcept>
#include <string>
#include <vector>

namespace PeripheralTaskbar
{
    using AliasMap = std::map<std::wstring, std::wstring>;
    constexpr size_t MaxAliases = 4096;

    inline bool ValidId(const std::wstring& id)
    {
        return id.size() == 32 && std::all_of(id.begin(), id.end(), [](wchar_t c)
        { return (c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f'); });
    }

    inline std::wstring ResolveAlias(const AliasMap& aliases, const std::wstring& id)
    {
        const auto found = aliases.find(id);
        return found == aliases.end() ? id : found->second; // Validated maps are flat.
    }

    // Validate the whole graph before changing any live state. A later snapshot
    // may extend a retained identity, but cannot redirect an already proven edge.
    inline AliasMap MergeAliases(const AliasMap& existing, const AliasMap& incoming)
    {
        if (incoming.size() > MaxAliases) throw std::invalid_argument("Too many peripheral aliases.");
        auto graph = existing;
        for (const auto& [oldId, retained] : incoming)
        {
            if (!ValidId(oldId) || !ValidId(retained) || oldId == retained)
                throw std::invalid_argument("Invalid peripheral alias.");
            graph.try_emplace(oldId, retained);
        }
        if (graph.size() > MaxAliases) throw std::invalid_argument("Too many peripheral aliases.");
        AliasMap flat;
        for (const auto& edge : graph)
        {
            std::set<std::wstring> visited;
            std::vector<std::wstring> path;
            auto id = edge.first;
            while (true)
            {
                if (flat.contains(id)) { id = flat.at(id); break; }
                const auto next = graph.find(id);
                if (next == graph.end()) break;
                if (!visited.insert(id).second) throw std::invalid_argument("Peripheral alias cycle.");
                path.push_back(id);
                id = next->second;
            }
            for (const auto& member : path) flat.emplace(member, id);
        }
        for (const auto& [oldId, retained] : incoming)
            if (flat.at(oldId) != ResolveAlias(flat, retained))
                throw std::invalid_argument("Conflicting peripheral alias.");
        return flat;
    }

    inline std::wstring CanonicalItemId(const AliasMap& aliases, const std::wstring& value)
    {
        for (const std::wstring prefix : {L"peripheral_", L"builtin_peripheral_"})
        {
            if (!value.starts_with(prefix)) continue;
            const auto id = value.substr(prefix.size(), 32);
            const auto suffix = value.substr((std::min)(value.size(), prefix.size() + 32));
            if (ValidId(id) && (suffix.empty() || suffix == L".label" || suffix == L".value"))
                return prefix + ResolveAlias(aliases, id) + suffix;
        }
        return value;
    }

    inline std::wstring CanonicalSettingKey(const AliasMap& aliases, const std::wstring& key)
    {
        if (!key.starts_with(L"items.")) return key;
        const auto split = key.find(L'.', 6);
        return split == std::wstring::npos ? key :
            L"items." + CanonicalItemId(aliases, key.substr(6, split - 6)) + key.substr(split);
    }

    template<class T> inline std::map<std::wstring, T> CanonicalMap(
        const AliasMap& aliases, const std::map<std::wstring, T>& values)
    {
        std::map<std::wstring, T> result;
        // Explicit canonical entries always win, including empty labels/zero colors.
        for (const auto& [key, value] : values)
            if (CanonicalItemId(aliases, key) == key) result.emplace(key, value);
        for (const auto& [key, value] : values)
            result.try_emplace(CanonicalItemId(aliases, key), value);
        return result;
    }

    inline std::wstring CanonicalList(const AliasMap& aliases, const std::wstring& value)
    {
        std::wstring result;
        std::set<std::wstring> seen;
        size_t start = 0;
        while (start < value.size())
        {
            const auto end = value.find(L',', start);
            auto id = CanonicalItemId(aliases, value.substr(start, end - start));
            if (!id.empty() && seen.insert(id).second)
            {
                if (!result.empty()) result += L",";
                result += id; // Selection union; ordering keeps the earliest slot.
            }
            if (end == std::wstring::npos) break;
            start = end + 1;
        }
        return result;
    }
}
