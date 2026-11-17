using System.Text.Json;
using System.Text.Json.Nodes;

namespace TaskBarWidgets.UI.Settings;

// Save acknowledgements only retire the submitted revision, never later edits.
internal sealed class SettingsDraft
{
    private readonly Dictionary<string, JsonNode?> _base = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _revisions = new(StringComparer.Ordinal);
    private long _revision;
    private readonly Dictionary<string, string> _peripheralAliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Revision, bool Visible)> _peripheralVisibilityEdits = new(StringComparer.Ordinal);

    internal string PeripheralId(string id)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = id;
        while (_peripheralAliases.TryGetValue(current, out var canonical))
        {
            if (!visited.Add(current)) return id;
            current = canonical;
        }
        return current;
    }

    internal void SetPeripheralAliases(JsonObject snapshot)
    {
        if (snapshot["version"]?.ToString() != "2" || snapshot["aliases"] is not JsonObject aliases) return;
        foreach (var pair in aliases)
            if (ValidPeripheralId(pair.Key) && pair.Value is JsonValue value && value.TryGetValue<string>(out var id) &&
                ValidPeripheralId(id) && pair.Key != id)
                _peripheralAliases[pair.Key] = id;
        var current = Get("peripheralBattery");
        var hadEdits = Pending.ContainsKey("peripheralBattery") || _peripheralVisibilityEdits.Count > 0;
        if (_base.TryGetValue("peripheralBattery", out var saved))
            _base["peripheralBattery"] = NormalizePeripheral(saved);
        if (current is not null)
        {
            // Even an edit reverted to the old saved value may win after identities merge.
            var normalized = NormalizePeripheral(current, includeEdits: true);
            // A save already in flight may still acknowledge the pre-migration object.
            if (hadEdits && !JsonNode.DeepEquals(current, normalized)) SetNode("peripheralBattery", normalized);
            else if (JsonNode.DeepEquals(_base.GetValueOrDefault("peripheralBattery"), normalized)) Pending.Remove("peripheralBattery");
            else Pending["peripheralBattery"] = normalized;
        }
    }

    private static bool ValidPeripheralId(string id) => id.Length == 32 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private JsonNode? NormalizePeripheral(JsonNode? node, bool includeEdits = false)
    {
        if (node is not JsonObject values) return node?.DeepClone();
        var result = PeripheralSettingsDraft.Normalize(values, PeripheralId);
        if (includeEdits && _peripheralVisibilityEdits.Count > 0)
        {
            var visibility = result["cardVisibility"] as JsonObject ?? new JsonObject();
            foreach (var pair in _peripheralVisibilityEdits.OrderBy(pair => pair.Value.Revision))
                visibility[PeripheralId(pair.Key)] = pair.Value.Visible;
            if (visibility.Parent is null) result["cardVisibility"] = visibility;
        }
        return result;
    }

    internal void SetPeripheralVisible(string id, bool visible)
    {
        _peripheralVisibilityEdits[id] = (_revision + 1, visible);
        var values = Get("peripheralBattery") as JsonObject ?? new JsonObject();
        var visibility = values["cardVisibility"] as JsonObject ?? new JsonObject();
        visibility[PeripheralId(id)] = visible;
        if (visibility.Parent is null) values["cardVisibility"] = visibility;
        SetNode("peripheralBattery", values);
    }
    public Dictionary<string, JsonNode?> Pending { get; } = new(StringComparer.Ordinal);
    internal bool UpdatingControls { get; set; }
    public IEnumerable<string> Keys => _base.Keys;
    public JsonNode? Get(string key) => (Pending.TryGetValue(key, out var draft) ? draft : _base.GetValueOrDefault(key))?.DeepClone();
    public JsonNode? GetSaved(string key) => _base.GetValueOrDefault(key)?.DeepClone();
    public bool Bool(string key, bool fallback = false) => Get(key) is JsonValue value && value.TryGetValue<bool>(out var result) ? result : fallback;
    public double Number(string key, double fallback = 0) => double.TryParse(Get(key)?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;
    public double SavedNumber(string key, double fallback = 0) => double.TryParse(GetSaved(key)?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;
    public string Text(string key, string fallback = "") => Get(key)?.ToString() ?? fallback;
    public void Set(string key, object? value) => SetNode(key, JsonSerializer.SerializeToNode(value));
    public void SetNode(string key, JsonNode? value)
    {
        if (UpdatingControls) return;
        if (key == "peripheralBattery") value = NormalizePeripheral(value, includeEdits: true);
        _revisions[key] = ++_revision;
        if (JsonNode.DeepEquals(_base.GetValueOrDefault(key), value)) Pending.Remove(key);
        else Pending[key] = value?.DeepClone();
    }
    public SaveSnapshot Capture() => new(_revision, Pending.ToDictionary(pair => pair.Key, pair => pair.Value?.DeepClone(), StringComparer.Ordinal));
    public void Readback(IEnumerable<KeyValuePair<string, JsonNode?>> values, SaveSnapshot? saved = null, bool preservePending = false)
    {
        var newer = saved is null ? new Dictionary<string, JsonNode?>() : _revisions
            .Where(pair => pair.Value > saved.Revision).ToDictionary(pair => pair.Key, pair => Get(pair.Key), StringComparer.Ordinal);
        if (preservePending) foreach (var pair in Pending) newer.TryAdd(pair.Key, pair.Value?.DeepClone());
        foreach (var id in _peripheralVisibilityEdits.Keys.ToArray())
            if (!(saved is not null && _peripheralVisibilityEdits[id].Revision > saved.Revision) &&
                !(preservePending && Pending.ContainsKey("peripheralBattery")))
                _peripheralVisibilityEdits.Remove(id);
        _base.Clear();
        foreach (var pair in values) _base[pair.Key] = pair.Key == "peripheralBattery" ? NormalizePeripheral(pair.Value) : pair.Value?.DeepClone();
        Pending.Clear();
        foreach (var pair in newer) SetNode(pair.Key, pair.Value);
    }
    internal sealed record SaveSnapshot(long Revision, Dictionary<string, JsonNode?> Values);
}

internal sealed record DisplayModule(string Id, string Name, string Sample, bool DoubleLineExclusive = false);

// All peripheral edits use the existing revisioned, global Apply draft.
internal sealed class PeripheralSettingsDraft(SettingsDraft draft)
{
    internal static JsonObject Normalize(JsonObject values, Func<string, string> canonicalId)
    {
        var result = (JsonObject)values.DeepClone();
        if (values["order"] is JsonArray order)
            result["order"] = JsonSerializer.SerializeToNode(order.OfType<JsonValue>()
                .Select(value => value.TryGetValue<string>(out var id) ? id : "")
                .Where(id => id.Length > 0).Select(canonicalId).Distinct(StringComparer.Ordinal).ToArray());
        if (values["cardVisibility"] is JsonObject visibility)
        {
            var merged = new JsonObject();
            foreach (var group in visibility.Where(pair => pair.Value is JsonValue value && value.TryGetValue<bool>(out _))
                .GroupBy(pair => canonicalId(pair.Key), StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                // Explicit canonical wins; otherwise any explicit false wins.
                merged[group.Key] = visibility[group.Key] is JsonValue value && value.TryGetValue<bool>(out var enabled)
                    ? enabled : group.All(pair => pair.Value!.GetValue<bool>());
            }
            result["cardVisibility"] = merged;
        }
        return result;
    }

    // Polling charge/state changes must not reload the taskbar editor's schema.
    public static string InventoryKey(JsonObject snapshot) => JsonSerializer.Serialize(new
    {
        Aliases = (snapshot["aliases"] as JsonObject ?? new JsonObject())
            .OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new { pair.Key, Value = pair.Value?.ToString() }).ToArray(),
        Devices = (snapshot["devices"] as JsonArray ?? []).OfType<JsonObject>()
            .OrderBy(device => device["id"]?.ToString(), StringComparer.Ordinal)
            .Select(device => new
            {
                Id = device["id"]?.ToString(), Name = device["name"]?.ToString(), Kind = device["kind"]?.ToString(),
                Roles = (device["batteries"] as JsonArray ?? []).OfType<JsonObject>()
                    .Select(battery => battery["role"]?.ToString()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
            }).ToArray()
    });
    public JsonObject Values => draft.Get("peripheralBattery") as JsonObject ?? new JsonObject();
    public bool Bool(string key) => Values[key] is JsonValue value && value.TryGetValue<bool>(out var result) && result;
    public string Mode => Values["mode"]?.ToString() == "detailed" ? "detailed" : "compact";
    public void Set(string key, object value)
    {
        var values = Values;
        values[key] = JsonSerializer.SerializeToNode(value);
        draft.SetNode("peripheralBattery", values);
    }
    public bool Visible(string id, bool fallback) => Values["cardVisibility"] is JsonObject visibility &&
        visibility[draft.PeripheralId(id)] is JsonValue value && value.TryGetValue<bool>(out var enabled) ? enabled : fallback;
    public void SetVisible(string id, bool visible) => draft.SetPeripheralVisible(id, visible);
    public string[] Order(IEnumerable<string> inventory)
    {
        var saved = (Values["order"] as JsonArray ?? []).OfType<JsonValue>()
            .Select(value => value.TryGetValue<string>(out var id) ? id : "").Where(id => id.Length > 0);
        return saved.Concat(inventory).Select(draft.PeripheralId).Distinct(StringComparer.Ordinal).ToArray();
    }
    public void Reorder(IReadOnlyList<string> visibleOrder, string id, int gap)
    {
        visibleOrder = visibleOrder.Select(draft.PeripheralId).Distinct(StringComparer.Ordinal).ToArray();
        var moved = ReorderSequence.MoveToGap(visibleOrder, draft.PeripheralId(id), gap);
        // Keep remembered positions for devices missing from the current inventory.
        var full = Order(visibleOrder);
        var visible = visibleOrder.ToHashSet(StringComparer.Ordinal);
        var index = 0;
        Set("order", full.Select(item => visible.Contains(item) ? moved[index++] : item).ToArray());
    }
}
internal sealed record DisplayColumn(DisplayModule? Upper, DisplayModule? Lower, DisplayModule? FullHeight = null);

internal sealed class ModuleLayout(SettingsDraft draft, IReadOnlyList<DisplayModule> modules)
{
    public IReadOnlyList<DisplayModule> Modules => modules;
    public bool SingleRow => draft.Bool("taskbar.horizontal_arrange");
    public string Key(string id, string property) => $"items.{id}.{property}";
    public bool Enabled(string id) => draft.Bool(Key(id, "show"));
    public int Row(string id) => SingleRow ? 0 : (int)draft.Number(Key(id, "row"));
    public List<DisplayModule> Ordered() => modules.OrderBy(item => draft.Number(Key(item.Id, "order"))).ToList();
    public List<DisplayModule> InRow(int row) => Ordered().Where(item => Enabled(item.Id) && Row(item.Id) == row).ToList();
    public IReadOnlyList<DisplayColumn> Columns()
    {
        if (SingleRow) return InRow(0).Select(item => new DisplayColumn(item, null)).ToList();
        var upper = InRow(0); var lower = InRow(1);
        var columns = new List<DisplayColumn>();
        var top = 0; var bottom = 0;
        while (top < upper.Count || bottom < lower.Count)
        {
            if (top < upper.Count && upper[top].DoubleLineExclusive) columns.Add(new(null, null, upper[top++]));
            else if (bottom < lower.Count && lower[bottom].DoubleLineExclusive) columns.Add(new(null, null, lower[bottom++]));
            else columns.Add(new(top < upper.Count ? upper[top++] : null, bottom < lower.Count ? lower[bottom++] : null));
        }
        return columns;
    }
    public void Remove(string id) => draft.Set(Key(id, "show"), false);
    public void Move(string id, int row, int insertionIndex)
    {
        var item = modules.First(module => module.Id == id);
        row = SingleRow ? 0 : Math.Clamp(row, 0, 1);
        var rows = new[] { InRow(0), InRow(1) };
        foreach (var list in rows) list.RemoveAll(module => module.Id == id);
        rows[row].Insert(Math.Clamp(insertionIndex, 0, rows[row].Count), item);
        draft.Set(Key(id, "show"), true);
        draft.Set(Key(id, "row"), row);
        var active = rows.SelectMany(list => list).ToList();
        var full = active.Concat(Ordered().Where(module => active.All(shown => shown.Id != module.Id))).ToList();
        for (var index = 0; index < full.Count; index++) draft.Set(Key(full[index].Id, "order"), index + 1);
    }
    public void IncludeWholeLayout(Dictionary<string, JsonNode?> patch)
    {
        for (var index = 0; index < modules.Count; index++)
        {
            var item = modules[index];
            foreach (var property in new[] { "show", "row", "order" })
                patch[Key(item.Id, property)] = property == "show" ? JsonValue.Create(Enabled(item.Id)) :
                    JsonValue.Create((int)draft.Number(Key(item.Id, property), property == "order" ? index + 1 : 0));
        }
    }
}
