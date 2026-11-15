namespace VigilWatt.Core.Settings;

/// <summary>
/// 功 能： 一条"永久移除"记录（黑名单条目），只用于列表展示与解除屏蔽。
/// 2026/10/20 20:25:00 20211
/// </summary>
public sealed class BlockedDevice
{
    /// <summary>拉黑时的显示名，供「已屏蔽设备」列表展示；设备不再扫描，名字只能存下来。</summary>
    public string Name { get; set; } = "";

    public DateTimeOffset BlockedAt { get; set; }
}
