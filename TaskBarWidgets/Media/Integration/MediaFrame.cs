namespace TaskBarWidgets.Media.Integration;

internal sealed record MediaFrame(int version, string type, string sourceId, bool isPlaying,
    bool hasMedia, bool isBrowser, string trackId, string[] scripts);

internal sealed class MediaFrameFilter
{
    private readonly Dictionary<string, string> _last = new();
    internal object Filter(object value)
    {
        if (value is not MediaFrame frame) return value;
        return frame with { scripts = frame.scripts.Where(Changed).ToArray() };
    }

    private bool Changed(string script)
    {
        // Dispatch scripts use a trusted generated type marker. Full snapshots stay in the
        // bounded queue; only a live connection suppresses its already-applied heavy values.
        var slot = new[] { "style", "cover", "spectrumTuning", "browser", "lyrics", "spectrum" }.FirstOrDefault(type =>
            script.Contains($"\"type\":\"{type}\"", StringComparison.Ordinal));
        if (slot is null) return true;
        if (_last.TryGetValue(slot, out var previous) && previous == script) return false;
        _last[slot] = script;
        return true;
    }
}
