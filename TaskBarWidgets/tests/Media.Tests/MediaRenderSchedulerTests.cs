using TaskBarWidgets.Media.Integration;
using TaskbarLyrics.App;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class MediaRenderSchedulerTests
{
    [Fact]
    public void VisiblePlayingSurfaceUsesFastPresentationAndLowFrequencyStateRefresh()
    {
        var now = At(0);
        var scheduler = new MediaRenderScheduler(() => now);
        scheduler.SetVisibility(taskbarVisible: true, cardVisible: false);
        scheduler.SetPlaybackState(true);
        scheduler.SetPresentationActive(true);
        scheduler.Initialize();

        var initial = scheduler.Poll();
        Assert.Equal(MediaWorkKind.FullState, initial.Kind);
        scheduler.MarkCompleted(initial);

        Assert.Equal(MediaWorkKind.None, scheduler.Poll().Kind);
        now = At(100);
        Assert.Equal(MediaWorkKind.Presentation, scheduler.Poll().Kind);
        scheduler.MarkCompleted(scheduler.Poll());
        now = At(199);
        Assert.Equal(MediaWorkKind.None, scheduler.Poll().Kind);
        now = At(200);
        Assert.Equal(MediaWorkKind.Presentation, scheduler.Poll().Kind);
        now = At(2000);
        Assert.Equal(MediaWorkKind.FullState, scheduler.Poll().Kind);
    }

    [Fact]
    public void HiddenSurfacesDoNotGenerateHighFrequencyFramesButKeepFallbackDiscovery()
    {
        var now = At(0);
        var scheduler = new MediaRenderScheduler(() => now);
        scheduler.SetVisibility(taskbarVisible: false, cardVisible: false);
        scheduler.SetPlaybackState(true);
        scheduler.Initialize();
        scheduler.MarkCompleted(scheduler.Poll());

        now = At((int)MediaRenderScheduler.FallbackInterval.TotalMilliseconds - 1);
        Assert.Equal(MediaWorkKind.None, scheduler.Poll().Kind);
        now = At((int)MediaRenderScheduler.FallbackInterval.TotalMilliseconds);
        Assert.Equal(MediaWorkKind.FullState, scheduler.Poll().Kind);
    }

    [Fact]
    public void VisiblePausedSurfaceDoesNotRunThePresentationCadence()
    {
        var now = At(0);
        var scheduler = new MediaRenderScheduler(() => now);
        scheduler.SetVisibility(taskbarVisible: true, cardVisible: true);
        scheduler.SetPlaybackState(false);
        scheduler.Initialize();
        scheduler.MarkCompleted(scheduler.Poll());

        now = At(100);
        Assert.Equal(MediaWorkKind.None, scheduler.Poll().Kind);
        now = At(2000);
        Assert.Equal(MediaWorkKind.FullState, scheduler.Poll().Kind);
    }

    [Fact]
    public void NonLyricPresentationDoesNotRunTheFastCadence()
    {
        var now = At(0);
        var scheduler = new MediaRenderScheduler(() => now);
        scheduler.SetVisibility(taskbarVisible: true, cardVisible: false);
        scheduler.SetPlaybackState(true);
        scheduler.SetPresentationActive(false);
        scheduler.Initialize();
        scheduler.MarkCompleted(scheduler.Poll());

        now = At(100);
        Assert.Equal(MediaWorkKind.None, scheduler.Poll().Kind);
        now = At(2000);
        Assert.Equal(MediaWorkKind.FullState, scheduler.Poll().Kind);
    }

    [Fact]
    public void ProviderEventsCoalesceAndAreNotLostWhileWorkIsRunning()
    {
        var now = At(0);
        var scheduler = new MediaRenderScheduler(() => now);
        scheduler.SetVisibility(taskbarVisible: false, cardVisible: false);
        scheduler.Initialize();
        scheduler.MarkCompleted(scheduler.Poll());

        scheduler.RequestState();
        scheduler.RequestState();
        scheduler.RequestState();
        var refresh = scheduler.Poll();
        Assert.Equal(MediaWorkKind.FullState, refresh.Kind);

        scheduler.RequestState();
        scheduler.MarkCompleted(refresh);
        Assert.Equal(MediaWorkKind.FullState, scheduler.Poll().Kind);
    }

    [Fact]
    public void FullStateRequestArrivingDuringWorkKeepsItsMetadataRefresh()
    {
        var now = At(0);
        var scheduler = new MediaRenderScheduler(() => now);
        scheduler.SetVisibility(taskbarVisible: false, cardVisible: false);
        scheduler.Initialize();
        scheduler.MarkCompleted(scheduler.Poll());

        scheduler.RequestState(full: true);
        var inFlight = scheduler.Poll();
        scheduler.RequestState(full: true);
        scheduler.MarkCompleted(inFlight);

        var next = scheduler.Poll();
        Assert.Equal(MediaWorkKind.FullState, next.Kind);
        Assert.True(next.ForceMetadataRefresh);
    }

    [Fact]
    public void ShowingASurfaceTriggersImmediateStateReconciliation()
    {
        var now = At(0);
        var scheduler = new MediaRenderScheduler(() => now);
        scheduler.SetVisibility(taskbarVisible: false, cardVisible: false);
        scheduler.Initialize();
        scheduler.MarkCompleted(scheduler.Poll());

        now = At(500);
        scheduler.SetVisibility(taskbarVisible: false, cardVisible: true);
        Assert.Equal(MediaWorkKind.FullState, scheduler.Poll().Kind);
    }

    [Fact]
    public void DeferredStateRunsAtTheStabilityDeadline()
    {
        var now = At(0);
        var scheduler = new MediaRenderScheduler(() => now);
        scheduler.SetVisibility(taskbarVisible: true, cardVisible: true);
        scheduler.Initialize();
        scheduler.MarkCompleted(scheduler.Poll());

        scheduler.DeferState(PlaybackSnapshotStabilityGate.QuietPeriod);
        now = At((int)PlaybackSnapshotStabilityGate.QuietPeriod.TotalMilliseconds - 1);
        Assert.Equal(MediaWorkKind.None, scheduler.Poll().Kind);
        now = At((int)PlaybackSnapshotStabilityGate.QuietPeriod.TotalMilliseconds);
        Assert.Equal(MediaWorkKind.FullState, scheduler.Poll().Kind);
    }

    [Fact]
    public void ProcessFallbackObservationIsRefreshedAtTheCompatibilityInterval()
    {
        var now = At(0);
        var calls = 0;
        var cache = new ProcessFallbackDetectionCache(() => now);
        ProcessFallbackObservation Detect()
        {
            calls++;
            return new ProcessFallbackObservation("Netease", "Title", "Artist");
        }

        Assert.Equal("Netease", cache.GetOrRefresh(Detect).Source);
        now = now.Add(TimeSpan.FromMilliseconds(999));
        Assert.Equal("Netease", cache.GetOrRefresh(Detect).Source);
        Assert.Equal(1, calls);

        now = now.Add(TimeSpan.FromMilliseconds(1));
        Assert.Equal("Netease", cache.GetOrRefresh(Detect).Source);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void FailedProcessFallbackObservationIsCachedUntilTheRetryInterval()
    {
        var now = At(0);
        var calls = 0;
        var cache = new ProcessFallbackDetectionCache(() => now);
        ProcessFallbackObservation Detect()
        {
            calls++;
            return new ProcessFallbackObservation(
                null,
                string.Empty,
                string.Empty,
                ProcessQuerySucceeded: false);
        }

        Assert.False(cache.GetOrRefresh(Detect).ProcessQuerySucceeded);
        now = now.Add(TimeSpan.FromMilliseconds(999));
        Assert.False(cache.GetOrRefresh(Detect).ProcessQuerySucceeded);
        Assert.Equal(1, calls);

        now = now.Add(TimeSpan.FromMilliseconds(1));
        Assert.False(cache.GetOrRefresh(Detect).ProcessQuerySucceeded);
        Assert.Equal(2, calls);
    }

    private static DateTimeOffset At(int milliseconds) =>
        new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero)
            .AddMilliseconds(milliseconds);
}
