namespace AFMediaBar.Classes.Models;

/// <summary>
/// 显示器的稳定标识、物理边界、工作区和有效 DPI 快照。
/// Immutable display identity, physical bounds, work area, and effective-DPI snapshot.
/// </summary>
public sealed record DisplayMonitorInfo(
    string DeviceId,
    string DeviceName,
    bool IsPrimary,
    Rect MonitorArea,
    Rect WorkArea,
    uint DpiX,
    uint DpiY);

/// <summary>设置页面使用的显示器选项。 / Display option exposed to settings pages.</summary>
public sealed record DisplayMonitorOption(string DeviceId, string DisplayName, bool IsPrimary, bool IsAvailable = true);
