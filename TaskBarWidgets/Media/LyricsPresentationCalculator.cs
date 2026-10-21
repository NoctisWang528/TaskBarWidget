using TaskBarWidgets.Media.Integration;
using TaskbarLyrics.Core.Models;
using TaskbarLyrics.Core.Services;

namespace TaskbarLyrics.App;

internal static class LyricsPresentationCalculator
{
    public static MediaLyricsFrame Create(
        PlaybackSnapshot snapshot,
        LyricDisplayFrame frame,
        AppSettings settings,
        LyricsPresentationScene scene,
        bool isBrowser,
        LyricAcquisitionKind acquisition)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(settings);

        var track = snapshot.Track;
        var current = frame.CurrentLine;
        var next = frame.NextLine;
        if (!isBrowser &&
            scene == LyricsPresentationScene.Searching &&
            acquisition == LyricAcquisitionKind.Searching)
        {
            var searching = LyricsSearchingPresentationPolicy.Create(track);
            current = searching.Current;
            next = searching.Next;
        }
        if (track is null)
        {
            current = "等待播放...";
            next = string.Empty;
        }

        return new MediaLyricsFrame(
            current: current,
            next: next,
            progress: Math.Clamp(frame.LineProgress, 0, 1),
            currentLineIndex: current == frame.CurrentLine
                ? frame.CurrentLineIndex
                : -1,
            trackId: track is null ? string.Empty : LyricSyncService.BuildStableTrackIdentity(track),
            isPureMusic: frame.IsPureMusic,
            isPlaying: snapshot.IsPlaying,
            currentTranslation: frame.CurrentTranslation ?? string.Empty,
            nextTranslation: frame.NextTranslation ?? string.Empty,
            translationMode: settings.ShowLyricTranslation && frame.HasTrackTranslation,
            animateTransition: true,
            wordScanProgress: settings.EnableWordScanning
                ? frame.WordScanProgress is { } progress
                    ? Math.Clamp(progress, 0, 1)
                    : null
                : null,
            scene: scene.ToWireValue());
    }

    // The card never renders the spectrum, so it keeps a lyric-only scene even
    // when the taskbar switches to spectrum. Availability still decides whether
    // the card shows anything at all.
    public static LyricsPresentationScene ResolveCardScene(
        AppSettings settings,
        LyricDisplayFrame frame,
        bool isBrowser,
        bool hasTrack,
        LyricAcquisitionKind acquisition)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(frame);
        if (!hasTrack) return LyricsPresentationScene.NoPlayback;
        if (isBrowser) return LyricsPresentationScene.Message;
        if (acquisition == LyricAcquisitionKind.Searching) return LyricsPresentationScene.Searching;
        return frame.CurrentLineIndex >= 0 && !frame.IsPureMusic
            ? LyricsPresentationScene.Lyrics
            : LyricsPresentationScene.Message;
    }

    public static MediaLyricsFrame ForCard(MediaLyricsFrame frame, LyricsPresentationScene cardScene) =>
        frame with { scene = cardScene.ToWireValue() };

    public static LyricsPresentationScene ResolveScene(
        AppSettings settings,
        LyricDisplayFrame frame,
        bool isBrowser,
        bool hasTrack,
        LyricAcquisitionKind acquisition)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(frame);
        if (!hasTrack) return LyricsPresentationScene.NoPlayback;
        if (isBrowser) return LyricsPresentationScene.Message;
        var showSpectrum = settings.SpectrumDisplayMode switch
        {
            SpectrumDisplayMode.Always => true,
            SpectrumDisplayMode.PureMusicOrNoLyrics =>
                frame.IsPureMusic || frame.CurrentLine == LyricSyncService.NoLyricsText,
            SpectrumDisplayMode.PureMusicOnly => frame.IsPureMusic,
            _ => false
        };
        if (showSpectrum) return LyricsPresentationScene.Spectrum;
        if (acquisition == LyricAcquisitionKind.Searching) return LyricsPresentationScene.Searching;
        return frame.CurrentLineIndex >= 0
            ? LyricsPresentationScene.Lyrics
            : LyricsPresentationScene.Message;
    }

}
