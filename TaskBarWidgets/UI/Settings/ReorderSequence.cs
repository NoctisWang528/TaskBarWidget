namespace TaskBarWidgets.UI.Settings;

internal static class ReorderSequence
{
    // gap refers to the original list (before/after a row), before removing the source.
    public static string[] MoveToGap(IReadOnlyList<string> order, string id, int gap)
    {
        var result = order.ToList();
        var source = result.IndexOf(id);
        if (source < 0) return result.ToArray();
        gap = Math.Clamp(gap, 0, result.Count);
        result.RemoveAt(source);
        if (source < gap) gap--;
        result.Insert(gap, id);
        return result.ToArray();
    }
}
