using System.Text.Json;
using System.Text.Json.Nodes;

namespace TaskBarWidgets.UI.Settings;

// Save acknowledgements only retire the submitted revision, never later edits.
internal sealed class SettingsDraft
{
    private readonly Dictionary<string, JsonNode?> _base = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _revisions = new(StringComparer.Ordinal);
    private long _revision;
    public Dictionary<string, JsonNode?> Pending { get; } = new(StringComparer.Ordinal);
    public IEnumerable<string> Keys => _base.Keys;
    public JsonNode? Get(string key) => (Pending.TryGetValue(key, out var draft) ? draft : _base.GetValueOrDefault(key))?.DeepClone();
    public bool Bool(string key, bool fallback = false) => Get(key) is JsonValue value && value.TryGetValue<bool>(out var result) ? result : fallback;
    public double Number(string key, double fallback = 0) => double.TryParse(Get(key)?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;
    public string Text(string key, string fallback = "") => Get(key)?.ToString() ?? fallback;
    public void Set(string key, object? value) => SetNode(key, JsonSerializer.SerializeToNode(value));
    public void SetNode(string key, JsonNode? value)
    {
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
        _base.Clear();
        foreach (var pair in values) _base[pair.Key] = pair.Value?.DeepClone();
        Pending.Clear();
        foreach (var pair in newer) SetNode(pair.Key, pair.Value);
    }
    internal sealed record SaveSnapshot(long Revision, Dictionary<string, JsonNode?> Values);
}

internal sealed record DisplayModule(string Id, string Name, string Sample, bool DoubleLineExclusive = false);
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
