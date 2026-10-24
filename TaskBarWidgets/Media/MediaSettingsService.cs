using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaskbarLyrics.Core.Abstractions;
using TaskbarLyrics.Core.Models;
using TaskbarLyrics.Core.Services;
using TaskbarLyrics.Core.Utilities;

namespace TaskbarLyrics.App;

// Headless settings service used by the native host and WinUI settings surface.
// It owns no Window, WebView2, dispatcher, or JavaScript bridge.
public sealed class MediaSettingsService : IDisposable
{
    private const int ProtocolVersion = 1;
    private const string ProductRepositoryUrl = "https://github.com/NoctisWang528/TaskBarWidget";
    private static readonly string[] BuiltInPlayerSources = ["QQMusic", "Netease", "Kugou", "Spotify", "Browser"];
    private static readonly JsonSerializerOptions SettingsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly string[] AllowedCommands =
    [
        "discoverPlayerSessions", "addPlayerSource", "updatePlayerSource", "removePlayerSource",
        "reorderSources", "clearCache", "runLyricDiagnostics", "applyLyricDiagnosticCandidate",
        "queryTrackOffsets", "trackOffsetsPageActivated", "setCurrentTrackOffset", "setStoredTrackOffset",
        "deleteTrackOffset", "clearTrackOffsets", "confirmSpectrumAudioAccess", "revokeSpectrumAudioAccess",
        "retrySpectrumCapture", "disableSpectrum", "resetDefaults", "resetLyricsLayoutBase"
    ];

    private readonly AppSettings _settings;
    private readonly TrackLyricOffsetStore? _trackLyricOffsetStore;
    private readonly Func<Task<CurrentTrackLyricsContext?>> _getCurrentTrackLyricsContext;
    private readonly Func<LyricDiagnosticRunner> _createLyricDiagnosticRunner;
    private readonly Func<TrackInfo, ResolvedLyrics, CancellationToken, Task<bool>> _tryApplyResolvedLyrics;
    private readonly Func<TrackInfo, ResolvedLyrics, CancellationToken, Task<ResolvedLyricCacheStoreResult>> _rememberResolvedLyrics;
    private readonly Action _clearLyricCache;
    private readonly Func<AppSettings, bool> _saveSettings;
    private readonly Action<AppSettings>? _previewSettings;
    private readonly Action? _retrySpectrumCapture;
    private readonly Func<bool> _isApplicationUsingLightTheme;
    private readonly Func<bool> _isSystemUiUsingLightTheme;
    private readonly Func<IReadOnlyList<DisplayMonitor>> _getDisplays;
    private readonly Func<double>? _getPrimaryDpiScale;
    private readonly Func<double>? _getWorkAreaWidth;
    private readonly Func<CancellationToken, Task<IReadOnlyList<AvailablePlayerSession>>> _discoverPlayerSessions;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<MediaSettingsEvent> _events = [];
    private readonly object _stateGate = new();

    private string _lastCurrentTrackOffsetPayloadJson = string.Empty;
    private TrackOffsetQueryPayload _trackOffsetQuery = new();
    private string? _taskbarEmbeddingStatus;
    private string _lastSpectrumCaptureStateJson = string.Empty;
    private CancellationTokenSource? _lyricDiagnosticsCancellation;
    private CancellationTokenSource? _lyricDiagnosticsApplyCancellation;
    private LyricDiagnosticRunner? _lyricDiagnosticRunner;
    private CancellationTokenSource? _playerSessionDiscoveryCancellation;
    private Dictionary<string, AvailablePlayerSession> _discoveredPlayerSessions =
        new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _disposed;

    internal MediaSettingsService(
        AppSettings settings,
        TrackLyricOffsetStore? trackLyricOffsetStore,
        Func<Task<CurrentTrackLyricsContext?>> getCurrentTrackLyricsContext,
        Func<LyricDiagnosticRunner> createLyricDiagnosticRunner,
        Func<TrackInfo, ResolvedLyrics, CancellationToken, Task<bool>> tryApplyResolvedLyrics,
        Func<TrackInfo, ResolvedLyrics, CancellationToken, Task<ResolvedLyricCacheStoreResult>> rememberResolvedLyrics,
        Action clearLyricCache,
        Func<AppSettings, bool> saveSettings,
        Action<AppSettings>? previewSettings = null,
        Action? retrySpectrumCapture = null,
        Func<bool>? isApplicationUsingLightTheme = null,
        Func<bool>? isSystemUiUsingLightTheme = null,
        Func<IReadOnlyList<DisplayMonitor>>? getDisplays = null,
        Func<double>? getPrimaryDpiScale = null,
        Func<double>? getWorkAreaWidth = null,
        Func<CancellationToken, Task<IReadOnlyList<AvailablePlayerSession>>>? discoverPlayerSessions = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(getCurrentTrackLyricsContext);
        ArgumentNullException.ThrowIfNull(createLyricDiagnosticRunner);
        ArgumentNullException.ThrowIfNull(tryApplyResolvedLyrics);
        ArgumentNullException.ThrowIfNull(rememberResolvedLyrics);
        ArgumentNullException.ThrowIfNull(clearLyricCache);
        ArgumentNullException.ThrowIfNull(saveSettings);

        _settings = settings.Clone();
        _trackLyricOffsetStore = trackLyricOffsetStore;
        _getCurrentTrackLyricsContext = getCurrentTrackLyricsContext;
        _createLyricDiagnosticRunner = createLyricDiagnosticRunner;
        _tryApplyResolvedLyrics = tryApplyResolvedLyrics;
        _rememberResolvedLyrics = rememberResolvedLyrics;
        _clearLyricCache = clearLyricCache;
        _saveSettings = saveSettings;
        _previewSettings = previewSettings;
        _retrySpectrumCapture = retrySpectrumCapture;
        _isApplicationUsingLightTheme = isApplicationUsingLightTheme ?? (() => true);
        _isSystemUiUsingLightTheme = isSystemUiUsingLightTheme ?? (() => true);
        _getDisplays = getDisplays ?? DisplayMonitorService.GetDisplays;
        _getPrimaryDpiScale = getPrimaryDpiScale;
        _getWorkAreaWidth = getWorkAreaWidth;
        _discoverPlayerSessions = discoverPlayerSessions ?? PlayerMediaSessionDiscovery.DiscoverAsync;
    }

    public JsonElement ReadServiceSettings(AppSettings current)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(current);

        lock (_stateGate)
        {
            CopySettings(current, _settings);
            return JsonSerializer.SerializeToElement(CreateSettingsPayload(), SettingsJsonOptions);
        }
    }

    public JsonElement ApplyServiceSettings(AppSettings current, JsonElement patch)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(current);
        if (patch.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("设置格式无效。");
        }

        lock (_stateGate)
        {
            CopySettings(current, _settings);
            foreach (var field in patch.EnumerateObject())
            {
                ApplyWebSettingUpdate(field.Name, field.Value);
            }

            if (!SaveSettings())
            {
                throw new IOException("无法保存媒体设置。");
            }

            return JsonSerializer.SerializeToElement(CreateSettingsPayload(), SettingsJsonOptions);
        }
    }

    public async Task<JsonElement> ExecuteServiceCommandAsync(
        AppSettings current,
        string action,
        JsonElement? value)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(current);
        if (!AllowedCommands.Contains(action, StringComparer.Ordinal))
        {
            throw new ArgumentException("不支持的设置操作。");
        }

        ReadServiceSettings(current);
        lock (_stateGate)
        {
            _events.Clear();
        }

        await DispatchServiceCommandAsync(action, value, _lifetime.Token);
        ThrowIfDisposed();

        MediaSettingsPayload settings;
        MediaSettingsEvent[] events;
        lock (_stateGate)
        {
            settings = CreateSettingsPayload();
            events = _events.ToArray();
        }

        return JsonSerializer.SerializeToElement(
            new { settings, events },
            SettingsJsonOptions);
    }

    private Task DispatchServiceCommandAsync(string action, JsonElement? value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return action switch
        {
            "discoverPlayerSessions" => DiscoverPlayerSessionsAsync(cancellationToken),
            "addPlayerSource" => AddPlayerSourceAsync(value, cancellationToken),
            "updatePlayerSource" => UpdatePlayerSourceAsync(value, cancellationToken),
            "removePlayerSource" => RemovePlayerSourceAsync(value, cancellationToken),
            "reorderSources" => ReorderSourcesAsync(value, cancellationToken),
            "clearCache" => ClearLyricCacheAsync(cancellationToken),
            "runLyricDiagnostics" => RunLyricDiagnosticsAsync(cancellationToken),
            "applyLyricDiagnosticCandidate" => ApplyLyricDiagnosticCandidateAsync(value, cancellationToken),
            "queryTrackOffsets" => QueryTrackOffsetsAsync(value, cancellationToken),
            "trackOffsetsPageActivated" => ActivateTrackOffsetsPageAsync(cancellationToken),
            "setCurrentTrackOffset" => SetCurrentTrackOffsetAsync(value, cancellationToken),
            "setStoredTrackOffset" => SetStoredTrackOffsetAsync(value, cancellationToken),
            "deleteTrackOffset" => DeleteTrackOffsetAsync(value, cancellationToken),
            "clearTrackOffsets" => ClearTrackOffsetsAsync(cancellationToken),
            "confirmSpectrumAudioAccess" => ConfirmSpectrumAudioAccessAsync(value, cancellationToken),
            "revokeSpectrumAudioAccess" => RevokeSpectrumAudioAccessAsync(cancellationToken),
            "retrySpectrumCapture" => RetrySpectrumCaptureAsync(cancellationToken),
            "disableSpectrum" => DisableSpectrumAsync(cancellationToken),
            "resetDefaults" => ResetDefaultsAsync(cancellationToken),
            "resetLyricsLayoutBase" => ResetLyricsLayoutBaseAsync(cancellationToken),
            _ => throw new ArgumentException("不支持的设置操作。")
        };
    }

    private async Task DiscoverPlayerSessionsAsync(CancellationToken cancellationToken)
    {
        var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linkedCancellation.Token;
        var previous = Interlocked.Exchange(ref _playerSessionDiscoveryCancellation, linkedCancellation);
        previous?.Cancel();
        previous?.Dispose();

        try
        {
            var sessions = await _discoverPlayerSessions(token);
            token.ThrowIfCancellationRequested();
            _discoveredPlayerSessions = sessions.ToDictionary(
                session => session.SourceAppUserModelId,
                StringComparer.OrdinalIgnoreCase);
            Publish("playerSessions", new { status = "ready", sessions });
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log.Diagnostic("SMTC", $"PlayerSessionDiscoveryFailed Exception='{exception.GetType().Name}'");
            token.ThrowIfCancellationRequested();
            Publish(
                "playerSessions",
                new { status = "error", sessions = Array.Empty<AvailablePlayerSession>() });
        }
        finally
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _playerSessionDiscoveryCancellation, null, linkedCancellation),
                    linkedCancellation))
            {
                linkedCancellation.Dispose();
            }
        }
    }

    private async Task AddPlayerSourceAsync(JsonElement? value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryParseCustomPlayerSourceAddRequest(value, out var request) &&
            _discoveredPlayerSessions.TryGetValue(request.SourceAppUserModelId, out var discovered) &&
            !discovered.IsBuiltIn &&
            _settings.AddCustomPlayerSource(
                request.SourceAppUserModelId,
                request.DisplayName,
                request.IconDataUrl,
                request.PresetIconId,
                request.PresetIconColor,
                request.Enabled,
                request.LyricProviders))
        {
            await SaveSettingsAndNotifyAsync();
            PublishSettingsState();
            return;
        }

        Publish(
            "playerSourceActionResult",
            new { message = "无法添加播放器，请刷新媒体会话后重试。" });
        throw new InvalidOperationException("无法添加播放器，请刷新媒体会话后重试。");
    }

    private async Task UpdatePlayerSourceAsync(JsonElement? value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryParseCustomPlayerSourceAddRequest(value, out var request) &&
            _settings.UpdateCustomPlayerSource(
                request.SourceAppUserModelId,
                request.DisplayName,
                request.IconDataUrl,
                request.PresetIconId,
                request.PresetIconColor))
        {
            await SaveSettingsAndNotifyAsync();
            PublishSettingsState();
            return;
        }

        Publish(
            "playerSourceActionResult",
            new { message = "无法保存播放器信息，请重试。" });
        throw new InvalidOperationException("无法保存播放器信息，请重试。");
    }

    private async Task RemovePlayerSourceAsync(JsonElement? value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value is { ValueKind: JsonValueKind.String } removePayload &&
            _settings.RemoveCustomPlayerSource(removePayload.GetString()))
        {
            await SaveSettingsAndNotifyAsync();
            PublishSettingsState();
        }
    }

    private async Task ReorderSourcesAsync(JsonElement? value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplySourceOrder(value);
        await SaveSettingsAndNotifyAsync();
        PublishSettingsState();
    }

    private Task ClearLyricCacheAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _clearLyricCache();
        return Task.CompletedTask;
    }

    private async Task RunLyricDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = cancellation.Token;
        var previousCancellation = Interlocked.Exchange(ref _lyricDiagnosticsCancellation, cancellation);
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
        var previousApplyCancellation = Interlocked.Exchange(ref _lyricDiagnosticsApplyCancellation, null);
        previousApplyCancellation?.Cancel();
        previousApplyCancellation?.Dispose();
        var previousRunner = Interlocked.Exchange(ref _lyricDiagnosticRunner, null);
        previousRunner?.Dispose();
        LyricDiagnosticRunner? runner = null;

        try
        {
            var context = await _getCurrentTrackLyricsContext();
            token.ThrowIfCancellationRequested();
            if (context is null ||
                string.IsNullOrWhiteSpace(context.Track.Title) ||
                string.Equals(context.Track.Title, "Unknown Title", StringComparison.OrdinalIgnoreCase))
            {
                PublishLyricDiagnosticsState(new
                {
                    status = "empty",
                    message = "未检测到可检索的当前歌曲，请先开始播放。"
                });
                return;
            }

            var track = context.Track;
            PublishLyricDiagnosticsState(new
            {
                status = "running",
                track = new
                {
                    track.Title,
                    track.Artist,
                    track.Album,
                    track.SourceApp,
                    durationSeconds = track.Duration.TotalSeconds,
                    track.SongId
                }
            });
            runner = _createLyricDiagnosticRunner();
            Interlocked.Exchange(ref _lyricDiagnosticRunner, runner)?.Dispose();
            var report = await runner.RunAsync(track, token);
            token.ThrowIfCancellationRequested();
            PublishLyricDiagnosticsState(new
            {
                status = "success",
                report
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (runner is not null &&
                !ReferenceEquals(Volatile.Read(ref _lyricDiagnosticRunner), runner))
            {
                return;
            }

            Log.Warn($"Lyric diagnostics failed: {exception.Message}");
            PublishLyricDiagnosticsState(new
            {
                status = "error",
                message = "歌词匹配失败，请稍后重试。"
            });
        }
        finally
        {
            if (runner is not null &&
                ReferenceEquals(Volatile.Read(ref _lyricDiagnosticRunner), runner) &&
                cancellation.IsCancellationRequested)
            {
                Interlocked.CompareExchange(ref _lyricDiagnosticRunner, null, runner);
                runner.Dispose();
            }

            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _lyricDiagnosticsCancellation, null, cancellation),
                    cancellation))
            {
                cancellation.Dispose();
            }
        }
    }

    private async Task ApplyLyricDiagnosticCandidateAsync(
        JsonElement? value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryParseLyricDiagnosticCandidateApplyRequest(value, out var request))
        {
            return;
        }

        var providerId = request.ProviderId;
        var candidateId = request.CandidateId;
        var mode = request.Mode;
        var runner = Volatile.Read(ref _lyricDiagnosticRunner);
        if (runner is null)
        {
            PublishLyricDiagnosticsApplyState(
                "error",
                providerId,
                candidateId,
                mode,
                "匹配候选已失效，请重新查找。");
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = cancellation.Token;
        var previousCancellation = Interlocked.Exchange(
            ref _lyricDiagnosticsApplyCancellation,
            cancellation);
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();

        try
        {
            PublishLyricDiagnosticsApplyState(
                "running",
                providerId,
                candidateId,
                mode,
                mode == LyricDiagnosticApplyMode.Remember
                    ? "正在获取、应用并写入歌词缓存…"
                    : "正在获取并应用歌词…");
            var context = await _getCurrentTrackLyricsContext();
            token.ThrowIfCancellationRequested();
            if (context is null ||
                string.IsNullOrWhiteSpace(context.Track.Title) ||
                string.Equals(context.Track.Title, "Unknown Title", StringComparison.OrdinalIgnoreCase))
            {
                PublishLyricDiagnosticsApplyState(
                    "error",
                    providerId,
                    candidateId,
                    mode,
                    "当前没有可应用歌词的歌曲，请重新查找。");
                return;
            }

            var resolved = await runner.ResolveCandidateAsync(
                context.Track,
                providerId,
                candidateId,
                token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(Volatile.Read(ref _lyricDiagnosticRunner), runner))
            {
                return;
            }

            if (resolved is null)
            {
                PublishLyricDiagnosticsApplyState(
                    "error",
                    providerId,
                    candidateId,
                    mode,
                    "候选歌词未通过内容校验，请重新查找或选择其他候选。");
                return;
            }

            var applied = await _tryApplyResolvedLyrics(
                context.Track,
                resolved,
                token);
            token.ThrowIfCancellationRequested();
            if (!applied)
            {
                PublishLyricDiagnosticsApplyState(
                    "error",
                    providerId,
                    candidateId,
                    mode,
                    "当前歌曲已切换，请重新查找。");
                return;
            }

            if (mode == LyricDiagnosticApplyMode.Remember)
            {
                var remembered = await _rememberResolvedLyrics(
                    context.Track,
                    resolved,
                    token);
                token.ThrowIfCancellationRequested();
                PublishLyricDiagnosticsApplyState(
                    remembered.IsSaved ? "success" : "error",
                    providerId,
                    candidateId,
                    mode,
                    remembered.IsSaved
                        ? "已应用并写入歌词缓存；再次匹配到相同标题和歌手时会直接使用。"
                        : $"歌词已应用，但未能写入歌词缓存：{remembered.FailureReason ?? "请检查日志。"}");
                return;
            }

            PublishLyricDiagnosticsApplyState(
                "success",
                providerId,
                candidateId,
                mode,
                "已应用当前歌词。");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(Volatile.Read(ref _lyricDiagnosticRunner), runner) &&
                ReferenceEquals(Volatile.Read(ref _lyricDiagnosticsApplyCancellation), cancellation))
            {
                Log.Warn($"Applying lyric diagnostic candidate failed: {exception.Message}");
                PublishLyricDiagnosticsApplyState(
                    "error",
                    providerId,
                    candidateId,
                    mode,
                    "应用歌词失败，请重新查找或选择其他候选。");
            }
        }
        finally
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _lyricDiagnosticsApplyCancellation, null, cancellation),
                    cancellation))
            {
                cancellation.Dispose();
            }
        }
    }

    private void PublishLyricDiagnosticsApplyState(
        string status,
        string providerId,
        string candidateId,
        LyricDiagnosticApplyMode mode,
        string message)
    {
        PublishLyricDiagnosticsState(new
        {
            status = "success",
            apply = new
            {
                status,
                providerId,
                candidateId,
                mode = mode == LyricDiagnosticApplyMode.Remember ? "remember" : "current",
                message
            }
        });
    }

    private void PublishLyricDiagnosticsState(object payload)
    {
        Publish("lyricDiagnosticsState", payload);
    }

    private async Task ActivateTrackOffsetsPageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await PublishCurrentTrackOffsetAsync(force: true, cancellationToken);
    }

    private async Task QueryTrackOffsetsAsync(JsonElement? value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _trackOffsetQuery = DeserializeTrackOffsetQuery(value) ?? new TrackOffsetQueryPayload();
        await PublishTrackOffsetEntriesAsync(_trackOffsetQuery, cancellationToken);
    }

    private async Task SetCurrentTrackOffsetAsync(JsonElement? value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = await _getCurrentTrackLyricsContext();
        cancellationToken.ThrowIfCancellationRequested();
        if (context is null || string.IsNullOrWhiteSpace(context.LyricSource))
        {
            PublishTrackOffsetSaveStatus(false, "歌词源尚未确定，暂时无法保存单曲偏移。");
            return;
        }

        var offset = ReadOffsetMilliseconds(value, 0);
        var result = await OffsetStore.SetOffsetAsync(
            context.Track,
            context.LyricSource,
            offset,
            cancellationToken);
        await CompleteTrackOffsetMutationAsync(result, cancellationToken);
    }

    private async Task SetStoredTrackOffsetAsync(JsonElement? value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mutation = DeserializeTrackOffsetMutation(value);
        if (mutation?.Key is null)
        {
            PublishTrackOffsetSaveStatus(false, "偏移记录无效。");
            return;
        }

        var result = await OffsetStore.SetOffsetAsync(
            mutation.Key.Value,
            mutation.OffsetMilliseconds,
            cancellationToken);
        await CompleteTrackOffsetMutationAsync(result, cancellationToken);
    }

    private async Task DeleteTrackOffsetAsync(JsonElement? value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = DeserializeTrackOffsetKey(value);
        if (key is null)
        {
            PublishTrackOffsetSaveStatus(false, "偏移记录无效。");
            return;
        }

        var result = await OffsetStore.DeleteAsync(key.Value, cancellationToken);
        await CompleteTrackOffsetMutationAsync(result, cancellationToken);
    }

    private async Task ClearTrackOffsetsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await OffsetStore.ClearAsync(cancellationToken);
        await CompleteTrackOffsetMutationAsync(result, cancellationToken);
    }

    private async Task CompleteTrackOffsetMutationAsync(
        TrackLyricOffsetSaveResult result,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _lastCurrentTrackOffsetPayloadJson = string.Empty;
        await PublishCurrentTrackOffsetAsync(force: true, cancellationToken);
        await PublishTrackOffsetEntriesAsync(_trackOffsetQuery, cancellationToken);
        PublishTrackOffsetSaveStatus(
            result.IsSaved,
            result.IsSaved ? "单曲偏移已保存" : "单曲偏移保存失败，请稍后重试。");
    }

    private void PublishTrackOffsetSaveStatus(bool isSaved, string message)
    {
        Publish(
            "trackOffsetSaveStatus",
            new { state = isSaved ? "saved" : "error", message });
    }

    private async Task PublishCurrentTrackOffsetAsync(
        bool force,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = await _getCurrentTrackLyricsContext();
        cancellationToken.ThrowIfCancellationRequested();
        CurrentTrackOffsetPayload? current = null;
        if (context is not null)
        {
            var playerOffset = _settings.GetPlayerLyricOffsetMilliseconds(context.Track.SourceApp);
            var lyricSourceReady = !string.IsNullOrWhiteSpace(context.LyricSource);
            var trackOffset = lyricSourceReady
                ? OffsetStore.GetOffsetMilliseconds(context.Track, context.LyricSource)
                : 0;
            current = new CurrentTrackOffsetPayload
            {
                Title = context.Track.Title,
                Artist = context.Track.Artist,
                SourceApp = context.Track.SourceApp,
                LyricSource = context.LyricSource,
                DurationSeconds = Math.Max(0, (int)Math.Round(context.Track.Duration.TotalSeconds)),
                LyricSourceReady = lyricSourceReady,
                PlayerOffsetMilliseconds = playerOffset,
                TrackOffsetMilliseconds = trackOffset,
                EffectiveOffsetMilliseconds = playerOffset + trackOffset
            };
        }

        var json = JsonSerializer.Serialize(current, SettingsJsonOptions);
        if (!force && string.Equals(json, _lastCurrentTrackOffsetPayloadJson, StringComparison.Ordinal))
        {
            return;
        }

        _lastCurrentTrackOffsetPayloadJson = json;
        Publish("currentTrackOffset", current);
    }

    private async Task PublishTrackOffsetEntriesAsync(
        TrackOffsetQueryPayload query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var page = await OffsetStore.QueryEntriesAsync(
            query.Page,
            query.PageSize,
            query.Search,
            string.Equals(query.LyricSource, "All", StringComparison.OrdinalIgnoreCase)
                ? null
                : query.LyricSource,
            ParseTrackOffsetSort(query.Sort),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var payload = new TrackOffsetEntriesPagePayload
        {
            RequestId = query.RequestId,
            Page = page.Page,
            PageSize = page.PageSize,
            PageCount = page.PageCount,
            TotalCount = page.TotalCount,
            UnfilteredCount = page.UnfilteredCount,
            LyricSources = page.LyricSources.ToList(),
            Entries = page.Entries.Select(entry => new TrackOffsetEntryPayload
            {
                Key = entry.Key,
                Title = entry.DisplayTitle,
                Artist = entry.DisplayArtist,
                SourceApp = entry.SourceApp,
                LyricSource = entry.LyricSource,
                DurationSeconds = entry.Key.DurationBucketSeconds,
                OffsetMilliseconds = entry.OffsetMilliseconds,
                UpdatedAtUtc = entry.UpdatedAtUtc
            }).ToList()
        };
        Publish("trackOffsetEntries", payload);
    }

    private static int ReadOffsetMilliseconds(JsonElement? value, int fallback)
    {
        if (value is null)
        {
            return fallback;
        }

        return (int)Math.Round(Math.Clamp(
            ReadDouble(value.Value, fallback),
            TrackLyricOffsetStore.MinimumOffsetMilliseconds,
            TrackLyricOffsetStore.MaximumOffsetMilliseconds));
    }

    private static TrackOffsetMutationPayload? DeserializeTrackOffsetMutation(JsonElement? value)
    {
        return value is null
            ? null
            : JsonSerializer.Deserialize<TrackOffsetMutationPayload>(
                value.Value.GetRawText(),
                SettingsJsonOptions);
    }

    private static TrackOffsetQueryPayload? DeserializeTrackOffsetQuery(JsonElement? value)
    {
        return value is null
            ? null
            : JsonSerializer.Deserialize<TrackOffsetQueryPayload>(
                value.Value.GetRawText(),
                SettingsJsonOptions);
    }

    private static TrackLyricOffsetSort ParseTrackOffsetSort(string? value)
    {
        return value switch
        {
            "title" => TrackLyricOffsetSort.Title,
            "offset" => TrackLyricOffsetSort.OffsetMagnitude,
            _ => TrackLyricOffsetSort.Updated
        };
    }

    private static TrackLyricOffsetRecordKey? DeserializeTrackOffsetKey(JsonElement? value)
    {
        return value is null
            ? null
            : JsonSerializer.Deserialize<TrackLyricOffsetRecordKey>(
                value.Value.GetRawText(),
                SettingsJsonOptions);
    }

    private TrackLyricOffsetStore OffsetStore =>
        _trackLyricOffsetStore ??
        throw new InvalidOperationException("单曲偏移服务尚未准备好。");

    private async Task ConfirmSpectrumAudioAccessAsync(
        JsonElement? value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value is null ||
            !Enum.TryParse<SpectrumDisplayMode>(ReadString(value.Value, string.Empty), true, out var mode) ||
            mode == SpectrumDisplayMode.Disabled)
        {
            return;
        }

        _settings.SpectrumAudioAccessGranted = true;
        _settings.SpectrumDisplayMode = mode;
        await SaveSettingsAndNotifyAsync();
        PublishSettingsState();
        PublishSpectrumCaptureState(force: true);
    }

    private async Task RevokeSpectrumAudioAccessAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _settings.SpectrumAudioAccessGranted = false;
        _settings.SpectrumDisplayMode = SpectrumDisplayMode.Disabled;
        await SaveSettingsAndNotifyAsync();
        PublishSettingsState();
        PublishSpectrumCaptureState(force: true);
    }

    private Task RetrySpectrumCaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _retrySpectrumCapture?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        PublishSpectrumRetryingState();
        return Task.CompletedTask;
    }

    private async Task DisableSpectrumAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _settings.SpectrumDisplayMode = SpectrumDisplayMode.Disabled;
        await SaveSettingsAndNotifyAsync();
        PublishSettingsState();
        PublishSpectrumCaptureState(force: true);
    }

    private void PublishSpectrumCaptureState(bool force)
    {
        var diagnostics = SpectrumDiagnosticsState.Current;
        var state = !_settings.SpectrumAudioAccessGranted
            ? "notGranted"
            : _settings.SpectrumDisplayMode == SpectrumDisplayMode.Disabled
                ? "disabled"
                : !string.IsNullOrWhiteSpace(diagnostics.LastError)
                    ? "blocked"
                    : diagnostics.IsCaptureAvailable
                        ? "capturing"
                        : "waiting";
        var message = state switch
        {
            "notGranted" => "尚未允许读取系统播放声音。",
            "disabled" => "频谱已关闭，不会读取系统播放声音。",
            "blocked" => DescribeSpectrumCaptureError(diagnostics.LastError),
            "capturing" => "正在读取系统播放声音并生成频谱。",
            _ => "已允许；仅在频谱需要显示时读取系统播放声音。"
        };
        var payload = new { state, message };
        var json = JsonSerializer.Serialize(payload, SettingsJsonOptions);
        if (!force && string.Equals(json, _lastSpectrumCaptureStateJson, StringComparison.Ordinal))
        {
            return;
        }

        _lastSpectrumCaptureStateJson = json;
        Publish("spectrumCaptureState", payload);
    }

    private static string DescribeSpectrumCaptureError(string? lastError)
    {
        if (string.IsNullOrWhiteSpace(lastError))
        {
            return "暂时无法读取系统播放声音，将自动重试。";
        }

        // The capture services record LastError as "<ExceptionType>: <message>";
        // interop-casting errors mean a product bug, never a security software block.
        var separator = lastError.IndexOf(": ", StringComparison.Ordinal);
        var exceptionType = separator > 0 ? lastError[..separator] : string.Empty;
        if (exceptionType is "InvalidCastException"
            or "TypeInitializationException"
            or "TypeLoadException"
            or "MarshalDirectiveException"
            or "MissingMethodException"
            or "EntryPointNotFoundException")
        {
            return "频谱组件初始化出现内部错误，并非被安全软件阻止。";
        }

        if (exceptionType == "COMException")
        {
            if (lastError.Contains("0x80070490", StringComparison.OrdinalIgnoreCase))
            {
                return "未找到可用的播放设备，连接播放设备后会自动重试。";
            }

            if (lastError.Contains("0x88890004", StringComparison.OrdinalIgnoreCase))
            {
                return "音频设备已失效或被移除，重新选择设备后会自动重试。";
            }

            if (lastError.Contains("0x80070005", StringComparison.OrdinalIgnoreCase))
            {
                return "系统音频采集被系统或安全软件阻止。";
            }
        }

        return "暂时无法读取系统播放声音，将自动重试。";
    }

    private void PublishSpectrumRetryingState()
    {
        var payload = new
        {
            state = "waiting",
            message = "正在重新连接系统音频…"
        };
        _lastSpectrumCaptureStateJson = JsonSerializer.Serialize(payload, SettingsJsonOptions);
        Publish("spectrumCaptureState", payload);
    }

    private async Task ResetDefaultsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var defaultSettings = AppSettings.CreateDefault(_isApplicationUsingLightTheme());
        ForegroundColorPolicy.ApplySelectedMode(defaultSettings, _isSystemUiUsingLightTheme());
        CopySettings(defaultSettings, _settings);
        await SaveSettingsAndNotifyAsync();
        PublishSettingsState();
    }

    private async Task ResetLyricsLayoutBaseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _settings.FontSize = AppSettings.DefaultFontSize;
        _settings.CoverSize = AppSettings.DefaultCoverSize;
        _settings.CoverGap = AppSettings.DefaultCoverGap;
        _settings.CoverCornerRadius = AppSettings.DefaultCoverCornerRadius;
        _settings.WindowWidth = AppSettings.DefaultWindowWidth;
        await SaveSettingsAndNotifyAsync();
        PublishSettingsState();
    }

    private bool SaveSettings()
    {
        return _saveSettings(_settings.Clone());
    }

    private async Task SaveSettingsAndNotifyAsync()
    {
        var success = SaveSettings();
        Publish("settingsSaveResult", new { success });
        if (!success)
        {
            throw new IOException("媒体设置保存失败，请重试。");
        }

        await Task.CompletedTask;
    }

    private void PublishSettingsState()
    {
        Publish(
            "settingsState",
            new
            {
                settings = CreateSettingsPayload(),
                fonts = FontCatalogService.GetOptions()
            });
    }

    private void Publish(string type, object? payload)
    {
        if (_disposed)
        {
            return;
        }

        lock (_stateGate)
        {
            _events.Add(new MediaSettingsEvent(ProtocolVersion, type, payload));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();

        var discoveryCancellation = Interlocked.Exchange(ref _playerSessionDiscoveryCancellation, null);
        discoveryCancellation?.Cancel();
        discoveryCancellation?.Dispose();
        var diagnosticsCancellation = Interlocked.Exchange(ref _lyricDiagnosticsCancellation, null);
        diagnosticsCancellation?.Cancel();
        diagnosticsCancellation?.Dispose();
        var diagnosticsApplyCancellation = Interlocked.Exchange(
            ref _lyricDiagnosticsApplyCancellation,
            null);
        diagnosticsApplyCancellation?.Cancel();
        diagnosticsApplyCancellation?.Dispose();
        var diagnosticRunner = Interlocked.Exchange(ref _lyricDiagnosticRunner, null);
        diagnosticRunner?.Dispose();
        _lifetime.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private MediaSettingsPayload CreateSettingsPayload()
    {
        _settings.NormalizePlayerSources();
        _settings.NormalizeLyricsTextAlignment();
        _settings.NormalizeCoverPosition();
        _settings.NormalizeWindowLayout();
        _settings.NormalizeQuickCards();
        var layoutMetrics = CreateLyricsLayoutMetrics();
        var availableDisplays = GetDisplays();
        var targetDisplays = LyricsDisplayTargetSelector.Select(
            availableDisplays,
            _settings.LyricsDisplayMode,
            _settings.SelectedDisplayIds);
        var taskbarConstraints = TaskbarEmbeddingLayoutPolicy.FromDisplays(targetDisplays);
        var fallbackWidth = GetWorkAreaWidth();
        var inputBounds = TaskbarEmbeddingLayoutPolicy.GetInputBounds(
            _settings,
            taskbarConstraints,
            fallbackWidth);
        var payload = new MediaSettingsPayload
        {
            SourceRecognitionOrder = NormalizeSourceOrder(_settings.SourceRecognitionOrder),
            CustomPlayerSources = _settings.CustomPlayerSources.ToList(),
            EnableNetease = _settings.EnableNetease,
            EnableQQMusic = _settings.EnableQQMusic,
            EnableKugou = _settings.EnableKugou,
            EnableSpotify = _settings.EnableSpotify,
            EnableBrowser = _settings.EnableBrowser,
            PlayerLyricOffsets = _settings.PlayerSources.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.LyricOffsetMilliseconds,
                StringComparer.OrdinalIgnoreCase),
            PlayerLyricProviders = _settings.PlayerSources.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.LyricProviders,
                StringComparer.OrdinalIgnoreCase),
            DefaultPlayerLyricOffsets = _settings.PlayerSources.Keys.ToDictionary(
                source => source,
                AppSettings.GetDefaultPlayerLyricOffsetMilliseconds,
                StringComparer.OrdinalIgnoreCase),
            EnableLocalLyrics = _settings.EnableLocalLyrics,
            LocalMusicFolders = NormalizeLocalMusicFolders(_settings.LocalMusicFolders),
            ShowLyricsOnStartup = _settings.ShowLyricsOnStartup,
            AutoHideWhenNoPlayback = _settings.AutoHideWhenNoPlayback,
            StartWithWindows = _settings.StartWithWindows,
            AutoCheckUpdates = _settings.AutoCheckUpdates,
            ShowLyricTranslation = _settings.ShowLyricTranslation,
            EnableWordScanning = _settings.EnableWordScanning,
            ToolWindowTheme = _settings.ToolWindowTheme,
            QuickCardOrder = _settings.QuickCardOrder.ToList(),
            QuickCardColumns = new Dictionary<string, int>(
                _settings.QuickCardColumns ?? [],
                StringComparer.Ordinal),
            QuickCardEnabled = new Dictionary<string, bool>(
                _settings.QuickCardEnabled,
                StringComparer.Ordinal),
            QuickCardResourceMode = _settings.QuickCardResourceMode,
            CardFontFamily = _settings.CardFontFamily,
            CardMicaVivid = _settings.CardMicaVivid,
            CardMicaTintOpacity = _settings.CardMicaTintOpacity,
            CardMicaLuminosityOpacity = _settings.CardMicaLuminosityOpacity,
            CardMicaDarkTintOpacity = _settings.CardMicaDarkTintOpacity,
            CardMicaDarkLuminosityOpacity = _settings.CardMicaDarkLuminosityOpacity,
            SpectrumDisplayMode = _settings.SpectrumDisplayMode.ToString(),
            SpectrumAudioAccessGranted = _settings.SpectrumAudioAccessGranted,
            SpectrumTuning = (_settings.SpectrumTuning ?? SpectrumTuningSettings.CreateDefault()).Clone(),
            FontSize = _settings.FontSize,
            ShowCover = _settings.ShowCover,
            CoverSize = _settings.CoverSize,
            CoverGap = _settings.CoverGap,
            CoverCornerRadius = _settings.CoverCornerRadius,
            LyricsLayoutScalePercent = layoutMetrics.ScalePercent,
            EffectiveFontSize = layoutMetrics.FontSize,
            EffectiveCoverSize = layoutMetrics.CoverSize,
            EffectiveCoverGap = layoutMetrics.CoverGap,
            EffectiveCoverCornerRadius = layoutMetrics.CoverCornerRadius,
            EffectiveWindowWidth = AppSettings.ClampEffectiveWindowWidth(
                _settings.WindowWidth,
                _settings.LyricsLayoutScalePercent,
                _settings.UseFloatingWindow
                    ? fallbackWidth
                    : taskbarConstraints.MaxWidth),
            FontFamily = FontCatalogService.ResolveInstalledFamily(
                AppSettings.NormalizeFontFamily(_settings.FontFamily)) ?? AppSettings.BundledFontFamily,
            FontWeight = NormalizeFontWeight(_settings.FontWeight),
            ForegroundColorMode = _settings.ForegroundColorMode,
            ForegroundColor = _settings.ForegroundColor,
            ShowBackground = _settings.ShowBackground,
            BackgroundOpacity = _settings.BackgroundOpacity,
            ShowBorder = _settings.ShowBorder,
            ShowTextShadow = _settings.ShowTextShadow,
            WindowWidth = _settings.WindowWidth,
            HorizontalAnchor = _settings.HorizontalAnchor,
            LyricsTextAlignment = _settings.LyricsTextAlignment,
            CoverPosition = _settings.CoverPosition,
            XOffset = _settings.XOffset,
            YOffset = _settings.YOffset,
            ForceAlwaysOnTop = _settings.ForceAlwaysOnTop,
            UseFloatingWindow = _settings.UseFloatingWindow,
            TaskbarEmbeddingAvailable = inputBounds.IsSupported,
            TaskbarMaxWidth = inputBounds.MaxTaskbarWidth,
            TaskbarMaxHeight = inputBounds.MaxTaskbarHeight,
            TaskbarMaxScalePercent = inputBounds.MaxScalePercent,
            TaskbarMaxFontSize = inputBounds.MaxFontSize,
            TaskbarMaxCoverSize = inputBounds.MaxCoverSize,
            TaskbarMaxCoverGap = inputBounds.MaxCoverGap,
            TaskbarMaxWindowWidth = inputBounds.MaxWindowWidth,
            TaskbarMinXOffset = inputBounds.MinXOffset,
            TaskbarMaxXOffset = inputBounds.MaxXOffset,
            TaskbarMinYOffset = inputBounds.MinYOffset,
            TaskbarMaxYOffset = inputBounds.MaxYOffset,
            TaskbarEmbeddingStatus = _taskbarEmbeddingStatus,
            LyricsDisplayMode = _settings.LyricsDisplayMode,
            SelectedDisplayIds = _settings.SelectedDisplayIds.ToList(),
            AvailableDisplays = availableDisplays
                .Select(display => new WebDisplayMonitor
                {
                    Id = display.Id,
                    Name = display.Name,
                    IsPrimary = display.IsPrimary,
                    Width = display.Width,
                    Height = display.Height
                })
                .ToList(),
            AppVersion = UpdateChecker.GetCurrentVersion(),
            RepositoryUrl = ProductRepositoryUrl
        };
        _taskbarEmbeddingStatus = null;
        return payload;
    }

    private Dictionary<string, object> CreateLyricsLayoutPreview(bool includeInputBounds)
    {
        var metrics = CreateLyricsLayoutMetrics();
        var availableDisplays = GetDisplays();
        var targetDisplays = LyricsDisplayTargetSelector.Select(
            availableDisplays,
            _settings.LyricsDisplayMode,
            _settings.SelectedDisplayIds);
        var taskbarConstraints = TaskbarEmbeddingLayoutPolicy.FromDisplays(targetDisplays);
        return CreateLyricsLayoutPreviewPayload(
            _settings,
            metrics,
            taskbarConstraints,
            GetWorkAreaWidth(),
            includeInputBounds);
    }

    internal static Dictionary<string, object> CreateLyricsLayoutPreviewPayload(
        AppSettings settings,
        LyricsLayoutMetrics metrics,
        TaskbarEmbeddingConstraints taskbarConstraints,
        double fallbackWidth,
        bool includeInputBounds)
    {
        var preview = new Dictionary<string, object>
        {
            ["scalePercent"] = metrics.ScalePercent,
            ["fontSize"] = AppSettings.ClampFontSize(settings.FontSize),
            ["coverSize"] = AppSettings.ClampCoverSize(settings.CoverSize),
            ["coverGap"] = AppSettings.ClampCoverGap(settings.CoverGap),
            ["coverCornerRadius"] = AppSettings.ClampCoverCornerRadius(
                settings.CoverCornerRadius,
                AppSettings.ClampCoverSize(settings.CoverSize)),
            ["effectiveFontSize"] = metrics.FontSize,
            ["effectiveCoverSize"] = metrics.CoverSize,
            ["effectiveCoverGap"] = metrics.CoverGap,
            ["effectiveCoverCornerRadius"] = metrics.CoverCornerRadius,
            ["effectiveWindowWidth"] = AppSettings.ClampEffectiveWindowWidth(
                settings.WindowWidth,
                settings.LyricsLayoutScalePercent,
                settings.UseFloatingWindow || !taskbarConstraints.IsSupported
                    ? fallbackWidth
                    : taskbarConstraints.MaxWidth)
        };

        if (!includeInputBounds)
        {
            return preview;
        }

        var inputBounds = TaskbarEmbeddingLayoutPolicy.GetInputBounds(
            settings,
            taskbarConstraints,
            fallbackWidth);
        preview["taskbarEmbeddingAvailable"] = inputBounds.IsSupported;
        preview["taskbarMaxWidth"] = inputBounds.MaxTaskbarWidth;
        preview["taskbarMaxHeight"] = inputBounds.MaxTaskbarHeight;
        preview["taskbarMaxScalePercent"] = inputBounds.MaxScalePercent;
        preview["taskbarMaxFontSize"] = inputBounds.MaxFontSize;
        preview["taskbarMaxCoverSize"] = inputBounds.MaxCoverSize;
        preview["taskbarMaxCoverGap"] = inputBounds.MaxCoverGap;
        preview["taskbarMaxWindowWidth"] = inputBounds.MaxWindowWidth;
        preview["taskbarMinXOffset"] = inputBounds.MinXOffset;
        preview["taskbarMaxXOffset"] = inputBounds.MaxXOffset;
        preview["taskbarMinYOffset"] = inputBounds.MinYOffset;
        preview["taskbarMaxYOffset"] = inputBounds.MaxYOffset;
        return preview;
    }

    private LyricsLayoutMetrics CreateLyricsLayoutMetrics()
    {
        return LyricsLayoutMetrics.Create(_settings, GetPrimaryDpiScale());
    }

    private IReadOnlyList<DisplayMonitor> GetDisplays()
    {
        try
        {
            return _getDisplays() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private double GetPrimaryDpiScale()
    {
        if (_getPrimaryDpiScale is not null)
        {
            var injected = _getPrimaryDpiScale();
            return double.IsFinite(injected) && injected > 0 ? injected : 1;
        }

        var displays = GetDisplays();
        var display = displays.FirstOrDefault(item => item.IsPrimary) ?? displays.FirstOrDefault();
        return display is { PixelsPerDip: > 0 } ? display.PixelsPerDip : 1;
    }

    private double GetWorkAreaWidth()
    {
        if (_getWorkAreaWidth is not null)
        {
            var injected = _getWorkAreaWidth();
            return double.IsFinite(injected) && injected > 0
                ? injected
                : AppSettings.DefaultWindowWidth;
        }

        var displays = GetDisplays();
        var display = displays.FirstOrDefault(item => item.IsPrimary) ?? displays.FirstOrDefault();
        if (display is null)
        {
            return AppSettings.DefaultWindowWidth;
        }

        var pixelsPerDip = display.PixelsPerDip > 0 ? display.PixelsPerDip : 1;
        return display.WorkAreaWidth / pixelsPerDip;
    }

    private List<string> NormalizeSourceOrder(IEnumerable<string>? order)
    {
        var known = BuiltInPlayerSources
            .Concat(_settings.CustomPlayerSources.Select(source => source.SourceAppUserModelId))
            .ToArray();
        var result = new List<string>();

        foreach (var source in order ?? Enumerable.Empty<string>())
        {
            var match = known.FirstOrDefault(
                item => string.Equals(item, source, StringComparison.OrdinalIgnoreCase));
            if (match is not null && !result.Contains(match, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(match);
            }
        }

        foreach (var source in known)
        {
            if (!result.Contains(source, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(source);
            }
        }

        return result;
    }

    private static string NormalizeFontWeight(string? value)
    {
        return value?.Trim() switch
        {
            "Light" => "Light",
            "Normal" => "Normal",
            "Medium" => "Medium",
            "SemiBold" => "SemiBold",
            "Bold" => "Bold",
            _ => "SemiBold"
        };
    }

    private void ApplyWebSettingUpdate(string? key, JsonElement? value)
    {
        if (key is null || value is null)
        {
            return;
        }

        var element = value.Value;
        const string playerLyricOffsetPrefix = "playerLyricOffset:";
        const string playerLyricProvidersPrefix = "playerLyricProviders:";
        const string customPlayerEnabledPrefix = "customPlayerEnabled:";
        if (key.StartsWith(customPlayerEnabledPrefix, StringComparison.Ordinal))
        {
            if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                _settings.SetCustomPlayerSourceEnabled(
                    key[customPlayerEnabledPrefix.Length..],
                    element.GetBoolean());
            }
            return;
        }

        if (key.StartsWith(playerLyricProvidersPrefix, StringComparison.Ordinal))
        {
            if (TryParseLyricProviderPreferences(element, out var providers))
            {
                _settings.SetPlayerLyricProviders(
                    key[playerLyricProvidersPrefix.Length..],
                    providers);
            }
            return;
        }

        if (key.StartsWith(playerLyricOffsetPrefix, StringComparison.Ordinal))
        {
            var sourceApp = key[playerLyricOffsetPrefix.Length..];
            var offset = (int)Math.Round(ReadDouble(
                element,
                _settings.GetPlayerLyricOffsetMilliseconds(sourceApp)));
            _settings.SetPlayerLyricOffsetMilliseconds(sourceApp, offset);
            return;
        }

        switch (key)
        {
            case "spectrumTuning":
                _settings.SpectrumTuning = element.Deserialize<SpectrumTuningSettings>(
                    SettingsJsonOptions)
                    ?? throw new ArgumentException("频谱设置格式无效。");
                break;
            case "enableQQMusic":
                _settings.EnableQQMusic = ReadBool(element, _settings.EnableQQMusic);
                break;
            case "enableNetease":
                _settings.EnableNetease = ReadBool(element, _settings.EnableNetease);
                break;
            case "enableKugou":
                _settings.EnableKugou = ReadBool(element, _settings.EnableKugou);
                break;
            case "enableSpotify":
                _settings.EnableSpotify = ReadBool(element, _settings.EnableSpotify);
                break;
            case "enableBrowser":
                _settings.EnableBrowser = ReadBool(element, _settings.EnableBrowser);
                break;
            case "enableLocalLyrics":
                _settings.EnableLocalLyrics = ReadBool(element, _settings.EnableLocalLyrics);
                break;
            case "localMusicFolders":
                _settings.LocalMusicFolders = NormalizeLocalMusicFolders(ReadStringList(element));
                break;
            case "showLyricsOnStartup":
                _settings.ShowLyricsOnStartup = ReadBool(element, _settings.ShowLyricsOnStartup);
                break;
            case "autoHideWhenNoPlayback":
                _settings.AutoHideWhenNoPlayback = ReadBool(
                    element,
                    _settings.AutoHideWhenNoPlayback);
                break;
            case "startWithWindows":
                _settings.StartWithWindows = ReadBool(element, _settings.StartWithWindows);
                _settings.StartWithWindows = false;
                break;
            case "autoCheckUpdates":
                _settings.AutoCheckUpdates = ReadBool(element, _settings.AutoCheckUpdates);
                break;
            case "showLyricTranslation":
                _settings.ShowLyricTranslation = ReadBool(
                    element,
                    _settings.ShowLyricTranslation);
                break;
            case "enableWordScanning":
                _settings.EnableWordScanning = ReadBool(element, _settings.EnableWordScanning);
                break;
            case "toolWindowTheme":
                if (Enum.TryParse<ToolWindowTheme>(
                        ReadString(element, _settings.ToolWindowTheme.ToString()),
                        true,
                        out var toolWindowTheme))
                {
                    _settings.ToolWindowTheme = toolWindowTheme;
                }
                break;
            case "quickCardOrder":
                _settings.QuickCardOrder = ReadStringList(element);
                _settings.NormalizeQuickCards();
                break;
            case "quickCardColumns":
                if (element.ValueKind == JsonValueKind.Object)
                {
                    _settings.QuickCardColumns = element.EnumerateObject()
                        .Where(item =>
                            item.Value.ValueKind == JsonValueKind.Number &&
                            item.Value.TryGetInt32(out var column) &&
                            (column == 0 || column == 1))
                        .ToDictionary(
                            item => item.Name,
                            item => item.Value.GetInt32(),
                            StringComparer.Ordinal);
                    _settings.NormalizeQuickCards();
                }
                break;
            case "quickCardEnabled":
                if (element.ValueKind == JsonValueKind.Object)
                {
                    _settings.QuickCardEnabled = element.EnumerateObject()
                        .Where(item =>
                            item.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        .ToDictionary(
                            item => item.Name,
                            item => item.Value.GetBoolean(),
                            StringComparer.Ordinal);
                    _settings.NormalizeQuickCards();
                }
                break;
            case "quickCardResourceMode":
                if (Enum.TryParse<QuickCardResourceMode>(
                        ReadString(element, _settings.QuickCardResourceMode.ToString()),
                        true,
                        out var resourceMode))
                {
                    _settings.QuickCardResourceMode = resourceMode;
                }
                break;
            case "cardFontFamily":
                _settings.CardFontFamily = AppSettings.NormalizeFontFamily(
                    ReadString(element, _settings.CardFontFamily));
                break;
            case "cardMicaTintOpacity":
                _settings.CardMicaTintOpacity = Math.Clamp(
                    ReadDouble(element, _settings.CardMicaTintOpacity),
                    AppSettings.MinimumCardMicaTintOpacity,
                    AppSettings.MaximumCardMicaTintOpacity);
                break;
            case "cardMicaVivid":
                _settings.CardMicaVivid = ReadBool(element, _settings.CardMicaVivid);
                break;
            case "cardMicaLuminosityOpacity":
                _settings.CardMicaLuminosityOpacity = Math.Clamp(
                    ReadDouble(element, _settings.CardMicaLuminosityOpacity),
                    AppSettings.MinimumCardMicaLuminosityOpacity,
                    AppSettings.MaximumCardMicaLuminosityOpacity);
                break;
            case "cardMicaDarkTintOpacity":
                _settings.CardMicaDarkTintOpacity = Math.Clamp(
                    ReadDouble(element, _settings.CardMicaDarkTintOpacity),
                    AppSettings.MinimumCardMicaTintOpacity,
                    AppSettings.MaximumCardMicaTintOpacity);
                break;
            case "cardMicaDarkLuminosityOpacity":
                _settings.CardMicaDarkLuminosityOpacity = Math.Clamp(
                    ReadDouble(element, _settings.CardMicaDarkLuminosityOpacity),
                    AppSettings.MinimumCardMicaLuminosityOpacity,
                    AppSettings.MaximumCardMicaLuminosityOpacity);
                break;
            case "spectrumDisplayMode":
                var spectrumDisplayMode = ReadString(
                    element,
                    SpectrumDisplayMode.Disabled.ToString());
                if (Enum.TryParse<SpectrumDisplayMode>(
                        spectrumDisplayMode,
                        true,
                        out var parsedSpectrumDisplayMode))
                {
                    _settings.SpectrumDisplayMode =
                        parsedSpectrumDisplayMode == SpectrumDisplayMode.Disabled ||
                        _settings.SpectrumAudioAccessGranted
                            ? parsedSpectrumDisplayMode
                            : SpectrumDisplayMode.Disabled;
                }
                break;
            case "showBackground":
                _settings.ShowBackground = ReadBool(element, _settings.ShowBackground);
                break;
            case "showBorder":
                _settings.ShowBorder = ReadBool(element, _settings.ShowBorder);
                break;
            case "showTextShadow":
                _settings.ShowTextShadow = ReadBool(element, _settings.ShowTextShadow);
                break;
            case "forceAlwaysOnTop":
                _settings.ForceAlwaysOnTop = ReadBool(element, _settings.ForceAlwaysOnTop);
                break;
            case "useFloatingWindow":
                ApplyFloatingWindowSetting(ReadBool(element, _settings.UseFloatingWindow));
                break;
            case "lyricsDisplayMode":
                _settings.LyricsDisplayMode = ReadEnum(element, _settings.LyricsDisplayMode);
                break;
            case "selectedDisplayIds":
                _settings.SelectedDisplayIds = ReadStringList(element);
                _settings.NormalizeDisplaySelection();
                break;
            case "fontSize":
                _settings.FontSize = AppSettings.ClampFontSize(
                    ReadDouble(element, _settings.FontSize));
                break;
            case "showCover":
                _settings.ShowCover = ReadBool(element, _settings.ShowCover);
                break;
            case "coverSize":
                _settings.CoverSize = AppSettings.ClampCoverSize(
                    ReadDouble(element, _settings.CoverSize));
                _settings.CoverCornerRadius = AppSettings.ClampCoverCornerRadius(
                    _settings.CoverCornerRadius,
                    _settings.CoverSize);
                break;
            case "coverGap":
                _settings.CoverGap = AppSettings.ClampCoverGap(
                    ReadDouble(element, _settings.CoverGap));
                break;
            case "coverCornerRadius":
                _settings.CoverCornerRadius = AppSettings.ClampCoverCornerRadius(
                    ReadDouble(element, _settings.CoverCornerRadius),
                    _settings.CoverSize);
                break;
            case "lyricsLayoutScalePercent":
                _settings.LyricsLayoutScalePercent =
                    AppSettings.ClampLyricsLayoutScalePercent(
                        ReadDouble(element, _settings.LyricsLayoutScalePercent));
                break;
            case "fontFamily":
                _settings.FontFamily = AppSettings.NormalizeFontFamily(
                    ReadString(element, _settings.FontFamily));
                break;
            case "fontWeight":
                _settings.FontWeight = NormalizeFontWeight(
                    ReadString(element, _settings.FontWeight));
                break;
            case "foregroundColor":
                _settings.ForegroundColorMode = ForegroundColorMode.Custom;
                _settings.ForegroundColor = NormalizeColor(
                    ReadString(element, _settings.ForegroundColor));
                break;
            case "foregroundColorMode":
                _settings.ForegroundColorMode = ReadForegroundColorMode(
                    element,
                    _settings.ForegroundColorMode);
                ApplyForegroundColorMode();
                break;
            case "backgroundOpacity":
                _settings.BackgroundOpacity = Math.Clamp(
                    ReadDouble(element, _settings.BackgroundOpacity),
                    0,
                    1);
                break;
            case "windowWidth":
                _settings.WindowWidth = Math.Clamp(
                    ReadDouble(element, _settings.WindowWidth),
                    AppSettings.MinimumWindowWidth,
                    AppSettings.MaximumWindowWidth);
                break;
            case "horizontalAnchor":
                if (Enum.TryParse<LyricsHorizontalAnchor>(
                        ReadString(element, _settings.HorizontalAnchor.ToString()),
                        out var anchor))
                {
                    _settings.HorizontalAnchor = anchor;
                }
                break;
            case "lyricsTextAlignment":
                _settings.NormalizeLyricsTextAlignment();
                var textAlignment = ReadString(element, string.Empty);
                if (Enum.TryParse<LyricsTextAlignment>(
                        textAlignment,
                        ignoreCase: true,
                        out var parsedTextAlignment) &&
                    Enum.IsDefined(parsedTextAlignment))
                {
                    _settings.LyricsTextAlignment = parsedTextAlignment;
                }
                break;
            case "coverPosition":
                _settings.NormalizeCoverPosition();
                var coverPosition = ReadString(element, string.Empty);
                if (Enum.TryParse<CoverPosition>(
                        coverPosition,
                        ignoreCase: true,
                        out var parsedCoverPosition) &&
                    Enum.IsDefined(parsedCoverPosition))
                {
                    _settings.CoverPosition = parsedCoverPosition;
                }
                break;
            case "xOffset":
                _settings.XOffset = Math.Clamp(
                    ReadDouble(element, _settings.XOffset),
                    -2000,
                    2000);
                break;
            case "yOffset":
                _settings.YOffset = Math.Clamp(
                    ReadDouble(element, _settings.YOffset),
                    AppSettings.MinimumWindowOffset,
                    AppSettings.MaximumWindowOffset);
                break;
        }

        if (!_settings.UseFloatingWindow && IsTaskbarBoundedLayoutSetting(key))
        {
            ClampEmbeddedLayoutToCurrentDisplays();
        }
    }

    private void ApplyFloatingWindowSetting(bool useFloatingWindow)
    {
        if (useFloatingWindow || !_settings.UseFloatingWindow)
        {
            _settings.UseFloatingWindow = useFloatingWindow;
            return;
        }

        var constraints = GetCurrentTaskbarConstraints();
        var result = TaskbarEmbeddingLayoutPolicy.NormalizeForEmbedding(
            _settings,
            constraints);
        if (!result.CanEmbed)
        {
            _taskbarEmbeddingStatus = result.Message;
            _settings.UseFloatingWindow = true;
            return;
        }

        _settings.UseFloatingWindow = false;
        if (result.Changed)
        {
            _taskbarEmbeddingStatus = result.Message;
        }
    }

    private void ClampEmbeddedLayoutToCurrentDisplays()
    {
        TaskbarEmbeddingLayoutPolicy.ClampToConstraints(
            _settings,
            GetCurrentTaskbarConstraints());
    }

    private TaskbarEmbeddingConstraints GetCurrentTaskbarConstraints()
    {
        var displays = GetDisplays();
        var targetDisplays = LyricsDisplayTargetSelector.Select(
            displays,
            _settings.LyricsDisplayMode,
            _settings.SelectedDisplayIds);
        return TaskbarEmbeddingLayoutPolicy.FromDisplays(targetDisplays);
    }

    private static bool IsTaskbarBoundedLayoutSetting(string? key)
    {
        return key is "fontSize" or
            "showCover" or
            "coverSize" or
            "coverGap" or
            "coverCornerRadius" or
            "lyricsLayoutScalePercent" or
            "windowWidth" or
            "horizontalAnchor" or
            "xOffset" or
            "yOffset" or
            "lyricsDisplayMode" or
            "selectedDisplayIds";
    }

    private void ApplySourceOrder(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        _settings.SourceRecognitionOrder = NormalizeSourceOrder(
            value.Value.EnumerateArray()
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!));
    }

    private void ApplyForegroundColorMode()
    {
        ForegroundColorPolicy.ApplySelectedMode(
            _settings,
            _isSystemUiUsingLightTheme());
    }

    private static bool TryParseCustomPlayerSourceAddRequest(
        JsonElement? value,
        out CustomPlayerSourceAddRequest request)
    {
        request = null!;
        if (value is not { ValueKind: JsonValueKind.Object } payload ||
            !payload.TryGetProperty("sourceAppUserModelId", out var idElement) ||
            idElement.ValueKind != JsonValueKind.String ||
            !payload.TryGetProperty("displayName", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var id = idElement.GetString();
        var name = nameElement.GetString();
        if (string.IsNullOrWhiteSpace(id) ||
            id.Length > 256 ||
            id.Any(char.IsControl) ||
            name is null ||
            name.Length > 80 ||
            name.Any(char.IsControl))
        {
            return false;
        }

        string? iconDataUrl = null;
        if (payload.TryGetProperty("iconDataUrl", out var iconElement))
        {
            if (iconElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            iconDataUrl = iconElement.GetString();
        }
        if (!CustomPlayerIcon.TryNormalize(iconDataUrl, out var normalizedIcon))
        {
            return false;
        }

        string? presetIconId = null;
        string? presetIconColor = null;
        if (payload.TryGetProperty("presetIconId", out var presetIdElement))
        {
            if (presetIdElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            presetIconId = presetIdElement.GetString();
        }
        if (payload.TryGetProperty("presetIconColor", out var presetColorElement))
        {
            if (presetColorElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            presetIconColor = presetColorElement.GetString();
        }
        if (!PresetPlayerIcon.TryNormalize(
                presetIconId,
                presetIconColor,
                out var normalizedPresetId,
                out var normalizedPresetColor) ||
            (normalizedIcon.Length > 0 && normalizedPresetId.Length > 0))
        {
            return false;
        }

        var enabled = true;
        if (payload.TryGetProperty("enabled", out var enabledElement))
        {
            if (enabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return false;
            }
            enabled = enabledElement.GetBoolean();
        }

        var lyricProviders = PlayerSourceSettings.CreateDefaultLyricProviders();
        if (payload.TryGetProperty("lyricProviders", out var providersElement) &&
            !TryParseLyricProviderPreferences(providersElement, out lyricProviders))
        {
            return false;
        }

        request = new CustomPlayerSourceAddRequest(
            id.Trim(),
            name.Trim(),
            normalizedIcon,
            normalizedPresetId,
            normalizedPresetColor,
            enabled,
            lyricProviders);
        return true;
    }

    private static bool TryParseLyricProviderPreferences(
        JsonElement? value,
        out List<LyricProviderPreference> providers)
    {
        providers = [];
        if (value is not { ValueKind: JsonValueKind.Array } payload)
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<LyricProviderPreference>>(
                payload.GetRawText(),
                SettingsJsonOptions);
            if (!PlayerSourceSettings.IsValidLyricProviders(parsed))
            {
                return false;
            }

            providers = parsed!;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseLyricDiagnosticCandidateApplyRequest(
        JsonElement? value,
        out LyricDiagnosticCandidateApplyRequest request)
    {
        request = null!;
        if (value is not { ValueKind: JsonValueKind.Object } payload ||
            !payload.TryGetProperty("providerId", out var providerElement) ||
            !payload.TryGetProperty("candidateId", out var candidateElement) ||
            providerElement.ValueKind != JsonValueKind.String ||
            candidateElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var providerId = providerElement.GetString();
        var candidateId = candidateElement.GetString();
        if (string.IsNullOrWhiteSpace(providerId) ||
            string.IsNullOrWhiteSpace(candidateId))
        {
            return false;
        }

        var mode = LyricDiagnosticApplyMode.Current;
        if (payload.TryGetProperty("mode", out var modeElement))
        {
            if (modeElement.ValueKind != JsonValueKind.String ||
                !TryParseLyricDiagnosticApplyMode(modeElement.GetString(), out mode))
            {
                return false;
            }
        }

        request = new LyricDiagnosticCandidateApplyRequest(providerId, candidateId, mode);
        return true;
    }

    private static bool TryParseLyricDiagnosticApplyMode(
        string? value,
        out LyricDiagnosticApplyMode mode)
    {
        mode = value switch
        {
            "current" => LyricDiagnosticApplyMode.Current,
            "remember" => LyricDiagnosticApplyMode.Remember,
            _ => default
        };
        return value is "current" or "remember";
    }

    public JsonElement PreviewServiceSettings(AppSettings current, JsonElement patch)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(current);
        if (patch.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("设置格式无效。");
        }

        lock (_stateGate)
        {
            CopySettings(current, _settings);
            foreach (var field in patch.EnumerateObject())
            {
                ApplyWebSettingUpdate(field.Name, field.Value);
            }

            _previewSettings?.Invoke(_settings.Clone());
            return JsonSerializer.SerializeToElement(
                CreateSettingsPayload(),
                SettingsJsonOptions);
        }
    }

    private static bool ReadBool(JsonElement element, bool fallback)
    {
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(element.GetString(), out var value) => value,
            _ => fallback
        };
    }

    private static double ReadDouble(JsonElement element, double fallback)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDouble(out var value) && double.IsFinite(value) => value,
            JsonValueKind.String when double.TryParse(
                    element.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var value) &&
                double.IsFinite(value) => value,
            _ => fallback
        };
    }

    private static string ReadString(JsonElement element, string fallback)
    {
        return element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? fallback
            : fallback;
    }

    private static TEnum ReadEnum<TEnum>(JsonElement element, TEnum fallback)
        where TEnum : struct, Enum
    {
        return element.ValueKind == JsonValueKind.String &&
            Enum.TryParse<TEnum>(element.GetString(), ignoreCase: true, out var value)
                ? value
                : fallback;
    }

    private static List<string> ReadStringList(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToList();
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString()?
                .Split(
                    ['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList() ?? [];
        }

        return [];
    }

    private static List<string> NormalizeLocalMusicFolders(IEnumerable<string>? folders)
    {
        return (folders ?? Enumerable.Empty<string>())
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Select(folder => folder.Trim().Trim('"'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeColor(string color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return AppSettings.LightForegroundColor;
        }

        var trimmed = color.Trim();
        return trimmed.Length == 7 && trimmed.StartsWith('#')
            ? $"#FF{trimmed[1..]}"
            : trimmed;
    }

    private static ForegroundColorMode ReadForegroundColorMode(
        JsonElement element,
        ForegroundColorMode fallback)
    {
        if (element.ValueKind == JsonValueKind.String &&
            Enum.TryParse<ForegroundColorMode>(element.GetString(), out var stringValue))
        {
            return stringValue;
        }

        if (element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt32(out var intValue) &&
            Enum.IsDefined(typeof(ForegroundColorMode), intValue))
        {
            return (ForegroundColorMode)intValue;
        }

        return fallback;
    }

    private static void CopySettings(AppSettings source, AppSettings target)
    {
        target.SourceRecognitionOrder = source.SourceRecognitionOrder.ToList();
        target.CustomPlayerSources = source.CustomPlayerSources.ToList();
        target.EnableNetease = source.EnableNetease;
        target.EnableQQMusic = source.EnableQQMusic;
        target.EnableKugou = source.EnableKugou;
        target.EnableSpotify = source.EnableSpotify;
        target.EnableBrowser = source.EnableBrowser;
        source.NormalizePlayerSources();
        target.PlayerSources = source.PlayerSources.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone(),
            StringComparer.OrdinalIgnoreCase);
        target.EnableLocalLyrics = source.EnableLocalLyrics;
        target.LocalMusicFolders = NormalizeLocalMusicFolders(source.LocalMusicFolders);
        target.ShowLyricsOnStartup = source.ShowLyricsOnStartup;
        target.AutoHideWhenNoPlayback = source.AutoHideWhenNoPlayback;
        target.StartWithWindows = source.StartWithWindows;
        target.AutoCheckUpdates = source.AutoCheckUpdates;
        target.ProductAutomaticUpdatesEnabled = source.ProductAutomaticUpdatesEnabled;
        target.LastUpdateCheckUtc = source.LastUpdateCheckUtc;
        target.LastNotifiedUpdateVersion = source.LastNotifiedUpdateVersion;
        target.ShowLyricTranslation = source.ShowLyricTranslation;
        target.EnableWordScanning = source.EnableWordScanning;
        target.ToolWindowTheme = source.ToolWindowTheme;
        target.QuickCardOrder = source.QuickCardOrder.ToList();
        target.QuickCardColumns = new Dictionary<string, int>(
            source.QuickCardColumns ?? [],
            StringComparer.Ordinal);
        target.QuickCardEnabled = new Dictionary<string, bool>(
            source.QuickCardEnabled,
            StringComparer.Ordinal);
        target.QuickCardResourceMode = source.QuickCardResourceMode;
        target.CardFontFamily = source.CardFontFamily;
        target.CardMicaVivid = source.CardMicaVivid;
        target.CardMicaTintOpacity = source.CardMicaTintOpacity;
        target.CardMicaLuminosityOpacity = source.CardMicaLuminosityOpacity;
        target.CardMicaDarkTintOpacity = source.CardMicaDarkTintOpacity;
        target.CardMicaDarkLuminosityOpacity = source.CardMicaDarkLuminosityOpacity;
        target.SpectrumDisplayMode = source.SpectrumDisplayMode;
        target.SpectrumAudioAccessGranted = source.SpectrumAudioAccessGranted;
        target.SpectrumTuning = (source.SpectrumTuning ?? SpectrumTuningSettings.CreateDefault()).Clone();
        target.UseSafeFontSizeRange = source.UseSafeFontSizeRange;
        target.FontSize = source.FontSize;
        target.UseSafeCoverSizeRange = source.UseSafeCoverSizeRange;
        target.ShowCover = source.ShowCover;
        target.CoverSize = source.CoverSize;
        target.CoverGap = source.CoverGap;
        target.CoverCornerRadius = source.CoverCornerRadius;
        target.LyricsLayoutScalePercent = source.LyricsLayoutScalePercent;
        target.FontFamily = source.FontFamily;
        target.FontWeight = source.FontWeight;
        target.ForegroundColorMode = source.ForegroundColorMode;
        target.ForegroundColor = source.ForegroundColor;
        target.ShowBackground = source.ShowBackground;
        target.BackgroundOpacity = source.BackgroundOpacity;
        target.ShowBorder = source.ShowBorder;
        target.ShowTextShadow = source.ShowTextShadow;
        target.WindowWidth = source.WindowWidth;
        target.HorizontalAnchor = source.HorizontalAnchor;
        target.LyricsTextAlignment = source.LyricsTextAlignment;
        target.CoverPosition = source.CoverPosition;
        target.XOffset = source.XOffset;
        target.YOffset = source.YOffset;
        target.ForceAlwaysOnTop = source.ForceAlwaysOnTop;
        target.UseFloatingWindow = source.UseFloatingWindow;
        target.LyricsDisplayMode = source.LyricsDisplayMode;
        target.SelectedDisplayIds = source.SelectedDisplayIds.ToList();
    }

    private enum LyricDiagnosticApplyMode
    {
        Current,
        Remember
    }

    private sealed record LyricDiagnosticCandidateApplyRequest(
        string ProviderId,
        string CandidateId,
        LyricDiagnosticApplyMode Mode);

    private sealed record CustomPlayerSourceAddRequest(
        string SourceAppUserModelId,
        string DisplayName,
        string IconDataUrl,
        string PresetIconId,
        string PresetIconColor,
        bool Enabled,
        List<LyricProviderPreference> LyricProviders);

    private sealed record MediaSettingsEvent(int Version, string Type, object? Payload);

    private sealed class CurrentTrackOffsetPayload
    {
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string SourceApp { get; set; } = string.Empty;
        public string LyricSource { get; set; } = string.Empty;
        public int DurationSeconds { get; set; }
        public bool LyricSourceReady { get; set; }
        public int PlayerOffsetMilliseconds { get; set; }
        public int TrackOffsetMilliseconds { get; set; }
        public int EffectiveOffsetMilliseconds { get; set; }
    }

    private sealed class TrackOffsetQueryPayload
    {
        public int RequestId { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 50;
        public string Search { get; set; } = string.Empty;
        public string LyricSource { get; set; } = "All";
        public string Sort { get; set; } = "updated";
    }

    private sealed class TrackOffsetEntriesPagePayload
    {
        public int RequestId { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int PageCount { get; set; }
        public int TotalCount { get; set; }
        public int UnfilteredCount { get; set; }
        public List<string> LyricSources { get; set; } = new();
        public List<TrackOffsetEntryPayload> Entries { get; set; } = new();
    }

    private sealed class TrackOffsetEntryPayload
    {
        public TrackLyricOffsetRecordKey Key { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string SourceApp { get; set; } = string.Empty;
        public string LyricSource { get; set; } = string.Empty;
        public int DurationSeconds { get; set; }
        public int OffsetMilliseconds { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
    }

    private sealed class TrackOffsetMutationPayload
    {
        public TrackLyricOffsetRecordKey? Key { get; set; }
        public int OffsetMilliseconds { get; set; }
    }

    private sealed class MediaSettingsPayload
    {
        public List<string> SourceRecognitionOrder { get; set; } = new();
        public List<CustomPlayerSource> CustomPlayerSources { get; set; } = [];
        public bool EnableNetease { get; set; }
        public bool EnableQQMusic { get; set; }
        public bool EnableKugou { get; set; }
        public bool EnableSpotify { get; set; }
        public bool EnableBrowser { get; set; }
        public Dictionary<string, int> PlayerLyricOffsets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<LyricProviderPreference>> PlayerLyricProviders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> DefaultPlayerLyricOffsets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool EnableLocalLyrics { get; set; }
        public List<string> LocalMusicFolders { get; set; } = new();
        public bool ShowLyricsOnStartup { get; set; }
        public bool AutoHideWhenNoPlayback { get; set; }
        public bool StartWithWindows { get; set; }
        public bool AutoCheckUpdates { get; set; }
        public bool ShowLyricTranslation { get; set; }
        public bool EnableWordScanning { get; set; }
        public ToolWindowTheme ToolWindowTheme { get; set; }
        public List<string> QuickCardOrder { get; set; } = [];
        public Dictionary<string, int> QuickCardColumns { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, bool> QuickCardEnabled { get; set; } = new(StringComparer.Ordinal);
        public QuickCardResourceMode QuickCardResourceMode { get; set; }
        public string CardFontFamily { get; set; } = AppSettings.DefaultFontFamily;
        public bool CardMicaVivid { get; set; } = AppSettings.DefaultCardMicaVivid;
        public double CardMicaTintOpacity { get; set; } = AppSettings.DefaultCardMicaTintOpacity;
        public double CardMicaLuminosityOpacity { get; set; } = AppSettings.DefaultCardMicaLuminosityOpacity;
        public double CardMicaDarkTintOpacity { get; set; } = AppSettings.DefaultCardMicaDarkTintOpacity;
        public double CardMicaDarkLuminosityOpacity { get; set; } = AppSettings.DefaultCardMicaDarkLuminosityOpacity;
        public string SpectrumDisplayMode { get; set; } = TaskbarLyrics.App.SpectrumDisplayMode.Disabled.ToString();
        public bool SpectrumAudioAccessGranted { get; set; }
        public SpectrumTuningSettings SpectrumTuning { get; set; } = new();
        public double FontSize { get; set; }
        public bool ShowCover { get; set; }
        public double CoverSize { get; set; }
        public double CoverGap { get; set; }
        public double CoverCornerRadius { get; set; }
        public double LyricsLayoutScalePercent { get; set; }
        public double EffectiveFontSize { get; set; }
        public double EffectiveCoverSize { get; set; }
        public double EffectiveCoverGap { get; set; }
        public double EffectiveCoverCornerRadius { get; set; }
        public double EffectiveWindowWidth { get; set; }
        public string FontFamily { get; set; } = "";
        public string FontWeight { get; set; } = "";
        public ForegroundColorMode ForegroundColorMode { get; set; }
        public string ForegroundColor { get; set; } = "";
        public bool ShowBackground { get; set; }
        public double BackgroundOpacity { get; set; }
        public bool ShowBorder { get; set; }
        public bool ShowTextShadow { get; set; }
        public double WindowWidth { get; set; }
        public LyricsHorizontalAnchor HorizontalAnchor { get; set; }
        public LyricsTextAlignment LyricsTextAlignment { get; set; }
        public CoverPosition CoverPosition { get; set; }
        public double XOffset { get; set; }
        public double YOffset { get; set; }
        public bool ForceAlwaysOnTop { get; set; }
        public bool UseFloatingWindow { get; set; }
        public bool TaskbarEmbeddingAvailable { get; set; }
        public double TaskbarMaxWidth { get; set; }
        public double TaskbarMaxHeight { get; set; }
        public double TaskbarMaxScalePercent { get; set; }
        public double TaskbarMaxFontSize { get; set; }
        public double TaskbarMaxCoverSize { get; set; }
        public double TaskbarMaxCoverGap { get; set; }
        public double TaskbarMaxWindowWidth { get; set; }
        public double TaskbarMinXOffset { get; set; }
        public double TaskbarMaxXOffset { get; set; }
        public double TaskbarMinYOffset { get; set; }
        public double TaskbarMaxYOffset { get; set; }
        public string? TaskbarEmbeddingStatus { get; set; }
        public LyricsDisplayMode LyricsDisplayMode { get; set; }
        public List<string> SelectedDisplayIds { get; set; } = [];
        public List<WebDisplayMonitor> AvailableDisplays { get; set; } = [];
        public string AppVersion { get; set; } = "";
        public string RepositoryUrl { get; set; } = "";
    }

    private sealed class WebDisplayMonitor
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool IsPrimary { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }
}
