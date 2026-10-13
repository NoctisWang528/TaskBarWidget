namespace AFMediaBar.Classes.Services.Credits;

/// <summary>
/// 名单的呈现规则：把名字连成一段文本、给出头像地址的尺寸提示。
/// Presentation rules for the name lists: joining names into one paragraph and adding the size hint to an avatar address.
///
/// 赞助者可能有几十位，因此界面不为每人画一张卡片（那会长到无法阅读），而是像对照项目那样把名字连成一段可换行的文本。
/// 连接与去重的规则留在这里，是因为"名单怎么排版"属于界面策略，而它必须能被单元测试钉住。
/// There can be dozens of sponsors, so the interface does not draw a card per person — that would grow past readability — and instead joins the names into
/// one wrapping paragraph, the way the reference project does. The joining and de-duplication rules live here because "how a list is laid out" is an
/// interface policy, and it has to be pinnable by unit tests.
/// </summary>
public static class CreditsPresentationPolicy
{
    /// <summary>
    /// 头像请求的像素边长。界面只显示 32 DIP，取两倍边长即可覆盖高 DPI，同时把下载量控制在几十 KB：
    /// 名单里可能同时有几十张头像，按原始尺寸下载会白白多花几倍的流量与内存。
    /// The pixel edge length requested for avatars. The interface shows 32 DIP, so twice that covers high DPI while keeping a download in the tens of
    /// kilobytes: a list can hold dozens of avatars at once, and fetching them at full size would cost several times the traffic and memory for nothing.
    /// </summary>
    public const int AvatarPixelSize = 64;

    /// <summary>
    /// 把名字连成一段文本。
    ///
    /// 空白项会被丢掉（名单文件里可能有人占位），重复项按不区分大小写去重：同一个名字出现两次只说明名单写重了，
    /// 而界面上重复显示只会让人觉得名单不可信。
    /// Joins names into one paragraph.
    ///
    /// Blank entries are dropped — a list file can contain placeholders — and duplicates are removed case-insensitively: a name appearing twice only means
    /// the list was written twice, and showing it twice on the interface only makes the list look untrustworthy.
    /// </summary>
    /// <param name="names">名字。/ The names.</param>
    /// <param name="separator">连接符（按当前语言给出）。/ The separator, taken from the active language.</param>
    /// <returns>连接后的文本；没有名字时为空字符串。/ The joined text, or an empty string when there are no names.</returns>
    public static string BuildNameList(IEnumerable<string?> names, string separator)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        foreach (var name in names)
        {
            var trimmed = name?.Trim();
            if (string.IsNullOrEmpty(trimmed) || !seen.Add(trimmed))
            {
                continue;
            }

            ordered.Add(trimmed);
        }

        return string.Join(separator, ordered);
    }

    /// <summary>
    /// 给头像地址加上尺寸提示。
    ///
    /// 只在主机是 GitHub 头像域时添加：其它地址可能对未知查询参数报错，而"头像取不到"不该由一个可有可无的优化引起。
    /// Adds the size hint to an avatar address.
    ///
    /// It is only added for GitHub avatar hosts: other addresses may reject an unknown query parameter, and "the avatar cannot be fetched" must never be
    /// caused by an optional optimisation.
    /// </summary>
    /// <param name="url">原始地址。/ The original address.</param>
    /// <returns>用于下载的地址；不可用时返回原地址或空字符串。/ The address to download from, the original one when the hint does not apply, or an empty string.</returns>
    public static string ResolveAvatarRequestUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return string.Empty;
        }

        if (!uri.Host.EndsWith("githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            return url.Trim();
        }

        if (uri.Query.Contains("s=", StringComparison.OrdinalIgnoreCase))
        {
            return url.Trim();
        }

        var separator = string.IsNullOrEmpty(uri.Query) ? "?" : "&";
        return $"{url.Trim()}{separator}s={AvatarPixelSize}";
    }

    /// <summary>
    /// 头像/名单的磁盘缓存文件名：用地址的 SHA-256 而不是地址本身，避开了非法字符与长度上限。
    /// The disk-cache file name for an avatar: the SHA-256 of the address rather than the address itself, which avoids illegal characters and length limits.
    /// </summary>
    /// <param name="url">地址。/ The address.</param>
    /// <returns>十六进制文件名（不含扩展名）。/ A hexadecimal file name without an extension.</returns>
    public static string ResolveCacheFileName(string url)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
