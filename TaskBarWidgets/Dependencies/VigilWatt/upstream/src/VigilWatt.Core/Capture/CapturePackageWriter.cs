using System.IO.Compression;
using System.Text;

namespace VigilWatt.Core.Capture;
/// <summary>
/// 功 能： 把会话目录打成 vigilwatt-capture.zip，并生成 README.txt（文件说明、格式、限制、隐私提示）。
/// 2026/10/24 12:40:43 20211
/// 只写白名单里的文件名，且逐个校验条目名，杜绝路径穿越与越界写入。
/// </summary>
public static class CapturePackageWriter
{
    public const string ZipFileName = "vigilwatt-capture.zip";

    /// <summary>诊断包白名单。顺序即压缩包内顺序，便于人看。</summary>
    public static readonly string[] EntryNames =
    [
        "README.txt",
        "report.html",
        "session.json",
        "device.json",
        "records.jsonl",
        "events.jsonl",
        "errors.jsonl",
        "analysis.json",
        "backend.log",
    ];

    public static string Write(string sessionDir, string zipPath, bool overwrite = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ValidateEntryName(Path.GetFileName(zipPath));

        var mode = overwrite ? FileMode.Create : FileMode.CreateNew;
        using var file = new FileStream(zipPath, mode, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var name in EntryNames)
        {
            ValidateEntryName(name);
            var source = Path.Combine(sessionDir, name);
            if (!File.Exists(source)) continue;
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            input.CopyTo(entryStream);
        }
        return zipPath;
    }

    /// <summary>拒绝空名、绝对路径、`..` 与目录分隔符——压缩包条目只能是纯文件名。</summary>
    public static void ValidateEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("压缩包条目名不能为空", nameof(name));
        if (name.Contains("..", StringComparison.Ordinal)) throw new ArgumentException($"条目名不能包含 ..: {name}", nameof(name));
        if (name.IndexOfAny(['/', '\\']) >= 0) throw new ArgumentException($"条目名不能包含路径分隔符: {name}", nameof(name));
        if (Path.IsPathRooted(name)) throw new ArgumentException($"条目名不能是绝对路径: {name}", nameof(name));
    }

    public static string BuildReadme(CaptureBundle bundle)
    {
        var session = bundle.Session;
        var sb = new StringBuilder();
        sb.AppendLine("VigilWatt 采集诊断包");
        sb.AppendLine("====================");
        sb.AppendLine();
        sb.AppendLine($"会话 ID：{session.Id}");
        sb.AppendLine($"采集方式：USB/HID（用户选择，未切换）");
        sb.AppendLine($"开始时间（UTC）：{session.StartedUtc:yyyy-MM-ddTHH:mm:ss.fffZ}");
        sb.AppendLine($"操作系统：{session.OsVersion}");
        sb.AppendLine($"应用版本：VigilWatt {session.AppVersion}");
        sb.AppendLine($"采集后端：{session.Backend}");
        sb.AppendLine();
        sb.AppendLine("文件说明");
        sb.AppendLine("--------");
        sb.AppendLine("report.html     可离线打开的完整报告（不依赖网络、无脚本）。");
        sb.AppendLine("session.json    会话元数据、采集模式、能力预检结果、环境信息。");
        sb.AppendLine("device.json     目标设备与 HID 接口元信息（VID/PID、序列号、caps、report ID）。");
        sb.AppendLine("records.jsonl   原始采集记录，每行一个 JSON 对象。");
        sb.AppendLine("events.jsonl    会话事件：用户操作标记、暂停/继续、设备插拔。");
        sb.AppendLine("errors.jsonl    错误与告警：打开失败、读取失败、设备断开、记录上限。");
        sb.AppendLine("analysis.json   基础统计、报文分组、首末帧差异、用户标记前后对照。");
        sb.AppendLine("backend.log     采集后端自身的运行日志（本会话相关）。");
        sb.AppendLine();
        sb.AppendLine("records.jsonl 字段");
        sb.AppendLine("------------------");
        sb.AppendLine("tsMono        相对会话开始的毫秒数（单调时钟，排序用它）");
        sb.AppendLine("tsUtc         墙上时钟 UTC");
        sb.AppendLine("layer         通信层级：device-enum / hid-caps / hid-api-input / hid-api-feature");
        sb.AppendLine("recordType    记录类型：hid_input_report / hid_feature_report 等");
        sb.AppendLine("reportId      HID report ID（不适用时省略）");
        sb.AppendLine("api           实际调用的 API：ReadFile / HidD_GetFeature");
        sb.AppendLine("dataBase64    原始字节（规范来源）");
        sb.AppendLine("dataHex       由原始字节生成的十六进制展示");
        sb.AppendLine("status        received / failed");
        sb.AppendLine();
        sb.AppendLine("数据层级与限制");
        sb.AppendLine("--------------");
        sb.AppendLine("· 本包含有 HID 接口枚举信息、caps 摘要、经 HID API 实际读到的报文。");
        sb.AppendLine("· 不含原始 HID 报告描述符字节：Windows 用户态没有该 API。");
        sb.AppendLine("· 不含 USB 总线级通信：需要 USBPcap 等外部工具，本应用不采集。");
        sb.AppendLine("· 不含蓝牙 / 2.4G 空中链路：接收器暴露的 HID 数据不等于无线链路数据。");
        sb.AppendLine("· 没有报文不代表设备没有相关协议，可能只是设备不自发推送。");
        sb.AppendLine("· 各字节的协议含义未经证实，本包不做结论。");
        sb.AppendLine();
        sb.AppendLine("隐私提示");
        sb.AppendLine("--------");
        sb.AppendLine("本包含有设备序列号、产品名、厂商名与设备路径等可识别信息，未做脱敏。");
        sb.AppendLine("发送给他人或公开到 Issue 前请自行判断是否需要删除这些字段。");
        sb.AppendLine("VigilWatt 不会自动上传本包，也不会在未告知的情况下把内容发送到任何服务器。");
        return sb.ToString();
    }
}
