namespace VigilWatt.Core.Capture;
/// <summary>
/// 功 能： 会话元数据（session.json）：模式、环境、能力预检、计数与状态。
/// 2026/10/24 12:40:43 20211
/// </summary>
public sealed class CaptureSessionInfo
{
    public required string Id { get; init; }

    public CaptureTaskKind Task { get; set; } = CaptureTaskKind.UnknownDeviceInvestigation;

    public CaptureMethod Method { get; init; } = CaptureMethod.UsbHid;

    public CaptureSessionState State { get; set; } = CaptureSessionState.Created;

    public DateTimeOffset StartedUtc { get; init; }

    public DateTimeOffset? EndedUtc { get; set; }

    /// <summary>采集耗时（毫秒），停止时按单调时钟算，避免系统时间被调整时出现负数。</summary>
    public long? DurationMs { get; set; }

    public string OsVersion { get; init; } = "";

    public string AppVersion { get; init; } = "";

    /// <summary>采集后端标识，如 "Win32 hid.dll + CfgMgr32"。</summary>
    public string Backend { get; init; } = "";

    public string? DeviceId { get; init; }

    /// <summary>预检结果。报告与界面都读它，不再各算一遍。</summary>
    public IReadOnlyList<CapabilityItem> Capabilities { get; set; } = [];

    public int RecordCount { get; set; }

    public int EventCount { get; set; }

    public int ErrorCount { get; set; }

    /// <summary>触到记录上限后停止采集，报告里必须写"已截断"。</summary>
    public bool RecordsTruncated { get; set; }

    /// <summary>采集期间用户勾选过 Feature report 轮询（只读）。</summary>
    public bool FeaturePollingEnabled { get; init; }

    /// <summary>轮询的 feature report ID（只从 caps 里存在的选）。</summary>
    public IReadOnlyList<byte> FeaturePollReportIds { get; init; } = [];

    public string? Note { get; set; }
}
