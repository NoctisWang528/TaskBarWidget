using AFMediaBar.Classes.Abstractions;
using AFMediaBar.Classes.Models;
using Lyricify.Lyrics.Providers.Web.Netease;

namespace AFMediaBar.Classes.Services.Lyrics;

/// <summary>
/// 网易云歌词源（来源专用）：播放器本身就是网易云时，用歌曲 id 精确取词。
/// The NetEase source provider for the NetEase player itself: it retrieves exactly by song id.
///
/// QQ 未命中后参与并发备用阶段；没有歌曲 id 时不发请求。
/// Participates in parallel fallbacks after a QQ miss, and makes no request without a song id.
/// </summary>
public sealed class NetEaseLyricsProvider : ILyricsProvider
{
    private readonly Api _api = new();

    public string SourceName => LyricsSourceCatalog.NetEase;

    public async Task<LyricsResult?> GetLyricsAsync(
        LyricsRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.NetEaseSongId))
        {
            return null;
        }

        var result = await NetEaseLyricFetcher.FetchAndBuildAsync(
            _api,
            request.NetEaseSongId,
            SourceName,
            request,
            cancellationToken);
        return result is null ? null : result with { MatchScore = 100 };
    }
}
