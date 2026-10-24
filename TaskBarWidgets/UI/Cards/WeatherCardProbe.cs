using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
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
            temperature = "18.3",
            city = new { id = "101190101", name = "南京", region = "南京-江苏" },
            lastSuccessCity = new { id = "101190101", name = "南京" },
            details = new
            {
                current = new { temperature = "18.3", feelsLike = "22", weather = "多云", conditionKind = "partly-cloudy", humidity = "55", windDirection = "东北", windScale = "3", windSpeed = "12", observedAt = "2026-10-19 17:00" },
                forecast = new[]
                {
                    new { min = "19", max = "25", weatherDay = "晴", weatherNight = "多云", conditionDayKind = "clear", conditionNightKind = "partly-cloudy" },
                    new { min = "20", max = "26", weatherDay = "多云", weatherNight = "多云", conditionDayKind = "partly-cloudy", conditionNightKind = "partly-cloudy" },
                    new { min = "18", max = "24", weatherDay = "小雨", weatherNight = "小雨", conditionDayKind = "rain", conditionNightKind = "rain" },
                    new { min = "17", max = "23", weatherDay = "多云", weatherNight = "多云", conditionDayKind = "partly-cloudy", conditionNightKind = "partly-cloudy" },
                    new { min = "16", max = "22", weatherDay = "阴", weatherNight = "阴", conditionDayKind = "overcast", conditionNightKind = "overcast" }
                },
                airQuality = new { aqi = "42", indexCode = "cn-mee", indexName = "AQI (CN)", category = "优", pm25 = "0", pm10 = "30", pm25Unit = "µg/m³", pm10Unit = "µg/m³", publishedAt = "", attribution = "合成归因：和风天气 https://www.qweather.com/\n合成空气来源" },
                alertsAttribution = "合成归因：和风天气 https://www.qweather.com/",
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
        if (cards._weatherTemperature.Text != "18.3°" || cards._weatherCity.Text != "南京")
            throw new InvalidOperationException("Weather card did not render the normalized current snapshot.");
        if (cards._weatherAttribution.Visibility != Visibility.Visible ||
            cards._weatherAttribution.Text != "合成归因：和风天气 https://www.qweather.com/\n合成空气来源" ||
            cards._weatherAttribution.TextWrapping != TextWrapping.Wrap || cards._weatherStatus.Text.Contains("和风天气"))
            throw new InvalidOperationException("Alert attribution was missing, unwrapped, or treated as an error.");
        var airTooltip = ToolTipService.GetToolTip(cards._weatherAirQuality) as string ?? "";
        if (cards._weatherAirQuality.Text != "空气质量 · AQI (CN) 42 · 优" ||
            !airTooltip.Contains("PM2.5 0 µg/m³") || !airTooltip.Contains("PM10 30 µg/m³") || airTooltip.Contains("17:00") ||
            cards._weatherAirQuality.TextWrapping != TextWrapping.Wrap)
            throw new InvalidOperationException("Air quality lost its standard, zero, units or invented an observation time.");
        cards.RenderAirQuality(JsonSerializer.SerializeToElement(new { aqi = "0", indexCode = "cn-mee" }));
        if (cards._weatherAirQuality.Text != "空气质量 · cn-mee 0" || ToolTipService.GetToolTip(cards._weatherAirQuality) is not null)
            throw new InvalidOperationException("AQI zero/code fallback or tooltip cleanup failed.");
        cards.RenderAirQuality(Object(Object(status, "details"), "airQuality"));
        var firstAirFailure = JsonSerializer.SerializeToElement(new { details = new { airQuality = new { error = "空气质量失败：尚无上次数据" } } });
        if (WeatherStatus(firstAirFailure) != "空气质量失败：尚无上次数据")
            throw new InvalidOperationException("First air failure invented retained data instead of preserving provider explanation.");
        if (cards._weatherForecast.Children.Count != 5 || cards._weatherForecast.RowDefinitions.Count != 5)
            throw new InvalidOperationException("Weather card did not reserve the five-row Apple-style forecast.");
        if (cards._weatherIcon is not Viewbox { Width: 42, Height: 42 })
            throw new InvalidOperationException("Weather current icon is not the compact hero size.");
        var lightColors = IconColors(cards._weatherIcon);
        if (lightColors.Count < 3)
            throw new InvalidOperationException("Weather icon is not a multi-color symbol.");
        AssertTemperatureFitsColumn(cards, "A one-decimal temperature does not fit its column.");

        card.Root.RequestedTheme = ElementTheme.Dark;
        await Task.Delay(120);
        card.Root.UpdateLayout();
        var darkColors = IconColors(cards._weatherIcon);
        if (darkColors.Count < 3 || darkColors.SetEquals(lightColors))
            throw new InvalidOperationException("The weather icon did not switch to the dark palette.");
        card.Root.RequestedTheme = ElementTheme.Light;
        await Task.Delay(120);
        card.Root.UpdateLayout();
        AssertTemperatureFitsColumn(cards, "The weather temperature stopped fitting after a theme change.");

        var integerStatus = JsonNode.Parse(status.GetRawText())!.AsObject();
        integerStatus["revision"] = 43;
        integerStatus["details"]!["current"]!["temperature"] = "23";
        cards.RenderWeather(JsonSerializer.SerializeToElement(integerStatus));
        await Task.Delay(60);
        card.Root.UpdateLayout();
        if (cards._weatherTemperature.Text != "23°" || cards._weatherTemperature.FontSize != 48)
            throw new InvalidOperationException("A short temperature did not keep the full hero size.");
        AssertTemperatureFitsColumn(cards, "A short temperature does not fit its column.");

        if (cards._weatherTemperature.FontFamily.Source != "Microsoft YaHei UI")
            throw new InvalidOperationException("Weather text did not use the configured card font.");
        if (cards._weatherAttribution.FontFamily.Source != cards._weatherTemperature.FontFamily.Source ||
            cards._weatherAirQuality.FontFamily.Source != cards._weatherTemperature.FontFamily.Source)
            throw new InvalidOperationException("Alert attribution did not follow the card font.");
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
                airQuality = new { pm25 = "20", pm25Unit = "µg/m³", error = "空气质量获取失败：合成错误；上次数据可能过时" },
                alertsError = "预警获取失败：合成错误",
                alertsAttribution = "合成归因：和风天气 https://www.qweather.com/",
                alerts = new object[0],
                capabilities = new { forecast = false, humidity = false, wind = false, airQuality = true, precipitation = false, alerts = false, updatedAt = true }
            }
        });
        cards.RenderWeather(minimum);
        await Task.Delay(40);
        card.MeasureHeight();
        card.Root.UpdateLayout();
        if (!cards._weatherStatus.Text.Contains("预警获取失败：合成错误") ||
            !cards._weatherStatus.Text.Contains("空气质量获取失败：合成错误") || !cards._weatherStatus.Text.Contains("可能过时") ||
            !cards._weatherStatus.Text.Contains("上次成功数据（南京）") ||
            cards._weatherStatus.Visibility != Visibility.Visible || cards._weatherAttribution.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Zero alerts hid the alert failure, retained-data explanation, or attribution.");
        if (cards._weatherAirQuality.Text != "空气质量 · PM2.5 20 µg/m³" || cards._weatherAirQuality.Visibility != Visibility.Visible ||
            cards._weatherAttribution.Text != "合成归因：和风天气 https://www.qweather.com/")
            throw new InvalidOperationException("Pollutant-only air quality was hidden or an AQI was invented.");
        if (cards._weatherForecast.Children.Count != 0 || cards._weatherAlerts.Children.Count != 0 ||
            cards._weatherAlertPanel.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Missing forecast data was not hidden and zero alerts did not leave the lower-left area blank.");

        var gradientStatus = JsonSerializer.SerializeToElement(new
        {
            revision = 46,
            city = new { name = "南京" },
            details = new
            {
                current = new { temperature = "20", weather = "多云", conditionKind = "partly-cloudy" },
                airQuality = new { },
                forecast = new[]
                {
                    new { min = "19", max = "25", conditionDayKind = "clear" },
                    new { min = "19", max = "25", conditionDayKind = "clear" },
                    new { min = "-30", max = "-25", conditionDayKind = "clear" },
                    new { min = "31", max = "37", conditionDayKind = "clear" },
                    new { min = "5", max = "5", conditionDayKind = "clear" }
                },
                alerts = System.Array.Empty<object>()
            }
        });
        cards.RenderWeatherAttribution(new[] { "https://developer.qweather.com/attribution.html", "https://developer.qweather.com/attribution.html" });
        var attributionLinks = cards._weatherAttribution.Inlines.OfType<Microsoft.UI.Xaml.Documents.Hyperlink>().ToArray();
        if (attributionLinks.Length != 2 || attributionLinks[0].NavigateUri.AbsoluteUri != "https://www.qweather.com/" ||
            attributionLinks[1].NavigateUri.AbsoluteUri != "https://developer.qweather.com/attribution.html" ||
            attributionLinks[1].Inlines.OfType<Microsoft.UI.Xaml.Documents.Run>().Single().Text != "数据来源")
            throw new InvalidOperationException("Weather attribution did not render compact named links.");
        cards.RenderWeather(gradientStatus);
        if (cards._weatherStatus.Visibility != Visibility.Collapsed || cards._weatherAttribution.Visibility != Visibility.Collapsed ||
            cards._weatherAirQuality.Visibility != Visibility.Collapsed || cards._weatherAirQuality.Text.Length != 0 || cards._weatherAttribution.Text.Length != 0)
            throw new InvalidOperationException("Recovered weather retained an old alert error or attribution.");
        await Task.Delay(40);
        card.Root.UpdateLayout();

        var firstRange = ForecastGradientColors(cards, 0);
        var repeatedRange = ForecastGradientColors(cards, 1);
        var hotRange = ForecastGradientColors(cards, 3);
        if (!firstRange.SequenceEqual(repeatedRange) ||
            firstRange[0] != TemperatureColor(19).ToString() ||
            firstRange[1] != TemperatureColor(25).ToString() ||
            hotRange[0] != TemperatureColor(31).ToString() ||
            hotRange[1] != TemperatureColor(37).ToString() ||
            hotRange[0] == hotRange[1])
            throw new InvalidOperationException("Forecast temperature gradients do not follow the absolute temperature scale.");
        if (ForecastFill(cards, 2).Background is not SolidColorBrush ||
            ForecastFill(cards, 4).Background is not SolidColorBrush)
            throw new InvalidOperationException("Equivalent or clamped forecast temperatures did not use a solid color.");
        if (!SameColor(TemperatureColor(-30), TemperatureColor(-20)) ||
            !SameColor(TemperatureColor(50), TemperatureColor(40)) ||
            SameColor(TemperatureColor(0), TemperatureColor(5)) ||
            SameColor(TemperatureColor(5), TemperatureColor(10)))
            throw new InvalidOperationException("Weather temperature color interpolation or clamping is incorrect.");

        var missingMinimum = JsonNode.Parse(gradientStatus.GetRawText())!.AsObject();
        missingMinimum["revision"] = 47;
        ((JsonObject)((JsonArray)missingMinimum["details"]!["forecast"]!)[0]!).Remove("min");
        cards.RenderWeather(JsonSerializer.SerializeToElement(missingMinimum));
        if (ForecastFill(cards, 0).Background is not null)
            throw new InvalidOperationException("A forecast row missing its minimum temperature retained a colored fill.");

        // An incomplete provider snapshot is the empty state users actually see; it must
        // offer a retry and report the outcome there instead of only printing a sentence.
        var incomplete = JsonSerializer.SerializeToElement(new
        {
            provider = 1,
            revision = 48,
            stale = false,
            updating = false,
            state = "error",
            message = "中国天气网未返回完整的天气数据。",
            temperature = "--",
            city = new { id = "101190101", name = "南京", region = "" },
            details = new { current = new { }, forecast = System.Array.Empty<object>(), alerts = System.Array.Empty<object>() }
        });
        cards.RenderWeather(incomplete);
        await Task.Delay(40);
        card.MeasureHeight();
        card.Root.UpdateLayout();
        if (cards._weatherEmptyState.Visibility != Visibility.Visible || cards._weatherContent.Visibility != Visibility.Collapsed ||
            cards._weatherEmpty.Text != "中国天气网未返回完整的天气数据。")
            throw new InvalidOperationException("An incomplete weather snapshot did not show the provider message in the empty state.");
        if (cards._weatherEmptyRefresh.Visibility != Visibility.Visible || !cards._weatherEmptyRefresh.IsEnabled ||
            cards._weatherSettings.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("The weather empty state did not expose an enabled refresh action.");
        var emptyOrigin = cards._weatherEmptyRefresh.TransformToVisual(card.Root).TransformPoint(new Windows.Foundation.Point());
        if (cards._weatherEmptyRefresh.ActualWidth <= 0 || cards._weatherEmptyRefresh.ActualHeight <= 0 ||
            emptyOrigin.X < -0.5 || emptyOrigin.Y < -0.5 ||
            emptyOrigin.X + cards._weatherEmptyRefresh.ActualWidth > card.Root.ActualWidth + 0.5 ||
            emptyOrigin.Y + cards._weatherEmptyRefresh.ActualHeight > card.Root.ActualHeight + 0.5)
            throw new InvalidOperationException("The weather empty-state refresh action was laid out outside the card bounds.");
        var emptyImagePath = Path.Combine(outputDirectory, "weather-card-empty.png");
        await UiLayoutProbe.CaptureAsync(card.Root, emptyImagePath);
        images.Add(new { theme = "Empty", imagePath = emptyImagePath });
        var emptyLabel = cards._weatherEmpty.Text;
        var emptyPeer = FrameworkElementAutomationPeer.CreatePeerForElement(cards._weatherEmptyRefresh) ?? new ButtonAutomationPeer(cards._weatherEmptyRefresh);
        ((IInvokeProvider)emptyPeer.GetPattern(PatternInterface.Invoke)).Invoke();
        // The probe has no media backend, so this retry must fail visibly in the empty
        // state rather than silently doing nothing.
        for (var attempt = 0; attempt < 300 &&
            (cards._weatherRefreshing || cards._weatherEmpty.Text == emptyLabel); attempt++) await Task.Delay(10);
        if (!cards._weatherEmpty.Text.StartsWith("天气刷新失败：", StringComparison.Ordinal))
            throw new InvalidOperationException("A failed weather retry did not report into the empty state.");
        if (!cards._weatherEmptyRefresh.IsEnabled)
            throw new InvalidOperationException("The weather retry action stayed disabled after failing.");

        cards.SetQuickCardEnabled("weather", false);
        if (cards._cardsById.ContainsKey("weather")) throw new InvalidOperationException("Disabled weather card retained its HWND.");
        cards.SetQuickCardEnabled("weather", true);
        if (!cards._cardsById.ContainsKey("weather")) throw new InvalidOperationException("Re-enabled weather card did not recreate its HWND.");
        return new
        {
            providerNeutralSnapshot = true,
            airQualitySnapshot = true,
            airQualityErrorsAndRecovery = true,
            airQualityAttribution = true,
            images,
            forecastRows = 5,
            alertRotation = true,
            allAlertsRetained = true,
            singleAlertFixed = true,
            zeroAlertsBlank = true,
            missingDataHidden = true,
            micaSurface = true,
            staleRetained = true,
            temperatureGradient = true,
            emptyStateRefresh = true,
            disabledCardWindowReleased = true,
            reenabledCardWindowRecreated = true,
            realNetworkTouched = false
        };
    }

    private static Border ForecastFill(CardGroup cards, int row)
    {
        var rowGrid = (Grid)cards._weatherForecast.Children[row];
        var track = rowGrid.Children.OfType<Border>().Single(child => Grid.GetColumn(child) == 3);
        var range = track.Child as Grid
            ?? throw new InvalidOperationException("Forecast temperature track has no range host.");
        return range.Children.OfType<Border>().Single();
    }

    private static string[] ForecastGradientColors(CardGroup cards, int row)
    {
        if (ForecastFill(cards, row).Background is not LinearGradientBrush gradient)
            throw new InvalidOperationException("Forecast temperature range is not a gradient.");
        var colors = gradient.GradientStops.OrderBy(stop => stop.Offset).Select(stop => stop.Color.ToString()).ToArray();
        if (colors.Length != 2) throw new InvalidOperationException("Forecast temperature gradient does not have two endpoints.");
        return colors;
    }

    private static bool SameColor(Windows.UI.Color left, Windows.UI.Color right) =>
        left.A == right.A && left.R == right.R && left.G == right.G && left.B == right.B;

    private static HashSet<string> IconColors(FrameworkElement icon)
    {
        var colors = new HashSet<string>(StringComparer.Ordinal);
        void Walk(DependencyObject node)
        {
            switch (node)
            {
                case Microsoft.UI.Xaml.Shapes.Path path:
                    if (path.Fill is SolidColorBrush fill) colors.Add(fill.Color.ToString());
                    if (path.Stroke is SolidColorBrush stroke) colors.Add(stroke.Color.ToString());
                    break;
                case Microsoft.UI.Xaml.Shapes.Ellipse ellipse when ellipse.Fill is SolidColorBrush ellipseFill:
                    colors.Add(ellipseFill.Color.ToString());
                    break;
                case Microsoft.UI.Xaml.Shapes.Rectangle rectangle when rectangle.Fill is SolidColorBrush rectangleFill:
                    colors.Add(rectangleFill.Color.ToString());
                    break;
            }
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
                Walk(VisualTreeHelper.GetChild(node, index));
        }
        Walk(icon);
        return colors;
    }

    // TextBlock reports its layout slot, not the width of the glyphs, so the rendered width
    // is measured with a detached copy that mirrors the live font and text.
    private static void AssertTemperatureFitsColumn(CardGroup cards, string message)
    {
        var live = cards._weatherTemperature;
        var available = cards._weatherTemperatureColumn.ActualWidth;
        if (available <= 0) throw new InvalidOperationException("The temperature column has no width.");
        var probe = new TextBlock
        {
            FontFamily = live.FontFamily,
            FontSize = live.FontSize,
            FontWeight = live.FontWeight,
            MaxLines = 1,
            Text = live.Text
        };
        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        if (probe.DesiredSize.Width > available + 0.5) throw new InvalidOperationException(message);
        if (live.FontSize < 26 || live.FontSize > 48)
            throw new InvalidOperationException("The temperature font size left its supported range.");
    }
}
