namespace VigilWatt.Core.Capture;
/// <summary>
/// 功 能： 采集参数。Feature 读取**默认关**，只有用户在会话里显式勾选 report ID 才开启。
/// 2026/10/24 12:40:43 20211
/// </summary>
public sealed class CaptureOptions
{
    /// <summary>用户选的采集任务。只影响列哪些设备与提示文案，不影响采集方式。</summary>
    public CaptureTaskKind Task { get; init; } = CaptureTaskKind.UnknownDeviceInvestigation;
    /// <summary>要轮询的 Feature report ID，只能来自 caps 里真实存在的 ID；空 = 不读 Feature。</summary>
    public IReadOnlyList<byte> FeatureReportIds { get; init; } = [];

    /// <summary>本会话是否允许向设备发帧（用户在开始前显式勾选）。false = 句柄只申请读权限。</summary>
    public bool AllowSend { get; init; }

    public string AppVersion { get; init; } = "";

    /// <summary>环境信息由调用方（App）填，会话本身不碰 Windows API，才能进单测（R8）。</summary>
    public string OsVersion { get; init; } = "";

    public string Backend { get; init; } = "";

    /// <summary>单次输入报告读取的等待时间。太短会让 CPU 空转，太长会让时间戳变粗。</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Feature 轮询间隔。只读 GET_REPORT，但也不该高频打设备。</summary>
    public TimeSpan FeatureInterval { get; init; } = TimeSpan.FromSeconds(1);
}
