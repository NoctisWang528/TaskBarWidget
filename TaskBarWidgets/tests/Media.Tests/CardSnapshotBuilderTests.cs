using System.Text.Json;
using TaskBarWidgets.Media.Integration;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class CardSnapshotBuilderTests
{
    [Fact]
    public void WeatherJobUsesItsStatusAndOlderPollCannotReplaceIt()
    {
        var builder = new CardSnapshotBuilder();
        var job = JsonSerializer.SerializeToElement(new { status = new { revision = 8, provider = 1,
            city = new { id = "101010100", name = "北京" }, lastSuccessCity = new { id = "101010100", name = "北京" },
            lastSuccess = "2026-10-24 20:00:00", lastAttempt = "2026-10-24 19:59:00",
            details = new { current = new { temperature = "23" },
                airQuality = new { indexCode = "cn-mee", indexName = "AQI", error = "partial", attribution = "source" },
                alertsError = "alerts partial", alertsAttribution = "alerts source",
                alerts = new[] { new { type = "1001", publishedAt = "2026-10-24 19:00:00" } } } } });
        var current = builder.SetWeather(job);
        var first = JsonSerializer.SerializeToElement(current);
        Assert.Equal(1, first.GetProperty("revision").GetInt64());
        Assert.Equal(builder.WeatherRevision, first.GetProperty("revision").GetInt64());
        Assert.Equal("和风天气", first.GetProperty("provider").GetString());
        Assert.Equal("23", first.GetProperty("temperature").GetString());
        Assert.Equal("101010100", first.GetProperty("cityId").GetString());
        Assert.Equal("北京", first.GetProperty("lastSuccessCity").GetString());
        Assert.Equal("101010100", first.GetProperty("lastSuccessCityId").GetString());
        Assert.Equal("2026-10-24 20:00:00", first.GetProperty("lastSuccess").GetString());
        Assert.Equal("2026-10-24 19:59:00", first.GetProperty("lastAttempt").GetString());
        var details = first.GetProperty("details");
        Assert.Equal("cn-mee", details.GetProperty("airQuality").GetProperty("indexCode").GetString());
        Assert.Equal("partial", details.GetProperty("airQuality").GetProperty("error").GetString());
        Assert.Equal("source", details.GetProperty("airQuality").GetProperty("attribution").GetString());
        Assert.Equal("alerts partial", details.GetProperty("alertsError").GetString());
        Assert.Equal("alerts source", details.GetProperty("alertsAttribution").GetString());
        Assert.Equal("1001", details.GetProperty("alerts")[0].GetProperty("type").GetString());
        Assert.Equal("2026-10-24 19:00:00", details.GetProperty("alerts")[0].GetProperty("publishedAt").GetString());
        Assert.True(first.GetProperty("hasData").GetBoolean());
        Assert.Same(current, builder.SetWeather(JsonSerializer.SerializeToElement(new { revision = 7 })));
        Assert.Same(current, builder.SetWeather(job));
        Assert.Equal(1, builder.WeatherRevision);
        var empty = JsonSerializer.SerializeToElement(builder.SetWeather(JsonSerializer.SerializeToElement(new { revision = 9 })));
        Assert.Equal(2, empty.GetProperty("revision").GetInt64());
        Assert.False(empty.GetProperty("hasData").GetBoolean());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("details").ValueKind);
    }

    [Fact]
    public void CachedTrafficAndDirectReplyShareTheIncrementedRevision()
    {
        var builder = new CardSnapshotBuilder();
        var history = JsonSerializer.SerializeToElement(new { today = "2026-10-24", days = Array.Empty<object>() });
        var first = JsonSerializer.SerializeToElement(builder.SetTraffic(history));
        Assert.Equal(1, first.GetProperty("revision").GetInt64());
        Assert.Equal(builder.TrafficRevision, first.GetProperty("revision").GetInt64());
        Assert.Equal(84, first.GetProperty("days").GetArrayLength());
        var second = JsonSerializer.SerializeToElement(builder.SetTraffic(history));
        Assert.Equal(2, second.GetProperty("revision").GetInt64());
    }

    [Fact]
    public void InvalidTrafficDoesNotReplaceTheLastValidHeatmap()
    {
        var builder = new CardSnapshotBuilder();
        var history = JsonSerializer.SerializeToElement(new { today = "2026-10-25", days = Array.Empty<object>() });
        var valid = builder.SetTraffic(history);
        Assert.Throws<FormatException>(() => builder.SetTraffic(JsonSerializer.SerializeToElement(new { pending = true })));
        Assert.Equal(1, builder.TrafficRevision);
        var cache = typeof(CardSnapshotBuilder).GetField("_lastTraffic",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.Same(valid, cache!.GetValue(builder));
        Assert.Equal(84, JsonSerializer.SerializeToElement(valid).GetProperty("days").GetArrayLength());
    }
}
