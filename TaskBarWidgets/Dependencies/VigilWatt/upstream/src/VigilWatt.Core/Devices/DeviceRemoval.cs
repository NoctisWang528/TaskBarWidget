using VigilWatt.Core.Settings;

namespace VigilWatt.Core.Devices;

/// <summary>
/// 功 能： 删除设备后"该不该收这条上报"的判定（纯逻辑，单测直接构造字典）。
/// 2026/10/20 20:25:00 20211
/// 两份名单的用途与顺序见 docs/project/code-flow.md §5.1。
/// </summary>
public static class DeviceRemoval
{
    /// <summary>黑名单一律丢弃；删除过的只在离线时丢弃——设备真回来了就该显示。</summary>
    public static bool ShouldDrop(
        IReadOnlyDictionary<string, BlockedDevice> blocked,
        IReadOnlyDictionary<string, long> dismissed,
        string id,
        bool isOnline)
        => blocked.ContainsKey(id) || (!isOnline && dismissed.ContainsKey(id));

    /// <summary>设备重新上线 → 删除标记失效。调用方负责移除并落盘。</summary>
    public static bool ShouldClearDismissal(
        IReadOnlyDictionary<string, long> dismissed,
        string id,
        bool isOnline)
        => isOnline && dismissed.ContainsKey(id);
}
