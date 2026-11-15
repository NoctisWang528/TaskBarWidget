using VigilWatt.Core.Capture;
using Xunit;

namespace VigilWatt.Core.Tests;
/// <summary>
/// 功 能： 报告 / 摘要 / 诊断包：关键段落齐全、字符串转义、ZIP 条目白名单与路径穿越防护。
/// 2026/10/24 12:40:43 20211
/// </summary>
public sealed class CaptureReportTests
{
    private static CaptureBundle BuildBundle(string product = "Test Dongle",
        CaptureTaskKind task = CaptureTaskKind.UnknownDeviceInvestigation)
    {
        var device = CaptureTestData.Device();
        device = new CaptureDeviceInfo
        {
            DeviceId = device.DeviceId,
            VendorId = device.VendorId,
            ProductId = device.ProductId,
            Product = product,
            Manufacturer = device.Manufacturer,
            SerialNumber = device.SerialNumber,
            InterfacePath = device.InterfacePath,
            InterfaceKey = device.InterfaceKey,
            GroupKey = device.GroupKey,
            InputReportLength = device.InputReportLength,
            InputReportIds = device.InputReportIds,
            CapsAvailable = true,
        };

        var clock = new CaptureClock();
        var records = new List<CaptureRecord>
        {
            CaptureTestData.Record(clock, device, [0x07, 0x10, 0x04]),
            CaptureTestData.Record(clock, device, [0x07, 0x11, 0x04]),
        };
        var events = new List<CaptureEvent>
        {
            new()
            {
                TimestampMono = 10,
                TimestampUtc = DateTimeOffset.UnixEpoch,
                Kind = CaptureEventKind.UserMark,
                Note = "按下设备按键",
            },
        };
        var errors = new List<CaptureError>
        {
            new()
            {
                TimestampMono = 11,
                TimestampUtc = DateTimeOffset.UnixEpoch,
                Kind = CaptureErrorKind.OpenDenied,
                Message = "另一个接口打不开",
                HResult = 5,
            },
        };

        var session = new CaptureSessionInfo
        {
            Id = "20261024-120000-ABCD",
            Task = task,
            StartedUtc = DateTimeOffset.UnixEpoch,
            EndedUtc = DateTimeOffset.UnixEpoch.AddSeconds(30),
            DurationMs = 30_000,
            OsVersion = "Windows 11 10.0.26200",
            AppVersion = "1.2.3",
            Backend = "Win32 hid.dll + CfgMgr32（用户态，无驱动）",
            DeviceId = device.DeviceId,
            Capabilities =
            [
                new CapabilityItem
                {
                    Id = "input-report", Title = "读取 HID 输入报告", Status = CapabilityStatus.Available,
                },
                new CapabilityItem
                {
                    Id = "report-descriptor", Title = "HID 报告描述符（原始字节）", Status = CapabilityStatus.Unavailable,
                    Reason = "用户态没有该 API", Impact = "拿不到原始字节", NextStep = "用 USBPcap",
                },
            ],
            RecordCount = records.Count,
            EventCount = events.Count,
            ErrorCount = errors.Count,
        };

        return new CaptureBundle
        {
            Session = session,
            Device = device,
            Records = records,
            Events = events,
            Errors = errors,
            Analysis = CaptureAnalyzer.Analyze(records, events, errors),
        };
    }

    [Fact]
    public void Html_ContainsAllRequiredSections()
    {
        var html = HtmlReportGenerator.Generate(BuildBundle());

        foreach (var heading in new[]
                 {
                     "概览", "目标设备", "采集方式", "能力预检", "采集范围与已知限制",
                     "事件时间线", "原始数据概览", "原始记录", "首帧 / 末帧差异", "错误与告警",
                     "数据完整性", "事实 / 推断 / 未知", "调查摘要",
                 })
        {
            Assert.Contains(heading, html);
        }
        Assert.Contains("20261024-120000-ABCD", html);
        Assert.Contains("1.2.3", html);
        Assert.Contains("不可用", html);
        Assert.Contains("原始 HID 报告描述符", html);
    }

    [Theory]
    [InlineData(CaptureTaskKind.UnknownDeviceInvestigation, "未知设备调查", "已支持设备诊断")]
    [InlineData(CaptureTaskKind.SupportedDeviceDiagnosis, "已支持设备诊断", "未知设备调查")]
    public void Report_StatesTheTaskTheUserChose(CaptureTaskKind task, string expected, string forbidden)
    {
        var bundle = BuildBundle(task: task);

        foreach (var text in new[] { HtmlReportGenerator.Generate(bundle), IssueSummaryBuilder.Build(bundle) })
        {
            Assert.Contains(expected, text);
            Assert.DoesNotContain(forbidden, text);
        }
    }

    [Fact]
    public void Html_SaysWhenAChannelNeverChanged()
    {
        // 真机形态：雷柏 Col06 的 feature 0x08 采 43 帧全是 02 00 00…，用户看不出「这条通道没信息」
        var bundle = BuildBundle();
        var clock = new CaptureClock();
        var records = Enumerable.Range(0, 6)
            .Select(_ => CaptureTestData.Record(clock, bundle.Device!, [0x02, 0x00, 0x00]))
            .ToList();
        var staticBundle = new CaptureBundle
        {
            Session = bundle.Session,
            Device = bundle.Device,
            Records = records,
            Analysis = CaptureAnalyzer.Analyze(records, [], []),
        };

        var html = HtmlReportGenerator.Generate(staticBundle);
        var summary = IssueSummaryBuilder.Build(staticBundle);

        Assert.Contains("没有任何变化", html);
        Assert.Contains("配置/握手通道", html);
        Assert.Contains("整场没有变化", summary);
        // 报告/摘要/界面共用这一份措辞，格式别各写一遍
        Assert.Contains("report 0x07（6 帧）", summary);
    }

    [Fact]
    public void Html_DoesNotClaimStatic_WhenPayloadsVary()
    {
        var html = HtmlReportGenerator.Generate(BuildBundle());
        var summary = IssueSummaryBuilder.Build(BuildBundle());

        Assert.DoesNotContain("没有任何变化", html);
        Assert.DoesNotContain("整场没有变化", summary);
    }

    [Fact]
    public void Html_IsOfflineAndScriptFree()
    {
        var html = HtmlReportGenerator.Generate(BuildBundle());
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Html_EscapesDeviceStrings()
    {
        var html = HtmlReportGenerator.Generate(BuildBundle("<script>alert(1)</script> & Co"));
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&amp; Co", html);
    }

    [Fact]
    public void Summary_ContainsDeviceEnvironmentAndNextSteps()
    {
        var summary = IssueSummaryBuilder.Build(BuildBundle(), @"C:\data\captures\x\vigilwatt-capture.zip");

        Assert.Contains("### 设备", summary);
        Assert.Contains("Test Dongle", summary);
        Assert.Contains("VID_24AE&PID_1412", summary);
        Assert.Contains("SN-0001", summary);
        Assert.Contains("USB/HID", summary);
        Assert.Contains("### 能力预检", summary);
        Assert.Contains("### 需要进一步验证", summary);
        Assert.Contains("vigilwatt-capture.zip", summary);
    }

    [Fact]
    public void Summary_WhenNoRecords_DoesNotClaimDeviceHasNoProtocol()
    {
        var bundle = BuildBundle();
        var empty = new CaptureBundle
        {
            Session = bundle.Session,
            Device = bundle.Device,
            Analysis = CaptureAnalyzer.Analyze([], [], []),
        };

        var summary = IssueSummaryBuilder.Build(empty);

        Assert.Contains("不等于设备没有相关协议", summary);
    }

    [Fact]
    public void Package_WritesAllWhitelistedEntries()
    {
        var root = CaptureTestData.NewTempDir();
        try
        {
            var bundle = BuildBundle();
            var dir = Path.Combine(root, "session");
            Directory.CreateDirectory(dir);
            CaptureStore.WriteSession(dir, bundle.Session);
            CaptureStore.WriteDevice(dir, bundle.Device!);
            CaptureStore.WriteAnalysis(dir, bundle.Analysis);
            CaptureStore.WriteReadme(dir, bundle);
            CaptureStore.WriteReport(dir, bundle, null);
            File.WriteAllText(CaptureStore.PathOf(dir, "records.jsonl"), CaptureJson.RecordLine(bundle.Records[0]) + "\n");
            File.WriteAllText(CaptureStore.PathOf(dir, "events.jsonl"), CaptureJson.EventLine(bundle.Events[0]) + "\n");
            File.WriteAllText(CaptureStore.PathOf(dir, "errors.jsonl"), CaptureJson.ErrorLine(bundle.Errors[0]) + "\n");
            CaptureStore.WriteBackendLog(dir, "test\n");

            var zipPath = Path.Combine(root, CapturePackageWriter.ZipFileName);
            CapturePackageWriter.Write(dir, zipPath);

            using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
            var names = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);
            foreach (var expected in CapturePackageWriter.EntryNames)
            {
                Assert.Contains(expected, names);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Package_RejectsPathTraversal()
    {
        Assert.Throws<ArgumentException>(() => CapturePackageWriter.ValidateEntryName("../evil.txt"));
        Assert.Throws<ArgumentException>(() => CapturePackageWriter.ValidateEntryName("a/b.txt"));
        Assert.Throws<ArgumentException>(() => CapturePackageWriter.ValidateEntryName(@"C:\evil.txt"));
        Assert.Throws<ArgumentException>(() => CapturePackageWriter.ValidateEntryName(""));
        CapturePackageWriter.ValidateEntryName("report.html");
    }

    [Fact]
    public void Readme_StatesLimitsAndPrivacy()
    {
        var readme = CapturePackageWriter.BuildReadme(BuildBundle());
        Assert.Contains("report.html", readme);
        Assert.Contains("records.jsonl", readme);
        Assert.Contains("dataBase64", readme);
        Assert.Contains("不含 USB 总线级通信", readme);
        Assert.Contains("序列号", readme);
        Assert.Contains("不会自动上传", readme);
    }
}
