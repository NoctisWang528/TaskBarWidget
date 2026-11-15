using System.Text.Json.Nodes;
using TaskBarWidgets.UI.Settings;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class PeripheralSettingsLayoutTests
{
    [Fact]
    public void SnapshotPollingReloadsSchemaOnlyForInventoryOrDescriptorChanges()
    {
        var snapshot = JsonNode.Parse("""{"revision":1,"devices":[{"id":"a","name":"耳机","kind":"earbuds","state":"online","batteries":[{"role":"left","percentage":80},{"role":"right","percentage":70}]}]}""")!.AsObject();
        var key = PeripheralSettingsDraft.InventoryKey(snapshot);
        snapshot["revision"] = 2;
        snapshot["devices"]![0]!["state"] = "sleeping";
        snapshot["devices"]![0]!["batteries"]![0]!["percentage"] = 79;
        Assert.Equal(key, PeripheralSettingsDraft.InventoryKey(snapshot));
        var batteries = snapshot["devices"]![0]!["batteries"]!.AsArray();
        var first = batteries[0]; batteries.RemoveAt(0); batteries.Add(first);
        Assert.Equal(key, PeripheralSettingsDraft.InventoryKey(snapshot));
        batteries.Add(new JsonObject { ["role"] = "case", ["percentage"] = 50 });
        Assert.NotEqual(key, PeripheralSettingsDraft.InventoryKey(snapshot));
        batteries.RemoveAt(2);
        snapshot["devices"]![0]!["name"] = "新耳机名称";
        Assert.NotEqual(key, PeripheralSettingsDraft.InventoryKey(snapshot));
    }

    [Fact]
    public void NestedEditsUseGlobalApplyAndKeepUnrelatedSettings()
    {
        var draft = new SettingsDraft();
        draft.Readback(JsonNode.Parse("""{"peripheralBattery":{"mode":"compact","order":["a","b"],"cardVisibility":{"a":true},"hideDisconnected":false},"quickCardOrder":["weather","media"],"toolWindowTheme":"System"}""")!.AsObject());
        var peripheral = new PeripheralSettingsDraft(draft);
        Assert.False(peripheral.Bool("headsetControlEnabled"));
        Assert.False(peripheral.Bool("vendorExtensionsEnabled"));
        peripheral.Set("mode", "detailed");
        peripheral.Set("hideDisconnected", true);
        peripheral.SetVisible("b", true);
        Assert.Equal(new[] { "peripheralBattery" }, draft.Pending.Keys);
        Assert.Equal("compact", draft.GetSaved("peripheralBattery")!["mode"]!.GetValue<string>());
        Assert.Equal("detailed", peripheral.Mode);
        Assert.True(peripheral.Visible("a", false));
        Assert.True(peripheral.Visible("b", false));
        Assert.False(peripheral.Visible("unknown", false));
        Assert.Equal(new[] { "a", "b" }, peripheral.Order([]));
        Assert.Equal("System", draft.Text("toolWindowTheme"));
    }

    [Fact]
    public void SaveAcknowledgementKeepsNewerNestedEditsIncludingRevertedValues()
    {
        var draft = new SettingsDraft();
        draft.Readback(JsonNode.Parse("""{"peripheralBattery":{"mode":"compact","cardVisibility":{"a":true}}}""")!.AsObject());
        var peripheral = new PeripheralSettingsDraft(draft);
        peripheral.Set("mode", "detailed");
        var submitted = draft.Capture();
        peripheral.Set("mode", "compact");
        peripheral.SetVisible("a", false);
        draft.Readback(JsonNode.Parse("""{"peripheralBattery":{"mode":"detailed","cardVisibility":{"a":true}}}""")!.AsObject(), submitted);
        Assert.Equal("compact", peripheral.Mode);
        Assert.False(peripheral.Visible("a", true));
        Assert.Single(draft.Pending);
    }

    [Fact]
    public void ReorderUsesStableIdsAndRetainsMissingDevicesAndVisibility()
    {
        var draft = new SettingsDraft();
        draft.Readback(JsonNode.Parse("""{"peripheralBattery":{"order":["a","missing","b","a"],"cardVisibility":{"missing":true,"a":false}}}""")!.AsObject());
        var peripheral = new PeripheralSettingsDraft(draft);
        Assert.Equal(new[] { "a", "missing", "b", "new" }, peripheral.Order(["b", "new", "a"]));
        peripheral.Reorder(["a", "b", "new"], "new", 0);
        Assert.Equal(new[] { "new", "missing", "a", "b" }, peripheral.Order(["b", "new", "a"]));
        Assert.True(peripheral.Visible("missing", false));
        Assert.False(peripheral.Visible("a", true));
    }

    [Fact]
    public void DynamicInventoryReadbackKeepsLayoutDraftAndEditsMadeDuringRequest()
    {
        var draft = new SettingsDraft();
        draft.Readback(JsonNode.Parse("""{"items.native_0.show":true,"items.native_0.row":0,"items.native_0.order":1,"items.native_0.label":"CPU"}""")!.AsObject());
        draft.Set("items.native_0.row", 1);
        var requested = draft.Capture();
        // A newer edit can return to the saved value and disappear from Pending.
        draft.Set("items.native_0.row", 0);
        draft.Set("items.native_0.label", "处理器");
        var inventory = JsonNode.Parse("""{"items.native_0.show":true,"items.native_0.row":1,"items.native_0.order":1,"items.native_0.label":"CPU","items.builtin_peripheral_a.show":false,"items.builtin_peripheral_a.row":0,"items.builtin_peripheral_a.order":2}""")!.AsObject();
        draft.Readback(inventory, requested, preservePending: true);
        var layout = new ModuleLayout(draft, [new("native_0", "CPU", "20%"), new("builtin_peripheral_a", "键盘", "80%")]);
        Assert.Equal(0, draft.Number("items.native_0.row"));
        Assert.Equal("处理器", draft.Text("items.native_0.label"));
        layout.Move("builtin_peripheral_a", 1, 0);
        var patch = draft.Capture().Values;
        layout.IncludeWholeLayout(patch);
        Assert.True(patch["items.builtin_peripheral_a.show"]!.GetValue<bool>());
        Assert.Equal(1, patch["items.builtin_peripheral_a.row"]!.GetValue<int>());
        Assert.Equal("处理器", draft.Text("items.native_0.label"));
    }

    private const string OldId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherOldId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string CanonicalId = "cccccccccccccccccccccccccccccccc";
    private const string UnrelatedId = "dddddddddddddddddddddddddddddddd";

    private static JsonObject Aliases() => new()
    {
        ["version"] = 2,
        ["aliases"] = new JsonObject { [OldId] = CanonicalId, [OtherOldId] = CanonicalId }
    };

    private static JsonObject PeripheralValues(string[] order, JsonObject visibility) => new()
    {
        ["peripheralBattery"] = new JsonObject
        {
            ["mode"] = "compact", ["order"] = System.Text.Json.JsonSerializer.SerializeToNode(order),
            ["cardVisibility"] = visibility
        },
        ["toolWindowTheme"] = "Dark"
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AliasMergeUsesCanonicalVisibilityAndEarliestOrder(bool canonicalVisible)
    {
        var draft = new SettingsDraft();
        draft.Readback(PeripheralValues([OldId, UnrelatedId, OtherOldId, CanonicalId], new JsonObject
        {
            [OldId] = !canonicalVisible, [CanonicalId] = canonicalVisible, [OtherOldId] = false
        }));
        draft.SetPeripheralAliases(Aliases());
        var peripheral = new PeripheralSettingsDraft(draft);
        Assert.Equal(new[] { CanonicalId, UnrelatedId }, peripheral.Order([OtherOldId, CanonicalId, OldId]));
        Assert.Equal(canonicalVisible, peripheral.Visible(OldId, !canonicalVisible));
        Assert.Equal(canonicalVisible, peripheral.Visible(CanonicalId, !canonicalVisible));
        Assert.Single(peripheral.Values["cardVisibility"]!.AsObject());
        Assert.Empty(draft.Pending);
        Assert.Equal("Dark", draft.Text("toolWindowTheme"));
        // A late v1 readback cannot put legacy identities back into the draft.
        draft.Readback(PeripheralValues([OldId, CanonicalId], new JsonObject { [OldId] = canonicalVisible }));
        Assert.Equal(new[] { CanonicalId }, peripheral.Order([OtherOldId]));
        Assert.Single(peripheral.Values["cardVisibility"]!.AsObject());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AliasVisibilityWithoutCanonicalUsesExplicitFalseIndependentOfInputOrder(bool reverse)
    {
        var visibility = reverse ? new JsonObject { [OtherOldId] = false, [OldId] = true }
            : new JsonObject { [OldId] = true, [OtherOldId] = false };
        var draft = new SettingsDraft();
        draft.Readback(PeripheralValues([UnrelatedId, OtherOldId, OldId], visibility));
        draft.SetPeripheralAliases(Aliases());
        var peripheral = new PeripheralSettingsDraft(draft);
        Assert.False(peripheral.Visible(CanonicalId, true));
        peripheral.Reorder([UnrelatedId, OldId, CanonicalId], OtherOldId, 0);
        Assert.Equal(new[] { CanonicalId, UnrelatedId }, peripheral.Order([OldId, OtherOldId]));
        peripheral.SetVisible(OldId, true);
        Assert.True(peripheral.Visible(CanonicalId, false));
        Assert.Single(peripheral.Values["cardVisibility"]!.AsObject());
    }

    [Fact]
    public void MigrationDuringSavePreservesPreviouslySubmittedLegacyEdit()
    {
        var draft = new SettingsDraft();
        var saved = PeripheralValues([OldId, CanonicalId], new JsonObject { [OldId] = true, [CanonicalId] = true });
        draft.Readback(saved);
        var peripheral = new PeripheralSettingsDraft(draft);
        peripheral.SetVisible(OldId, false);
        var submitted = draft.Capture();
        draft.SetPeripheralAliases(Aliases());
        // The pre-migration whole-object request was resolved using canonical precedence.
        draft.Readback(saved, submitted);
        Assert.False(peripheral.Visible(CanonicalId, true));
        Assert.Equal(new[] { CanonicalId }, peripheral.Order([OldId]));
        Assert.Single(draft.Pending);
        var retry = draft.Capture();
        Assert.False(retry.Values["peripheralBattery"]!["cardVisibility"]![CanonicalId]!.GetValue<bool>());
        draft.Readback(new JsonObject { ["peripheralBattery"] = retry.Values["peripheralBattery"]!.DeepClone() }, retry);
        Assert.Empty(draft.Pending);
    }

    [Fact]
    public void MigrationKeepsLatestEditIncludingLegacyRevertMadeDuringSave()
    {
        var draft = new SettingsDraft();
        draft.Readback(PeripheralValues([OldId, CanonicalId], new JsonObject { [OldId] = true, [CanonicalId] = false }));
        var peripheral = new PeripheralSettingsDraft(draft);
        peripheral.SetVisible(OldId, false);
        var submitted = draft.Capture();
        peripheral.SetVisible(OldId, true); // Revert removes Pending before identities merge.
        Assert.Empty(draft.Pending);
        draft.SetPeripheralAliases(Aliases());
        draft.Readback(PeripheralValues([CanonicalId], new JsonObject { [CanonicalId] = false }), submitted);
        Assert.True(peripheral.Visible(CanonicalId, false));
        Assert.Single(draft.Pending);
        // Later edits through either identity must beat the earlier migrated edit.
        peripheral.SetVisible(CanonicalId, false);
        peripheral.SetVisible(OtherOldId, true);
        draft.SetPeripheralAliases(Aliases());
        Assert.True(peripheral.Visible(CanonicalId, false));
        Assert.Single(peripheral.Values["cardVisibility"]!.AsObject());
    }

    [Fact]
    public void AliasSnapshotPreservesUnrelatedNestedEditsAndRejectsMalformedIds()
    {
        var draft = new SettingsDraft();
        draft.Readback(PeripheralValues([OldId, CanonicalId], new JsonObject { [OldId] = true }));
        var peripheral = new PeripheralSettingsDraft(draft);
        peripheral.Set("mode", "detailed");
        var submitted = draft.Capture();
        peripheral.Set("mode", "compact");
        peripheral.Set("hideDisconnected", true);
        var snapshot = Aliases();
        snapshot["aliases"]![UnrelatedId] = "../not-an-id";
        snapshot["aliases"]!["bad"] = CanonicalId;
        draft.SetPeripheralAliases(snapshot);
        draft.Readback(PeripheralValues([OldId], new JsonObject { [OldId] = true }), submitted);
        Assert.Equal("compact", peripheral.Mode);
        Assert.True(peripheral.Bool("hideDisconnected"));
        Assert.Equal(new[] { CanonicalId, UnrelatedId, "bad" }, peripheral.Order([UnrelatedId, "bad"]));
        Assert.Single(draft.Pending);
        Assert.Equal("Dark", draft.Text("toolWindowTheme"));
    }

    [Fact]
    public void AliasOnlyChangeInvalidatesInventoryKeyButDictionaryOrderDoesNot()
    {
        var snapshot = new JsonObject { ["version"] = 2, ["devices"] = new JsonArray() };
        var original = PeripheralSettingsDraft.InventoryKey(snapshot);
        snapshot["aliases"] = Aliases()["aliases"]!.DeepClone();
        var migrated = PeripheralSettingsDraft.InventoryKey(snapshot);
        Assert.NotEqual(original, migrated);
        snapshot["aliases"] = new JsonObject { [OtherOldId] = CanonicalId, [OldId] = CanonicalId };
        Assert.Equal(migrated, PeripheralSettingsDraft.InventoryKey(snapshot));
    }
}
