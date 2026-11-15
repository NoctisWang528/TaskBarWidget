namespace VigilWatt.Core.Capture;
/// <summary>
/// 功 能： 一个 HID 字段的结构（来自 caps 明细）。**不是原始报告描述符**，是它派生出来的字段表。
/// 2026/10/24 15:30:00 20211
/// 拿不到描述符字节时，bit size / logical range / report count 就是判断"第 N 字节是什么"的唯一依据。
/// </summary>
public sealed class HidFieldInfo
{
    /// <summary>input / output / feature。</summary>
    public required string ReportType { get; init; }

    /// <summary>所属 report ID。null = 该接口不使用 report ID（caps 里为 0）；读不到 caps 时整个 fields 为空。</summary>
    public int? ReportId { get; init; }

    public ushort UsagePage { get; init; }

    /// <summary>单个 usage，或范围的下界。</summary>
    public ushort UsageMin { get; init; }

    public ushort UsageMax { get; init; }

    /// <summary>每个字段的位数。按钮恒为 1。</summary>
    public int BitSize { get; init; }

    public int ReportCount { get; init; }

    public int LogicalMin { get; init; }

    public int LogicalMax { get; init; }

    public bool IsAbsolute { get; init; }

    /// <summary>true = 来自 button caps（按键位），false = value caps（数值字段）。</summary>
    public bool IsButton { get; init; }

    public bool IsRange => UsageMax > UsageMin;

    /// <summary>一行展示：`input 0x07 · 0xFF00/0x0002 · 8 bit ×1 · 0..100`。</summary>
    public string Describe()
    {
        var id = ReportId is { } rid ? $"0x{rid:X2}" : "—";
        var usage = IsRange
            ? $"0x{UsagePage:X4}/0x{UsageMin:X4}–0x{UsageMax:X4}"
            : $"0x{UsagePage:X4}/0x{UsageMin:X4}";
        var size = IsButton
            ? $"按键 {ReportCount} 个"
            : $"{BitSize} bit × {ReportCount}";
        var range = IsButton ? "" : $" · {LogicalMin}..{LogicalMax}";
        var kind = IsButton ? "按键" : IsAbsolute ? "绝对" : "相对";
        return $"{ReportType} {id} · {usage} · {size}{range} · {kind}";
    }
}
