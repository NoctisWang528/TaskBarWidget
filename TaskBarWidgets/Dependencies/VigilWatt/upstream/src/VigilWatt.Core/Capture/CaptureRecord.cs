namespace VigilWatt.Core.Capture;
/// <summary>
/// 功 能： 一条原始采集记录。字节数组是唯一证据，十六进制只是它的派生展示。
/// 2026/10/24 12:40:43 20211
/// 时间戳同时留单调时钟（排序/间隔）与墙上时钟（可读），两者缺一不可。
/// </summary>
public sealed class CaptureRecord
{
    /// <summary>相对会话开始的毫秒数，来自 Stopwatch，不受系统时间调整影响。</summary>
    public required long TimestampMono { get; init; }

    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>该记录属于哪一层通信。**不要用字符串硬编码**，用 <see cref="CaptureLayer"/>。</summary>
    public required CaptureLayer Layer { get; init; }

    /// <summary>采集来源标识，目前恒为 "usb-hid"。</summary>
    public required string Source { get; init; }

    /// <summary>会话内稳定的设备标识（cap:VID:PID:接口段），不随 USB 口变化。</summary>
    public required string DeviceId { get; init; }

    /// <summary>HID 接口设备路径。null = 该记录不属于某个具体接口。</summary>
    public string? InterfaceId { get; init; }

    /// <summary>记录类型："hid_input_report" / "hid_feature_report" / "device_enum" / "hid_caps"。</summary>
    public required string RecordType { get; init; }

    public int? ReportId { get; init; }

    /// <summary>实际调用到的 API（"ReadFile" / "HidD_GetFeature"），便于判断证据来源。</summary>
    public string? Api { get; init; }

    /// <summary>原始字节。**只存这里，不要只存 hex**。</summary>
    public byte[] Data { get; init; } = [];

    public CaptureRecordStatus Status { get; init; } = CaptureRecordStatus.Received;

    /// <summary>失败原因（Status=Failed 时必有值）。</summary>
    public string? Error { get; init; }

    public int Length => Data.Length;

    /// <summary>由 <see cref="Data"/> 生成，只用于人读与报告展示。</summary>
    public string DataHex => ToHex(Data);

    public static string ToHex(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return "";
        var chars = Convert.ToHexString(data);
        return string.Create(chars.Length + chars.Length / 2 - 1, chars, static (span, source) =>
        {
            var w = 0;
            for (var i = 0; i < source.Length; i += 2)
            {
                if (i > 0) span[w++] = ' ';
                span[w++] = source[i];
                span[w++] = source[i + 1];
            }
        });
    }
}
