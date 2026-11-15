namespace VigilWatt.Core.Capture;
/// <summary>
/// 功 能： 采集上限与时钟。上限防日志无限增长，时钟给单调时间戳（排序/间隔）。
/// 2026/10/24 12:40:43 20211
/// </summary>
public sealed class CaptureLimits
{
    /// <summary>默认 2 万条。2 万条 × 64 字节约 1.3 MB，够覆盖一次按键/充电全过程。</summary>
    public int MaxRecords { get; init; } = 20_000;

    public long MaxBytes { get; init; } = 8L * 1024 * 1024;

    public bool IsExceeded(int recordCount, long bytes) => recordCount >= MaxRecords || bytes >= MaxBytes;

    public string Describe() => $"上限 {MaxRecords:N0} 条 / {MaxBytes / 1024 / 1024} MB";
}

/// <summary>会话单调时钟：所有记录的时间戳都相对它，保证同一会话内可比较。</summary>
public sealed class CaptureClock
{
    private readonly System.Diagnostics.Stopwatch _stopwatch = new();

    public void Start() => _stopwatch.Restart();

    public long ElapsedMs => _stopwatch.ElapsedMilliseconds;
}
