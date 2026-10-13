namespace AFMediaBar.Classes.Abstractions;

/// <summary>
/// 后台内存剪枝档位：越大表示越确定"当前没人需要这个进程马上响应"，可以回收得越彻底。
/// Background memory prune level: the higher it is, the surer it is that nobody needs this process to react immediately, and the more
/// can be reclaimed.
/// </summary>
public enum MemoryPruneLevel
{
    /// <summary>正常运行：一切照常，参与者也应恢复到常规节奏。/ Normal operation: everything as usual, and participants return to their normal pace.</summary>
    None = 0,

    /// <summary>空闲：长时间没有媒体且用户也没有输入。清缓存、放慢或停掉非必需计时器，不做工作集回收。
    /// Idle: no media for a long time and no user input either. Caches are dropped and non-essential timers slow down or stop, while the
    /// working set is left alone.</summary>
    Idle = 1,

    /// <summary>显示关闭或会话锁定：可以回收工作集、降内存优先级并停掉采集类工作。
    /// Display off or session locked: the working set can be reclaimed, memory priority lowered, and capture-style work stopped.</summary>
    DisplayOff = 2,

    /// <summary>系统进入睡眠：停掉一切非必需工作，工作集与内存优先级都压到最低。
    /// The system is suspending: every non-essential task stops, and both the working set and the memory priority go as low as they can.</summary>
    Suspended = 3
}

/// <summary>
/// 可由后台剪枝协调器回收的参与者。
/// A participant the background prune coordinator can reclaim from.
///
/// 由**资源的所有者**实现：协调器只按档位发通知，具体回收哪些缓存、哪些计时器由所有者决定（服务不反向操作窗口与控件）。
/// Implemented by the **owner** of the resource: the coordinator only publishes a level, while the owner decides which caches and which
/// timers to reclaim, so no service reaches into windows or controls.
/// </summary>
public interface IMemoryPrunable
{
    /// <summary>参与者名称，只用于诊断日志。/ Participant name, used for diagnostics only.</summary>
    string PruneParticipantName { get; }

    /// <summary>
    /// 按档位回收或恢复正常运行。
    /// Reclaims for the given level, or restores normal operation.
    /// </summary>
    /// <param name="level">目标档位；<see cref="MemoryPruneLevel.None"/> 表示恢复正常运行。/ Target level; <see cref="MemoryPruneLevel.None"/> means back to normal.</param>
    void Prune(MemoryPruneLevel level);
}
