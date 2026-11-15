using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using TaskBarWidgets.Media.Integration;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class GeminiApiUsageTests
{
    private static JsonElement Series(string type, string limit, string model, params (string From, string To, long Value)[] points) =>
        JsonSerializer.SerializeToElement(new { metric = new { type = "generativelanguage.googleapis.com/" + type, labels = new { limit_name = limit, model } },
            resource = new { type = "generativelanguage.googleapis.com/Location", labels = new { project_id = "project-test", location = "global" } },
            points = points.Select(point => new { interval = new { startTime = point.From, endTime = point.To }, value = new { int64Value = point.Value.ToString(CultureInfo.InvariantCulture) } }) });

    [Fact]
    public void OutputHistoryUsesUtcHoursDeduplicatesPagesAndPreservesUnknownDays()
    {
        var first = Series("generate_content_usage_output_token_count", "", "a", ("2026-11-14T23:00:00Z", "2026-11-15T00:00:00Z", 3_000_000_001));
        var second = Series("generate_content_usage_output_token_count", "", "b", ("2026-11-14T23:00:00Z", "2026-11-15T00:00:00Z", 4));
        var partial = Series("generate_content_usage_output_token_count", "", "a", ("2026-11-15T00:00:00Z", "2026-11-15T00:15:00Z", 500));
        var result = GeminiApiUsageReader.ParseOutputDays([first, first, second, partial], DateTimeOffset.Parse("2026-11-13T00:00:00Z"), DateTimeOffset.Parse("2026-11-16T00:00:00Z"));
        Assert.Equal(new AiUsageTokenDay("2026-11-14", 3_000_000_005), Assert.Single(result));
    }

    [Fact]
    public void DailyQuotaMatchesModelAndLimitWithoutInventingZeroOrUsingMinuteLimits()
    {
        var limits = new[] {
            Series("quota/generate_requests_per_model/limit", "GenerateRequestsPerDay", "a", ("", "2026-11-15T10:00:00Z", 100)),
            Series("quota/generate_requests_per_model/limit", "GenerateRequestsPerDay", "b", ("", "2026-11-15T10:00:00Z", 1000)),
            Series("quota/generate_requests_per_model/limit", "GenerateRequestsPerMinute", "a", ("", "2026-11-15T10:00:00Z", 2)) };
        var usage = Series("quota/generate_requests_per_model/usage", "GenerateRequestsPerDay", "a",
            ("2026-11-15T09:00:00Z", "2026-11-15T09:01:00Z", 20),
            ("2026-11-15T07:59:00Z", "2026-11-15T08:00:00Z", 70));
        var result = GeminiApiUsageReader.ParseDailyQuotas(limits, [usage, usage], DateTimeOffset.Parse("2026-11-15T08:00:00Z"), DateTimeOffset.Parse("2026-11-15T10:00:00Z"));
        var quota = Assert.Single(result);
        Assert.Equal(20, quota.UsedPercent);
        Assert.Equal(1440, quota.WindowMinutes);
        Assert.Equal(DateTimeOffset.Parse("2026-11-16T08:00:00Z").ToUnixTimeSeconds(), quota.ResetsAt);
    }

    [Theory]
    [InlineData("2026-07-01T08:00:00Z", "2026-07-01T07:00:00Z")]
    [InlineData("2026-11-15T08:00:00Z", "2026-11-15T08:00:00Z")]
    [InlineData("2026-11-01T09:30:00Z", "2026-11-01T07:00:00Z")]
    public void DailyResetUsesPacificDst(string timestamp, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected), GeminiApiUsageReader.PacificDayStart(DateTimeOffset.Parse(timestamp)));

    [Fact]
    public void SpringTransitionQuotaResetIsTwentyThreeHoursAfterMidnight()
    {
        var limit = Series("quota/generate_requests_per_model/limit", "GenerateRequestsPerDay", "a", ("", "2026-03-08T12:00:00Z", 100));
        var usage = Series("quota/generate_requests_per_model/usage", "GenerateRequestsPerDay", "a", ("2026-03-08T11:00:00Z", "2026-03-08T11:01:00Z", 0));
        var quota = Assert.Single(GeminiApiUsageReader.ParseDailyQuotas([limit], [usage], DateTimeOffset.Parse("2026-03-08T08:00:00Z"), DateTimeOffset.Parse("2026-03-08T12:00:00Z")));
        Assert.Equal(DateTimeOffset.Parse("2026-03-09T07:00:00Z").ToUnixTimeSeconds(), quota.ResetsAt);
        Assert.Equal(0, quota.UsedPercent);
    }

    [Fact]
    public async Task ServiceAccountCannotRedirectCredentialAndNoRecordsRemainUnknown()
    {
        using var rsa = RSA.Create(2048);
        var credential = JsonSerializer.Serialize(new { type = "service_account", client_email = "reader@project-test.iam.gserviceaccount.com",
            private_key = rsa.ExportPkcs8PrivateKeyPem(), token_uri = "https://attacker.invalid/token" });
        using var handler = new FixedHostsHandler();
        using var client = new HttpClient(handler);
        var result = await GeminiApiUsageReader.ReadAsync(credential, "project-test", client, DateTimeOffset.Parse("2026-11-15T12:00:00Z"), CancellationToken.None);
        Assert.Empty(result.Quotas);
        Assert.Empty(result.Days!);
        Assert.Empty(result.Balances);
        Assert.Null(result.LifetimeTokens);
        Assert.Equal("partial", result.Status);
        Assert.DoesNotContain("test-token", JsonSerializer.Serialize(result));
        Assert.Contains("仅输出 Token", result.HistoryNote);
        Assert.True(result.ActivityUtc);
        Assert.Equal(6, handler.Calls);
    }

    private sealed class FixedHostsHandler : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var auth = request.RequestUri!.Host == "oauth2.googleapis.com";
            Assert.True(auth || request.RequestUri.Host == "monitoring.googleapis.com");
            if (auth) Assert.Null(request.Headers.Authorization);
            else
            {
                Assert.Equal("test-token", request.Headers.Authorization!.Parameter);
                Assert.Contains("project = \"project-test\"", Uri.UnescapeDataString(request.RequestUri.Query));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(auth ? "{\"access_token\":\"test-token\"}" : "{\"timeSeries\":[]}") });
        }
    }

    [Fact]
    public async Task HttpSuccessWithPartialMonitoringErrorsDoesNotBecomeEmptyHistory()
    {
        using var rsa = RSA.Create(2048);
        var credential = JsonSerializer.Serialize(new { type = "service_account", client_email = "reader@project-test.iam.gserviceaccount.com", private_key = rsa.ExportPkcs8PrivateKeyPem() });
        using var client = new HttpClient(new PartialHandler());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => GeminiApiUsageReader.ReadAsync(credential, "project-test", client,
            DateTimeOffset.Parse("2026-11-15T12:00:00Z"), CancellationToken.None));
        Assert.Contains("不完整", error.Message);
        Assert.DoesNotContain("sensitive-diagnostic", error.Message);
    }
    private sealed class PartialHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.Host == "oauth2.googleapis.com"
                ? "{\"access_token\":\"test-token\"}" : "{\"timeSeries\":[],\"executionErrors\":[{\"message\":\"sensitive-diagnostic\"}]}") });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CyclicOrExcessivePaginationCannotProduceTruncatedTotals(bool cycle)
    {
        using var rsa = RSA.Create(2048);
        var credential = JsonSerializer.Serialize(new { type = "service_account", client_email = "reader@project-test.iam.gserviceaccount.com", private_key = rsa.ExportPkcs8PrivateKeyPem() });
        using var handler = new PaginationHandler(cycle);
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => GeminiApiUsageReader.ReadAsync(credential, "project-test", client,
            DateTimeOffset.Parse("2026-11-15T12:00:00Z"), CancellationToken.None));
        Assert.Equal(cycle ? 2 : 32, handler.Pages);
    }
    private sealed class PaginationHandler(bool cycle) : HttpMessageHandler
    {
        internal int Pages;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = "{\"access_token\":\"test-token\"}";
            if (request.RequestUri!.Host != "oauth2.googleapis.com")
            {
                Pages++;
                content = JsonSerializer.Serialize(new { timeSeries = Array.Empty<object>(), nextPageToken = "page-" + (cycle ? 1 : Pages) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) });
        }
    }
}
