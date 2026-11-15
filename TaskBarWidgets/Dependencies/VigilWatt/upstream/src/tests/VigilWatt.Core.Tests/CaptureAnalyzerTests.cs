using VigilWatt.Core.Capture;
using Xunit;

namespace VigilWatt.Core.Tests;
/// <summary>
/// 功 能： 采集分析：分组、重复率、首末帧差异、用户标记前后对照，且**不得修改原始证据**。
/// 2026/10/24 12:40:43 20211
/// </summary>
public sealed class CaptureAnalyzerTests
{
    private static CaptureRecord Rec(CaptureClock clock, CaptureDeviceInfo device, byte[] data,
        long mono, int? reportId = 0x07) => new()
    {
        TimestampMono = mono,
        TimestampUtc = DateTimeOffset.UnixEpoch.AddMilliseconds(mono),
        Layer = CaptureLayer.HidApiInput,
        Source = "usb-hid",
        DeviceId = device.DeviceId,
        InterfaceId = device.InterfacePath,
        RecordType = "hid_input_report",
        ReportId = reportId,
        Api = "ReadFile",
        Data = data,
        Status = CaptureRecordStatus.Received,
    };

    [Fact]
    public void Groups_ByTypeAndReportId_NotByLength()
    {
        var device = CaptureTestData.Device();
        var clock = new CaptureClock();
        var records = new List<CaptureRecord>
        {
            Rec(clock, device, [0x07, 0x10], 10),
            Rec(clock, device, [0x07, 0x10], 20),
            Rec(clock, device, [0x07, 0x11], 30),
            Rec(clock, device, [0x08, 0x22, 0x33], 40, reportId: 0x08),
        };

        var analysis = CaptureAnalyzer.Analyze(records, [], []);

        Assert.Equal(4, analysis.TotalRecords);
        Assert.Equal(4, analysis.ReceivedRecords);
        Assert.Equal(2, analysis.Groups.Count);
        var main = analysis.Groups.Single(g => g.ReportId == 0x07);
        Assert.Equal(3, main.Count);
        Assert.Equal(2, main.DistinctPayloads);
        Assert.Equal(1.0 / 3, main.DuplicateRatio, 3);
        Assert.Equal("07 10", main.FirstHex);
        Assert.Equal(new[] { 2 }, main.Lengths);
    }

    [Fact]
    public void Diff_ReportsChangedOffsetBetweenFirstAndLast()
    {
        var device = CaptureTestData.Device();
        var clock = new CaptureClock();
        var records = new List<CaptureRecord>
        {
            Rec(clock, device, [0x07, 0x10, 0x04], 10),
            Rec(clock, device, [0x07, 0x10, 0x04], 20),
            Rec(clock, device, [0x07, 0x11, 0x04], 30),
        };

        var analysis = CaptureAnalyzer.Analyze(records, [], []);

        var change = Assert.Single(analysis.Changes);
        Assert.Equal(1, change.Offset);
        Assert.Equal<byte?>(0x10, change.From);
        Assert.Equal<byte?>(0x11, change.To);
        Assert.False(change.LengthChanged);
    }

    [Fact]
    public void Diff_ReportsLengthChange()
    {
        var device = CaptureTestData.Device();
        var clock = new CaptureClock();
        var records = new List<CaptureRecord>
        {
            Rec(clock, device, [0x07, 0x10], 10),
            Rec(clock, device, [0x07, 0x10, 0x99], 20),
        };

        var analysis = CaptureAnalyzer.Analyze(records, [], []);

        var change = Assert.Single(analysis.Changes);
        Assert.True(change.LengthChanged);
        Assert.Equal(2, change.FromLength);
        Assert.Equal(3, change.ToLength);
    }

    [Fact]
    public void Analyze_DoesNotMutateOriginalEvidence()
    {
        var device = CaptureTestData.Device();
        var clock = new CaptureClock();
        var first = new byte[] { 0x07, 0x10, 0x04 };
        var last = new byte[] { 0x07, 0x11, 0x04 };
        var records = new List<CaptureRecord> { Rec(clock, device, first, 10), Rec(clock, device, last, 20) };
        var beforeFirst = first.ToArray();
        var beforeLast = last.ToArray();
        var beforeCount = records.Count;

        _ = CaptureAnalyzer.Analyze(records, [], []);

        Assert.Equal(beforeFirst, records[0].Data);
        Assert.Equal(beforeLast, records[1].Data);
        Assert.Equal(beforeCount, records.Count);
        Assert.Equal(beforeFirst, first);
    }

    [Fact]
    public void MarkWindow_ReportsWhichBytesChangedInsideThatStep()
    {
        var device = CaptureTestData.Device();
        var clock = new CaptureClock();
        var records = new List<CaptureRecord>
        {
            Rec(clock, device, [0x07, 0x5B, 0x10], 10),
            Rec(clock, device, [0x07, 0x5B, 0x10], 20),
            Rec(clock, device, [0x07, 0x4F, 0x10], 300),
            Rec(clock, device, [0x07, 0x4E, 0x01], 400),
        };
        var events = new List<CaptureEvent>
        {
            new() { TimestampMono = 100, TimestampUtc = DateTimeOffset.UnixEpoch, Kind = CaptureEventKind.UserMark, Note = "连接充电器" },
            new() { TimestampMono = 500, TimestampUtc = DateTimeOffset.UnixEpoch, Kind = CaptureEventKind.UserMark, Note = "断开充电器" },
        };

        var analysis = CaptureAnalyzer.Analyze(records, events, []);

        Assert.Equal(2, analysis.Marks.Count);
        // 第一步（会话开始–100）里两帧完全相同 → 没有变化
        Assert.Empty(analysis.Marks[0].Changes);
        // 第二步（100–500）里 0x4F→0x4E、0x10→0x01
        var second = analysis.Marks[1];
        Assert.Equal(2, second.RecordsInStep);
        Assert.Contains(second.Changes, c => c.Offset == 1 && c.From == 0x4F && c.To == 0x4E);
        Assert.Contains(second.Changes, c => c.Offset == 2 && c.From == 0x10 && c.To == 0x01);
    }

    [Fact]
    public void BatteryReference_ListsOffsetsEqualToUserPercent()
    {
        var device = CaptureTestData.Device();
        var clock = new CaptureClock();
        var records = new List<CaptureRecord>
        {
            Rec(clock, device, [0x07, 0x4E, 0x01], 100),
        };
        var events = new List<CaptureEvent>
        {
            new()
            {
                TimestampMono = 90,
                TimestampUtc = DateTimeOffset.UnixEpoch,
                Kind = CaptureEventKind.BatteryReference,
                Percent = 78,
            },
        };

        var analysis = CaptureAnalyzer.Analyze(records, events, []);

        var reference = Assert.Single(analysis.BatteryReferences);
        Assert.Equal(78, reference.Percent);
        Assert.Equal("07 4E 01", reference.NextRecordHex);
        Assert.Equal(new[] { 1 }, reference.MatchingOffsets);
    }

    [Fact]
    public void BatteryReference_WithoutLaterRecord_IsKeptButHasNoFrame()
    {
        var device = CaptureTestData.Device();
        var clock = new CaptureClock();
        var events = new List<CaptureEvent>
        {
            new()
            {
                TimestampMono = 500,
                TimestampUtc = DateTimeOffset.UnixEpoch,
                Kind = CaptureEventKind.BatteryReference,
                Percent = 55,
            },
        };

        var analysis = CaptureAnalyzer.Analyze([Rec(clock, device, [0x07, 0x10], 10)], events, []);

        var reference = Assert.Single(analysis.BatteryReferences);
        Assert.Null(reference.NextRecordHex);
        Assert.Empty(reference.MatchingOffsets);
    }

    [Fact]
    public void Marks_WindowRunsFromPreviousMarkToThisOne_AndFlagsNewGroups()
    {
        var device = CaptureTestData.Device();
        var clock = new CaptureClock();
        var records = new List<CaptureRecord>
        {
            Rec(clock, device, [0x07, 0x10], 10),
            Rec(clock, device, [0x07, 0x11], 20),
            Rec(clock, device, [0x09, 0x01], 300, reportId: 0x09),
        };
        var events = new List<CaptureEvent>
        {
            new()
            {
                TimestampMono = 100,
                TimestampUtc = DateTimeOffset.UnixEpoch,
                Kind = CaptureEventKind.UserMark,
                Note = "按下设备按键",
            },
            new()
            {
                TimestampMono = 400,
                TimestampUtc = DateTimeOffset.UnixEpoch,
                Kind = CaptureEventKind.UserMark,
                Note = "再按一次",
            },
        };

        var analysis = CaptureAnalyzer.Analyze(records, events, []);

        Assert.Equal(2, analysis.Marks.Count);
        // 第一条标记的窗口从会话开始算，两帧都在里面
        Assert.Equal(0, analysis.Marks[0].RecordsBefore);
        Assert.Equal(2, analysis.Marks[0].RecordsInStep);
        // 第二条只覆盖 100–400：之前累计 2 条，本步 1 条，且 0x09 是本步才出现的分组
        var mark = analysis.Marks[1];
        Assert.Equal("再按一次", mark.Note);
        Assert.Equal(2, mark.RecordsBefore);
        Assert.Equal(1, mark.RecordsInStep);
        Assert.Contains(mark.NewGroupsInStep, key => key.Contains("0x09", StringComparison.OrdinalIgnoreCase) || key.Contains("09"));
    }

    [Fact]
    public void StaticGroups_NeedsEnoughRecords_BeforeConcluding()
    {
        var device = CaptureTestData.Device();
        var clock = new CaptureClock();
        List<CaptureRecord> Same(int count) => Enumerable.Range(0, count)
            .Select(i => Rec(clock, device, [0x07, 0x10], i * 10L))
            .ToList();

        // 2~3 帧不足以说明「这条通道没变」，别据此下结论
        Assert.Empty(CaptureAnalyzer.Analyze(Same(4), [], []).StaticGroups);
        Assert.Single(CaptureAnalyzer.Analyze(Same(CaptureAnalysis.StaticGroupMinRecords), [], []).StaticGroups);
    }

    [Fact]
    public void FailedRecords_AreCountedSeparately()
    {
        var device = CaptureTestData.Device();
        var clock = new CaptureClock();
        var failed = new CaptureRecord
        {
            TimestampMono = 5,
            TimestampUtc = DateTimeOffset.UnixEpoch,
            Layer = CaptureLayer.HidApiInput,
            Source = "usb-hid",
            DeviceId = device.DeviceId,
            RecordType = "hid_input_report",
            Data = [],
            Status = CaptureRecordStatus.Failed,
            Error = "读失败",
        };

        var analysis = CaptureAnalyzer.Analyze([failed], [], []);

        Assert.Equal(1, analysis.TotalRecords);
        Assert.Equal(0, analysis.ReceivedRecords);
        Assert.Equal(1, analysis.FailedRecords);
        Assert.Empty(analysis.Groups);
    }

    [Fact]
    public void GroupKey_EncodesTypeAndReportId()
    {
        var device = CaptureTestData.Device();
        var clock = new CaptureClock();
        var key = CaptureAnalyzer.GroupKeyOf(Rec(clock, device, [0x07, 0x10], 0));
        Assert.Equal("hid_input_report|07", key);
    }
}
