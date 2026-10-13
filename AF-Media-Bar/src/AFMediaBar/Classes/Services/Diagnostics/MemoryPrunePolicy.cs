using AFMediaBar.Classes.Abstractions;

namespace AFMediaBar.Classes.Services;

/// <summary>
/// 决定当前该用哪一档内存剪枝，以及什么时候值得重新执行一次。
/// Decides which prune level applies right now, and when it is worth running the deep actions again.
///
/// 判定只依赖外部信号（电源、会话、显示器、媒体与用户空闲时长），因此可以纯逻辑测试：真实系统上的"锁屏 3 分钟后回收"这类结论
/// 由这些阈值表达，而不是散落在计时器回调里。
/// The decision depends only on external signals (power, session, display, media and user idle time), which keeps it testable: statements
/// such as "reclaim three minutes after the screen locks" live in these thresholds instead of being scattered across timer callbacks.
/// </summary>
public static class MemoryPrunePolicy
{
    /// <summary>多久没有媒体才算"媒体空闲"/ How long without media counts as media-idle.</summary>
    public static readonly TimeSpan MediaIdleThreshold = TimeSpan.FromMinutes(5);

    /// <summary>用户多久没有输入才算"用户空闲"/ How long without user input counts as user-idle.</summary>
    public static readonly TimeSpan UserIdleThreshold = TimeSpan.FromMinutes(10);

    /// <summary>同一档位下重复执行深度回收的最小间隔（显示一直关着时，定期再收一次）。/ Minimum interval between deep actions at one level.</summary>
    public static readonly TimeSpan MinimumReapply = TimeSpan.FromMinutes(10);

    /// <summary>空闲档位的评估周期：阈值是分钟级，30 秒一次足够，且不会成为新的常驻唤醒源。
    /// Evaluation period for the idle level: the thresholds are measured in minutes, so 30 seconds is plenty and stays a negligible wakeup source.</summary>
    public static readonly TimeSpan EvaluationInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 已剪枝时的评估周期。之所以比 <see cref="EvaluationInterval"/> 短得多，是因为"用户回来了"这件事没有广播可用：
    /// 键盘输入不产生任何系统事件，只有轮询 `GetLastInputInfo` 才看得见，因此恢复的延迟就是这一个周期。
    /// 它同时决定了可见界面的恢复延迟（暂停曲目的标题跑马灯、进度条），所以取 2 秒而不是分钟级。
    /// The evaluation period while pruned. It is far shorter than <see cref="EvaluationInterval"/> because "the user is back" has no broadcast: keyboard
    /// input raises no system event and is only visible by polling `GetLastInputInfo`, so the restore latency is exactly this period. It also decides how
    /// long visible interface elements stay frozen — a paused track's title marquee, the progress bar — which is why it is two seconds rather than minutes.
    /// </summary>
    public static readonly TimeSpan PrunedEvaluationInterval = TimeSpan.FromSeconds(2);

    /// <summary>显示器关闭但系统未睡眠时的兜底周期：进入与退出都由广播驱动，这里只是防止广播丢失后卡在剪枝状态。
    /// Safety period while the display is off but the system has not suspended: both edges are driven by broadcasts, so this only guards against
    /// a lost broadcast leaving the process pruned forever.</summary>
    public static readonly TimeSpan DisplayOffEvaluationInterval = TimeSpan.FromMinutes(2);

    /// <summary>睡眠期间的评估周期：机器已经在睡，进程不该再定时醒来，唤醒通知会立刻把状态改回来。
    /// Evaluation period while suspending: the machine is already asleep, so the process should not keep waking up; the resume notification turns
    /// the state back immediately.</summary>
    public static readonly TimeSpan SuspendedEvaluationInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 返回某一档位下的评估周期。
    /// Returns the evaluation period for one level.
    /// </summary>
    /// <param name="level">当前档位 / The current level.</param>
    /// <returns>评估周期 / The evaluation period.</returns>
    public static TimeSpan EvaluationIntervalFor(MemoryPruneLevel level) => level switch
    {
        MemoryPruneLevel.None => EvaluationInterval,
        MemoryPruneLevel.Idle => PrunedEvaluationInterval,
        MemoryPruneLevel.DisplayOff => DisplayOffEvaluationInterval,
        _ => SuspendedEvaluationInterval
    };

    /// <summary>
    /// 按外部信号解析当前档位。
    /// Resolves the current level from the external signals.
    ///
    /// 优先级从高到低：睡眠 → 显示关闭/会话锁定 → 空闲。空闲档位刻意要求**媒体与用户同时空闲**：用户正在操作电脑时清掉封面与歌词
    /// 缓存，只会让下一次切歌重新联网，得不偿失。
    /// Priority: suspending, then display-off or session-locked, then idle. The idle level deliberately requires **both** media and the user
    /// to be idle: dropping the artwork and lyric caches while someone is using the machine only makes the next track fetch them again.
    /// </summary>
    /// <param name="signals">外部信号 / The external signals.</param>
    /// <returns>当前适用的档位 / The level that currently applies.</returns>
    public static MemoryPruneLevel Resolve(MemoryPruneSignals signals)
    {
        if (signals.IsSuspended)
        {
            return MemoryPruneLevel.Suspended;
        }

        if (signals.IsSessionLocked || signals.IsDisplayOff)
        {
            return MemoryPruneLevel.DisplayOff;
        }

        if (signals.SinceLastMedia >= MediaIdleThreshold && signals.UserIdle >= UserIdleThreshold)
        {
            return MemoryPruneLevel.Idle;
        }

        return MemoryPruneLevel.None;
    }

    /// <summary>
    /// 判断是否该执行（或在同一档位下重新执行）深度回收。
    /// Decides whether the deep actions should run now, or run again while the level has not changed.
    /// </summary>
    /// <param name="level">解析出的档位 / The resolved level.</param>
    /// <param name="appliedLevel">上一次已应用的档位 / The level applied last.</param>
    /// <param name="sinceLastDeepAction">距上一次深度回收的时长 / Time since the last deep action.</param>
    /// <returns>需要执行时为 true / True when the actions should run.</returns>
    public static bool ShouldApply(MemoryPruneLevel level, MemoryPruneLevel appliedLevel, TimeSpan sinceLastDeepAction)
    {
        if (level != appliedLevel)
        {
            return true;
        }

        // 档位没变时只有"确实要回收工作集"的两档值得重做；空闲档只清缓存，反复清没有意义。
        // While the level is unchanged, only the two levels that really reclaim the working set are worth repeating; the idle level only
        // drops caches, and dropping them again and again achieves nothing.
        return level >= MemoryPruneLevel.DisplayOff && sinceLastDeepAction >= MinimumReapply;
    }
}

/// <summary>
/// 解析剪枝档位所需的外部信号。
/// The external signals the prune level is resolved from.
/// </summary>
/// <param name="IsSuspended">系统是否正在睡眠 / Whether the system is suspending.</param>
/// <param name="IsSessionLocked">会话是否已锁定 / Whether the session is locked.</param>
/// <param name="IsDisplayOff">显示器是否已关闭 / Whether the display is off.</param>
/// <param name="SinceLastMedia">距最后一次有媒体的时长 / Time since media was last connected.</param>
/// <param name="UserIdle">用户空闲时长 / How long the user has been idle.</param>
public readonly record struct MemoryPruneSignals(
    bool IsSuspended,
    bool IsSessionLocked,
    bool IsDisplayOff,
    TimeSpan SinceLastMedia,
    TimeSpan UserIdle);
