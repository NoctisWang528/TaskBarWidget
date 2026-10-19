using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TaskBarWidgets.UI;

public sealed partial class CardGroup
{
    internal static async Task<object> ProbeWeatherCardAsync(CardGroup cards)
    {
        var status = JsonSerializer.SerializeToElement(new
        {
            provider = 1,
            revision = 42,
            stale = false,
            updating = false,
            state = "ready",
            message = "",
            temperature = "23",
            city = new { id = "101190101", name = "南京", region = "南京-江苏" },
            lastSuccessCity = new { id = "101190101", name = "南京" },
            details = new
            {
                current = new { temperature = "23", feelsLike = "22", weather = "晴", conditionKind = "clear", humidity = "55", windDirection = "东北", windScale = "3", windSpeed = "12", observedAt = "2026-10-19 17:00" },
                forecast = new[]
                {
                    new { min = "19", max = "25", weatherDay = "晴", weatherNight = "多云", conditionDayKind = "clear", conditionNightKind = "partly-cloudy" },
                    new { min = "20", max = "26", weatherDay = "多云", weatherNight = "多云", conditionDayKind = "partly-cloudy", conditionNightKind = "partly-cloudy" },
                    new { min = "18", max = "24", weatherDay = "小雨", weatherNight = "小雨", conditionDayKind = "rain", conditionNightKind = "rain" }
                },
                airQuality = new { aqi = "42", category = "优", pm25 = "18", pm10 = "30", publishedAt = "17:00" },
                alerts = new[] { new { typeName = "大风", level = "蓝色", title = "大风蓝色预警", text = "注意户外活动。", publishedAt = "16:00" } },
                capabilities = new { forecast = true, humidity = true, wind = true, airQuality = true, precipitation = false, alerts = true, updatedAt = true }
            }
        });
        cards._quickCardEnabled["weather"] = true;
        cards._weatherModules = new Dictionary<string, bool>(StringComparer.Ordinal)
        { ["current"] = true, ["forecast"] = true, ["humidity"] = true, ["wind"] = true, ["airQuality"] = true, ["precipitation"] = false, ["updatedAt"] = true, ["alerts"] = true };
        cards.RenderWeather(status);
        var card = cards._cardsById["weather"];
        card.MeasureHeight();
        card.Root.UpdateLayout();
        if (cards._weatherTemperature.Text != "23°" || cards._weatherCity.Text != "南京 · 南京-江苏" || cards._weatherForecast.Children.Count != 3)
            throw new InvalidOperationException("Weather card did not render the normalized snapshot.");
        if (cards._weatherForecast.Children.OfType<Grid>().Any(row => row.Children.OfType<ContentPresenter>().Any()))
            throw new InvalidOperationException("Weather forecast unexpectedly used a presenter template.");
        if (cards._weatherPrecipitationBlock.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Missing precipitation module was not hidden.");
        if (cards._weatherAlertsHost.MaxHeight != 140)
            throw new InvalidOperationException("Weather alert scroll limit changed.");
        if (cards._weatherIcon.Width != 96 || cards._weatherIcon.Height != 96)
            throw new InvalidOperationException("Weather hero icon is no longer the enlarged readable size.");
        if (cards._weatherSky.Child is not Grid hero || hero.MinHeight < 160)
            throw new InvalidOperationException("Weather hero no longer reserves space for the enlarged icon.");
        var minimum = JsonSerializer.SerializeToElement(new
        {
            provider = 0, revision = 43, stale = true, updating = false, state = "error", message = "保留上次成功数据。",
            temperature = "22", city = new { id = "101010100", name = "北京", region = "" },
            lastSuccessCity = new { id = "101190101", name = "南京" },
            details = new
            {
                current = new { temperature = "22", weather = "多云", conditionKind = "partly-cloudy", observedAt = "2026-10-19 16:00" },
                forecast = new object[0],
                airQuality = new { pm25 = "20" },
                alerts = new object[0],
                capabilities = new { forecast = false, humidity = false, wind = false, airQuality = true, precipitation = false, alerts = false, updatedAt = true }
            }
        });
        cards.RenderWeather(minimum);
        await Task.Delay(40);
        card.MeasureHeight(); card.Root.UpdateLayout();
        if (cards._weatherForecast.Visibility != Visibility.Collapsed || cards._weatherHumidityBlock.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Unsupported weather modules were not hidden.");
        cards.SetQuickCardEnabled("weather", false);
        if (cards._cardsById.ContainsKey("weather")) throw new InvalidOperationException("Disabled weather card retained its HWND.");
        cards.SetQuickCardEnabled("weather", true);
        if (!cards._cardsById.ContainsKey("weather")) throw new InvalidOperationException("Re-enabled weather card did not recreate its HWND.");
        return new { providerNeutralSnapshot = true, forecastRows = 3, alertScroll = 140, missingModulesHidden = true, staleRetained = true,
            disabledCardWindowReleased = true, reenabledCardWindowRecreated = true, realNetworkTouched = false };
    }
}
