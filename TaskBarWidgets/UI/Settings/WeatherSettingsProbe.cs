using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
        var jobs = new Dictionary<long, JsonObject>();
        long nextJobId = 0;
        JsonObject Status() => new() {
            ["provider"] = provider, ["city"] = savedCity.DeepClone(), ["apiHost"] = apiHost, ["hasApiKey"] = hasKey,
            ["updating"] = false, ["message"] = refreshed ? "晴" : "尚未刷新", ["temperature"] = refreshed ? "23°C" : "",
            ["lastAttempt"] = "2026-10-16T10:00:00", ["lastSuccess"] = refreshed ? "2026-10-16T10:00:00" : "" };

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
                            apiHost = Text(payload["apiHost"]);
                            if (payload["clearApiKey"]?.ToString() == "true") { hasKey = false; cleared = true; }
                            else if (payload.ContainsKey("apiKey"))
                            {
                                if (Text(payload["apiKey"]) != "synthetic-probe-key") throw new InvalidOperationException("Explicit key write was not preserved.");
                                hasKey = true;
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
            window.ShowPage("hardware");
            window.LoadValidationData(new JsonObject {
                ["groups"] = new JsonArray(new JsonObject { ["id"] = "builtins.weather", ["page"] = "hardware", ["label"] = "天气" }),
                ["fields"] = new JsonArray(
                    new JsonObject { ["key"] = "builtins.weatherpro.dll.config.city_no", ["group"] = "builtins.weather", ["type"] = "string", ["label"] = "城市", ["value"] = "101010100" },
                    new JsonObject { ["key"] = "items.native_0.label", ["itemId"] = "native_0", ["itemName"] = "CPU", ["value"] = "CPU" },
                    new JsonObject { ["key"] = "items.native_0.show", ["value"] = true }) }, new JsonObject());
            window.AppWindow.IsShownInSwitchers = false;
            window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-30000, -30000, 900, 1000)); window.AppWindow.Show(false);
            while (window._weatherPage?.HasStatus != true || window._weatherPage.LoadingStatus || statusReads < 2 || window._weatherPage.Status.Text.Contains("正在更新")) await Task.Delay(10, timeout.Token);
            var page = window._weatherPage ?? throw new InvalidOperationException("Weather settings were not created.");
            window._native.Set("builtins.weatherpro.dll.config.show_weather_icon", true); window.UpdateSaveStatus();
            if (!window._apply.IsEnabled) throw new InvalidOperationException("Synthetic native draft must be applicable before testing weather write exclusion.");
            window._busy = true; window.UpdateSaveStatus();
            if (page.Search.IsEnabled || page.SaveCredentials.IsEnabled || page.Refresh.IsEnabled) throw new InvalidOperationException("Global settings save did not disable weather operations.");
            await window.SaveWeatherCredentialsAsync(page, false);
            if (configureCount != 0) throw new InvalidOperationException("Weather wrote credentials during global settings save.");
            window._busy = false; window.UpdateSaveStatus();
            page.Query.Text = "南京";
            var staleSearch = window.SearchWeatherCityAsync(page);
            while (!searchStarted) await Task.Delay(10, timeout.Token);
            page.Provider.SelectedIndex = 1;
            await staleSearch.WaitAsync(timeout.Token);
            if (page.Results.Items.Count != 0 || Text(page.SavedCity?["id"]) != "101010100") throw new InvalidOperationException("Changing provider accepted stale search results or changed saved city.");
            await window.SearchWeatherCityAsync(page).WaitAsync(timeout.Token);
            if (page.Results.Items.Count != 1 || page.CityCode.Text != "101010100" || configureCount != 0) throw new InvalidOperationException("Search changed the saved city before selection/save.");
            page.Results.SelectedIndex = 0;
            if (page.CityCode.Text != "101190101" || !page.SaveCity.IsEnabled) throw new InvalidOperationException("Selecting a city did not populate its read-only code.");
            var citySave = window.SaveWeatherCityAsync(page);
            if (window._apply.IsEnabled) throw new InvalidOperationException("Weather commit did not disable global Apply.");
            await citySave.WaitAsync(timeout.Token);
            if (Text(savedCity["id"]) != "101190101" || provider != 1 || !page.LastSuccess.Text.Contains("2026-10-16")) throw new InvalidOperationException("City save did not apply the chosen provider/city and weather status.");
            page.ApiHost.Text = "probe.qweatherapi.com"; page.ApiKey.Password = "synthetic-probe-key";
            await window.SaveWeatherCredentialsAsync(page, false).WaitAsync(timeout.Token);
            if (!hasKey || page.ApiKey.Password.Length != 0 || !page.KeyStatus.Text.Contains("已保存")) throw new InvalidOperationException("Key was not write-only or persisted status is missing.");
            await window.SaveWeatherCredentialsAsync(page, false).WaitAsync(timeout.Token);
            if (!hasKey) throw new InvalidOperationException("Blank password replaced the stored key.");
            await window.SaveWeatherCredentialsAsync(page, true).WaitAsync(timeout.Token);
            if (!cleared || hasKey) throw new InvalidOperationException("Explicit key clear did not work.");
            await window.RefreshWeatherAsync(page).WaitAsync(timeout.Token);
            if (page.Message.Severity != InfoBarSeverity.Error || !page.Message.Message.Contains("模拟天气连接错误")) throw new InvalidOperationException("Refresh failure was silently hidden.");
            await window.RefreshWeatherAsync(page).WaitAsync(timeout.Token);
            if (page.Message.Severity != InfoBarSeverity.Success) throw new InvalidOperationException("Refresh could not recover after failure.");
            var pending = window.SearchWeatherCityAsync(page);
            window.ShowPage("general"); await pending.WaitAsync(timeout.Token);
            if (window._weatherPage is not null || activated) throw new InvalidOperationException("Leaving the weather page retained it or probe activated a window.");
            if (backend.IsFaulted) await backend;
            return new { typedCitySearch = true, explicitSelectionAndSave = true, staleProviderResultsDiscarded = true, writeOnlyPassword = true, blankKeyPreserved = true, explicitClear = true, refreshErrorAndRecovery = true, initialUpdatingStatusPolled = true, globalSaveMutualExclusion = true, pageCancellation = true, realWeatherOrCredentialsTouched = false };
        }
        finally
        {
            window.Shutdown(); timeout.Cancel();
            try { await backend; } catch (OperationCanceledException) { }
        }
    }
}
