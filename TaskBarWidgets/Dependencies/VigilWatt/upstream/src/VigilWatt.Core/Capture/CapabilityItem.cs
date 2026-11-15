namespace VigilWatt.Core.Capture;
/// <summary>
/// 功 能： 预检里的一项能力。非「可用」时必须带原因、影响与后续处理，否则界面与报告都不完整。
/// 2026/10/24 12:40:43 20211
/// </summary>
public sealed class CapabilityItem
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required CapabilityStatus Status { get; init; }

    /// <summary>判定依据（实测到的具体事实，如 "CreateFile 返回 5"）。</summary>
    public string? Evidence { get; init; }

    /// <summary>为什么是这个状态。</summary>
    public string? Reason { get; init; }

    /// <summary>对本次采集的影响。</summary>
    public string? Impact { get; init; }

    /// <summary>用户可执行的后续处理（装工具、换权限、换设备）。</summary>
    public string? NextStep { get; init; }

    /// <summary>非「可用」状态是否写全了原因/影响/后续。用于测试与报告自检。</summary>
    public bool IsExplained =>
        Status == CapabilityStatus.Available ||
        (!string.IsNullOrWhiteSpace(Reason) && !string.IsNullOrWhiteSpace(Impact) && !string.IsNullOrWhiteSpace(NextStep));
}
