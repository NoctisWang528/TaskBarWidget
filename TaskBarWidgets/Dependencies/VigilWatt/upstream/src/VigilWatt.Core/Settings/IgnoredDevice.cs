namespace VigilWatt.Core.Settings;

/// <summary>
/// 功 能： 一条"忽略"记录：设备照常轮询，但不进列表/托盘/贴片，也不写历史。
/// 2026/10/25 12:20:00 20211
/// </summary>
public sealed class IgnoredDevice
{
    /// <summary>忽略时的显示名，供「已忽略设备」列表展示；设备可能已离线，名字只能存下来。</summary>
    public string Name { get; set; } = "";

    public DateTimeOffset IgnoredAt { get; set; }
}
