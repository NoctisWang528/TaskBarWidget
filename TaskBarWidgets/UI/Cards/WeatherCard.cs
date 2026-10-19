using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace TaskBarWidgets.UI;

public sealed partial class CardGroup
{
    private const double WeatherCardDesignWidth = 384;
    private const double WeatherAlertMaxHeight = 140;
    private static readonly string[] WeatherDayLabels = ["今天", "明天", "后天"];

    internal Button _weatherRefresh = CreateWeatherRefreshButton();
    internal Grid _weatherContent = new() { MaxWidth = WeatherCardDesignWidth };
    internal TextBlock _weatherTemperature = new() { FontSize = 42, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, MaxLines = 1 };
    internal TextBlock _weatherCity = new() { FontSize = 12, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
    internal TextBlock _weatherStatus = new() { FontSize = 11, MaxLines = 2, TextWrapping = TextWrapping.Wrap };

    private JsonElement _weatherLastStatus;
    private JsonElement _weatherLastDetails;
    private readonly Border _weatherSky = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 12, 16, 12) };
    private readonly Image _weatherIcon = new()
    {
        Width = 96,
        Height = 96,
        Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _weatherCondition = new()
    {
        FontSize = 13,
        MaxLines = 2,
        TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Color.FromArgb(236, 255, 255, 255))
    };
    private readonly TextBlock _weatherCurrentDetail = Text("", 11);
    private readonly TextBlock _weatherUpdated = Text("", 11);
    private readonly TextBlock _weatherHumidity = Text("", 12);
    private readonly TextBlock _weatherWind = Text("", 12);
    private readonly TextBlock _weatherAir = Text("", 12);
    private readonly TextBlock _weatherPrecipitation = Text("", 12);
    private readonly TextBlock _weatherEmpty = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 3 };
    private readonly Button _weatherSettings = new() { Content = "打开天气设置", MinHeight = 32, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _weatherCurrent = new() { Spacing = 2, Margin = new Thickness(0, 0, 0, 8) };
    private readonly StackPanel _weatherForecast = new() { Spacing = 5, Margin = new Thickness(0, 0, 0, 8) };
    private readonly StackPanel _weatherHumidityBlock = new() { Spacing = 2, Margin = new Thickness(0, 0, 0, 8) };
    private readonly StackPanel _weatherWindBlock = new() { Spacing = 2, Margin = new Thickness(0, 0, 0, 8) };
    private readonly StackPanel _weatherAirBlock = new() { Spacing = 2, Margin = new Thickness(0, 0, 0, 8) };
    private readonly StackPanel _weatherPrecipitationBlock = new() { Spacing = 2, Margin = new Thickness(0, 0, 0, 8) };
    private readonly StackPanel _weatherUpdatedBlock = new() { Spacing = 2, Margin = new Thickness(0, 0, 0, 8) };
    private readonly StackPanel _weatherAlerts = new() { Spacing = 6 };
    private readonly ScrollViewer _weatherAlertsHost = new()
    {
        MaxHeight = WeatherAlertMaxHeight,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    private readonly StackPanel _weatherEmptyState = new() { Spacing = 8 };

    private FrameworkElement BuildWeather()
    {
        var root = new StackPanel { Spacing = 10 };
        var header = Columns(new GridLength(1, GridUnitType.Star), GridLength.Auto);
        var heading = new StackPanel { Spacing = 2 };
        var title = Text("天气", 13);
        title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        heading.Children.Add(title);
        Secondary(_weatherCity);
        heading.Children.Add(_weatherCity);
        Add(header, heading, 0);
        Add(header, _weatherRefresh, 1);
        root.Children.Add(header);

        BuildWeatherSky();
        root.Children.Add(_weatherSky);
        root.Children.Add(BuildWeatherEmptyState());
        root.Children.Add(BuildWeatherContent());

        _weatherRefresh.Click += async (_, _) => await RefreshWeatherButtonAsync();
        _weatherSettings.Click += (_, _) => OpenWeatherSettings();
        _weatherSettings.Visibility = Visibility.Collapsed;
        _weatherSky.ActualThemeChanged += (_, _) => RenderWeatherModules();
        AutomationProperties.SetName(_weatherRefresh, "刷新天气");
        AutomationProperties.SetName(_weatherSettings, "打开天气设置");

        _weatherCity.Text = "尚未读取天气";
        _weatherStatus.Visibility = Visibility.Collapsed;
        _weatherCondition.Text = "";
        _weatherTemperature.Text = "";
        _weatherEmpty.Text = "打开天气卡后读取天气。";
        ShowWeatherEmpty(true);
        _weatherSky.Visibility = Visibility.Collapsed;
        return root;
    }

    private void BuildWeatherSky()
    {
        var gradient = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1)
        };
        gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 61, 123, 176), Offset = 0 });
        gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 87, 153, 196), Offset = 0.58 });
        gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 111, 180, 214), Offset = 1 });

        var hero = new Grid { MinHeight = 136 };
        hero.Children.Add(new Rectangle { RadiusX = 8, RadiusY = 8, Fill = gradient });
        var columns = Columns(new GridLength(1, GridUnitType.Star), new GridLength(104));
        var readings = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        _weatherTemperature.Foreground = new SolidColorBrush(Colors.White);
        readings.Children.Add(_weatherTemperature);
        readings.Children.Add(_weatherCondition);
        _weatherStatus.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
        readings.Children.Add(_weatherStatus);
        Add(columns, readings, 0);

        var iconBack = new Grid { Width = 104, Height = 104, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        iconBack.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromArgb(51, 255, 255, 255)) });
        iconBack.Children.Add(_weatherIcon);
        Add(columns, iconBack, 1);
        hero.Children.Add(columns);
        _weatherSky.Child = hero;
    }

    private FrameworkElement BuildWeatherEmptyState()
    {
        Secondary(_weatherEmpty);
        _weatherEmptyState.Children.Add(_weatherEmpty);
        _weatherEmptyState.Children.Add(_weatherSettings);
        return _weatherEmptyState;
    }

    private FrameworkElement BuildWeatherContent()
    {
        for (var row = 0; row < 8; row++)
            _weatherContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Secondary(_weatherCurrentDetail);
        _weatherCurrent.Children.Add(_weatherCurrentDetail);
        AddWeatherModule(_weatherCurrent, 0);

        AddWeatherModule(_weatherForecast, 1);
        AddWeatherField(_weatherHumidityBlock, "湿度", _weatherHumidity, 2);
        AddWeatherField(_weatherWindBlock, "风", _weatherWind, 3);
        AddWeatherField(_weatherAirBlock, "空气质量", _weatherAir, 4);
        AddWeatherField(_weatherPrecipitationBlock, "降水", _weatherPrecipitation, 5);
        AddWeatherField(_weatherUpdatedBlock, "更新时间", _weatherUpdated, 6);

        _weatherAlertsHost.Content = _weatherAlerts;
        AddWeatherModule(_weatherAlertsHost, 7);
        return _weatherContent;
    }

    private void AddWeatherModule(FrameworkElement module, int row)
    {
        Grid.SetRow(module, row);
        _weatherContent.Children.Add(module);
    }

    private void AddWeatherField(StackPanel block, string label, TextBlock value, int row)
    {
        var heading = Text(label, 10);
        Secondary(heading);
        block.Children.Add(heading);
        block.Children.Add(value);
        AddWeatherModule(block, row);
    }

    internal async Task RefreshWeatherAsync()
    {
        if (_disposed || _weatherRefreshing) return;
        _weatherRefreshing = true;
        _weatherRefresh.IsEnabled = false;
        _weatherStatus.Text = "正在刷新天气…";
        _weatherStatus.Visibility = Visibility.Visible;
        try
        {
            var token = _visibleLifetime?.Token ?? CancellationToken.None;
            var reply = await _connection.RequestAsync("weatherCommand", new { operation = "refresh" }, token);
            while (reply.TryGetProperty("pending", out var pending) && pending.ValueKind == JsonValueKind.True)
            {
                if (!reply.TryGetProperty("jobId", out var jobValue) || !jobValue.TryGetInt64(out var jobId) || jobId <= 0)
                    throw new InvalidDataException("天气刷新任务缺少有效编号。");
                await Task.Delay(350, token);
                reply = await _connection.RequestAsync("weatherCommand", new { operation = "result", jobId }, token);
                if (_disposed || (_visibleLifetime is { IsCancellationRequested: true })) return;
            }
            if (_disposed) return;
            if (reply.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object)
                RenderWeather(status);
            else
            {
                _weatherStatus.Text = String(reply, "message", "天气刷新未返回状态。");
                _weatherStatus.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (_disposed) return;
            _weatherStatus.Text = "天气刷新失败：" + error.Message;
            _weatherStatus.Visibility = Visibility.Visible;
        }
        finally
        {
            _weatherRefreshing = false;
            if (!_disposed) _weatherRefresh.IsEnabled = true;
        }
    }

    private Task RefreshWeatherButtonAsync() => RefreshWeatherAsync();

    internal async Task LoadWeatherAsync()
    {
        if (_disposed || !_quickCardEnabled.GetValueOrDefault("weather")) return;
        if (_weatherRefresh.IsEnabled) _weatherRefresh.IsEnabled = false;
        try
        {
            var token = _visibleLifetime?.Token ?? CancellationToken.None;
            var status = await _connection.RequestAsync("weatherCommand", new { operation = "status" }, token);
            if (!_disposed) RenderWeather(status);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!_disposed)
            {
                _weatherStatus.Text = "读取天气失败：" + error.Message;
                _weatherStatus.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            if (!_disposed) _weatherRefresh.IsEnabled = !_weatherRefreshing;
        }
    }

    private void RenderWeather(JsonElement status)
    {
        if (_disposed || status.ValueKind != JsonValueKind.Object) return;
        var revision = (long)Number(status, "revision");
        if (revision > 0 && revision <= _lastWeatherRevision && _weatherLastStatus.ValueKind == JsonValueKind.Object) return;
        if (revision > 0)
        {
            _lastWeatherRevision = revision;
            _lastWeatherSnapshotKey = status.GetRawText();
        }

        _weatherLastStatus = status.Clone();
        _weatherLastDetails = Object(status, "details").Clone();
        var city = Object(status, "city");
        var lastSuccessCity = Object(status, "lastSuccessCity");
        var details = _weatherLastDetails;
        var current = Object(details, "current");
        var shownCity = Bool(status, "stale") && HasValue(String(lastSuccessCity, "name")) ? lastSuccessCity : city;
        var cityName = String(shownCity, "name");
        var region = String(shownCity, "region");
        var hasCity = HasValue(cityName);
        _weatherCity.Text = !hasCity ? "未设置城市" : HasValue(region) && region != cityName ? cityName + " · " + region : cityName;

        var currentTemperature = String(current, "temperature");
        var temperature = FirstNonEmpty(currentTemperature, String(status, "temperature"));
        _weatherTemperature.Text = NormalizeTemperature(temperature);
        _weatherCondition.Text = FirstNonEmpty(String(current, "weather"), String(status, "message"), "天气");
        var isNight = String(current, "observedAt").Length >= 2 &&
            int.TryParse(String(current, "observedAt")[..2], out var observedHour) && (observedHour >= 20 || observedHour < 6);
        _ = ShowWeatherIconAsync(String(current, "conditionKind", "unknown"), isNight);
        _weatherStatus.Text = WeatherStatus(status);
        _weatherStatus.Visibility = HasValue(_weatherStatus.Text) ? Visibility.Visible : Visibility.Collapsed;

        var forecast = Array(details, "forecast").ToArray();
        var alerts = Array(details, "alerts").ToArray();
        var available = details.ValueKind == JsonValueKind.Object &&
            (HasValue(currentTemperature) || HasValue(String(current, "weather")) || forecast.Length > 0 || alerts.Length > 0);
        var showDetails = hasCity && available;
        var showCurrent = showDetails && _weatherModules.GetValueOrDefault("current");
        ShowWeatherEmpty(!showDetails);
        _weatherSky.Visibility = showCurrent ? Visibility.Visible : Visibility.Collapsed;
        if (!showDetails)
        {
            _weatherEmpty.Text = hasCity
                ? FirstNonEmpty(String(status, "message"), "当前天气来源还没有完整数据。可以稍后刷新，或在天气设置中切换来源。")
                : "还没有设置天气城市。设置城市后，这里会显示当前天气和预报。";
            _weatherSettings.Visibility = hasCity ? Visibility.Collapsed : Visibility.Visible;
            _weatherRefresh.IsEnabled = !_weatherRefreshing;
            RefreshWeatherLayout();
            return;
        }

        RenderWeatherModules();
        _weatherSettings.Visibility = Visibility.Collapsed;
        _weatherRefresh.IsEnabled = !_weatherRefreshing && !Bool(status, "updating");
        RefreshWeatherLayout();
    }

    internal void RenderWeatherModules()
    {
        if (_disposed || _weatherLastStatus.ValueKind != JsonValueKind.Object) return;
        var details = _weatherLastDetails.ValueKind == JsonValueKind.Object ? _weatherLastDetails : Object(_weatherLastStatus, "details");
        var current = Object(details, "current");
        var forecast = Array(details, "forecast").Take(3).ToArray();
        var air = Object(details, "airQuality");
        var alerts = Array(details, "alerts").ToArray();

        var feelsLike = String(current, "feelsLike");
        var observedAt = String(current, "observedAt");
        _weatherCurrentDetail.Text = string.Join(" · ", new[]
        {
            HasValue(feelsLike) ? "体感 " + NormalizeTemperature(feelsLike) : "",
            HasValue(observedAt) ? observedAt : ""
        }.Where(HasValue));
        RenderModule("current", _weatherCurrent, true);

        RenderForecast(forecast);
        RenderModule("forecast", _weatherForecast, forecast.Length > 0);

        _weatherHumidity.Text = FormatMeasure(String(current, "humidity"), "%");
        RenderModule("humidity", _weatherHumidityBlock, HasValue(_weatherHumidity.Text));

        var windScale = FormatWindScale(String(current, "windScale"));
        var windSpeed = String(current, "windSpeed");
        _weatherWind.Text = string.Join(" · ", new[]
        {
            String(current, "windDirection"),
            windScale,
            HasValue(windSpeed) ? windSpeed + (windSpeed.EndsWith("km/h", StringComparison.OrdinalIgnoreCase) ? "" : " km/h") : ""
        }.Where(HasValue));
        RenderModule("wind", _weatherWindBlock, HasValue(_weatherWind.Text));

        _weatherAir.Text = AirQualityText(air);
        RenderModule("airQuality", _weatherAirBlock, HasValue(_weatherAir.Text));

        // Precipitation is not included in the current status payload.
        _weatherPrecipitation.Text = "";
        RenderModule("precipitation", _weatherPrecipitationBlock, false);

        _weatherUpdated.Text = FirstNonEmpty(String(current, "observedAt"), String(_weatherLastStatus, "lastSuccess"));
        RenderModule("updatedAt", _weatherUpdatedBlock, HasValue(_weatherUpdated.Text));
        RenderAlerts(alerts);
        RefreshWeatherLayout();
    }

    private void RenderForecast(JsonElement[] forecast)
    {
        _weatherForecast.Children.Clear();
        for (var index = 0; index < forecast.Length; index++)
        {
            var day = forecast[index];
            var row = Columns(new GridLength(44), new GridLength(26), new GridLength(1, GridUnitType.Star), GridLength.Auto);
            row.ColumnSpacing = 8;
            row.MinHeight = 27;

            var label = Text(WeatherDayLabels[index], 12);
            label.VerticalAlignment = VerticalAlignment.Center;
            Add(row, label, 0);

            var icon = new Image { Width = 24, Height = 24, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
            _ = SetIconAsync(icon, String(day, "conditionDayKind", "unknown"), clear: false);
            Add(row, icon, 1);

            var weather = Text(FirstNonEmpty(String(day, "weatherDay"), String(day, "weatherNight"), "—"), 12);
            weather.VerticalAlignment = VerticalAlignment.Center;
            Add(row, weather, 2);

            var range = Text(TemperatureRange(day), 12);
            range.HorizontalAlignment = HorizontalAlignment.Right;
            range.VerticalAlignment = VerticalAlignment.Center;
            Add(row, range, 3);
            _weatherForecast.Children.Add(row);
        }
    }

    private void RenderAlerts(JsonElement[] alerts)
    {
        _weatherAlerts.Children.Clear();
        foreach (var alert in alerts)
        {
            var block = new StackPanel { Spacing = 2 };
            var type = FirstNonEmpty(String(alert, "typeName"), String(alert, "type"), "天气预警");
            var level = String(alert, "level");
            var title = Text(HasValue(level) ? "[" + level + "] " + type : type, 12);
            title.TextWrapping = TextWrapping.Wrap;
            title.MaxLines = 3;
            title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            block.Children.Add(title);

            var text = Text(String(alert, "text"), 11);
            text.TextWrapping = TextWrapping.Wrap;
            text.MaxLines = 8;
            Secondary(text);
            block.Children.Add(text);
            _weatherAlerts.Children.Add(block);
        }
        _weatherAlertsHost.Content = _weatherAlerts;
        var enabled = _weatherModules.GetValueOrDefault("alerts");
        _weatherAlertsHost.Visibility = enabled && alerts.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderModule(string id, FrameworkElement module, bool hasContent)
    {
        var visible = hasContent && _weatherModules.GetValueOrDefault(id);
        module.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        module.Opacity = visible ? 1 : 0;
    }

    private void ShowWeatherEmpty(bool empty)
    {
        _weatherEmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _weatherContent.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenWeatherSettings()
    {
        Hide("weather-settings");
        _openSettings("quickCardsWeather");
    }

    private static Button CreateWeatherRefreshButton() => new()
    {
        Width = 32,
        Height = 32,
        MinWidth = 0,
        Padding = new Thickness(4),
        CornerRadius = new CornerRadius(16),
        Content = new FontIcon { Glyph = "\uE72C", FontSize = 16 }
    };

    private static JsonElement Object(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Object ? item : default;

    private static string WeatherStatus(JsonElement status)
    {
        if (status.ValueKind != JsonValueKind.Object) return "";
        var state = String(status, "state");
        var message = String(status, "message");
        var lastCity = String(Object(status, "lastSuccessCity"), "name");
        var parts = new List<string>();
        if (Bool(status, "updating")) parts.Add("正在更新");
        if (Bool(status, "stale"))
            parts.Add(HasValue(lastCity) ? "上次成功城市：" + lastCity : "当前显示上次成功数据");
        if (state == "error" && HasValue(message)) parts.Add(message);
        return string.Join(" · ", parts.Distinct(StringComparer.Ordinal));
    }

    private static string AirQualityText(JsonElement air)
    {
        if (air.ValueKind != JsonValueKind.Object) return "";
        var category = String(air, "category");
        var aqi = String(air, "aqi");
        if (HasValue(aqi))
            return string.Join(" · ", new[] { "AQI " + aqi, category }.Where(HasValue));
        var pm25 = String(air, "pm25");
        if (HasValue(pm25)) return "PM2.5 " + pm25;
        var pm10 = String(air, "pm10");
        if (HasValue(pm10)) return "PM10 " + pm10;
        return category;
    }

    private static string NormalizeTemperature(string value)
    {
        if (!HasValue(value)) return "";
        if (value.Contains('℃', StringComparison.Ordinal) || value.EndsWith('°')) return value;
        return value + "°";
    }

    private static string FormatMeasure(string value, string suffix)
    {
        if (!HasValue(value)) return "";
        return value.EndsWith(suffix, StringComparison.Ordinal) ? value : value + suffix;
    }

    private static string FormatWindScale(string value) =>
        !HasValue(value) || value.Contains('级', StringComparison.Ordinal) ? value : value + "级";

    private static string TemperatureRange(JsonElement day)
    {
        var min = FormatMeasure(String(day, "min"), "°");
        var max = FormatMeasure(String(day, "max"), "°");
        if (!HasValue(min)) return max;
        if (!HasValue(max)) return min;
        return min + "~" + max;
    }

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(HasValue) ?? "";
    private static bool HasValue(string? value) => !string.IsNullOrWhiteSpace(value) && value != "--";

    private Task ShowWeatherIconAsync(string kind, bool clear)
    {
        var cacheKey = kind + (clear ? ":night" : ":day");
        var cached = _weatherIconCache.GetValueOrDefault(cacheKey);
        if (cached is not null) { _weatherIcon.Source = new SvgImageSource { UriSource = new Uri(cached) }; return Task.CompletedTask; }
        var path = WeatherIconPath(kind, clear);
        try
        {
            var image = new SvgImageSource { UriSource = new Uri(path) };
            if (_disposed) return Task.CompletedTask;
            _weatherIconCache[cacheKey] = path;
            _weatherIcon.Source = image;
        }
        catch (Exception error) when (error is IOException or ArgumentException or System.Runtime.InteropServices.COMException) { }
        return Task.CompletedTask;
    }

    private Task SetIconAsync(Image image, string kind, bool clear)
    {
        try
        {
            var bitmap = new SvgImageSource { UriSource = new Uri(WeatherIconPath(kind, clear)) };
            if (!_disposed) image.Source = bitmap;
        }
        catch (Exception error) when (error is IOException or ArgumentException or System.Runtime.InteropServices.COMException) { }
        return Task.CompletedTask;
    }

    private static string WeatherIconPath(string kind, bool clear)
    {
        var name = kind.ToLowerInvariant() switch
        {
            "clear" => clear ? "clear-night" : "clear-day",
            "partly-cloudy" => clear ? "partly-cloudy-night" : "partly-cloudy-day",
            "overcast" => clear ? "overcast-night" : "overcast-day",
            "rain" => "rain",
            "drizzle" => "drizzle",
            "snow" => "snow",
            "sleet" => "sleet",
            "fog" => clear ? "fog-night" : "fog-day",
            "haze" => clear ? "haze-night" : "haze-day",
            "dust" => clear ? "dust-night" : "dust-day",
            "thunder" => clear ? "thunderstorms-night" : "thunderstorms-day",
            "wind" => "wind",
            _ => "cloudy"
        };
        return "ms-appx:///Assets/WeatherIcons/" + name + ".svg";
    }
}
