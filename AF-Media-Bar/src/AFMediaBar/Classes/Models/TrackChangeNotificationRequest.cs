using AFMediaBar.Classes.Settings;

namespace AFMediaBar.Classes.Models;

/// <summary>
/// 从媒体协调层发布到窗口宿主的不可变曲目切换通知请求。
/// Immutable track-change notification request published to the window host.
/// </summary>
public sealed record TrackChangeNotificationRequest(
    MediaSnapshot Snapshot,
    TrackChangeNotificationSettings Settings,
    DisplayMonitorInfo Monitor);
