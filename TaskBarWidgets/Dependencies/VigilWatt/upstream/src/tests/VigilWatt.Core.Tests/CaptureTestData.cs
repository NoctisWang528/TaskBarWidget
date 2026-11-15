using VigilWatt.Core.Capture;

namespace VigilWatt.Core.Tests;
/// <summary>
/// 功 能： 采集测试的公共素材：假设备、假记录、假采集来源、临时目录。
/// 2026/10/24 12:40:43 20211
/// 覆盖「用可注入的采集后端测，不依赖真实硬件」这条要求。
/// </summary>
internal static class CaptureTestData
{
    public static CaptureDeviceInfo Device(ushort vid = 0x24AE, ushort pid = 0x1412) => new()
    {
        DeviceId = $"cap:{vid:X4}:{pid:X4}:vid_{vid:x4}&pid_{pid:x4}&mi_01&col09",
        VendorId = vid,
        ProductId = pid,
        VersionNumber = 0x0100,
        Product = "Test Dongle",
        Manufacturer = "TestVendor",
        SerialNumber = "SN-0001",
        InterfacePath = $@"\\?\hid#vid_{vid:x4}&pid_{pid:x4}&mi_01&col09#8&abc&0&0006#{{4d1e55b2}}",
        InterfaceKey = $"vid_{vid:x4}&pid_{pid:x4}&mi_01&col09",
        GroupKey = $"vid_{vid:x4}&pid_{pid:x4}&mi_01",
        UsagePage = 0xFF00,
        Usage = 0x0002,
        InputReportLength = 19,
        OutputReportLength = 19,
        FeatureReportLength = 0,
        InputReportIds = [0x07],
        OutputReportIds = [0x13],
        CapsAvailable = true,
    };

    public static CaptureRecord Record(CaptureClock clock, CaptureDeviceInfo device, byte[] data,
        int? reportId = 0x07, string recordType = "hid_input_report",
        CaptureLayer layer = CaptureLayer.HidApiInput) => new()
    {
        TimestampMono = clock.ElapsedMs,
        TimestampUtc = DateTimeOffset.UtcNow,
        Layer = layer,
        Source = "usb-hid",
        DeviceId = device.DeviceId,
        InterfaceId = device.InterfacePath,
        RecordType = recordType,
        ReportId = reportId,
        Api = layer == CaptureLayer.HidApiFeature ? "HidD_GetFeature" : "ReadFile",
        Data = data,
        Status = CaptureRecordStatus.Received,
    };

    public static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"vw-capture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}

/// <summary>可注入的假采集来源：按给定帧发记录，然后一直等取消。</summary>
internal sealed class FakeCaptureSource : ICaptureSource
{
    private readonly IReadOnlyList<byte[]> _frames;
    private readonly CaptureDeviceInfo _device;
    private readonly bool _failOpen;

    public FakeCaptureSource(CaptureDeviceInfo device, IReadOnlyList<byte[]> frames, bool failOpen = false)
    {
        _device = device;
        _frames = frames;
        _failOpen = failOpen;
    }

    public string SourceName => "usb-hid";

    public int Runs { get; private set; }

    public async Task RunAsync(CaptureClock clock, ICaptureSink sink, CancellationToken token)
    {
        Runs++;
        if (_failOpen)
        {
            sink.Error(new CaptureError
            {
                TimestampMono = clock.ElapsedMs,
                TimestampUtc = DateTimeOffset.UtcNow,
                Kind = CaptureErrorKind.OpenDenied,
                Message = "模拟：接口被系统保护，打不开",
                HResult = 5,
                DeviceId = _device.DeviceId,
            });
            return;
        }

        foreach (var frame in _frames)
        {
            token.ThrowIfCancellationRequested();
            sink.Record(CaptureTestData.Record(clock, _device, frame));
            await Task.Delay(5, token).ConfigureAwait(false);
        }

        await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
    }

    public void Dispose()
    {
    }
}

/// <summary>可写来源：发一帧就记下来，用来验证「手动发帧」这条路径不依赖硬件。</summary>
internal sealed class FakeWritableSource : ICaptureSource, ICaptureWriter
{
    private readonly CaptureDeviceInfo _device;

    public FakeWritableSource(CaptureDeviceInfo device) => _device = device;

    public List<byte[]> Sent { get; } = [];

    public string? FailWith { get; set; }

    public string SourceName => "usb-hid";

    public async Task RunAsync(CaptureClock clock, ICaptureSink sink, CancellationToken token)
    {
        sink.Record(CaptureTestData.Record(clock, _device, [0x07, 0x10]));
        await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
    }

    public bool TrySend(byte[] report, out string? error)
    {
        if (FailWith != null)
        {
            error = FailWith;
            return false;
        }

        Sent.Add(report);
        error = null;
        return true;
    }

    public void Dispose()
    {
    }
}
