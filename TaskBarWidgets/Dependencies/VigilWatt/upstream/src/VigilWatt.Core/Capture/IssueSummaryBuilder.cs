using System.Text;

namespace VigilWatt.Core.Capture;
/// <summary>
/// 功 能： 生成可离线打开的调查摘要（贴 Issue / 贴 AI 对话）。事实、未知分开写，不给未证实的结论。
/// 2026/10/24 12:40:43 20211
/// </summary>
public static class IssueSummaryBuilder
{
    public static string Build(CaptureBundle bundle, string? packagePath = null)
    {
        var session = bundle.Session;
        var device = bundle.Device;
        var analysis = bundle.Analysis;
        var sb = new StringBuilder();

        sb.AppendLine("### 设备");
        if (device == null)
        {
            sb.AppendLine("- 设备：未选择（仅会话级信息）");
        }
        else
        {
            sb.AppendLine($"- 名称：{OrUnknown(device.Product)}");
            sb.AppendLine($"- 厂商：{OrUnknown(device.Manufacturer)}");
            sb.AppendLine($"- VID/PID：{device.VidPid}（版本 0x{device.VersionNumber:X4}）");
            sb.AppendLine($"- 接口：{device.InterfaceKey}  usage 0x{device.UsagePage:X4}/0x{device.Usage:X4}");
            sb.AppendLine($"- 序列号：{(string.IsNullOrEmpty(device.SerialNumber) ? "设备未提供" : device.SerialNumber)}");
        }

        sb.AppendLine();
        sb.AppendLine("### 采集方式与环境");
        sb.AppendLine($"- 任务：{session.Task.Label()}");
        sb.AppendLine("- 方式：USB/HID（用户选择，采集期间未切换）");
        sb.AppendLine($"- 系统：{session.OsVersion}");
        sb.AppendLine($"- 应用：VigilWatt {session.AppVersion}，后端 {session.Backend}");
        sb.AppendLine($"- 时间：{session.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} 起，"
                      + $"耗时 {(session.DurationMs is { } ms ? $"{ms / 1000.0:0.0} 秒" : "未知")}");

        sb.AppendLine();
        sb.AppendLine("### 能力预检");
        foreach (var group in bundle.Session.Capabilities.GroupBy(c => c.Status))
        {
            var names = string.Join("、", group.Select(c => c.Title));
            sb.AppendLine($"- {group.Key.Label()}：{names}");
        }

        sb.AppendLine();
        sb.AppendLine("### 采集结果（事实）");
        sb.AppendLine($"- 原始记录 {analysis.TotalRecords} 条（成功 {analysis.ReceivedRecords}，失败 {analysis.FailedRecords}），"
                      + $"共 {analysis.TotalBytes} 字节");
        sb.AppendLine($"- 事件 {analysis.TotalEvents} 条，错误/告警 {analysis.TotalErrors} 条");
        if (analysis.Groups.Count == 0)
        {
            sb.AppendLine("- 未记录到任何报文。**这不等于设备没有相关协议**，见下方待验证项。");
        }
        else
        {
            foreach (var group in analysis.Groups.Take(6))
            {
                var id = group.ReportId is { } rid ? $"0x{rid:X2}" : "无";
                sb.AppendLine($"- 分组 {group.RecordType} report={id} 长度={group.Length}：{group.Count} 条，"
                              + $"不同内容 {group.DistinctPayloads} 种，重复率 {group.DuplicateRatio:P0}");
            }
        }

        if (analysis.StaticGroups.Count > 0)
        {
            sb.AppendLine($"- **整场没有变化**：{analysis.StaticGroupText}——内容完全一致，"
                          + "可能不是状态通道（配置/握手通道常见这种表现）。");
        }

        if (analysis.Changes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### 关键差异（首帧 vs 末帧）");
            foreach (var change in analysis.Changes.Take(8))
            {
                var text = change.LengthChanged
                    ? $"长度 {change.FromLength} → {change.ToLength}"
                    : $"偏移 {change.Offset}：{change.From:X2} → {change.To:X2}";
                sb.AppendLine($"- {change.GroupKey}：{text}");
            }
        }

        if (analysis.BatteryReferences.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### 电量核对（用户输入）");
            foreach (var reference in analysis.BatteryReferences.Take(8))
            {
                var offsets = reference.MatchingOffsets.Count == 0
                    ? "无匹配字节"
                    : "候选偏移 " + string.Join("/", reference.MatchingOffsets);
                sb.AppendLine($"- +{reference.TimestampMono}ms 用户读到 {reference.Percent}% → {offsets}");
            }
        }

        if (bundle.Errors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### 错误与告警");
            foreach (var error in bundle.Errors.Take(8))
            {
                var hr = error.HResult is { } code ? $"，HRESULT 0x{code:X8}" : "";
                sb.AppendLine($"- [{error.Kind}] {error.Message}{hr}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("### 需要进一步验证");
        if (analysis.ReceivedRecords == 0)
        {
            sb.AppendLine("- 本次没有任何报文：可能是设备不自发推送（需要先开会话）、采错了接口、"
                          + "或该接口的通道类型不是输入报告。**不能据此判定设备没有电量/状态协议。**");
        }
        else
        {
            sb.AppendLine("- 记录里各分组的字节含义尚未证实，需要对照设备物理状态（按键、切换模式、充电）复采比对。");
        }
        if (bundle.Session.Capabilities.Any(c => c.Status is CapabilityStatus.Unavailable or CapabilityStatus.NeedsExtraTool))
        {
            sb.AppendLine("- 预检里有不可用/需额外工具项，USB 总线级证据本次未采集。");
        }

        sb.AppendLine();
        sb.AppendLine("### 诊断包");
        sb.AppendLine(string.IsNullOrEmpty(packagePath)
            ? "- 本地路径：<请在此处填写你保存的 vigilwatt-capture.zip 路径>"
            : $"- 本地路径：{packagePath}");

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string OrUnknown(string value) => string.IsNullOrWhiteSpace(value) ? "（未提供）" : value;
}
