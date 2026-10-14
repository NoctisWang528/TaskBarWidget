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
    internal readonly SmtcMusicSessionProvider Provider = new();
    internal LyricSyncService Lyrics { get; private set; }
    private readonly SystemAudioSpectrumService _spectrum = new();
    private readonly PlaybackSnapshotStabilityGate _stability = new();
    private readonly CoverVisualTransitionState _coverTransition = new();
    private readonly CoverFallbackIconResolver _fallbackIcon = new();
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _spectrumTimer;
    private readonly CancellationTokenSource _stop = new();
    private LocalMediaCoverProvider? _localCover;
    private AppSettings _settings = new();
    private SpectrumTuningSettings _tuning = SpectrumTuningSettings.CreateDefault();
    private PlaybackSnapshot _playback = new(false, TimeSpan.Zero, null);
    private bool _busy, _disposed, _visible, _spectrumActive, _preview;
    private string _coverData = "", _coverIcon = "", _coverTrackId = "";
    private byte[]? _coverBytes;
    private BitmapSource? _coverBitmap;
    private string? _localCoverIdentity;
    private DateTimeOffset _nextLocalCover;
    private string _styleScript = "", _tuningScript = "", _coverScript = "", _spectrumScript = "";
    private object _frame = new { version = 1, type = "frame", sourceId = "", isPlaying = false, hasMedia = false, isBrowser = false, trackId = "", scripts = Array.Empty<string>() };
    internal event Action<object>? FrameReady;
    internal MediaControlState ControlState { get; private set; } = new("没有媒体", "", "", 0, 0, false, false, false, false, false);
    internal CurrentTrackLyricsContext? CurrentTrackContext => LyricsInput(_playback).Track is not { } track
        ? null : new(track, Lyrics.CurrentLyricSourceApp ?? "");

    internal MediaCoordinator()
    {
        Lyrics = Composition.CreateLyricSyncService(_settings, Offsets);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
        _timer.Tick += Tick;
        _spectrumTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _spectrumTimer.Tick += SpectrumTick;
        _styleScript = LyricsStyleScriptFactory.Create(_settings, 1);
        _tuningScript = LyricsWebViewScriptFactory.SetSpectrumTuning(_tuning);
        _coverScript = LyricsWebViewScriptFactory.SetCover("", "N", "rgba(67,160,71,1)", "", "");
    }

    internal void Start() => _timer.Start();
    internal object Snapshot() => Volatile.Read(ref _frame);
    internal string CoverDataUrl => _coverData;
    internal static bool IsBrowser(TrackInfo? track) => string.Equals(track?.SourceApp, "Browser", StringComparison.OrdinalIgnoreCase);
    internal static PlaybackSnapshot LyricsInput(PlaybackSnapshot snapshot) => IsBrowser(snapshot.Track) ||
        SmtcMusicSessionProvider.IsBrowserSource(snapshot.SourceAppUserModelId) ? snapshot with { Track = null } : snapshot;

    internal void ApplySettings(AppSettings settings)
    {
        if (_disposed) return;
        var previous = _settings;
        _settings = settings.Clone();
        _settings.UseFloatingWindow = false;
        _styleScript = LyricsStyleScriptFactory.Create(_settings, 1);
        var changes = AppSettingsChangeSet.Create(previous, _settings);
        Provider.SetRecognitionOrder(_settings.SourceRecognitionOrder, Composition.GetEnabledPlayerSources(_settings),
            _settings.CustomPlayerSources.Select(x => x.SourceAppUserModelId).ToArray());
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
        UpdateSpectrum(false);
    }

    internal void SetVisible(bool visible) { _visible = visible; if (!visible) UpdateSpectrum(false); }
    internal void SetPreview(bool preview) { _preview = preview; if (!preview) UpdateSpectrum(false); }
    internal void RetrySpectrum() { _spectrum.Stop(); _spectrumActive = false; }
    internal void ApplyTuning(SpectrumTuningSettings tuning) { _tuning = tuning.Clone(); _spectrum.ApplyTuning(_tuning); _tuningScript = LyricsWebViewScriptFactory.SetSpectrumTuning(_tuning); }

    private async void Tick(object? sender, EventArgs args)
    {
        if (_busy || _disposed) return;
        _busy = true;
        try
        {
            var snapshot = await Provider.GetCurrentAsync(_stop.Token);
            if (_disposed) return;
            var decision = _stability.Observe(snapshot, PlaybackInputPolicy.Classify(snapshot), DateTimeOffset.UtcNow);
            if (decision.Action == PlaybackSnapshotGateAction.Hold) return;
            _playback = snapshot = decision.Snapshot;
            var track = snapshot.Track;
            var browser = IsBrowser(track) || SmtcMusicSessionProvider.IsBrowserSource(snapshot.SourceAppUserModelId);
            var trackId = track is null ? "" : LyricSyncService.BuildStableTrackIdentity(track);
            // A browser snapshot must never reach the lyric resolver, including while paused.
            var frame = await Lyrics.GetDisplayFrameAsync(LyricsInput(snapshot));
            if (_disposed) return;
            snapshot = Provider.RefreshPlaybackState(snapshot);
            _playback = snapshot;
            var scene = ResolveScene(frame, browser, track is not null);
            var showSpectrum = scene == LyricsPresentationScene.Spectrum;
            UpdateSpectrum(showSpectrum);
            await UpdateCoverAsync(snapshot);
            if (_disposed) return;

            var scripts = new List<string>
            {
                _styleScript, _tuningScript, _coverScript
            };
            string current = frame.CurrentLine, next = frame.NextLine;
            if (scene == LyricsPresentationScene.Searching)
            {
                var searching = LyricsSearchingPresentationPolicy.Create(track);
                current = searching.Current; next = searching.Next;
            }
            if (track is null) { current = "等待播放..."; next = ""; }
            scripts.Add(LyricsWebViewScriptFactory.SetLyrics(current, next, frame.LineProgress, frame.CurrentLineIndex,
                trackId, showSpectrum, snapshot.IsPlaying, _settings.EnableWordScanning ? frame.WordScanProgress : null,
                frame.CurrentTranslation, frame.NextTranslation, _settings.ShowLyricTranslation && frame.HasTrackTranslation,
                presentationScene: scene));
            var capabilities = Provider.GetControlCapabilities();
            ControlState = new MediaControlState(track?.Title ?? "没有媒体", track?.Artist ?? "", track?.SourceApp ?? "",
                snapshot.Position.TotalSeconds, track?.Duration.TotalSeconds ?? 0, snapshot.IsPlaying,
                capabilities.Previous, capabilities.Next, capabilities.PlayPause, snapshot.CanSeek, CreateCover(),
                Provider.GetSourceChoices(), snapshot.SourceAppUserModelId);
            scripts.Add(WebViewMessageScriptFactory.Dispatch("taskbarMedia", "browser", new
            {
                active = browser, title = track?.Title ?? "", artist = track?.Artist ?? "", source = snapshot.SourceAppUserModelId,
                isPlaying = snapshot.IsPlaying, canPrevious = capabilities.Previous, canNext = capabilities.Next, canPlayPause = capabilities.PlayPause
            }));
            if (_spectrumActive)
            {
                scripts.Add(_spectrumScript);
            }
            Provider.SetCurrentLyricSource(Lyrics.CurrentLyricSourceApp);
            Provider.SetCurrentLyricOffsets(_settings.GetPlayerLyricOffsetMilliseconds(track?.SourceApp),
                Offsets.GetOffsetMilliseconds(track, Lyrics.CurrentLyricSourceApp));
            var value = new MediaFrame(1, "frame", snapshot.SourceAppUserModelId,
                snapshot.IsPlaying, track is not null, browser, trackId, scripts.ToArray());
            Volatile.Write(ref _frame, value);
            FrameReady?.Invoke(value);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception exception)
        {
            Log.Warn($"Media coordinator: {exception}");
            UpdateSpectrum(false);
            var value = new { version = 1, type = "frame", sourceId = "", isPlaying = false,
                hasMedia = false, isBrowser = false, trackId = "", scripts = Array.Empty<string>() };
            Volatile.Write(ref _frame, value);
            FrameReady?.Invoke(value);
        }
        finally { _busy = false; }
    }

    private LyricsPresentationScene ResolveScene(LyricDisplayFrame frame, bool browser, bool hasTrack)
    {
        if (!hasTrack) return LyricsPresentationScene.NoPlayback;
        if (browser) return LyricsPresentationScene.Message;
        var spectrum = _settings.SpectrumDisplayMode switch
        {
            SpectrumDisplayMode.Always => true,
            SpectrumDisplayMode.PureMusicOrNoLyrics => frame.IsPureMusic || frame.CurrentLine == LyricSyncService.NoLyricsText,
            SpectrumDisplayMode.PureMusicOnly => frame.IsPureMusic,
            _ => false
        };
        if (spectrum) return LyricsPresentationScene.Spectrum;
        if (Lyrics.CurrentLyricAcquisition == LyricAcquisitionKind.Searching) return LyricsPresentationScene.Searching;
        return frame.CurrentLineIndex >= 0 ? LyricsPresentationScene.Lyrics : LyricsPresentationScene.Message;
    }

    private void UpdateSpectrum(bool showSpectrum)
    {
        if (_disposed) return;
        var capture = _settings.SpectrumAudioAccessGranted && (_preview || (_visible && showSpectrum && _playback.IsPlaying));
        if (capture == _spectrumActive) return;
        _spectrumActive = capture;
        if (capture) { _spectrum.Start(); _spectrumTimer.Start(); }
        else { _spectrumTimer.Stop(); _spectrum.Stop(); }
    }

    private void SpectrumTick(object? sender, EventArgs args)
    {
        if (_disposed || !_spectrumActive) return;
        var bars = _spectrum.GetSpectrum();
        _spectrumScript = LyricsWebViewScriptFactory.SetSpectrum(bars);
        var capture = _spectrum.GetDiagnostics();
        SpectrumDiagnosticsState.Update(new(DateTimeOffset.UtcNow, true, _playback.IsPlaying, capture.IsAvailable,
            capture.SampleRate, capture.Channels, capture.Format, capture.InputPeak,
            bars.Length == 0 ? 0 : bars.Max(), bars, capture.LastAudioUtc, capture.LastError));
        if (Snapshot() is not MediaFrame frame) return;
        frame = frame with { scripts = frame.scripts.Where(script => !script.Contains("\"type\":\"spectrum\"", StringComparison.Ordinal)).Append(_spectrumScript).ToArray() };
        Volatile.Write(ref _frame, frame);
        FrameReady?.Invoke(frame);
    }

    private async Task UpdateCoverAsync(PlaybackSnapshot snapshot)
    {
        var identity = CoverIdentity.FromTrack(snapshot.Track);
        var changed = _coverTransition.Begin(identity);
        if (!changed && _coverTransition.IsVisualFor(identity) && _coverBytes is { Length: > 0 }) return;
        var bytes = snapshot.CoverImageBytes;
        if (bytes is not { Length: > 0 } && _localCover is not null && snapshot.Track is not null &&
            (identity != _localCoverIdentity || DateTimeOffset.UtcNow >= _nextLocalCover))
        {
            _localCoverIdentity = identity;
            _nextLocalCover = DateTimeOffset.UtcNow.AddSeconds(5);
            bytes = await Task.Run(() => _localCover.TryGetCover(snapshot.Track));
        }
        if (bytes is { Length: > 0 } && bytes.Length <= 512 * 1024)
        {
            _coverBytes = bytes;
            _coverBitmap = null;
            var mime = bytes.Length > 3 && bytes[0] == 0xff && bytes[1] == 0xd8 ? "image/jpeg" : "image/png";
            _coverData = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
            _coverIcon = "";
        }
        else
        {
            if (snapshot.Track is not null && ((snapshot.IsCoverLoading && _coverTransition.ShouldRetainPreviousVisual()) ||
                _coverTransition.ShouldDebounceFallback())) return;
            _coverBytes = null; _coverBitmap = null; _coverData = "";
            _coverIcon = snapshot.Track is null ? "" : _fallbackIcon.Resolve(snapshot.SourceAppUserModelId, snapshot.Track.SourceApp);
        }
        _coverTrackId = snapshot.Track is null ? "" : LyricSyncService.BuildStableTrackIdentity(snapshot.Track);
        _coverTransition.MarkVisual(identity);
        _coverScript = LyricsWebViewScriptFactory.SetCover(_coverData, snapshot.Track?.SourceApp == "QQMusic" ? "Q" : "N",
            "rgba(67,160,71,1)", _coverIcon, _coverTrackId);
    }

    private BitmapSource? CreateCover()
    {
        if (_coverBytes is null) return null;
        if (_coverBitmap is not null) return _coverBitmap;
        try
        {
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 120;
            image.StreamSource = new MemoryStream(_coverBytes); image.EndInit(); image.Freeze();
            return _coverBitmap = image;
        }
        catch (Exception exception) when (exception is NotSupportedException or IOException) { return null; }
    }

    internal async Task CommandAsync(string action, double? position)
    {
        if (_disposed) return;
        if (action.StartsWith("selectSource:", StringComparison.Ordinal)) { Provider.SelectSource(action[13..]); return; }
        if (action == "seek" && position.HasValue && double.IsFinite(position.Value))
            await Provider.SeekToAsync(TimeSpan.FromSeconds(Math.Max(0, position.Value)), _stop.Token);
        else if (action is "playPause" or "previous" or "next")
            await Provider.ExecuteAsync(action switch { "previous" => MediaHotkeyAction.PreviousTrack, "next" => MediaHotkeyAction.NextTrack, _ => MediaHotkeyAction.TogglePlayPause }, _stop.Token);
    }

    internal bool TryApplyResolvedLyrics(TrackInfo track, ResolvedLyrics resolved) => !IsBrowser(_playback.Track) &&
        !SmtcMusicSessionProvider.IsBrowserSource(_playback.SourceAppUserModelId) && Lyrics.TryApplyResolvedLyrics(track, resolved);
    internal static string GetHtml()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Web", "Lyrics");
        var script = string.Join("\n", new[] { "bridge.js", "state.js", "presentation.js", "app.js", "browser.js" }.Select(file => File.ReadAllText(Path.Combine(directory, file))));
        return File.ReadAllText(Path.Combine(directory, "index.html"))
            .Replace("{{STYLE_CSS}}", File.ReadAllText(Path.Combine(directory, "style.css")))
            .Replace("{{APP_JS}}", script);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _stop.Cancel(); _timer.Stop(); _timer.Tick -= Tick;
        _spectrumTimer.Stop(); _spectrumTimer.Tick -= SpectrumTick;
        _spectrum.Dispose(); Lyrics.Dispose(); Provider.Dispose(); _localCover?.Dispose(); Composition.Dispose(); Offsets.Dispose();
    }
}
