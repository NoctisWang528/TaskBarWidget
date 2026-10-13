// 统一网易云两条通道的来源身份、候选与快照；纯策略，不读取进程或调用 SMTC。
using AFMediaBar.Classes.Models;
using AFMediaBar.Classes.Abstractions;

namespace AFMediaBar.Classes.Services.Media.Sources.NetEase;

/// <summary>网易云以一个来源承接内存信息和 SMTC 控制。 / One NetEase source combines memory information and SMTC controls.</summary>
public sealed class NetEaseSourcePolicy : IMediaSourcePolicy
{
    /// <summary>沿用已有内存快照的来源标识。</summary>
    public const string SourceId = "cloudmusic";
    /// <summary>跨 SMTC 重建和通道切换保持稳定的选择键。</summary>
    public const string SelectionKey = "source:cloudmusic";

    string IMediaSourcePolicy.SourceId => SourceId;
    string IMediaSourcePolicy.SelectionKey => SelectionKey;

    /// <summary>识别项目已支持的网易云来源标识。</summary>
    public bool Matches(string? sourceId) => sourceId is not null &&
        (sourceId.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase) ||
         sourceId.Contains("netease", StringComparison.OrdinalIgnoreCase) ||
         sourceId.Contains("163music", StringComparison.OrdinalIgnoreCase));

    /// <summary>将既有网易云 SMTC 标识映射到同一来源；不改变其他应用标识。</summary>
    public string NormalizeSourceId(string sourceId) => Matches(sourceId) ? SourceId : sourceId;

    /// <summary>合并网易云候选，保留其他播放器的独立会话。</summary>
    public IReadOnlyList<MediaSourceCandidate> Combine(
        IReadOnlyList<MediaSourceCandidate> sessions, MediaSnapshot? memory)
    {
        var result = sessions.Where(source => !Matches(source.SourceId)).ToList();
        var smtc = sessions.Where(source => Matches(source.SourceId))
            .OrderByDescending(source => source.IsPlaying).FirstOrDefault();
        if (smtc is not null || memory is { IsConnected: true })
        {
            result.Add(new(SelectionKey, SourceId,
                memory is { IsConnected: true, IsStale: false } ? memory.IsPlaying : smtc?.IsPlaying ?? memory!.IsPlaying,
                smtc?.SessionKey, smtc is null && memory is { IsStale: true }));
        }
        return result;
    }

    /// <summary>内存数据优先；仅从同来源 SMTC 补充控制及同曲目信息。</summary>
    public MediaSnapshot Merge(MediaSnapshot? memory, MediaSnapshot smtc)
    {
        var baseline = smtc.IsConnected && Matches(smtc.SourceId) ? smtc : MediaSnapshot.Disconnected;
        if (memory is not { IsConnected: true } || (memory.IsStale && baseline.IsConnected))
            return baseline.IsConnected ? baseline with { SourceId = SourceId } : MediaSnapshot.Disconnected;

        var sameTrack = baseline.IsConnected &&
            !string.IsNullOrWhiteSpace(memory.Title) &&
            string.Equals(memory.Title.Trim(), baseline.Title.Trim(), StringComparison.OrdinalIgnoreCase) &&
            ArtistsDescribeTheSameTrack(memory.Artist, baseline.Artist);
        return memory with
        {
            SourceId = SourceId,
            Artwork = sameTrack ? baseline.Artwork : null,
            Lyrics = memory.Lyrics ?? (sameTrack ? baseline.Lyrics : null),
            CanPlayPause = baseline.CanPlayPause,
            CanSkipPrevious = baseline.CanSkipPrevious,
            CanSkipNext = baseline.CanSkipNext,
            CanSeek = baseline.CanSeek && memory.Duration > 0,
            CanChangeRepeat = baseline.CanChangeRepeat,
            RepeatMode = baseline.RepeatMode
        };
    }

    private static bool ArtistsDescribeTheSameTrack(string memoryArtist, string smtcArtist)
    {
        if (string.IsNullOrWhiteSpace(memoryArtist) || string.IsNullOrWhiteSpace(smtcArtist))
            return false;

        var artists = memoryArtist.Trim();
        var reported = smtcArtist.Trim();
        if (string.Equals(artists, reported, StringComparison.OrdinalIgnoreCase))
            return true;

        // Client metadata joins artists with commas, while SMTC can report only the primary artist.
        // Accept that first artist only; a guest or a name substring cannot identify the track by itself.
        var separator = artists.IndexOf(',');
        return separator > 0 && string.Equals(artists[..separator].Trim(), reported, StringComparison.OrdinalIgnoreCase);
    }
}
