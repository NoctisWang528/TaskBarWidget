#pragma once
#include "../../include/PluginInterface.h"
#include <algorithm>
#include <cstdint>
#include <map>
#include <memory>
#include <optional>
#include <set>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

// Memory-only built-in items. Creation, snapshot application and rendering belong
// to the native main thread; DataRequired never enumerates or queries hardware.
namespace PeripheralTaskbar
{
    inline bool ValidId(const std::wstring& id)
    {
        return id.size() == 32 && std::all_of(id.begin(), id.end(), [](wchar_t c)
        { return (c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f'); });
    }

    inline std::wstring DeviceId(const std::wstring& itemId)
    {
        constexpr std::wstring_view prefix = L"peripheral_";
        if (!itemId.starts_with(prefix)) return {};
        auto id = itemId.substr(prefix.size());
        return ValidId(id) ? id : std::wstring{};
    }

    struct Battery
    {
        std::wstring role = L"main";
        std::optional<int> percentage;
        std::wstring levelText;
        bool estimated{};
        std::optional<bool> charging;
    };

    struct Device
    {
        std::wstring id, name;
        std::wstring state = L"unavailable";
        bool stale = true;
        std::vector<Battery> batteries;
    };

    struct Snapshot
    {
        std::uint64_t revision{};
        bool hideDisconnected{}; // Card preference; taskbar selection stays authoritative.
        std::vector<Device> devices;
    };

    class Item final : public IPluginItem
    {
    public:
        explicit Item(const std::wstring& id, bool chinese = false) : m_id(L"peripheral_" + id), m_chinese(chinese)
        {
            m_device.id = id;
            m_device.name = L"Peripheral " + id.substr(0, 8);
            Update(m_device);
        }

        bool Update(Device device)
        {
            const auto previousName = m_device.name;
            const auto previousValue = m_value;
            if (device.name.empty()) device.name = m_device.name;
            if (device.batteries.empty() && (device.stale || device.state != L"online"))
                device.batteries = m_device.batteries;
            m_device = std::move(device);
            m_label = m_device.name + L":";
            m_value.clear();
            bool hasReading = false;
            const auto primary = std::find_if(m_device.batteries.begin(), m_device.batteries.end(),
                [](const auto& b) { return b.role == L"main" || b.role == L"primary"; });
            for (const auto& b : m_device.batteries)
            {
                if (primary != m_device.batteries.end() && &b != &*primary) continue;
                if (!m_value.empty()) m_value += L" / ";
                if (primary == m_device.batteries.end() && !b.role.empty()) m_value += b.role + L" ";
                const bool valid = b.percentage && *b.percentage >= 0 && *b.percentage <= 100;
                hasReading = hasReading || valid || !b.levelText.empty();
                if (b.estimated && (valid || !b.levelText.empty())) m_value += L"\u2248";
                m_value += valid ? std::to_wstring(*b.percentage) + L"%"
                    : b.levelText.empty() ? L"\u2014" : b.levelText;
                if (b.charging.value_or(false)) m_value += m_chinese ? L" (\u5145\u7535\u4e2d)" : L" (charging)";
            }
            if (m_value.empty()) m_value = L"\u2014";
            if (hasReading && (m_device.stale || m_device.state != L"online"))
                m_value = (m_chinese ? L"\u4e0a\u6b21\u8bfb\u6570: " : L"last: ") + m_value;
            // The actual component/level text can be wider than a percentage.
            m_sample = m_value.size() > 4 ? m_value : L"100%";
            return previousName != m_device.name || previousValue != m_value;
        }

        const wchar_t* GetItemName() const override { return m_device.name.c_str(); }
        const wchar_t* GetItemId() const override { return m_id.c_str(); }
        const wchar_t* GetItemLableText() const override { return m_label.c_str(); }
        const wchar_t* GetItemValueText() const override { return m_value.c_str(); }
        const wchar_t* GetItemValueSampleText() const override { return m_sample.c_str(); }
        const Device& Current() const { return m_device; }
        void SetChinese(bool chinese) { m_chinese = chinese; Update(m_device); }

    private:
        Device m_device;
        std::wstring m_id, m_label, m_value, m_sample;
        bool m_chinese{};
    };

    class Provider final : public ITMPlugin
    {
    public:
        static Provider& Instance()
        {
            // MFC saves theme presets during global teardown. Keep item pointers
            // valid through that phase, after function-local destructors run.
            static auto* provider = new Provider;
            return *provider;
        }
        Item* Ensure(const std::wstring& id)
        {
            if (!ValidId(id)) return nullptr;
            auto found = m_items.find(id);
            if (found != m_items.end()) return found->second.get();
            auto item = std::make_unique<Item>(id, m_chinese);
            auto* result = item.get();
            m_items.emplace(id, std::move(item));
            m_order.push_back(result);
            return result;
        }
        bool Apply(const Snapshot& snapshot)
        {
            if (m_hasRevision && snapshot.revision <= m_revision) return false;
            m_revision = snapshot.revision;
            m_hasRevision = true;
            // Accept newer state/revisions even when their rendered text is
            // identical. Timestamp, refreshing and card-preference churn must
            // not request native taskbar invalidation or relayout.
            bool changed = false;
            std::set<std::wstring> seen;
            for (const auto& device : snapshot.devices)
            {
                const bool known = m_items.contains(device.id);
                if (auto* item = Ensure(device.id))
                {
                    const bool presentationChanged = item->Update(device);
                    changed = changed || !known || presentationChanged;
                    seen.insert(device.id);
                }
            }
            // A complete snapshot may omit previously known devices. Keep their
            // object identity, last reading and all native composition settings.
            for (auto& [id, item] : m_items)
            {
                if (seen.contains(id)) continue;
                auto device = item->Current();
                device.state = L"disconnected";
                device.stale = true;
                const bool presentationChanged = item->Update(std::move(device));
                changed = changed || presentationChanged;
            }
            return changed;
        }
        void Disconnected()
        {
            m_hasRevision = false; // A restarted Media service starts a new sequence.
            for (auto& [id, item] : m_items)
            {
                auto device = item->Current();
                device.stale = true;
                item->Update(std::move(device));
            }
        }
        bool Visible(const std::wstring&) const
        {
            // Keep the existing native predicate entry point: hiding offline
            // devices is card-only. Selected taskbar slots retain their place.
            return true;
        }
        IPluginItem* GetItem(int index) override
        { return index >= 0 && static_cast<size_t>(index) < m_order.size() ? m_order[index] : nullptr; }
        void DataRequired() override {}
        void SetChinese(bool chinese)
        {
            m_chinese = chinese;
            for (auto& [id, item] : m_items) item->SetChinese(chinese);
        }
        const wchar_t* GetInfo(PluginInfoIndex index) override
        {
            return index == TMI_NAME ? L"Peripheral batteries"
                : index == TMI_DESCRIPTION ? L"Built-in peripheral battery snapshot items" : L"";
        }
    private:
        std::map<std::wstring, std::unique_ptr<Item>> m_items;
        std::vector<Item*> m_order;
        std::uint64_t m_revision{};
        bool m_hasRevision{}, m_chinese{};
    };
}
