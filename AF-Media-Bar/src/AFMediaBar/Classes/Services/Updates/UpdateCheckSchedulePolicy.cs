using AFMediaBar.Classes.Settings;

namespace AFMediaBar.Classes.Services.Updates;

/// <summary>
/// 自动检查更新的排期策略。
///
/// "每天最多一次"是给用户和清单服务器的承诺，因此判据完全来自设置里记录的上次检查时间，而不是进程内的计时器：
/// 反复重启程序不会让程序一天检查十几次。失败按更短的间隔重试，否则一次开机时的断网会让用户整天看不到更新。
/// Scheduling policy for automatic update checks.
///
/// "At most once a day" is a promise to the user and to the manifest host, so the criterion comes entirely from the
/// recorded time of the last check instead of an in-process timer: restarting the application repeatedly cannot
/// make it check a dozen times a day. Failures retry sooner, because one offline start-up should not hide updates
/// for a whole day.
/// </summary>
public static class UpdateCheckSchedulePolicy
{
    /// <summary>启动后首次自动检查的延迟：让媒体会话、任务栏和主题先完成初始化。/ Delay before the first automatic check, so media, taskbar and theme finish initializing first.</summary>
    public static TimeSpan InitialDelay { get; } = TimeSpan.FromSeconds(20);

    /// <summary>上次检查成功后的重试间隔。/ Interval after a successful check.</summary>
    public static TimeSpan SuccessInterval { get; } = TimeSpan.FromHours(24);

    /// <summary>上次检查失败后的重试间隔。/ Interval after a failed check.</summary>
    public static TimeSpan FailureRetryInterval { get; } = TimeSpan.FromHours(1);

    /// <summary>
    /// 现在是否应当自动检查。手动检查不受该策略约束。
    /// Whether an automatic check is due now. A manual check is never constrained by this policy.
    /// </summary>
    /// <param name="settings">更新设置。/ Update settings.</param>
    /// <param name="now">当前时间。/ Current time.</param>
    public static bool ShouldCheck(UpdateSettings settings, DateTimeOffset now)
    {
        if (!settings.AutoCheckEnabled)
        {
            return false;
        }

        if (settings.LastCheckUtc is not { } lastCheck)
        {
            return true;
        }

        // 系统时间被回拨时上次检查"位于未来"，此时直接放行：它造成的重复检查会被新的时间戳立刻纠正，
        // 而拒绝检查会让更新在时间同步之后长期停摆。
        // When the clock moves backwards the previous check lies in the future; letting it through is corrected by
        // the new timestamp immediately, whereas refusing would stall updates long after the clock is fixed.
        if (now < lastCheck)
        {
            return true;
        }

        var interval = settings.LastCheckSucceeded == false ? FailureRetryInterval : SuccessInterval;
        return now - lastCheck >= interval;
    }

    /// <summary>
    /// 下一次自动检查的最早时间；关掉自动检查时为 null。
    /// Earliest time of the next automatic check, or null when automatic checking is disabled.
    /// </summary>
    /// <param name="settings">更新设置。/ Update settings.</param>
    /// <param name="now">当前时间。/ Current time.</param>
    public static DateTimeOffset? ResolveNextCheckUtc(UpdateSettings settings, DateTimeOffset now)
    {
        if (!settings.AutoCheckEnabled)
        {
            return null;
        }

        if (settings.LastCheckUtc is not { } lastCheck || now < lastCheck)
        {
            return now + InitialDelay;
        }

        var interval = settings.LastCheckSucceeded == false ? FailureRetryInterval : SuccessInterval;
        return lastCheck + interval;
    }

    /// <summary>
    /// 用户是否已跳过该版本。比较按版本语义进行，因此 <c>1.2.0</c> 与 <c>v1.2</c> 被视为同一个版本，
    /// 而无法解析的遗留文本永远不匹配。
    /// Whether the user already skipped this version. The comparison is semantic, so <c>1.2.0</c> and <c>v1.2</c>
    /// count as the same version while unparseable leftovers never match.
    /// </summary>
    /// <param name="settings">更新设置。/ Update settings.</param>
    /// <param name="manifestVersion">清单声明的版本。/ Version declared by the manifest.</param>
    public static bool IsSkipped(UpdateSettings settings, string? manifestVersion) =>
        settings.SkippedVersion is not null &&
        UpdateVersionPolicy.IsSameVersion(settings.SkippedVersion, manifestVersion);
}
