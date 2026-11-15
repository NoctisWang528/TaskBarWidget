#include "../Builtins/Peripherals/PeripheralTaskbarJson.h"
#include "../TrafficMonitor/StableItemOrder.h"
#include "../Cards/CardLayout.h"
#include <cassert>
#include <iostream>

using namespace PeripheralTaskbar;
using namespace winrt::Windows::Data::Json;

namespace
{
    const std::wstring MouseId(32, L'a');
    const std::wstring BudsId(32, L'b');
    const std::wstring RetainedId(32, L'c');

    template<class Action> void Reject(Action action)
    {
        bool rejected = false;
        try { action(); } catch (...) { rejected = true; }
        assert(rejected);
    }

    void TestAliasValidationAndMigration()
    {
        const auto aliases = MergeAliases({}, {{MouseId, BudsId}, {BudsId, RetainedId}});
        assert(aliases.at(MouseId) == RetainedId && aliases.at(BudsId) == RetainedId);
        assert(MergeAliases(aliases, {{MouseId, BudsId}}) == aliases);
        Reject([&] { (void)MergeAliases({}, {{MouseId, MouseId}}); });
        Reject([&] { (void)MergeAliases({}, {{MouseId, BudsId}, {BudsId, MouseId}}); });
        Reject([&] { (void)MergeAliases(aliases, {{RetainedId, MouseId}}); });
        Reject([&] { (void)MergeAliases(aliases, {{MouseId, std::wstring(32, L'd')}}); });
        Reject([&] { (void)MergeAliases({}, {{L"bad", RetainedId}}); });
        Reject([&] { (void)MergeAliases({}, {{MouseId, std::wstring(32, L'A')}}); });
        AliasMap excessive;
        for (size_t i = 0; i <= MaxAliases; ++i)
        {
            wchar_t id[33]{};
            swprintf_s(id, L"%032x", static_cast<unsigned>(i));
            excessive.emplace(id, RetainedId);
        }
        Reject([&] { (void)MergeAliases({}, excessive); });

        const auto oldItem = L"peripheral_" + MouseId, retainedItem = L"peripheral_" + RetainedId;
        const auto oldStable = L"builtin_" + oldItem, retainedStable = L"builtin_" + retainedItem;
        assert(CanonicalList(aliases, oldItem + L"," + retainedItem) == retainedItem); // Selection union.
        assert(CanonicalList(aliases, L"native_0," + oldStable + L",native_1," + retainedStable) ==
            L"native_0," + retainedStable + L",native_1"); // Earliest position.
        assert(CanonicalList(aliases, retainedStable + L",native_0," + oldStable) == retainedStable + L",native_0");
        const std::map<std::wstring, int> rows{{oldStable, 1}, {retainedStable, 0}, {L"native_0", 1}};
        const auto migratedRows = CanonicalMap(aliases, rows);
        assert(migratedRows.size() == 2 && migratedRows.at(retainedStable) == 0 && !migratedRows.contains(oldStable));
        const std::map<std::wstring, std::wstring> labels{{oldItem, L"custom"}, {retainedItem, L""}};
        assert(CanonicalMap(aliases, labels).at(retainedItem).empty()); // Explicit empty remains explicit.
        const std::map<std::wstring, int> colors{{oldStable + L".label", 42}, {oldStable + L".value", 88},
            {retainedStable + L".label", 0}};
        const auto migratedColors = CanonicalMap(aliases, colors);
        assert(migratedColors.size() == 2 && migratedColors.at(retainedStable + L".label") == 0 &&
            migratedColors.at(retainedStable + L".value") == 88);
        assert(CanonicalMap(aliases, migratedColors) == migratedColors); // Restart/save is idempotent.
        for (const auto* suffix : {L".show", L".order", L".row", L".label", L".labelColor", L".valueColor"})
            assert(CanonicalSettingKey(aliases, L"items." + oldStable + suffix) == L"items." + retainedStable + suffix);
        assert(CanonicalItemId(aliases, L"native_0") == L"native_0");
    }

    void TestAliasApplyAndLifetime()
    {
        Provider provider;
        auto* old = provider.Ensure(MouseId);
        auto* retained = provider.Ensure(RetainedId);
        Device device{RetainedId, L"Retained mouse", L"online", false, {{L"main", 73, L"", false, false}}};
        assert(provider.Apply({1, false, {device}, {{MouseId, RetainedId}}}));
        assert(provider.Ensure(MouseId) == retained && provider.Ensure(RetainedId) == retained);
        assert(provider.GetItem(0) == retained && provider.GetItem(1) == nullptr);
        assert(provider.Retired(old) && !provider.Retired(retained));
        assert(std::wstring(old->GetItemId()) == L"peripheral_" + MouseId); // Never mutate map keys/objects in place.
        assert(std::wstring(old->GetItemValueText()) == L"73%"); // Outstanding draft pointer remains usable.
        Reject([&] { provider.Apply({2, false, {device}, {{MouseId, BudsId}}}); });
        assert(provider.Ensure(MouseId) == retained && std::wstring(retained->GetItemValueText()) == L"73%");
        device.batteries[0].percentage = 72;
        assert(provider.Apply({2, false, {device}, {}})); // Rejected payload did not consume revision.
        assert(std::wstring(old->GetItemValueText()) == L"72%");
        assert(!provider.Apply({1, false, {}, {{RetainedId, BudsId}}})); // Stale aliases cannot mutate identity.
        Reject([&] { auto duplicate = device; duplicate.id = MouseId;
            provider.Apply({3, false, {device, duplicate}, {}}); });
        provider.Disconnected();
        device.id = MouseId; // A v1 peer after reconnect cannot resurrect the retired item.
        assert(provider.Apply({0, false, {device}}));
        assert(provider.Ensure(MouseId) == retained && provider.GetItem(1) == nullptr);

        Provider restarted;
        restarted.RestoreAliases(provider.Aliases());
        assert(restarted.Ensure(MouseId) == restarted.Ensure(RetainedId));
        assert(restarted.GetItem(1) == nullptr);
        Provider onlyOld;
        auto* pointer = onlyOld.Ensure(MouseId);
        device.id = MouseId;
        assert(onlyOld.Apply({1, false, {device}}));
        assert(onlyOld.Apply({2, false, {}, {{MouseId, RetainedId}}}, {MouseId}));
        assert(onlyOld.GetItem(0) == onlyOld.Ensure(RetainedId) && onlyOld.GetItem(0) != pointer);
        assert(std::wstring(pointer->GetItemValueText()) == L"last: 72%");
    }

    void TestAliasWireVersions()
    {
        auto payload = JsonObject::Parse(LR"({"version":2,"revision":1,"hideDisconnected":false,
            "aliases":{"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                       "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb":"cccccccccccccccccccccccccccccccc"},
            "devices":[{"id":"cccccccccccccccccccccccccccccccc","name":"Mouse","state":"online","stale":false,"batteries":[]}]})");
        const auto snapshot = ReadSnapshot(payload);
        assert(snapshot.aliases.at(MouseId) == RetainedId && snapshot.devices.size() == 1);
        Provider provider;
        auto* old = provider.Ensure(MouseId);
        assert(provider.Apply(snapshot) && provider.Retired(old));
        auto aliases = payload.GetNamedObject(L"aliases");
        aliases.SetNamedValue(RetainedId, JsonValue::CreateStringValue(MouseId));
        Reject([&] { (void)ReadSnapshot(payload); });
        assert(provider.Ensure(MouseId)->Current().name == L"Mouse");
        aliases.Remove(RetainedId);
        payload.SetNamedValue(L"aliases", JsonValue::CreateStringValue(L"bad"));
        Reject([&] { (void)ReadSnapshot(payload); });
        payload.Remove(L"aliases");
        Reject([&] { (void)ReadSnapshot(payload); }); // v2 requires its alias object, even empty.
        payload.SetNamedValue(L"version", JsonValue::CreateNumberValue(1));
        assert(ReadSnapshot(payload).aliases.empty());
        payload.SetNamedValue(L"version", JsonValue::CreateNumberValue(3));
        Reject([&] { (void)ReadSnapshot(payload); });
        payload.SetNamedValue(L"version", JsonValue::CreateNumberValue(2));
        payload.SetNamedValue(L"aliases", aliases);
        auto duplicate = JsonObject::Parse(LR"({"id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","name":"Old","state":"online","stale":false,"batteries":[]})");
        payload.GetNamedArray(L"devices").Append(duplicate);
        Reject([&] { (void)ReadSnapshot(payload); }); // Canonical duplicate must reject atomically.
    }

    void TestSettingsSaveDuringAliasMigration()
    {
        const AliasMap aliases{{MouseId, RetainedId}};
        const auto old = L"items.builtin_peripheral_" + MouseId;
        const auto current = L"items.builtin_peripheral_" + RetainedId;
        JsonObject values;
        values.SetNamedValue(old + L".show", JsonValue::CreateBooleanValue(true));
        values.SetNamedValue(current + L".show", JsonValue::CreateBooleanValue(false));
        values.SetNamedValue(old + L".order", JsonValue::CreateNumberValue(2));
        values.SetNamedValue(current + L".order", JsonValue::CreateNumberValue(5));
        values.SetNamedValue(old + L".row", JsonValue::CreateNumberValue(1));
        values.SetNamedValue(current + L".row", JsonValue::CreateNumberValue(0));
        values.SetNamedValue(old + L".label", JsonValue::CreateStringValue(L"old label"));
        values.SetNamedValue(current + L".label", JsonValue::CreateStringValue(L""));
        values.SetNamedValue(old + L".valueColor", JsonValue::CreateStringValue(L"#123456"));
        values.SetNamedValue(L"taskbar.backgroundOpacity", JsonValue::CreateNumberValue(50));
        const auto migrated = CanonicalSettingsValues(aliases, values, 10);
        assert(migrated.GetNamedBoolean(current + L".show"));
        assert(migrated.GetNamedNumber(current + L".order") == 2);
        assert(migrated.GetNamedNumber(current + L".row") == 0);
        assert(migrated.GetNamedString(current + L".label").empty());
        assert(migrated.GetNamedString(current + L".valueColor") == L"#123456");
        assert(migrated.GetNamedNumber(L"taskbar.backgroundOpacity") == 50);
        for (const auto& pair : migrated) assert(!std::wstring(pair.Key()).starts_with(old));
        const auto savedAgain = CanonicalSettingsValues(aliases, migrated, 10);
        assert(savedAgain.Size() == migrated.Size());
        for (const auto& pair : migrated)
            assert(savedAgain.GetNamedValue(pair.Key()).Stringify() == pair.Value().Stringify());
        values.SetNamedValue(old + L".order", JsonValue::CreateNumberValue(2.5));
        Reject([&] { (void)CanonicalSettingsValues(aliases, values, 10); });
        values.SetNamedValue(old + L".order", JsonValue::CreateNumberValue(2));
        values.SetNamedValue(old + L".show", JsonValue::CreateNumberValue(1));
        Reject([&] { (void)CanonicalSettingsValues(aliases, values, 10); });
        JsonObject deselect;
        deselect.SetNamedValue(old + L".show", JsonValue::CreateBooleanValue(false));
        assert(!CanonicalSettingsValues(aliases, deselect, 10).GetNamedBoolean(current + L".show"));
    }

    void TestPeripheralFooterGeometry()
    {
        using namespace Cards::Layout;
        assert(HeightPeripheralCompact == 184 && HeightPeripheralDetailed == 288);
        assert(PeripheralCompactStatusTop + PeripheralCompactStatusHeight < PeripheralPagingTop(HeightPeripheralCompact));
        for (int rows = 1; rows <= 4; ++rows)
        {
            const float height = HeightPeripheralDetailed - (4 - rows) * 50.f;
            assert(48 + (rows - 1) * 50 + 48 <= PeripheralPagingTop(height));
            assert(PeripheralPagingTop(height) + 24 == height - PeripheralFooterBottomGap);
        }
    }

    void TestAuthoritativeActiveMembership()
    {
        Provider provider;
        Device selected{MouseId, L"Same marketing name", L"online", false, {{L"main", 50, L"", false, false}}};
        Device unselected{BudsId, selected.name, L"online", false, {{L"main", 80, L"", false, false}}};
        assert(provider.Apply({1, false, {selected, unselected}}, {MouseId})); // v1, no alias provenance.
        auto* retained = provider.Ensure(MouseId);
        auto* dormant = provider.Ensure(BudsId);
        assert(retained != dormant && provider.GetItem(1) == dormant); // Names never infer identity.
        assert(provider.Apply({2, false, {}}, {MouseId}));
        assert(provider.GetItem(0) == retained && provider.GetItem(1) == nullptr);
        assert(provider.Active(retained) && !provider.Active(dormant));
        assert(std::wstring(retained->GetItemValueText()) == L"last: 50%");
        assert(std::wstring(dormant->GetItemValueText()) == L"last: 80%"); // Lifetime/readings retained outside presentation.
        assert(provider.Ensure(BudsId) == dormant && provider.GetItem(1) == nullptr); // Config access cannot resurrect it.
        assert(!provider.Apply({3, false, {}}, {MouseId}));
        assert(provider.Apply({4, false, {unselected}}, {MouseId})); // New evidence reactivates the same object.
        assert(provider.GetItem(1) == dormant && provider.Active(dormant));
        assert(provider.Apply({5, false, {}})); // Clearing taskbar selection removes both omitted rows.
        assert(provider.GetItem(0) == nullptr && !provider.Active(retained) && !provider.Active(dormant));
        assert(provider.Apply({6, false, {}}, {MouseId}));
        assert(provider.GetItem(0) == retained && provider.GetItem(1) == nullptr);
        assert(provider.Aliases().empty());
    }

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
        assert(provider.Apply({6, true, {}}, {MouseId})); // Only a taskbar-selected omitted device remains active.
        assert(provider.Ensure(MouseId) == mouse && provider.GetItem(0) == mouse);
        assert(provider.Visible(MouseId)); // Card hiding never removes selected taskbar slots.
        assert(std::wstring(mouse->GetItemValueText()) == L"last: 82%");
        assert(!provider.Apply({7, false, {}}, {MouseId})); // Only a card preference changed.
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
        assert(provider.Apply({2, true, {}}, {MouseId}));
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
    TestAliasValidationAndMigration();
    TestAliasApplyAndLifetime();
    TestAliasWireVersions();
    TestSettingsSaveDuringAliasMigration();
    TestPeripheralFooterGeometry();
    TestAuthoritativeActiveMembership();
    winrt::uninit_apartment();
    std::cout << "Peripheral snapshots, aliases, settings migration, pointer lifetime and footer geometry checks passed.\n";
}
