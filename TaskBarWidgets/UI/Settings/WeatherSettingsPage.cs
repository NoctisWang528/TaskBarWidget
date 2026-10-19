using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace TaskBarWidgets.UI;

public sealed partial class SettingsWindow
{
    private WeatherEditor? _weatherPage;

    private sealed class WeatherEditor(CancellationToken lifetime) : IDisposable
    {
        internal readonly CancellationTokenSource Lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        internal CancellationTokenSource? Operation;
        internal readonly ComboBox Provider = new() { Header = "天气来源", HorizontalAlignment = HorizontalAlignment.Stretch };
        internal readonly TextBox Query = new() { Header = "查找城市", PlaceholderText = "输入城市名称，例如：南京", MaxLength = 80 };
        internal readonly Button Search = new() { Content = "搜索", MinHeight = 32 };
        internal readonly ListView Results = new() { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 230, HorizontalAlignment = HorizontalAlignment.Stretch };
        internal readonly TextBox CityCode = new() { Header = "已选择城市代码", IsReadOnly = true };
        internal readonly TextBlock CityName = Copy("正在读取已保存城市…", 14);
        internal readonly Button SaveCity = new() { Content = "保存城市并刷新", IsEnabled = false };
        internal readonly Button Refresh = new() { Content = "立即刷新已保存城市" };
        internal readonly TextBlock Status = Copy("正在读取天气状态…", 14);
        internal readonly TextBlock LastSuccess = Copy("上次成功：尚无记录");
        internal readonly TextBlock LastAttempt = Copy("上次尝试：尚无记录");
        internal readonly InfoBar Message = new() { IsClosable = false };
        internal readonly StackPanel Credentials = new() { Spacing = 10 };
        internal readonly PasswordBox ApiKey = new() { Header = "和风天气 API Key", PlaceholderText = "留空保留已有密钥", MaxLength = 512, PasswordRevealMode = PasswordRevealMode.Hidden };
        internal readonly TextBox ApiHost = new() { Header = "API Host", PlaceholderText = "填写和风控制台分配的 API Host", MaxLength = 255 };
        internal readonly TextBlock KeyStatus = Copy("密钥状态：正在读取");
        internal readonly Button SaveCredentials = new() { Content = "保存连接配置" };
        internal readonly Button ClearKey = new() { Content = "清除已保存密钥" };
        internal JsonObject? SelectedCity;
        internal JsonObject? SavedCity;
        internal JsonObject? Capabilities;
        internal Action? CapabilitiesChanged;
        internal int SavedProvider;
        internal bool ProviderEdited;
        internal bool Binding;
        internal bool Committing;
        internal bool HasStatus;
        internal bool LoadingStatus;
        internal bool Searching;
        internal bool HostEdited;
        internal void CancelOperation() { Operation?.Cancel(); Operation?.Dispose(); Operation = null; }
        public void Dispose() { CancelOperation(); Lifetime.Cancel(); ApiKey.Password = string.Empty; Lifetime.Dispose(); }
    }

    private void CancelWeatherPage()
    {
        var page = _weatherPage; _weatherPage = null; page?.Dispose();
    }

    private bool IsWeatherCurrent(WeatherEditor page) => ReferenceEquals(_weatherPage, page) && !_closed && !page.Lifetime.IsCancellationRequested;
    private bool IsWeatherOperationCurrent(WeatherEditor page, CancellationToken token) =>
        IsWeatherCurrent(page) && !token.IsCancellationRequested && page.Operation is not null && page.Operation.Token == token;

    private void RenderWeatherSettings()
    {
        var previous = _weatherPage;
        _weatherPage = null;
        previous?.Dispose();
        var page = new WeatherEditor(_lifetime.Token);
        _weatherPage = page;
        _page.Children.Add(new TextBlock
        {
            Text = QuickCardEnabled("weather") ? "天气卡已启用" : "天气卡未启用",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        var body = Section("天气", "搜索后选择城市，再保存并刷新。以下操作立即保存；更新频率使用上方“应用更改”。");
        page.Provider.Items.Add("中国天气网"); page.Provider.Items.Add("和风天气");
        page.Provider.SelectedIndex = 0;
        var searchRow = new Grid { ColumnSpacing = 8 };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.Children.Add(page.Query); Grid.SetColumn(page.Search, 1); page.Search.VerticalAlignment = VerticalAlignment.Bottom; searchRow.Children.Add(page.Search);
        body.Children.Add(page.Provider); body.Children.Add(searchRow); body.Children.Add(page.Results);
        body.Children.Add(page.CityName); body.Children.Add(page.CityCode);
        var cityActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        cityActions.Children.Add(page.SaveCity); cityActions.Children.Add(page.Refresh); body.Children.Add(cityActions);
        body.Children.Add(page.Status); body.Children.Add(page.LastAttempt); body.Children.Add(page.LastSuccess); body.Children.Add(page.Message);
        page.Credentials.Children.Add(new TextBlock { Text = "和风天气连接", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        page.Credentials.Children.Add(Copy("从和风天气控制台获取 API Host 和 API Key。密钥仅在点击保存时写入本机；不会读取或显示已保存的值。保存连接后可重新搜索或刷新天气。"));
        page.Credentials.Children.Add(page.ApiHost); page.Credentials.Children.Add(page.ApiKey); page.Credentials.Children.Add(page.KeyStatus);
        var connectionActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        connectionActions.Children.Add(page.SaveCredentials); connectionActions.Children.Add(page.ClearKey); page.Credentials.Children.Add(connectionActions);
        body.Children.Add(page.Credentials);

        foreach (var (element, id) in new (FrameworkElement, string)[] {
            (page.Provider, "provider"), (page.Query, "query"), (page.Search, "search"), (page.Results, "results"),
            (page.CityCode, "city-code"), (page.SaveCity, "save-city"), (page.Refresh, "refresh"),
            (page.ApiHost, "api-host"), (page.ApiKey, "api-key"), (page.SaveCredentials, "save-connection"), (page.ClearKey, "clear-key"), (page.Message, "message") })
            AutomationProperties.SetAutomationId(element, "weather-" + id);
        AutomationProperties.SetName(page.Results, "城市搜索结果，选择后保存");
        page.Provider.SelectionChanged += (_, _) => {
            if (page.Binding || !IsWeatherCurrent(page)) return;
            page.ProviderEdited = true; page.CancelOperation(); page.Searching = false; page.LoadingStatus = false;
            page.Results.Items.Clear(); page.SelectedCity = null; page.Message.IsOpen = false;
            UpdateWeatherSelection(page); UpdateWeatherButtons(page);
        };
        page.Results.SelectionChanged += (_, _) => {
            page.SelectedCity = (page.Results.SelectedItem as ListViewItem)?.Tag as JsonObject;
            UpdateWeatherSelection(page); UpdateWeatherButtons(page);
        };
        page.Search.Click += async (_, _) => await SearchWeatherCityAsync(page);
        page.Query.KeyDown += async (_, args) => { if (args.Key == VirtualKey.Enter) { args.Handled = true; await SearchWeatherCityAsync(page); } };
        page.SaveCity.Click += async (_, _) => await SaveWeatherCityAsync(page);
        page.Refresh.Click += async (_, _) => await RefreshWeatherAsync(page);
        page.SaveCredentials.Click += async (_, _) => await SaveWeatherCredentialsAsync(page, false);
        page.ClearKey.Click += async (_, _) => await SaveWeatherCredentialsAsync(page, true);
        page.ApiHost.TextChanged += (_, _) => { if (!page.Binding) page.HostEdited = true; };
        page.Query.TextChanged += (_, _) => {
            if (!page.Searching || page.Committing) return;
            page.CancelOperation(); page.Searching = false; page.Results.Items.Clear(); page.SelectedCity = null;
            UpdateWeatherSelection(page); UpdateWeatherButtons(page);
        };
        body.Loaded += async (_, _) => { if (IsWeatherCurrent(page) && _connection.IsConnected) await ReadWeatherStatusAsync(page); };
        UpdateWeatherButtons(page);

        var update = Section("天气更新", "天气卡与任务栏温度共用此频率。修改后点击上方“应用更改”，保存成功后会自动刷新天气。");
        var updateFrequency = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 32 };
        updateFrequency.Items.Add(new ComboBoxItem { Content = "每小时三次", Tag = 0 });
        updateFrequency.Items.Add(new ComboBoxItem { Content = "每小时两次", Tag = 1 });
        updateFrequency.Items.Add(new ComboBoxItem { Content = "每小时一次", Tag = 2 });
        updateFrequency.Items.Add(new ComboBoxItem { Content = "每两小时一次", Tag = 3 });
        updateFrequency.Items.Add(new ComboBoxItem { Content = "每三小时一次", Tag = 4 });
        const string UpdateFrequencyKey = "builtins.weatherpro.dll.config.update_freq";
        var currentFrequency = (int)Math.Clamp(_native.Number(UpdateFrequencyKey, 1), 0, 4);
        var bindingFrequency = true;
        updateFrequency.SelectedItem = updateFrequency.Items.OfType<ComboBoxItem>().First(item => item.Tag is int value && value == currentFrequency);
        bindingFrequency = false;
        AutomationProperties.SetAutomationId(updateFrequency, "weather-update-frequency");
        updateFrequency.SelectionChanged += (_, _) =>
        {
            if (bindingFrequency) return;
            if (updateFrequency.SelectedItem is ComboBoxItem { Tag: int value }) { _native.Set(UpdateFrequencyKey, value); Changed(); }
        };
        update.Children.Add(updateFrequency);
    }

    private static void WeatherMessage(WeatherEditor page, string text, InfoBarSeverity severity)
    {
        page.Message.Message = text; page.Message.Severity = severity; page.Message.IsOpen = true;
    }

    private void UpdateWeatherSelection(WeatherEditor page)
    {
        if (!IsWeatherCurrent(page)) return;
        var city = page.SelectedCity ?? page.SavedCity;
        var id = Text(city?["id"]);
        page.CityCode.Text = id;
        page.CityName.Text = city is null ? "尚未选择城市。" : (page.SelectedCity is null ? "已保存：" : "待保存：") + WeatherCityLabel(city);
    }

    private static string WeatherCityLabel(JsonObject city) => string.Join(" · ", new[] { Text(city["name"]), Text(city["region"]), Text(city["id"]) }.Where(part => part.Length > 0));

    private void UpdateWeatherButtons(WeatherEditor page)
    {
        if (!IsWeatherCurrent(page)) return;
        var available = !_busy && !page.Committing && !page.LoadingStatus;
        page.Provider.IsEnabled = available;
        page.Search.IsEnabled = available;
        page.Search.Content = page.Searching ? "重新搜索" : "搜索";
        page.Query.IsEnabled = available;
        page.Results.IsEnabled = !_busy && !page.Committing;
        page.SaveCity.IsEnabled = available && !page.Searching && page.SelectedCity is not null;
        page.Refresh.IsEnabled = available && page.HasStatus;
        page.Credentials.Visibility = page.Provider.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        page.SaveCredentials.IsEnabled = available;
        page.ClearKey.IsEnabled = available && page.HasStatus;
        page.ApiHost.IsEnabled = available;
        page.ApiKey.IsEnabled = available;
    }

    private void ApplyWeatherStatus(WeatherEditor page, JsonObject status)
    {
        if (!IsWeatherCurrent(page)) return;
        page.HasStatus = true; page.SavedProvider = (int)Number(status["provider"]);
        page.Capabilities = status["details"] is JsonObject details && details["capabilities"] is JsonObject capabilities ? (JsonObject)capabilities.DeepClone() : null;
        page.SavedCity = status["city"] is JsonObject city ? (JsonObject)city.DeepClone() : null;
        if (page.Committing && page.SelectedCity is JsonObject selected && page.SavedProvider == page.Provider.SelectedIndex && Text(selected["id"]) == Text(page.SavedCity?["id"]))
            page.SelectedCity = null;
        page.Status.Text = string.Join(" · ", new[] { status["updating"]?.ToString() == "true" ? "正在更新" : "当前天气", Text(status["temperature"]), Text(status["message"]) }.Where(value => value.Length > 0));
        page.LastAttempt.Text = "上次尝试：" + (Text(status["lastAttempt"]).Length > 0 ? Text(status["lastAttempt"]) : "尚无记录");
        page.LastSuccess.Text = "上次成功：" + (Text(status["lastSuccess"]).Length > 0 ? Text(status["lastSuccess"]) : "尚无记录");
        if (status["lastSuccessCity"] is JsonObject lastCity && Text(lastCity["name"]).Length > 0)
            page.LastSuccess.Text += " · " + Text(lastCity["name"]);
        page.KeyStatus.Text = status["hasApiKey"]?.ToString() == "true" ? "已保存密钥；留空不会覆盖。" : "尚未配置密钥。";
        if (!page.ProviderEdited)
        {
            page.Binding = true;
            page.Provider.SelectedIndex = Math.Clamp(page.SavedProvider, 0, 1);
            page.Binding = false;
        }
        if (!page.HostEdited) { page.Binding = true; page.ApiHost.Text = Text(status["apiHost"]); page.Binding = false; }
        UpdateWeatherSelection(page); UpdateWeatherButtons(page);
        page.CapabilitiesChanged?.Invoke();
    }

    private async Task<JsonObject> WeatherCommandAsync(WeatherEditor page, object payload, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var response = await _connection.RequestAsync("weatherCommand", payload, token);
        if (!IsWeatherOperationCurrent(page, token)) throw new OperationCanceledException(token);
        var reply = JsonNode.Parse(response.GetRawText()) as JsonObject ?? throw new InvalidDataException("天气服务返回了无效结果。");
        var jobId = reply["jobId"] is JsonValue jobValue && jobValue.TryGetValue<long>(out var number) ? number : 0;
        while (reply["pending"]?.ToString() == "true")
        {
            if (jobId <= 0) throw new InvalidDataException("天气任务缺少有效任务编号。");
            await Task.Delay(350, token);
            if (!IsWeatherCurrent(page)) throw new OperationCanceledException(token);
            response = await _connection.RequestAsync("weatherCommand", new { operation = "result", jobId }, token);
            if (!IsWeatherOperationCurrent(page, token)) throw new OperationCanceledException(token);
            reply = JsonNode.Parse(response.GetRawText()) as JsonObject ?? throw new InvalidDataException("天气任务返回了无效结果。");
        }
        if (reply["success"]?.ToString() == "false")
        {
            if (reply["status"] is JsonObject status) ApplyWeatherStatus(page, status);
            throw new InvalidOperationException(Text(reply["error"], "天气操作失败，请检查来源、城市和连接配置。"));
        }
        return reply;
    }

    private async Task RunWeatherAsync(WeatherEditor page, bool committing, Func<CancellationToken, Task> operation)
    {
        if (!IsWeatherCurrent(page) || _busy || page.Committing) return;
        page.CancelOperation();
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(page.Lifetime.Token);
        pending.CancelAfter(TimeSpan.FromSeconds(90)); page.Operation = pending;
        page.Committing = committing; page.Message.IsOpen = false; UpdateSaveStatus();
        try { await operation(pending.Token); }
        catch (OperationCanceledException) { if (IsWeatherCurrent(page) && ReferenceEquals(page.Operation, pending)) WeatherMessage(page, "天气操作超时或连接中断，请重试。", InfoBarSeverity.Warning); }
        catch (Exception error) { if (IsWeatherCurrent(page) && ReferenceEquals(page.Operation, pending)) WeatherMessage(page, error.Message, InfoBarSeverity.Error); }
        finally
        {
            if (IsWeatherCurrent(page) && ReferenceEquals(page.Operation, pending)) { page.Operation = null; page.Committing = false; page.Searching = false; page.LoadingStatus = false; UpdateSaveStatus(); }
        }
    }

    private async Task ReadWeatherStatusAsync(WeatherEditor page)
    {
        if (_busy || page.Committing || page.HasStatus || page.LoadingStatus) return;
        page.LoadingStatus = true;
        await RunWeatherAsync(page, false, async token => {
            while (IsWeatherOperationCurrent(page, token))
            {
                var reply = await WeatherCommandAsync(page, new { operation = "status" }, token);
                if (!IsWeatherOperationCurrent(page, token)) return;
                var status = reply["status"] as JsonObject ?? reply;
                ApplyWeatherStatus(page, status);
                if (status["updating"]?.ToString() != "true") return;
                page.LoadingStatus = false; UpdateWeatherButtons(page);
                await Task.Delay(500, token);
            }
        });
    }

    private async Task SearchWeatherCityAsync(WeatherEditor page)
    {
        if (!IsWeatherCurrent(page) || _busy || page.Committing || page.LoadingStatus) return;
        var query = page.Query.Text.Trim();
        if (query.Length == 0) { WeatherMessage(page, "请输入城市名称，例如：南京。", InfoBarSeverity.Warning); return; }
        var provider = page.Provider.SelectedIndex;
        page.Searching = true; page.Results.Items.Clear(); page.SelectedCity = null; UpdateWeatherSelection(page);
        await RunWeatherAsync(page, false, async token => {
            var reply = await WeatherCommandAsync(page, new { operation = "search", provider, query }, token);
            if (!IsWeatherOperationCurrent(page, token) || page.Provider.SelectedIndex != provider || page.Query.Text.Trim() != query) return;
            foreach (var city in (reply["cities"] as JsonArray)?.OfType<JsonObject>() ?? [])
                page.Results.Items.Add(new ListViewItem { Content = new TextBlock { Text = WeatherCityLabel(city), TextWrapping = TextWrapping.Wrap }, Tag = city.DeepClone(), HorizontalContentAlignment = HorizontalAlignment.Stretch });
            WeatherMessage(page, page.Results.Items.Count == 0 ? "未找到城市，请尝试更完整的城市或区县名称。" : $"找到 {page.Results.Items.Count} 个城市，请选择一项后保存。", InfoBarSeverity.Informational);
        });
    }

    private async Task SaveWeatherCityAsync(WeatherEditor page)
    {
        if (_busy || page.SelectedCity is not JsonObject city || page.Committing) return;
        var provider = page.Provider.SelectedIndex;
        var selected = (JsonObject)city.DeepClone();
        await RunWeatherAsync(page, true, async token => {
            var reply = await WeatherCommandAsync(page, new { operation = "configure", provider, city = selected }, token);
            if (!IsWeatherOperationCurrent(page, token)) return;
            page.ProviderEdited = false; page.SelectedCity = null;
            if (reply["status"] is JsonObject status) ApplyWeatherStatus(page, status);
            else { page.SavedCity = selected; page.SavedProvider = provider; UpdateWeatherSelection(page); }
            WeatherMessage(page, "城市已保存，天气刷新完成。", InfoBarSeverity.Success);
        });
    }

    private async Task RefreshWeatherAsync(WeatherEditor page) => await RunWeatherAsync(page, true, async token => {
        var reply = await WeatherCommandAsync(page, new { operation = "refresh" }, token);
        if (!IsWeatherOperationCurrent(page, token)) return;
        if (reply["status"] is JsonObject status) ApplyWeatherStatus(page, status);
        WeatherMessage(page, "已完成刷新。", InfoBarSeverity.Success);
    });

    private async Task SaveWeatherCredentialsAsync(WeatherEditor page, bool clear)
    {
        if (!IsWeatherCurrent(page) || _busy || page.Committing) return;
        var payload = new JsonObject { ["operation"] = "configure", ["credentialsOnly"] = true, ["apiHost"] = page.ApiHost.Text.Trim(), ["clearApiKey"] = clear };
        if (!clear && !string.IsNullOrWhiteSpace(page.ApiKey.Password)) payload["apiKey"] = page.ApiKey.Password;
        page.ApiKey.Password = string.Empty;
        await RunWeatherAsync(page, true, async token => {
            try
            {
                var reply = await WeatherCommandAsync(page, payload, token);
                if (!IsWeatherOperationCurrent(page, token)) return;
                page.HostEdited = false;
                if (reply["status"] is JsonObject status) ApplyWeatherStatus(page, status);
                WeatherMessage(page, clear ? "已清除保存的密钥。" : "连接配置已保存；可重新搜索城市或刷新天气。", InfoBarSeverity.Success);
            }
            finally { payload.Remove("apiKey"); }
        });
    }
}
