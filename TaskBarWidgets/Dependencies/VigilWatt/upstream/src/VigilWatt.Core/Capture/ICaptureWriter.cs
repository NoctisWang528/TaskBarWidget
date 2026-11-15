namespace VigilWatt.Core.Capture;
/// <summary>
/// 功 能： 采集来源的可选能力——向设备发一帧（主机→设备方向）。
/// 2026/10/24 22:20:00 20211
/// 只有用户显式授权（<see cref="CaptureOptions.AllowSend"/>）且句柄真的以读写方式打开时才可用；
/// 只读来源不实现本接口，采集逻辑据此判断能不能发。
/// </summary>
public interface ICaptureWriter
{
    /// <summary>发一帧。返回 false 时 <paramref name="error"/> 说明原因（未授权 / 未运行 / 写失败）。</summary>
    bool TrySend(byte[] report, out string? error);
}
