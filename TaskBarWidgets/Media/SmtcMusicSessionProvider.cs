using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TaskbarLyrics.Core.Abstractions;
using TaskbarLyrics.Core.Models;
using TaskbarLyrics.Core.Utilities;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace TaskbarLyrics.App;

internal static class CoverIdentity
{
    public static string FromTrack(TrackInfo? track)
    {
        return track is null
            ? string.Empty
            : FromMetadata(track.SourceApp, track.SongId, track.Title, track.Artist, track.Album);
    }

    public static string FromMetadata(
        string? sourceApp,
        string? songId,
        string? title,
        string? artist,
        string? album)
    {
        var normalizedSource = Normalize(sourceApp);
        var normalizedSongId = Normalize(songId);
        return !string.IsNullOrEmpty(normalizedSongId)
            ? Compose("song", normalizedSource, normalizedSongId)
            : Compose(
                "metadata",
                normalizedSource,
                Normalize(title),
                Normalize(artist),
                Normalize(album));
    }

    private static string Compose(string scope, params string[] parts)
    {
        var builder = new StringBuilder(scope);
        foreach (var part in parts)
        {
            builder.Append('|').Append(part.Length).Append(':').Append(part);
        }

        return builder.ToString();
    }

    private static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}

internal sealed class CoverVisualTransitionState
{
    internal static readonly TimeSpan DefaultRetention = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Players can report a new track several hundred milliseconds before publishing its artwork.
    /// </summary>
    internal static readonly TimeSpan DefaultFallbackSettleDelay = TimeSpan.FromMilliseconds(800);

    private readonly TimeSpan _retention;
    private readonly TimeSpan _fallbackSettleDelay;
    private readonly Func<DateTimeOffset> _utcNow;
    private DateTimeOffset _transitionDeadlineUtc;
    private DateTimeOffset _fallbackSettleDeadlineUtc;

    internal CoverVisualTransitionState()
        : this(DefaultRetention, DefaultFallbackSettleDelay, () => DateTimeOffset.UtcNow)
    {
    }

    internal CoverVisualTransitionState(TimeSpan retention, Func<DateTimeOffset> utcNow)
        : this(retention, DefaultFallbackSettleDelay, utcNow)
    {
    }

    internal CoverVisualTransitionState(
        TimeSpan retention,
        TimeSpan fallbackSettleDelay,
        Func<DateTimeOffset> utcNow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(fallbackSettleDelay, TimeSpan.Zero);

        _retention = retention;
        _fallbackSettleDelay = fallbackSettleDelay;
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    internal string RequestedIdentity { get; private set; } = string.Empty;

    internal string VisualIdentity { get; private set; } = string.Empty;

    internal bool Begin(string? identity)
    {
        var normalizedIdentity = (identity ?? string.Empty).Trim();
        if (string.Equals(RequestedIdentity, normalizedIdentity, StringComparison.Ordinal))
        {
            return false;
        }

        RequestedIdentity = normalizedIdentity;
        var now = _utcNow();
        _transitionDeadlineUtc = string.IsNullOrEmpty(normalizedIdentity)
            ? now
            : now + _retention;
        _fallbackSettleDeadlineUtc = string.IsNullOrEmpty(normalizedIdentity)
            ? now
            : now + _fallbackSettleDelay;
        return true;
    }

    internal bool IsVisualFor(string? identity)
    {
        return string.Equals(VisualIdentity, identity ?? string.Empty, StringComparison.Ordinal);
    }

    internal bool ShouldRetainPreviousVisual()
    {
        return !string.IsNullOrEmpty(RequestedIdentity) &&
               !IsVisualFor(RequestedIdentity) &&
               _utcNow() < _transitionDeadlineUtc;
    }

    /// <summary>
    /// True right after a track change while its artwork may still be arriving. The caller holds the
    /// previous visual instead of switching to the fallback icon, so a late cover cannot flash the
    /// fallback in and immediately replace it.
    /// </summary>
    internal bool ShouldDebounceFallback()
    {
        return VisualIdentity.Length > 0 &&
               !string.IsNullOrEmpty(RequestedIdentity) &&
               !IsVisualFor(RequestedIdentity) &&
               _utcNow() < _fallbackSettleDeadlineUtc;
    }

    internal void MarkVisual(string? identity)
    {
        VisualIdentity = (identity ?? string.Empty).Trim();
    }
}

internal sealed class ProcessFallbackDetectionCache
{
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private readonly Func<DateTimeOffset> _utcNow;
    private DateTimeOffset _nextRefreshUtc;
    private string? _cachedSource;
    private bool _hasCachedResult;

    internal ProcessFallbackDetectionCache(Func<DateTimeOffset>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal string? GetOrRefresh(Func<string?> detect)
    {
        ArgumentNullException.ThrowIfNull(detect);

        var nowUtc = _utcNow();
        if (_hasCachedResult && nowUtc < _nextRefreshUtc)
        {
            return _cachedSource;
        }

        _cachedSource = detect();
        _hasCachedResult = true;
        _nextRefreshUtc = nowUtc + RefreshInterval;
        return _cachedSource;
    }

    internal void Invalidate()
    {
        _cachedSource = null;
        _hasCachedResult = false;
        _nextRefreshUtc = default;
    }
}

public sealed class SmtcMusicSessionProvider : IMusicSessionProvider, IMediaPlaybackController, IPlayerRecognitionController, IDisposable
{
    private string? _selectedSourceId;
    private readonly Func<long> _tickCount;

    public SmtcMusicSessionProvider() : this(() => Environment.TickCount64) { }
    internal SmtcMusicSessionProvider(Func<long> tickCount) => _tickCount = tickCount;

    internal void SelectSource(string sourceId)
    {
        _selectedSourceId = string.IsNullOrWhiteSpace(sourceId) ? null : sourceId;
        ResetPlaybackTransition();
    }

    internal IReadOnlyList<TaskBarWidgets.Media.Integration.MediaSourceChoice> GetSourceChoices() =>
        _manager?.GetSessions().Select(session => new TaskBarWidgets.Media.Integration.MediaSourceChoice(
            session.SourceAppUserModelId, NonLyricsMediaPolicy.IsDouyin(session.SourceAppUserModelId)
                ? "抖音" : ResolveSource(session.SourceAppUserModelId)))
            .DistinctBy(source => source.Id).ToArray() ?? [];

    internal (bool Previous, bool Next, bool PlayPause) GetControlCapabilities()
    {
        var controls = _manager is null ? null : SelectSessionForControl(_manager)?.GetPlaybackInfo()?.Controls;
        return (controls?.IsPreviousEnabled == true, controls?.IsNextEnabled == true,
            controls?.IsPlayEnabled == true || controls?.IsPauseEnabled == true || controls?.IsPlayPauseToggleEnabled == true);
    }
    private static readonly string[] DefaultEnabledSources = { "QQMusic", "Netease", "Kugou", "Spotify" };
    private static readonly string[] DefaultRecognitionOrder = { "QQMusic", "Netease", "Kugou", "Spotify", "Browser" };
    private static readonly TimeSpan MissingCoverRetryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CoverReadTimeout = TimeSpan.FromSeconds(5);
    private static readonly Regex TitleArtistRegex = new(
        @"^(?<title>.+?)\s*[-|—]\s*(?<artist>.+)$",
        RegexOptions.Compiled);

    private readonly SemaphoreSlim _managerLock = new(1, 1);
    private readonly TimelinePositionStrategyRegistry _timelineStrategyRegistry = TimelinePositionStrategyRegistry.CreateDefault();
    private readonly ActiveSessionCache<GlobalSystemMediaTransportControlsSession> _activeSessionCache = new();
    private readonly DouyinPlaybackGate _douyinPlayback = new();
    private PlaybackSnapshot? _lastRawSnapshot;
    private int _playbackRevision;
    private readonly ProcessFallbackDetectionCache _processFallbackDetectionCache = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private SmtcTimelineDiagnostics? _lastTimelineDiagnostics;
    private string? _currentLyricSourceApp;
    private int _currentPlayerLyricOffsetMilliseconds;
    private int _currentTrackLyricOffsetMilliseconds;
    private string[] _recognitionOrder = DefaultRecognitionOrder;
    private HashSet<string> _enabledSources = new(DefaultEnabledSources, StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _configuredCustomSources = new(StringComparer.OrdinalIgnoreCase);
    private string _lastCoverMetadataKey = string.Empty;
    private string _lastQqMetadataDiagnosticsKey = string.Empty;
    private byte[]? _lastCoverImageBytes;
    private DateTimeOffset _nextMissingCoverRetryUtc;
    private readonly object _coverLock = new();
    private Task? _coverReadTask;
    private string _coverReadMetadataKey = string.Empty;
    private int _coverReadSequence;
    private string _lastCoverThumbnailDiagnosticsKey = string.Empty;
    private string _lastMediaPropertiesErrorKey = string.Empty;
    private DateTimeOffset _nextMediaPropertiesErrorLogUtc;
    private string _lastPlaybackStateRefreshErrorKey = string.Empty;
    private DateTimeOffset _nextPlaybackStateRefreshErrorLogUtc;
    private string _lastSessionReadErrorKey = string.Empty;
    private DateTimeOffset _nextSessionReadErrorLogUtc;
    private int _isDisposed;

    public void SetRecognitionOrder(
        IReadOnlyList<string>? order,
        IReadOnlyCollection<string>? enabledSources = null,
        IReadOnlyCollection<string>? customSources = null)
    {
        _enabledSources = enabledSources is null
            ? new HashSet<string>(DefaultEnabledSources, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(enabledSources, StringComparer.OrdinalIgnoreCase);
        _configuredCustomSources = customSources is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(customSources, StringComparer.OrdinalIgnoreCase);
        var normalized = NormalizeRecognitionOrder(order, _enabledSources);
        _recognitionOrder = normalized.ToArray();
        ResetPlaybackTransition();
        _processFallbackDetectionCache.Invalidate();
    }

    private void ResetPlaybackTransition()
    {
        _douyinPlayback.Reset();
        _lastRawSnapshot = new(false, TimeSpan.Zero, null);
        _activeSessionCache.Clear();
        Interlocked.Increment(ref _playbackRevision);
    }

    internal PlaybackSnapshot CommitPlaybackSnapshot(PlaybackSnapshot raw)
    {
        _lastRawSnapshot = raw;
        return _douyinPlayback.Observe(raw, _tickCount());
    }

    public SmtcTimelineDiagnostics? GetLastTimelineDiagnostics()
    {
        return _lastTimelineDiagnostics;
    }

    internal PlaybackSnapshot RefreshPlaybackState(PlaybackSnapshot snapshot)
    {
        // The incoming snapshot may be retained Douyin output while the raw session is QQ or absent.
        // Reobserve raw input so a refresh cannot cancel or restart the loss deadline.
        var raw = _lastRawSnapshot ?? snapshot;
        var session = _activeSessionCache.Current;
        if (session is null)
        {
            return CommitPlaybackSnapshot(raw);
        }

        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            var isPlaying = playbackInfo?.PlaybackStatus ==
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            return CommitPlaybackSnapshot(ApplyLatestPlaybackState(raw, isPlaying));
        }
        catch (Exception exception)
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var errorKey = $"{exception.GetType().FullName}|{exception.HResult:X8}";
            if (!string.Equals(
                    errorKey,
                    _lastPlaybackStateRefreshErrorKey,
                    StringComparison.Ordinal) ||
                nowUtc >= _nextPlaybackStateRefreshErrorLogUtc)
            {
                _lastPlaybackStateRefreshErrorKey = errorKey;
                _nextPlaybackStateRefreshErrorLogUtc = nowUtc.AddSeconds(30);
                Log.Diagnostic(
                    "SMTC",
                    $"PlaybackStateRefreshFailed Exception='{exception.GetType().Name}' " +
                    $"HResult=0x{exception.HResult:X8}");
            }

            return CommitPlaybackSnapshot(raw);
        }
    }

    internal static PlaybackSnapshot ApplyLatestPlaybackState(
        PlaybackSnapshot snapshot,
        bool isPlaying)
    {
        return snapshot.IsPlaying == isPlaying
            ? snapshot
            : snapshot with { IsPlaying = isPlaying };
    }

    public void SetCurrentLyricSource(string? sourceApp)
    {
        _currentLyricSourceApp = sourceApp;
    }

    public string GetCurrentLyricSource()
    {
        return _currentLyricSourceApp ?? string.Empty;
    }

    public void SetCurrentLyricOffsets(int playerOffsetMilliseconds, int trackOffsetMilliseconds)
    {
        Volatile.Write(ref _currentPlayerLyricOffsetMilliseconds, playerOffsetMilliseconds);
        Volatile.Write(ref _currentTrackLyricOffsetMilliseconds, trackOffsetMilliseconds);
    }

    public LyricOffsetDiagnostics GetCurrentLyricOffsets()
    {
        var playerOffset = Volatile.Read(ref _currentPlayerLyricOffsetMilliseconds);
        var trackOffset = Volatile.Read(ref _currentTrackLyricOffsetMilliseconds);
        return new LyricOffsetDiagnostics(playerOffset, trackOffset, playerOffset + trackOffset);
    }

    public async Task TryControlAsync(MediaControlAction action, CancellationToken cancellationToken)
    {
        try
        {
            var manager = await GetManagerAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var session = manager is null ? null : SelectSessionForControl(manager);
            if (session is null)
            {
                throw new InvalidOperationException("媒体来源已关闭。");
            }

            switch (action)
            {
                case MediaControlAction.TogglePlayPause:
                    var playback = session.GetPlaybackInfo();
                    cancellationToken.ThrowIfCancellationRequested();
                    var displayedDouyin = _douyinPlayback.ControlSourceId is { } controlId &&
                        string.Equals(controlId, session.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase);
                    // During a transition the displayed pause button must not resume a temporarily paused video.
                    if (displayedDouyin ? _douyinPlayback.Current!.IsPlaying :
                        playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        EnsureCommandAccepted(await session.TryPauseAsync());
                        if (displayedDouyin) ConfirmDouyinPlayback(session, false);
                    }
                    else
                    {
                        EnsureCommandAccepted(await session.TryPlayAsync());
                        if (displayedDouyin) ConfirmDouyinPlayback(session, true);
                    }
                    break;
                case MediaControlAction.PreviousTrack:
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureCommandAccepted(await session.TrySkipPreviousAsync());
                    break;
                case MediaControlAction.NextTrack:
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureCommandAccepted(await session.TrySkipNextAsync());
                    break;
                case MediaControlAction.SeekBackward:
                    await TrySeekAsync(session, TimeSpan.FromSeconds(-5), cancellationToken);
                    break;
                case MediaControlAction.SeekForward:
                    await TrySeekAsync(session, TimeSpan.FromSeconds(5), cancellationToken);
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log.Warn($"SMTC media hotkey command failed: {exception}");
            throw;
        }
    }

    public Task ExecuteAsync(MediaControlAction action, CancellationToken cancellationToken) =>
        TryControlAsync(action, cancellationToken);

    private void ConfirmDouyinPlayback(GlobalSystemMediaTransportControlsSession session, bool isPlaying)
    {
        if (!string.Equals(_douyinPlayback.ControlSourceId, session.SourceAppUserModelId,
            StringComparison.OrdinalIgnoreCase)) return; // Selection may have changed while awaiting the command.
        _douyinPlayback.ConfirmPlayback(isPlaying, _tickCount());
        _lastRawSnapshot = _douyinPlayback.Current;
        _activeSessionCache.Remember(session);
        Interlocked.Increment(ref _playbackRevision);
    }

    public async Task SeekToAsync(TimeSpan position, CancellationToken cancellationToken)
    {
        try
        {
            var manager = await GetManagerAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var session = manager is null ? null : SelectSessionForControl(manager);
            if (session?.GetPlaybackInfo()?.Controls?.IsPlaybackPositionEnabled != true)
            {
                throw new InvalidOperationException("当前媒体来源不支持调整进度。");
            }

            var timeline = session.GetTimelineProperties();
            if (timeline.EndTime <= timeline.StartTime)
            {
                throw new InvalidOperationException("当前媒体没有可用的进度范围。");
            }

            var target = TimeSpan.FromTicks(Math.Clamp(position.Ticks, timeline.StartTime.Ticks, timeline.EndTime.Ticks));
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCommandAccepted(await session.TryChangePlaybackPositionAsync(target.Ticks));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log.Warn($"SMTC seek command failed: {exception}");
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        _activeSessionCache.Clear();
        _manager = null;
        _managerLock.Dispose();
        GC.SuppressFinalize(this);
    }

    internal static void EnsureCommandAccepted(bool accepted)
    {
        if (!accepted) throw new InvalidOperationException("播放器未接受此操作，请检查播放源是否仍可用。");
    }

    private static async Task TrySeekAsync(
        GlobalSystemMediaTransportControlsSession session,
        TimeSpan offset,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var timeline = session.GetTimelineProperties();
        var playback = session.GetPlaybackInfo();
        if (playback?.Controls?.IsPlaybackPositionEnabled != true)
            throw new InvalidOperationException("当前媒体来源不支持调整进度。");
        var current = ComputeExtrapolatedPosition(
            timeline,
            playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            DateTimeOffset.UtcNow,
            out _);
        var target = current + offset;
        if (target < timeline.StartTime)
        {
            target = timeline.StartTime;
        }
        else if (timeline.EndTime > timeline.StartTime && target > timeline.EndTime)
        {
            target = timeline.EndTime;
        }

        cancellationToken.ThrowIfCancellationRequested();
        EnsureCommandAccepted(await session.TryChangePlaybackPositionAsync(target.Ticks));
    }

    public async Task<PlaybackSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        PlaybackSnapshot raw;
        int revision;
        do
        {
            revision = Volatile.Read(ref _playbackRevision);
            raw = await SmtcSessionReadBoundary.ReadAsync(
                ReadCurrentSnapshotAsync,
                () =>
                {
                    _activeSessionCache.Clear();
                    _manager = null;
                    return BuildProcessFallbackSnapshot();
                },
                LogSessionReadUnavailable,
                cancellationToken);
            // Explicit selection/pause wins over metadata that was already being read.
        } while (revision != Volatile.Read(ref _playbackRevision));
        return CommitPlaybackSnapshot(raw);
    }

    private void LogSessionReadUnavailable(Exception? exception)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var errorKey = exception is null ? "missing-timeline" : $"{exception.GetType().Name}|{exception.HResult:X8}";
        if (_lastSessionReadErrorKey == errorKey && nowUtc < _nextSessionReadErrorLogUtc) return;
        _lastSessionReadErrorKey = errorKey;
        _nextSessionReadErrorLogUtc = nowUtc.AddSeconds(30);
        Log.Diagnostic("SMTC", $"SessionReadUnavailable Reason='{errorKey}' Action='process-fallback'.");
    }

    private async Task<PlaybackSnapshot?> ReadCurrentSnapshotAsync(CancellationToken cancellationToken)
    {
        var manager = await GetManagerAsync(cancellationToken);
        if (manager is null)
        {
            _activeSessionCache.Clear();
            return BuildProcessFallbackSnapshot();
        }

        var session = SelectSession(manager);
        _activeSessionCache.Remember(session);
        if (session is null)
        {
            return BuildProcessFallbackSnapshot();
        }

        var playbackInfo = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();
        if (timeline is null) return null;
        var nowUtc = DateTimeOffset.UtcNow;

        var sourceAppUserModelId = session.SourceAppUserModelId ?? string.Empty;
        var rawSource = ResolveSource(sourceAppUserModelId);
        var sourceApp = ResolveSourceWithProcessFallback(rawSource);

        var isPlaying = playbackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var position = timeline.Position;
        var extrapolatedPosition = ComputeExtrapolatedPosition(timeline, isPlaying, nowUtc, out var lastUpdateAge);

        string title = string.Empty;
        string artist = string.Empty;
        string album = string.Empty;
        byte[]? coverImageBytes = null;
        bool isCoverLoading = false;
        string? songId = null;

        try
        {
            var media = await session.TryGetMediaPropertiesAsync().AsTask(cancellationToken);
            title = media?.Title?.Trim() ?? string.Empty;
            artist = media?.Artist?.Trim() ?? string.Empty;
            album = media?.AlbumTitle?.Trim() ?? string.Empty;

            if (media?.Genres != null)
            {
                if (sourceApp.Equals("Netease", StringComparison.OrdinalIgnoreCase))
                {
                    songId = media.Genres
                        .FirstOrDefault(x => x.StartsWith("NCM-", StringComparison.OrdinalIgnoreCase))?
                        .Replace("NCM-", "");
                }
                else if (sourceApp.Equals("QQMusic", StringComparison.OrdinalIgnoreCase))
                {
                    songId = media.Genres
                        .FirstOrDefault(x => x.StartsWith("QQ-", StringComparison.OrdinalIgnoreCase))?
                        .Replace("QQ-", "");
                }
            }

            (coverImageBytes, isCoverLoading) = GetCoverBytes(
                sourceApp,
                title,
                artist,
                album,
                songId,
                media?.Thumbnail,
                nowUtc);

            // 从 Genres 流派元数据列表中解析播放器写入的 SongId (例如网易云 NCM-, QQ音乐 QQ-)
            if (media?.Genres != null && sourceApp.Equals("QQMusic", StringComparison.OrdinalIgnoreCase))
            {
                var genresText = string.Join(" | ", media.Genres);
                var diagnosticsKey = $"{title}|{artist}|{album}|{songId}|{genresText}";
                if (!string.Equals(_lastQqMetadataDiagnosticsKey, diagnosticsKey, StringComparison.Ordinal))
                {
                    _lastQqMetadataDiagnosticsKey = diagnosticsKey;
                    Log.Debug($"SMTC QQMusic metadata: Album='{album}', SongId='{songId ?? string.Empty}', Genres='{genresText}'");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Keep fallback chain alive.
            var errorKey = $"{sourceApp}|{ex.GetType().FullName}|{ex.HResult:X8}|{ex.Message}";
            if (!string.Equals(errorKey, _lastMediaPropertiesErrorKey, StringComparison.Ordinal) ||
                nowUtc >= _nextMediaPropertiesErrorLogUtc)
            {
                _lastMediaPropertiesErrorKey = errorKey;
                _nextMediaPropertiesErrorLogUtc = nowUtc.AddSeconds(30);
                Log.Diagnostic(
                    "COVER-SMTC",
                    $"MediaPropertiesFailed Source='{ToLogValue(sourceApp)}' Exception='{ex.GetType().Name}' " +
                    $"HResult=0x{ex.HResult:X8} Message='{ToLogValue(ex.Message)}'");
            }
        }

        if (sourceApp.Equals("Netease", StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(title) || string.Equals(title, "Unknown Title", StringComparison.OrdinalIgnoreCase)))
        {
            if (TryInferTrackFromNeteaseProcess(out var inferredTitle, out var inferredArtist))
            {
                title = inferredTitle;
                artist = inferredArtist;
            }
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Unknown Title";
        }

        if (string.IsNullOrWhiteSpace(artist))
        {
            artist = "Unknown Artist";
        }

        var trackId = $"{sourceApp}|{title}|{artist}";
        var track = new TrackInfo(trackId, title, artist, album, sourceApp, timeline.EndTime, songId);
        var diagnostics = new SmtcTimelineDiagnostics(
            CapturedAtUtc: nowUtc,
            SourceAppUserModelId: sourceAppUserModelId,
            NormalizedSource: rawSource,
            ResolvedSource: sourceApp,
            IsPlaying: isPlaying,
            RawPosition: position,
            LastUpdatedTimeUtc: timeline.LastUpdatedTime,
            LastUpdateAge: lastUpdateAge,
            ExtrapolatedPosition: extrapolatedPosition,
            SelectedPosition: position,
            StrategyName: string.Empty,
            Title: title,
            Artist: artist,
            IsFallbackSnapshot: false);
        var selection = _timelineStrategyRegistry.Select(diagnostics);
        diagnostics = diagnostics with
        {
            SelectedPosition = selection.Position,
            StrategyName = selection.StrategyName
        };
        PublishDiagnostics(diagnostics);
        return new PlaybackSnapshot(
            IsPlaying: isPlaying,
            Position: selection.Position,
            Track: track,
            CoverImageBytes: coverImageBytes,
            RawPosition: position,
            ExtrapolatedPosition: extrapolatedPosition,
            IsCoverLoading: isCoverLoading,
            SourceAppUserModelId: sourceAppUserModelId,
            CanSeek: playbackInfo?.Controls?.IsPlaybackPositionEnabled == true);
    }

    private PlaybackSnapshot BuildProcessFallbackSnapshot()
    {
        var preferredSource = DetectRunningSource();
        if (string.Equals(preferredSource, "Netease", StringComparison.OrdinalIgnoreCase))
        {
            if (TryInferTrackFromNeteaseProcess(out var inferredTitle, out var inferredArtist))
            {
                PublishFallbackDiagnostics(
                    sourceAppUserModelId: string.Empty,
                    resolvedSource: "Netease",
                    title: inferredTitle,
                    artist: inferredArtist);
                return new PlaybackSnapshot(
                    IsPlaying: false,
                    Position: TimeSpan.Zero,
                    Track: new TrackInfo(
                        Id: $"Netease|{inferredTitle}|{inferredArtist}",
                        Title: inferredTitle,
                        Artist: inferredArtist,
                        Album: string.Empty,
                        SourceApp: "Netease",
                        Duration: TimeSpan.Zero),
                    CoverImageBytes: null);
            }

            PublishFallbackDiagnostics(
                sourceAppUserModelId: string.Empty,
                resolvedSource: "Netease",
                title: "Unknown Title",
                artist: "Unknown Artist");
            return new PlaybackSnapshot(
                IsPlaying: false,
                Position: TimeSpan.Zero,
                Track: new TrackInfo(
                    Id: "Netease|ProcessFallback",
                    Title: "Unknown Title",
                    Artist: "Unknown Artist",
                    Album: string.Empty,
                    SourceApp: "Netease",
                    Duration: TimeSpan.Zero),
                CoverImageBytes: null);
        }

        if (string.Equals(preferredSource, "QQMusic", StringComparison.OrdinalIgnoreCase))
        {
            PublishFallbackDiagnostics(
                sourceAppUserModelId: string.Empty,
                resolvedSource: "QQMusic",
                title: "Unknown Title",
                artist: "Unknown Artist");
            return new PlaybackSnapshot(
                IsPlaying: false,
                Position: TimeSpan.Zero,
                Track: new TrackInfo(
                    Id: "QQMusic|ProcessFallback",
                    Title: "Unknown Title",
                    Artist: "Unknown Artist",
                    Album: string.Empty,
                    SourceApp: "QQMusic",
                    Duration: TimeSpan.Zero),
                CoverImageBytes: null);
        }

        if (string.Equals(preferredSource, "Kugou", StringComparison.OrdinalIgnoreCase))
        {
            PublishFallbackDiagnostics(
                sourceAppUserModelId: string.Empty,
                resolvedSource: "Kugou",
                title: "Unknown Title",
                artist: "Unknown Artist");
            return new PlaybackSnapshot(
                IsPlaying: false,
                Position: TimeSpan.Zero,
                Track: new TrackInfo(
                    Id: "Kugou|ProcessFallback",
                    Title: "Unknown Title",
                    Artist: "Unknown Artist",
                    Album: string.Empty,
                    SourceApp: "Kugou",
                    Duration: TimeSpan.Zero),
                CoverImageBytes: null);
        }

        PublishFallbackDiagnostics(string.Empty, preferredSource ?? string.Empty, string.Empty, string.Empty);
        return new PlaybackSnapshot(false, TimeSpan.Zero, null);
    }

    private static TimeSpan ComputeExtrapolatedPosition(
        GlobalSystemMediaTransportControlsSessionTimelineProperties timeline,
        bool isPlaying,
        DateTimeOffset nowUtc,
        out TimeSpan lastUpdateAge)
    {
        lastUpdateAge = nowUtc - timeline.LastUpdatedTime;
        if (lastUpdateAge < TimeSpan.Zero)
        {
            lastUpdateAge = TimeSpan.Zero;
        }

        if (!isPlaying)
        {
            return timeline.Position;
        }

        return timeline.Position + lastUpdateAge;
    }

    private void PublishDiagnostics(SmtcTimelineDiagnostics diagnostics)
    {
        _lastTimelineDiagnostics = diagnostics;
    }

    private void PublishFallbackDiagnostics(
        string sourceAppUserModelId,
        string resolvedSource,
        string title,
        string artist)
    {
        PublishDiagnostics(new SmtcTimelineDiagnostics(
            CapturedAtUtc: DateTimeOffset.UtcNow,
            SourceAppUserModelId: sourceAppUserModelId,
            NormalizedSource: string.Empty,
            ResolvedSource: resolvedSource,
            IsPlaying: false,
            RawPosition: TimeSpan.Zero,
            LastUpdatedTimeUtc: DateTimeOffset.UtcNow,
            LastUpdateAge: TimeSpan.Zero,
            ExtrapolatedPosition: TimeSpan.Zero,
            SelectedPosition: TimeSpan.Zero,
            StrategyName: "Fallback",
            Title: title,
            Artist: artist,
            IsFallbackSnapshot: true));
    }

    private GlobalSystemMediaTransportControlsSession? SelectSession(
        GlobalSystemMediaTransportControlsSessionManager manager)
    {
        return SelectSession(manager.GetSessions());
    }

    private GlobalSystemMediaTransportControlsSession? SelectSessionForControl(
        GlobalSystemMediaTransportControlsSessionManager manager)
    {
        var sessions = manager.GetSessions();
        // Never send a retained Douyin display's commands to a paused fallback player.
        // Look up by source ID because SMTC may recreate the session object between videos.
        if (_douyinPlayback.ControlSourceId is not null)
            return _douyinPlayback.FindControlSession(sessions, session => session.SourceAppUserModelId);
        var activeSession = _activeSessionCache.FindIn(sessions);
        if (activeSession is not null)
        {
            return activeSession;
        }

        var selectedSession = SelectSession(sessions);
        _activeSessionCache.Remember(selectedSession);
        return selectedSession;
    }

    private GlobalSystemMediaTransportControlsSession? SelectSession(
        IEnumerable<GlobalSystemMediaTransportControlsSession>? sessions)
    {
        var available = sessions?.Where(session => session is not null &&
            (CanUseSource(session.SourceAppUserModelId) || CanUseGenericSession(session.SourceAppUserModelId))).ToArray() ?? [];
        var current = _activeSessionCache.FindIn(available);
        var candidates = available.Select(session => new TaskBarWidgets.Media.Integration.MediaSessionCandidate(
            session.SourceAppUserModelId, ResolveSource(session.SourceAppUserModelId), IsSessionPlaying(session),
            GetRecognitionPriority(ResolveSource(session.SourceAppUserModelId)))).ToArray();
        var selected = TaskBarWidgets.Media.Integration.MediaSourcePolicy.Select(candidates, _selectedSourceId,
            _douyinPlayback.ControlSourceId ?? current?.SourceAppUserModelId);
        if (!available.Any(session => session.SourceAppUserModelId == _selectedSourceId) &&
            !string.Equals(_selectedSourceId, _douyinPlayback.ControlSourceId, StringComparison.OrdinalIgnoreCase)) _selectedSourceId = null;
        return available.FirstOrDefault(session => session.SourceAppUserModelId == selected);
    }
    private async Task<GlobalSystemMediaTransportControlsSessionManager?> GetManagerAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return null;
        }

        if (_manager is not null)
        {
            return _manager;
        }

        try
        {
            await _managerLock.WaitAsync(cancellationToken);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _isDisposed) != 0)
        {
            return null;
        }

        try
        {
            if (Volatile.Read(ref _isDisposed) != 0)
            {
                return null;
            }

            if (_manager is not null)
            {
                return _manager;
            }

            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellationToken);
            return _manager;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            try
            {
                _managerLock.Release();
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _isDisposed) != 0)
            {
                // Shutdown can race an in-flight manager request; its result is no longer needed.
            }
        }
    }

    internal static string NormalizeSource(string? sourceAppUserModelId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppUserModelId)) return string.Empty;
        if (sourceAppUserModelId.Contains("spotify", StringComparison.OrdinalIgnoreCase))
        {
            return "Spotify";
        }

        if (sourceAppUserModelId.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase) ||
            sourceAppUserModelId.Contains("netease", StringComparison.OrdinalIgnoreCase) ||
            sourceAppUserModelId.Contains("163music", StringComparison.OrdinalIgnoreCase) ||
            sourceAppUserModelId.Contains("music.163", StringComparison.OrdinalIgnoreCase) ||
            sourceAppUserModelId.Contains("wyy", StringComparison.OrdinalIgnoreCase))
        {
            return "Netease";
        }

        if (sourceAppUserModelId.Contains("qqmusic", StringComparison.OrdinalIgnoreCase))
        {
            return "QQMusic";
        }

        if (sourceAppUserModelId.Contains("kugou", StringComparison.OrdinalIgnoreCase))
        {
            return "Kugou";
        }

        if (NonLyricsMediaPolicy.IsDouyin(sourceAppUserModelId)) return "Douyin";
        if (IsBrowserSource(sourceAppUserModelId)) return "Browser";

        return sourceAppUserModelId;
    }

    private string ResolveSource(string? sourceAppUserModelId) =>
        sourceAppUserModelId is not null && _configuredCustomSources.Contains(sourceAppUserModelId)
            ? sourceAppUserModelId
            : NormalizeSource(sourceAppUserModelId);

    private bool IsSupportedSource(string sourceApp)
    {
        return _enabledSources.Contains(sourceApp);
    }

    internal static bool IsBlockedSystemSource(string sourceAppUserModelId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppUserModelId))
        {
            return false;
        }

        return sourceAppUserModelId.Contains("explorer", StringComparison.OrdinalIgnoreCase) ||
               sourceAppUserModelId.Contains("shellexperiencehost", StringComparison.OrdinalIgnoreCase) ||
               sourceAppUserModelId.Contains("searchhost", StringComparison.OrdinalIgnoreCase) ||
               sourceAppUserModelId.Contains("startmenuexperiencehost", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsBrowserSource(string sourceAppUserModelId)
    {
        return sourceAppUserModelId.Contains("msedge", StringComparison.OrdinalIgnoreCase) ||
               sourceAppUserModelId.Contains("microsoftedge", StringComparison.OrdinalIgnoreCase) ||
               sourceAppUserModelId.Contains("chrome", StringComparison.OrdinalIgnoreCase) ||
               sourceAppUserModelId.Contains("firefox", StringComparison.OrdinalIgnoreCase) ||
               sourceAppUserModelId.Contains("opera", StringComparison.OrdinalIgnoreCase) ||
               sourceAppUserModelId.Contains("brave", StringComparison.OrdinalIgnoreCase) ||
               sourceAppUserModelId.Contains("arc", StringComparison.OrdinalIgnoreCase) ||
               sourceAppUserModelId.Contains("vivaldi", StringComparison.OrdinalIgnoreCase);
    }

    internal bool CanUseSource(string? sourceAppUserModelId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppUserModelId)) return false;
        if (IsBlockedSystemSource(sourceAppUserModelId)) return false;
        if (!IsBrowserSource(sourceAppUserModelId)) return true;
        return _configuredCustomSources.Contains(sourceAppUserModelId)
            ? _enabledSources.Contains(sourceAppUserModelId)
            : _enabledSources.Contains("Browser");
    }

    private static bool IsAnyProcessRunning(params string[] names)
    {
        Process[] allProcesses;
        try
        {
            allProcesses = Process.GetProcesses();
        }
        catch
        {
            return false;
        }

        foreach (var rawName in names)
        {
            var name = rawName.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            try
            {
                if (Process.GetProcessesByName(name).Length > 0)
                {
                    return true;
                }

                if (allProcesses.Any(p =>
                {
                    try
                    {
                        return p.ProcessName.Contains(name, StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        return false;
                    }
                }))
                {
                    return true;
                }
            }
            catch
            {
                // Ignore query failures.
            }
        }

        return false;
    }

    private string ResolveSourceWithProcessFallback(string sourceApp)
    {
        if (!string.IsNullOrWhiteSpace(sourceApp) &&
            CanUseSource(sourceApp) &&
            !IsDisabledKnownSource(sourceApp))
        {
            return sourceApp;
        }

        var detected = DetectRunningSource();
        return detected ?? sourceApp;
    }

    private bool CanUseGenericSession(string sourceAppUserModelId)
    {
        return CanUseSource(sourceAppUserModelId) &&
               !IsDisabledKnownSource(sourceAppUserModelId);
    }

    private bool IsDisabledKnownSource(string sourceAppUserModelId)
    {
        var normalized = ResolveSource(sourceAppUserModelId);
        return (IsKnownSource(normalized) || _configuredCustomSources.Contains(normalized)) &&
               !_enabledSources.Contains(normalized);
    }

    private static bool IsKnownSource(string sourceApp)
    {
        return DefaultRecognitionOrder.Contains(sourceApp, StringComparer.OrdinalIgnoreCase);
    }

    private string? DetectRunningSource()
    {
        return _processFallbackDetectionCache.GetOrRefresh(DetectRunningSourceUncached);
    }

    private string? DetectRunningSourceUncached()
    {
        foreach (var source in _recognitionOrder)
        {
            if (source.Equals("QQMusic", StringComparison.OrdinalIgnoreCase) &&
                IsAnyProcessRunning("qqmusic", "qqmusicapp", "qqmusicplayer"))
            {
                return "QQMusic";
            }

            if (source.Equals("Netease", StringComparison.OrdinalIgnoreCase) &&
                IsAnyProcessRunning("cloudmusic", "neteasecloudmusic", "neteasemusic", "music.163", "music163"))
            {
                return "Netease";
            }

            if (source.Equals("Spotify", StringComparison.OrdinalIgnoreCase) &&
                IsAnyProcessRunning("spotify"))
            {
                return "Spotify";
            }

            if (source.Equals("Kugou", StringComparison.OrdinalIgnoreCase) &&
                IsAnyProcessRunning("kugou", "kugoumusic", "kgmusic"))
            {
                return "Kugou";
            }
        }

        return null;
    }

    private int GetRecognitionPriority(string source)
    {
        for (var i = 0; i < _recognitionOrder.Length; i++)
        {
            if (string.Equals(_recognitionOrder[i], source, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private static bool IsSessionPlaying(GlobalSystemMediaTransportControlsSession session)
    {
        var playback = session.GetPlaybackInfo();
        return playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
    }

    private static List<string> NormalizeRecognitionOrder(
        IReadOnlyList<string>? order,
        HashSet<string> enabledSources)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (order is not null)
        {
            foreach (var item in order)
            {
                var normalized = NormalizeRecognitionSource(item);
                if (!string.IsNullOrWhiteSpace(normalized) &&
                    enabledSources.Contains(normalized) &&
                    seen.Add(normalized))
                {
                    result.Add(normalized);
                }
            }
        }

        foreach (var source in DefaultRecognitionOrder)
        {
            if (enabledSources.Contains(source) && seen.Add(source))
            {
                result.Add(source);
            }
        }

        foreach (var source in enabledSources)
        {
            if (seen.Add(source)) result.Add(source);
        }

        return result;
    }

    private static string NormalizeRecognitionSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return string.Empty;
        }

        if (source.Contains("qqmusic", StringComparison.OrdinalIgnoreCase))
        {
            return "QQMusic";
        }

        if (source.Contains("netease", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase))
        {
            return "Netease";
        }

        if (source.Contains("spotify", StringComparison.OrdinalIgnoreCase))
        {
            return "Spotify";
        }

        if (source.Contains("kugou", StringComparison.OrdinalIgnoreCase))
        {
            return "Kugou";
        }

        return source.Trim();
    }

    private static bool TryInferTrackFromNeteaseProcess(out string title, out string artist)
    {
        title = string.Empty;
        artist = string.Empty;

        Process[] allProcesses;
        try
        {
            allProcesses = Process.GetProcesses();
        }
        catch
        {
            return false;
        }

        foreach (var process in allProcesses)
        {
            string processName;
            string windowTitle;
            try
            {
                processName = process.ProcessName;
                windowTitle = process.MainWindowTitle?.Trim() ?? string.Empty;
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(windowTitle))
            {
                continue;
            }

            if (!(processName.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase) ||
                  processName.Contains("netease", StringComparison.OrdinalIgnoreCase) ||
                  processName.Contains("163", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (windowTitle.Contains("网易云音乐", StringComparison.OrdinalIgnoreCase) &&
                windowTitle.Length <= 12)
            {
                continue;
            }

            var match = TitleArtistRegex.Match(windowTitle);
            if (!match.Success)
            {
                continue;
            }

            title = match.Groups["title"].Value.Trim();
            artist = match.Groups["artist"].Value.Trim();

            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(artist))
            {
                artist = "Unknown Artist";
            }

            return true;
        }

        return false;
    }

    private static async Task<CoverReadResult> ReadCoverBytesAsync(
        IRandomAccessStreamReference? thumbnail,
        CancellationToken cancellationToken)
    {
        if (thumbnail is null)
        {
            return new CoverReadResult(null, string.Empty, "Thumbnail reference is null", null, 0, 0);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var stream = await thumbnail.OpenReadAsync().AsTask(cancellationToken);
            var contentType = stream.ContentType ?? string.Empty;
            if (stream.Size == 0)
            {
                return new CoverReadResult(null, contentType, "Thumbnail stream is empty", null, 0, stopwatch.ElapsedMilliseconds);
            }

            if (stream.Size > uint.MaxValue)
            {
                return new CoverReadResult(null, contentType, $"Thumbnail stream is too large ({stream.Size} bytes)", null, 0, stopwatch.ElapsedMilliseconds);
            }

            using var input = stream.GetInputStreamAt(0);
            using var dataReader = new DataReader(input);
            var size = (uint)stream.Size;
            var loadedSize = await dataReader.LoadAsync(size).AsTask(cancellationToken);
            if (loadedSize != size)
            {
                return new CoverReadResult(
                    null,
                    contentType,
                    $"Thumbnail stream was only partially loaded ({loadedSize}/{size} bytes)",
                    null,
                    loadedSize,
                    stopwatch.ElapsedMilliseconds);
            }

            var bytes = new byte[size];
            dataReader.ReadBytes(bytes);
            return new CoverReadResult(bytes, contentType, string.Empty, null, loadedSize, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return new CoverReadResult(null, string.Empty, ex.Message, ex, 0, stopwatch.ElapsedMilliseconds);
        }
    }

    private (byte[]? Bytes, bool IsLoading) GetCoverBytes(
        string sourceApp,
        string title,
        string artist,
        string album,
        string? songId,
        IRandomAccessStreamReference? thumbnail,
        DateTimeOffset nowUtc)
    {
        var metadataKey = CoverIdentity.FromMetadata(sourceApp, songId, title, artist, album);
        byte[]? cachedCover;
        var isLoading = false;
        var thumbnailStateChanged = false;
        var thumbnailState = thumbnail is null ? "Missing" : "Provided";

        lock (_coverLock)
        {
            var trackChanged = !string.Equals(metadataKey, _lastCoverMetadataKey, StringComparison.Ordinal);
            if (trackChanged)
            {
                _lastCoverMetadataKey = metadataKey;
                _lastCoverImageBytes = null;
                _nextMissingCoverRetryUtc = nowUtc;
            }

            var thumbnailDiagnosticsKey = $"{metadataKey}|{thumbnailState}";
            if (!string.Equals(thumbnailDiagnosticsKey, _lastCoverThumbnailDiagnosticsKey, StringComparison.Ordinal))
            {
                _lastCoverThumbnailDiagnosticsKey = thumbnailDiagnosticsKey;
                thumbnailStateChanged = true;
            }

            cachedCover = _lastCoverImageBytes;
            var shouldRetryMissingCover = cachedCover is null && nowUtc >= _nextMissingCoverRetryUtc;
            var isReadingCurrentCover =
                _coverReadTask is { IsCompleted: false } &&
                string.Equals(_coverReadMetadataKey, metadataKey, StringComparison.Ordinal);

            if (thumbnail is not null && shouldRetryMissingCover && !isReadingCurrentCover)
            {
                var readSequence = Interlocked.Increment(ref _coverReadSequence);
                _coverReadMetadataKey = metadataKey;
                _coverReadTask = ReadCoverBytesInBackgroundAsync(metadataKey, thumbnail, readSequence);
                _nextMissingCoverRetryUtc = DateTimeOffset.MaxValue;
                isLoading = true;
            }
            else
            {
                isLoading = cachedCover is null && isReadingCurrentCover;
            }
        }

        if (thumbnailStateChanged)
        {
            Log.Diagnostic(
                "COVER-SMTC",
                $"Thumbnail{thumbnailState} Track='{ToLogValue(metadataKey)}'");
        }

        return (cachedCover, isLoading);
    }

    private async Task ReadCoverBytesInBackgroundAsync(
        string metadataKey,
        IRandomAccessStreamReference thumbnail,
        int readSequence)
    {
        Log.Diagnostic(
            "COVER-SMTC",
            $"ReadStarted Sequence={readSequence} Track='{ToLogValue(metadataKey)}'");

        using var timeoutSource = new CancellationTokenSource(CoverReadTimeout);
        var result = await ReadCoverBytesAsync(thumbnail, timeoutSource.Token);
        var nowUtc = DateTimeOffset.UtcNow;
        var isStale = false;

        lock (_coverLock)
        {
            if (!string.Equals(metadataKey, _lastCoverMetadataKey, StringComparison.Ordinal))
            {
                isStale = true;
            }
            else
            {
                _lastCoverImageBytes = result.Bytes;
                _nextMissingCoverRetryUtc = result.Bytes is null
                    ? nowUtc + MissingCoverRetryInterval
                    : DateTimeOffset.MaxValue;
            }
        }

        if (isStale)
        {
            Log.Diagnostic(
                "COVER-SMTC",
                $"ReadDiscarded Sequence={readSequence} Reason='Track changed' Track='{ToLogValue(metadataKey)}' " +
                $"ElapsedMs={result.ElapsedMilliseconds}");
            return;
        }

        if (result.Bytes is { Length: > 0 } bytes)
        {
            Log.Diagnostic(
                "COVER-SMTC",
                $"ReadSucceeded Sequence={readSequence} Track='{ToLogValue(metadataKey)}' Bytes={bytes.Length} " +
                $"ContentType='{ToLogValue(result.ContentType)}' Signature='{GetByteSignature(bytes)}' " +
                $"ElapsedMs={result.ElapsedMilliseconds}");
            return;
        }

        var exceptionText = result.Exception is null
            ? string.Empty
            : $" Exception='{result.Exception.GetType().Name}' HResult=0x{result.Exception.HResult:X8}";
        Log.Diagnostic(
            "COVER-SMTC",
            $"ReadFailed Sequence={readSequence} Track='{ToLogValue(metadataKey)}' " +
            $"ContentType='{ToLogValue(result.ContentType)}' LoadedBytes={result.LoadedBytes} " +
            $"Reason='{ToLogValue(result.FailureReason)}'{exceptionText} ElapsedMs={result.ElapsedMilliseconds} " +
            $"RetryAfterMs={(int)MissingCoverRetryInterval.TotalMilliseconds}");
    }

    private static string GetByteSignature(byte[] bytes)
    {
        return Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 12)));
    }

    private static string ToLogValue(string? value)
    {
        var normalized = (value ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return normalized.Length <= 240 ? normalized : normalized[..240];
    }

    private sealed record CoverReadResult(
        byte[]? Bytes,
        string ContentType,
        string FailureReason,
        Exception? Exception,
        uint LoadedBytes,
        long ElapsedMilliseconds);

}
