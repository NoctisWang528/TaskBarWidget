using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TaskbarLyrics.App;
using TaskbarLyrics.Core.Models;
using TaskbarLyrics.Core.Services;
using TaskbarLyrics.Core.Utilities;

namespace TaskBarWidgets.Media.Integration;

internal sealed class MediaCoordinator : IDisposable
{
    internal readonly AppCompositionRoot Composition = new();
    internal readonly TrackLyricOffsetStore Offsets = new();
    internal SmtcMusicSessionProvider Provider { get; }
    internal LyricSyncService Lyrics { get; private set; }

    private readonly SystemAudioSpectrumService _spectrum = new();
    private readonly PlaybackSnapshotStabilityGate _stability = new();
    private readonly CoverVisualTransitionState _coverTransition = new();
    private readonly CoverFallbackIconResolver _fallbackIcon = new();
    private readonly Dispatcher _dispatcher;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly MediaRenderScheduler _scheduler;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _spectrumTimer;
    private readonly CancellationTokenSource _stop = new();
    private readonly MediaPresentationState _presentation = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly ConcurrentDictionary<string, Task<string>> _sourceIcons =
        new(StringComparer.OrdinalIgnoreCase);

    private LocalMediaCoverProvider? _localCover;
    private AppSettings _settings = new();
    private SpectrumTuningSettings _tuning = SpectrumTuningSettings.CreateDefault();
    private PlaybackSnapshot _playback = new(false, TimeSpan.Zero, null);
    private MediaStyleFrame _style;
    private MediaSpectrumTuningFrame _tuningFrame;
    private MediaCoverFrame _coverFrame;
    private MediaFrame _frame;
    private CardFrame _card;
    private JsonElement _nativeAppearance = NativeAppearanceJson.Empty;
    private float[] _spectrumBars = [];
    private bool _busy;
    private bool _disposed;
    private bool _started;
    private bool _taskbarVisible;
    private bool _cardVisible;
    private bool _spectrumActive;
    private int _spectrumDiagnosticTicks;
    private int _spectrumDiagnosticReports;
    private bool _preview;
    private bool _showSpectrum;
    private LyricsPresentationScene _taskbarScene = LyricsPresentationScene.NoPlayback;
    private LyricsPresentationScene _cardScene = LyricsPresentationScene.NoPlayback;
    private long _stateVersion;
    private long _authorityVersion;
    private long _playbackAuthorityVersion;
    private DateTimeOffset _lastAuthorityUtc;
    private int _presentationFailureCount;
    private int _invalidationPosted;
    private string _coverData = "";
    private string _coverIcon = "";
    private string _coverTrackId = "";
    private byte[]? _coverBytes;
    private BitmapSource? _coverBitmap;
    private string? _localCoverIdentity;
    private DateTimeOffset _nextLocalCover;

    internal event Action<object>? FrameReady;
    internal event Action<object>? CardFrameReady;

    internal object Snapshot() => Volatile.Read(ref _frame);
    internal object CardSnapshot => Volatile.Read(ref _card);
    internal string CoverDataUrl => _coverData;
    internal MediaControlState ControlState { get; private set; } =
        new("没有媒体", "", "", 0, 0, false, false, false, false, false);
    internal CurrentTrackLyricsContext? CurrentTrackContext => LyricsInput(_playback).Track is not { } track
        ? null
        : new(track, Lyrics.CurrentLyricSourceApp ?? "");

    internal JsonElement NativeAppearance
    {
        get => _nativeAppearance;
        set
        {
            ApplyNativeAppearance(value);
        }
    }

    internal void ApplyNativeAppearance(JsonElement appearance)
    {
        var normalized = NativeAppearanceJson.Normalize(appearance);
        RunOnDispatcher(() =>
        {
            if (_disposed || NativeAppearanceJson.Equivalent(_nativeAppearance, normalized)) return;
            _nativeAppearance = normalized;
            Interlocked.Increment(ref _stateVersion);
            _scheduler.RequestState();
            _scheduler.RequestPresentation();
            ScheduleNext();
        });
    }

    internal MediaCoordinator()
        : this(new SmtcMusicSessionProvider())
    {
    }

    internal MediaCoordinator(SmtcMusicSessionProvider provider)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _dispatcher = Dispatcher.CurrentDispatcher;
        _utcNow = () => DateTimeOffset.UtcNow;
        _scheduler = new MediaRenderScheduler(_utcNow);
        Lyrics = Composition.CreateLyricSyncService(_settings, Offsets);
        _style = NativeMediaPayloadFactory.CreateStyle(_settings, 1);
        _tuningFrame = NativeMediaPayloadFactory.CreateSpectrumTuning(_tuning);
        _coverFrame = NativeMediaPayloadFactory.EmptyCover();

        var initialSnapshot = new PlaybackSnapshot(false, TimeSpan.Zero, null);
        var initialDisplay = new LyricDisplayFrame("", "", "", 0, -1);
        var initialLyrics = LyricsPresentationCalculator.Create(
            initialSnapshot,
            initialDisplay,
            _settings,
            LyricsPresentationScene.NoPlayback,
            isBrowser: false,
            LyricAcquisitionKind.Unknown);
        var initial = _presentation.Apply(new MediaPresentationInput(
            initialSnapshot,
            IsBrowser: false,
            TrackId: "",
            initialLyrics,
            LyricsPresentationScene.NoPlayback,
            _style,
            _coverFrame,
            new MediaBrowserFrame(false, "", "", "", false, false, false, false),
            _tuningFrame,
            [],
            _nativeAppearance));
        _frame = initial.Frame;
        _card = initial.Card;

        _timer = new DispatcherTimer(DispatcherPriority.Background);
        _timer.Tick += Tick;
        _spectrumTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Clamp(_tuning.UpdateIntervalMs, 16, 100))
        };
        _spectrumTimer.Tick += SpectrumTick;
        Provider.Invalidated += OnProviderInvalidated;
    }

    internal void Start()
    {
        RunOnDispatcher(() =>
        {
            if (_disposed || _started) return;
            _started = true;
            _scheduler.Initialize();
            ScheduleNext();
        });
    }

    internal string SourceIcon(MediaSourceChoice source)
    {
        if (!_sourceIcons.TryGetValue(source.Id, out var pending))
        {
            if (_sourceIcons.Count >= 64) _sourceIcons.Clear();
            var settings = _settings.Clone();
            pending = Task.Run(() =>
            {
                try
                {
                    // Never borrow the icon of a different, currently audible application.
                    var icon = PlayerIconResolver.ResolveDataUrl(
                        source.Id,
                        allowActiveSessionFallback: false);
                    return icon.Length > 0
                        ? icon
                        : PlayerSourceBadgeResolver.Resolve(source.Name, settings).IconDataUri;
                }
                catch
                {
                    return "";
                }
            });
            _sourceIcons[source.Id] = pending;
        }
        return pending.IsCompletedSuccessfully ? pending.Result : "";
    }

    internal static bool IsBrowser(TrackInfo? track) =>
        string.Equals(track?.SourceApp, "Browser", StringComparison.OrdinalIgnoreCase);

    internal static PlaybackSnapshot LyricsInput(PlaybackSnapshot snapshot) =>
        NonLyricsMediaPolicy.IsNonLyrics(snapshot) ? snapshot with { Track = null } : snapshot;

    internal void ApplySettings(AppSettings settings)
    {
        RunOnDispatcher(() =>
        {
            if (_disposed) return;
            var previous = _settings;
            _settings = settings.Clone();
            _settings.UseFloatingWindow = false;
            _style = NativeMediaPayloadFactory.CreateStyle(_settings, 1);
            var changes = AppSettingsChangeSet.Create(previous, _settings);
            Provider.SetRecognitionOrder(
                _settings.SourceRecognitionOrder,
                Composition.GetEnabledPlayerSources(_settings),
                _settings.CustomPlayerSources
                    .Select(source => source.SourceAppUserModelId)
                    .ToArray());

            if (changes.LyricSyncServiceChanged)
            {
                Lyrics.Dispose();
                Lyrics = Composition.CreateLyricSyncService(_settings, Offsets);
            }
            if (changes.LocalMediaLibraryChanged)
            {
                _localCover?.Dispose();
                _localCover = Composition.CreateLocalMediaCoverProvider(_settings);
                _localCoverIdentity = null;
            }

            Interlocked.Increment(ref _stateVersion);
            Interlocked.Increment(ref _authorityVersion);
            _scheduler.RequestState(full: true);
            UpdateSpectrum(_showSpectrum);
            ScheduleNext();
        });
    }

    internal void SetVisible(bool visible)
    {
        RunOnDispatcher(() =>
        {
            if (_disposed || _taskbarVisible == visible) return;
            _taskbarVisible = visible;
            _scheduler.SetVisibility(_taskbarVisible, _cardVisible);
            UpdatePresentationActivity();
            if (visible)
            {
                MediaPublicationPolicy.PublishFrameOnReveal(
                    _frame,
                    FrameReady);
            }
            UpdateSpectrum(_showSpectrum);
            ScheduleNext();
        });
    }

    internal void SetCardVisible(bool visible)
    {
        RunOnDispatcher(() =>
        {
            if (_disposed || _cardVisible == visible) return;
            _cardVisible = visible;
            _scheduler.SetVisibility(_taskbarVisible, _cardVisible);
            UpdatePresentationActivity();
            if (visible)
            {
                MediaPublicationPolicy.PublishCardOnReveal(
                    _card,
                    CardFrameReady);
            }
            ScheduleNext();
        });
    }

    internal void SetPreview(bool preview)
    {
        RunOnDispatcher(() =>
        {
            if (_disposed || _preview == preview) return;
            _preview = preview;
            UpdateSpectrum(_showSpectrum);
            ScheduleNext();
        });
    }

    internal void RetrySpectrum()
    {
        RunOnDispatcher(() =>
        {
            if (_disposed) return;
            _spectrum.Stop();
            _spectrumActive = false;
            _spectrumBars = [];
            UpdateSpectrum(_showSpectrum);
            _scheduler.RequestPresentation();
            ScheduleNext();
        });
    }

    internal void RefreshPresentation()
    {
        RunOnDispatcher(() =>
        {
            if (_disposed) return;
            _scheduler.RequestPresentation();
            ScheduleNext();
        });
    }

    internal void ApplyTuning(SpectrumTuningSettings tuning)
    {
        RunOnDispatcher(() =>
        {
            if (_disposed) return;
            _tuning = tuning.Clone();
            _tuning.UpdateIntervalMs = Math.Clamp(_tuning.UpdateIntervalMs, 16, 100);
            _spectrumTimer.Interval = TimeSpan.FromMilliseconds(_tuning.UpdateIntervalMs);
            _spectrum.ApplyTuning(_tuning);
            _tuningFrame = NativeMediaPayloadFactory.CreateSpectrumTuning(_tuning);
            Interlocked.Increment(ref _stateVersion);
            Interlocked.Increment(ref _authorityVersion);
            _scheduler.RequestState(full: true);
            _scheduler.RequestPresentation();
            ScheduleNext();
        });
    }

    private void OnProviderInvalidated()
    {
        if (_disposed || Interlocked.Exchange(ref _invalidationPosted, 1) != 0) return;
        Interlocked.Increment(ref _stateVersion);
        Interlocked.Increment(ref _authorityVersion);
        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                Volatile.Write(ref _invalidationPosted, 0);
                if (_disposed) return;
                _scheduler.RequestState(full: true);
                _scheduler.RequestPresentation();
                ScheduleNext();
            }));
        }
        catch (InvalidOperationException)
        {
            Volatile.Write(ref _invalidationPosted, 0);
        }
    }

    private async void Tick(object? sender, EventArgs args)
    {
        if (_disposed || _busy) return;
        var plan = _scheduler.Poll();
        if (plan.Kind == MediaWorkKind.None)
        {
            ScheduleNext();
            return;
        }

        _busy = true;
        var held = false;
        try
        {
            if (plan.Kind == MediaWorkKind.Presentation)
            {
                try
                {
                    await ApplyPresentationTickAsync(_stop.Token);
                    _presentationFailureCount = 0;
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException)
                {
                    _presentationFailureCount++;
                    if (_presentationFailureCount <= 3)
                    {
                        TaskbarLyrics.Core.Utilities.Log.Warn(
                            $"Media presentation tick failed: {exception.Message}");
                    }
                }
            }
            else
            {
                held = await RefreshPresentationAsync(
                    forceMetadataRefresh: plan.ForceMetadataRefresh,
                    _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception exception)
        {
            Log.Warn($"Media coordinator: {exception}");
            UpdateSpectrum(false);
        }
        finally
        {
            _scheduler.MarkCompleted(plan);
            if (held)
            {
                _scheduler.RequestPresentation();
                _scheduler.DeferState(
                    PlaybackSnapshotStabilityGate.QuietPeriod,
                    plan.StateRequestGeneration);
            }
            _busy = false;
            ScheduleNext();
        }
    }

    private async Task<bool> RefreshPresentationAsync(
        bool forceMetadataRefresh,
        CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            return await RefreshPresentationCoreAsync(
                forceMetadataRefresh,
                cancellationToken);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<bool> RefreshPresentationCoreAsync(
        bool forceMetadataRefresh,
        CancellationToken cancellationToken)
    {
        var stateVersion = Volatile.Read(ref _stateVersion);
        var authorityVersion = Volatile.Read(ref _authorityVersion);
        var snapshot = await Provider.GetCurrentAsync(forceMetadataRefresh, cancellationToken);
        if (_disposed) return false;

        // Re-observe the provider's transition boundary before deriving presentation state.
        snapshot = Provider.RefreshPlaybackState(snapshot);
        var decision = _stability.Observe(
            snapshot,
            PlaybackInputPolicy.Classify(snapshot),
            _utcNow());
        if (decision.Action == PlaybackSnapshotGateAction.Hold) return true;

        _playback = snapshot = decision.Snapshot;
        _lastAuthorityUtc = _utcNow();
        var track = snapshot.Track;
        var isBrowser = NonLyricsMediaPolicy.IsNonLyrics(snapshot);
        var trackId = track is null ? "" : LyricSyncService.BuildStableTrackIdentity(track);
        var lyricsService = Lyrics;

        // Start or advance lyric acquisition before cover I/O, while keeping
        // browser and other non-lyric media on a strict no-query path.
        await lyricsService.GetDisplayFrameAsync(LyricsInput(snapshot));
        if (_disposed ||
            stateVersion != Volatile.Read(ref _stateVersion) ||
            authorityVersion != Volatile.Read(ref _authorityVersion)) return false;
        _scheduler.SetPlaybackState(snapshot.IsPlaying);

        await UpdateCoverAsync(snapshot, cancellationToken);
        if (_disposed ||
            stateVersion != Volatile.Read(ref _stateVersion) ||
            authorityVersion != Volatile.Read(ref _authorityVersion)) return false;

        var latest = Provider.RefreshPlaybackState(snapshot);
        _playback = latest;
        if (!CanPublishSnapshot(snapshot, latest))
        {
            UpdateSpectrum(false);
            _scheduler.RequestState();
            return false;
        }
        snapshot = latest;

        var display = await lyricsService.GetDisplayFrameAsync(LyricsInput(snapshot));
        // Settings and commands invalidate in-flight presentation calculations.
        if (_disposed ||
            stateVersion != Volatile.Read(ref _stateVersion) ||
            authorityVersion != Volatile.Read(ref _authorityVersion)) return false;
        var scene = LyricsPresentationCalculator.ResolveScene(
            _settings,
            display,
            isBrowser,
            snapshot.Track is not null,
            lyricsService.CurrentLyricAcquisition);
        var cardScene = LyricsPresentationCalculator.ResolveCardScene(
            _settings,
            display,
            isBrowser,
            snapshot.Track is not null,
            lyricsService.CurrentLyricAcquisition);
        _taskbarScene = scene;
        _cardScene = cardScene;
        _showSpectrum = scene == LyricsPresentationScene.Spectrum;
        UpdateSpectrum(_showSpectrum);
        // Keep ticking while a lyric search can complete asynchronously, but never
        // for browser/Douyin/message-only playback. A visible card keeps its own
        // lyric timeline running even when the taskbar shows the spectrum.
        UpdatePresentationActivity();

        var lyrics = LyricsPresentationCalculator.Create(
            snapshot,
            display,
            _settings,
            scene,
            isBrowser,
            lyricsService.CurrentLyricAcquisition);
        var capabilities = Provider.GetControlCapabilities();
        var sources = Provider.GetSourceChoices();
        ControlState = new MediaControlState(
            snapshot.Track?.Title ?? "没有媒体",
            snapshot.Track?.Artist ?? "",
            NonLyricsMediaPolicy.IsDouyin(snapshot)
                ? "抖音"
                : snapshot.Track?.SourceApp ?? "",
            snapshot.Position.TotalSeconds,
            snapshot.Track?.Duration.TotalSeconds ?? 0,
            snapshot.IsPlaying,
            capabilities.Previous,
            capabilities.Next,
            capabilities.PlayPause,
            snapshot.CanSeek,
            CreateCover(),
            sources,
            snapshot.SourceAppUserModelId);

        Provider.SetCurrentLyricSource(lyricsService.CurrentLyricSourceApp);
        Provider.SetCurrentLyricOffsets(
            _settings.GetPlayerLyricOffsetMilliseconds(snapshot.Track?.SourceApp),
            Offsets.GetOffsetMilliseconds(snapshot.Track, lyricsService.CurrentLyricSourceApp));

        var browser = new MediaBrowserFrame(
            isBrowser,
            snapshot.Track?.Title ?? "",
            snapshot.Track?.Artist ?? "",
            NonLyricsMediaPolicy.IsDouyin(snapshot)
                ? "抖音"
                : snapshot.SourceAppUserModelId,
            snapshot.IsPlaying,
            capabilities.Previous,
            capabilities.Next,
            capabilities.PlayPause);
        var update = _presentation.Apply(new MediaPresentationInput(
            snapshot,
            isBrowser,
            trackId,
            lyrics,
            cardScene,
            _style,
            _coverFrame,
            browser,
            _tuningFrame,
            _spectrumActive ? _spectrumBars : [],
            _nativeAppearance));
        Volatile.Write(ref _frame, update.Frame);
        Volatile.Write(ref _card, update.Card);
        Volatile.Write(ref _playbackAuthorityVersion, authorityVersion);
        _scheduler.SetPlaybackState(snapshot.IsPlaying);

        MediaPublicationPolicy.PublishFrame(update, _taskbarVisible, FrameReady);
        MediaPublicationPolicy.PublishCard(update, _cardVisible, CardFrameReady);
        // Timeline changes can move the lyrics before another SMTC event arrives.
        // Keep authoritative reads low-frequency, then smooth only within the
        // current lyric line's own interval.
        return lyricsService.CurrentLyricAcquisition == LyricAcquisitionKind.Searching;
    }

    internal static bool CanPublishSnapshot(
        PlaybackSnapshot prepared,
        PlaybackSnapshot latest) =>
        string.Equals(
            prepared.SourceAppUserModelId,
            latest.SourceAppUserModelId,
            StringComparison.OrdinalIgnoreCase) &&
        prepared.IsPlaying == latest.IsPlaying &&
        (prepared.Track is null
            ? latest.Track is null
            : latest.Track is not null &&
              LyricSyncService.BuildStableTrackIdentity(prepared.Track) ==
              LyricSyncService.BuildStableTrackIdentity(latest.Track));

    // Fast lyric scheduling follows every surface that actually renders lyrics.
    private void UpdatePresentationActivity() =>
        _scheduler.SetPresentationActive(MediaPresentationActivity.ShouldAdvancePresentation(
            _taskbarVisible,
            _taskbarScene,
            _cardVisible,
            _cardScene));

    private void UpdateSpectrum(bool spectrumContentVisible)
    {
        if (_disposed) return;
        var shouldCapture = _playback.IsPlaying &&
            SpectrumCapturePolicy.ShouldCapture(
                _settings.SpectrumAudioAccessGranted,
                _preview,
                _taskbarVisible,
                spectrumContentVisible);
        if (shouldCapture == _spectrumActive) return;

        if (_spectrumDiagnosticReports < 8)
            Log.Diagnostic("SPECTRUM", $"active={shouldCapture} visible={_taskbarVisible} playing={_playback.IsPlaying} scene={spectrumContentVisible}");
        _spectrumActive = shouldCapture;
        if (shouldCapture)
        {
            _spectrumDiagnosticTicks = 0;
            _spectrum.Start();
            _spectrumTimer.Start();
        }
        else
        {
            _spectrumTimer.Stop();
            _spectrum.Stop();
            _spectrumBars = [];
            _scheduler.RequestPresentation();
        }
    }

    private void SpectrumTick(object? sender, EventArgs args)
    {
        if (_disposed || !_spectrumActive) return;
        var bars = _spectrum.GetSpectrum();
        _spectrumBars = bars;
        var capture = _spectrum.GetDiagnostics();
        if (++_spectrumDiagnosticTicks == 60 && _spectrumDiagnosticReports++ < 8)
            Log.Diagnostic("SPECTRUM", $"available={capture.IsAvailable} visible={_taskbarVisible} playing={_playback.IsPlaying} inputPeak={capture.InputPeak:F4} barPeak={(bars.Length == 0 ? 0 : bars.Max()):F4} error={capture.LastError}");
        SpectrumDiagnosticsState.Update(new(
            _utcNow(),
            true,
            _playback.IsPlaying,
            capture.IsAvailable,
            capture.SampleRate,
            capture.Channels,
            capture.Format,
            capture.InputPeak,
            bars.Length == 0 ? 0 : bars.Max(),
            bars,
            capture.LastAudioUtc,
            capture.LastError));

        var updated = _presentation.UpdateSpectrum(bars);
        if (updated is null) return;
        Volatile.Write(ref _frame, updated);
        MediaPublicationPolicy.PublishSpectrumFrame(
            updated,
            _taskbarVisible || _preview,
            FrameReady);
    }

    private async Task UpdateCoverAsync(
        PlaybackSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var identity = CoverIdentity.FromTrack(snapshot.Track);
        var changed = _coverTransition.Begin(identity);
        if (!changed &&
            _coverTransition.IsVisualFor(identity) &&
            _coverBytes is { Length: > 0 })
        {
            return;
        }

        var bytes = snapshot.CoverImageBytes;
        if (bytes is not { Length: > 0 } &&
            _localCover is not null &&
            snapshot.Track is not null &&
            (identity != _localCoverIdentity || _utcNow() >= _nextLocalCover))
        {
            _localCoverIdentity = identity;
            _nextLocalCover = _utcNow().AddSeconds(5);
            bytes = await Task.Run(
                () => _localCover.TryGetCover(snapshot.Track),
                cancellationToken);
        }

        if (bytes is { Length: > 0 } && bytes.Length <= 512 * 1024)
        {
            _coverBytes = bytes;
            _coverBitmap = null;
            var mime = bytes.Length > 3 && bytes[0] == 0xff && bytes[1] == 0xd8
                ? "image/jpeg"
                : "image/png";
            _coverData = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
            _coverIcon = "";
        }
        else
        {
            if (snapshot.Track is not null &&
                ((snapshot.IsCoverLoading && _coverTransition.ShouldRetainPreviousVisual()) ||
                 _coverTransition.ShouldDebounceFallback()))
            {
                return;
            }
            _coverBytes = null;
            _coverBitmap = null;
            _coverData = "";
            _coverIcon = snapshot.Track is null
                ? ""
                : _fallbackIcon.Resolve(
                    snapshot.SourceAppUserModelId,
                    snapshot.Track.SourceApp);
            _coverFrame = snapshot.Track is null
                ? NativeMediaPayloadFactory.EmptyCover()
                : NativeMediaPayloadFactory.CreateCover(
                    "",
                    snapshot.Track.SourceApp == "QQMusic" ? "Q" : "N",
                    "rgba(67,160,71,1)",
                    _coverIcon,
                    LyricSyncService.BuildStableTrackIdentity(snapshot.Track));
        }

        _coverTrackId = snapshot.Track is null
            ? ""
            : LyricSyncService.BuildStableTrackIdentity(snapshot.Track);
        _coverTransition.MarkVisual(identity);
        if (_coverData.Length > 0 || _coverIcon.Length > 0)
        {
            _coverFrame = NativeMediaPayloadFactory.CreateCover(
                _coverData,
                snapshot.Track?.SourceApp == "QQMusic" ? "Q" : "N",
                "rgba(67,160,71,1)",
                _coverIcon,
                _coverTrackId);
        }
    }

    private BitmapSource? CreateCover()
    {
        if (_coverBytes is null) return null;
        if (_coverBitmap is not null) return _coverBitmap;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 120;
            image.StreamSource = new MemoryStream(_coverBytes);
            image.EndInit();
            image.Freeze();
            return _coverBitmap = image;
        }
        catch (Exception exception) when (exception is NotSupportedException or IOException)
        {
            return null;
        }
    }

    internal async Task CommandAsync(string action, double? position)
    {
        if (_disposed) return;
        if (action.StartsWith("selectSource:", StringComparison.Ordinal))
        {
            Provider.SelectSource(action[13..]);
            InvalidateAfterCommand();
            return;
        }

        try
        {
            if (action == "seek" && position.HasValue && double.IsFinite(position.Value))
            {
                await Provider.SeekToAsync(
                    TimeSpan.FromSeconds(Math.Max(0, position.Value)),
                    _stop.Token);
            }
            else if (action is "playPause" or "previous" or "next")
            {
                await Provider.ExecuteAsync(
                    action switch
                    {
                        "previous" => MediaControlAction.PreviousTrack,
                        "next" => MediaControlAction.NextTrack,
                        _ => MediaControlAction.TogglePlayPause
                    },
                    _stop.Token);
            }
        }
        finally
        {
            InvalidateAfterCommand();
        }
    }

    internal bool TryApplyResolvedLyrics(TrackInfo track, ResolvedLyrics resolved)
    {
        var applied = !NonLyricsMediaPolicy.IsNonLyrics(_playback) &&
            Lyrics.TryApplyResolvedLyrics(track, resolved);
        if (applied) InvalidateAfterCommand();
        return applied;
    }

    private void InvalidateAfterCommand()
    {
        RunOnDispatcher(() =>
        {
            if (_disposed) return;
            Interlocked.Increment(ref _stateVersion);
            Interlocked.Increment(ref _authorityVersion);
            _scheduler.RequestState(full: true);
            ScheduleNext();
        });
    }

    private async Task ApplyPresentationTickAsync(CancellationToken cancellationToken)
    {
        if (!_taskbarVisible && !_cardVisible) return;
        if (_presentation.CurrentFrame is not { isPlaying: true } previous ||
            !_playback.IsPlaying)
        {
            return;
        }

        var now = _utcNow();
        if (now - _lastAuthorityUtc >= MediaRenderScheduler.FallbackInterval)
        {
            _scheduler.RequestState(full: true);
            return;
        }

        var stateVersion = Volatile.Read(ref _stateVersion);
        var authorityVersion = Volatile.Read(ref _playbackAuthorityVersion);
        if (authorityVersion != Volatile.Read(ref _authorityVersion))
        {
            _scheduler.RequestState();
            return;
        }
        var update = await MediaPresentationTickBuilder.BuildAsync(
            _playback,
            _lastAuthorityUtc,
            now,
            previous.isBrowser,
            _taskbarVisible,
            _cardVisible,
            previous,
            _presentation,
            Lyrics,
            _settings,
            _spectrumActive ? _spectrumBars : [],
            _nativeAppearance,
            () => !_disposed &&
                stateVersion == Volatile.Read(ref _stateVersion) &&
                authorityVersion == Volatile.Read(ref _authorityVersion) &&
                ReferenceEquals(previous, _presentation.CurrentFrame));
        if (_disposed ||
            stateVersion != Volatile.Read(ref _stateVersion) ||
            authorityVersion != Volatile.Read(ref _authorityVersion)) return;
        if (update is null) return;
        var current = update.Value;
        Volatile.Write(ref _frame, current.Frame);
        Volatile.Write(ref _card, current.Card);
        MediaPublicationPolicy.PublishFrame(current, _taskbarVisible, FrameReady);
        MediaPublicationPolicy.PublishCard(current, _cardVisible, CardFrameReady);
    }

    private void RunOnDispatcher(Action action)
    {
        if (_disposed) return;
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }
        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, action);
        }
        catch (InvalidOperationException)
        {
            // The dispatcher may have shut down concurrently with helper shutdown.
        }
    }

    private void ScheduleNext()
    {
        if (_disposed || !_started) return;
        var delay = _scheduler.TimeUntilNext();
        _timer.Stop();
        _timer.Interval = delay <= TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(1)
            : delay;
        _timer.Start();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Provider.Invalidated -= OnProviderInvalidated;
        foreach (var sourceIcon in _sourceIcons.Values.Where(task => !task.IsCompleted))
        {
            _ = sourceIcon.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted |
                    TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        _stop.Cancel();
        _timer.Stop();
        _timer.Tick -= Tick;
        _spectrumTimer.Stop();
        _spectrumTimer.Tick -= SpectrumTick;
        _spectrum.Dispose();
        Lyrics.Dispose();
        Provider.Dispose();
        _localCover?.Dispose();
        Composition.Dispose();
        Offsets.Dispose();
        _stop.Dispose();
    }
}
