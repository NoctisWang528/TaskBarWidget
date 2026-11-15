namespace VigilWatt.Core.Capture;
/// <summary>
/// 功 能： 会话事件（用户操作标记、暂停/继续、设备插拔）。**独立于原始记录流**。
/// 2026/10/24 12:40:43 20211
/// </summary>
public sealed class CaptureEvent
{
    public required long TimestampMono { get; init; }

    public required DateTimeOffset TimestampUtc { get; init; }

    public required CaptureEventKind Kind { get; init; }

    /// <summary>用户填写的标记文字（如「按下设备按键」）。仅 UserMark 有值。</summary>
    public string? Note { get; init; }

    /// <summary>引导步骤的稳定 ID（device-action / plug-charger…）。手写标记为 null。</summary>
    public string? StepId { get; init; }

    /// <summary>用户输入的电量（0–100）。仅 BatteryReference 有值，是外部参照，不是报文内容。</summary>
    public int? Percent { get; init; }

    public string? DeviceId { get; init; }
}
