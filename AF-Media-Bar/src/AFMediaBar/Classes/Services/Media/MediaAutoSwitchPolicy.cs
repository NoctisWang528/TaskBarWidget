namespace AFMediaBar.Classes.Services;

/// <summary>
/// 自动跟随的判定结果。
/// The decision of the auto-follow check.
/// </summary>
public enum MediaAutoSwitchDecision
{
    /// <summary>什么都不做（不切换，也不排队）。/ Do nothing: neither switch nor start waiting.</summary>
    Stay = 0,

    /// <summary>开始或继续等待宽限期，之后再看一次。/ Start or continue waiting for the grace period and look again afterwards.</summary>
    WaitForGrace = 1,

    /// <summary>现在切换到这个正在播放的会话。/ Switch to the playing session now.</summary>
    Switch = 2
}

/// <summary>
/// 自动跟随的输入信号。
/// The signals the auto-follow decision is made from.
/// </summary>
/// <param name="CurrentExists">当前选中的会话是否仍然存在。/ Whether the currently selected session still exists.</param>
/// <param name="CurrentIsPlaying">当前选中的会话是否正在播放。/ Whether the currently selected session is playing.</param>
/// <param name="CurrentIsBrowserSource">当前选中的来源是否属于浏览器。/ Whether the selected source belongs to a browser.</param>
/// <param name="IsManualSelection">当前选择是否来自用户的明确动作（源菜单）。/ Whether the current selection came from an explicit user action, the source menu.</param>
/// <param name="HasPlayingReplacement">是否存在另一个正在播放的会话。/ Whether another session is playing.</param>
/// <param name="ReplacementMatchesPending">这次看到的候选是否就是正在等待的那一个。/ Whether the candidate seen now is the one already being waited for.</param>
/// <param name="SincePending">从开始等待算起的时长。/ How long the wait has been running.</param>
/// <param name="GracePeriod">宽限期。/ The grace period.</param>
public readonly record struct MediaAutoSwitchSignals(
    bool CurrentExists,
    bool CurrentIsPlaying,
    bool CurrentIsBrowserSource,
    bool IsManualSelection,
    bool HasPlayingReplacement,
    bool ReplacementMatchesPending,
    TimeSpan SincePending,
    TimeSpan GracePeriod);

/// <summary>
/// 自动跟随（"有别的来源在播就切过去"）的判定。
///
/// 判定集中在这里，是因为它此前散落在服务里、并且**不对称**：用户手动选中的来源会被自动跟随在 1 秒后抢回去，
/// 而从浏览器切走之后却永远不会被抢回来。根因是同一个——判定不知道"当前这个选择是用户自己做的"。
/// 现在手动选择本身就是一条终止条件：用户选了什么，就一直是什么，直到那个会话消失或用户再选一次；
/// 浏览器豁免则只对**自动**选择生效（它原本就是为了在没人指定来源时跟随网页播放器）。
/// The auto-follow decision: "another source is playing, so switch to it".
///
/// It lives here because it used to be spread through the service and was **asymmetric**: a source the user had picked by hand was taken back by auto-follow
/// about a second later, while switching away from a browser was never taken back. Both came from one cause — the decision did not know that the current
/// selection was the user's own. A manual selection is now a terminating condition in its own right: whatever the user picked stays picked until that
/// session disappears or the user picks again, while the browser exemption applies only to **automatic** selections, which is what it was for: following a
/// web player while nobody has named a source.
/// </summary>
public static class MediaAutoSwitchPolicy
{
    /// <summary>
    /// 按信号给出这一次的判定。
    /// Resolves this round's decision from the signals.
    /// </summary>
    /// <param name="signals">输入信号。/ The input signals.</param>
    /// <returns>判定结果。/ The decision.</returns>
    public static MediaAutoSwitchDecision Resolve(MediaAutoSwitchSignals signals)
    {
        // 当前会话已经不存在：交给上层处理（会话临时缺失宽限、或重新解析）。
        // The current session is gone: the caller handles it, either through the missing-session grace or by resolving again.
        if (!signals.CurrentExists)
        {
            return MediaAutoSwitchDecision.Stay;
        }

        // 用户明确选中的来源不会被自动跟随抢走：这是"切换后不到一秒又跳回去"的直接原因。
        // A source the user picked explicitly is never taken back by auto-follow, which is exactly what made the bar jump back within a second.
        if (signals.IsManualSelection)
        {
            return MediaAutoSwitchDecision.Stay;
        }

        // 当前来源自己在播，没有理由切走。
        // The selected source is playing, so there is no reason to move.
        if (signals.CurrentIsPlaying)
        {
            return MediaAutoSwitchDecision.Stay;
        }

        // 浏览器来源豁免：Web 播放器的会话会随页面重建，切走会让媒体栏在两个来源之间来回跳。
        // 这条规则只对自动选择生效（手动选择在前面已经返回），因此它不再造成"切得过去、切不回来"的不对称。
        // Browser sources are exempt: a web player's session is recreated with its page, and switching away would make the bar jump back and forth between
        // two sources. The rule only applies to automatic selections, since a manual one has already returned above, so it no longer causes the
        // "you can switch to it but never back" asymmetry.
        if (signals.CurrentIsBrowserSource)
        {
            return MediaAutoSwitchDecision.Stay;
        }

        if (!signals.HasPlayingReplacement)
        {
            return MediaAutoSwitchDecision.Stay;
        }

        if (!signals.ReplacementMatchesPending || signals.SincePending < signals.GracePeriod)
        {
            return MediaAutoSwitchDecision.WaitForGrace;
        }

        return MediaAutoSwitchDecision.Switch;
    }
}
