using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TaskBarWidgets.UI;

public sealed partial class CardGroup
{
    private const double WeatherCardDesignWidth = CardWidth - 32;
    private const double WeatherCardDesignHeight = 176;
    private const double WeatherAlertMaxHeight = 54;
    private const int WeatherForecastRows = 5;
    private static readonly string[] WeatherDayLabels = ["今天", "明天", "后天", "周四", "周五"];

    internal Button _weatherRefresh = CreateWeatherRefreshButton();
    internal Grid _weatherContent = new()
    {
        Width = WeatherCardDesignWidth,
        Height = WeatherCardDesignHeight,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };
    internal TextBlock _weatherTemperature = new()
    {
        FontFamily = WeatherFontFamily,
        FontSize = 48,
        FontWeight = Microsoft.UI.Text.FontWeights.Light,
        LineHeight = 52,
        MaxLines = 1
    };
    internal TextBlock _weatherCity = new()
    {
        FontFamily = WeatherFontFamily,
        FontSize = 15,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        MaxLines = 1,
        TextTrimming = TextTrimming.CharacterEllipsis
    };
    internal TextBlock _weatherStatus = new()
    {
        FontFamily = WeatherFontFamily,
        FontSize = 11,
        MaxLines = 1,
        TextWrapping = TextWrapping.Wrap
    };

    private JsonElement _weatherLastStatus;
    private JsonElement _weatherLastDetails;
    private readonly DispatcherTimer _weatherAlertTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private int _weatherAlertIndex;
    private readonly Grid _weatherLeft = new()
    {
        Margin = new Thickness(0, 0, 12, 0),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch
    };
    private readonly StackPanel _weatherLocationRow = new()
    {
        Spacing = 6,
        MinHeight = 28,
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly StackPanel _weatherRight = new()
    {
        Spacing = 5,
        Margin = new Thickness(12, 0, 0, 0),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Grid _weatherMain = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch
    };
    private readonly Grid _weatherForecast = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Grid _weatherAlertPanel = new() { VerticalAlignment = VerticalAlignment.Bottom };
    private readonly StackPanel _weatherAlerts = new() { Spacing = 4 };
    private readonly ScrollViewer _weatherAlertsHost = new()
    {
        MaxHeight = WeatherAlertMaxHeight,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    private readonly TextBlock _weatherCondition = new()
    {
        FontFamily = WeatherFontFamily,
        FontSize = 13,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        MaxLines = 2,
        TextWrapping = TextWrapping.Wrap,
        TextTrimming = TextTrimming.CharacterEllipsis
    };
    private readonly FontIcon _weatherIcon = CreateWeatherIcon(42);
    private readonly Grid _weatherCurrentRow = new()
    {
        MinHeight = 58,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly StackPanel _weatherSummary = new() { Spacing = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Grid _weatherIconHost = new()
    {
        Width = 42,
        Height = 42,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly StackPanel _weatherEmptyState = new() { Spacing = 8 };
    private readonly TextBlock _weatherEmpty = new()
    {
        FontFamily = WeatherFontFamily,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        MaxLines = 3
    };
    private readonly Button _weatherSettings = new() { Content = "打开天气设置", MinHeight = 32, HorizontalAlignment = HorizontalAlignment.Left };

    private static FontFamily WeatherFontFamily => new("Microsoft YaHei UI");

    private static readonly FontFamily WeatherIconFont =
        new("ms-appx:///Assets/QWeatherIcons/qweather-icons.ttf#qweather-icons");

    private FrameworkElement BuildWeather()
    {
        var root = new Grid { Width = WeatherCardDesignWidth, Height = WeatherCardDesignHeight };
        BuildWeatherContent();
        root.Children.Add(_weatherContent);
        root.Children.Add(BuildWeatherEmptyState());

        _weatherRefresh.Click += async (_, _) => await RefreshWeatherButtonAsync();
        _weatherSettings.Click += (_, _) => OpenWeatherSettings();
        _weatherAlertTimer.Tick += (_, _) =>
        {
            if (_disposed || _weatherAlerts.Children.Count <= 1) return;
            ShowWeatherAlert((_weatherAlertIndex + 1) % _weatherAlerts.Children.Count);
        };
        _weatherSettings.Visibility = Visibility.Collapsed;
        _weatherContent.ActualThemeChanged += (_, _) => RenderWeatherModules();
        AutomationProperties.SetName(_weatherRefresh, "刷新天气");
        AutomationProperties.SetName(_weatherSettings, "打开天气设置");

        _weatherCity.Text = "尚未读取天气";
        _weatherStatus.Visibility = Visibility.Collapsed;
        _weatherCondition.Text = "";
        _weatherTemperature.Text = "";
        _weatherEmpty.Text = "打开天气卡后读取天气。";
        ShowWeatherEmpty(true);
        return root;
    }

    private void BuildWeatherContent()
    {
        _weatherContent.Children.Add(_weatherMain);

        _weatherMain.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45, GridUnitType.Star) });
        _weatherMain.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55, GridUnitType.Star) });
        Add(_weatherMain, _weatherLeft, 0);
        Add(_weatherMain, _weatherRight, 1);

        _weatherLeft.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _weatherLeft.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _weatherLeft.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _weatherLeft.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _weatherLeft.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _weatherCity.VerticalAlignment = VerticalAlignment.Center;
        _weatherRefresh.VerticalAlignment = VerticalAlignment.Center;
        _weatherLocationRow.Children.Add(_weatherCity);
        _weatherLocationRow.Children.Add(_weatherRefresh);
        Grid.SetRow(_weatherLocationRow, 0);
        _weatherLeft.Children.Add(_weatherLocationRow);

        _weatherCurrentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _weatherCurrentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        Add(_weatherCurrentRow, _weatherTemperature, 0);
        _weatherIconHost.Children.Add(_weatherIcon);
        Add(_weatherCurrentRow, _weatherIconHost, 1);
        Grid.SetRow(_weatherCurrentRow, 1);
        _weatherLeft.Children.Add(_weatherCurrentRow);

        _weatherSummary.Children.Add(_weatherCondition);
        _weatherSummary.Children.Add(_weatherStatus);
        Grid.SetRow(_weatherSummary, 2);
        _weatherLeft.Children.Add(_weatherSummary);

        _weatherAlertPanel.Children.Add(_weatherAlertsHost);
        Grid.SetRow(_weatherAlertPanel, 4);
        _weatherLeft.Children.Add(_weatherAlertPanel);

        _weatherRight.Children.Add(_weatherForecast);
    }

    private FrameworkElement BuildWeatherEmptyState()
    {
        Secondary(_weatherEmpty);
        _weatherEmptyState.Children.Add(_weatherEmpty);
        _weatherEmptyState.Children.Add(_weatherSettings);
        return _weatherEmptyState;
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
        var hasCity = HasValue(cityName);
        _weatherCity.Text = hasCity ? cityName : "未设置城市";

        var currentTemperature = String(current, "temperature");
        var temperature = FirstNonEmpty(currentTemperature, String(status, "temperature"));
        _weatherTemperature.Text = NormalizeTemperature(temperature);
        _weatherCondition.Text = FirstNonEmpty(String(current, "weather"), String(status, "message"), "天气");
        var observedAt = String(current, "observedAt");
        var isNight = DateTime.TryParse(observedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var observedTime) &&
            (observedTime.Hour >= 20 || observedTime.Hour < 6);
        SetWeatherIcon(_weatherIcon, String(current, "conditionKind", "unknown"), isNight);
        _weatherStatus.Text = WeatherStatus(status);
        _weatherStatus.Visibility = HasValue(_weatherStatus.Text) ? Visibility.Visible : Visibility.Collapsed;

        var forecast = Array(details, "forecast").ToArray();
        var alerts = Array(details, "alerts").ToArray();
        var available = details.ValueKind == JsonValueKind.Object &&
            (HasValue(currentTemperature) || HasValue(String(current, "weather")) || forecast.Length > 0 || alerts.Length > 0);
        ShowWeatherEmpty(!hasCity || !available);
        if (!hasCity || !available)
        {
            _weatherEmpty.Text = hasCity
                ? FirstNonEmpty(String(status, "message"), "当前天气来源还没有完整数据。可以稍后刷新，或在天气设置中切换来源。")
                : "还没有设置天气城市。设置城市后，这里会显示当前天气和预报。";
            _weatherSettings.Visibility = hasCity ? Visibility.Collapsed : Visibility.Visible;
            _weatherRefresh.IsEnabled = !_weatherRefreshing;
            return;
        }

        RenderWeatherModules();
        _weatherSettings.Visibility = Visibility.Collapsed;
        _weatherRefresh.IsEnabled = !_weatherRefreshing && !Bool(status, "updating");
    }

    internal void RenderWeatherModules()
    {
        if (_disposed || _weatherLastStatus.ValueKind != JsonValueKind.Object) return;
        var details = _weatherLastDetails.ValueKind == JsonValueKind.Object ? _weatherLastDetails : Object(_weatherLastStatus, "details");
        var current = Object(details, "current");
        var forecast = Array(details, "forecast").ToArray();
        var alerts = Array(details, "alerts").ToArray();
        RenderForecast(forecast);
        RenderAlerts(alerts);
    }

    private void RenderForecast(JsonElement[] forecast)
    {
        _weatherForecast.Children.Clear();
        _weatherForecast.RowDefinitions.Clear();
        var temperatures = forecast
            .SelectMany(day => new[] { String(day, "min"), String(day, "max") })
            .Select(value => TryTemperature(value, out var number) ? number : (double?)null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
        var minimum = temperatures.Length > 0 ? temperatures.Min() : 0;
        var maximum = temperatures.Length > 0 ? temperatures.Max() : 1;
        var span = Math.Max(1, maximum - minimum);
        for (var row = 0; row < WeatherForecastRows; row++)
        {
            _weatherForecast.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            if (row >= forecast.Length) continue;

            var day = forecast[row];
            var rowGrid = Columns(new GridLength(30), new GridLength(28), new GridLength(26), new GridLength(1, GridUnitType.Star), new GridLength(28));
            rowGrid.ColumnSpacing = 3;
            rowGrid.VerticalAlignment = VerticalAlignment.Center;
            rowGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetRow(rowGrid, row);

            var label = Text(WeatherDayLabels[row], 11);
            label.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            label.VerticalAlignment = VerticalAlignment.Center;
            Add(rowGrid, label, 0);

            var icon = CreateWeatherIcon(28, String(day, "conditionDayKind", "unknown"));
            Add(rowGrid, icon, 1);

            var min = Text(NormalizeTemperature(String(day, "min")), 11);
            min.FontWeight = Microsoft.UI.Text.FontWeights.Light;
            Secondary(min);
            min.VerticalAlignment = VerticalAlignment.Center;
            Add(rowGrid, min, 2);

            var bar = new Border
            {
                Height = 6,
                Margin = new Thickness(1, 0, 1, 0),
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Color.FromArgb(72, 112, 120, 128)),
                VerticalAlignment = VerticalAlignment.Center
            };
            var range = new Grid();
            var fill = new Border
            {
                Height = 6,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(ForecastColor(row))
            };
            var hasRange = TryTemperature(String(day, "min"), out var low) && TryTemperature(String(day, "max"), out var high);
            void ResizeRange(object _, SizeChangedEventArgs args) =>
                SetForecastRange(fill, args.NewSize.Width, hasRange, low,
                    TryTemperature(String(day, "max"), out var parsedHigh) ? parsedHigh : 0, minimum, span);
            bar.SizeChanged += ResizeRange;
            range.Children.Add(fill);
            bar.Child = range;
            Add(rowGrid, bar, 3);

            var max = Text(NormalizeTemperature(String(day, "max")), 11);
            max.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            max.VerticalAlignment = VerticalAlignment.Center;
            max.HorizontalAlignment = HorizontalAlignment.Right;
            Add(rowGrid, max, 4);
            _weatherForecast.Children.Add(rowGrid);
        }
    }

    private static void SetForecastRange(Border fill, double width, bool hasRange, double low, double high, double minimum, double span)
    {
        if (!hasRange || width <= 0) return;
        var left = Math.Clamp((low - minimum) / span, 0, 1);
        var size = Math.Clamp(Math.Max(0.08, (high - low) / span), 0, 1);
        fill.Margin = new Thickness(left * width, 0, 0, 0);
        fill.Width = Math.Max(5, size * width);
    }

    private void RenderAlerts(JsonElement[] alerts)
    {
        _weatherAlerts.Children.Clear();
        _weatherAlertsHost.Content = _weatherAlerts;
        if (alerts.Length == 0)
        {
            _weatherAlertTimer.Stop();
            _weatherAlertPanel.Visibility = Visibility.Collapsed;
            _weatherAlertsHost.Visibility = Visibility.Collapsed;
            return;
        }

        _weatherAlertPanel.Visibility = Visibility.Visible;
        _weatherAlertsHost.Visibility = Visibility.Visible;
        _weatherAlertsHost.VerticalScrollBarVisibility = alerts.Length > 1 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        foreach (var alert in alerts)
        {
            var block = new StackPanel { Spacing = 1 };
            var type = FirstNonEmpty(String(alert, "typeName"), String(alert, "type"), "天气预警");
            var level = String(alert, "level");
            var title = Text(HasValue(level) ? level + " · " + type : type, 11);
            title.FontFamily = WeatherFontFamily;
            title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            title.TextWrapping = TextWrapping.Wrap;
            title.MaxLines = 1;
            block.Children.Add(title);

            var text = Text(String(alert, "text"), 10);
            text.FontFamily = WeatherFontFamily;
            text.TextWrapping = TextWrapping.Wrap;
            text.MaxLines = 2;
            Secondary(text);
            block.Children.Add(text);
            _weatherAlerts.Children.Add(block);
        }
        if (alerts.Length > 1)
        {
            ShowWeatherAlert(0);
            _weatherAlertTimer.Start();
        }
        else
        {
            _weatherAlertTimer.Stop();
            ShowWeatherAlert(0);
        }
    }

    private void ShowWeatherAlert(int index)
    {
        _weatherAlertIndex = index;
        for (var item = 0; item < _weatherAlerts.Children.Count; item++)
            _weatherAlerts.Children[item].Visibility = item == index ? Visibility.Visible : Visibility.Collapsed;
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

    private static string NormalizeTemperature(string value)
    {
        if (!HasValue(value)) return "";
        if (value.Contains('℃', StringComparison.Ordinal) || value.EndsWith('°')) return value;
        return value + "°";
    }

    private static bool TryTemperature(string value, out double result)
    {
        result = 0;
        if (!HasValue(value)) return false;
        var normalized = value.Trim().Replace("°C", "", StringComparison.OrdinalIgnoreCase)
            .Replace("°", "", StringComparison.Ordinal).Replace("℃", "", StringComparison.Ordinal);
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && double.IsFinite(result);
    }

    private static Color ForecastColor(int index) => index switch
    {
        0 => Color.FromArgb(255, 242, 201, 76),
        1 => Color.FromArgb(255, 151, 196, 145),
        2 => Color.FromArgb(255, 114, 183, 170),
        3 => Color.FromArgb(255, 169, 197, 174),
        _ => Color.FromArgb(255, 220, 203, 113)
    };

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(HasValue) ?? "";
    private static bool HasValue(string? value) => !string.IsNullOrWhiteSpace(value) && value != "--";

    private static FontIcon CreateWeatherIcon(double size, string kind = "", bool night = false) => new()
    {
        Width = size,
        Height = size,
        FontFamily = WeatherIconFont,
        FontSize = size - 4,
        Glyph = WeatherIconGlyph(kind, night),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static void SetWeatherIcon(FontIcon icon, string kind, bool night) =>
        icon.Glyph = WeatherIconGlyph(kind, night);

    private static string WeatherIconGlyph(string kind, bool night) => kind.ToLowerInvariant() switch
    {
        "clear" => night ? "\uF106" : "\uF101",
        "partly-cloudy" => night ? "\uF108" : "\uF103",
        "overcast" => "\uF105",
        "rain" => "\uF10F",
        "drizzle" => "\uF110",
        "snow" => "\uF120",
        "sleet" => "\uF124",
        "fog" => "\uF12F",
        "haze" => "\uF130",
        "dust" => "\uF131",
        "thunder" => "\uF10C",
        "wind" => "\uF146",
        _ => "\uF102"
    };
}
