using TaskBarWidgets.Media.Integration;
using TaskbarLyrics.App;
using TaskbarLyrics.Core.Abstractions;
using TaskbarLyrics.Core.Models;
using TaskbarLyrics.Core.Services;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class MediaLyricsFlowTests
{
    [Fact]
    public async Task AsyncLyricsCompletionAdvancesTheAuthoritativeFrame()
    {
        using var resolver = new DelayedResolver();
        using var service = new LyricSyncService(
            resolver,
            metadataStabilizationDelay: TimeSpan.Zero);
        var track = new TrackInfo(
            "song",
            "Title",
            "Artist",
            "Album",
            "QQMusic",
            TimeSpan.FromMinutes(4));
        var snapshot = new PlaybackSnapshot(
            true,
            TimeSpan.FromSeconds(2),
            track,
            SourceAppUserModelId: "qqmusic.exe");

        var searching = await service.GetDisplayFrameAsync(snapshot);
        Assert.Equal(LyricSyncService.SearchingText, searching.CurrentLine);

        resolver.Complete(CreateResolvedLyrics());
        await WaitUntilAsync(() => service.CurrentLyricAcquisition == LyricAcquisitionKind.Remote);

        var frame = await service.GetDisplayFrameAsync(snapshot);
        Assert.Equal("Hello", frame.CurrentLine);
        Assert.Equal("World", frame.NextLine);
        Assert.Equal(0, frame.CurrentLineIndex);
        Assert.Equal("你好", frame.CurrentTranslation);
        Assert.True(frame.HasTrackTranslation);
    }

    [Fact]
    public async Task BrowserMediaNeverStartsLyricResolution()
    {
        using var resolver = new CountingResolver();
        using var service = new LyricSyncService(
            resolver,
            metadataStabilizationDelay: TimeSpan.Zero);
        var browserTrack = new TrackInfo(
            "video",
            "Video title",
            "Channel",
            "",
            "Browser",
            TimeSpan.FromMinutes(2));
        var snapshot = new PlaybackSnapshot(
            true,
            TimeSpan.FromSeconds(5),
            browserTrack,
            SourceAppUserModelId: "msedge.exe");

        await service.GetDisplayFrameAsync(MediaCoordinator.LyricsInput(snapshot));
        await Task.Delay(25);
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task LatePreviousTrackLyricsCannotReplaceTheCurrentTrack()
    {
        using var resolver = new SwitchableResolver();
        using var service = new LyricSyncService(
            resolver,
            metadataStabilizationDelay: TimeSpan.Zero);
        var first = CreateSnapshot("First", "Artist", "qqmusic.exe", "QQMusic");
        var second = CreateSnapshot("Second", "Artist", "cloudmusic.exe", "Netease");

        await service.GetDisplayFrameAsync(first);
        await service.GetDisplayFrameAsync(second);
        resolver.Complete("First", CreateResolvedLyrics("Old", "Previous"));
        await Task.Delay(50);
        Assert.Equal(LyricAcquisitionKind.Searching, service.CurrentLyricAcquisition);

        resolver.Complete("Second", CreateResolvedLyrics("New", "Current"));
        await WaitUntilAsync(() => service.CurrentLyricAcquisition == LyricAcquisitionKind.Remote);
        var frame = await service.GetDisplayFrameAsync(second);
        Assert.Equal("New", frame.CurrentLine);
    }

    [Fact]
    public void PresentationProjectionClampsWordScanAndCarriesTranslationMode()
    {
        var settings = new AppSettings { EnableWordScanning = true, ShowLyricTranslation = true };
        var snapshot = new PlaybackSnapshot(
            true,
            TimeSpan.FromSeconds(2),
            new TrackInfo("song", "Title", "Artist", "", "QQMusic", TimeSpan.FromMinutes(4)));
        var frame = new LyricDisplayFrame(
            "Hello",
            "World",
            "Title",
            2.5,
            3,
            false,
            WordScanProgress: 1.5,
            CurrentTranslation: "你好",
            NextTranslation: "世界",
            HasTrackTranslation: true);

        var lyrics = LyricsPresentationCalculator.Create(
            snapshot,
            frame,
            settings,
            LyricsPresentationScene.Lyrics,
            isBrowser: false,
            LyricAcquisitionKind.Remote);

        Assert.Equal(1, lyrics.progress);
        Assert.Equal(1, lyrics.wordScanProgress);
        Assert.True(lyrics.translationMode);
        Assert.Equal("lyrics", lyrics.scene);
        Assert.Equal(3, lyrics.currentLineIndex);
    }

    [Fact]
    public void SearchingPlaceholderCannotRetainAnActionableLyricLineIndex()
    {
        var snapshot = new PlaybackSnapshot(
            true,
            TimeSpan.Zero,
            new TrackInfo("song", "Title", "Artist", "", "QQMusic", TimeSpan.FromMinutes(4)));
        var searching = new LyricDisplayFrame(
            LyricSyncService.SearchingText,
            "",
            "Title",
            0,
            2);

        var lyrics = LyricsPresentationCalculator.Create(
            snapshot,
            searching,
            new AppSettings(),
            LyricsPresentationScene.Searching,
            isBrowser: false,
            LyricAcquisitionKind.Searching);

        Assert.Equal(-1, lyrics.currentLineIndex);
        Assert.Equal("Title - Artist", lyrics.current);
    }

    private static ResolvedLyrics CreateResolvedLyrics()
        => CreateResolvedLyrics("Hello", "World");

    private static ResolvedLyrics CreateResolvedLyrics(string firstLine, string secondLine)
    {
        var parsed = new ParsedLyrics(
            [
                new ParsedLyricLine(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(3),
                    firstLine,
                    "你好"),
                new ParsedLyricLine(
                    TimeSpan.FromSeconds(3),
                    TimeSpan.FromSeconds(5),
                    secondLine,
                    "世界")
            ],
            LyricTimingKind.LineTimed,
            LyricTimingProvenance.ProviderSupplied,
            LyricPayloadFormat.Lrc);
        return new ResolvedLyrics(
            parsed,
            KnownLyricProviders.QQMusic,
            "candidate",
            LyricAcquisitionKind.Remote,
            new Dictionary<string, string> { ["elapsedMs"] = "5" });
    }

    private static PlaybackSnapshot CreateSnapshot(
        string title,
        string artist,
        string sourceId,
        string source) =>
        new(
            true,
            TimeSpan.FromSeconds(2),
            new TrackInfo(
                title,
                title,
                artist,
                "Album",
                source,
                TimeSpan.FromMinutes(4)),
            SourceAppUserModelId: sourceId);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class DelayedResolver : ILyricResolutionCoordinator
    {
        private readonly TaskCompletionSource<LyricResolutionResult> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Complete(ResolvedLyrics lyrics) =>
            _result.TrySetResult(new LyricResolutionResult(
                lyrics,
                LyricResolutionStatus.Succeeded));

        public async Task<ResolvedLyrics?> ResolveAsync(
            TrackInfo track,
            CancellationToken cancellationToken = default) =>
            (await ResolveWithResultAsync(track, cancellationToken)).Lyrics;

        public Task<LyricResolutionResult> ResolveWithResultAsync(
            TrackInfo track,
            CancellationToken cancellationToken = default) =>
            _result.Task.WaitAsync(cancellationToken);

        public void Dispose()
        {
        }
    }

    private sealed class CountingResolver : ILyricResolutionCoordinator
    {
        internal int Calls;

        public Task<ResolvedLyrics?> ResolveAsync(
            TrackInfo track,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<ResolvedLyrics?>(null);
        }

        public void Dispose()
        {
        }
    }

    private sealed class SwitchableResolver : ILyricResolutionCoordinator
    {
        private readonly Dictionary<string, TaskCompletionSource<LyricResolutionResult>> _results =
            new(StringComparer.Ordinal);

        internal void Complete(string title, ResolvedLyrics lyrics)
        {
            Get(title).TrySetResult(new LyricResolutionResult(
                lyrics,
                LyricResolutionStatus.Succeeded));
        }

        public async Task<ResolvedLyrics?> ResolveAsync(
            TrackInfo track,
            CancellationToken cancellationToken = default) =>
            (await ResolveWithResultAsync(track, cancellationToken)).Lyrics;

        public Task<LyricResolutionResult> ResolveWithResultAsync(
            TrackInfo track,
            CancellationToken cancellationToken = default) =>
            Get(track.Title).Task.WaitAsync(cancellationToken);

        public void Dispose()
        {
        }

        private TaskCompletionSource<LyricResolutionResult> Get(string title)
        {
            if (!_results.TryGetValue(title, out var result))
            {
                result = new TaskCompletionSource<LyricResolutionResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _results[title] = result;
            }
            return result;
        }
    }
}
