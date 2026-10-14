using System.Text.Json;
using TaskBarWidgets.Media.Integration;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class TrafficHeatmapTests
{
    [Theory]
    [InlineData("2026-10-13", "2026-07-27", 1)]
    [InlineData("2024-03-03", "2023-12-11", 6)]
    [InlineData("2024-01-01", "2023-10-16", 0)]
    public void CalendarContainsTwelveMondayWeeksAndDisablesOnlyFutureDates(string today, string start, int currentWeekday)
    {
        var map = TrafficHeatmap.Create(DateOnly.Parse(today));
        Assert.Equal(84, map.Days.Count);
        Assert.Equal(DateOnly.Parse(start), map.Days[0].Date);
        Assert.Equal(DayOfWeek.Monday, map.Days[0].Date.DayOfWeek);
        Assert.Equal(DayOfWeek.Sunday, map.Days[^1].Date.DayOfWeek);
        Assert.Equal(12, map.Days.Select(day => day.Week).Distinct().Count());
        Assert.Equal(7, map.Days.Select(day => day.Weekday).Distinct().Count());
        Assert.Equal(6 - currentWeekday, map.Days.Count(day => day.IsFuture));
        Assert.Equal(84, map.Days.Select(day => day.Date).Distinct().Count());
        Assert.All(map.Days, day => Assert.Equal(map.Days[0].Date.AddDays(day.Week * 7 + day.Weekday), day.Date));
    }

    [Fact]
    public void DailyTotalsDeduplicateAndPreserveUnknownZeroAndFutureStates()
    {
        using var json = JsonDocument.Parse("""
            {"today":"2026-10-13","scopeLabel":"以太网","days":[
              {"date":"2026-10-12","totalKiB":1024},
              {"date":"2026-10-12","totalKiB":2048},
              {"date":"2026-10-13","totalKiB":0},
              {"date":"2026-10-14","totalKiB":8192},
              {"date":"2026-10-11","totalKiB":-1},
              {"date":"2026-10-10","totalKiB":"0"},
              {"date":"2020-01-01","totalKiB":900000}
            ]}
            """);
        var map = TrafficHeatmap.Parse(json.RootElement);
        Assert.Equal("以太网", map.ScopeLabel);
        Assert.Equal(2048, map.TotalKiB);
        var known = map.Days.Single(day => day.Date == new DateOnly(2026, 10, 12));
        var zero = map.Days.Single(day => day.Date == map.Today);
        var missing = map.Days.Single(day => day.Date == new DateOnly(2026, 10, 11));
        var future = map.Days.Single(day => day.Date == new DateOnly(2026, 10, 14));
        Assert.Equal(2048, known.TotalKiB);
        Assert.Equal(1, zero.Level);
        Assert.Contains("0 MB", zero.Description);
        Assert.Equal(0, missing.Level);
        Assert.Null(missing.TotalKiB);
        Assert.Contains("没有记录", missing.Description);
        Assert.Equal(-1, future.Level);
        Assert.Null(future.TotalKiB);
        Assert.Contains("未来日期", future.Description);
    }

    [Theory]
    [InlineData(0, 1, "0 MB")]
    [InlineData(1, 2, "<0.01 MB")]
    [InlineData(10.24, 2, "0.01 MB")]
    [InlineData(512, 2, "0.5 MB")]
    [InlineData(102400, 3, "100 MB")]
    [InlineData(1048576, 4, "1 GB")]
    [InlineData(5242880, 5, "5 GB")]
    public void UsageBandsAndUnitsShareTheSameDailyTotal(double total, int level, string label)
    {
        var day = new TrafficHeatmapDay(new DateOnly(2026, 10, 13), 11, 1, total, false);
        Assert.Equal(level, day.Level);
        Assert.Equal(label, TrafficHeatmap.FormatUsage(total));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"today\":\"2026-13-01\",\"days\":[]}")]
    [InlineData("{\"today\":\"2026-10-13\",\"days\":null}")]
    public void InvalidHistoryCannotBeShownAsZeroUsage(string payload)
    {
        using var json = JsonDocument.Parse(payload);
        Assert.Throws<FormatException>(() => TrafficHeatmap.Parse(json.RootElement));
    }
}
