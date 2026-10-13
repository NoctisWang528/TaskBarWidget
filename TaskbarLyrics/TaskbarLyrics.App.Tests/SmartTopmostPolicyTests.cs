using Xunit;

namespace TaskbarLyrics.App.Tests;

public sealed class SmartTopmostPolicyTests
{
    private static readonly NativeRect PrimaryMonitor = new(0, 0, 1920, 1080);

    [Fact]
    public void EvaluateWhenForceAlwaysOnTopIsEnabledKeepsLyricsTopmost()
    {
        var foreground = CreateForegroundWindow(new NativeRect(0, 0, 1920, 1080));

        var result = SmartTopmostPolicy.Evaluate(true, PrimaryMonitor, foreground);

        Assert.True(result.ShouldBeTopmost);
        Assert.Equal(IntPtr.Zero, result.WindowToPlaceAfter);
    }

    [Fact]
    public void EvaluateWhenEligibleForegroundWindowCoversTargetMonitorYieldsToForegroundWindow()
    {
        var handle = new IntPtr(42);
        var foreground = CreateForegroundWindow(new NativeRect(0, 0, 1920, 1080), handle);

        var result = SmartTopmostPolicy.Evaluate(false, PrimaryMonitor, foreground);

        Assert.False(result.ShouldBeTopmost);
        Assert.Equal(handle, result.WindowToPlaceAfter);
    }

    [Theory]
    [InlineData(1, 0, 1920, 1080)]
    [InlineData(0, 1, 1920, 1080)]
    [InlineData(0, 0, 1919, 1080)]
    [InlineData(0, 0, 1920, 1079)]
    public void EvaluateWhenForegroundWindowDoesNotCoverTargetMonitorKeepsLyricsTopmost(
        int left,
        int top,
        int right,
        int bottom)
    {
        var foreground = CreateForegroundWindow(new NativeRect(left, top, right, bottom));

        var result = SmartTopmostPolicy.Evaluate(false, PrimaryMonitor, foreground);

        Assert.True(result.ShouldBeTopmost);
        Assert.Equal(IntPtr.Zero, result.WindowToPlaceAfter);
    }

    [Fact]
    public void EvaluateWhenForegroundWindowIsNotEligibleKeepsLyricsTopmost()
    {
        var foreground = CreateForegroundWindow(
            new NativeRect(0, 0, 1920, 1080),
            isDesktopOrShell: true);

        var result = SmartTopmostPolicy.Evaluate(false, PrimaryMonitor, foreground);

        Assert.True(result.ShouldBeTopmost);
        Assert.Equal(IntPtr.Zero, result.WindowToPlaceAfter);
    }

    [Fact]
    public void EvaluateSupportsNegativeCoordinateTargetMonitor()
    {
        var targetMonitor = new NativeRect(-2560, 0, 0, 1440);
        var handle = new IntPtr(99);
        var foreground = CreateForegroundWindow(targetMonitor, handle);

        var result = SmartTopmostPolicy.Evaluate(false, targetMonitor, foreground);

        Assert.False(result.ShouldBeTopmost);
        Assert.Equal(handle, result.WindowToPlaceAfter);
    }

    [Fact]
    public void RequiresTopmostStateUpdateWhenTopmostDecisionIsUnchangedReassertsTopmost()
    {
        var decision = new SmartTopmostDecision(true, IntPtr.Zero);

        var result = SmartTopmostPolicy.RequiresTopmostStateUpdate(decision, decision);

        Assert.True(result);
    }

    [Fact]
    public void RequiresTopmostStateUpdateWhenYieldDecisionIsUnchangedDoesNotReassertTopmost()
    {
        var foregroundHandle = new IntPtr(42);
        var decision = new SmartTopmostDecision(false, foregroundHandle);

        var result = SmartTopmostPolicy.RequiresTopmostStateUpdate(decision, decision);

        Assert.False(result);
    }

    private static SmartTopmostForegroundWindow CreateForegroundWindow(
        NativeRect bounds,
        IntPtr? handle = null,
        bool isDesktopOrShell = false) =>
        new(
            handle ?? new IntPtr(1),
            bounds,
            IsDesktopOrShell: isDesktopOrShell);
}
