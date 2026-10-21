using System.Text.Json;
using TaskBarWidgets.UI;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class PanelToggleTests
{
    [Fact]
    public void BusyDispatcherKeepsOneLatestToggleAndOriginalWaitTime()
    {
        var pending = new PendingPanelToggle();
        var started = DateTimeOffset.UtcNow;
        var callbacks = 0;
        for (var gesture = 1; gesture <= 30; gesture++)
            if (pending.Post(JsonSerializer.SerializeToElement(new { gesture, x = gesture * 10 }), started.AddMilliseconds(gesture))) callbacks++;
        Assert.Equal(1, callbacks);
        var click = pending.Take()!.Value;
        Assert.Equal(30, click.Message.GetProperty("gesture").GetInt32());
        Assert.Equal(300, click.Message.GetProperty("x").GetInt32());
        Assert.Equal(started.AddMilliseconds(1), click.ReceivedAt);
        Assert.Null(pending.Take());
        Assert.True(pending.Post(JsonSerializer.SerializeToElement(new { gesture = 31 }), started.AddSeconds(2)));
        Assert.Equal(31, pending.Take()!.Value.Message.GetProperty("gesture").GetInt32());
    }

    [Fact]
    public void ConcurrentRetriesScheduleOnlyOneCallback()
    {
        var pending = new PendingPanelToggle();
        var callbacks = 0;
        Parallel.For(0, 100, gesture =>
        {
            if (pending.Post(JsonSerializer.SerializeToElement(new { gesture }), DateTimeOffset.UtcNow))
                Interlocked.Increment(ref callbacks);
        });
        Assert.Equal(1, callbacks);
        Assert.NotNull(pending.Take());
        Assert.Null(pending.Take());
    }
}
