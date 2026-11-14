using TaskbarLyrics.App;

namespace TaskBarWidgets.Media.Integration;

// Display-only fallbacks must never become artist metadata used by lyric lookup.
internal static class MediaSourcePresentation
{
    internal static string Artist(string? artist, MediaSourceChoice source)
    {
        var value = artist?.Trim();
        // Exact placeholder matches only: real names such as "Unknown Artist
        // Collective" and "Various Artists" must remain untouched.
        return string.IsNullOrEmpty(value) ||
            value.Equals("Unknown Artist", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Unknown Artists", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("<unknown>", StringComparison.OrdinalIgnoreCase) ||
            value is "未知艺术家" or "未知歌手" or "未知作者"
                ? Label(source) : artist!;
    }

    internal static string Label(MediaSourceChoice source)
    {
        var name = source.Name?.Trim() ?? "";
        var id = source.Id?.Trim() ?? "";
        // Preserve an actual application display name. Browser is a policy
        // category, not an application name, and must never hide a session ID.
        if (name.Length > 0 && !name.Equals(id, StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("Browser", StringComparison.OrdinalIgnoreCase) && name != "浏览器")
            return FriendlyName(name);

        var process = ProcessExecutablePathResolver.GetProcessNameCandidate(id);
        return process?.ToLowerInvariant() switch
        {
            "msedge" => "Microsoft Edge",
            "chrome" => "Google Chrome",
            "firefox" => "Firefox",
            "brave" => "Brave",
            "opera" => "Opera",
            "vivaldi" => "Vivaldi",
            "arc" => "Arc",
            "douyin" => "抖音",
            "qqmusic" => "QQ 音乐",
            "cloudmusic" or "netease" => "网易云音乐",
            "kugou" => "酷狗音乐",
            "spotify" => "Spotify",
            _ => process ?? (id.Length > 0 ? FriendlyName(id) : name),
        };
    }

    private static string FriendlyName(string value) => value switch
    {
        "QQMusic" => "QQ 音乐",
        "Netease" => "网易云音乐",
        "Kugou" => "酷狗音乐",
        "Douyin" => "抖音",
        _ => value,
    };
}
