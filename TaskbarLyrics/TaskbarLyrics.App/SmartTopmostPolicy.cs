namespace TaskbarLyrics.App;

internal readonly record struct SmartTopmostForegroundWindow(
    IntPtr Handle,
    NativeRect VisibleBounds,
    bool IsVisible = true,
    bool IsMinimized = false,
    bool IsOwnedByCurrentProcess = false,
    bool IsDesktopOrShell = false)
{
    public bool IsEligible =>
        Handle != IntPtr.Zero &&
        IsVisible &&
        !IsMinimized &&
        !IsOwnedByCurrentProcess &&
        !IsDesktopOrShell &&
        VisibleBounds.Width > 0 &&
        VisibleBounds.Height > 0;
}

internal readonly record struct SmartTopmostDecision(
    bool ShouldBeTopmost,
    IntPtr WindowToPlaceAfter);

internal static class SmartTopmostPolicy
{
    internal static SmartTopmostDecision Evaluate(
        bool forceAlwaysOnTop,
        NativeRect targetMonitorBounds,
        SmartTopmostForegroundWindow? foregroundWindow)
    {
        if (!forceAlwaysOnTop &&
            foregroundWindow is { IsEligible: true } candidate &&
            CoversMonitor(candidate.VisibleBounds, targetMonitorBounds))
        {
            return new SmartTopmostDecision(false, candidate.Handle);
        }

        return new SmartTopmostDecision(true, IntPtr.Zero);
    }

    internal static bool CoversMonitor(NativeRect windowBounds, NativeRect monitorBounds) =>
        monitorBounds.Width > 0 &&
        monitorBounds.Height > 0 &&
        windowBounds.Left <= monitorBounds.Left &&
        windowBounds.Top <= monitorBounds.Top &&
        windowBounds.Right >= monitorBounds.Right &&
        windowBounds.Bottom >= monitorBounds.Bottom;

    internal static bool RequiresTopmostStateUpdate(
        SmartTopmostDecision? previousDecision,
        SmartTopmostDecision currentDecision) =>
        previousDecision != currentDecision || currentDecision.ShouldBeTopmost;
}
