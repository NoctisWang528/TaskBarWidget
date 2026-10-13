using AFMediaBar.Classes.Services.Media.Smtc;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using AFMediaBar.Classes.Abstractions;
using AFMediaBar.Classes.Models;

namespace AFMediaBar.Classes.Services;

/// <summary>
/// 维护当前媒体来源选择、自动跟随和浏览器会话重建缓冲。
/// Maintains the selected source, auto-follow behavior, and the browser session recreation grace period.
/// </summary>
public sealed class MediaSessionSelectionService : IDisposable, IMemoryPrunable
{
    private static readonly TimeSpan AutoSwitchGracePeriod = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MissingSessionGracePeriod = TimeSpan.FromSeconds(3);

    /// <summary>常规刷新周期。计时器只在自动切换或会话重建的宽限期内运行，因此这是"正在挣扎着切源"时的节奏。
    /// The ordinary refresh period. The timer only runs inside the auto-switch or session-recreation grace period, so this is the cadence while a
    /// switch is being resolved.</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>剪枝后的刷新周期：宽限期是按时间戳判定的，放慢只是让恢复得晚一点，不会漏掉切换。
    /// The refresh period while pruned: the grace periods are decided from timestamps, so slowing down only delays the resolution instead of losing it.</summary>
    private static readonly TimeSpan PrunedRefreshInterval = TimeSpan.FromSeconds(1);
    private readonly Func<string?> _getFocusedSessionKey;
    private readonly Func<DateTime> _utcNow;
    private readonly DispatcherTimer _timer;
    private string? _pendingAutoSwitchKey;
    private DateTime _pendingAutoSwitchSinceUtc;
    private DateTime _missingSessionSinceUtc;
    private bool _isDisposed;

    public string? SelectedKey { get; private set; }
    public string? SelectedSourceId { get; private set; }

    /// <summary>
    /// 当前选择是否来自用户的明确动作（源菜单），而不是自动跟随挑的。
    ///
    /// 自动跟随必须尊重它：此前它不认识这个区别，于是用户手动选的来源会在约一秒后被"有别的来源在播"抢回去，
    /// 而从浏览器切走之后又永远回不来——同一个根因的两个表现。会话消失或用户再选一次时它会回到 false。
    /// Whether the current selection came from an explicit user action, the source menu, rather than from auto-follow.
    ///
    /// Auto-follow has to respect it: it used to have no idea about the difference, so a source the user picked by hand was taken back about a second later by
    /// "another source is playing", while switching away from a browser could never be undone — two faces of one cause. It returns to false when that session
    /// disappears or the user picks again.
    /// </summary>
    public bool IsManualSelection { get; private set; }

    public event Action? RefreshRequested;

    /// <summary>
    /// 创建会话选择状态机，并使用共享目录提供焦点会话与刷新信号。
    /// Creates the session-selection state machine using the shared catalog for focused sessions and refresh signals.
    /// </summary>
    public MediaSessionSelectionService(MediaSessionCatalog catalog)
        : this(() => catalog.GetFocusedSession()?.Id, Application.Current.Dispatcher)
    {
    }

    internal MediaSessionSelectionService(Func<string?> getFocusedSessionKey, Dispatcher dispatcher)
        : this(getFocusedSessionKey, dispatcher, () => DateTime.UtcNow)
    {
    }

    internal MediaSessionSelectionService(Func<string?> getFocusedSessionKey, Dispatcher dispatcher, Func<DateTime> utcNow)
    {
        _getFocusedSessionKey = getFocusedSessionKey;
        _utcNow = utcNow;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = RefreshInterval
        };
        _timer.Tick += OnTimerTick;
    }

    /// <summary>
    /// 在给定稳定会话集合中建立手动选择；键不存在时不改变当前状态。
    /// Establishes a manual selection within the supplied stable session set without changing state for an unknown key.
    /// </summary>
    public bool Select(string key, IReadOnlyList<MediaSourceCandidate> sessions)
    {
        var selected = sessions.FirstOrDefault(session =>
            string.Equals(session.Key, key, StringComparison.Ordinal));
        if (selected is null)
        {
            return false;
        }

        ClearPendingAutoSwitch();
        ClearMissingSession();
        SelectedKey = selected.Key;
        SelectedSourceId = selected.SourceId;
        IsManualSelection = true;
        return true;
    }

    /// <summary>
    /// 按手动选择、宽限期和系统焦点优先级解析当前会话，并且只返回仍可读取的会话；全部会话已关闭时返回空值。
    /// Resolves the current session using manual selection, grace-period, and system-focus precedence, returning only a
    /// still-readable session and null when every session has been closed.
    /// </summary>
    public MediaSourceCandidate? Resolve(IReadOnlyList<MediaSourceCandidate> sessions)
    {
        var selected = sessions.FirstOrDefault(session =>
            string.Equals(session.Key, SelectedKey, StringComparison.Ordinal));
        if (selected is not null)
        {
            if (_missingSessionSinceUtc != default)
            {
                Debug.WriteLine($"[MediaSessionSelection] Browser source recovered: {SelectedSourceId}");
            }

            SelectedSourceId = selected.SourceId;
            ClearMissingSession();
            return selected;
        }

        var restored = FindRestoredSession(sessions);
        if (restored is not null)
        {
            SelectedKey = restored.Key;
            SelectedSourceId = restored.SourceId;
            Debug.WriteLine($"[MediaSessionSelection] Browser source recreated: {SelectedSourceId}");
            ClearMissingSession();
            return restored;
        }

        if (TryHoldMissingSession())
        {
            return null;
        }

        ClearPendingAutoSwitch();
        ClearMissingSession();
        var focused = _getFocusedSessionKey();
        selected = focused is not null
            ? sessions.FirstOrDefault(session =>
                string.Equals(session.SessionKey, focused, StringComparison.Ordinal))
            : null;
        selected ??= sessions.FirstOrDefault(session => session.IsPlaying);
        selected ??= sessions.FirstOrDefault();
        if (selected is null)
        {
            SelectedKey = null;
            SelectedSourceId = null;
            IsManualSelection = false;
            return null;
        }

        SelectedKey = selected.Key;
        SelectedSourceId = selected.SourceId;

        // 走到这里说明当前选择已经不存在，这一份是**自动**挑的，因此自动跟随可以继续接管它。
        // Reaching this point means the previous selection no longer exists and this one was picked **automatically**, so auto-follow may keep managing it.
        IsManualSelection = false;
        return selected;
    }

    /// <summary>
    /// 在自动跟随模式下尝试切换到正在播放的会话，并报告选择是否变化。
    ///
    /// 判定本身在 <see cref="MediaAutoSwitchPolicy"/> 里（可单元测试），这里只负责把状态映射成信号、并按判定结果启动计时器或改选择。
    /// Attempts to follow a playing session in automatic mode and reports whether the selection changed.
    ///
    /// The decision itself lives in <see cref="MediaAutoSwitchPolicy"/> so that it can be unit-tested; this method only maps state into signals and then starts
    /// the timer or changes the selection according to the decision.
    /// </summary>
    public bool TryAutoSwitchToPlaying(IReadOnlyList<MediaSourceCandidate> sessions)
    {
        var current = sessions.FirstOrDefault(session =>
            string.Equals(session.Key, SelectedKey, StringComparison.Ordinal));

        if (current is { IsRecovering: true })
        {
            ClearPendingAutoSwitch();
            return false;
        }

        var replacement = current is null
            ? null
            : sessions.FirstOrDefault(candidate =>
                !candidate.IsRecovering && candidate.Key != current.Key && candidate.IsPlaying);

        var decision = MediaAutoSwitchPolicy.Resolve(new MediaAutoSwitchSignals(
            CurrentExists: current is not null,
            CurrentIsPlaying: current is not null && current.IsPlaying,
            CurrentIsBrowserSource: current is not null && IsBrowserSource(current.SourceId),
            IsManualSelection: IsManualSelection,
            HasPlayingReplacement: replacement is not null,
            ReplacementMatchesPending: replacement is not null &&
                                       string.Equals(_pendingAutoSwitchKey, replacement.Key, StringComparison.Ordinal),
            SincePending: _utcNow() - _pendingAutoSwitchSinceUtc,
            GracePeriod: AutoSwitchGracePeriod));

        switch (decision)
        {
            case MediaAutoSwitchDecision.WaitForGrace:
                if (!string.Equals(_pendingAutoSwitchKey, replacement!.Key, StringComparison.Ordinal))
                {
                    _pendingAutoSwitchKey = replacement.Key;
                    _pendingAutoSwitchSinceUtc = _utcNow();
                }

                _timer.Start();
                return false;

            case MediaAutoSwitchDecision.Switch:
                ClearPendingAutoSwitch();
                SelectedKey = replacement!.Key;
                SelectedSourceId = replacement.SourceId;

                // 切换之后这一份是自动挑的：用户没有参与，自动跟随继续对它负责。
                // After a switch this selection is automatic: the user took no part in it, so auto-follow keeps managing it.
                IsManualSelection = false;
                return true;

            default:
                // 当前会话不存在时，仍要维持"会话临时缺失"的宽限：浏览器会话重建期间媒体栏不该跳到别的来源。
                // While the current session is missing the missing-session grace still has to be maintained: the bar must not jump to another source while a
                // browser session is being recreated.
                if (current is null && IsMissingSessionGraceActive)
                {
                    _timer.Start();
                }
                else
                {
                    ClearPendingAutoSwitch();
                }

                return false;
        }
    }

    public bool IsMissingSessionGraceActive =>
        _missingSessionSinceUtc != default &&
        _utcNow() - _missingSessionSinceUtc < MissingSessionGracePeriod &&
        IsBrowserSource(SelectedSourceId ?? string.Empty);

    /// <summary>
    /// 在浏览器会话暂时消失时启动或维持恢复缓冲。
    /// Starts or maintains the recovery grace period while a browser session is temporarily missing.
    /// </summary>
    public bool TryHoldMissingSession()
    {
        if (!IsBrowserSource(SelectedSourceId ?? string.Empty))
        {
            return false;
        }

        if (_missingSessionSinceUtc == default)
        {
            _missingSessionSinceUtc = _utcNow();
            Debug.WriteLine($"[MediaSessionSelection] Holding missing browser source: {SelectedSourceId}");
        }

        if (_utcNow() - _missingSessionSinceUtc >= MissingSessionGracePeriod)
        {
            Debug.WriteLine($"[MediaSessionSelection] Missing browser source grace expired: {SelectedSourceId}");
            return false;
        }

        _timer.Start();
        return true;
    }

    /// <summary>
    /// 清除手动选择和相关宽限状态，使后续解析恢复自动跟随。
    /// Clears manual selection and its grace state so subsequent resolution returns to automatic following.
    /// </summary>
    public void ClearSelection()
    {
        SelectedKey = null;
        SelectedSourceId = null;
        IsManualSelection = false;
        ClearPendingAutoSwitch();
        ClearMissingSession();
    }

    /// <summary>参与者名称，只用于诊断。/ Participant name, used for diagnostics only.</summary>
    public string PruneParticipantName => "session-selection";

    /// <summary>
    /// 放慢或恢复刷新周期。这个计时器本身只在自己的宽限期内运行（平时是停的），因此剪枝对它做的只是"每秒看一次"而不是"每秒看五次"：
    /// 它的状态机完全按时间戳判定，放慢只影响恰好卡在宽限期里的那一次切换早几百毫秒还是晚几百毫秒被发现。
    /// Slows down or restores the refresh period. This timer only runs inside its own grace period — it is stopped the rest of the time — so pruning
    /// only changes five looks per second into one, and since the state machine decides everything from timestamps, the only effect is that a switch
    /// caught mid-grace-period is noticed a few hundred milliseconds later.
    /// </summary>
    /// <param name="level">目标档位。/ The target level.</param>
    public void Prune(MemoryPruneLevel level)
    {
        if (_isDisposed)
        {
            return;
        }

        var interval = level == MemoryPruneLevel.None ? RefreshInterval : PrunedRefreshInterval;
        if (_timer.Interval != interval)
        {
            _timer.Interval = interval;
        }
    }

    /// <summary>
    /// 停止选择计时器并阻止后续刷新请求。
    /// Stops selection timers and prevents subsequent refresh requests.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
    }

    private MediaSourceCandidate? FindRestoredSession(IReadOnlyList<MediaSourceCandidate> sessions) =>
        IsMissingSessionGraceActive
            ? sessions.FirstOrDefault(session => IsSameBrowserSource(
                session.SourceId,
                SelectedSourceId ?? string.Empty))
            : null;

    private void ClearPendingAutoSwitch()
    {
        _pendingAutoSwitchKey = null;
        _pendingAutoSwitchSinceUtc = default;
        if (!IsMissingSessionGraceActive)
        {
            _timer.Stop();
        }
    }

    private void ClearMissingSession()
    {
        _missingSessionSinceUtc = default;
        if (string.IsNullOrEmpty(_pendingAutoSwitchKey))
        {
            _timer.Stop();
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_isDisposed)
        {
            _timer.Stop();
            return;
        }

        RefreshRequested?.Invoke();
    }

    private static bool IsBrowserSource(string sourceId) =>
        sourceId.Contains("chrome", StringComparison.OrdinalIgnoreCase) ||
        sourceId.Contains("msedge", StringComparison.OrdinalIgnoreCase) ||
        sourceId.Contains("microsoftedge", StringComparison.OrdinalIgnoreCase) ||
        sourceId.Contains("firefox", StringComparison.OrdinalIgnoreCase);

    private static bool IsSameBrowserSource(string leftSourceId, string rightSourceId)
    {
        var leftFamily = GetBrowserFamily(leftSourceId);
        var rightFamily = GetBrowserFamily(rightSourceId);
        return leftFamily is not null && string.Equals(leftFamily, rightFamily, StringComparison.Ordinal);
    }

    private static string? GetBrowserFamily(string sourceId)
    {
        if (sourceId.Contains("chrome", StringComparison.OrdinalIgnoreCase))
        {
            return "chrome";
        }

        if (sourceId.Contains("msedge", StringComparison.OrdinalIgnoreCase) ||
            sourceId.Contains("microsoftedge", StringComparison.OrdinalIgnoreCase))
        {
            return "edge";
        }

        return sourceId.Contains("firefox", StringComparison.OrdinalIgnoreCase)
            ? "firefox"
            : null;
    }
}
