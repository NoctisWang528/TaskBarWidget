using System.IO;
using System.Text.Json;
using TaskbarLyrics.App;

namespace TaskBarWidgets.Media.Integration;

internal static class MediaProtocol
{
    internal const int Version = 1;
    internal const int RenderVersion = 2;

    internal static object Initialize() => new
    {
        version = Version,
        type = "initialize",
        renderVersion = RenderVersion
    };
}

internal sealed record MediaLyricsFrame(
    string current,
    string next,
    double progress,
    int currentLineIndex,
    string trackId,
    bool isPureMusic,
    bool isPlaying,
    string currentTranslation,
    string nextTranslation,
    bool translationMode,
    bool animateTransition,
    double? wordScanProgress,
    string scene);

internal sealed record MediaStyleFrame(
    string fontFamily,
    double layoutScalePercent,
    double fontSize,
    bool showCover,
    string coverPosition,
    bool enableControlPanel,
    double coverSize,
    double coverGap,
    double coverCornerRadius,
    double viewportDescenderBuffer,
    double layoutHorizontalPadding,
    double lyricsPaneTopPadding,
    double lyricsPaneRightPadding,
    double lyricsPaneLeftPadding,
    double primaryOffsetY,
    double secondaryOffsetY,
    double lineTextBottomPadding,
    double surfaceRadius,
    double layerTransitionOffset,
    double coverFallbackFontSize,
    double spectrumWidth,
    double spectrumHeight,
    double spectrumGap,
    double spectrumBarWidth,
    double spectrumBarHeight,
    double spectrumLowHeight,
    double spectrumHighHeight,
    double spectrumMiddleHeight,
    string fontWeight,
    string primaryColor,
    string secondaryColor,
    string translationColor,
    string wordScanOverlayColor,
    string surfaceColor,
    string surfaceShadow,
    string textShadow,
    string textAlignment);

internal sealed record MediaCoverFrame(
    string dataUri,
    string fallbackText,
    string fallbackColor,
    string fallbackIconDataUri,
    string trackId);

internal sealed record MediaBrowserFrame(
    bool active,
    string title,
    string artist,
    string source,
    bool isPlaying,
    bool canPrevious,
    bool canNext,
    bool canPlayPause);

internal sealed record MediaSpectrumTuningFrame(
    double rise,
    double fall,
    double minHeight,
    double heightRange,
    double opacity,
    int barCount);

internal sealed record MediaFrame(
    int version,
    string type,
    int renderVersion,
    long revision,
    string sourceId,
    bool isPlaying,
    bool hasMedia,
    bool isBrowser,
    string trackId,
    MediaLyricsFrame lyrics,
    MediaStyleFrame? style,
    MediaCoverFrame? cover,
    MediaBrowserFrame browser,
    MediaSpectrumTuningFrame spectrumTuning,
    float[] spectrum)
{
    // null means "reuse the value already applied on this connection". A clear
    // remains an empty cover object so it can never be mistaken for reuse.
    internal MediaFrame WithConnectionDelta(MediaStyleFrame? style, MediaCoverFrame? cover) =>
        this with { style = style, cover = cover };

    internal bool StateEquals(MediaFrame other) =>
        version == other.version &&
        type == other.type &&
        renderVersion == other.renderVersion &&
        string.Equals(sourceId, other.sourceId, StringComparison.Ordinal) &&
        isPlaying == other.isPlaying &&
        hasMedia == other.hasMedia &&
        isBrowser == other.isBrowser &&
        string.Equals(trackId, other.trackId, StringComparison.Ordinal) &&
        lyrics == other.lyrics &&
        style == other.style &&
        cover == other.cover &&
        browser == other.browser &&
        spectrumTuning == other.spectrumTuning &&
        spectrum.SequenceEqual(other.spectrum);

    internal bool SemanticEquals(MediaFrame other) =>
        version == other.version &&
        type == other.type &&
        renderVersion == other.renderVersion &&
        string.Equals(sourceId, other.sourceId, StringComparison.Ordinal) &&
        isPlaying == other.isPlaying &&
        hasMedia == other.hasMedia &&
        isBrowser == other.isBrowser &&
        string.Equals(trackId, other.trackId, StringComparison.Ordinal) &&
        style == other.style &&
        cover == other.cover &&
        browser == other.browser &&
        spectrumTuning == other.spectrumTuning;
}

internal sealed record CardFrame(
    int renderVersion,
    long revision,
    string trackId,
    bool available,
    bool isPlaying,
    MediaLyricsFrame lyrics,
    MediaStyleFrame style,
    JsonElement nativeAppearance)
{
    internal bool StateEquals(CardFrame other) =>
        renderVersion == other.renderVersion &&
        string.Equals(trackId, other.trackId, StringComparison.Ordinal) &&
        available == other.available &&
        isPlaying == other.isPlaying &&
        lyrics == other.lyrics &&
        style == other.style &&
        NativeAppearanceJson.Equivalent(nativeAppearance, other.nativeAppearance);
}

internal static class NativeAppearanceJson
{
    internal static JsonElement Empty { get; } = CreateEmpty();

    internal static JsonElement Normalize(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object ? value.Clone() : Empty;

    internal static bool Equivalent(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != JsonValueKind.Object || right.ValueKind != JsonValueKind.Object)
        {
            return left.ValueKind == right.ValueKind && left.GetRawText() == right.GetRawText();
        }

        using var leftBuffer = new MemoryStream();
        using var rightBuffer = new MemoryStream();
        using (var leftWriter = new Utf8JsonWriter(leftBuffer))
        {
            left.WriteTo(leftWriter);
        }
        using (var rightWriter = new Utf8JsonWriter(rightBuffer))
        {
            right.WriteTo(rightWriter);
        }
        return leftBuffer.ToArray().AsSpan().SequenceEqual(rightBuffer.ToArray());
    }

    private static JsonElement CreateEmpty()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}

internal sealed class MediaFrameFilter
{
    private MediaStyleFrame? _lastStyle;
    private MediaCoverFrame _lastCover = NativeMediaPayloadFactory.EmptyCover();
    private bool _hasCover;

    internal object Filter(object value)
    {
        if (value is not MediaFrame frame) return value;

        var fullSnapshot = !_hasCover;
        var styleChanged = fullSnapshot ||
            (frame.style is not null && frame.style != _lastStyle);
        var coverChanged = fullSnapshot ||
            frame.cover is not null && frame.cover != _lastCover;
        if (styleChanged) _lastStyle = frame.style;
        if (coverChanged && frame.cover is not null)
        {
            _lastCover = frame.cover;
            _hasCover = true;
        }

        // The queue retains complete frames. Only an established connection may
        // reuse its last style/cover values.
        return frame.WithConnectionDelta(
            styleChanged ? frame.style : null,
            coverChanged ? frame.cover! : null);
    }
}

internal sealed class LatestDisplayQueue
{
    private readonly object _gate = new();
    private object? _latest;
    private long _published;

    internal long PublishedCount => Interlocked.Read(ref _published);

    internal void Publish(object value)
    {
        lock (_gate) _latest = value;
        Interlocked.Increment(ref _published);
    }

    internal bool TryTake(out object? value)
    {
        lock (_gate)
        {
            value = _latest;
            _latest = null;
            return value is not null;
        }
    }

    internal void Clear()
    {
        lock (_gate) _latest = null;
    }
}
