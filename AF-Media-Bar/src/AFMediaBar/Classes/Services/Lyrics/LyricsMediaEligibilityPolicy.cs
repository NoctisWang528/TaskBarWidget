namespace AFMediaBar.Classes.Services.Lyrics;

/// <summary>
/// 根据 SMTC 来源及明确的视频类型决定是否自动取词；只影响取词，不影响媒体快照或播放控制。
/// Decides whether to fetch lyrics from an SMTC source without affecting its media snapshot or playback controls.
/// </summary>
public static class LyricsMediaEligibilityPolicy
{
    private static readonly string[] BrowserMarkers =
    [
        "chrome", "msedge", "microsoftedge", "firefox", "brave", "opera", "vivaldi",
        "chromium", "arc.exe", "thebrowser", "qqbrowser", "360se", "sogouexplorer",
        "ucbrowser", "maxthon", "liebao"
    ];

    private static readonly string[] VideoAppMarkers =
    [
        "bilibili", "哔哩哔哩", "嗶哩嗶哩", "douyin", "抖音", "tiktok", "youtube",
        "iqiyi", "爱奇艺", "愛奇藝", "qqlive", "腾讯视频", "騰訊視頻", "youku", "优酷", "優酷",
        "mangotv", "mgtv", "netflix", "potplayer", "vlc", "mpv.exe", "mpc-hc", "mpc-be"
    ];

    public static bool ShouldFetch(string? sourceId, bool isVideo, bool allowBrowserAndVideoLyrics)
    {
        if (allowBrowserAndVideoLyrics)
        {
            return true;
        }

        if (isVideo)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return true;
        }

        return !BrowserMarkers.Any(marker => sourceId.Contains(marker, StringComparison.OrdinalIgnoreCase)) &&
               !VideoAppMarkers.Any(marker => sourceId.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
