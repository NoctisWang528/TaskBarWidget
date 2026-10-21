using TaskBarWidgets.Media.Integration;
using TaskbarLyrics.App;
using TaskbarLyrics.Core.Abstractions;
using TaskbarLyrics.Core.Models;
using TaskbarLyrics.Core.Services;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

// The taskbar shows either lyrics, a message or the spectrum. The card always
// renders lyrics. These checks drive the production scene/tick/scheduler path
// that joins the two surfaces.
public sealed class MediaSurfaceSceneTests
{
    [Fact]
    public void AlwaysSpectrumKeepsTheCardOnLyrics()
    {
        var settings = new AppSettings { SpectrumDisplayMode = SpectrumDisplayMode.Always };
        var display = new LyricDisplayFrame("First", "Second", "Title", 0.5, 2);

        Assert.Equal(
            LyricsPresentationScene.Spectrum,
            LyricsPresentationCalculator.ResolveScene(
                settings, display, isBrowser: false, hasTrack: true, LyricAcquisitionKind.Remote));
        Assert.Equal(
            LyricsPresentationScene.Lyrics,
            LyricsPresentationCalculator.ResolveCardScene(
                settings, display, isBrowser: false, hasTrack: true, LyricAcquisitionKind.Remote));
    }

    [Fact]
    public void CardSceneKeepsSearchingAndPureMusicSemantics()
    {
        var settings = new AppSettings { SpectrumDisplayMode = SpectrumDisplayMode.Always };
        var searching = new LyricDisplayFrame(
            LyricSyncService.SearchingText, "", "Title", 0, -1);
        var pureMusic = new LyricDisplayFrame("纯音乐", "", "Title", 0, -1, IsPureMusic: true);

        Assert.Equal(
            LyricsPresentationScene.Searching,
            LyricsPresentationCalculator.ResolveCardScene(
                settings, searching, false, true, LyricAcquisitionKind.Searching));
        Assert.Equal(
            LyricsPresentationScene.Message,
            LyricsPresentationCalculator.ResolveCardScene(
                settings, pureMusic, false, true, LyricAcquisitionKind.Remote));
        Assert.Equal(
            LyricsPresentationScene.Message,
            LyricsPresentationCalculator.ResolveCardScene(
                settings, pureMusic, isBrowser: true, hasTrack: true, LyricAcquisitionKind.Unknown));
        Assert.Equal(
            LyricsPresentationScene.NoPlayback,
            LyricsPresentationCalculator.ResolveCardScene(
                settings, pureMusic, false, hasTrack: false, LyricAcquisitionKind.Unknown));
    }

    [Fact]
    public async Task SpectrumTaskbarKeepsAdvancingVisibleCardLyrics()
    {
        var track = CreateTrack("Spectrum taskbar");
        var resolved = CreateResolvedLyrics(
            new ParsedLyricLine(At(1.0), At(1.5), "First"),
            new ParsedLyricLine(At(1.5), At(4.0), "Second"));
        using var service = await CreateServiceAsync(track, resolved);
        var settings = new AppSettings { SpectrumDisplayMode = SpectrumDisplayMode.Always };
        var authoritative = CreateSnapshot(track, 1.05);
        var display = await service.GetDisplayFrameAsync(authoritative);
        var state = new MediaPresentationState();
        var initialLyrics = LyricsPresentationCalculator.Create(
            authoritative, display, settings, LyricsPresentationScene.Spectrum, false, LyricAcquisitionKind.Remote);
        var initial = state.Apply(CreateInput(
            authoritative, initialLyrics, LyricsPresentationScene.Lyrics, settings));
        Assert.Equal("spectrum", JsonScene(initial.Frame));
        Assert.Equal("lyrics", JsonScene(initial.Card));

        // No new SMTC event: the tick projects 0.6s past the authoritative read,
        // which crosses the second line's real start time.
        var update = await MediaPresentationTickBuilder.BuildAsync(
            authoritative,
            authoritativeAtUtc: At(0),
            nowUtc: At(600),
            isBrowser: false,
            taskbarVisible: true,
            cardVisible: true,
            previous: state.CurrentFrame!,
            state: state,
            lyrics: service,
            settings: settings,
            spectrum: [],
            nativeAppearance: NativeAppearanceJson.Empty);

        Assert.NotNull(update);
        var current = update.Value;
        Assert.Equal("spectrum", JsonScene(current.Frame));
        Assert.Equal("lyrics", JsonScene(current.Card));
        Assert.Equal(1, current.Frame.lyrics.currentLineIndex);
        Assert.Equal("Second", current.Frame.lyrics.current);
        Assert.Equal("Second", current.Card.lyrics.current);

        var mediaFrames = new List<MediaFrame>();
        var cardFrames = new List<CardFrame>();
        MediaPublicationPolicy.PublishFrame(current, taskbarVisible: true, value => mediaFrames.Add((MediaFrame)value));
        MediaPublicationPolicy.PublishCard(current, cardVisible: true, value => cardFrames.Add((CardFrame)value));
        Assert.Single(mediaFrames);
        Assert.Single(cardFrames);
        Assert.Equal("Second", cardFrames[0].lyrics.current);
    }

    [Fact]
    public async Task HiddenTaskbarStillAdvancesTheVisibleCard()
    {
        var track = CreateTrack("Monitoring taskbar");
        var resolved = CreateResolvedLyrics(
            new ParsedLyricLine(At(1.0), At(1.4), "First"),
            new ParsedLyricLine(At(1.4), At(4.0), "Second"));
        using var service = await CreateServiceAsync(track, resolved);
        var settings = new AppSettings();
        var authoritative = CreateSnapshot(track, 1.05);
        var display = await service.GetDisplayFrameAsync(authoritative);
        var state = new MediaPresentationState();
        var initialLyrics = LyricsPresentationCalculator.Create(
            authoritative, display, settings, LyricsPresentationScene.Lyrics, false, LyricAcquisitionKind.Remote);
        state.Apply(CreateInput(authoritative, initialLyrics, LyricsPresentationScene.Lyrics, settings));

        var update = await MediaPresentationTickBuilder.BuildAsync(
            authoritative,
            authoritativeAtUtc: At(0),
            nowUtc: At(500),
            isBrowser: false,
            taskbarVisible: false,
            cardVisible: true,
            previous: state.CurrentFrame!,
            state: state,
            lyrics: service,
            settings: settings,
            spectrum: [],
            nativeAppearance: NativeAppearanceJson.Empty);

        Assert.NotNull(update);
        Assert.Equal("Second", update.Value.Card.lyrics.current);
    }

    [Fact]
    public async Task ClosingEverySurfaceStopsLyricTicks()
    {
        var track = CreateTrack("Closed surfaces");
        var resolved = CreateResolvedLyrics(
            new ParsedLyricLine(At(1.0), At(1.4), "First"),
            new ParsedLyricLine(At(1.4), At(4.0), "Second"));
        using var service = await CreateServiceAsync(track, resolved);
        var settings = new AppSettings { SpectrumDisplayMode = SpectrumDisplayMode.Always };
        var authoritative = CreateSnapshot(track, 1.05);
        var display = await service.GetDisplayFrameAsync(authoritative);
        var state = new MediaPresentationState();
        var initialLyrics = LyricsPresentationCalculator.Create(
            authoritative, display, settings, LyricsPresentationScene.Spectrum, false, LyricAcquisitionKind.Remote);
        state.Apply(CreateInput(authoritative, initialLyrics, LyricsPresentationScene.Lyrics, settings));

        var update = await MediaPresentationTickBuilder.BuildAsync(
            authoritative,
            authoritativeAtUtc: At(0),
            nowUtc: At(600),
            isBrowser: false,
            taskbarVisible: false,
            cardVisible: false,
            previous: state.CurrentFrame!,
            state: state,
            lyrics: service,
            settings: settings,
            spectrum: [],
            nativeAppearance: NativeAppearanceJson.Empty);

        Assert.Null(update);
    }

    [Fact]
    public void SchedulerFollowsTheVisibleSurfaceForLyricWork()
    {
        var now = At(0);
        var scheduler = new MediaRenderScheduler(() => now);
        scheduler.Initialize();
        scheduler.SetPlaybackState(true);
        scheduler.SetVisibility(taskbarVisible: true, cardVisible: true);
        Assert.True(scheduler.HasVisibleSurface);

        scheduler.SetPresentationActive(MediaPresentationActivity.ShouldAdvancePresentation(
            taskbarVisible: true,
            taskbarScene: LyricsPresentationScene.Spectrum,
            cardVisible: true,
            cardScene: LyricsPresentationScene.Lyrics));
        Assert.True(scheduler.HasActivePresentation);
        scheduler.MarkCompleted(scheduler.Poll());
        now = At(150);
        Assert.Equal(MediaWorkKind.Presentation, scheduler.Poll().Kind);

        // The taskbar drops to monitoring while the card stays open: lyric work
        // must continue for the card.
        scheduler.SetVisibility(taskbarVisible: false, cardVisible: true);
        scheduler.SetPresentationActive(MediaPresentationActivity.ShouldAdvancePresentation(
            taskbarVisible: false,
            taskbarScene: LyricsPresentationScene.NoPlayback,
            cardVisible: true,
            cardScene: LyricsPresentationScene.Lyrics));
        Assert.True(scheduler.HasActivePresentation);
        now = At(300);
        Assert.Equal(MediaWorkKind.Presentation, scheduler.Poll().Kind);
        scheduler.MarkCompleted(scheduler.Poll());

        // Closing the card leaves nothing that renders lyrics.
        scheduler.SetVisibility(taskbarVisible: true, cardVisible: false);
        scheduler.SetPresentationActive(MediaPresentationActivity.ShouldAdvancePresentation(
            taskbarVisible: true,
            taskbarScene: LyricsPresentationScene.Spectrum,
            cardVisible: false,
            cardScene: LyricsPresentationScene.Message));
        Assert.False(scheduler.HasActivePresentation);
        // Coming back from monitoring legitimately requests one fresh state read.
        Assert.Equal(MediaWorkKind.FullState, scheduler.Poll().Kind);
        scheduler.MarkCompleted(scheduler.Poll());
        now = At(450);
        Assert.Equal(MediaWorkKind.None, scheduler.Poll().Kind);
    }

    [Fact]
    public void TaskbarSceneChangeDoesNotRestartTheCardTransition()
    {
        var settings = new AppSettings { SpectrumDisplayMode = SpectrumDisplayMode.Always };
        var snapshot = CreateSnapshot(CreateTrack("Card revision"), 2.0);
        var lyrics = new MediaLyricsFrame(
            "First", "Second", 0.5, 1, "QQMUSIC|CARD REVISION|ARTIST",
            false, true, "", "", false, true, null, "lyrics");
        var state = new MediaPresentationState();
        var withSpectrum = state.Apply(CreateInput(
            snapshot, lyrics, LyricsPresentationScene.Lyrics, settings));
        var taskbarLyrics = lyrics with { scene = "spectrum" };
        var spectrumTaskbar = state.Apply(CreateInput(
            snapshot, taskbarLyrics, LyricsPresentationScene.Lyrics, settings));

        Assert.Equal("spectrum", JsonScene(spectrumTaskbar.Frame));
        Assert.Equal("lyrics", JsonScene(spectrumTaskbar.Card));
        Assert.Equal(withSpectrum.Card.revision, spectrumTaskbar.Card.revision);
    }

    private static string JsonScene(MediaFrame frame) => frame.lyrics.scene;

    private static string JsonScene(CardFrame card) => card.lyrics.scene;

    private static MediaPresentationInput CreateInput(
        PlaybackSnapshot snapshot,
        MediaLyricsFrame lyrics,
        LyricsPresentationScene cardScene,
        AppSettings settings) =>
        new(
            snapshot,
            false,
            lyrics.trackId,
            lyrics,
            cardScene,
            NativeMediaPayloadFactory.CreateStyle(settings, 1),
            NativeMediaPayloadFactory.EmptyCover(),
            new MediaBrowserFrame(false, "Title", "Artist", "qqmusic.exe", true, true, true, true),
            NativeMediaPayloadFactory.CreateSpectrumTuning(SpectrumTuningSettings.CreateDefault()),
            [],
            NativeAppearanceJson.Empty);

    private static async Task<LyricSyncService> CreateServiceAsync(
        TrackInfo track,
        ResolvedLyrics resolved)
    {
        var service = new LyricSyncService(
            new NeverResolver(),
            metadataStabilizationDelay: TimeSpan.Zero);
        await service.GetDisplayFrameAsync(CreateSnapshot(track, 1.0));
        Assert.True(service.TryApplyResolvedLyrics(track, resolved));
        return service;
    }

    private static ResolvedLyrics CreateResolvedLyrics(params ParsedLyricLine[] lines) =>
        new(
            new ParsedLyrics(
                lines,
                LyricTimingKind.LineTimed,
                LyricTimingProvenance.ProviderSupplied,
                LyricPayloadFormat.Lrc),
            KnownLyricProviders.QQMusic,
            "candidate",
            LyricAcquisitionKind.Remote,
            new Dictionary<string, string> { ["elapsedMs"] = "1" });

    private static TrackInfo CreateTrack(string title) =>
        new(title, title, "Artist", "Album", "QQMusic", TimeSpan.FromMinutes(5));

    private static PlaybackSnapshot CreateSnapshot(TrackInfo track, double seconds) =>
        new(
            true,
            TimeSpan.FromSeconds(seconds),
            track,
            SourceAppUserModelId: "qqmusic.exe",
            CanSeek: true);

    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    private static DateTimeOffset At(int milliseconds) =>
        new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero)
            .AddMilliseconds(milliseconds);

    private sealed class NeverResolver : ILyricResolutionCoordinator
    {
        public async Task<ResolvedLyrics?> ResolveAsync(
            TrackInfo track,
            CancellationToken cancellationToken = default) =>
            (await ResolveWithResultAsync(track, cancellationToken)).Lyrics;

        public async Task<LyricResolutionResult> ResolveWithResultAsync(
            TrackInfo track,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new LyricResolutionResult(null, LyricResolutionStatus.NotFound);
        }

        public void Dispose()
        {
        }
    }
}
