using VigilWatt.Core.Capture;
using Xunit;

namespace VigilWatt.Core.Tests;
/// <summary>
/// 功 能： 采集记录的 JSONL 契约：原始字节、report ID、时间戳、缺失字段与层级都不许走样。
/// 2026/10/24 12:40:43 20211
/// </summary>
public sealed class CaptureRecordTests
{
    [Fact]
    public void RoundTrip_KeepsRawBytesReportIdAndTimestamps()
    {
        var clock = new CaptureClock();
        clock.Start();
        var device = CaptureTestData.Device();
        var payload = new byte[] { 0x07, 0x10, 0x04, 0x80, 0x0C, 0x80, 0x0C, 0x01, 0x54 };
        var record = CaptureTestData.Record(clock, device, payload);

        var line = CaptureJson.RecordLine(record);
        Assert.True(CaptureJson.TryParseRecord(line, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(payload, parsed!.Data);
        Assert.Equal(7, parsed.ReportId);
        Assert.Equal(CaptureLayer.HidApiInput, parsed.Layer);
        Assert.Equal("ReadFile", parsed.Api);
        Assert.Equal(payload.Length, parsed.Length);
        Assert.Equal(record.TimestampMono, parsed.TimestampMono);
        Assert.Equal(record.TimestampUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            parsed.TimestampUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
    }

    [Fact]
    public void DataHex_IsDerivedFromBytes_NotTheOtherWayRound()
    {
        var device = CaptureTestData.Device();
        var clock = new CaptureClock();
        var record = CaptureTestData.Record(clock, device, [0x07, 0xAB, 0x00]);

        Assert.Equal("07 AB 00", record.DataHex);
        Assert.Equal(3, record.Data.Length);
        // hex 变了但字节没变 → 说明 hex 只是展示
        var line = CaptureJson.RecordLine(record);
        Assert.Contains("\"dataBase64\":\"B6sA\"", line);
        Assert.Contains("\"dataHex\":\"07 AB 00\"", line);
    }

    [Fact]
    public void MissingFields_AreOmittedAndParseBackAsNull()
    {
        var record = new CaptureRecord
        {
            TimestampMono = 0,
            TimestampUtc = DateTimeOffset.UnixEpoch,
            Layer = CaptureLayer.DeviceEnum,
            Source = "usb-hid",
            DeviceId = "cap:0000:0000:x",
            RecordType = "device_enum",
            Data = [],
        };

        var line = CaptureJson.RecordLine(record);
        Assert.DoesNotContain("reportId", line);
        Assert.DoesNotContain("interfaceId", line);
        Assert.True(CaptureJson.TryParseRecord(line, out var parsed));
        Assert.Null(parsed!.ReportId);
        Assert.Null(parsed.InterfaceId);
        Assert.Empty(parsed.Data);
    }

    [Fact]
    public void FailedRecord_CarriesStatusAndError_AndEmptyData()
    {
        var record = new CaptureRecord
        {
            TimestampMono = 12,
            TimestampUtc = DateTimeOffset.UnixEpoch,
            Layer = CaptureLayer.HidApiInput,
            Source = "usb-hid",
            DeviceId = "cap:0000:0000:x",
            RecordType = "hid_input_report",
            Data = [],
            Status = CaptureRecordStatus.Failed,
            Error = "ReadFile 返回 0 字节",
        };

        var line = CaptureJson.RecordLine(record);
        Assert.Contains("\"status\":\"failed\"", line);
        Assert.True(CaptureJson.TryParseRecord(line, out var parsed));
        Assert.Equal(CaptureRecordStatus.Failed, parsed!.Status);
        Assert.Equal("ReadFile 返回 0 字节", parsed.Error);
        Assert.Empty(parsed.Data);
    }

    [Fact]
    public void Events_AreASeparateStream_FromRecords()
    {
        var item = new CaptureEvent
        {
            TimestampMono = 100,
            TimestampUtc = DateTimeOffset.UnixEpoch,
            Kind = CaptureEventKind.UserMark,
            Note = "按下设备按键",
        };

        var line = CaptureJson.EventLine(item);
        Assert.Contains("user_mark", line);
        Assert.DoesNotContain("dataBase64", line);
        Assert.True(CaptureJson.TryParseEvent(line, out var parsed));
        Assert.Equal(CaptureEventKind.UserMark, parsed!.Kind);
        Assert.Equal("按下设备按键", parsed.Note);
    }

    [Fact]
    public void Errors_RoundTripWithHResult()
    {
        var error = new CaptureError
        {
            TimestampMono = 5,
            TimestampUtc = DateTimeOffset.UnixEpoch,
            Kind = CaptureErrorKind.OpenDenied,
            Message = "无权访问",
            HResult = 5,
        };

        var line = CaptureJson.ErrorLine(error);
        Assert.True(CaptureJson.TryParseError(line, out var parsed));
        Assert.Equal(CaptureErrorKind.OpenDenied, parsed!.Kind);
        Assert.Equal(5, parsed.HResult);
    }

    [Fact]
    public void LayerAndStatusNames_RoundTrip()
    {
        foreach (var layer in Enum.GetValues<CaptureLayer>())
        {
            Assert.Equal(layer, CaptureNames.ParseLayer(layer.ToWire()));
        }
        foreach (var status in Enum.GetValues<CapabilityStatus>())
        {
            Assert.Equal(status, CaptureNames.ParseCapabilityStatus(status.ToWire()));
        }
        foreach (var kind in Enum.GetValues<CaptureErrorKind>())
        {
            Assert.Equal(kind, CaptureNames.ParseErrorKind(kind.ToWire()));
        }
        foreach (var state in Enum.GetValues<CaptureSessionState>())
        {
            Assert.Equal(state, CaptureNames.ParseState(state.ToWire()));
        }
    }

    [Fact]
    public void CapabilityItem_RequiresExplanation_WhenNotAvailable()
    {
        var silent = new CapabilityItem
        {
            Id = "x",
            Title = "某项能力",
            Status = CapabilityStatus.Unavailable,
        };
        Assert.False(silent.IsExplained);

        var explained = new CapabilityItem
        {
            Id = "x",
            Title = "某项能力",
            Status = CapabilityStatus.NeedsExtraTool,
            Reason = "缺工具",
            Impact = "采不到总线数据",
            NextStep = "装 USBPcap",
        };
        Assert.True(explained.IsExplained);

        var available = new CapabilityItem
        {
            Id = "y",
            Title = "另一项",
            Status = CapabilityStatus.Available,
        };
        Assert.True(available.IsExplained);
    }

    [Fact]
    public void DeviceJson_RoundTrip_KeepsFieldStructure()
    {
        var device = CaptureTestData.Device();
        var json = CaptureJson.DeviceJson(new CaptureDeviceInfo
        {
            DeviceId = device.DeviceId,
            VendorId = device.VendorId,
            ProductId = device.ProductId,
            InterfacePath = device.InterfacePath,
            InterfaceKey = device.InterfaceKey,
            GroupKey = device.GroupKey,
            Fields =
            [
                new HidFieldInfo
                {
                    ReportType = "input", ReportId = 0x07, UsagePage = 0xFF00, UsageMin = 0x0002, UsageMax = 0x0002,
                    BitSize = 8, ReportCount = 1, LogicalMin = 0, LogicalMax = 100, IsAbsolute = true,
                },
                new HidFieldInfo
                {
                    ReportType = "input", ReportId = 0x07, UsagePage = 0x0009, UsageMin = 0x0001, UsageMax = 0x0005,
                    BitSize = 1, ReportCount = 5, LogicalMin = 0, LogicalMax = 1, IsAbsolute = true, IsButton = true,
                },
            ],
        });

        Assert.True(CaptureJson.TryParseDevice(json, out var parsed), json);
        Assert.Equal(2, parsed!.Fields.Count);
        var value = parsed.Fields[0];
        Assert.Equal("input", value.ReportType);
        Assert.Equal(7, value.ReportId);
        Assert.Equal(8, value.BitSize);
        Assert.Equal(100, value.LogicalMax);
        Assert.False(value.IsButton);
        Assert.False(value.IsRange);

        var button = parsed.Fields[1];
        Assert.True(button.IsButton);
        Assert.True(button.IsRange);
        Assert.Equal(5, button.ReportCount);
    }

    [Fact]
    public void HidField_Describe_TellsSizeAndRange()
    {
        var value = new HidFieldInfo
        {
            ReportType = "input", ReportId = 0x07, UsagePage = 0xFF00, UsageMin = 0x0002, UsageMax = 0x0002,
            BitSize = 8, ReportCount = 1, LogicalMin = 0, LogicalMax = 100, IsAbsolute = true,
        };
        Assert.Equal("input 0x07 · 0xFF00/0x0002 · 8 bit × 1 · 0..100 · 绝对", value.Describe());

        var button = new HidFieldInfo
        {
            ReportType = "input", ReportId = 0x01, UsagePage = 0x0009, UsageMin = 0x0001, UsageMax = 0x0005,
            BitSize = 1, ReportCount = 5, LogicalMin = 0, LogicalMax = 1, IsButton = true,
        };
        Assert.Contains("按键 5 个", button.Describe());
        Assert.Contains("0x0001–0x0005", button.Describe());
    }

    [Fact]
    public void DeviceJson_RoundTrip_KeepsReportIdsAndMissingSerial()
    {
        var device = CaptureTestData.Device();
        var json = CaptureJson.DeviceJson(new CaptureDeviceInfo
        {
            DeviceId = device.DeviceId,
            VendorId = device.VendorId,
            ProductId = device.ProductId,
            Product = device.Product,
            Manufacturer = device.Manufacturer,
            SerialNumber = null,
            InterfacePath = device.InterfacePath,
            InterfaceKey = device.InterfaceKey,
            GroupKey = device.GroupKey,
            InputReportIds = [0x07],
            FeatureReportIds = [0x08, 0x09],
            CapsAvailable = true,
        });

        Assert.True(CaptureJson.TryParseDevice(json, out var parsed), json);
        Assert.Null(parsed!.SerialNumber);
        Assert.Equal(new byte[] { 0x07 }, parsed.InputReportIds);
        Assert.Equal(new byte[] { 0x08, 0x09 }, parsed.FeatureReportIds);
        Assert.True(parsed.CapsAvailable);
    }
}
