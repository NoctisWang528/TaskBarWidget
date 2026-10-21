using TaskBarWidgets.Media.Integration;
using TaskbarLyrics.App;
using TaskbarLyrics.Core.Abstractions;
using TaskbarLyrics.Core.Models;
using TaskbarLyrics.Core.Services;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class MediaTimelineIntegrationTests
{
    [Fact]
    public async Task PresentationTickCrossesUnequalLinesByActualLyricTimesWithoutAnotherSmtcRead()
    {
        var track = CreateTrack("Timeline");
        var resolved = CreateResolvedLyrics(
            new ParsedLyricLine(At(1.0), At(1.1), "First"),
            new ParsedLyricLine(At(1.1), At(1.3), "Second"),
            new ParsedLyricLine(At(1.3), At(4.0), "Third"));
        using var service = await CreateServiceAsync(track, resolved);
        var authoritative = CreateSnapshot(track, 1.05);
        var initialDisplay = await service.GetDisplayFrameAsync(authoritative);
        var settings = new AppSettings();
        var initialLyrics = LyricsPresentationCalculator.Create(
            authoritative,
            initialDisplay,
            settings,
            LyricsPresentationScene.Lyrics,
            isBrowser: false,
            LyricAcquisitionKind.Remote);
        var state = new MediaPresentationState();
        state.Apply(CreateInput(authoritative, initialLyrics, settings));

        var now = At(0);
        var scheduler = new MediaRenderScheduler(() => now);
        scheduler.SetVisibility(taskbarVisible: true, cardVisible: false);
        scheduler.SetPlaybackState(true);
        scheduler.Initialize();
        scheduler.MarkCompleted(scheduler.Poll());

        now = At(300);
        var plan = scheduler.Poll();
        Assert.Equal(MediaWorkKind.Presentation, plan.Kind);
        var update = await MediaPresentationTickBuilder.BuildAsync(
            authoritative,
            authoritativeAtUtc: At(0),
            nowUtc: now,
            isBrowser: false,
            previous: state.CurrentFrame!,
            state: state,
            lyrics: service,
            settings: settings,
            spectrum: [],
            nativeAppearance: NativeAppearanceJson.Empty);

        Assert.NotNull(update);
        var current = update.Value;
        Assert.Equal(2, current.Frame.lyrics.currentLineIndex);
        Assert.Equal("Third", current.Frame.lyrics.current);
        Assert.False(current.SemanticFrameChanged);
        var published = new List<MediaFrame>();
        Assert.True(MediaPublicationPolicy.PublishFrame(
            current,
            taskbarVisible: true,
            value => published.Add((MediaFrame)value)));
        Assert.Single(published);
        Assert.Equal("Third", published[0].lyrics.current);
    }

    [Fact]
    public async Task WordScanHonorsSyllableGapsInsteadOfUsingAFixedLineDuration()
    {
        var track = CreateTrack("Word gaps");
        var gapLine = new ParsedLyricLine(
            At(1.0),
            At(3.0),
            "AB",
            segments:
            [
                new ParsedLyricSegment(At(1.0), At(1.2), "A"),
                new ParsedLyricSegment(At(2.5), At(2.8), "B")
            ]);
        using var service = await CreateServiceAsync(
            track,
            CreateResolvedLyrics(gapLine));

        var firstHalf = await service.GetDisplayFrameAsync(CreateSnapshot(track, 1.5));
        var throughoutGap = await service.GetDisplayFrameAsync(CreateSnapshot(track, 2.0));
        var secondHalf = await service.GetDisplayFrameAsync(CreateSnapshot(track, 2.65));

        Assert.Equal(firstHalf.WordScanProgress, throughoutGap.WordScanProgress);
        Assert.True(secondHalf.WordScanProgress > throughoutGap.WordScanProgress);
    }

    [Fact]
    public async Task OffsetsAndSeekUseTheRealLyricTimeline()
    {
        var track = CreateTrack("Offsets");
        var resolved = CreateResolvedLyrics(
            new ParsedLyricLine(At(1.0), At(2.0), "First"),
            new ParsedLyricLine(At(2.0), At(4.0), "Second"));
        using var service = await CreateServiceAsync(
            track,
            resolved,
            getPlayerLeadTime: _ => TimeSpan.FromMilliseconds(400));

        var offsetFrame = await service.GetDisplayFrameAsync(CreateSnapshot(track, 1.7));
        var seekBackFrame = await service.GetDisplayFrameAsync(CreateSnapshot(track, 1.1));

        Assert.Equal(1, offsetFrame.CurrentLineIndex);
        Assert.Equal("Second", offsetFrame.CurrentLine);
        Assert.Equal(0, seekBackFrame.CurrentLineIndex);
        Assert.Equal("First", seekBackFrame.CurrentLine);
    }

    [Fact]
    public async Task ConsecutiveShortLinesSwitchWithoutAnSmtcEvent()
    {
        var track = CreateTrack("Short lines");
        var resolved = CreateResolvedLyrics(
            new ParsedLyricLine(At(1.0), At(1.2), "A"),
            new ParsedLyricLine(At(1.2), At(1.4), "B"),
            new ParsedLyricLine(At(1.4), At(3.0), "C"));
        using var service = await CreateServiceAsync(track, resolved);
        var authoritative = CreateSnapshot(track, 1.05);
        var projected = MediaPresentationTimeline.Project(
            authoritative,
            authoritativeAtUtc: At(0),
            nowUtc: At(400));
        var display = await service.GetDisplayFrameAsync(projected);

        Assert.Equal(2, display.CurrentLineIndex);
        Assert.Equal("C", display.CurrentLine);
        Assert.Equal("", display.NextLine);
    }

    private static async Task<LyricSyncService> CreateServiceAsync(
        TrackInfo track,
        ResolvedLyrics resolved,
        Func<string?, TimeSpan>? getPlayerLeadTime = null)
    {
        var service = new LyricSyncService(
            new NeverResolver(),
            getPlayerLeadTime,
            metadataStabilizationDelay: TimeSpan.Zero);
        await service.GetDisplayFrameAsync(CreateSnapshot(track, 1.0));
        Assert.True(service.TryApplyResolvedLyrics(track, resolved));
        return service;
    }

    private static MediaPresentationInput CreateInput(
        PlaybackSnapshot snapshot,
        MediaLyricsFrame lyrics,
        AppSettings settings) =>
        new(
            snapshot,
            false,
            lyrics.trackId,
            lyrics,
            NativeMediaPayloadFactory.CreateStyle(settings, 1),
            NativeMediaPayloadFactory.EmptyCover(),
            new MediaBrowserFrame(false, "Timeline", "Artist", "qqmusic.exe", true, true, true, true),
            NativeMediaPayloadFactory.CreateSpectrumTuning(SpectrumTuningSettings.CreateDefault()),
            [],
            NativeAppearanceJson.Empty);

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
