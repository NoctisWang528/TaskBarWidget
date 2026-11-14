using TaskbarLyrics.Core.Models;
using TaskBarWidgets.Media.Integration;
using TaskbarLyrics.Core.Services;

namespace TaskbarLyrics.App;

internal readonly record struct LyricsSearchingPresentation(string Current, string Next);

internal static class LyricsSearchingPresentationPolicy
{
    public static LyricsSearchingPresentation Create(TrackInfo? track)
    {
        var title = track?.Title?.Trim() ?? string.Empty;
        var artist = track is null ? string.Empty : MediaSourcePresentation.Artist(
            track.Artist, new MediaSourceChoice(track.SourceApp, track.SourceApp)).Trim();
        var current = title.Length == 0
            ? artist
            : artist.Length == 0
                ? title
                : $"{title} - {artist}";

        if (current.Length == 0)
        {
            current = LyricSyncService.SearchingText;
        }

        return new LyricsSearchingPresentation(current, LyricSyncService.SearchingText);
    }
}
