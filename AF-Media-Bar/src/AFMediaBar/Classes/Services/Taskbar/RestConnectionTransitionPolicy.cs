// Resolves one shared rest-layer transition frame so text never covers a moving widget.
// The policy owns no WPF visuals or timers; the media control applies its geometry.
namespace AFMediaBar.Classes.Services;

/// <summary>媒体连接切换时的组件位置和文字揭示边界。/ Widget positions and text reveal bounds during a media connection change.</summary>
public static class RestConnectionTransitionPolicy
{
    /// <summary>按同一进度从旧位置移动到新位置。/ Moves from the old position to the new one using the shared progress.</summary>
    public static double WidgetLeft(double sourceLeft, double targetLeft, double progress) =>
        sourceLeft + (targetLeft - sourceLeft) * Math.Clamp(progress, 0, 1);

    /// <summary>把文字右缘限制在最近组件左侧。/ Keeps the text's right edge behind the nearest widget.</summary>
    public static double VisibleTextWidth(
        double sourceWidth,
        double targetWidth,
        double progress,
        double textLeft,
        double nearestWidgetLeft,
        double maximumWidth)
    {
        var desired = sourceWidth + (targetWidth - sourceWidth) * Math.Clamp(progress, 0, 1);
        var available = nearestWidgetLeft - textLeft;
        return Math.Clamp(Math.Min(desired, available), 0, maximumWidth);
    }
}
