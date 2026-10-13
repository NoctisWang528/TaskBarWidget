// Defines the retry window for inconclusive lyric retrieval; confirmed results keep the normal LRU lifetime.
using AFMediaBar.Classes.Models;

namespace AFMediaBar.Classes.Services.Lyrics;

/// <summary>区分暂时未取得结果与已确认的歌词结果，避免把网络失败永久缓存。</summary>
internal static class LyricsCacheRetentionPolicy
{
    internal static TimeSpan? Lifetime(LyricsResult? result) =>
        result is null ? TimeSpan.FromSeconds(30) : null;
}
