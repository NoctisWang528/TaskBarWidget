using TaskbarLyrics.Core.Models;

namespace TaskbarLyrics.App;

internal static class NonLyricsMediaPolicy
{
    internal static bool IsDouyin(string? source) =>
        string.Equals(source, "electron.app.douyin", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(source, "Douyin", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(source, "douyin.exe", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(source, "抖音", StringComparison.OrdinalIgnoreCase);

    internal static bool IsDouyin(PlaybackSnapshot snapshot) =>
        IsDouyin(snapshot.SourceAppUserModelId) || IsDouyin(snapshot.Track?.SourceApp);

    internal static bool IsNonLyrics(string? source) => IsDouyin(source) ||
        string.Equals(source, "Browser", StringComparison.OrdinalIgnoreCase) ||
        SmtcMusicSessionProvider.IsBrowserSource(source ?? string.Empty);

    internal static bool IsNonLyrics(PlaybackSnapshot snapshot) =>
        IsNonLyrics(snapshot.SourceAppUserModelId) || IsNonLyrics(snapshot.Track?.SourceApp);
}

// Retain only Douyin's short video transitions. The first raw loss owns the deadline;
// retained output must never be fed back as a fresh playing observation.
internal sealed class DouyinPlaybackGate
{
    internal const int GraceMilliseconds = 750;
    private long? _lossStarted;
    private long? _explicitPauseStarted;
    internal PlaybackSnapshot? Current { get; private set; }
    internal string? ControlSourceId => Current is { Track: not null } current && NonLyricsMediaPolicy.IsDouyin(current)
        ? current.SourceAppUserModelId : null;

    internal TSession? FindControlSession<TSession>(IEnumerable<TSession> sessions, Func<TSession, string> sourceId)
        where TSession : class => ControlSourceId is { } id
            ? sessions.FirstOrDefault(session => string.Equals(sourceId(session), id, StringComparison.OrdinalIgnoreCase))
            : null;

    internal PlaybackSnapshot Observe(PlaybackSnapshot raw, long nowMilliseconds)
    {
        // A successful pause command wins over SMTC's briefly stale Playing readback.
        if (_explicitPauseStarted is { } pausedAt && Current is { } paused)
        {
            if (nowMilliseconds - pausedAt < GraceMilliseconds && string.Equals(paused.SourceAppUserModelId,
                raw.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase))
            {
                if (raw.IsPlaying) return paused;
            }
            else _explicitPauseStarted = null;
        }

        if (Current is { Track: not null } current && NonLyricsMediaPolicy.IsDouyin(current) &&
            (!raw.IsPlaying || raw.Track is null) && (current.IsPlaying || raw.Track is null || !string.Equals(current.SourceAppUserModelId,
                raw.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase)))
        {
            _lossStarted ??= nowMilliseconds;
            if (nowMilliseconds - _lossStarted.Value < GraceMilliseconds)
                return current with { CanSeek = raw.CanSeek && string.Equals(current.SourceAppUserModelId,
                    raw.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase) };
        }

        _lossStarted = null;
        Current = raw;
        return raw;
    }

    internal void ConfirmPlayback(bool isPlaying, long nowMilliseconds)
    {
        _lossStarted = null;
        _explicitPauseStarted = isPlaying ? null : nowMilliseconds;
        if (Current is { } current) Current = current with { IsPlaying = isPlaying };
    }

    internal void Reset()
    {
        Current = null;
        _lossStarted = null;
        _explicitPauseStarted = null;
    }
}
