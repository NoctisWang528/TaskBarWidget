using TaskbarLyrics.App;

namespace TaskBarWidgets.Media.Integration;

internal sealed record MediaSessionCandidate(string Id, string Source, bool IsPlaying, int Priority);

internal static class MediaSourcePolicy
{
    internal static string? Select(IReadOnlyList<MediaSessionCandidate> candidates, string? manualId, string? currentId)
    {
        if (candidates.Any(candidate => candidate.Id == manualId)) return manualId;
        var ordered = candidates.OrderByDescending(candidate => candidate.IsPlaying)
            .ThenBy(IsNonLyrics)
            .ThenBy(candidate => candidate.Priority).ToArray();
        var best = ordered.FirstOrDefault();
        if (best is null) return null;
        var current = candidates.FirstOrDefault(candidate => candidate.Id == currentId);
        // A paused music player must not displace Douyin between videos or while paused.
        if (!best.IsPlaying && current is not null &&
            (NonLyricsMediaPolicy.IsDouyin(current.Id) || NonLyricsMediaPolicy.IsDouyin(current.Source))) return current.Id;
        // Keep the current playing music source while QQ and NetEase both remain active.
        if (current is { IsPlaying: true } && best.IsPlaying &&
            !IsNonLyrics(current) && !IsNonLyrics(best)) return current.Id;
        return best.Id;
    }

    private static bool IsNonLyrics(MediaSessionCandidate candidate) =>
        NonLyricsMediaPolicy.IsNonLyrics(candidate.Id) || NonLyricsMediaPolicy.IsNonLyrics(candidate.Source);
}
