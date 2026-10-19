using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TaskBarWidgets.UI;

public sealed partial class CardGroup
{
    internal static async Task<object> ProbeWeatherCardAsync(CardGroup cards, string outputDirectory)
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
                    new { min = "18", max = "24", weatherDay = "小雨", weatherNight = "小雨", conditionDayKind = "rain", conditionNightKind = "rain" },
                    new { min = "17", max = "23", weatherDay = "多云", weatherNight = "多云", conditionDayKind = "partly-cloudy", conditionNightKind = "partly-cloudy" },
                    new { min = "16", max = "22", weatherDay = "阴", weatherNight = "阴", conditionDayKind = "overcast", conditionNightKind = "overcast" }
                },
                airQuality = new { aqi = "42", category = "优", pm25 = "18", pm10 = "30", publishedAt = "17:00" },
                alerts = new[]
                {
                    new { typeName = "大风", level = "蓝色", title = "大风蓝色预警", text = "注意户外活动。", publishedAt = "16:00" },
                    new { typeName = "暴雨", level = "黄色", title = "暴雨黄色预警", text = "注意交通安全。", publishedAt = "15:30" }
                },
                capabilities = new { forecast = true, humidity = true, wind = true, airQuality = true, precipitation = false, alerts = true, updatedAt = true }
            }
        });
        Directory.CreateDirectory(outputDirectory);
        cards._quickCardEnabled["weather"] = true;
        cards.RenderWeather(status);
        var card = cards._cardsById["weather"];
        card.MeasureHeight();
        card.Root.UpdateLayout();
        var images = new List<object>();
        foreach (var theme in new[] { ElementTheme.Dark, ElementTheme.Light })
        {
            card.Root.RequestedTheme = theme;
            await Task.Delay(100);
            card.Root.UpdateLayout();
            var imagePath = Path.Combine(outputDirectory, $"weather-card-{theme.ToString().ToLowerInvariant()}.png");
            await UiLayoutProbe.CaptureAsync(card.Root, imagePath);
            images.Add(new { theme = theme.ToString(), imagePath });
        }
        if (cards._weatherTemperature.Text != "23°" || cards._weatherCity.Text != "南京")
            throw new InvalidOperationException("Weather card did not render the normalized current snapshot.");
        if (cards._weatherForecast.Children.Count != 5 || cards._weatherForecast.RowDefinitions.Count != 5)
            throw new InvalidOperationException("Weather card did not reserve the five-row Apple-style forecast.");
        if (cards._weatherIcon.Width != 42 || cards._weatherIcon.Height != 42)
            throw new InvalidOperationException("Weather current icon is not the compact hero size.");
        if (cards._weatherIcon.Glyph != "\uF101")
            throw new InvalidOperationException("Daytime clear weather did not use the daytime icon.");
        if (cards._weatherAlertsHost.MaxHeight != 54)
            throw new InvalidOperationException("Weather alert scroll limit changed.");
        if (cards._weatherAlerts.Children.Count != 2)
            throw new InvalidOperationException("Weather card did not retain all supplied alerts.");
        if (cards._weatherAlerts.Children.Count(child => child.Visibility == Visibility.Visible) != 1 ||
            !cards._weatherAlertTimer.IsEnabled)
            throw new InvalidOperationException("Multiple weather alerts did not switch to one-at-a-time rotation.");
        await Task.Delay(4300);
        if (cards._weatherAlertIndex != 1 ||
            cards._weatherAlerts.Children.Count(child => child.Visibility == Visibility.Visible) != 1)
            throw new InvalidOperationException("Weather alert rotation did not advance to the next alert.");
        if (cards._weatherContent.ActualWidth > CardGroup.CardWidth - 32 || card.Window.SystemBackdrop is null)
            throw new InvalidOperationException("Weather content overflows the Mica card surface.");

        var singleAlert = JsonNode.Parse(status.GetRawText())!.AsObject();
        var singleAlerts = (JsonArray)singleAlert["details"]!["alerts"]!;
        singleAlerts.RemoveAt(singleAlerts.Count - 1);
        singleAlert["revision"] = 44;
        cards.RenderWeather(JsonSerializer.SerializeToElement(singleAlert));
        if (cards._weatherAlerts.Children.Count != 1 ||
            cards._weatherAlertsHost.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled ||
            cards._weatherAlertTimer.IsEnabled)
            throw new InvalidOperationException("A single weather alert must remain fixed without scrolling.");

        var minimum = JsonSerializer.SerializeToElement(new
        {
            provider = 0,
            revision = 45,
            stale = true,
            updating = false,
            state = "error",
            message = "保留上次成功数据。",
            temperature = "22",
            city = new { id = "101010100", name = "北京", region = "" },
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
        card.MeasureHeight();
        card.Root.UpdateLayout();
        if (cards._weatherForecast.Children.Count != 0 || cards._weatherAlerts.Children.Count != 0 ||
            cards._weatherAlertPanel.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Missing forecast data was not hidden and zero alerts did not leave the lower-left area blank.");

        cards.SetQuickCardEnabled("weather", false);
        if (cards._cardsById.ContainsKey("weather")) throw new InvalidOperationException("Disabled weather card retained its HWND.");
        cards.SetQuickCardEnabled("weather", true);
        if (!cards._cardsById.ContainsKey("weather")) throw new InvalidOperationException("Re-enabled weather card did not recreate its HWND.");
        return new
        {
            providerNeutralSnapshot = true,
            images,
            forecastRows = 5,
            alertRotation = true,
            allAlertsRetained = true,
            singleAlertFixed = true,
            zeroAlertsBlank = true,
            missingDataHidden = true,
            micaSurface = true,
            staleRetained = true,
            disabledCardWindowReleased = true,
            reenabledCardWindowRecreated = true,
            realNetworkTouched = false
        };
    }
}
