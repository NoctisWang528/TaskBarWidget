namespace VigilWatt.Core.Capture;
/// <summary>
/// 功 能： 采集来源抽象。新增来源（USB 总线、BLE）只实现它，采集/分析/报告都不必改。
/// 2026/10/24 12:40:43 20211
/// </summary>
public interface ICaptureSource : IDisposable
{
    /// <summary>来源标识，写进每条记录的 source 字段。</summary>
    string SourceName { get; }

    /// <summary>跑到取消为止。读失败要经 sink 报错，不要抛异常结束整个会话。</summary>
    Task RunAsync(CaptureClock clock, ICaptureSink sink, CancellationToken token);
}

/// <summary>采集来源向外投递的出口。实现方负责线程安全。</summary>
public interface ICaptureSink
{
    void Record(CaptureRecord record);

    void Error(CaptureError error);
}
