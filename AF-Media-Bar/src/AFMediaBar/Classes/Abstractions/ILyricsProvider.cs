using AFMediaBar.Classes.Models;

namespace AFMediaBar.Classes.Abstractions;

/// <summary>
/// 单个歌词源：有歌词或确认无歌词均返回有效结果，未匹配及请求/解析失败返回 null。
/// A lyric source returns lyrics or confirmed absence; unmatched or failed lookups return null.
/// </summary>
public interface ILyricsProvider
{
    string SourceName { get; }

    /// <summary>
    /// 异步获取歌词结果。
    /// Asynchronously retrieves a lyric result.
    /// </summary>
    /// <param name="request">歌词查询请求 / Lyric query request.</param>
    /// <param name="cancellationToken">取消令牌 / Cancellation token.</param>
    /// <returns>有歌词或确认无歌词的结果；失败为 null。</returns>
    Task<LyricsResult?> GetLyricsAsync(
        LyricsRequest request,
        CancellationToken cancellationToken);
}
