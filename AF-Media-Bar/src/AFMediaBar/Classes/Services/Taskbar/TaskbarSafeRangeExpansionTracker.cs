// Accepts a larger taskbar safe range only after repeated stable observations.
// The taskbar window owns this short-lived state; no UI Automation or timer is owned here.
using AFMediaBar.Classes.Models.Layout;

namespace AFMediaBar.Classes.Services;

/// <summary>Filters transient missing icon groups while allowing a genuinely freed taskbar range to grow. / 过滤短暂漏报图标，同时允许真实空闲区间扩张。</summary>
public sealed class TaskbarSafeRangeExpansionTracker
{
    private static readonly TimeSpan ConfirmationDelay = TimeSpan.FromSeconds(2);
    private TaskbarPrimaryRange? _candidate;
    private DateTime _firstSeenUtc;

    /// <summary>Resets a pending expansion when the host or probe context changes. / 宿主或探测上下文变化时清除候选区间。</summary>
    public void Reset() => _candidate = null;

    /// <summary>Accepts a containing, larger range after it remains identical for the confirmation interval. / 扩大区间连续稳定后才采纳。</summary>
    public bool TryAccept(
        IReadOnlyList<TaskbarPrimaryRange> ranges,
        TaskbarPrimaryRange previous,
        DateTime nowUtc,
        out TaskbarPrimaryRange expanded)
    {
        expanded = default;
        var candidate = ranges.FirstOrDefault(range =>
            range.Start <= previous.Start && range.End >= previous.End && range.Length > previous.Length);
        if (candidate.Length <= 0)
        {
            Reset();
            return false;
        }

        if (_candidate != candidate || nowUtc < _firstSeenUtc)
        {
            _candidate = candidate;
            _firstSeenUtc = nowUtc;
            return false;
        }

        if (nowUtc - _firstSeenUtc < ConfirmationDelay)
            return false;

        expanded = candidate;
        Reset();
        return true;
    }
}
