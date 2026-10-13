// 在 UI 线程维护网易云内存快照的短暂失败缓冲；不拥有读取器，进程退出或停用时立即清空。
using AFMediaBar.Classes.Models;

namespace AFMediaBar.Classes.Services.Media.Sources.NetEase;

/// <summary>内存读取临时失败时冻结最多三秒，避免瞬间丢失来源或无限保留旧歌。 / Holds a failed read for at most three seconds without advancing its timeline.</summary>
public sealed class NetEaseMemoryContinuity
{
    private MediaSnapshot? _last;
    private DateTimeOffset? _missingSince;

    /// <summary>接收一次读取结果；进程不存在时不使用缓冲。</summary>
    public MediaSnapshot? Update(MediaSnapshot? snapshot, bool processPresent, DateTimeOffset now)
    {
        if (!processPresent)
        {
            Clear();
            return null;
        }
        if (snapshot is { IsConnected: true })
        {
            _last = snapshot;
            _missingSince = null;
            return snapshot;
        }
        _missingSince ??= now;
        if (_last is null || now - _missingSince.Value >= TimeSpan.FromSeconds(3))
        {
            _last = null;
            return null;
        }
        return _last with { IsPlaying = false, PlaybackRate = 0, IsStale = true };
    }

    /// <summary>隐藏、停止或切换进程后立即丢弃旧来源状态。</summary>
    public void Clear()
    {
        _last = null;
        _missingSince = null;
    }
}
