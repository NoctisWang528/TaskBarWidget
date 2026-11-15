namespace VigilWatt.Core.Capture;
/// <summary>
/// 功 能： 对采集记录做基础统计、分组与首末帧差异；**只读输入，不修改任何原始证据**。
/// 2026/10/24 12:40:43 20211
/// </summary>
public static class CaptureAnalyzer
{
    /// <summary>报告里展示的首帧前缀长度（字节）。太长会淹没差异信息。</summary>
    private const int HexPrefixBytes = 16;

    public static CaptureAnalysis Analyze(
        IReadOnlyList<CaptureRecord> records,
        IReadOnlyList<CaptureEvent> events,
        IReadOnlyList<CaptureError> errors)
    {
        var received = records.Where(r => r.Status == CaptureRecordStatus.Received).ToList();
        var groups = BuildGroups(received);
        return new CaptureAnalysis
        {
            TotalRecords = records.Count,
            ReceivedRecords = received.Count,
            // 别用「总数 - 收到」算失败：主机发出去的帧（Sent）既不是收到也不是失败
            FailedRecords = records.Count(r => r.Status == CaptureRecordStatus.Failed),
            SentRecords = records.Count(r => r.Status == CaptureRecordStatus.Sent),
            TotalEvents = events.Count,
            TotalErrors = errors.Count,
            TotalBytes = received.Sum(r => (long)r.Data.Length),
            Groups = groups,
            Changes = BuildChanges(groups, received),
            Marks = BuildMarks(received, events),
            BatteryReferences = BuildBatteryReferences(received, events),
        };
    }

    /// <summary>分组只按 (记录类型, report ID)：长度变化本身是要报告的证据，不能当成分组依据。</summary>
    public static string GroupKeyOf(CaptureRecord record)
        => $"{record.RecordType}|{(record.ReportId is { } id ? id.ToString("X2") : "-")}";

    private static List<CaptureRecordGroup> BuildGroups(List<CaptureRecord> received)
    {
        var groups = new List<CaptureRecordGroup>();
        foreach (var bucket in received.GroupBy(GroupKeyOf))
        {
            var items = bucket.ToList();
            var distinct = items.Select(r => r.DataHex).Distinct(StringComparer.Ordinal).Count();
            var first = items[0];
            var last = items[^1];
            groups.Add(new CaptureRecordGroup
            {
                Key = bucket.Key,
                RecordType = first.RecordType,
                ReportId = first.ReportId,
                Length = first.Length,
                Lengths = items.Select(r => r.Length).Distinct().OrderBy(l => l).ToList(),
                Count = items.Count,
                DistinctPayloads = distinct,
                DuplicateRatio = items.Count == 0 ? 0 : 1.0 - (double)distinct / items.Count,
                FirstSeenUtc = items.Min(r => r.TimestampUtc),
                LastSeenUtc = items.Max(r => r.TimestampUtc),
                FirstHexPrefix = CaptureRecord.ToHex(first.Data.AsSpan(0, Math.Min(HexPrefixBytes, first.Length))),
                FirstHex = first.DataHex,
                LastHex = last.DataHex,
            });
        }
        return groups.OrderByDescending(g => g.Count).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
    }

    private static List<CapturePayloadChange> BuildChanges(List<CaptureRecordGroup> groups, List<CaptureRecord> received)
    {
        var changes = new List<CapturePayloadChange>();
        foreach (var group in groups)
        {
            if (group.Count < 2) continue;
            var items = received.Where(r => GroupKeyOf(r) == group.Key).OrderBy(r => r.TimestampMono).ToList();
            AppendDiff(changes, group.Key, items);
        }
        return changes;
    }

    /// <summary>同一分组内首帧 vs 末帧的逐字节差异。两个调用方共用，避免两套判据。</summary>
    private static void AppendDiff(List<CapturePayloadChange> changes, string groupKey, List<CaptureRecord> items)
    {
        if (items.Count < 2) return;
        var first = items[0].Data;
        var last = items[^1].Data;
        if (first.AsSpan().SequenceEqual(last)) return;

        if (first.Length != last.Length)
        {
            changes.Add(new CapturePayloadChange
            {
                GroupKey = groupKey,
                Offset = Math.Min(first.Length, last.Length),
                LengthChanged = true,
                FromLength = first.Length,
                ToLength = last.Length,
                Occurrences = items.Count,
            });
        }

        var shared = Math.Min(first.Length, last.Length);
        for (var i = 0; i < shared; i++)
        {
            if (first[i] == last[i]) continue;
            changes.Add(new CapturePayloadChange
            {
                GroupKey = groupKey,
                Offset = i,
                From = first[i],
                To = last[i],
                Occurrences = items.Count(r => r.Data.Length > i && r.Data[i] == last[i]),
            });
        }
    }

    /// <summary>
    /// 把用户输入的电量与其后第一帧对齐，列出值等于该电量的字节偏移。
    /// **只是候选**：一个字节等于 78 可能纯属巧合，多采几个点才能收敛。
    /// </summary>
    private static List<CaptureBatteryReference> BuildBatteryReferences(List<CaptureRecord> received, IReadOnlyList<CaptureEvent> events)
    {
        var list = new List<CaptureBatteryReference>();
        var ordered = received.OrderBy(r => r.TimestampMono).ToList();
        foreach (var item in events.Where(e => e.Kind == CaptureEventKind.BatteryReference && e.Percent is not null)
                     .OrderBy(e => e.TimestampMono))
        {
            var percent = item.Percent!.Value;
            var next = ordered.FirstOrDefault(r => r.TimestampMono >= item.TimestampMono);
            var offsets = new List<int>();
            if (next != null)
            {
                for (var i = 0; i < next.Data.Length; i++)
                {
                    if (next.Data[i] == percent) offsets.Add(i);
                }
            }
            list.Add(new CaptureBatteryReference
            {
                Percent = percent,
                TimestampMono = item.TimestampMono,
                TimestampUtc = item.TimestampUtc,
                NextRecordHex = next?.DataHex,
                NextRecordMono = next?.TimestampMono,
                MatchingOffsets = offsets,
            });
        }
        return list;
    }

    /// <summary>每个标记代表「这一步做完了」，所以本步窗口是**上一条标记到这一条**，不是这条之后。</summary>
    private static List<CaptureMarkComparison> BuildMarks(List<CaptureRecord> received, IReadOnlyList<CaptureEvent> events)
    {
        var marks = new List<CaptureMarkComparison>();
        var marksInOrder = events.Where(e => e.Kind == CaptureEventKind.UserMark).OrderBy(e => e.TimestampMono).ToList();
        var ordered = received.OrderBy(r => r.TimestampMono).ToList();
        for (var index = 0; index < marksInOrder.Count; index++)
        {
            var mark = marksInOrder[index];
            // 第一步没有上一条标记，窗口从会话开始算（long.MinValue 只是"没有下界"）
            var start = index == 0 ? long.MinValue : marksInOrder[index - 1].TimestampMono;
            var before = ordered.Where(r => r.TimestampMono < start).ToList();
            var window = ordered.Where(r => r.TimestampMono >= start && r.TimestampMono < mark.TimestampMono).ToList();
            var beforeKeys = before.Select(GroupKeyOf).ToHashSet(StringComparer.Ordinal);
            var stepChanges = new List<CapturePayloadChange>();
            foreach (var bucket in window.GroupBy(GroupKeyOf))
            {
                AppendDiff(stepChanges, bucket.Key, bucket.OrderBy(r => r.TimestampMono).ToList());
            }
            marks.Add(new CaptureMarkComparison
            {
                Note = string.IsNullOrWhiteSpace(mark.Note) ? "(未填写)" : mark.Note,
                TimestampMono = mark.TimestampMono,
                TimestampUtc = mark.TimestampUtc,
                RecordsBefore = before.Count,
                RecordsInStep = window.Count,
                NewGroupsInStep = window.Select(GroupKeyOf).Distinct(StringComparer.Ordinal)
                    .Where(k => !beforeKeys.Contains(k)).ToList(),
                Changes = stepChanges,
            });
        }
        return marks;
    }
}
