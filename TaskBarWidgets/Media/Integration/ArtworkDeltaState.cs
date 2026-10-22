namespace TaskBarWidgets.Media.Integration;

/// <summary>
/// Per-connection memory of the artwork already sent by getState. The card polls every 400 ms
/// and the source icons dominate the reply, so a client may opt into an artwork delta where an
/// unchanged cover or icon string is omitted. The memory belongs to exactly one connection: a
/// new generation starts from a complete reply, because a re-created client cannot inherit
/// artwork the previous client received.
/// </summary>
internal sealed class ArtworkDeltaState
{
    private long _generation = -1;
    private string? _cover;
    private readonly Dictionary<string, string> _icons = new(StringComparer.Ordinal);

    /// <summary>Returns true when this reply may omit unchanged artwork.</summary>
    public bool Begin(long generation, bool requested)
    {
        if (generation == _generation) return requested;
        _generation = generation;
        _cover = null;
        _icons.Clear();
        return false;
    }

    public bool CoverChanged(string cover, bool delta) =>
        !delta || !string.Equals(cover, _cover, StringComparison.Ordinal);

    public void CommitCover(string cover) => _cover = cover;

    public bool IconChanged(string id, string icon, bool delta)
    {
        if (delta && _icons.TryGetValue(id, out var known) &&
            string.Equals(known, icon, StringComparison.Ordinal)) return false;
        _icons[id] = icon;
        return true;
    }
}
