using System.Globalization;
using System.Text.Json;
using TaskbarLyrics.App;
using TaskBarWidgets.Media.Updates;

namespace TaskBarWidgets.Media.Integration;

// Builds the complete card snapshot. Each part carries its own revision so the
// native front-end can ignore an older part without dropping a newer one: a new
// lyric frame can never evict the audio state.
internal sealed class CardSnapshotBuilder
{
    private long _trafficRevision;
    private long _weatherRevision;
    private long _audioRevision;
    private object? _lastTraffic;
    private object? _lastWeather;

    internal long TrafficRevision => Interlocked.Read(ref _trafficRevision);
    internal long WeatherRevision => Interlocked.Read(ref _weatherRevision);
    internal long AudioRevision => Interlocked.Read(ref _audioRevision);

    internal void SetTraffic(JsonElement payload)
    {
        _lastTraffic = DescribeTraffic(payload);
        Interlocked.Increment(ref _trafficRevision);
    }

    internal void SetWeather(JsonElement payload)
    {
        _lastWeather = DescribeWeather(payload);
        Interlocked.Increment(ref _weatherRevision);
    }

    internal void BumpAudio() => Interlocked.Increment(ref _audioRevision);

    internal object Build(AppSettings settings, MediaCoordinator media, AudioSnapshot? audio,
        UpdateSnapshot? update, bool connected, int theme)
    {
        // Quick cards: the exact persisted configuration, never re-derived here.
        var quickCards = new
        {
            order = settings.QuickCardOrder,
            columns = settings.QuickCardColumns,
            enabled = settings.QuickCardEnabled,
            resourceMode = settings.QuickCardResourceMode.ToString(),
            font = settings.CardFontFamily,
            revision = 1,
            mica = new
            {
                vivid = settings.CardMicaVivid,
                tintOpacity = settings.CardMicaTintOpacity,
                luminosityOpacity = settings.CardMicaLuminosityOpacity,
                darkTintOpacity = settings.CardMicaDarkTintOpacity,
                darkLuminosityOpacity = settings.CardMicaDarkLuminosityOpacity,
            },
        };

        var mediaState = DescribeMedia(media);
        var lyrics = DescribeLyrics(media.CardFrame);
        var audioState = DescribeAudio(audio);
        return new
        {
            statusText = connected ? "" : "媒体服务正在连接…",
            connected,
            toolWindowTheme = theme,
            quickCards,
            media = mediaState,
            lyrics,
            audio = audioState,
            traffic = _lastTraffic,
            weather = _lastWeather,
            update = DescribeUpdate(update),
        };
    }

    internal object DescribeAudio(AudioSnapshot? snapshot, bool operationSucceeded = true, string? message = null)
    {
        static object Endpoint(AudioEndpoint item) => new
        {
            id = item.Id, name = item.Name, isDefault = item.IsDefault,
            isCommunications = item.IsCommunications, volume = item.Volume / 100.0,
            isMuted = item.IsMuted, canControl = item.CanControl,
        };
        return new
        {
            revision = Interlocked.Read(ref _audioRevision),
            outputs = snapshot?.Outputs.Select(Endpoint) ?? [],
            inputs = snapshot?.Inputs.Select(Endpoint) ?? [],
            operationSucceeded,
            message,
        };
    }

    internal object DescribeMedia(MediaCoordinator media)
    {
        var state = media.ControlState;
        return new
        {
            revision = media.CardFrame?.revision ?? 0,
            title = state.Title,
            artist = state.Artist,
            source = state.Source,
            positionSeconds = state.PositionSeconds,
            durationSeconds = state.DurationSeconds,
            isPlaying = state.IsPlaying,
            canPrevious = state.CanPrevious,
            canNext = state.CanNext,
            canPlayPause = state.CanPlayPause,
            canSeek = state.CanSeek,
            coverDataUrl = media.CoverDataUrl,
            sources = (state.Sources ?? []).Select(source => new
            {
                source.Id,
                label = Label(source),
                iconDataUrl = media.SourceIcon(source),
            }),
            selectedSourceId = state.SelectedSourceId,
        };
    }

    private static string Label(MediaSourceChoice source) => source.Name switch
    {
        "QQMusic" => "QQ 音乐",
        "Netease" => "网易云音乐",
        "Kugou" => "酷狗音乐",
        "Douyin" => "抖音",
        "Browser" => source.Id.Contains("msedge", StringComparison.OrdinalIgnoreCase) ? "Microsoft Edge"
            : source.Id.Contains("chrome", StringComparison.OrdinalIgnoreCase) ? "Google Chrome"
            : source.Id.Contains("firefox", StringComparison.OrdinalIgnoreCase) ? "Firefox" : "浏览器",
        _ => source.Name,
    };

    // The card front-end consumes the same renderVersion 2 frame the WinUI card
    // did; it never re-queries or re-matches lyrics.
    internal object DescribeLyrics(CardFrame? frame)
    {
        if (frame is null) return new { revision = -1, scene = "noPlayback" };
        var lyrics = frame.lyrics;
        var style = frame.style;
        return new
        {
            renderVersion = frame.renderVersion,
            revision = frame.revision,
            trackId = frame.trackId,
            available = frame.available,
            isPlaying = frame.isPlaying,
            current = lyrics.current,
            next = lyrics.next,
            progress = lyrics.progress,
            currentLineIndex = lyrics.currentLineIndex,
            isPureMusic = lyrics.isPureMusic,
            currentTranslation = lyrics.currentTranslation,
            nextTranslation = lyrics.nextTranslation,
            translationMode = lyrics.translationMode,
            animateTransition = lyrics.animateTransition,
            wordScanProgress = lyrics.wordScanProgress,
            scene = lyrics.scene,
            style = new
            {
                fontFamily = style.fontFamily,
                layoutScalePercent = style.layoutScalePercent,
                fontSize = style.fontSize,
                fontWeight = style.fontWeight,
                primaryColor = style.primaryColor,
                secondaryColor = style.secondaryColor,
                translationColor = style.translationColor,
                wordScanOverlayColor = style.wordScanOverlayColor,
                textShadow = style.textShadow,
                textAlignment = style.textAlignment,
            },
        };
    }

    internal object DescribeUpdate(UpdateSnapshot? snapshot) => new
    {
        revision = snapshot?.Revision ?? 0,
        phase = snapshot?.Phase ?? "idle",
        currentVersion = snapshot?.CurrentVersion ?? "",
        latestVersion = snapshot?.LatestVersion ?? "",
        message = snapshot?.Message ?? "",
        error = snapshot?.Error ?? "",
        bytesDownloaded = snapshot?.BytesDownloaded ?? 0,
        totalBytes = snapshot?.TotalBytes ?? 0,
        canDownload = snapshot?.CanDownload ?? false,
        canInstall = snapshot?.CanInstall ?? false,
    };

    // The traffic display model stays in Media: 84 days, relative to the twelve
    // week peak, with future, unknown, recorded zero and positive kept distinct.
    internal object DescribeTraffic(JsonElement payload)
    {
        TrafficHeatmap heatmap;
        try { heatmap = TrafficHeatmap.Parse(payload); }
        catch (FormatException)
        {
            return new { revision = TrafficRevision, available = false, days = Array.Empty<object>() };
        }
        var days = heatmap.Days.Select(day => new
        {
            week = day.Week,
            weekday = day.Weekday,
            date = day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            isFuture = day.IsFuture,
            known = day.TotalKiB is not null,
            isToday = day.Date == heatmap.Today,
            totalKiB = day.TotalKiB ?? 0,
            level = heatmap.LevelFor(day),
        }).ToArray();
        return new
        {
            revision = TrafficRevision,
            today = heatmap.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            scopeLabel = heatmap.ScopeLabel,
            totalKiB = heatmap.TotalKiB,
            peakKiB = heatmap.PeakKiB,
            available = true,
            days,
        };
    }

    // Weather keeps its existing native payload; only the shape the card reads is
    // added here so the front-end never parses provider-specific fields.
    internal object DescribeWeather(JsonElement payload)
    {
        var details = payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("details", out var value) && value.ValueKind == JsonValueKind.Object
            ? value : default;
        var current = details.ValueKind == JsonValueKind.Object &&
            details.TryGetProperty("current", out var item) && item.ValueKind == JsonValueKind.Object
            ? item : default;
        var city = payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("city", out var cityValue) && cityValue.ValueKind == JsonValueKind.Object
            ? cityValue : default;
        return new
        {
            revision = WeatherRevision,
            provider = Text(payload, "provider"),
            city = Text(city, "name"),
            region = Text(city, "region"),
            state = Text(payload, "state"),
            message = Text(payload, "message"),
            temperature = Text(current, "temperature"),
            feelsLike = Text(current, "feelsLike"),
            weather = Text(current, "weather"),
            conditionKind = Text(current, "conditionKind"),
            humidity = Text(current, "humidity"),
            observedAt = Text(current, "observedAt"),
            stale = Boolean(payload, "stale"),
            updating = Boolean(payload, "updating"),
            hasData = current.ValueKind == JsonValueKind.Object,
            details,
        };
    }

    private static string Text(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : "";

    private static bool Boolean(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False && property.GetBoolean();
}
