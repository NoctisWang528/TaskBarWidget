namespace VigilWatt.Core.Capture;
/// <summary>
/// 功 能： 一条采集错误/警告。读失败只记这里，绝不生成空报文冒充成功记录。
/// 2026/10/24 12:40:43 20211
/// </summary>
public sealed class CaptureError
{
    public required long TimestampMono { get; init; }

    public required DateTimeOffset TimestampUtc { get; init; }

    public required CaptureErrorKind Kind { get; init; }

    public required string Message { get; init; }

    /// <summary>Win32 HRESULT / 错误码。取不到为 null，不填 0 占位。</summary>
    public int? HResult { get; init; }

    public string? DeviceId { get; init; }

    public string? InterfaceId { get; init; }
}
