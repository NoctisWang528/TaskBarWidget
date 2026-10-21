using TaskbarLyrics.Core.Models;

namespace TaskBarWidgets.Media.Integration;

internal static class MediaPresentationTimeline
{
    internal static PlaybackSnapshot Project(
        PlaybackSnapshot authoritative,
        DateTimeOffset authoritativeAtUtc,
        DateTimeOffset nowUtc)
    {
        if (!authoritative.IsPlaying || authoritative.Track is null) return authoritative;

        var elapsed = nowUtc - authoritativeAtUtc;
        if (elapsed <= TimeSpan.Zero) return authoritative;
        if (elapsed > MediaRenderScheduler.FallbackInterval)
        {
            elapsed = MediaRenderScheduler.FallbackInterval;
        }
        return authoritative with { Position = authoritative.Position + elapsed };
    }
}
