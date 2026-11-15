namespace VigilWatt.Core.Capture;
/// <summary>
/// 功 能： 采集目标设备与接口的元信息（写进 device.json）。字段取不到就为 null，不填猜测值。
/// 2026/10/24 12:40:43 20211
/// </summary>
public sealed class CaptureDeviceInfo
{
    /// <summary>会话内稳定标识：cap:VID:PID:接口段（如 cap:24AE:1412:mi01col09）。</summary>
    public required string DeviceId { get; init; }

    public required ushort VendorId { get; init; }

    public required ushort ProductId { get; init; }

    public ushort VersionNumber { get; init; }

    public string Product { get; init; } = "";

    public string Manufacturer { get; init; } = "";

    /// <summary>序列号。设备不提供时为 null（**不是空串、不是占位**）。</summary>
    public string? SerialNumber { get; init; }

    /// <summary>HID 接口设备路径。</summary>
    public required string InterfacePath { get; init; }

    /// <summary>接口段（mi01col09），用于同一物理设备的多接口分组。</summary>
    public required string InterfaceKey { get; init; }

    /// <summary>同属一台物理设备的分组键：`container:{guid}`，读不到 ContainerId 时为 `vidpid:VID:PID`。</summary>
    public required string GroupKey { get; init; }

    public ushort UsagePage { get; init; }

    public ushort Usage { get; init; }

    public ushort InputReportLength { get; init; }

    public ushort OutputReportLength { get; init; }

    public ushort FeatureReportLength { get; init; }

    /// <summary>caps 里真实存在的 report ID（去重）。空数组 = 该接口不使用 report ID 或读不到 caps。</summary>
    public IReadOnlyList<byte> InputReportIds { get; init; } = [];

    /// <summary>该接口是否使用 report ID。**用它判断报文首字节是不是 ID**，别自己数列表长度。</summary>
    public bool UsesReportId => InputReportIds.Count > 0;

    /// <summary>输出方向是否用 report ID。发帧时按它决定首字节是不是 ID（和输入方向可能不同）。</summary>
    public bool UsesOutputReportId => OutputReportIds.Count > 0;

    public IReadOnlyList<byte> OutputReportIds { get; init; } = [];

    public IReadOnlyList<byte> FeatureReportIds { get; init; } = [];

    /// <summary>是否成功拿到 caps（HidP_GetCaps）。false = 该接口的能力信息不完整。</summary>
    public bool CapsAvailable { get; init; }

    /// <summary>caps 明细：每个字段的 usage / bit size / logical range。原始描述符拿不到时的替代。</summary>
    public IReadOnlyList<HidFieldInfo> Fields { get; init; } = [];

    /// <summary>厂商定义用途页（≥0xFF00）才可能有私有协议；标准页只会有键鼠/消费类报文。</summary>
    public bool IsVendorDefined => UsagePage >= 0xFF00;

    /// <summary>打开该接口是否被拒（保护 collection）。列表里要照样显示，不能藏。</summary>
    public bool AccessDenied { get; init; }

    /// <summary>打不开时的 Win32 错误码。</summary>
    public int? OpenError { get; init; }

    public string VidPid => $"VID_{VendorId:X4}&PID_{ProductId:X4}";
}
