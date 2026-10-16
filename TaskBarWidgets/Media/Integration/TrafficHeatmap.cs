using System.Globalization;
using System.Text.Json;

namespace TaskBarWidgets.Media.Integration;

internal sealed record TrafficHeatmapDay(DateOnly Date, int Week, int Weekday, double? TotalKiB, bool IsFuture)
{
    internal string Description => $"{Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} · {(IsFuture ? "未来日期" : TotalKiB is { } total ? TrafficHeatmap.FormatUsage(total) : "没有记录")}";
}

internal sealed record TrafficHeatmap(DateOnly Today, string ScopeLabel, IReadOnlyList<TrafficHeatmapDay> Days)
{
    internal double TotalKiB => Days.Sum(day => day.TotalKiB ?? 0);
    internal double PeakKiB { get; } = Days.Where(day => !day.IsFuture)
        .Select(day => day.TotalKiB ?? 0).DefaultIfEmpty(0).Max();

    // Relative to this twelve-week window; future, unknown and recorded zero
    // remain separate from the four positive bands used by token-monitor.
    internal int LevelFor(TrafficHeatmapDay day)
    {
        if (day.IsFuture) return -1;
        if (day.TotalKiB is not { } total) return 0;
        if (total <= 0) return 1;
        var ratio = PeakKiB > 0 ? total / PeakKiB : 0;
        return ratio >= .75 ? 5 : ratio >= .50 ? 4 : ratio >= .25 ? 3 : 2;
    }

    internal static TrafficHeatmap Parse(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("today", out var todayValue) || todayValue.ValueKind != JsonValueKind.String ||
            !DateOnly.TryParseExact(todayValue.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var today) ||
            !payload.TryGetProperty("days", out var dayValues) || dayValues.ValueKind != JsonValueKind.Array)
            throw new FormatException("流量历史格式无效");

        var totals = new Dictionary<DateOnly, double>();
        foreach (var value in dayValues.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty("date", out var dateValue) || dateValue.ValueKind != JsonValueKind.String ||
                !DateOnly.TryParseExact(dateValue.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                !value.TryGetProperty("totalKiB", out var totalValue) || totalValue.ValueKind != JsonValueKind.Number ||
                !totalValue.TryGetDouble(out var total) || !double.IsFinite(total) || total < 0)
                continue; // An invalid record is unknown, never a fabricated zero.
            totals[date] = total; // A repeated daily total replaces the previous total; never sum it twice.
        }
        var scope = payload.TryGetProperty("scopeLabel", out var scopeValue) && scopeValue.ValueKind == JsonValueKind.String
            ? scopeValue.GetString() : null;
        return Create(today, totals, string.IsNullOrWhiteSpace(scope) ? "当前监测范围" : scope);
    }

    internal static TrafficHeatmap Create(DateOnly today, IReadOnlyDictionary<DateOnly, double>? totals = null, string scopeLabel = "当前监测范围")
    {
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var start = monday.AddDays(-11 * 7);
        var days = new List<TrafficHeatmapDay>(84);
        for (var week = 0; week < 12; week++)
        for (var weekday = 0; weekday < 7; weekday++)
        {
            var date = start.AddDays(week * 7 + weekday);
            var future = date > today;
            double? total = !future && totals is not null && totals.TryGetValue(date, out var recorded) && double.IsFinite(recorded) && recorded >= 0
                ? recorded : null;
            days.Add(new(date, week, weekday, total, future));
        }
        return new(today, scopeLabel, days);
    }

    internal static string FormatUsage(double totalKiB) => totalKiB switch
    {
        > 0 and < 10.24 => "<0.01 MB",
        >= 1024 * 1024 => $"{totalKiB / (1024 * 1024):0.##} GB",
        _ => $"{totalKiB / 1024:0.##} MB"
    };
}
