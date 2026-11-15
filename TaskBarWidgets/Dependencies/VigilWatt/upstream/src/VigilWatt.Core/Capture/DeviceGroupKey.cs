namespace VigilWatt.Core.Capture;
/// <summary>
/// 功 能： 把 HID 接口归到「一台物理设备」的分组键：优先系统 ContainerId，取不到退回 VID/PID。
/// 2026/10/24 20:05:00 20211
/// 只用 VID/PID 会把同型号两只 dongle 并成一台（与 CR-74 同一取舍），所以容器优先。
/// </summary>
public static class DeviceGroupKey
{
    /// <summary>有 ContainerId 就用它，否则退回 VID/PID。null 只来自读不到该属性的接口。</summary>
    public static string For(Guid? containerId, ushort vendorId, ushort productId)
        => containerId is { } id ? FromContainer(id) : FromVidPid(vendorId, productId);

    /// <summary>系统的「物理设备」标识：同一个复合设备的全部接口共用一个值。</summary>
    public static string FromContainer(Guid containerId) => $"container:{containerId:D}";

    /// <summary>兜底键：设备不提供 container 描述符（或读属性失败）时用，同型号会被并成一台。</summary>
    public static string FromVidPid(ushort vendorId, ushort productId) => $"vidpid:{vendorId:X4}:{productId:X4}";

    /// <summary>这个键是不是来自系统 ContainerId。false = VID/PID 兜底，分组会粗一些（要如实告诉用户）。</summary>
    public static bool IsContainer(string groupKey) => groupKey.StartsWith("container:", StringComparison.Ordinal);
}
