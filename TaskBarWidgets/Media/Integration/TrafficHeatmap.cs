using System.Globalization;
using System.Text.Json;

namespace TaskBarWidgets.Media.Integration;

internal sealed record TrafficHeatmapDay(DateOnly Date, int Week, int Weekday, double? TotalKiB, bool IsFuture)
{
    // Future, missing, known zero, then four fixed usage bands.
    internal int Level => IsFuture ? -1 : TotalKiB switch
    {
        null => 0,
        0 => 1,
        < 100 * 1024 => 2,
        < 1024 * 1024 => 3,
        < 5 * 1024 * 1024 => 4,
        _ => 5
    };

    internal string Description => $"{Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} · {(IsFuture ? "未来日期" : TotalKiB is { } total ? TrafficHeatmap.FormatUsage(total) : "没有记录")}";
}

internal sealed record TrafficHeatmap(DateOnly Today, string ScopeLabel, IReadOnlyList<TrafficHeatmapDay> Days)
{
    internal double TotalKiB => Days.Sum(day => day.TotalKiB ?? 0);

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
