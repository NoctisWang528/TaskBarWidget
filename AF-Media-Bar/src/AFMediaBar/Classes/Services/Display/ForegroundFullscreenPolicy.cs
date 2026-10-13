namespace AFMediaBar.Classes.Services;

/// <summary>判断前台窗口是否覆盖其显示器物理边界。 / Determines whether a foreground window covers its display's physical bounds.</summary>
public static class ForegroundFullscreenPolicy
{
    /// <summary>按允许的像素误差判断全屏覆盖。 / Tests fullscreen coverage with a physical-pixel tolerance.</summary>
    public static bool IsFullscreen(Rect windowBounds, Rect monitorBounds, double tolerance = 2)
    {
        if (windowBounds.IsEmpty || monitorBounds.IsEmpty ||
            windowBounds.Width <= 0 || windowBounds.Height <= 0 ||
            monitorBounds.Width <= 0 || monitorBounds.Height <= 0)
        {
            return false;
        }

        var safeTolerance = double.IsFinite(tolerance) ? Math.Max(0, tolerance) : 0;
        return windowBounds.Left <= monitorBounds.Left + safeTolerance &&
               windowBounds.Top <= monitorBounds.Top + safeTolerance &&
               windowBounds.Right >= monitorBounds.Right - safeTolerance &&
               windowBounds.Bottom >= monitorBounds.Bottom - safeTolerance;
    }
}
