using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TaskBarWidgets.Media.Integration;

internal static class AiUsageApiReader
{
    internal static async Task<AiUsageSnapshot> ReadAsync(string id, string credential, CancellationToken token)
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TaskBarWidgets/1.0");
        if (id == "claude-api")
        {
            client.DefaultRequestHeaders.Add("x-api-key", credential);
            client.DefaultRequestHeaders.Add("anthropic-version", "2026-10-21");
            client.DefaultRequestHeaders.Add("anthropic-beta", "analytics-usage-cost-2026-08-24");
        }
        else client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        if (id == "deepseek-api")
            return ParseDeepSeek(await GetAsync(client, "https://api.deepseek.com/user/balance", token));
        if (id is not ("openai-api" or "claude-api")) throw new ArgumentException("无效的 API 服务。");
        var start = DateTimeOffset.UtcNow.Date.AddDays(-83); var end = DateTimeOffset.UtcNow;
        var daily = new SortedDictionary<string, long>(StringComparer.Ordinal);
        // Daily query ranges are capped to 31 days by the services.
        for (var cursor = new DateTimeOffset(start, TimeSpan.Zero); cursor < end; cursor = cursor.AddDays(30))
        {
            var until = cursor.AddDays(30) < end ? cursor.AddDays(30) : end;
            var baseUrl = id == "openai-api"
                ? $"https://api.openai.com/v1/organization/usage/completions?start_time={cursor.ToUnixTimeSeconds()}&end_time={until.ToUnixTimeSeconds()}&bucket_width=1d&limit=31"
                : "https://api.anthropic.com/v1/organizations/analytics/usage?starting_at=" + Uri.EscapeDataString(cursor.ToString("O", CultureInfo.InvariantCulture)) +
                    "&ending_at=" + Uri.EscapeDataString(until.ToString("O", CultureInfo.InvariantCulture)) + "&time_bucket_width=1d&order_by=time&limit=100";
            string? page = null;
            var seenPages = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; ; index++)
            {
                if (index >= 30) throw new InvalidDataException("分析结果分页过多。");
                var data = await GetAsync(client, baseUrl + (page is null ? "" : "&page=" + Uri.EscapeDataString(page)), token);
                foreach (var day in ParseDays(id, data)) daily[day.Date] = checked(daily.GetValueOrDefault(day.Date) + day.Tokens);
                page = AiUsageHub.Text(data, "next_page");
                if (page.Length == 0) break;
                if (!seenPages.Add(page)) throw new InvalidDataException("分页标记重复。");
            }
        }
        var snapshot = new AiUsageSnapshot("partial", null, DateTimeOffset.UtcNow, [],
            daily.Select(day => new AiUsageTokenDay(day.Key, day.Value)).ToArray(), null, daily.Count == 0 ? null : daily.Values.Max())
        { ActivityUtc = true, SourceLabel = "官方组织 API", HistoryNote = id == "openai-api" ? "UTC 日聚合，仅组织 completions 用量；不代表所有 API 产品。" : "UTC 日聚合，组织分析数据可能延迟。",
            QuotaNote = "官方接口未提供可花费余额或订阅剩余百分比。" };
        if (id == "openai-api")
        {
            // Costs are spend, never remaining balance. Preserve independent token results if cost access fails.
            try
            {
                var month = new DateTimeOffset(end.Year, end.Month, 1, 0, 0, 0, TimeSpan.Zero);
                decimal spent = 0; string? page = null;
                var seenPages = new HashSet<string>(StringComparer.Ordinal);
                for (var index = 0; ; index++)
                {
                    if (index >= 30) throw new InvalidDataException();
                    var data = await GetAsync(client, $"https://api.openai.com/v1/organization/costs?start_time={month.ToUnixTimeSeconds()}&end_time={end.ToUnixTimeSeconds()}&bucket_width=1d&limit=31" +
                        (page is null ? "" : "&page=" + Uri.EscapeDataString(page)), token);
                    spent += ParseOpenAiCosts(data); page = AiUsageHub.Text(data, "next_page"); if (page.Length == 0) break;
                    if (!seenPages.Add(page)) throw new InvalidDataException("费用分页标记重复。");
                }
                snapshot = snapshot with { QuotaNote = $"本月组织支出 USD {spent.ToString("0.00######", CultureInfo.InvariantCulture)}；这不是余额，剩余额度未知。" };
            }
            catch (Exception error) when (error is HttpRequestException or InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException or OverflowException) { }
        }
        return snapshot;
    }
    internal static AiUsageSnapshot ParseDeepSeek(JsonElement root)
    {
        var balances = new List<AiUsageBalance>();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("balance_infos", out var values) && values.ValueKind == JsonValueKind.Array)
            foreach (var item in values.EnumerateArray())
            {
                var currency = AiUsageHub.Text(item, "currency"); var text = AiUsageHub.Text(item, "total_balance");
                if (currency is not ("CNY" or "USD") || text.Length is 0 or > 64 ||
                    !System.Text.RegularExpressions.Regex.IsMatch(text, @"^-?[0-9]+(?:\.[0-9]+)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)) continue;
                balances.Add(new(currency, text, "可用余额"));
            }
        if (balances.Count == 0) throw new InvalidDataException("余额数据不可用。");
        return new("partial", null, DateTimeOffset.UtcNow, [], null, null, null)
        { Balances = balances, SourceLabel = "官方 DeepSeek API", HistoryNote = "公开接口未提供全账户每日 Token 历史；可手动记录。",
            QuotaNote = root.TryGetProperty("is_available", out var available) && available.ValueKind == JsonValueKind.False
                ? "官方报告余额不足以进行 API 调用；余额不能换算成剩余百分比。" : "余额按币种显示，不能换算成剩余百分比。" };
    }
    internal static IReadOnlyList<AiUsageTokenDay> ParseDays(string id, JsonElement root)
    {
        var days = new SortedDictionary<string, long>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("缺少官方每日数据。");
        foreach (var bucket in data.EnumerateArray())
        {
            DateTimeOffset date; long tokens = 0;
            if (id == "openai-api")
            {
                if (!bucket.TryGetProperty("start_time", out var time) || !time.TryGetInt64(out var seconds)) throw new InvalidDataException();
                date = DateTimeOffset.FromUnixTimeSeconds(seconds);
                if (!bucket.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
                foreach (var result in results.EnumerateArray()) tokens = checked(tokens + RequiredCount(result, "input_tokens") + RequiredCount(result, "output_tokens"));
                // input_cached_tokens is already included in input_tokens.
            }
            else
            {
                if (!DateTimeOffset.TryParse(AiUsageHub.Text(bucket, "starting_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date) ||
                    !bucket.TryGetProperty("usage", out var usage)) throw new InvalidDataException();
                tokens = RequiredCount(usage, "total_tokens");
            }
            var key = date.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            days[key] = checked(days.GetValueOrDefault(key) + tokens);
        }
        return days.Select(day => new AiUsageTokenDay(day.Key, day.Value)).ToArray();
    }
    private static long RequiredCount(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) &&
        item.TryGetInt64(out var count) && count >= 0 ? count : throw new InvalidDataException("Token 计数无效。");
    internal static decimal ParseOpenAiCosts(JsonElement root)
    {
        decimal total = 0;
        foreach (var day in root.GetProperty("data").EnumerateArray())
            foreach (var result in day.GetProperty("results").EnumerateArray())
            {
                var amount = result.GetProperty("amount");
                if (AiUsageHub.Text(amount, "currency") != "usd" || !amount.TryGetProperty("value", out var value) || !value.TryGetDecimal(out var cost)) throw new InvalidDataException();
                total += cost;
            }
        return total;
    }
    private static async Task<JsonElement> GetAsync(HttpClient client, string url, CancellationToken token)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream(); var buffer = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(buffer, token).ConfigureAwait(false); if (read == 0) break;
            if (output.Length + read > 4 * 1024 * 1024) throw new InvalidDataException(); output.Write(buffer, 0, read);
        }
        using var document = JsonDocument.Parse(output.ToArray()); return document.RootElement.Clone();
    }
}
