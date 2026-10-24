using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TaskBarWidgets.UI;

public sealed partial class SettingsWindow
{
    // Real WinUI controls and the normal pipe, backed only by synthetic jobs.
    // No weather HTTP calls, saved configuration or personal credentials.
    internal static async Task<object> ProbeWeatherControlsAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var pipeName = "TaskBarWidgets.UI.weather-probe." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var accepting = server.WaitForConnectionAsync(timeout.Token);
        using var connection = new UiConnection(pipeName); connection.Start(); await accepting;
        using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, true);
        using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        var savedCity = new JsonObject { ["id"] = "101010100", ["name"] = "北京", ["region"] = "北京" };
        var provider = 0; var hasKey = false; var apiHost = ""; var refreshed = false;
        var searchStarted = false; var configureCount = 0; var cleared = false; var failRefresh = true; var statusReads = 0;
        var refreshCount = 0; var alertsError = ""; var airError = ""; var failCredentials = false;
        var jobs = new Dictionary<long, JsonObject>();
        long nextJobId = 0;
        JsonObject Status() => new() {
            ["provider"] = provider, ["city"] = savedCity.DeepClone(), ["apiHost"] = apiHost, ["hasApiKey"] = hasKey,
            ["updating"] = false, ["message"] = refreshed ? "晴" : "尚未刷新", ["temperature"] = refreshed ? "23°C" : "",
            ["lastAttempt"] = "2026-10-16T10:00:00", ["lastSuccess"] = refreshed ? "2026-10-16T10:00:00" : "",
            ["details"] = new JsonObject { ["alertsError"] = alertsError, ["airQuality"] = new JsonObject { ["error"] = airError } } };

        async Task BackendAsync()
        {
            while (!timeout.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(timeout.Token);
                if (line is null) return;
                var request = JsonNode.Parse(line)!.AsObject();
                if (Text(request["action"]) != "weatherCommand") throw new InvalidOperationException("Weather UI invoked an unrelated backend action.");
                var payload = request["payload"]!.AsObject();
                var operation = Text(payload["operation"]);
                JsonObject reply;
                if (operation == "status") { reply = Status(); reply["updating"] = ++statusReads == 1; }
                else if (operation == "result") {
                    if (payload["jobId"] is not JsonValue value || !value.TryGetValue<long>(out var jobId)) throw new InvalidOperationException("Weather result job ID must remain numeric.");
                    reply = (JsonObject)jobs[jobId].DeepClone();
                }
                else
                {
                    var jobId = ++nextJobId;
                    var completed = new JsonObject { ["jobId"] = jobId, ["pending"] = false, ["success"] = true };
                    if (operation == "search")
                    {
                        searchStarted = true;
                        if (Text(payload["query"]) != "南京") throw new InvalidOperationException("Weather search did not send the typed city name.");
                        completed["cities"] = new JsonArray(new JsonObject { ["id"] = "101190101", ["name"] = "南京", ["region"] = "江苏" });
                    }
                    else if (operation == "configure")
                    {
                        configureCount++;
                        if (payload["credentialsOnly"]?.ToString() == "true")
                        {
                            if (payload.ContainsKey("provider") || payload.ContainsKey("city")) throw new InvalidOperationException("Credential save changed the city/provider.");
                            if (failCredentials && payload["clearApiKey"]?.ToString() != "true")
                            {
                                completed["success"] = false; completed["error"] = "模拟连接验证失败：网络连接失败（阶段：发送请求）。";
                            }
                            else
                            {
                                if (!string.IsNullOrWhiteSpace(Text(payload["apiHost"]))) apiHost = Text(payload["apiHost"]);
                                if (payload["clearApiKey"]?.ToString() == "true") { hasKey = false; cleared = true; }
                                else if (payload.ContainsKey("apiKey"))
                                {
                                    if (Text(payload["apiKey"]) != "synthetic-probe-key") throw new InvalidOperationException("Explicit key write was not preserved.");
                                    hasKey = true;
                                }
                                completed["message"] = "和风天气连接已验证并保存；当前生效来源和城市未改变。";
                            }
                        }
                        else
                        {
                            if (payload.ContainsKey("apiKey")) throw new InvalidOperationException("City save exposed a key.");
                            savedCity = (JsonObject)payload["city"]!.DeepClone(); provider = (int)Number(payload["provider"]); refreshed = true;
                        }
                        completed["status"] = Status();
                    }
                    else if (operation == "refresh")
                    {
                        refreshCount++;
                        if (failRefresh) { failRefresh = false; completed["success"] = false; completed["error"] = "模拟天气连接错误"; }
                        else refreshed = true;
                        completed["status"] = Status();
                    }
                    else throw new InvalidOperationException("Unknown synthetic weather operation.");
                    jobs[jobId] = completed;
                    reply = new JsonObject { ["jobId"] = jobId, ["pending"] = true };
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { version = 1, type = "command", action = "uiResult", requestId = Text(request["requestId"]), success = true, payload = reply }));
            }
        }

        var backend = BackendAsync();
        var window = new SettingsWindow(connection);
        var activated = false;
        UiTemplateProbe.PreventProbeActivation(window);
        window.Activated += (_, args) => { if (args.WindowActivationState != WindowActivationState.Deactivated) activated = true; };
        try
        {
            window.ShowPage("quickCardsWeather");
            window.LoadValidationData(new JsonObject {
                ["groups"] = new JsonArray(new JsonObject { ["id"] = "builtins.weather", ["page"] = "quickCardsWeather", ["label"] = "天气" }),
                ["fields"] = new JsonArray(
                    new JsonObject { ["key"] = "builtins.weatherpro.dll.config.city_no", ["group"] = "builtins.weather", ["type"] = "string", ["label"] = "城市", ["value"] = "101010100" },
                    new JsonObject { ["key"] = "items.native_0.label", ["itemId"] = "native_0", ["itemName"] = "CPU", ["value"] = "CPU" },
                    new JsonObject { ["key"] = "items.native_0.show", ["value"] = true }) }, new JsonObject());
            window.AppWindow.IsShownInSwitchers = false;
            window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-30000, -30000, 900, 1000)); window.AppWindow.Show(false);
            while (window._weatherPage?.HasStatus != true || window._weatherPage.LoadingStatus || statusReads < 2 || window._weatherPage.Status.Text.Contains("正在更新")) await Task.Delay(10, timeout.Token);
            var page = window._weatherPage ?? throw new InvalidOperationException("Weather settings were not created.");
            var updateFrequency = WalkWeather(window._page).OfType<ComboBox>().FirstOrDefault(box => AutomationProperties.GetAutomationId(box) == "weather-update-frequency")
                ?? throw new InvalidOperationException("Weather update frequency control is missing.");
            updateFrequency.SelectedItem = updateFrequency.Items.OfType<ComboBoxItem>().First(item => item.Tag is int value && value == 3);
            if ((int)window._native.Number("builtins.weatherpro.dll.config.update_freq", 1) != 3 || !window._apply.IsEnabled)
                throw new InvalidOperationException("Weather update frequency did not draft the native setting.");
            updateFrequency.SelectedItem = updateFrequency.Items.OfType<ComboBoxItem>().First(item => item.Tag is int value && value == 1);
            window._native.Set("builtins.weatherpro.dll.config.show_weather_icon", true); window.UpdateSaveStatus();
            if (!window._apply.IsEnabled) throw new InvalidOperationException("Synthetic native draft must be applicable before testing weather write exclusion.");
            window._busy = true; window.UpdateSaveStatus();
            if (page.Search.IsEnabled || page.SaveCredentials.IsEnabled || page.Refresh.IsEnabled) throw new InvalidOperationException("Global settings save did not disable weather operations.");
            await window.SaveWeatherCredentialsAsync(page, false);
            if (configureCount != 0) throw new InvalidOperationException("Weather wrote credentials during global settings save.");
            window._busy = false; window.UpdateSaveStatus();
            page.Query.Text = "南京";
            await Task.Delay(20, timeout.Token);
            var staleSearch = window.SearchWeatherCityAsync(page);
            while (!searchStarted) await Task.Delay(10, timeout.Token);
            page.Provider.SelectedIndex = 1;
            await staleSearch.WaitAsync(timeout.Token);
            if (!window._apply.IsEnabled || !window._saveStatus.Text.Contains("待应用"))
                throw new InvalidOperationException("Weather provider draft did not enable global Apply.");
            if (page.Results.Items.Count != 0 || Text(page.SavedCity?["id"]) != "101010100") throw new InvalidOperationException("Changing provider accepted stale search results or changed saved city.");
            if (page.Refresh.IsEnabled || !page.SwitchProvider.IsEnabled || !page.Active.Text.Contains("中国天气网") || !page.Draft.Text.Contains("尚未保存"))
                throw new InvalidOperationException("Provider draft was confused with the active provider.");
            await window.RefreshWeatherAsync(page);
            if (refreshCount != 0) throw new InvalidOperationException("Draft provider refreshed the old provider.");
            window._native.Pending.Clear();
            var switchSave = window.SaveAsync();
            if (window._apply.IsEnabled || page.ApiHost.IsEnabled || page.SwitchProvider.IsEnabled)
                throw new InvalidOperationException("Pending provider switch did not exclude other writes.");
            await switchSave.WaitAsync(timeout.Token);
            if (window._apply.IsEnabled || window.HasWeatherDraft() || !ReferenceEquals(window._weatherPage, page))
                throw new InvalidOperationException("Global Apply did not clear the weather draft or preserved status was lost.");
            if (provider != 1 || Text(savedCity["id"]) != "101010100" || !page.Message.Message.Contains("和风天气") || !page.Message.Message.Contains("北京"))
                throw new InvalidOperationException("Explicit switch did not configure the saved city and chosen provider.");
            page.SavedCity!["id"] = "non-cn-city"; page.Provider.SelectedIndex = 0;
            if (page.SwitchProvider.IsEnabled) throw new InvalidOperationException("An incompatible saved city allowed direct provider switching.");
            await window.SwitchWeatherProviderAsync(page);
            if (configureCount != 1) throw new InvalidOperationException("Incompatible city reached configure.");
            page.SavedCity["id"] = "101010100"; page.Provider.SelectedIndex = 1;
            await window.SearchWeatherCityAsync(page).WaitAsync(timeout.Token);
            if (page.Results.Items.Count != 1 || page.CityCode.Text != "101010100" || configureCount != 1) throw new InvalidOperationException("Search changed the saved city before selection/save.");
            page.Results.SelectedIndex = 0;
            if (page.CityCode.Text != "101190101" || !page.SaveCity.IsEnabled) throw new InvalidOperationException("Selecting a city did not populate its read-only code.");
            if (page.Refresh.IsEnabled) throw new InvalidOperationException("Unsaved selected city allowed old-city refresh.");
            await window.RefreshWeatherAsync(page);
            if (refreshCount != 0) throw new InvalidOperationException("Unsaved city reached refresh.");
            alertsError = "预警获取失败：模拟错误";
            airError = "空气质量获取失败：模拟错误";
            if (!window._apply.IsEnabled) throw new InvalidOperationException("City selection did not enable global Apply.");
            var citySave = window.SaveAsync();
            if (window._apply.IsEnabled) throw new InvalidOperationException("Weather commit did not disable global Apply.");
            await citySave.WaitAsync(timeout.Token);
            if (Text(savedCity["id"]) != "101190101" || provider != 1 || !page.LastSuccess.Text.Contains("2026-10-16")) throw new InvalidOperationException("City save did not apply the chosen provider/city and weather status.");
            if (page.Message.Severity != InfoBarSeverity.Warning || !page.Message.Message.Contains(alertsError) ||
                !page.Message.Message.Contains(airError) || !page.Status.Text.Contains(airError) || page.Message.Message.Contains("可能过时") || !page.Message.Message.Contains("南京"))
                throw new InvalidOperationException("City save hid alert failure or actual city.");
            page.ApiKey.Password = "synthetic-probe-key";
            await Task.Delay(20, timeout.Token);
            if (page.Refresh.IsEnabled) throw new InvalidOperationException("Key editing did not immediately disable refresh.");
            page.ApiKey.Password = "";
            await Task.Delay(20, timeout.Token);
            if (!page.Refresh.IsEnabled) throw new InvalidOperationException("Discarding the key draft did not restore refresh.");
            page.ApiHost.Text = "probe.qweatherapi.com";
            await Task.Delay(20, timeout.Token);
            if (page.Refresh.IsEnabled) throw new InvalidOperationException("Host editing did not immediately disable refresh.");
            page.ApiKey.Password = "synthetic-probe-key";
            await Task.Delay(20, timeout.Token);
            if (page.Refresh.IsEnabled || page.SwitchProvider.IsEnabled) throw new InvalidOperationException("Unsaved credentials allowed refresh/switch.");
            page.Provider.SelectedIndex = 0;
            if (!page.SwitchProvider.IsEnabled || !page.Draft.Text.Contains("已保存城市"))
                throw new InvalidOperationException("Hidden QWeather drafts blocked switching back to China Weather.");
            var chinaStatus = Status(); chinaStatus["provider"] = 0;
            window.ApplyWeatherStatus(page, chinaStatus);
            if (!page.Refresh.IsEnabled) throw new InvalidOperationException("Hidden QWeather drafts blocked active China Weather refresh.");
            window.ApplyWeatherStatus(page, Status());
            page.Provider.SelectedIndex = 1;
            failCredentials = true;
            await window.SaveAsync().WaitAsync(timeout.Token);
            if (hasKey || apiHost.Length != 0 || page.Message.Severity != InfoBarSeverity.Error || page.ApiKey.Password != "synthetic-probe-key" ||
                page.ApiHost.Text != "probe.qweatherapi.com" || !page.Message.Message.Contains("模拟连接验证失败") || page.Provider.SelectedIndex != 1 || !page.ProviderEdited || page.Refresh.IsEnabled)
                throw new InvalidOperationException("Failed credential validation lost its draft or changed saved credentials.");
            if (!window._apply.IsEnabled || !window.HasWeatherDraft())
                throw new InvalidOperationException("Failed global Apply lost retryable weather drafts.");
            failCredentials = false;
            var connectionSave = window.SaveAsync();
            if (window._apply.IsEnabled || page.ApiKey.IsEnabled) throw new InvalidOperationException("Credential validation did not exclude other writes.");
            await connectionSave.WaitAsync(timeout.Token);
            if (window._apply.IsEnabled || window.HasWeatherDraft())
                throw new InvalidOperationException("Successful global Apply left a stale weather dirty flag.");
            if (!hasKey || page.ApiKey.Password.Length != 0 || !page.KeyStatus.Text.Contains("已保存")) throw new InvalidOperationException("Key was not write-only or persisted status is missing.");
            if (!page.Message.Message.Contains("已验证") || !page.Message.Message.Contains("未改变") || page.Message.Message.Contains("尚未切换") || provider != 1 || Text(savedCity["id"]) != "101190101")
                throw new InvalidOperationException("Credential success implied a weather refresh or source switch.");
            page.ApiHost.Text = "";
            await Task.Delay(20, timeout.Token);
            await window.SaveWeatherCredentialsAsync(page, false).WaitAsync(timeout.Token);
            if (!hasKey || apiHost != "probe.qweatherapi.com") throw new InvalidOperationException("Blank credentials replaced stored values.");
            failCredentials = true;
            page.ApiHost.Text = "unsaved.qweatherapi.com";
            await Task.Delay(20, timeout.Token);
            await window.SaveWeatherCredentialsAsync(page, true).WaitAsync(timeout.Token);
            if (!cleared || hasKey || apiHost != "probe.qweatherapi.com" || page.ApiHost.Text != "unsaved.qweatherapi.com")
                throw new InvalidOperationException("Explicit key clear failed or committed/discarded the host draft.");
            page.ApiHost.Text = apiHost;
            await Task.Delay(20, timeout.Token);
            await window.RefreshWeatherAsync(page).WaitAsync(timeout.Token);
            if (page.Message.Severity != InfoBarSeverity.Error || !page.Message.Message.Contains("模拟天气连接错误")) throw new InvalidOperationException("Refresh failure was silently hidden.");
            await window.RefreshWeatherAsync(page).WaitAsync(timeout.Token);
            if (page.Message.Severity != InfoBarSeverity.Warning || !page.Message.Message.Contains(alertsError) || !page.Message.Message.Contains(airError) || !page.Message.Message.Contains("和风天气") || !page.Message.Message.Contains("南京"))
                throw new InvalidOperationException("Refresh reported full success despite alert failure.");
            alertsError = "";
            await window.RefreshWeatherAsync(page).WaitAsync(timeout.Token);
            if (page.Message.Severity != InfoBarSeverity.Warning || !page.Message.Message.Contains(airError))
                throw new InvalidOperationException("Air-only failure reported full success.");
            airError = "";
            await window.RefreshWeatherAsync(page).WaitAsync(timeout.Token);
            if (page.Message.Severity != InfoBarSeverity.Success || page.Status.Text.Contains("模拟错误")) throw new InvalidOperationException("Refresh could not recover after failure.");
            var pending = window.SearchWeatherCityAsync(page);
            window.ShowPage("general"); await pending.WaitAsync(timeout.Token);
            if (window._weatherPage is not null || activated) throw new InvalidOperationException("Leaving the weather page retained it or probe activated a window.");
            if (backend.IsFaulted) await backend;
            return new { typedCitySearch = true, explicitSelectionAndSave = true, staleProviderResultsDiscarded = true, writeOnlyPassword = true, blankKeyPreserved = true, explicitClear = true, refreshErrorAndRecovery = true, airQualityWarning = true, connectionDraftPreserved = true, initialUpdatingStatusPolled = true, globalSaveMutualExclusion = true, pageCancellation = true, realWeatherOrCredentialsTouched = false };
        }
        finally
        {
            window.Shutdown(); timeout.Cancel();
            try { await backend; } catch (OperationCanceledException) { }
        }
    }

    private static IEnumerable<DependencyObject> WalkWeather(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in WalkWeather(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
