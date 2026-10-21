using System.Text.Json;
using TaskbarLyrics.App;
using TaskbarLyrics.Core.Models;
using TaskbarLyrics.Core.Services;

namespace TaskBarWidgets.Media.Integration;

internal static class MediaPresentationTickBuilder
{
    internal static async Task<MediaPresentationUpdate?> BuildAsync(
        PlaybackSnapshot authoritative,
        DateTimeOffset authoritativeAtUtc,
        DateTimeOffset nowUtc,
        bool isBrowser,
        bool taskbarVisible,
        bool cardVisible,
        MediaFrame previous,
        MediaPresentationState state,
        LyricSyncService lyrics,
        AppSettings settings,
        IReadOnlyList<float> spectrum,
        JsonElement nativeAppearance,
        Func<bool>? isCurrent = null)
    {
        if (!authoritative.IsPlaying || !previous.isPlaying) return null;

        var projected = MediaPresentationTimeline.Project(
            authoritative,
            authoritativeAtUtc,
            nowUtc);
        var lyricInput = NonLyricsMediaPolicy.IsNonLyrics(projected)
            ? projected with { Track = null }
            : projected;
        var display = await lyrics.GetDisplayFrameAsync(lyricInput);
        var scene = LyricsPresentationCalculator.ResolveScene(
            settings,
            display,
            isBrowser,
            projected.Track is not null,
            lyrics.CurrentLyricAcquisition);
        var cardScene = LyricsPresentationCalculator.ResolveCardScene(
            settings,
            display,
            isBrowser,
            projected.Track is not null,
            lyrics.CurrentLyricAcquisition);
        // A visible card keeps lyric advancement alive even while the taskbar
        // shows its spectrum, and a hidden pair of surfaces stops it again.
        if (!MediaPresentationActivity.ShouldAdvancePresentation(
            taskbarVisible,
            scene,
            cardVisible,
            cardScene))
        {
            return null;
        }

        var lyricFrame = LyricsPresentationCalculator.Create(
            projected,
            display,
            settings,
            scene,
            isBrowser,
            lyrics.CurrentLyricAcquisition);
        if (isCurrent is not null && !isCurrent()) return null;
        return state.Apply(new MediaPresentationInput(
            projected,
            isBrowser,
            lyricFrame.trackId,
            lyricFrame,
            cardScene,
            previous.style ?? throw new InvalidOperationException("Presentation frame has no style."),
            previous.cover ?? NativeMediaPayloadFactory.EmptyCover(),
            previous.browser,
            previous.spectrumTuning,
            spectrum,
            nativeAppearance));
    }
}
