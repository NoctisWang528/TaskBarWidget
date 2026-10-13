using AFMediaBar.Classes.Settings;

namespace AFMediaBar.Classes.Services;

/// <summary>应用通知启用状态和全屏抑制规则。 / Applies notification enablement and fullscreen-suppression rules.</summary>
public static class TrackChangeNotificationPresentationPolicy
{
    /// <summary>判断已识别的曲目切换是否允许呈现。 / Determines whether an identified track change may be presented.</summary>
    public static bool ShouldPresent(TrackChangeNotificationSettings settings, bool isFullscreen)
    {
        var normalized = settings.Normalize();
        return normalized.Enabled && (normalized.ShowWhenFullscreen || !isFullscreen);
    }
}
