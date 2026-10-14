namespace TaskBarWidgets.Media.Integration;

internal sealed record MediaSessionCandidate(string Id, string Source, bool IsPlaying, int Priority);

internal static class MediaSourcePolicy
{
    internal static string? Select(IReadOnlyList<MediaSessionCandidate> candidates, string? manualId, string? currentId)
    {
        if (candidates.Any(candidate => candidate.Id == manualId)) return manualId;
        var ordered = candidates.OrderByDescending(candidate => candidate.IsPlaying)
            .ThenBy(candidate => candidate.Source.Equals("Browser", StringComparison.OrdinalIgnoreCase))
            .ThenBy(candidate => candidate.Priority).ToArray();
        var best = ordered.FirstOrDefault();
        if (best is null) return null;
        var current = candidates.FirstOrDefault(candidate => candidate.Id == currentId);
        // Keep the current playing music source while QQ and NetEase both remain active.
        if (current is { IsPlaying: true } && best.IsPlaying &&
            !current.Source.Equals("Browser", StringComparison.OrdinalIgnoreCase) &&
            !best.Source.Equals("Browser", StringComparison.OrdinalIgnoreCase)) return current.Id;
        return best.Id;
    }
}
