namespace AFMediaBar.Classes.Services.Updates;

/// <summary>
/// GitHub 直链的加速候选展开策略。
///
/// 加速站点只解决"国内能否连上 GitHub"，不承担完整性：安装包的 SHA-256 来自版本清单，而清单只从
/// raw.githubusercontent.com 与 jsDelivr 这类不改写内容的端点读取，因此换包一定会在校验阶段被拒绝。
/// 站点会过期，所以清单里的 <c>accelerators</c> 可以整体覆盖内置默认列表；代码里不写死任何"唯一可信站点"。
/// Expansion policy for GitHub direct-link accelerator candidates.
///
/// Accelerator sites only answer "can this machine reach GitHub", never integrity: the installer's SHA-256 comes
/// from the version manifest, and that manifest is read only from endpoints that do not rewrite content
/// (raw.githubusercontent.com and jsDelivr), so a substituted file is always rejected during verification. Sites
/// come and go, so the manifest's <c>accelerators</c> field may replace the built-in defaults entirely; the code
/// never hard-codes a single trusted site.
/// </summary>
public static class GitHubAcceleratorPolicy
{
    /// <summary>唯一会被加速的原始主机。/ The only origin host that gets accelerated.</summary>
    public const string GitHubHost = "github.com";

    /// <summary>
    /// 内置加速站点模板（按顺序尝试）。
    ///
    /// 这些是第三方反向代理，可用性与速度随时变化：发布侧应当实测后用清单里的 <c>accelerators</c> 覆盖它们，
    /// 或者用空数组明确关闭加速。
    /// Built-in accelerator templates, attempted in order.
    ///
    /// These are third-party reverse proxies whose availability and speed change over time: the release side is
    /// expected to verify them and override this list through the manifest's <c>accelerators</c> field, or to
    /// disable acceleration outright with an empty array.
    /// </summary>
    public static IReadOnlyList<string> DefaultAccelerators { get; } =
    [
        "https://v4.gh-proxy.org/",
        "https://cdn.gh-proxy.org/",
        "https://gh-proxy.com/"
    ];

    /// <summary>
    /// 归一化一个加速站点模板：只接受"https 主机根路径"形式，其余一律视为无效并返回空字符串。
    /// Normalizes an accelerator template: only "https host root path" is accepted, and anything else is reported
    /// as invalid by returning an empty string.
    /// </summary>
    /// <param name="template">待归一化模板。/ Template to normalize.</param>
    /// <returns>形如 <c>https://host/</c> 的模板，或空字符串。/ A <c>https://host/</c> template, or an empty string.</returns>
    public static string NormalizeTemplate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return string.Empty;
        }

        var text = template.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return string.Empty;
        }

        // 带路径、查询或片段的模板不是模板：拼接时无法保证结果仍是直链。
        // A template carrying a path, query or fragment is not a template: concatenation could not guarantee a direct link.
        if (uri.AbsolutePath.Trim('/').Length > 0 || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return string.Empty;
        }

        return $"https://{uri.Host.ToLowerInvariant()}/";
    }

    /// <summary>
    /// 该地址是否可以被加速：必须是 <c>github.com</c> 上的 https 直链，且本身还不是一个已改写的加速地址。
    /// Whether the address can be accelerated: it must be an https direct link on <c>github.com</c> and must not
    /// already be a rewritten accelerator address.
    /// </summary>
    /// <param name="url">原始地址。/ Original address.</param>
    public static bool CanAccelerate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(uri.Host, GitHubHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !IsAlreadyAccelerated(uri);
    }

    /// <summary>
    /// 展开可尝试的加速地址。返回的列表不含原始直链，也绝不重复改写已经带代理前缀的地址。
    /// Expands the accelerator addresses to try. The result never contains the original direct link and never
    /// rewrites an address that already carries a proxy prefix.
    /// </summary>
    /// <param name="url">GitHub 原始直链。/ Original GitHub direct link.</param>
    /// <param name="accelerators">清单提供的模板；为 null 时使用内置默认列表，空列表表示不加速。/ Templates from the manifest; null uses the built-in defaults and an empty list disables acceleration.</param>
    /// <returns>按顺序去重的加速地址。/ Deduplicated accelerator addresses in order.</returns>
    public static IReadOnlyList<string> Expand(string? url, IReadOnlyList<string>? accelerators)
    {
        if (!CanAccelerate(url))
        {
            return [];
        }

        var templates = accelerators ?? DefaultAccelerators;
        if (templates.Count == 0)
        {
            return [];
        }

        var original = url!.Trim();
        var host = new Uri(original).Host;
        var expanded = new List<string>(templates.Count);
        foreach (var candidate in templates)
        {
            var template = NormalizeTemplate(candidate);
            if (template.Length == 0)
            {
                continue;
            }

            // 自己的主机不做自己的代理：那只会产生一条必然失败的候选。
            // A host is never its own proxy: that would only add a candidate that cannot succeed.
            if (template.Equals($"https://{host.ToLowerInvariant()}/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var address = template + original;
            if (!expanded.Contains(address, StringComparer.OrdinalIgnoreCase))
            {
                expanded.Add(address);
            }
        }

        return expanded;
    }

    /// <summary>
    /// 地址是否已经被某个加速站点改写（路径本身又是一个 http(s) 地址）。重复改写会把两个站点串起来，
    /// 结果通常是 404，因此这里显式拒绝。
    /// Whether the address has already been rewritten by an accelerator, which shows up as a path that is itself
    /// an http(s) address. Rewriting twice chains two sites together and usually ends in a 404, so it is refused.
    /// </summary>
    /// <param name="uri">已解析的地址。/ Parsed address.</param>
    private static bool IsAlreadyAccelerated(Uri uri)
    {
        var path = uri.AbsolutePath;
        return path.StartsWith("/http://", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/https://", StringComparison.OrdinalIgnoreCase);
    }
}
