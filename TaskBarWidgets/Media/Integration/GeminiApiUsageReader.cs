using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TaskBarWidgets.Media.Integration;

// Only explicitly supplied service-account credentials are used. Neither credential
// URLs nor response links can redirect the fixed Google authorization/monitoring hosts.
internal static class GeminiApiUsageReader
{
    private const string Prefix = "generativelanguage.googleapis.com/";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private static readonly string[] RequestMetrics = ["generate_content_free_tier_requests", "generate_requests_per_model",
        "generate_content_paid_tier_2_requests", "generate_content_paid_tier_3_requests"];

    internal static async Task<AiUsageSnapshot> ReadAsync(string serviceAccountJson, string projectId, CancellationToken token)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        return await ReadAsync(serviceAccountJson, projectId, client, DateTimeOffset.UtcNow, token);
    }

    internal static async Task<AiUsageSnapshot> ReadAsync(string credential, string projectId, HttpClient client,
        DateTimeOffset now, CancellationToken token)
    {
        now = now.ToUniversalTime();
        if (!Regex.IsMatch(projectId, "^[a-z][a-z0-9-]{4,28}[a-z0-9]$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("请填写有效的 Google Cloud 项目 ID。");
        using var parsed = JsonDocument.Parse(credential);
        var account = parsed.RootElement;
        if (Text(account, "type") != "service_account" || !Text(account, "client_email").EndsWith(".iam.gserviceaccount.com", StringComparison.Ordinal))
            throw new InvalidOperationException("请选择 Google Cloud 服务账号 JSON；普通 Gemini API Key 无法读取项目监控。");
        var assertion = CreateAssertion(account, now);
        using var auth = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer", ["assertion"] = assertion }) };
        using var authResponse = await client.SendAsync(auth, HttpCompletionOption.ResponseHeadersRead, token);
        using var authData = await ReadJsonAsync(authResponse, token);
        var accessToken = Text(authData.RootElement, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Length > 16384 || accessToken.Any(char.IsControl))
            throw new InvalidOperationException("Google 授权未返回有效凭据。请检查服务账号权限。");

        var outputEnd = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        var historyStart = new DateTimeOffset(now.UtcDateTime.Date.AddDays(-83), TimeSpan.Zero);
        var history = await QueryAsync(Prefix + "generate_content_usage_output_token_count", historyStart, outputEnd, true);
        var days = ParseOutputDays(history, historyStart, outputEnd);
        var quotas = new List<AiUsageQuota>();
        var quotaFailed = false;
        var asOf = DateTimeOffset.FromUnixTimeSeconds((now.ToUnixTimeSeconds() - 180) / 60 * 60);
        var quotaStart = PacificDayStart(now);
        // The preceding day's counters must never be shown against today's allowance
        // during the monitoring delay just after Pacific midnight.
        if (asOf > quotaStart)
        {
            foreach (var metric in RequestMetrics)
            {
                try
                {
                    var limits = await QueryAsync(Prefix + "quota/" + metric + "/limit", asOf.AddMinutes(-30), asOf, false);
                    if (!limits.Any(HasDailyLimit)) continue;
                    var usage = await QueryAsync(Prefix + "quota/" + metric + "/usage", quotaStart, asOf, false);
                    quotas.AddRange(ParseDailyQuotas(limits, usage, quotaStart, asOf));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) when (error is HttpRequestException or InvalidOperationException or JsonException)
                { quotaFailed = true; }
            }
        }
        return new AiUsageSnapshot(quotas.Count == 0 || quotaFailed ? "partial" : "ready", null, now,
            quotas, days, null, days.Count == 0 ? null : days.Max(day => day.Tokens))
        {
            SourceLabel = "Google Cloud Monitoring · 项目监控",
            ActivityUnit = "tokens",
            ActivityUtc = true,
            HistoryNote = $"仅输出 Token，不含输入；按 UTC 日期汇总，截止 {outputEnd:MM-dd HH:mm} UTC。空白表示未返回记录。",
            QuotaNote = quotas.Count == 0 ? "未返回可匹配的日请求配额；实时额度请在 AI Studio 查看。"
                : $"日请求额度为监控观测值（截至 {asOf:MM-dd HH:mm} UTC），存在延迟；未包含每分钟等其他限制。"
        };

        async Task<List<JsonElement>> QueryAsync(string metric, DateTimeOffset start, DateTimeOffset end, bool hourly)
        {
            var results = new List<JsonElement>();
            string? page = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var number = 0; number < 32; number++)
            {
                var query = new Dictionary<string, string>
                {
                    ["filter"] = "metric.type = \"" + metric + "\" AND project = \"" + projectId + "\"",
                    ["interval.startTime"] = start.ToString("O", CultureInfo.InvariantCulture),
                    ["interval.endTime"] = end.ToString("O", CultureInfo.InvariantCulture),
                    ["view"] = "FULL", ["pageSize"] = "4000"
                };
                if (hourly) { query["aggregation.alignmentPeriod"] = "3600s"; query["aggregation.perSeriesAligner"] = "ALIGN_SUM"; }
                if (page is not null) query["pageToken"] = page;
                var uri = "https://monitoring.googleapis.com/v3/projects/" + Uri.EscapeDataString(projectId) + "/timeSeries?" +
                    string.Join("&", query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                using var data = await ReadJsonAsync(response, token);
                foreach (var field in new[] { "executionErrors", "unreachable" })
                    if (data.RootElement.TryGetProperty(field, out var incomplete) &&
                        (incomplete.ValueKind != JsonValueKind.Array || incomplete.GetArrayLength() != 0))
                        throw new InvalidOperationException("Google 监控返回不完整数据，未将其作为完整用量或剩余额度显示。");
                if (data.RootElement.TryGetProperty("timeSeries", out var found) && found.ValueKind == JsonValueKind.Array)
                    results.AddRange(found.EnumerateArray().Select(value => value.Clone()));
                page = Text(data.RootElement, "nextPageToken");
                if (page.Length == 0) return results;
                if (page.Length > 8192 || !seen.Add(page)) throw new InvalidOperationException("Google 监控分页无效，请稍后重试。");
            }
            throw new InvalidOperationException("项目监控记录过多，未显示截断的用量，请在 AI Studio 查看。");
        }
    }

    private static string CreateAssertion(JsonElement account, DateTimeOffset now)
    {
        static string Encode(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var claims = Encode(JsonSerializer.SerializeToUtf8Bytes(new { iss = Text(account, "client_email"),
            scope = "https://www.googleapis.com/auth/monitoring.read", aud = TokenEndpoint,
            iat = now.ToUnixTimeSeconds(), exp = now.AddMinutes(10).ToUnixTimeSeconds() }));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(Text(account, "private_key"));
        var content = header + "." + claims;
        return content + "." + Encode(rsa.SignData(Encoding.ASCII.GetBytes(content), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        // Do not surface raw error bodies, URLs or Google account diagnostics.
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Google 监控请求失败。请确认已启用 Monitoring API，并授予 Monitoring Viewer 权限。", null, response.StatusCode);
        if (response.Content.Headers.ContentLength is > 4 * 1024 * 1024) throw new InvalidOperationException("监控响应过大，未显示截断的数据。");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) != 0)
        {
            if (buffer.Length + read > 4 * 1024 * 1024) throw new InvalidOperationException("监控响应过大，未显示截断的数据。");
            buffer.Write(chunk, 0, read);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }

    internal static DateTimeOffset PacificDayStart(DateTimeOffset now)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        var local = TimeZoneInfo.ConvertTime(now, zone).Date;
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    internal static IReadOnlyList<AiUsageTokenDay> ParseOutputDays(IEnumerable<JsonElement> series, DateTimeOffset start, DateTimeOffset end)
    {
        var days = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in series)
        {
            var identity = Identity(item, true);
            foreach (var point in Points(item))
            {
                if (!TryPoint(point, out var from, out var until, out var count) || from < start || until > end ||
                    until - from != TimeSpan.FromHours(1) || from.UtcDateTime.Minute != 0 || from.UtcDateTime.Second != 0 ||
                    !seen.Add(identity + "|" + from.ToUnixTimeSeconds())) continue;
                var date = from.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                days[date] = checked(days.GetValueOrDefault(date) + count);
            }
        }
        return days.Select(pair => new AiUsageTokenDay(pair.Key, pair.Value)).ToArray();
    }

    private static bool HasDailyLimit(JsonElement item) => Label(item, "limit_name").Contains("PerDay", StringComparison.OrdinalIgnoreCase);
    internal static IReadOnlyList<AiUsageQuota> ParseDailyQuotas(IEnumerable<JsonElement> limits, IEnumerable<JsonElement> usages,
        DateTimeOffset start, DateTimeOffset asOf)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var usage in usages.Where(HasDailyLimit))
        foreach (var point in Points(usage))
        {
            if (!TryPoint(point, out var from, out var until, out var value) || from < start || until > asOf || until <= from ||
                !seen.Add(Identity(usage, true) + "|" + from.ToUnixTimeSeconds() + "|" + until.ToUnixTimeSeconds())) continue;
            var key = Identity(usage, false);
            counts[key] = checked(counts.GetValueOrDefault(key) + value);
        }
        var result = new Dictionary<string, AiUsageQuota>(StringComparer.Ordinal);
        foreach (var limit in limits.Where(HasDailyLimit))
        {
            var key = Identity(limit, false);
            var latest = Points(limit).Select(point => (Point: point, End: PointEnd(point))).Where(pair => pair.End <= asOf && pair.End >= asOf.AddMinutes(-30))
                .OrderByDescending(pair => pair.End).FirstOrDefault();
            if (latest.Point.ValueKind != JsonValueKind.Object || Number(latest.Point) is not { } maximum || maximum <= 0 || !counts.TryGetValue(key, out var used)) continue;
            var next = PacificDayStart(start.AddHours(36));
            result[key] = new AiUsageQuota(key, Label(limit, "model") + " · 日请求额度（监控观测）",
                Math.Clamp(100.0 * used / maximum, 0, 100), 1440, next.ToUnixTimeSeconds());
        }
        return result.Values.ToArray();
    }

    private static string Identity(JsonElement series, bool allLabels)
    {
        var type = series.TryGetProperty("metric", out var metric) ? Text(metric, "type") : "";
        type = Regex.Replace(type, "/(usage|limit)$", "");
        string Labels(JsonElement element) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Object
            ? string.Join(";", labels.EnumerateObject().Where(pair => allLabels || pair.Name is "model" or "limit_name")
                .OrderBy(pair => pair.Name, StringComparer.Ordinal).Select(pair => pair.Name + "=" + pair.Value.GetRawText())) : "";
        var resource = series.TryGetProperty("resource", out var found) ? Text(found, "type") + ":" +
            (found.TryGetProperty("labels", out var resourceLabels) ? string.Join(";", resourceLabels.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => p.Name + "=" + p.Value.GetRawText())) : "") : "";
        return type + "|" + Labels(metric) + "|" + resource;
    }
    private static IEnumerable<JsonElement> Points(JsonElement series) => series.ValueKind == JsonValueKind.Object && series.TryGetProperty("points", out var points) && points.ValueKind == JsonValueKind.Array ? points.EnumerateArray() : [];
    private static string Label(JsonElement item, string name) => item.TryGetProperty("metric", out var metric) && metric.TryGetProperty("labels", out var labels) ? Text(labels, name) : "";
    private static string Text(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    private static DateTimeOffset? PointEnd(JsonElement point) => point.TryGetProperty("interval", out var interval) && DateTimeOffset.TryParse(Text(interval, "endTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var end) ? end : null;
    private static long? Number(JsonElement point) => point.TryGetProperty("value", out var value) && long.TryParse(Text(value, "int64Value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number >= 0 ? number : null;
    private static bool TryPoint(JsonElement point, out DateTimeOffset start, out DateTimeOffset end, out long value)
    {
        start = end = default; value = 0;
        if (point.ValueKind != JsonValueKind.Object || !point.TryGetProperty("interval", out var interval) || Number(point) is not { } count ||
            !DateTimeOffset.TryParse(Text(interval, "startTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out start) ||
            !DateTimeOffset.TryParse(Text(interval, "endTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out end)) return false;
        value = count; return true;
    }
}
