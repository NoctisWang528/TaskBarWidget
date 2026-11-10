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
}
