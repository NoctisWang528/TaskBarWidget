using VigilWatt.Core.Capture;
using Xunit;

namespace VigilWatt.Core.Tests;
/// <summary>
/// 功 能： 采集引导的步骤推进：一步做完进下一步、能回退重做、全部完成后不再前进。
/// 2026/10/24 14:10:00 20211
/// </summary>
public sealed class CaptureGuideTests
{
    [Fact]
    public void Default_HasTheFiveOrderedSteps()
    {
        var guide = CaptureGuide.Default();

        Assert.Equal(5, guide.Steps.Count);
        Assert.Equal("device-action", guide.Steps[0].Id);
        Assert.Equal("plug-charger", guide.Steps[1].Id);
        Assert.Equal("unplug-charger", guide.Steps[2].Id);
        Assert.Equal("power-off", guide.Steps[3].Id);
        Assert.Equal("power-on", guide.Steps[4].Id);
    }

    [Fact]
    public void Steps_HaveUniqueIdsAndExplainHowToDoIt()
    {
        var guide = CaptureGuide.Default();

        Assert.Equal(guide.Steps.Count, guide.Steps.Select(s => s.Id).Distinct().Count());
        Assert.All(guide.Steps, step =>
        {
            Assert.False(string.IsNullOrWhiteSpace(step.Id));
            Assert.False(string.IsNullOrWhiteSpace(step.Title));
            Assert.False(string.IsNullOrWhiteSpace(step.Hint));
        });
    }

    [Fact]
    public void Advance_WalksToTheEnd_ThenRefuses()
    {
        var guide = CaptureGuide.Default();
        Assert.Equal(0, guide.CurrentIndex);
        Assert.False(guide.IsComplete);

        for (var i = 0; i < guide.Steps.Count; i++) Assert.True(guide.Advance());

        Assert.True(guide.IsComplete);
        Assert.Null(guide.Current);
        Assert.False(guide.Advance());
    }

    [Fact]
    public void Back_GoesOneStepBack_AndStopsAtFirst()
    {
        var guide = CaptureGuide.Default();
        guide.Advance();
        guide.Advance();

        Assert.True(guide.Back());
        Assert.Equal(1, guide.CurrentIndex);
        Assert.Equal("plug-charger", guide.Current!.Id);
        Assert.Equal("device-action", guide.Previous!.Id);

        Assert.True(guide.Back());
        Assert.False(guide.Back());
        Assert.Equal(0, guide.CurrentIndex);
    }

    [Fact]
    public void Back_FromCompleteState_AllowsRedo()
    {
        var guide = CaptureGuide.Default();
        while (guide.Advance()) { }
        Assert.True(guide.IsComplete);

        Assert.True(guide.Back());
        Assert.False(guide.IsComplete);
        Assert.Equal("power-on", guide.Current!.Id);
    }

    [Fact]
    public void Reset_ReturnsToFirstStep()
    {
        var guide = CaptureGuide.Default();
        guide.Advance();
        guide.Advance();

        guide.Reset();

        Assert.Equal(0, guide.CurrentIndex);
        Assert.Equal("device-action", guide.Current!.Id);
    }

    [Fact]
    public void Event_KeepsBatteryPercentThroughJson()
    {
        var item = new CaptureEvent
        {
            TimestampMono = 200,
            TimestampUtc = DateTimeOffset.UnixEpoch,
            Kind = CaptureEventKind.BatteryReference,
            Note = "78%",
            Percent = 78,
        };

        var line = CaptureJson.EventLine(item);
        Assert.Contains("battery_reference", line);
        Assert.Contains("\"percent\":78", line);
        Assert.True(CaptureJson.TryParseEvent(line, out var parsed));
        Assert.Equal(CaptureEventKind.BatteryReference, parsed!.Kind);
        Assert.Equal(78, parsed.Percent);
        Assert.Null(parsed.StepId);
    }

    [Fact]
    public void Event_KeepsStepIdThroughJson()
    {
        var item = new CaptureEvent
        {
            TimestampMono = 100,
            TimestampUtc = DateTimeOffset.UnixEpoch,
            Kind = CaptureEventKind.UserMark,
            Note = "连接充电器",
            StepId = "plug-charger",
        };

        var line = CaptureJson.EventLine(item);
        Assert.Contains("plug-charger", line);
        Assert.True(CaptureJson.TryParseEvent(line, out var parsed));
        Assert.Equal("plug-charger", parsed!.StepId);
        Assert.Equal("连接充电器", parsed.Note);
    }
}
