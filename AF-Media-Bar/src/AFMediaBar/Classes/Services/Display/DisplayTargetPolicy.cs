using AFMediaBar.Classes.Models;
using AFMediaBar.Classes.Settings;

namespace AFMediaBar.Classes.Services;

/// <summary>解析固定显示器与通知目标，并给出固定目标的回退顺序。 / Resolves fixed displays and notification targets, and provides the fixed-target fallback order.</summary>
public static class DisplayTargetPolicy
{
    /// <summary>按通知模式解析前台或固定目标，并应用固定目标回退链。 / Resolves a foreground or fixed notification target and applies the fixed-target fallback chain.</summary>
    public static DisplayMonitorInfo? ResolveNotification(
        IReadOnlyList<DisplayMonitorInfo> monitors,
        NotificationTargetMode mode,
        string? fixedDeviceId,
        string? foregroundDeviceId)
    {
        if (mode == NotificationTargetMode.ForegroundWindow && !string.IsNullOrWhiteSpace(foregroundDeviceId))
        {
            var foreground = monitors.FirstOrDefault(monitor => string.Equals(
                monitor.DeviceId,
                foregroundDeviceId,
                StringComparison.OrdinalIgnoreCase));
            if (foreground is not null)
                return foreground;
        }

        return ResolveFixed(monitors, fixedDeviceId);
    }

    /// <summary>按设备标识解析显示器，缺失时回退到主屏或首屏。 / Resolves a device identifier, falling back to the primary or first display.</summary>
    public static DisplayMonitorInfo? ResolveFixed(
        IReadOnlyList<DisplayMonitorInfo> monitors,
        string? deviceId)
    {
        if (monitors.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            var matched = monitors.FirstOrDefault(monitor =>
                string.Equals(monitor.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
                return matched;
        }

        return monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? monitors[0];
    }
}
