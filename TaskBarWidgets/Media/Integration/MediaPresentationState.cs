using System.Text.Json;
using TaskbarLyrics.Core.Models;

namespace TaskBarWidgets.Media.Integration;

internal readonly record struct MediaPresentationInput(
    PlaybackSnapshot Snapshot,
    bool IsBrowser,
    string TrackId,
    MediaLyricsFrame Lyrics,
    MediaStyleFrame Style,
    MediaCoverFrame Cover,
    MediaBrowserFrame Browser,
    MediaSpectrumTuningFrame SpectrumTuning,
    IReadOnlyList<float> Spectrum,
    JsonElement NativeAppearance);

internal readonly record struct MediaPresentationUpdate(
    MediaFrame Frame,
    CardFrame Card,
    bool MediaFrameChanged,
    bool CardFrameChanged);

internal sealed class MediaPresentationState
{
    private long _mediaRevision = -1;
    private long _cardRevision = -1;
    private MediaFrame? _frame;
    private CardFrame? _card;

    internal MediaFrame? CurrentFrame => _frame;
    internal CardFrame? CurrentCard => _card;

    internal MediaPresentationUpdate Apply(MediaPresentationInput input)
    {
        var nextFrame = new MediaFrame(
            version: MediaProtocol.Version,
            type: "frame",
            renderVersion: MediaProtocol.RenderVersion,
            revision: _mediaRevision,
            sourceId: input.Snapshot.SourceAppUserModelId,
            isPlaying: input.Snapshot.IsPlaying,
            hasMedia: input.Snapshot.Track is not null,
            isBrowser: input.IsBrowser,
            trackId: input.TrackId,
            lyrics: input.Lyrics,
            style: input.Style,
            cover: input.Cover,
            browser: input.Browser,
            spectrumTuning: input.SpectrumTuning,
            spectrum: input.Spectrum.ToArray());
        var mediaChanged = _frame is null || !_frame.StateEquals(nextFrame);
        if (mediaChanged)
        {
            nextFrame = nextFrame with { revision = ++_mediaRevision };
            _frame = nextFrame;
        }

        var available = !input.IsBrowser &&
            input.Lyrics.currentLineIndex >= 0 &&
            !input.Lyrics.isPureMusic;
        var nextCard = new CardFrame(
            renderVersion: MediaProtocol.RenderVersion,
            revision: _cardRevision,
            trackId: input.TrackId,
            available: available,
            isPlaying: input.Snapshot.IsPlaying,
            lyrics: input.Lyrics,
            style: input.Style,
            nativeAppearance: NativeAppearanceJson.Normalize(input.NativeAppearance));
        var cardChanged = _card is null || !_card.StateEquals(nextCard);
        if (cardChanged)
        {
            nextCard = nextCard with { revision = ++_cardRevision };
            _card = nextCard;
        }

        return new MediaPresentationUpdate(_frame!, _card!, mediaChanged, cardChanged);
    }

    internal MediaFrame? UpdateSpectrum(IReadOnlyList<float> bars)
    {
        if (_frame is null) return null;
        var normalized = bars.Select(value => Math.Clamp(value, 0f, 1f)).ToArray();
        var next = _frame with
        {
            revision = _mediaRevision + 1,
            spectrum = normalized
        };
        if (_frame.StateEquals(next)) return null;
        _mediaRevision++;
        _frame = next;
        return next;
    }
}
