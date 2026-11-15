using System.Text.Json;
using System.Globalization;
using TaskBarWidgets.Media.Integration;
using TaskbarLyrics.App;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class CodexUsageTests
{
    [Theory]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    public void HistoryDatesRemainGregorianUnderOtherSystemCultures(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var snapshot = CodexUsageService.Parse(Json("{}"),
                Json("""{"dailyUsageBuckets":[{"startDate":"2026-11-14","tokens":42}]}"""), DateTimeOffset.UtcNow);
            Assert.Equal("2026-11-14", Assert.Single(snapshot.Days!).Date);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    [Fact]
    public void UsesNamedQuotaBucketsAndDoesNotInventAnAbsentWindow()
    {
        var limits = Json("""{"rateLimits":{"primary":{"usedPercent":99}},"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":20,"windowDurationMins":10080,"resetsAt":1792886290},"secondary":null},"other":{"primary":{"usedPercent":null}}}}""");
        var quota = Assert.Single(CodexUsageService.ParseQuotas(limits));
        Assert.Equal(20, quota.UsedPercent); Assert.Equal(10080, quota.WindowMinutes);
        Assert.Equal("codex:primary", quota.Id);
    }
    [Fact]
    public void DailyTotalsAre64BitAndDuplicateAggregatesAreNotSummed()
    {
        var data = Json("""{"summary":{"lifetimeTokens":2848538927,"peakDailyTokens":null},"dailyUsageBuckets":[{"startDate":"2026-10-18","tokens":3000000000},{"startDate":"2026-10-18","tokens":3000000001},{"startDate":"invalid","tokens":9},{"startDate":"2026-10-17","tokens":-1}]}""");
        var snapshot = CodexUsageService.Parse(Json("{}"), data, DateTimeOffset.UtcNow);
        Assert.Equal(2848538927L, snapshot.LifetimeTokens);
        Assert.Equal(3000000001L, Assert.Single(snapshot.Days!).Tokens);
        Assert.Null(snapshot.PeakDailyTokens);
    }
    [Fact]
    public void UnavailableHistoryRemainsDistinctFromAnEmptyHistory()
    {
        Assert.Null(CodexUsageService.Parse(Json("{}"), Json("""{"summary":null,"dailyUsageBuckets":null}"""), DateTimeOffset.UtcNow).Days);
        Assert.Empty(CodexUsageService.Parse(Json("{}"), Json("""{"summary":{},"dailyUsageBuckets":[]}"""), DateTimeOffset.UtcNow).Days!);
    }
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void MalformedAndMissingDataRemainUnknown(string data)
    {
        var snapshot = CodexUsageService.Parse(Json(data), Json(data), DateTimeOffset.UtcNow);
        Assert.Empty(snapshot.Quotas);
        Assert.Null(snapshot.Days);
        Assert.Null(snapshot.LifetimeTokens);
    }

    [Fact]
    public void AccountChangeOrMissingIdentityCannotPublishMixedHistory()
    {
        Assert.True(CodexUsageService.SameAccount(Json("""{"accountId":"a"}"""), Json("""{"accountId":"a"}""")));
        Assert.False(CodexUsageService.SameAccount(Json("""{"accountId":"a"}"""), Json("""{"accountId":"b"}""")));
        Assert.False(CodexUsageService.SameAccount(Json("{}"), Json("{}")));
    }

    [Fact]
    public void MissingHistoryIsPartialAndDoesNotReplaceValidQuotaWithZero()
    {
        var snapshot = CodexUsageService.Parse(Json("""{"rateLimits":{"primary":{"usedPercent":19,"windowDurationMins":10080},"secondary":null}}"""),
            Json("null"), DateTimeOffset.UtcNow);
        Assert.Equal("partial", snapshot.Status);
        Assert.Equal(19, Assert.Single(snapshot.Quotas).UsedPercent);
        Assert.Null(snapshot.Days);
        Assert.Null(snapshot.LifetimeTokens);
    }

    [Fact]
    public void PeekDoesNotStartCollectionAndDisposedCollectorCannotLaunch()
    {
        using var service = new CodexUsageService();
        Assert.Equal("idle", service.Snapshot.Status);
        service.Dispose();
        Assert.Equal("idle", service.Read(force: true).Status);
    }

    [Fact]
    public void AddingAiCardPreservesExistingPreferencesAndExplicitOptOut()
    {
        var settings = new AppSettings
        {
            QuickCardOrder = ["media", "weather"],
            QuickCardColumns = new() { ["media"] = 1, ["weather"] = 0 },
            QuickCardEnabled = new() { ["media"] = false, ["aiUsage"] = false },
        };
        settings.NormalizeQuickCards();
        Assert.Contains("aiUsage", settings.QuickCardOrder);
        Assert.False(settings.QuickCardEnabled["aiUsage"]);
        Assert.False(settings.QuickCardEnabled["media"]);
        Assert.Equal(1, settings.QuickCardColumns["media"]);
        Assert.Equal(0, settings.QuickCardColumns["weather"]);
    }

    [Fact]
    public void WireSnapshotUsesCamelCaseAndNeverIncludesThreadUsage()
    {
        var snapshot = CodexUsageService.Parse(Json("{}"), Json("""{"summary":{"lifetimeTokens":3000000000},"threadUsage":[{"title":"private"}]}"""), DateTimeOffset.UtcNow);
        var wire = JsonSerializer.SerializeToElement(CardSnapshotBuilder.DescribeAiUsage(snapshot));
        Assert.Equal("codex", wire.GetProperty("providerId").GetString());
        Assert.Equal(3000000000L, wire.GetProperty("lifetimeTokens").GetInt64());
        Assert.False(wire.TryGetProperty("threadUsage", out _));
        Assert.False(wire.TryGetProperty("ThreadUsage", out _));
    }
}
