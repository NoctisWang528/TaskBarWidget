using TaskBarWidgets.Media.Integration;
using TaskbarLyrics.App;
using TaskbarLyrics.Core.Abstractions;
using TaskbarLyrics.Core.Models;
using TaskbarLyrics.Core.Services;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class DouyinMediaTests
{
    [Theory]
    [InlineData("electron.app.douyin", "Custom player")]
    [InlineData("ELECTRON.APP.DOUYIN", "Custom player")]
    [InlineData("mapped-id", "Douyin")]
    [InlineData("mapped-id", "抖音")]
    [InlineData("mapped-id", "electron.app.douyin")]
    public async Task DouyinNeverReachesLyricResolutionWhilePlayingOrPaused(string id, string source)
    {
        using var resolver = new CountingResolver();
        using var lyrics = new LyricSyncService(resolver, metadataStabilizationDelay: TimeSpan.Zero);
        var snapshot = Snapshot(id, source);
        foreach (var playing in new[] { true, false })
        {
            var input = MediaCoordinator.LyricsInput(snapshot with { IsPlaying = playing });
            Assert.Null(input.Track);
            await lyrics.GetDisplayFrameAsync(input);
        }
        Assert.Equal(0, resolver.Calls);
        Assert.Equal("Douyin", SmtcMusicSessionProvider.NormalizeSource("electron.app.douyin"));
    }

    [Theory]
    [InlineData("electron.app.other", "Other player")]
    [InlineData("QQMusic.exe", "QQMusic")]
    [InlineData("cloudmusic.exe", "Netease")]
    public void OtherElectronAppsAndMusicKeepTheirExistingLyricRoute(string id, string source)
    {
        var snapshot = Snapshot(id, source);
        Assert.False(NonLyricsMediaPolicy.IsDouyin(snapshot));
        Assert.Same(snapshot, MediaCoordinator.LyricsInput(snapshot));
    }

    [Fact]
    public void BrowserStillUsesNonLyricsRouteWithCustomMappedName()
    {
        Assert.Null(MediaCoordinator.LyricsInput(Snapshot("msedge.exe", "Custom player")).Track);
    }

    [Theory]
    [InlineData("QQMusic.exe")]
    [InlineData("electron.app.douyin")]
    public void PlayingMusicHasPriorityOverDouyinRegardlessOfCurrentSource(string currentId)
    {
        MediaSessionCandidate[] candidates = [
            new("QQMusic.exe", "QQMusic", true, 10),
            new("electron.app.douyin", "Custom player", true, -1)];
        Assert.Equal("QQMusic.exe", MediaSourcePolicy.Select(candidates, null, currentId));
        Assert.Equal("electron.app.douyin", MediaSourcePolicy.Select(candidates, "electron.app.douyin", currentId));
    }

    [Fact]
    public void PausedQqDoesNotDisplacePresentDouyinAndPlayingQqTakesOver()
    {
        MediaSessionCandidate[] candidates = [
            new("QQMusic.exe", "QQMusic", false, 0),
            new("electron.app.douyin", "Douyin", false, int.MaxValue)];
        Assert.Equal("electron.app.douyin", MediaSourcePolicy.Select(candidates, null, "electron.app.douyin"));
        Assert.Equal("QQMusic.exe", MediaSourcePolicy.Select([candidates[0] with { IsPlaying = true }, candidates[1]], null, "electron.app.douyin"));
    }

    [Fact]
    public void ChangingLossCandidatesDoNotExtendTheFirstDeadline()
    {
        var gate = new DouyinPlaybackGate();
        var playing = Snapshot();
        gate.Observe(playing, 0);
        Assert.True(gate.Observe(playing with { IsPlaying = false }, 100).IsPlaying);
        Assert.Equal(playing with { CanSeek = false }, gate.Observe(Snapshot("QQMusic.exe", "QQMusic", false), 400));
        Assert.Equal(playing with { CanSeek = false }, gate.Observe(Empty(), 849));
        Assert.Equal(Empty(), gate.Observe(Empty(), 850));
        Assert.Equal(Empty(), gate.Observe(Empty(), 1000));
    }

    [Fact]
    public void BriefGapRecoveryKeepsTheNativeStateTupleWhileTracksAndPositionsChange()
    {
        var gate = new DouyinPlaybackGate();
        var first = Snapshot();
        gate.Observe(first, 0);
        var held = gate.Observe(Snapshot("QQMusic.exe", "QQMusic", false), 100);
        var next = first with { Position = TimeSpan.FromSeconds(4), Track = first.Track! with { Id = "next", Title = "Next video" } };
        var recovered = gate.Observe(next, 500);
        Assert.Equal(NativeState(first), NativeState(held));
        Assert.Equal(NativeState(first), NativeState(recovered));
        Assert.Equal("Next video", recovered.Track!.Title);
        Assert.True(gate.Observe(next with { IsPlaying = false }, 600).IsPlaying);
        Assert.False(gate.Observe(next with { IsPlaying = false }, 1350).IsPlaying);
    }

    [Fact]
    public void PersistentSameSourcePauseCommitsOnceAndDoesNotRearm()
    {
        var gate = new DouyinPlaybackGate();
        var playing = Snapshot();
        var paused = playing with { IsPlaying = false };
        gate.Observe(playing, 0);
        Assert.True(gate.Observe(paused, 10).IsPlaying);
        Assert.True(gate.Observe(paused, 759).IsPlaying);
        Assert.False(gate.Observe(paused, 760).IsPlaying);
        Assert.False(gate.Observe(paused, 800).IsPlaying);
        Assert.True(gate.Observe(playing, 801).IsPlaying);
    }

    [Fact]
    public void FinalRefreshReobservesRawFallbackInsteadOfHeldPlayingOutput()
    {
        long now = 0;
        using var provider = new SmtcMusicSessionProvider(() => now);
        var playing = Snapshot();
        provider.CommitPlaybackSnapshot(playing);
        now = 100;
        var fallback = Snapshot("QQMusic.exe", "QQMusic", false);
        var held = provider.CommitPlaybackSnapshot(fallback);
        now = 849;
        Assert.Equal(NativeState(playing), NativeState(provider.RefreshPlaybackState(held)));
        now = 850;
        Assert.Equal(fallback, provider.RefreshPlaybackState(held));
        Assert.Equal(fallback, provider.RefreshPlaybackState(held));
    }

    [Fact]
    public void FinalRefreshAlsoExpiresSameSourcePause()
    {
        long now = 0;
        using var provider = new SmtcMusicSessionProvider(() => now);
        var playing = Snapshot();
        provider.CommitPlaybackSnapshot(playing);
        now = 10;
        var held = provider.CommitPlaybackSnapshot(playing with { IsPlaying = false });
        now = 760;
        Assert.False(provider.RefreshPlaybackState(held).IsPlaying);
        Assert.False(provider.RefreshPlaybackState(held).IsPlaying);
    }

    [Fact]
    public void LatePauseSelectionAndExpiryDiscardPreparedContentButPositionDoesNot()
    {
        var prepared = Snapshot();
        Assert.False(MediaCoordinator.CanPublishSnapshot(prepared, prepared with { IsPlaying = false }));
        Assert.False(MediaCoordinator.CanPublishSnapshot(prepared, Snapshot("QQMusic.exe", "QQMusic")));
        Assert.False(MediaCoordinator.CanPublishSnapshot(prepared, Empty()));
        Assert.False(MediaCoordinator.CanPublishSnapshot(prepared, prepared with {
            Track = prepared.Track! with { Title = "Next video" } }));
        Assert.True(MediaCoordinator.CanPublishSnapshot(prepared, prepared with { Position = TimeSpan.FromSeconds(5) }));
        long now = 0;
        using var provider = new SmtcMusicSessionProvider(() => now);
        provider.CommitPlaybackSnapshot(prepared);
        provider.SelectSource("QQMusic.exe"); // Selection during an await invalidates cached raw output immediately.
        Assert.False(MediaCoordinator.CanPublishSnapshot(prepared, provider.RefreshPlaybackState(prepared)));
    }

    [Fact]
    public void CompetingPlayingSourceBypassesGrace()
    {
        var gate = new DouyinPlaybackGate();
        gate.Observe(Snapshot(), 0);
        gate.Observe(Empty(), 10);
        var qq = Snapshot("QQMusic.exe", "QQMusic");
        Assert.Same(qq, gate.Observe(qq, 20));
        Assert.Null(gate.ControlSourceId);
    }

    [Fact]
    public void ExplicitPauseWinsImmediatelyOverLossAndBoundedStaleReadback()
    {
        var gate = new DouyinPlaybackGate();
        var playing = Snapshot();
        gate.Observe(playing, 0);
        gate.Observe(playing with { IsPlaying = false }, 10);
        gate.ConfirmPlayback(false, 20);
        Assert.False(gate.Current!.IsPlaying);
        Assert.False(gate.Observe(playing, 21).IsPlaying);
        Assert.False(gate.Observe(playing with { IsPlaying = false }, 30).IsPlaying);
        Assert.False(gate.Observe(playing, 31).IsPlaying);
        Assert.True(gate.Observe(playing, 770).IsPlaying); // No indefinite optimistic pause.
    }

    [Fact]
    public void ExplicitPlayAndCompetingMusicBypassPendingPauseReadback()
    {
        var gate = new DouyinPlaybackGate();
        var playing = Snapshot();
        gate.Observe(playing, 0);
        gate.ConfirmPlayback(false, 10);
        gate.ConfirmPlayback(true, 11);
        Assert.True(gate.Observe(playing, 12).IsPlaying);
        gate.ConfirmPlayback(false, 20);
        var qq = Snapshot("QQMusic.exe", "QQMusic");
        Assert.Same(qq, gate.Observe(qq, 21));
    }

    [Theory]
    [InlineData("QQMusic.exe", "QQMusic")]
    [InlineData("electron.app.douyin", "Douyin")]
    public void ExplicitSourceSelectionClearsPendingGrace(string id, string source)
    {
        long now = 0;
        using var provider = new SmtcMusicSessionProvider(() => now);
        provider.CommitPlaybackSnapshot(Snapshot());
        now = 10;
        provider.CommitPlaybackSnapshot(Empty());
        provider.SelectSource(id);
        var selected = Snapshot(id, source, false);
        Assert.Same(selected, provider.CommitPlaybackSnapshot(selected));
        Assert.False(provider.RefreshPlaybackState(selected).IsPlaying);
    }

    [Fact]
    public void ControlsFailClosedDuringLossAndFindRecreatedSameSourceById()
    {
        var gate = new DouyinPlaybackGate();
        gate.Observe(Snapshot(), 0);
        gate.Observe(Snapshot("QQMusic.exe", "QQMusic", false), 10);
        var qq = new Session("QQMusic.exe");
        var recreated = new Session("ELECTRON.APP.DOUYIN");
        Assert.Null(gate.FindControlSession(new[] { qq }, session => session.Id));
        Assert.Same(recreated, gate.FindControlSession(new[] { qq, recreated }, session => session.Id));
        gate.Observe(Snapshot(), 20);
        Assert.Same(recreated, gate.FindControlSession(new[] { qq, recreated }, session => session.Id));
    }

    [Fact]
    public void MissingTrackWithStalePlayingFlagExpiresAndCannotHoldPhantomMedia()
    {
        var gate = new DouyinPlaybackGate();
        gate.Observe(Snapshot(), 0);
        var missing = Snapshot() with { Track = null };
        Assert.NotNull(gate.Observe(missing, 10).Track);
        Assert.Same(missing, gate.Observe(missing, 760));
        Assert.Null(gate.ControlSourceId);
        Assert.Equal(Empty(), gate.Observe(Empty(), 761));
        Assert.Null(gate.Observe(missing, 800).Track); // Initial phantom input never arms retention.
        Assert.Equal(Empty(), gate.Observe(Empty(), 801));
    }

    [Theory]
    [InlineData("QQMusic.exe", "QQMusic")]
    [InlineData("msedge.exe", "Browser")]
    [InlineData("electron.app.other", "Other")]
    public void OtherSourcesNeverReceiveDouyinGrace(string id, string source)
    {
        var gate = new DouyinPlaybackGate();
        gate.Observe(Snapshot(id, source), 0);
        Assert.Equal(Empty(), gate.Observe(Empty(), 1));
    }

    private static PlaybackSnapshot Snapshot(string id = "electron.app.douyin", string source = "Douyin", bool playing = true) =>
        new(playing, TimeSpan.Zero, new TrackInfo("video", "抖音-记录美好生活", "channel", "", source,
            TimeSpan.FromMinutes(1)), SourceAppUserModelId: id, CanSeek: true);

    private static PlaybackSnapshot Empty() => new(false, TimeSpan.Zero, null);
    private static (string, bool, bool) NativeState(PlaybackSnapshot snapshot) =>
        (snapshot.SourceAppUserModelId, snapshot.IsPlaying, snapshot.Track is not null);

    private sealed record Session(string Id);
    private sealed class CountingResolver : ILyricResolutionCoordinator
    {
        internal int Calls;
        public Task<ResolvedLyrics?> ResolveAsync(TrackInfo track, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<ResolvedLyrics?>(null);
        }
        public void Dispose() { }
    }
}
