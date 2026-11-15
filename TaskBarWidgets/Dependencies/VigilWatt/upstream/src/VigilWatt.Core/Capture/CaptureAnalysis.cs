namespace VigilWatt.Core.Capture;
/// <summary>
/// 功 能： 基础分析结果（analysis.json）：分组统计、重复率、字节差异、用户标记前后对照。
/// 2026/10/24 12:40:43 20211
/// 只读结论，不含任何协议语义推断——解释数据是适配器的事。
/// </summary>
public sealed class CaptureAnalysis
{
    public int TotalRecords { get; init; }

    public int ReceivedRecords { get; init; }

    public int FailedRecords { get; init; }

    /// <summary>主机→设备方向的记录数（用户在采集页手动发出的帧）。</summary>
    public int SentRecords { get; init; }

    public int TotalEvents { get; init; }

    public int TotalErrors { get; init; }

    public long TotalBytes { get; init; }

    /// <summary>按 (记录类型, report ID, 长度) 分组。</summary>
    public IReadOnlyList<CaptureRecordGroup> Groups { get; init; } = [];

    /// <summary>同一分组内首帧与末帧的逐字节差异。</summary>
    public IReadOnlyList<CapturePayloadChange> Changes { get; init; } = [];

    /// <summary>每个用户标记前后的记录数与新增分组。</summary>
    public IReadOnlyList<CaptureMarkComparison> Marks { get; init; } = [];

    /// <summary>用户输入的电量参照，以及其后第一帧里「值等于该电量」的偏移（未证实，只是线索）。</summary>
    public IReadOnlyList<CaptureBatteryReference> BatteryReferences { get; init; } = [];

    /// <summary>判「整场没变」所需的最少条数。2~3 帧不足以说明问题，别据此下结论。</summary>
    public const int StaticGroupMinRecords = 5;

    /// <summary>整场内容始终没变的分组：通常是配置/握手通道，不携带状态数据（见 capture.md §8）。</summary>
    public IReadOnlyList<CaptureRecordGroup> StaticGroups
        => Groups.Where(g => g.Count >= StaticGroupMinRecords && g.DistinctPayloads == 1).ToList();

    /// <summary>「哪些分组整场没变」的一句话描述：报告、摘要、界面共用同一份措辞。</summary>
    public string StaticGroupText => string.Join("、", StaticGroups.Select(g => $"{g.DisplayName}（{g.Count} 帧）"));
}

/// <summary>一条用户输入的电量参照，用来核对报文里哪个字节是电量。</summary>
public sealed class CaptureBatteryReference
{
    public int Percent { get; init; }

    public long TimestampMono { get; init; }

    public DateTimeOffset TimestampUtc { get; init; }

    /// <summary>该时刻之后的第一帧。找不到时为 null（用户记录后设备一直没出报文）。</summary>
    public string? NextRecordHex { get; init; }

    public long? NextRecordMono { get; init; }

    /// <summary>这一帧里值恰好等于 Percent 的字节偏移。**只是候选**，多字节巧合很常见。</summary>
    public IReadOnlyList<int> MatchingOffsets { get; init; } = [];
}

/// <summary>一个报文分组的统计。</summary>
public sealed class CaptureRecordGroup
{
    public required string Key { get; init; }

    public required string RecordType { get; init; }

    public int? ReportId { get; init; }

    /// <summary>报告与摘要里给分组起的名字：`hid_input_report report 0x07`。别在两处各拼一遍。</summary>
    public string DisplayName => ReportId is { } id ? $"{RecordType} report 0x{id:X2}" : RecordType;

    public int Length { get; init; }

    /// <summary>该分组出现过的所有长度（升序）。多于一个说明报文长度本身在变。</summary>
    public IReadOnlyList<int> Lengths { get; init; } = [];

    public int Count { get; init; }

    /// <summary>不同 payload 的个数。等于 1 说明该分组内容完全固定。</summary>
    public int DistinctPayloads { get; init; }

    /// <summary>重复率 = 1 - 不同 payload / 总数。</summary>
    public double DuplicateRatio { get; init; }

    public DateTimeOffset FirstSeenUtc { get; init; }

    public DateTimeOffset LastSeenUtc { get; init; }

    /// <summary>首帧前 16 字节的十六进制，供报告里快速识别。</summary>
    public string FirstHexPrefix { get; init; } = "";

    public string FirstHex { get; init; } = "";

    public string LastHex { get; init; } = "";
}

/// <summary>首末帧在某个偏移上的字节变化。</summary>
public sealed class CapturePayloadChange
{
    public required string GroupKey { get; init; }

    public int Offset { get; init; }

    public byte? From { get; init; }

    public byte? To { get; init; }

    /// <summary>该分组内出现过这个偏移取值的次数（含首末帧）。</summary>
    public int Occurrences { get; init; }

    /// <summary>首末帧长度不同时为 true，此时 From/To 只对重叠部分有效。</summary>
    public bool LengthChanged { get; init; }

    public int FromLength { get; init; }

    public int ToLength { get; init; }
}

/// <summary>用户操作标记前后的对照。判断"按键有没有触发新报文"就靠它。</summary>
public sealed class CaptureMarkComparison
{
    public required string Note { get; init; }

    public long TimestampMono { get; init; }

    public DateTimeOffset TimestampUtc { get; init; }

    /// <summary>本步开始前（上一条标记之前）的累计条数。</summary>
    public int RecordsBefore { get; init; }

    /// <summary>本步窗口内的条数：上一条标记到这一条之间。</summary>
    public int RecordsInStep { get; init; }

    /// <summary>本步窗口内才首次出现的分组 key。</summary>
    public IReadOnlyList<string> NewGroupsInStep { get; init; } = [];

    /// <summary>**这一步之内**首末帧的字节差异。步骤引导的意义就在这一列：哪一步动了哪个字节。</summary>
    public IReadOnlyList<CapturePayloadChange> Changes { get; init; } = [];
}
