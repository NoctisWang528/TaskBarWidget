namespace VigilWatt.Core.Capture;
/// <summary>
/// 功 能： 一次采集的全部材料（会话 + 设备 + 记录 + 事件 + 错误 + 分析），报告与摘要的唯一输入。
/// 2026/10/24 12:40:43 20211
/// </summary>
public sealed class CaptureBundle
{
    public required CaptureSessionInfo Session { get; init; }

    public CaptureDeviceInfo? Device { get; init; }

    public IReadOnlyList<CaptureRecord> Records { get; init; } = [];

    public IReadOnlyList<CaptureEvent> Events { get; init; } = [];

    public IReadOnlyList<CaptureError> Errors { get; init; } = [];

    public required CaptureAnalysis Analysis { get; init; }
}
