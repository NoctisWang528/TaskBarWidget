#include "../Builtins/Peripherals/PeripheralTaskbarJson.h"
#include "../TrafficMonitor/StableItemOrder.h"
#include <cassert>
#include <iostream>

using namespace PeripheralTaskbar;
using namespace winrt::Windows::Data::Json;

namespace
{
    const std::wstring MouseId(32, L'a');
    const std::wstring BudsId(32, L'b');

    void TestSnapshots()
    {
        Provider provider;
        auto* mouse = provider.Ensure(MouseId);
        assert(mouse && std::wstring(mouse->GetItemId()) == L"peripheral_" + MouseId);
        assert(std::wstring(mouse->GetItemValueText()) == L"\u2014");
        assert(provider.Ensure(L"Mouse") == nullptr);

        Device device{MouseId, L"Mouse", L"online", false, {{L"main", 0, L"", false, std::nullopt}}};
        assert(provider.Apply({4, false, {device}}));
        assert(provider.Ensure(MouseId) == mouse);
        assert(std::wstring(mouse->GetItemValueText()) == L"0%");
        device.batteries[0].percentage = 82;
        assert(!provider.Apply({3, true, {device}}));
        assert(std::wstring(mouse->GetItemValueText()) == L"0%");
        assert(provider.Apply({5, false, {device}}));
        assert(provider.Apply({6, true, {}})); // Omitted known device remains an item.
        assert(provider.Ensure(MouseId) == mouse && provider.GetItem(0) == mouse);
        assert(provider.Visible(MouseId)); // Card hiding never removes selected taskbar slots.
        assert(std::wstring(mouse->GetItemValueText()) == L"last: 82%");
        assert(!provider.Apply({7, false, {}})); // Only a card preference changed.
        assert(provider.Visible(MouseId));

        provider.Disconnected();
        // Media restart permits a lower sequence and preserves the object.
        assert(provider.Apply({1, true, {device}}));
        assert(provider.Visible(MouseId));
        assert(std::wstring(mouse->GetItemValueText()) == L"82%");
        device.state = L"sleeping";
        device.stale = true;
        device.batteries.clear();
        assert(provider.Apply({2, true, {device}}));
        assert(provider.Visible(MouseId));
        assert(std::wstring(mouse->GetItemValueText()) == L"last: 82%");

        Device buds{BudsId, L"Earbuds", L"online", false,
            {{L"left", 70, L"", false, true}, {L"right", std::nullopt, L"Low", true, std::nullopt},
             {L"case", std::nullopt, L"", false, std::nullopt}}};
        assert(provider.Apply({3, false, {device, buds}}));
        auto* components = provider.Ensure(BudsId);
        assert(std::wstring(components->GetItemValueText()) == L"left 70% (charging) / right \u2248Low / case \u2014");
        buds.batteries.push_back({L"main", 90, L"", false, false});
        assert(provider.Apply({4, false, {device, buds}}));
        assert(std::wstring(components->GetItemValueText()) == L"90%");
        assert(provider.GetItem(2) == nullptr && provider.GetItem(-1) == nullptr);
    }

    void TestWireSnapshot()
    {
        const auto payload = JsonObject::Parse(LR"({"version":1,"revision":1,"mode":"compact","hideDisconnected":false,
            "devices":[{"id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","name":"Mouse","state":"online","stale":false,
            "showInCard":false,"batteries":[{"role":"main","percentage":null,"levelText":null,"estimated":false,"charging":null}]}]})");
        auto snapshot = ReadSnapshot(payload);
        Provider provider;
        assert(provider.Apply(snapshot));
        assert(provider.Visible(MouseId)); // Card selection has no taskbar effect.
        assert(std::wstring(provider.GetItem(0)->GetItemValueText()) == L"\u2014");

        const auto rejected = [](const JsonObject& input)
        {
            bool failed = false;
            try { (void)ReadSnapshot(input); } catch (...) { failed = true; }
            assert(failed);
        };
        payload.SetNamedValue(L"revision", JsonValue::CreateNumberValue(1.5));
        rejected(payload);
        payload.SetNamedValue(L"revision", JsonValue::CreateNumberValue(2));
        auto device = payload.GetNamedArray(L"devices").GetObjectAt(0);
        auto battery = device.GetNamedArray(L"batteries").GetObjectAt(0);
        battery.SetNamedValue(L"percentage", JsonValue::CreateNumberValue(101));
        rejected(payload);
        battery.SetNamedValue(L"percentage", JsonValue::CreateNumberValue(0));
        assert(ReadSnapshot(payload).devices[0].batteries[0].percentage == 0);
        payload.GetNamedArray(L"devices").Append(device);
        rejected(payload); // Duplicate identities cannot partially mutate live items.
        assert(std::wstring(provider.GetItem(0)->GetItemValueText()) == L"\u2014");
    }

    void TestLateOrder()
    {
        const std::wstring id = L"builtin_peripheral_" + MouseId;
        const std::vector<std::wstring> saved{L"native_0", id, L"native_1", L"future_item"};
        assert(MergeStableItemOrder(saved, {L"native_0", L"native_1"}) == saved);
        const std::vector<std::wstring> reordered{L"native_1", id, L"native_0", L"future_item"};
        assert(MergeStableItemOrder(saved, {L"native_1", L"native_0"}) == reordered);
        assert(MergeStableItemOrder(reordered, {L"native_1", id, L"native_0"}) == reordered);
        assert(MergeStableItemOrder({}, {id, L"native_0"}) == std::vector<std::wstring>({id, L"native_0"}));
    }

    void TestPresentationChangesOnly()
    {
        Provider provider;
        auto payload = JsonObject::Parse(LR"({"version":1,"revision":1,"hideDisconnected":false,"refreshing":false,
            "devices":[{"id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","name":"Mouse","state":"online","stale":false,
            "updatedAt":"2026-11-10T10:00:00Z","showInCard":true,
            "batteries":[{"role":"main","percentage":75,"levelText":null,"estimated":false,"charging":null}]}]})");
        assert(provider.Apply(ReadSnapshot(payload)));
        auto* mouse = provider.Ensure(MouseId);
        auto device = payload.GetNamedArray(L"devices").GetObjectAt(0);
        device.SetNamedValue(L"updatedAt", JsonValue::CreateStringValue(L"2026-11-10T10:01:00Z"));
        device.SetNamedValue(L"lastSuccessfulAt", JsonValue::CreateStringValue(L"2026-11-10T10:01:00Z"));
        device.SetNamedValue(L"showInCard", JsonValue::CreateBooleanValue(false));
        payload.SetNamedValue(L"refreshing", JsonValue::CreateBooleanValue(true));
        payload.SetNamedValue(L"hideDisconnected", JsonValue::CreateBooleanValue(true));
        payload.SetNamedValue(L"revision", JsonValue::CreateNumberValue(2));
        assert(!provider.Apply(ReadSnapshot(payload)));
        payload.SetNamedValue(L"refreshing", JsonValue::CreateBooleanValue(false));
        payload.SetNamedValue(L"revision", JsonValue::CreateNumberValue(3));
        assert(!provider.Apply(ReadSnapshot(payload)));
        assert(provider.Ensure(MouseId) == mouse && provider.Visible(MouseId));

        auto battery = device.GetNamedArray(L"batteries").GetObjectAt(0);
        battery.SetNamedValue(L"percentage", JsonValue::CreateNumberValue(74));
        payload.SetNamedValue(L"revision", JsonValue::CreateNumberValue(2));
        assert(!provider.Apply(ReadSnapshot(payload))); // Metadata-only updates still advanced the revision.
        assert(std::wstring(mouse->GetItemValueText()) == L"75%");
        payload.SetNamedValue(L"revision", JsonValue::CreateNumberValue(4));
        assert(provider.Apply(ReadSnapshot(payload)));
        assert(std::wstring(mouse->GetItemValueText()) == L"74%");

        device.SetNamedValue(L"state", JsonValue::CreateStringValue(L"disconnected"));
        payload.SetNamedValue(L"revision", JsonValue::CreateNumberValue(5));
        assert(provider.Apply(ReadSnapshot(payload)));
        assert(provider.Visible(MouseId) && provider.Ensure(MouseId) == mouse);
        assert(std::wstring(mouse->GetItemValueText()) == L"last: 74%");
        payload.SetNamedValue(L"revision", JsonValue::CreateNumberValue(6));
        assert(!provider.Apply(ReadSnapshot(payload)));
        device.SetNamedValue(L"state", JsonValue::CreateStringValue(L"sleeping"));
        payload.SetNamedValue(L"revision", JsonValue::CreateNumberValue(7));
        assert(!provider.Apply(ReadSnapshot(payload))); // State accepted; displayed last value is unchanged.
        assert(mouse->Current().state == L"sleeping");
    }

    void TestLocalizedLastReading()
    {
        Provider provider;
        provider.SetChinese(true);
        Device device{MouseId, L"Mouse", L"online", false, {{L"main", 75, L"", true, true}}};
        assert(provider.Apply({1, false, {device}}));
        auto* item = provider.Ensure(MouseId);
        assert(std::wstring(item->GetItemValueText()) == L"\u224875% (\u5145\u7535\u4e2d)");
        assert(provider.Apply({2, true, {}}));
        assert(provider.Visible(MouseId));
        assert(std::wstring(item->GetItemValueText()) == L"\u4e0a\u6b21\u8bfb\u6570: \u224875% (\u5145\u7535\u4e2d)");
        provider.SetChinese(false);
        assert(std::wstring(item->GetItemValueText()) == L"last: \u224875% (charging)");
        assert(provider.Ensure(MouseId) == item);
    }
}

int main()
{
    winrt::init_apartment(winrt::apartment_type::single_threaded);
    TestSnapshots();
    TestWireSnapshot();
    TestLateOrder();
    TestPresentationChangesOnly();
    TestLocalizedLastReading();
    winrt::uninit_apartment();
    std::cout << "Peripheral taskbar snapshot, lifetime, wire validation and late-order checks passed.\n";
}
