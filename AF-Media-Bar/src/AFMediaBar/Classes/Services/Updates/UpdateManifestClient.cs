using System.Diagnostics;
using System.IO;
using System.Net.Http;
using AFMediaBar.Classes.Models.Updates;
using AFMediaBar.Resources;

namespace AFMediaBar.Classes.Services.Updates;

/// <summary>
/// 一次清单读取的结果。
/// Result of one manifest fetch.
/// </summary>
/// <param name="Manifest">成功时的清单。/ Parsed manifest on success.</param>
/// <param name="FailureReason">失败原因；可直接展示。/ Failure reason, ready to display.</param>
/// <param name="Endpoint">实际使用的端点地址。/ Endpoint that was actually used.</param>
public sealed record UpdateManifestFetchResult(UpdateManifest? Manifest, string? FailureReason, string? Endpoint)
{
    /// <summary>是否读取成功。/ Whether the fetch succeeded.</summary>
    public bool Succeeded => Manifest is not null;
}

/// <summary>
/// 版本清单的读取器。
///
/// 只在<a href="UpdateSourcePlanPolicy">非代理端点</a>之间回退：安装包有清单里的 SHA-256 兜底，清单没有，
/// 所以清单一旦经第三方代理获取，"哈希保护"就会退化成"信任代理"。所有失败都被转换成一句可直接展示的原因，
/// 更新链路永远不会因为网络问题向调用方抛异常。
/// Reader for the version manifest.
///
/// It only falls back between <a href="UpdateSourcePlanPolicy">non-proxy endpoints</a>: an installer is backstopped
/// by the SHA-256 in the manifest while the manifest is not, so fetching the manifest through a third-party proxy
/// would degrade hash protection into trusting that proxy. Every failure is turned into a reason that can be shown
/// as-is, and the update path never throws at its caller because of a network problem.
/// </summary>
public sealed class UpdateManifestClient
{
    /// <summary>
    /// 仅用于测试与人工验收的覆盖变量：设置后**替换**整份端点列表。
    ///
    /// 它可以是一个本地文件的完整路径（便于验收时用本地清单走完整链路），也可以是一个 http/https 地址。
    /// 因为它是"替换"而不是"追加"，验收结果与线上顺序完全一致，不会出现悄悄回退到真实端点的情况。
    /// Override variable used only by tests and manual acceptance: when set it <b>replaces</b> the whole endpoint
    /// list.
    ///
    /// It may be a full path to a local file, which makes it practical to drive the whole update path from a local
    /// manifest during acceptance, or an http/https address. Because it replaces rather than appends, what is
    /// verified matches the production order exactly and can never silently fall back to a real endpoint.
    /// </summary>
    public const string ManifestUrlOverrideVariable = "AFMEDIABAR_UPDATE_MANIFEST_URL";

    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// 读取版本清单，按顺序尝试端点，返回第一个可用结果。
    /// Reads the version manifest, trying endpoints in order and returning the first usable result.
    /// </summary>
    /// <param name="userAgent">请求标识，包含程序版本，便于清单侧统计。/ Request identifier including the version, which lets the manifest side count requests.</param>
    /// <param name="cancellationToken">取消标记；用户取消或程序退出时中止。/ Cancellation token, cancelled by the user or on shutdown.</param>
    public async Task<UpdateManifestFetchResult> FetchAsync(string userAgent, CancellationToken cancellationToken)
    {
        string? firstFailure = null;
        foreach (var endpoint in ResolveEndpoints())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var content = await ReadEndpointAsync(endpoint, userAgent, cancellationToken).ConfigureAwait(false);
            if (content.Content is null)
            {
                firstFailure ??= content.FailureReason;
                continue;
            }

            var parsed = UpdateManifestParser.Parse(content.Content);
            if (parsed.Manifest is not null)
            {
                return new UpdateManifestFetchResult(parsed.Manifest, null, endpoint);
            }

            // 解析失败也继续尝试下一个端点：端点内容可能不一致，而换一个地址往往就能拿到完整清单。
            // A parse failure also moves on: endpoints can disagree, and a different address often serves a
            // complete manifest.
            firstFailure ??= parsed.FailureReason;
        }

        return new UpdateManifestFetchResult(
            null,
            firstFailure ?? Translations.Get("Update.Reason.ManifestUnreachable"),
            null);
    }

    /// <summary>当前会尝试的端点列表，已包含测试覆盖变量。/ Endpoints that will be attempted, including the test override.</summary>
    public static IReadOnlyList<string> ResolveEndpoints()
    {
        var overridden = Environment.GetEnvironmentVariable(ManifestUrlOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return [overridden.Trim()];
        }

        return UpdateSourcePlanPolicy.ManifestEndpoints;
    }

    private static async Task<(string? Content, string? FailureReason)> ReadEndpointAsync(
        string endpoint,
        string userAgent,
        CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(endpoint))
            {
                return (await File.ReadAllTextAsync(endpoint, cancellationToken).ConfigureAwait(false), null);
            }

            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                return (null, Translations.Get("Update.Reason.ManifestEndpointInvalid"));
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(userAgent);
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
            using var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (null, Translations.Format("Update.Reason.ManifestHttp", (int)response.StatusCode));
            }

            return (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Update] Manifest endpoint failed ({endpoint}): {exception.Message}");
            return (null, Translations.Get("Update.Reason.ManifestNetwork"));
        }
    }
}
