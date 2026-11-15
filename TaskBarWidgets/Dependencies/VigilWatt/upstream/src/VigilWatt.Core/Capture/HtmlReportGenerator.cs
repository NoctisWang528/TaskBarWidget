using System.Net;
using System.Text;

namespace VigilWatt.Core.Capture;
/// <summary>
/// 功 能： 生成可离线打开的 report.html：内联样式、无脚本、无外链，事实与未知分开写。
/// 2026/10/24 12:40:43 20211
/// 设备名等字符串一律转义（设备名里出现 &lt; 会把整页结构冲掉）。
/// </summary>
public static class HtmlReportGenerator
{
    /// <summary>报告里最多列出的原始记录条数。全量在 records.jsonl 里，不在这里堆。</summary>
    public const int RecordPreviewLimit = 200;

    /// <summary>报告里最多列出的 caps 字段条数。完整表在 device.json。</summary>
    public const int FieldPreviewLimit = 40;

    public static string Generate(CaptureBundle bundle, string? packagePath = null)
    {
        var session = bundle.Session;
        var analysis = bundle.Analysis;
        var sb = new StringBuilder(16 * 1024);

        sb.Append("<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>VigilWatt 采集报告 ").Append(E(session.Id)).Append("</title>");
        AppendStyle(sb);
        sb.Append("</head><body><main>");

        sb.Append("<h1>VigilWatt 采集报告</h1>");
        sb.Append("<p class=\"sub\">会话 ").Append(E(session.Id)).Append(" · 状态 ")
          .Append(E(session.State.ToWire())).Append("</p>");

        AppendOverview(sb, bundle);
        AppendDevice(sb, bundle);
        AppendMethod(sb, bundle);
        AppendCapabilities(sb, bundle);
        AppendScope(sb, bundle);
        AppendTimeline(sb, bundle);
        AppendGroups(sb, analysis);
        AppendRecords(sb, bundle);
        AppendChanges(sb, analysis);
        AppendErrors(sb, bundle);
        AppendIntegrity(sb, bundle);
        AppendFacts(sb, bundle);
        AppendSummary(sb, bundle, packagePath);

        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static void AppendOverview(StringBuilder sb, CaptureBundle bundle)
    {
        var session = bundle.Session;
        sb.Append("<section><h2>概览</h2><table class=\"kv\">");
        Row(sb, "会话 ID", session.Id);
        Row(sb, "任务", session.Task.Label());
        Row(sb, "开始（本地时间）", session.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"));
        Row(sb, "结束（本地时间）", session.EndedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "未结束");
        Row(sb, "采集耗时", session.DurationMs is { } ms ? $"{ms / 1000.0:0.0} 秒" : "未知");
        Row(sb, "操作系统", session.OsVersion);
        Row(sb, "应用版本", session.AppVersion);
        Row(sb, "采集后端", session.Backend);
        sb.Append("</table></section>");
    }

    private static void AppendDevice(StringBuilder sb, CaptureBundle bundle)
    {
        sb.Append("<section><h2>目标设备</h2>");
        var device = bundle.Device;
        if (device == null)
        {
            sb.Append("<p class=\"muted\">本次没有选定设备（只采集了会话级信息）。</p></section>");
            return;
        }
        sb.Append("<table class=\"kv\">");
        Row(sb, "产品名", OrUnknown(device.Product));
        Row(sb, "厂商", OrUnknown(device.Manufacturer));
        Row(sb, "VID/PID", device.VidPid);
        Row(sb, "版本号", $"0x{device.VersionNumber:X4}");
        Row(sb, "序列号", string.IsNullOrEmpty(device.SerialNumber) ? "设备未提供" : device.SerialNumber);
        Row(sb, "接口段", device.InterfaceKey);
        Row(sb, "设备路径", device.InterfacePath);
        Row(sb, "usage", $"0x{device.UsagePage:X4} / 0x{device.Usage:X4}");
        Row(sb, "报文长度（in/out/feature）", $"{device.InputReportLength} / {device.OutputReportLength} / {device.FeatureReportLength}");
        Row(sb, "caps 里的 report ID（in/out/feature）",
            $"{Ids(device.InputReportIds)} / {Ids(device.OutputReportIds)} / {Ids(device.FeatureReportIds)}");
        Row(sb, "打开状态", device.AccessDenied
            ? $"被拒绝（Win32 错误 {device.OpenError?.ToString() ?? "未知"}，系统保护 collection）"
            : "可打开");
        sb.Append("</table>");
        sb.Append("<p class=\"note\">信息来源：Windows 设备枚举（CfgMgr32）与 hid.dll 的 caps 查询，"
                  + "均为本机实时读取，未做推断。序列号缺失表示设备没有提供该字符串，不是读取失败。</p>");
        if (device.Fields.Count > 0)
        {
            sb.Append("<h3>字段结构（caps 明细）</h3>");
            sb.Append("<p class=\"note\">caps 派生出的字段表，<b>不是</b>原始报告描述符；字段顺序与描述符一致，"
                      + "可用来判断「第 N 个字节是几位的什么量」。</p>");
            sb.Append("<table><thead><tr><th>报文</th><th>report</th><th>usage</th>"
                      + "<th>位宽 × 数量</th><th>逻辑范围</th><th>类型</th></tr></thead><tbody>");
            foreach (var field in device.Fields.Take(FieldPreviewLimit))
            {
                sb.Append("<tr><td>").Append(E(field.ReportType)).Append("</td><td>")
                  .Append(field.ReportId is { } rid ? $"0x{rid:X2}" : "—").Append("</td><td>")
                  .Append(field.IsRange
                      ? $"0x{field.UsagePage:X4}/0x{field.UsageMin:X2}–0x{field.UsageMax:X2}"
                      : $"0x{field.UsagePage:X4}/0x{field.UsageMin:X4}").Append("</td><td>")
                  .Append(field.IsButton ? $"按键 {field.ReportCount} 个" : $"{field.BitSize} × {field.ReportCount}")
                  .Append("</td><td>")
                  .Append(field.IsButton ? "—" : $"{field.LogicalMin}..{field.LogicalMax}")
                  .Append("</td><td>")
                  .Append(field.IsButton ? "按键" : field.IsAbsolute ? "绝对" : "相对")
                  .Append("</td></tr>");
            }
            sb.Append("</tbody></table>");
            if (device.Fields.Count > FieldPreviewLimit)
            {
                sb.Append("<p class=\"note\">另有 ").Append(device.Fields.Count - FieldPreviewLimit)
                  .Append(" 个字段未在此页显示，完整表在 device.json 的 fields 里。</p>");
            }
        }
        if (!device.IsVendorDefined)
        {
            sb.Append("<p class=\"warn\">该接口是标准 HID 用途页（usage page 0x")
              .Append(device.UsagePage.ToString("X4"))
              .Append("），只能读到标准键鼠/消费类报文，不会出现厂商定义的电量协议。"
                      + "如果这台设备在系统里没有 0xFF00 段的接口，说明它不通过 HID 暴露状态数据。</p>");
        }
        sb.Append("</section>");
    }

    private static void AppendMethod(StringBuilder sb, CaptureBundle bundle)
    {
        var session = bundle.Session;
        sb.Append("<section><h2>采集方式</h2><table class=\"kv\">");
        Row(sb, "方式", "USB / HID（用户明确选择）");
        Row(sb, "是否切换过方式", "否");
        Row(sb, "Feature 轮询", session.FeaturePollingEnabled
            ? $"已开启（report ID {Ids(session.FeaturePollReportIds)}，只读 GET_REPORT）"
            : "未开启");
        sb.Append("</table>");
        var sent = bundle.Analysis.SentRecords;
        sb.Append(sent > 0
            ? $"<p class=\"note\">本会话由用户手动发出 {sent} 帧（记录里 layer=hid-api-output、status=sent）。"
              + "除此之外不写设备，也不枚举 report ID。"
            : "<p class=\"note\">本采集不向设备写入任何报文，也不枚举 report ID。"
                  + "采集期间不存在自动切换到蓝牙、厂商软件或其他接口的逻辑。</p></section>");
    }

    private static void AppendCapabilities(StringBuilder sb, CaptureBundle bundle)
    {
        sb.Append("<section><h2>能力预检</h2>");
        if (bundle.Session.Capabilities.Count == 0)
        {
            sb.Append("<p class=\"muted\">本会话没有预检记录。</p></section>");
            return;
        }
        sb.Append("<table><thead><tr><th>能力</th><th>状态</th><th>依据</th><th>原因 / 影响 / 后续</th></tr></thead><tbody>");
        foreach (var cap in bundle.Session.Capabilities)
        {
            sb.Append("<tr><td>").Append(E(cap.Title)).Append("</td><td class=\"").Append(StatusClass(cap.Status)).Append("\">")
              .Append(E(cap.Status.Label())).Append("</td><td>").Append(E(cap.Evidence ?? "—")).Append("</td><td>");
            if (cap.Status == CapabilityStatus.Available)
            {
                sb.Append("—");
            }
            else
            {
                sb.Append("<b>原因：</b>").Append(E(cap.Reason ?? "—")).Append("<br>");
                sb.Append("<b>影响：</b>").Append(E(cap.Impact ?? "—")).Append("<br>");
                sb.Append("<b>后续：</b>").Append(E(cap.NextStep ?? "—"));
            }
            sb.Append("</td></tr>");
        }
        sb.Append("</tbody></table></section>");
    }

    private static void AppendScope(StringBuilder sb, CaptureBundle bundle)
    {
        sb.Append("<section><h2>采集范围与已知限制</h2><ul>");
        sb.Append("<li>已采集：设备枚举信息、HID 接口 caps、经 HID API 实际读到的输入报告");
        sb.Append(bundle.Session.FeaturePollingEnabled ? "、经 GET_REPORT 读到的 Feature 报告。" : "（未开启 Feature 读取）。").Append("</li>");
        sb.Append("<li>未采集：原始 HID 报告描述符字节（用户态无此 API）、USB 总线级记录（需 USBPcap）、"
                  + "蓝牙与 2.4G 空中通信（接收器 HID 层面不可见）。</li>");
        sb.Append("<li>HID 输入报告 ≠ USB 全部控制传输；接收器暴露的 HID 数据 ≠ 设备与接收器之间的无线链路。</li>");
        if (bundle.Session.RecordsTruncated)
        {
            sb.Append("<li class=\"warn\">已达到记录上限，采集被截断：本报告只覆盖上限之前的部分。</li>");
        }
        sb.Append("</ul></section>");
    }

    private static void AppendTimeline(StringBuilder sb, CaptureBundle bundle)
    {
        sb.Append("<section><h2>事件时间线</h2>");
        if (bundle.Events.Count == 0)
        {
            sb.Append("<p class=\"muted\">无事件。</p></section>");
            return;
        }
        sb.Append("<table><thead><tr><th>时间（本地）</th><th>相对</th><th>事件</th><th>说明</th></tr></thead><tbody>");
        foreach (var item in bundle.Events)
        {
            sb.Append("<tr><td>").Append(E(item.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff"))).Append("</td><td>+")
              .Append(item.TimestampMono).Append(" ms</td><td>").Append(E(item.Kind.ToWire())).Append("</td><td>")
              .Append(E(item.Note ?? "")).Append("</td></tr>");
        }
        sb.Append("</tbody></table>");
        sb.Append("<p class=\"note\">用户操作标记是独立事件，不混入原始报文。</p></section>");
    }

    private static void AppendGroups(StringBuilder sb, CaptureAnalysis analysis)
    {
        sb.Append("<section><h2>原始数据概览</h2>");
        if (analysis.Groups.Count == 0)
        {
            sb.Append("<p class=\"warn\">本次没有记录到任何报文。</p>");
            sb.Append("<p class=\"note\">零报文不等于设备没有相关协议：可能是设备不自发推送（需要先开会话）、"
                      + "选错了接口，或该接口的通道类型不是输入报告。</p></section>");
            return;
        }
        sb.Append("<table><thead><tr><th>分组</th><th>条数</th><th>长度</th><th>不同内容</th><th>重复率</th>"
                  + "<th>首帧前缀</th><th>首帧时间</th></tr></thead><tbody>");
        foreach (var group in analysis.Groups)
        {
            sb.Append("<tr><td>").Append(E(group.DisplayName)).Append("</td><td>").Append(group.Count)
              .Append("</td><td>").Append(group.Length).Append("</td><td>").Append(group.DistinctPayloads)
              .Append("</td><td>").Append(group.DuplicateRatio.ToString("P0")).Append("</td><td class=\"mono\">")
              .Append(E(group.FirstHexPrefix)).Append("</td><td>")
              .Append(E(group.FirstSeenUtc.ToLocalTime().ToString("HH:mm:ss.fff"))).Append("</td></tr>");
        }
        sb.Append("</tbody></table>");
        AppendStaticGroupWarning(sb, analysis);
        sb.Append("</section>");
    }

    /// <summary>整场没变的分组要直接给结论：光看「不同内容 1 / 重复率 98%」推不出「这条通道是死的」。</summary>
    private static void AppendStaticGroupWarning(StringBuilder sb, CaptureAnalysis analysis)
    {
        var groups = analysis.StaticGroups;
        if (groups.Count == 0) return;

        sb.Append("<p class=\"warn\">");
        for (var i = 0; i < groups.Count; i++)
        {
            if (i > 0) sb.Append("；");
            sb.Append("<b>").Append(E(groups[i].DisplayName)).Append("</b> 的 ")
              .Append(groups[i].Count).Append(" 帧在整场采集里完全一致");
        }
        sb.Append("——没有任何变化。</p>");
        sb.Append("<p class=\"note\">整场不变通常说明这条通道是配置/握手通道，不携带会变化的状态量。"
                  + "要电量、充电这类数据，请换到有输入报告的接口（设备列表里标「有输入通道」的那些）重新采。</p>");
    }

    private static void AppendRecords(StringBuilder sb, CaptureBundle bundle)
    {
        var records = bundle.Records;
        sb.Append("<section><h2>原始记录</h2>");
        if (records.Count == 0)
        {
            sb.Append("<p class=\"muted\">无记录。</p></section>");
            return;
        }
        sb.Append("<p class=\"note\">最多显示前 ").Append(RecordPreviewLimit).Append(" 条，完整数据见 records.jsonl。</p>");
        sb.Append("<table><thead><tr><th>相对</th><th>时间（本地）</th><th>层</th><th>类型</th><th>report</th>"
                  + "<th>API</th><th>长度</th><th>状态</th><th>数据（hex）</th></tr></thead><tbody>");
        foreach (var record in records.Take(RecordPreviewLimit))
        {
            sb.Append("<tr><td>+").Append(record.TimestampMono).Append(" ms</td><td>")
              .Append(E(record.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff"))).Append("</td><td>")
              .Append(E(record.Layer.ToWire())).Append("</td><td>").Append(E(record.RecordType)).Append("</td><td>")
              .Append(record.ReportId is { } rid ? $"0x{rid:X2}" : "—").Append("</td><td>")
              .Append(E(record.Api ?? "—")).Append("</td><td>").Append(record.Length).Append("</td><td>")
              .Append(E(record.Status.ToWire())).Append("</td><td class=\"mono\">").Append(E(record.DataHex)).Append("</td></tr>");
        }
        sb.Append("</tbody></table>");
        if (records.Count > RecordPreviewLimit)
        {
            sb.Append("<p class=\"note\">另有 ").Append(records.Count - RecordPreviewLimit).Append(" 条未在此页显示。</p>");
        }
        sb.Append("</section>");
    }

    private static void AppendChanges(StringBuilder sb, CaptureAnalysis analysis)
    {
        sb.Append("<section><h2>首帧 / 末帧差异</h2>");
        if (analysis.Changes.Count == 0)
        {
            sb.Append("<p class=\"muted\">没有可比较的差异（记录不足两条，或内容完全一致）。</p></section>");
            return;
        }
        sb.Append("<table><thead><tr><th>分组</th><th>偏移</th><th>首帧</th><th>末帧</th><th>末值出现次数</th></tr></thead><tbody>");
        foreach (var change in analysis.Changes)
        {
            sb.Append("<tr><td>").Append(E(change.GroupKey)).Append("</td><td>")
              .Append(change.LengthChanged ? "长度" : change.Offset.ToString()).Append("</td><td>")
              .Append(change.LengthChanged ? change.FromLength.ToString() : change.From is { } f ? $"0x{f:X2}" : "—")
              .Append("</td><td>")
              .Append(change.LengthChanged ? change.ToLength.ToString() : change.To is { } t ? $"0x{t:X2}" : "—")
              .Append("</td><td>").Append(change.Occurrences).Append("</td></tr>");
        }
        sb.Append("</tbody></table>");
        if (analysis.Marks.Count > 0)
        {
            sb.Append("<h3>按步骤对照</h3><table><thead><tr><th>步骤 / 标记</th><th>相对</th><th>之前累计</th>"
                      + "<th>本步记录</th><th>本步变化的字节</th><th>本步新出现的分组</th></tr></thead><tbody>");
            foreach (var mark in analysis.Marks)
            {
                sb.Append("<tr><td>").Append(E(mark.Note)).Append("</td><td>+").Append(mark.TimestampMono)
                  .Append(" ms</td><td>").Append(mark.RecordsBefore).Append("</td><td>").Append(mark.RecordsInStep)
                  .Append("</td><td class=\"mono\">").Append(E(DescribeStepChanges(mark.Changes)))
                  .Append("</td><td>").Append(mark.NewGroupsInStep.Count == 0 ? "无" : E(string.Join("、", mark.NewGroupsInStep)))
                  .Append("</td></tr>");
            }
            sb.Append("</tbody></table>");
        }
        if (analysis.BatteryReferences.Count > 0)
        {
            sb.Append("<h3>电量核对（用户输入）</h3>");
            sb.Append("<table><thead><tr><th>时间</th><th>用户输入</th><th>其后第一帧</th>"
                      + "<th>值等于该电量的偏移</th></tr></thead><tbody>");
            foreach (var reference in analysis.BatteryReferences)
            {
                sb.Append("<tr><td>+").Append(reference.TimestampMono).Append(" ms</td><td>")
                  .Append(reference.Percent).Append("%</td><td class=\"mono\">")
                  .Append(E(reference.NextRecordHex ?? "之后没有报文")).Append("</td><td>")
                  .Append(reference.MatchingOffsets.Count == 0
                      ? "无"
                      : string.Join(", ", reference.MatchingOffsets.Select(o => $"偏移 {o}")))
                  .Append("</td></tr>");
            }
            sb.Append("</tbody></table>");
            sb.Append("<p class=\"note\">偏移只是<b>候选</b>：某个字节恰好等于该数字很常见。"
                      + "记录几个不同电量（如放电中、充电中）再比对，才能收敛到真正的电量字节。</p>");
        }
        sb.Append("</section>");
    }

    private static void AppendErrors(StringBuilder sb, CaptureBundle bundle)
    {
        sb.Append("<section><h2>错误与告警</h2>");
        if (bundle.Errors.Count == 0)
        {
            sb.Append("<p class=\"muted\">本次没有错误或告警。</p></section>");
            return;
        }
        sb.Append("<table><thead><tr><th>时间（本地）</th><th>类型</th><th>说明</th><th>HRESULT</th><th>接口</th></tr></thead><tbody>");
        foreach (var error in bundle.Errors)
        {
            sb.Append("<tr><td>").Append(E(error.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff"))).Append("</td><td>")
              .Append(E(error.Kind.ToWire())).Append("</td><td>").Append(E(error.Message)).Append("</td><td>")
              .Append(error.HResult is { } hr ? $"0x{hr:X8}" : "—").Append("</td><td>")
              .Append(E(error.InterfaceId ?? "—")).Append("</td></tr>");
        }
        sb.Append("</tbody></table></section>");
    }

    private static void AppendIntegrity(StringBuilder sb, CaptureBundle bundle)
    {
        var analysis = bundle.Analysis;
        sb.Append("<section><h2>数据完整性</h2><ul>");
        sb.Append("<li>原始字节以 base64 存在 records.jsonl，hex 由字节生成；本页展示的是派生结果。</li>");
        sb.Append("<li>时间同时记录单调时钟（毫秒，相对会话开始）与墙上时钟（UTC），排序用前者。</li>");
        sb.Append("<li>成功记录 ").Append(analysis.ReceivedRecords).Append(" 条，失败 ").Append(analysis.FailedRecords)
          .Append(" 条；失败只记在 errors.jsonl，不会生成空报文。</li>");
        if (bundle.Session.RecordsTruncated)
        {
            sb.Append("<li class=\"warn\">记录数达到上限，采集已截断，后续报文未记录。</li>");
        }
        if (bundle.Errors.Any(e => e.Kind == CaptureErrorKind.DeviceRemoved))
        {
            sb.Append("<li class=\"warn\">采集中设备曾断开，断开后的数据缺失。</li>");
        }
        sb.Append("<li>本报告不含任何协议含义结论；字节代表什么需要额外证据。</li>");
        sb.Append("</ul></section>");
    }

    private static void AppendFacts(StringBuilder sb, CaptureBundle bundle)
    {
        var analysis = bundle.Analysis;
        sb.Append("<section><h2>事实 / 推断 / 未知</h2>");
        sb.Append("<h3>事实（可直接从数据读出）</h3><ul>");
        sb.Append("<li>共 ").Append(analysis.TotalRecords).Append(" 条记录，").Append(analysis.TotalBytes).Append(" 字节。</li>");
        foreach (var group in analysis.Groups.Take(4))
        {
            sb.Append("<li>").Append(E(group.RecordType)).Append(group.ReportId is { } rid ? $" report 0x{rid:X2}" : "")
              .Append(" 共 ").Append(group.Count).Append(" 条，长度 ").Append(group.Length).Append("。</li>");
        }
        sb.Append("</ul>");

        sb.Append("<h3>推断（未证实，仅作方向）</h3><ul>");
        if (analysis.Changes.Count == 0)
        {
            sb.Append("<li>无可推断项。</li>");
        }
        else
        {
            foreach (var change in analysis.Changes.Take(4))
            {
                sb.Append("<li>").Append(E(change.GroupKey)).Append(" 的偏移 ").Append(change.Offset)
                  .Append(" 在采集期间发生过变化，是否对应某个物理状态**尚未证实**。</li>");
            }
        }
        sb.Append("</ul>");

        sb.Append("<h3>未知</h3><ul>");
        sb.Append("<li>原始 HID 报告描述符字节：用户态不可读，本报告没有。</li>");
        sb.Append("<li>USB 总线级完整通信：本次未采集。</li>");
        sb.Append("<li>蓝牙 / 2.4G 空中链路：接收器 HID 层面不可见。</li>");
        sb.Append("<li>设备是否被其他进程占用：无法可靠判定。</li>");
        sb.Append("<li>各分组字节的协议含义：无证据。</li>");
        sb.Append("</ul></section>");
    }

    private static void AppendSummary(StringBuilder sb, CaptureBundle bundle, string? packagePath)
    {
        sb.Append("<section><h2>调查摘要（可复制）</h2><pre class=\"summary\">")
          .Append(E(IssueSummaryBuilder.Build(bundle, packagePath)))
          .Append("</pre></section>");
    }

    private static void AppendStyle(StringBuilder sb)
    {
        sb.Append("<style>")
          .Append(":root{color-scheme:light}")
          .Append("body{margin:0;background:#f6f6f6;color:#1b1b1b;font:14px/1.6 'Segoe UI','Microsoft YaHei',sans-serif}")
          .Append("main{max-width:1080px;margin:0 auto;padding:24px 20px 64px}")
          .Append("h1{font-size:22px;margin:0 0 4px}h2{font-size:16px;margin:0 0 10px}")
          .Append("h3{font-size:14px;margin:14px 0 6px}")
          .Append(".sub{color:#666;margin:0 0 20px}")
          .Append("section{background:#fff;border:1px solid #e3e3e3;border-radius:8px;padding:16px 18px;margin:0 0 14px}")
          .Append("table{border-collapse:collapse;width:100%;font-size:13px}")
          .Append("th,td{border-bottom:1px solid #ececec;padding:6px 8px;text-align:left;vertical-align:top}")
          .Append("th{background:#fafafa;font-weight:600;color:#444}")
          .Append("table.kv td:first-child{width:210px;color:#555}")
          .Append(".mono,.summary{font-family:Consolas,'Cascadia Mono',monospace;font-size:12px;word-break:break-all}")
          .Append(".summary{white-space:pre-wrap;background:#fafafa;border:1px solid #eee;border-radius:6px;padding:12px}")
          .Append(".muted{color:#888}.note{color:#666;font-size:12px;margin:8px 0 0}")
          .Append(".warn{color:#a33}.ok{color:#0f7b0f}.bad{color:#c42b1c}.pend{color:#9d5d00}")
          .Append("ul{margin:6px 0 0 18px;padding:0}li{margin:2px 0}")
          .Append("</style>");
    }

    /// <summary>一步内变化的字节。最多列 4 处，多了表格会被撑爆。</summary>
    private static string DescribeStepChanges(IReadOnlyList<CapturePayloadChange> changes)
    {
        if (changes.Count == 0) return "无变化";
        var parts = changes.Take(4).Select(c => c.LengthChanged
            ? $"长度 {c.FromLength}→{c.ToLength}"
            : $"偏移 {c.Offset}: 0x{c.From:X2}→0x{c.To:X2}");
        var text = string.Join("；", parts);
        return changes.Count > 4 ? $"{text}；…（共 {changes.Count} 处）" : text;
    }

    private static string StatusClass(CapabilityStatus status) => status switch
    {
        CapabilityStatus.Available => "ok",
        CapabilityStatus.Unavailable => "bad",
        _ => "pend",
    };

    private static void Row(StringBuilder sb, string key, string value)
        => sb.Append("<tr><td>").Append(E(key)).Append("</td><td class=\"mono\">").Append(E(value)).Append("</td></tr>");

    private static string Ids(IReadOnlyList<byte> ids)
        => ids.Count == 0 ? "无" : string.Join(", ", ids.Select(i => $"0x{i:X2}"));

    private static string OrUnknown(string value) => string.IsNullOrWhiteSpace(value) ? "（未提供）" : value;

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? "");
}
