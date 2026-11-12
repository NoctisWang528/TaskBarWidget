using TaskbarLyrics.App;

namespace TaskBarWidgets.Media.Integration;

// Display-only fallbacks must never become artist metadata used by lyric lookup.
internal static class MediaSourcePresentation
{
    internal static string Artist(string? artist, MediaSourceChoice source) =>
        string.IsNullOrWhiteSpace(artist) ? Label(source) : artist;

    internal static string Label(MediaSourceChoice source)
    {
        var process = ProcessExecutablePathResolver.GetProcessNameCandidate(source.Id)?.ToLowerInvariant();
        return process switch
        {
            "msedge" => "Microsoft Edge",
            "chrome" => "Google Chrome",
            "firefox" => "Firefox",
            "brave" => "Brave",
            "opera" => "Opera",
            "vivaldi" => "Vivaldi",
            "arc" => "Arc",
            "douyin" => "抖音",
            _ => source.Name switch
            {
                "QQMusic" => "QQ 音乐",
                "Netease" => "网易云音乐",
                "Kugou" => "酷狗音乐",
                "Douyin" => "抖音",
                "Browser" => "浏览器",
                _ => source.Name == source.Id && process is not null ? process : source.Name,
            },
        };
    }
}
