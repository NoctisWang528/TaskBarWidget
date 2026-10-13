using System.Diagnostics.CodeAnalysis;

namespace AFMediaBar.Classes.Models;

/// <summary>
/// 一个逐字（音节）时间片：文本与它在曲目时间轴上的起止秒数。
/// A single syllable span: its text and its start and end seconds on the track timeline.
/// </summary>
/// <param name="Start">起始秒数 / Start in seconds.</param>
/// <param name="End">结束秒数 / End in seconds.</param>
/// <param name="Text">该时间片的文本 / Text of this span.</param>
public readonly record struct LyricWord(double Start, double End, string Text);

/// <summary>
/// 歌词同步级别：决定是否具备逐字擦亮的条件。
/// Lyric synchronization level, which decides whether syllable-level highlighting is possible.
/// </summary>
public enum LyricsSyncType
{
    /// <summary>未识别。/ Not recognized.</summary>
    Unknown = 0,

    /// <summary>没有时间轴（纯文本歌词）。/ No timeline (plain-text lyrics).</summary>
    Unsynced = 1,

    /// <summary>行级同步。/ Line synced.</summary>
    LineSynced = 2,

    /// <summary>逐字（音节）同步。/ Syllable synced.</summary>
    SyllableSynced = 3,

    /// <summary>同一份歌词里行级与逐字混合。/ Both line-synced and syllable-synced inside one document.</summary>
    MixedSynced = 4
}

/// <summary>
/// 一行歌词：时间窗、文本、可选逐字时间轴、行级译文与音译。
/// A lyric line: its time window, text, optional syllable timeline, and line-level translation and romanization.
///
/// 行结束时间由解析器补齐（下一个行起点或曲目时长），因此呈现层只按窗口换算进度，不再自行推断。
/// The line end is completed by the parser (from the next line's start or the track duration), so the presentation
/// layer only converts a position into progress and never infers the window itself.
/// </summary>
public sealed class LyricLine
{
    /// <summary>
    /// 创建一行歌词。
    /// Creates one lyric line.
    /// </summary>
    /// <param name="start">起始秒数 / Start in seconds.</param>
    /// <param name="end">结束秒数 / End in seconds.</param>
    /// <param name="text">行文本 / Line text.</param>
    [SetsRequiredMembers]
    public LyricLine(double start, double end, string text)
    {
        Start = start;
        End = end;
        Text = text;
    }

    /// <summary>行起始时间（秒）。/ Line start in seconds.</summary>
    public required double Start { get; init; }

    /// <summary>行结束时间（秒）。/ Line end in seconds.</summary>
    public required double End { get; init; }

    /// <summary>行文本。/ Line text.</summary>
    public required string Text { get; init; }

    /// <summary>逐字时间轴；行级歌词为空集合。/ Syllable timeline; empty for line-level lyrics.</summary>
    public IReadOnlyList<LyricWord> Words { get; init; } = [];

    /// <summary>行级译文，缺失为 null。/ Line-level translation; null when absent.</summary>
    public string? Translation { get; init; }

    /// <summary>行级音译，缺失为 null。/ Line-level romanization; null when absent.</summary>
    public string? Romanization { get; init; }

    /// <summary>
    /// 该行是否附带背景人声：附带的子行文本已按括号并入 <see cref="Text"/>，这里只描述行的性质。
    /// Whether the line carries a background vocal: the attached sub-line has already been folded into
    /// <see cref="Text"/> in parentheses, so this only describes the nature of the line.
    /// </summary>
    public bool IsBackground { get; init; }
}

/// <summary>
/// 一首曲目的歌词文档：解析后的行、同步级别与来源格式。
/// The lyric document of one track: parsed lines, sync level, and source format.
/// </summary>
/// <param name="Lines">按起始时间升序排列的行 / Lines ordered by start time.</param>
/// <param name="SyncType">同步级别 / Synchronization level.</param>
/// <param name="SourceFormat">来源格式标识（如 LRC、YRC、QRC），只用于诊断 / Source format identifier such as LRC, YRC, or QRC; diagnostics only.</param>
public sealed record LyricDocument(
    IReadOnlyList<LyricLine> Lines,
    LyricsSyncType SyncType,
    string SourceFormat)
{
    /// <summary>
    /// 空文档：没有歌词或解析失败时使用，界面回落到标题与歌手。
    /// Empty document used when lyrics are absent or parsing failed, leaving the UI on title and artist.
    /// </summary>
    public static LyricDocument Empty { get; } = new([], LyricsSyncType.Unknown, "none");
}
