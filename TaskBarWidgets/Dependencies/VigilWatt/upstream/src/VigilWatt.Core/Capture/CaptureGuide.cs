namespace VigilWatt.Core.Capture;
/// <summary>
/// 功 能： 采集引导：把「自己想办法制造状态变化」变成一步一步照着做，每步自动记一条用户标记。
/// 2026/10/24 14:10:00 20211
/// 步骤顺序就是排查顺序：先有活动、再充电、再断电、再关机——差异分析靠这些标记分段。
/// </summary>
public sealed class CaptureGuideStep
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>具体怎么做（用户看不懂标题时要靠这句）。</summary>
    public required string Hint { get; init; }
}

public sealed class CaptureGuide
{
    private CaptureGuide(IReadOnlyList<CaptureGuideStep> steps) => Steps = steps;

    public IReadOnlyList<CaptureGuideStep> Steps { get; }

    public int CurrentIndex { get; private set; }

    public bool IsComplete => CurrentIndex >= Steps.Count;

    public CaptureGuideStep? Current => IsComplete ? null : Steps[CurrentIndex];

    public CaptureGuideStep? Previous => CurrentIndex > 0 ? Steps[CurrentIndex - 1] : null;

    /// <summary>默认引导：未知设备调查用。顺序按「先便宜后麻烦」排，充电/关机放后面。</summary>
    public static CaptureGuide Default() => new(
    [
        new CaptureGuideStep
        {
            Id = "device-action",
            Title = "操作设备",
            Hint = "按几下按键、移动鼠标或按手柄按钮，持续 5 秒左右。",
        },
        new CaptureGuideStep
        {
            Id = "plug-charger",
            Title = "连接充电器",
            Hint = "插上充电线，等 5 秒。充电状态通常会改变报文里的某些字节。",
        },
        new CaptureGuideStep
        {
            Id = "unplug-charger",
            Title = "断开充电器",
            Hint = "拔掉充电线，等 5 秒。和上一步对照就能看出哪个字节是充电标志。",
        },
        new CaptureGuideStep
        {
            Id = "power-off",
            Title = "关闭设备",
            Hint = "把设备电源关掉，等 5 秒。关机帧常用来判断离线/休眠标志。",
        },
        new CaptureGuideStep
        {
            Id = "power-on",
            Title = "重新开启设备",
            Hint = "把设备重新打开，等 5 秒。和上一步对照可看出唤醒帧。",
        },
    ]);

    /// <summary>标记当前步骤完成并前进。返回 false = 已经全部完成。</summary>
    public bool Advance()
    {
        if (IsComplete) return false;
        CurrentIndex++;
        return true;
    }

    /// <summary>回到上一步重做（不改变已完成步骤的记录）。</summary>
    public bool Back()
    {
        if (CurrentIndex == 0) return false;
        CurrentIndex--;
        return true;
    }

    public void Reset() => CurrentIndex = 0;
}
