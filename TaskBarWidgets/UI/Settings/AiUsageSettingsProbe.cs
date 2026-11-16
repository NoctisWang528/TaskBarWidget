using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace TaskBarWidgets.UI;

public sealed partial class SettingsWindow
{
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendAiUsageProbeMessage(nint window, uint message, nuint wParam, nint lParam);

    internal static async Task<object> ProbeAiUsageControlsAsync(JsonObject native, JsonObject media, string outputDirectory)
    {
        using var connection = new UiConnection("TaskBarWidgets.UI.ai-probe.disconnected");
        var window = new SettingsWindow(connection);
        var reads = 0;
        var state = "partial";
        var providerId = "";
        var enabledServices = new Dictionary<string, bool> { ["openai"] = false, ["deepseek"] = false, ["gemini"] = false, ["claude"] = false };
        var showApi = false;
        var hasCredential = false;
        var configured = 0;
        var failConfigure = false;
        var cleared = 0;
        var manualWrites = 0;
        var globalWrites = 0;
        TaskCompletionSource<bool>? mutationRelease = null;
        var mutationStarted = false;
        var manualDays = new JsonArray();
        double? manualPercent = null;
        var holdNextRead = false;
        TaskCompletionSource<JsonObject>? heldRead = null;
        JsonObject? heldSnapshot = null;
        var providerDefinitions = new[] {
            ("codex", "ChatGPT", "subscription", "openai"), ("openai-api", "ChatGPT API", "api", "openai"),
            ("deepseek-web", "DeepSeek", "subscription", "deepseek"), ("deepseek-api", "DeepSeek API", "api", "deepseek"),
            ("gemini-web", "Gemini", "subscription", "gemini"), ("gemini-api", "Gemini API", "api", "gemini"),
            ("claude-web", "Claude", "subscription", "claude"), ("claude-api", "Claude API", "api", "claude") };
        var updatedAt = DateTimeOffset.Now.ToString("O");
        var days = new JsonArray();
        var today = DateOnly.FromDateTime(DateTime.Now);
        for (var i = 0; i < 80; i++)
            if (i != 9) days.Add(new JsonObject { ["date"] = today.AddDays(-i).ToString("yyyy-MM-dd"), ["tokens"] = i == 5 ? 0 : i * 123456L });
        window.ProbeRequestHandler = async (action, payload) => {
            if (action == "applyNativeSettings")
            {
                foreach (var pair in payload!.AsObject())
                    native["fields"]!.AsArray().OfType<JsonObject>().Single(field => Text(field["key"]) == pair.Key)["value"] = pair.Value?.DeepClone();
                globalWrites++;
                return (JsonObject)native.DeepClone();
            }
            if (action == "applyMediaSettings")
            {
                foreach (var pair in payload!.AsObject()) media[pair.Key] = pair.Value?.DeepClone();
                globalWrites++;
                return (JsonObject)media.DeepClone();
            }
            if (action is "configureAiUsage" or "saveAiUsageManual" or "setAiUsagePreferences" && mutationRelease is { } release)
            {
                mutationStarted = true;
                await release.Task;
            }
            if (action == "getAiUsage") reads++;
            else if (action == "setAiUsagePreferences")
            {
                if (payload?["showApi"] is JsonValue enabled) showApi = enabled.GetValue<bool>();
                if (payload?["providerId"] is JsonValue chosen) providerId = chosen.GetValue<string>();
                if (payload?["enabledServices"] is JsonObject servicePatch)
                    foreach (var service in servicePatch) enabledServices[service.Key] = service.Value!.GetValue<bool>();
                var selectedProvider = providerDefinitions.FirstOrDefault(item => item.Item1 == providerId);
                if (selectedProvider.Item1 is null || !enabledServices[selectedProvider.Item4] || (!showApi && selectedProvider.Item3 == "api"))
                    providerId = providerDefinitions.FirstOrDefault(item => item.Item3 == "subscription" && enabledServices[item.Item4]).Item1 ?? "";
            }
            else if (action == "configureAiUsage")
            {
                if (failConfigure) throw new InvalidOperationException("synthetic credential validation rejected");
                configured++;
                if (payload?["clearCredential"]?.GetValue<bool>() == true) { hasCredential = false; cleared++; }
                else if (payload?["credential"] is JsonValue secret)
                {
                    if (secret.GetValue<string>() != "synthetic-write-only-key") throw new InvalidOperationException("Unexpected fixture credential.");
                    hasCredential = true;
                }
            }
            else if (action == "saveAiUsageManual")
            {
                manualWrites++; manualDays = (JsonArray)payload!["days"]!.DeepClone();
                manualPercent = payload["remainingPercent"]?.GetValue<double>();
            }
            else return new JsonObject();
            var api = providerId.EndsWith("-api", StringComparison.Ordinal);
            var catalog = new JsonArray(providerDefinitions.Select(item => (JsonNode)new JsonObject {
                ["id"] = item.Item1, ["name"] = item.Item2, ["mode"] = item.Item3, ["serviceId"] = item.Item4 }).ToArray());
            var result = new JsonObject {
                ["revision"] = state == "error" ? 2 : 1, ["providerId"] = providerId, ["providerName"] = providerDefinitions.FirstOrDefault(item => item.Item1 == providerId).Item2 ?? "", ["status"] = providerId.Length == 0 ? "disabled" : state,
                ["enabledServices"] = new JsonObject(enabledServices.Select(item => new KeyValuePair<string, JsonNode?>(item.Key, JsonValue.Create(item.Value)))),
                ["providers"] = catalog, ["showApi"] = showApi, ["sourceLabel"] = providerId == "codex" ? "官方 Codex" : "测试服务来源",
                ["activityUnit"] = api || providerId == "codex" ? "tokens" : "requests", ["historyNote"] = providerId == "codex" ? "官方 Token 活动" : "手动记录，不是自动读取",
                ["activityUtc"] = providerId is "openai-api" or "gemini-api" or "claude-api",
                ["quotaNote"] = "测试额度来源说明", ["hasCredential"] = hasCredential, ["credentialKind"] = "apiKey", ["projectId"] = state == "loading" ? "" : "fixture-project",
                ["balances"] = providerId == "deepseek-api" ? new JsonArray(new JsonObject { ["currency"] = "CNY", ["available"] = "12.3456", ["label"] = "官方余额" }) : new JsonArray(),
                ["error"] = state == "error" ? "模拟官方客户端尚未登录" : "部分官方统计尚未返回", ["updatedAt"] = updatedAt,
                ["quotas"] = state == "error" || providerId.Length == 0 ? new JsonArray() : providerId == "codex"
                    ? new JsonArray(new JsonObject { ["id"] = "primary", ["label"] = "每周额度", ["usedPercent"] = 34.5, ["windowMinutes"] = 10080, ["resetsAt"] = 1900000000L })
                    : !api && manualPercent.HasValue ? new JsonArray(new JsonObject { ["id"] = "manual", ["label"] = "手动剩余额度", ["usedPercent"] = 100 - manualPercent.Value }) : new JsonArray(),
                ["days"] = state == "error" || providerId.Length == 0 ? null : providerId == "codex" ? days.DeepClone() : manualDays.DeepClone(), ["lifetimeTokens"] = state == "error" || providerId != "codex" ? null : JsonValue.Create(2976664580L)
            };
            if (action == "getAiUsage" && holdNextRead)
            {
                holdNextRead = false; heldSnapshot = result;
                heldRead = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
                return await heldRead.Task;
            }
            return result;
        };
        IEnumerable<FrameworkElement> Walk(DependencyObject root)
        {
            if (root is FrameworkElement element) yield return element;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (var child in Walk(VisualTreeHelper.GetChild(root, i))) yield return child;
        }
        T Find<T>(string id) where T : FrameworkElement => Walk(window._root).OfType<T>().Single(element => AutomationProperties.GetAutomationId(element) == id);
        async Task InvokeButton(Button button)
        {
            var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(button);
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
            await Task.Delay(100);
        }
        Task Invoke(string id) => InvokeButton(Find<Button>(id));
        async Task AssertActiveWriteGuards(Func<Task> startWrite)
        {
            if (window.HasPendingChanges) throw new InvalidOperationException("AI write-guard fixture must start without unrelated drafts.");
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            mutationRelease = release; mutationStarted = false;
            var pageCancellation = window._aiUsageCancellation;
            var provider = Find<ComboBox>("ai-usage-provider");
            try
            {
                await startWrite();
                if (!mutationStarted || !window._aiUsageMutating || !window.HasPendingChanges || provider.IsEnabled || window._apply.IsEnabled)
                    throw new InvalidOperationException("An active AI write did not guard controls and the updater's pending-changes gate.");
                var sources = window.NavigationPages().Single(item => Equals(item.Tag, "sources"));
                window._navigation.SelectedItem = sources;
                window.NavigateTo("sources");
                if (window._currentPage != "aiUsage" || !Equals((window._navigation.SelectedItem as NavigationViewItem)?.Tag, "aiUsage") ||
                    !ReferenceEquals(pageCancellation, window._aiUsageCancellation) || pageCancellation!.IsCancellationRequested)
                    throw new InvalidOperationException("Navigation abandoned an active AI write.");
                window.ProbeRenderPage();
                if (!ReferenceEquals(provider, Find<ComboBox>("ai-usage-provider")))
                    throw new InvalidOperationException("Rerender replaced controls during an active AI write.");
                await window.ReloadAsync();
                await window.SaveAsync();
                // WM_CLOSE exercises AppWindow.Closing; Window.Close bypasses it.
                SendAiUsageProbeMessage(WinRT.Interop.WindowNative.GetWindowHandle(window), 0x0010, 0, 0);
                await Task.Delay(80);
                if (window._closed || window._shutdown || !window._notice.IsOpen || window._notice.Severity != InfoBarSeverity.Informational ||
                    window._currentPage != "aiUsage" || pageCancellation.IsCancellationRequested || !window.HasPendingChanges)
                    throw new InvalidOperationException("Normal closing or reload abandoned an active AI write.");
            }
            finally
            {
                mutationRelease = null;
                release.TrySetResult(true);
            }
            for (var attempt = 0; window._aiUsageMutating && attempt < 100; attempt++) await Task.Delay(10);
            if (window._aiUsageMutating || window.HasPendingChanges || !provider.IsEnabled)
                throw new InvalidOperationException("AI write guards did not release after completion.");
        }
        async Task AssertDraftsSurviveApply()
        {
            var password = Find<PasswordBox>("ai-usage-credential");
            var project = Find<TextBox>("ai-usage-project");
            var csv = Find<TextBox>("ai-usage-manual-days");
            var remaining = Find<TextBox>("ai-usage-manual-remaining");
            var values = new[] { password.Password, project.Text, csv.Text, remaining.Text };
            var cancellation = window._aiUsageCancellation;
            var renders = window.PageRenderCount;
            var writes = globalWrites;
            window.Changed();
            if (!window._apply.IsEnabled) throw new InvalidOperationException("Global Apply fixture had no pending settings.");
            await InvokeButton(window._apply);
            window.ProbeRenderPage();
            if (window.HasPendingChanges || globalWrites <= writes || window.PageRenderCount != renders ||
                !ReferenceEquals(cancellation, window._aiUsageCancellation) ||
                !ReferenceEquals(password, Find<PasswordBox>("ai-usage-credential")) || !ReferenceEquals(project, Find<TextBox>("ai-usage-project")) ||
                !ReferenceEquals(csv, Find<TextBox>("ai-usage-manual-days")) || !ReferenceEquals(remaining, Find<TextBox>("ai-usage-manual-remaining")) ||
                !values.SequenceEqual(new[] { password.Password, project.Text, csv.Text, remaining.Text }))
                throw new InvalidOperationException("Global Apply or same-page rerender replaced live AI inputs or lost their drafts.");
        }
        async Task Select(string id)
        {
            var selector = Find<ComboBox>("ai-usage-provider");
            selector.SelectedItem = selector.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, id));
            await Task.Delay(100);
        }
        var screenshots = new List<object>();
        var providerScreenshots = new List<object>();
        async Task CaptureProvider(string name, FrameworkElement target)
        {
            target.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = .5 });
            await Task.Delay(100);
            window._root.UpdateLayout();
            var png = Path.Combine(outputDirectory, $"settings-ai-usage-{name}.png");
            await UiLayoutProbe.CaptureAsync(window._root, png);
            providerScreenshots.Add(new { provider = name, png });
        }
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var credentialFixture = Path.Combine(outputDirectory, "ai-credential-reader-fixture.json");
            try
            {
                const string fixtureContent = "{\"type\":\"service_account\",\"fixture\":true}";
                await File.WriteAllTextAsync(credentialFixture, fixtureContent, new System.Text.UTF8Encoding(true));
                if (await ReadAiCredentialFileAsync(credentialFixture, CancellationToken.None) != fixtureContent)
                    throw new InvalidOperationException("Credential reader did not handle a UTF-8 BOM.");
                await File.WriteAllBytesAsync(credentialFixture, new byte[128 * 1024 + 1]);
                var oversizedRejected = false;
                try { await ReadAiCredentialFileAsync(credentialFixture, CancellationToken.None); }
                catch (InvalidDataException) { oversizedRejected = true; }
                if (!oversizedRejected) throw new InvalidOperationException("Credential reader accepted more than 128 KB.");
            }
            finally { if (File.Exists(credentialFixture)) File.Delete(credentialFixture); }
            UiTemplateProbe.ShowOffscreen(window, 1180, 1100);
            window.LoadValidationData((JsonObject)native.DeepClone(), (JsonObject)media.DeepClone());
            var nativeToggle = native["fields"]!.AsArray().OfType<JsonObject>().First(field =>
                !Text(field["key"]).StartsWith("items.", StringComparison.Ordinal) && field["value"] is JsonValue value && value.TryGetValue<bool>(out _));
            window._native.Set(Text(nativeToggle["key"]), !nativeToggle["value"]!.GetValue<bool>());
            window._media.Set("enableQQMusic", !window._media.Bool("enableQQMusic"));
            window.ShowPage("aiUsage");
            await Task.Delay(120);
            if (Find<ComboBox>("ai-usage-provider").Items.Count != 0 || Find<Grid>("ai-usage-heatmap").Children.Count != 0 || Find<TextBlock>("ai-usage-empty").Visibility != Visibility.Visible || enabledServices.Values.Any(enabled => enabled))
                throw new InvalidOperationException("AI services were not all disabled by default.");
            await CaptureProvider("disabled", Find<TextBlock>("ai-usage-empty"));
            var disabledReads = reads;
            await Task.Delay(2150);
            if (reads != disabledReads) throw new InvalidOperationException("Disabled AI services continued polling.");
            var expectedNames = new[] { "ChatGPT", "DeepSeek", "Gemini", "Claude" };
            var serviceIds = new[] { "openai", "deepseek", "gemini", "claude" };
            for (var index = 0; index < serviceIds.Length; index++)
            {
                var toggle = Find<ToggleSwitch>("ai-usage-enabled-" + serviceIds[index]);
                if (!Equals(toggle.Header, expectedNames[index])) throw new InvalidOperationException("AI service names were not exact.");
                toggle.IsOn = true;
                await Task.Delay(100);
                if (!Find<ComboBox>("ai-usage-provider").Items.OfType<ComboBoxItem>().Select(item => item.Content as string).SequenceEqual(expectedNames.Take(index + 1)))
                    throw new InvalidOperationException("Enabling one service did not expose only the enabled names.");
            }
            if (Find<ComboBox>("ai-usage-provider").Items.Count != 4 || Find<ToggleSwitch>("ai-usage-show-api").IsOn || Find<StackPanel>("ai-usage-credentials").Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("API providers or credential controls were enabled by default.");
            foreach (var width in new[] { 720, 1180 })
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                window.AppWindow.Resize(new SizeInt32(width, 1100));
                window._root.RequestedTheme = theme;
                await Task.Delay(120);
                window._root.UpdateLayout();
                var heatmap = Find<Grid>("ai-usage-heatmap");
                if (heatmap.Children.Count != 84 || heatmap.ActualWidth <= 0 || heatmap.ActualHeight <= 0 || heatmap.ActualWidth > window._page.ActualWidth)
                    throw new InvalidOperationException("AI heatmap did not realize or fit its page.");
                if (heatmap.Children.OfType<Button>().Any(cell => Math.Abs(cell.ActualWidth - cell.ActualHeight) > 1))
                    throw new InvalidOperationException("AI heatmap cells were not square.");
                var names = heatmap.Children.OfType<Button>().Select(AutomationProperties.GetName).ToArray();
                if (!names.Any(name => name.Contains("未返回记录")) || !names.Any(name => name.Contains("0 Token")))
                    throw new InvalidOperationException("AI unknown records and zero records were conflated.");
                var png = Path.Combine(outputDirectory, $"settings-ai-usage-{theme.ToString().ToLowerInvariant()}-{width}.png");
                window._pageScroll!.ChangeView(null, 0, null, true);
                await Task.Delay(80);
                await UiLayoutProbe.CaptureAsync(window._root, png);
                heatmap.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = .5 });
                await Task.Delay(120);
                window._root.UpdateLayout();
                var viewport = window._pageScroll;
                var bounds = heatmap.TransformToVisual(viewport).TransformBounds(new Windows.Foundation.Rect(0, 0, heatmap.ActualWidth, heatmap.ActualHeight));
                if (bounds.X < -1 || bounds.Y < -1 || bounds.Right > viewport.ActualWidth + 1 || bounds.Bottom > viewport.ActualHeight + 1)
                    throw new InvalidOperationException("AI heatmap was not fully visible within the scrolled viewport.");
                var heatmapPng = Path.Combine(outputDirectory, $"settings-ai-usage-heatmap-{theme.ToString().ToLowerInvariant()}-{width}.png");
                await UiLayoutProbe.CaptureAsync(window._root, heatmapPng);
                screenshots.Add(new { width, theme = theme.ToString(), png, heatmapPng, heatmapVisible = true,
                    heatmapWidth = heatmap.ActualWidth, heatmapTop = bounds.Y, heatmapBottom = bounds.Bottom, viewportHeight = viewport.ActualHeight });
            }
            var steadyHeatmap = Find<Grid>("ai-usage-heatmap");
            var firstCell = steadyHeatmap.Children[0];
            var beforePoll = reads;
            await Task.Delay(2150);
            if (reads <= beforePoll || !ReferenceEquals(firstCell, steadyHeatmap.Children[0]))
                throw new InvalidOperationException("Unchanged AI polling replaced realized heatmap cells.");
            holdNextRead = true;
            await Invoke("ai-usage-refresh");
            if (heldRead is null) throw new InvalidOperationException("AI stale-response fixture did not start.");
            await Select("deepseek-web");
            heldRead.SetResult(heldSnapshot!);
            await Task.Delay(100);
            if (providerId != "deepseek-web" || Find<TextBlock>("ai-usage-source-note").Text.Contains("官方 Codex"))
                throw new InvalidOperationException("A stale response overwrote the selected provider.");
            Find<ToggleSwitch>("ai-usage-show-api").IsOn = true;
            await Task.Delay(100);
            if (Find<ComboBox>("ai-usage-provider").Items.Count != 8) throw new InvalidOperationException("API toggle did not expose all eight providers.");
            await Select("deepseek-api");
            if (Find<TextBox>("ai-usage-manual-remaining").Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("DeepSeek API exposed subscription-only manual percentages.");
            var balanceText = string.Join(" ", Walk(Find<StackPanel>("ai-usage-quotas")).OfType<TextBlock>().Select(item => item.Text));
            if (!balanceText.Contains("CNY 12.3456") || Walk(Find<StackPanel>("ai-usage-quotas")).OfType<ProgressBar>().Any())
                throw new InvalidOperationException("Currency balance was converted to a quota percentage.");
            await CaptureProvider("deepseek-balance", Find<StackPanel>("ai-usage-quotas"));
            var password = Find<PasswordBox>("ai-usage-credential");
            password.Password = "synthetic-write-only-key";
            Find<TextBox>("ai-usage-manual-days").Text = "unfinished manual draft";
            Find<TextBox>("ai-usage-manual-remaining").Text = "42";
            await AssertDraftsSurviveApply();
            failConfigure = true;
            await Invoke("ai-usage-save-credential");
            if (password.Password != "synthetic-write-only-key" || configured != 0 || Find<TextBlock>("ai-usage-status").Text.Contains("synthetic credential"))
                throw new InvalidOperationException("Rejected credentials were erased or internal error details were exposed.");
            if (window._aiUsageMutating || window.HasPendingChanges) throw new InvalidOperationException("Rejected AI writes left guards active.");
            failConfigure = false;
            await AssertActiveWriteGuards(() => Invoke("ai-usage-save-credential"));
            if (!hasCredential || password.Password.Length != 0 || configured != 1) throw new InvalidOperationException("Credential was not write-only.");
            await Invoke("ai-usage-save-credential");
            if (!hasCredential || configured != 2) throw new InvalidOperationException("Blank credential did not retain the saved value.");
            await Invoke("ai-usage-clear-credential");
            if (hasCredential || cleared != 1) throw new InvalidOperationException("Explicit credential clearing failed.");
            state = "loading";
            await Select("gemini-api");
            state = "partial";
            await Invoke("ai-usage-refresh");
            if (Find<TextBox>("ai-usage-project").Text != "fixture-project")
                throw new InvalidOperationException("The initial loading snapshot prevented project prefill on completion.");
            var utcToday = DateOnly.FromDateTime(DateTime.UtcNow);
            var utcStart = utcToday.AddDays(-((int)utcToday.DayOfWeek + 6) % 7 - 77);
            if (!Find<TextBlock>("ai-usage-date-basis").Text.Contains($"UTC · 今日 {utcToday:yyyy-MM-dd}") ||
                !AutomationProperties.GetName((Button)Find<Grid>("ai-usage-heatmap").Children[0]).StartsWith($"{utcStart:yyyy-MM-dd}（UTC）", StringComparison.Ordinal))
                throw new InvalidOperationException("UTC history did not use and disclose the UTC date anchor.");
            if (Find<PasswordBox>("ai-usage-credential").Visibility != Visibility.Collapsed || Find<TextBox>("ai-usage-project").Visibility != Visibility.Visible || Find<Button>("ai-usage-pick-json").Visibility != Visibility.Visible)
                throw new InvalidOperationException("Gemini did not request the project/service-account flow.");
            await CaptureProvider("gemini-connection", Find<StackPanel>("ai-usage-credentials"));
            state = "loading";
            await Select("gemini-web");
            await Select("gemini-api");
            Find<TextBox>("ai-usage-project").Text = "draft-project";
            state = "partial";
            await Invoke("ai-usage-refresh");
            if (Find<TextBox>("ai-usage-project").Text != "draft-project")
                throw new InvalidOperationException("The completed initial snapshot overwrote the user's project draft.");
            Find<TextBox>("ai-usage-project").Text = "";
            password.Password = "unsaved-draft";
            var csv = Find<TextBox>("ai-usage-manual-days");
            csv.Text = "unsaved CSV draft";
            Find<TextBox>("ai-usage-manual-remaining").Text = "42";
            await Task.Delay(2150);
            if (Find<TextBox>("ai-usage-project").Text != "" || password.Password != "unsaved-draft" || csv.Text != "unsaved CSV draft" || Find<TextBox>("ai-usage-manual-remaining").Text != "42")
                throw new InvalidOperationException("Polling overwrote an unfinished connection or manual draft.");
            window._media.Set("enableQQMusic", !window._media.Bool("enableQQMusic"));
            await AssertDraftsSurviveApply();
            await Select("claude-api");
            Find<ToggleSwitch>("ai-usage-show-api").IsOn = false;
            await Task.Delay(100);
            if (providerId != "claude-web" || Find<ComboBox>("ai-usage-provider").Items.Count != 4 || Find<StackPanel>("ai-usage-credentials").Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Disabling APIs did not return to the matching subscription.");
            if (cleared != 1) throw new InvalidOperationException("Hiding API providers cleared their credentials.");
            Find<TextBox>("ai-usage-manual-days").Text = $"{today:yyyy-MM-dd},0\r{today.AddDays(-1):yyyy-MM-dd},12";
            Find<TextBox>("ai-usage-manual-remaining").Text = "75";
            await AssertActiveWriteGuards(() => Invoke("ai-usage-save-manual"));
            if (manualWrites != 1 || !Find<Grid>("ai-usage-heatmap").Children.OfType<Button>().Any(cell => AutomationProperties.GetName(cell).Contains("12 次")))
            {
                var names = Find<Grid>("ai-usage-heatmap").Children.OfType<Button>().Select(AutomationProperties.GetName).ToArray();
                var sample = string.Join(" | ", names.Where(name => name.StartsWith(today.ToString("yyyy-MM-dd"), StringComparison.Ordinal) || name.StartsWith(today.AddDays(-1).ToString("yyyy-MM-dd"), StringComparison.Ordinal)).Take(2));
                var input = Find<TextBox>("ai-usage-manual-days").Text;
                var selectedUnit = providerId.EndsWith("-api", StringComparison.Ordinal) || providerId == "codex" ? "tokens" : "requests";
                throw new InvalidOperationException($"Manual request activity did not use the correct unit. writes={manualWrites}; provider={providerId}; selectedUnit={selectedUnit}; cells={names.Length}; CR={input.Count(character => character == '\r')}; LF={input.Count(character => character == '\n')}; samples={sample}; status={Find<TextBlock>("ai-usage-status").Text}");
            }
            if (!Find<TextBlock>("ai-usage-source-note").Text.Contains("暂不支持自动读取")) throw new InvalidOperationException("Subscription limitation was not disclosed.");
            await Select("deepseek-web");
            state = "loading";
            await Select("claude-web");
            state = "partial";
            await Invoke("ai-usage-refresh");
            if (!Find<TextBox>("ai-usage-manual-days").Text.Contains($"{today.AddDays(-1):yyyy-MM-dd},12"))
                throw new InvalidOperationException("Saved manual activity was not loaded for editing.");
            if (Find<TextBox>("ai-usage-manual-remaining").Text != "75")
                throw new InvalidOperationException("Saved manual percentage was not loaded for editing.");
            Find<TextBox>("ai-usage-manual-days").Text += $"\n{today.AddDays(-2):yyyy-MM-dd},3";
            await Invoke("ai-usage-save-manual");
            if (manualPercent != 75 || manualWrites != 2)
                throw new InvalidOperationException("Editing only CSV erased the existing manual percentage.");
            await CaptureProvider("claude-manual", Find<Border>("ai-usage-manual"));
            await AssertActiveWriteGuards(async () => {
                Find<ToggleSwitch>("ai-usage-enabled-claude").IsOn = false;
                await Task.Delay(100);
            });
            if (providerId == "claude-web" || Find<ComboBox>("ai-usage-provider").Items.OfType<ComboBoxItem>().Any(item => (item.Tag as string)?.StartsWith("claude", StringComparison.Ordinal) == true) ||
                Find<TextBlock>("ai-usage-source-note").Text.Contains("测试服务来源"))
                throw new InvalidOperationException("Disabling the selected service left its source or data visible.");
            foreach (var id in new[] { "openai", "deepseek", "gemini" })
            {
                Find<ToggleSwitch>("ai-usage-enabled-" + id).IsOn = false;
                await Task.Delay(100);
            }
            if (providerId.Length != 0 || Find<ComboBox>("ai-usage-provider").Items.Count != 0 || Find<Grid>("ai-usage-heatmap").Children.Count != 0 ||
                Find<Border>("ai-usage-connection").Visibility != Visibility.Collapsed || Find<Border>("ai-usage-activity").Visibility != Visibility.Collapsed ||
                Find<Border>("ai-usage-quota-section").Visibility != Visibility.Collapsed || Find<TextBlock>("ai-usage-empty").Visibility != Visibility.Visible)
                throw new InvalidOperationException("Disabling every service did not clear and hide its data sections.");
            var allDisabledReads = reads;
            await Task.Delay(2150);
            if (reads != allDisabledReads) throw new InvalidOperationException("All-disabled services still made refresh requests.");
            Find<ToggleSwitch>("ai-usage-enabled-claude").IsOn = true;
            await Task.Delay(100);
            if (providerId != "claude-web" || Find<TextBox>("ai-usage-manual-remaining").Text != "75" || !Find<TextBox>("ai-usage-manual-days").Text.Contains($"{today.AddDays(-2):yyyy-MM-dd},3") || cleared != 1)
                throw new InvalidOperationException("Re-enabling a service lost its stored records or cleared credentials.");
            state = "error";
            await Invoke("ai-usage-refresh");
            if (!Find<TextBlock>("ai-usage-status").Text.Contains("模拟官方客户端尚未登录")) throw new InvalidOperationException("AI error state was not visible.");
            var cardAvailable = QuickCardDefinitions.Any(card => card.Id == "aiUsage" && card.Label == "AI 用量");
            window.ShowPage("sources");
            var stoppedAt = reads;
            await Task.Delay(2150);
            if (reads != stoppedAt || window._aiUsageCancellation is not null) throw new InvalidOperationException("AI polling survived navigation.");
            return new { passed = true, providerCount = 4, apiProviderCount = 8, servicesDefaultOff = true, exactServiceNames = true, independentServiceVisibility = true,
                allDisabledNoPolling = true, reenabledRecordsRetained = true, apiDefaultOff = true, apiToggleFallback = true, balanceUnitPreserved = true,
                credentialsWriteOnly = true, credentialFileBounded = true, blankCredentialRetained = true, explicitCredentialClear = true, credentialFailureRetained = true, draftsSurvivePolling = true,
                draftsSurviveGlobalApply = true, liveInputsRetained = true, activeWriteNavigationGuard = true, activeWriteCloseGuard = true, activeWriteUpdateGate = true, writeGuardsReleased = true,
                geminiProjectFlow = true, utcDateAnchor = true, initialLoadPreservesDraft = true, manualRequestUnits = true, manualHistoryLoaded = true, manualPercentRoundTrip = true, staleResponseRejected = true,
                cells = 84, squareCells = true, unchangedCellsPreserved = true, unknownDistinctFromZero = true, errorVisible = true, pollingReleased = true, cardAvailable, screenshots, providerScreenshots };
        }
        finally { window.Shutdown(); }
    }
}
