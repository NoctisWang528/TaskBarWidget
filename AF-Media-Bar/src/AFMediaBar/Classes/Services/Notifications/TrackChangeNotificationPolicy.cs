using AFMediaBar.Classes.Models;

namespace AFMediaBar.Classes.Services;

/// <summary>
/// 从媒体快照识别可观察的曲目切换，并抑制首次快照和非曲目更新。
/// Detects observable track changes while suppressing the first snapshot and non-track updates.
/// </summary>
public sealed class TrackChangeNotificationPolicy
{
    private string? _currentTrackKey;
    private string? _pendingTrackKey;
    private bool _hasBaseline;

    /// <summary>应用一个快照，并在新曲目首次进入播放状态时返回 true。 / Applies a snapshot and returns true when a new track first enters playing state.</summary>
    public bool ShouldNotify(MediaSnapshot snapshot)
    {
        var trackKey = CreateIdentity(snapshot);
        if (trackKey is null)
            return false;

        if (!_hasBaseline)
        {
            _hasBaseline = true;
            _currentTrackKey = trackKey;
            return false;
        }

        if (!string.Equals(_currentTrackKey, trackKey, StringComparison.Ordinal))
        {
            _currentTrackKey = trackKey;
            _pendingTrackKey = trackKey;
        }

        if (!snapshot.IsPlaying || !string.Equals(_pendingTrackKey, trackKey, StringComparison.Ordinal))
            return false;

        _pendingTrackKey = null;
        return true;
    }

    /// <summary>创建 SMTC 可观察的曲目身份；无有效标题时返回 null。 / Creates the SMTC-observable track identity, or null when no valid title exists.</summary>
    public static string? CreateIdentity(MediaSnapshot snapshot) =>
        snapshot.IsConnected && !string.IsNullOrWhiteSpace(snapshot.Title)
            ? $"{snapshot.SourceId.Trim().ToUpperInvariant()}\u001F{snapshot.Title.Trim().ToUpperInvariant()}"
            : null;
}
