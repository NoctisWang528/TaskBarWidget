#pragma once
#include <algorithm>
#include <string>
#include <vector>

// Keep unregistered IDs in their slots while applying the user's current order
// to registered items. A late registration can then recover its original rank.
inline std::vector<std::wstring> MergeStableItemOrder(
    const std::vector<std::wstring>& saved, const std::vector<std::wstring>& current)
{
    std::vector<std::wstring> result;
    size_t next = 0;
    for (const auto& id : saved)
    {
        if (std::find(current.begin(), current.end(), id) == current.end())
        {
            if (std::find(result.begin(), result.end(), id) == result.end()) result.push_back(id);
        }
        else if (next < current.size()) result.push_back(current[next++]);
    }
    while (next < current.size()) result.push_back(current[next++]);
    return result;
}
