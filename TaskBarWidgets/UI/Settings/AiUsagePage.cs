using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TaskBarWidgets.UI;

public sealed partial class SettingsWindow
{
    private CancellationTokenSource? _aiUsageCancellation;
    private static async Task<string> ReadAiCredentialFileAsync(string path, CancellationToken token)
    {
        const int maximum = 128 * 1024;
        await using var stream = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read,
            System.IO.FileShare.Read, 4096, System.IO.FileOptions.Asynchronous | System.IO.FileOptions.SequentialScan);
        if (stream.Length > maximum) throw new System.IO.InvalidDataException();
        var bytes = new byte[maximum + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), token);
            if (read == 0) break;
            count += read;
        }
        if (count > maximum) throw new System.IO.InvalidDataException();
        var offset = count >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        return new System.Text.UTF8Encoding(false, true).GetString(bytes, offset, count - offset);
    }
    private void LeaveAiUsagePage()
    {
        _aiUsageCancellation?.Cancel();
        _aiUsageCancellation?.Dispose();
        _aiUsageCancellation = null;
    }

    private void RenderAiUsage()
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _aiUsageCancellation = cancellation;
        var token = cancellation.Token;
        var services = Section("AI 服务", "仅显示你打开的服务。关闭服务会保留已保存的连接与手动记录。");
        var serviceDefinitions = new[] { (Id: "openai", Name: "ChatGPT"), (Id: "deepseek", Name: "DeepSeek"), (Id: "gemini", Name: "Gemini"), (Id: "claude", Name: "Claude") };
        var serviceToggles = new Dictionary<string, ToggleSwitch>();
        foreach (var service in serviceDefinitions)
        {
            var toggle = new ToggleSwitch { Header = service.Name, IsOn = false };
            AutomationProperties.SetAutomationId(toggle, "ai-usage-enabled-" + service.Id);
            serviceToggles[service.Id] = toggle; services.Children.Add(toggle);
        }
        var emptyHint = Copy("打开需要查看的 AI 服务。");
        AutomationProperties.SetAutomationId(emptyHint, "ai-usage-empty");
        services.Children.Add(emptyHint);
        var showApi = new ToggleSwitch { Header = "显示 API 用量", IsOn = false };
        AutomationProperties.SetAutomationId(showApi, "ai-usage-show-api");
        services.Children.Add(showApi);
        var connection = Section("连接与服务", "订阅与 API 分开统计；只有真实返回或你明确录入的数据会显示为已知用量。");
        var connectionSurface = (Border)_page.Children.Last();
        AutomationProperties.SetAutomationId(connectionSurface, "ai-usage-connection");
        connectionSurface.Visibility = Visibility.Collapsed;
        var providers = new ComboBox { Header = "AI 服务", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(providers, "AI 服务");
        AutomationProperties.SetAutomationId(providers, "ai-usage-provider");
        connection.Children.Add(providers);
        var sourceNote = Copy("");
        AutomationProperties.SetAutomationId(sourceNote, "ai-usage-source-note");
        connection.Children.Add(sourceNote);
        var official = new HyperlinkButton { Content = "查看官方用量", Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(official, "ai-usage-official");
        connection.Children.Add(official);
        var status = Copy("正在读取服务…");
        AutomationProperties.SetAutomationId(status, "ai-usage-status");
        connection.Children.Add(status);
        var credentialPanel = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(credentialPanel, "ai-usage-credentials");
        connection.Children.Add(credentialPanel);
        var credentialNote = Copy("");
        var credential = new PasswordBox { Header = "凭证（空白保留已保存凭证）", PasswordRevealMode = PasswordRevealMode.Hidden };
        AutomationProperties.SetAutomationId(credential, "ai-usage-credential");
        var project = new TextBox { Header = "Google Cloud 项目 ID", Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(project, "ai-usage-project");
        credentialPanel.Children.Add(credentialNote);
        credentialPanel.Children.Add(credential);
        credentialPanel.Children.Add(project);
        var quotas = Section("剩余额度", "显示服务提供的额度或余额；如有重置时间，按本地时区显示。");
        var quotasSurface = (Border)_page.Children.Last();
        AutomationProperties.SetAutomationId(quotasSurface, "ai-usage-quota-section");
        quotasSurface.Visibility = Visibility.Collapsed;
        var quotaRows = new StackPanel { Spacing = 12 };
        AutomationProperties.SetAutomationId(quotaRows, "ai-usage-quotas");
        quotas.Children.Add(quotaRows);
        var activity = Section("活动 · 最近 12 周", "每列一周，周一至周日从上到下。空心格为未返回记录，浅格为未来日期；有记录的零值与未知分开显示。悬停或键盘聚焦查看日期与数量。");
        var activitySurface = (Border)_page.Children.Last();
        AutomationProperties.SetAutomationId(activitySurface, "ai-usage-activity");
        activitySurface.Visibility = Visibility.Collapsed;
        var dateBasis = Copy("");
        AutomationProperties.SetAutomationId(dateBasis, "ai-usage-date-basis");
        activity.Children.Add(dateBasis);
        var totals = Copy("Token 汇总尚未返回。");
        activity.Children.Add(totals);
        var heatmap = new Grid { ColumnSpacing = 3, RowSpacing = 3, HorizontalAlignment = HorizontalAlignment.Stretch, MaxWidth = 440 };
        AutomationProperties.SetAutomationId(heatmap, "ai-usage-heatmap");
        for (var column = 0; column < 12; column++) heatmap.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var row = 0; row < 7; row++) heatmap.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void SquareCells()
        {
            var size = Math.Max(1, (heatmap.ActualWidth - 33) / 12);
            foreach (var cell in heatmap.Children.OfType<Button>()) cell.Height = size;
        }
        heatmap.SizeChanged += (_, _) => SquareCells();
        activity.Children.Add(heatmap);
        var legend = Copy("■ 有记录（颜色越深活动越多）　□ 无记录　· 未来日期");
        activity.Children.Add(legend);
        var historyNote = Copy("");
        activity.Children.Add(historyNote);
        var manual = Section("手动记录", "这是你自行录入的记录，不是自动读取的官方用量。保存会替换该服务的手动日期记录；订阅剩余百分比留空表示不记录该值，会清除原手动额度。");
        var manualSurface = (Border)_page.Children.Last();
        manualSurface.Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(manualSurface, "ai-usage-manual");
        var manualDays = new TextBox { Header = "每日次数（每行 YYYY-MM-DD,数量）", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 100 };
        AutomationProperties.SetAutomationId(manualDays, "ai-usage-manual-days");
        var manualRemaining = new TextBox { Header = "剩余百分比（可选，0–100）" };
        AutomationProperties.SetAutomationId(manualRemaining, "ai-usage-manual-remaining");
        manual.Children.Add(manualDays); manual.Children.Add(manualRemaining);
        var busy = false;
        var updating = false;
        var anyServiceEnabled = false;
        var enabledServices = serviceDefinitions.ToDictionary(service => service.Id, _ => false);
        var mutation = false;
        var manualLoaded = false;
        var connectionLoaded = false;
        var bindingForms = false;
        var projectEdited = false;
        var manualEdited = false;
        // TextChanged is deferred and can arrive after a cached IPC reply.
        // TextChanging runs synchronously, so the same-turn reply cannot replace
        // a new draft, and programmatic bindings remain inside their guard.
        project.TextChanging += (_, _) => { if (!bindingForms) projectEdited = true; };
        manualDays.TextChanging += (_, _) => { if (!bindingForms) manualEdited = true; };
        manualRemaining.TextChanging += (_, _) => { if (!bindingForms) manualEdited = true; };
        var generation = 0;
        string? selected = null;
        var catalog = new List<(string Id, string Name, string Mode, string ServiceId)>();
        CancellationTokenSource? readCancellation = null;
        string? pendingServiceAccount = null;
        var pickingCredential = false;
        var serviceAccountChosen = Copy("");
        credentialPanel.Children.Add(serviceAccountChosen);
        string? renderedData = null;
        Button? refresh = null;
        bool Current() => !token.IsCancellationRequested && !_closed && _currentPage == "aiUsage";
        async Task<JsonObject> Send(string action, JsonObject payload, CancellationToken requestToken)
        {
            if (ProbeRequestHandler is not null) return await ProbeRequestHandler(action, payload).WaitAsync(requestToken);
            var response = await _connection.RequestAsync(action, payload, requestToken);
            return JsonNode.Parse(response.GetRawText()) as JsonObject ?? new();
        }
        void ClearService()
        {
            generation++;
            readCancellation?.Cancel();
            renderedData = null;
            quotaRows.Children.Clear(); heatmap.Children.Clear();
            totals.Text = "正在读取…"; historyNote.Text = ""; sourceNote.Text = ""; dateBasis.Text = "";
            credential.Password = ""; pendingServiceAccount = null; serviceAccountChosen.Text = "";
            credentialPanel.Visibility = manualSurface.Visibility = official.Visibility = Visibility.Collapsed;
            bindingForms = true;
            try { project.Text = ""; manualDays.Text = ""; manualRemaining.Text = ""; }
            finally { bindingForms = false; }
            manualLoaded = false;
            connectionLoaded = false;
            projectEdited = manualEdited = false;
            status.Text = "正在切换服务…";
        }
        async Task<bool> Mutate(string action, JsonObject payload)
        {
            if (!Current() || mutation) return false;
            mutation = true; providers.IsEnabled = showApi.IsEnabled = false;
            foreach (var toggle in serviceToggles.Values) toggle.IsEnabled = false;
            if (action == "configureAiUsage") status.Text = payload["clearCredential"]?.GetValue<bool>() == true
                ? "正在清除凭证…" : "正在验证连接…凭证仅在验证成功后保存。";
            foreach (var control in credentialPanel.Children.Concat(manual.Children).OfType<Control>()) control.IsEnabled = false;
            try
            {
                var result = await Send(action, payload, token);
                if (result["success"] is JsonValue success && success.TryGetValue<bool>(out var succeeded) && !succeeded)
                { status.Text = "保存未完成，请检查输入后重试。"; return false; }
                if (Current())
                {
                    while (busy && Current()) await Task.Delay(10, token);
                    renderedData = null; await Read(false, afterMutation: true, supplied: result);
                }
                return Current();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
            catch (Exception error) when (error is not OutOfMemoryException) { if (Current()) status.Text = "操作未完成，请检查输入与连接后重试。"; return false; }
            finally
            {
                mutation = false;
                if (Current())
                {
                    providers.IsEnabled = showApi.IsEnabled = true;
                    foreach (var toggle in serviceToggles.Values) toggle.IsEnabled = true;
                    foreach (var control in credentialPanel.Children.Concat(manual.Children).OfType<Control>()) control.IsEnabled = true;
                }
            }
        }
        var pick = ActionButton("选择服务账号 JSON 文件", async () => {
            if (!Current() || selected != "gemini-api" || pickingCredential) return;
            var version = generation;
            pickingCredential = true;
            try
            {
                // The desktop picker supports elevated settings windows.
                var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id);
                picker.FileTypeFilter.Add(".json");
                var file = await picker.PickSingleFileAsync();
                if (file is null || !Current() || version != generation) return;
                var content = await ReadAiCredentialFileAsync(file.Path, token);
                if (!Current() || version != generation) return;
                pendingServiceAccount = content; serviceAccountChosen.Text = "已选择服务账号文件，点击保存并验证连接后生效。";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (Current() && version == generation) status.Text = "无法读取服务账号文件。请选择不超过 128 KB 的有效 UTF-8 JSON 文件后重试。";
            }
            finally { pickingCredential = false; }
        });
        AutomationProperties.SetAutomationId(pick, "ai-usage-pick-json");
        credentialPanel.Children.Add(pick);
        foreach (var (serviceId, toggle) in serviceToggles)
            toggle.Toggled += async (_, _) => {
                if (updating || mutation) return;
                ClearService(); selected = null;
                await Mutate("setAiUsagePreferences", new JsonObject { ["enabledServices"] = new JsonObject { [serviceId] = toggle.IsOn } });
            };
        providers.SelectionChanged += async (_, _) => {
            if (updating || mutation || providers.SelectedItem is not ComboBoxItem item || item.Tag is not string id || id == selected) return;
            ClearService(); selected = id;
            await Mutate("setAiUsagePreferences", new JsonObject { ["providerId"] = id });
        };
        showApi.Toggled += async (_, _) => {
            if (updating || mutation) return;
            var enabled = showApi.IsOn;
            var current = catalog.FirstOrDefault(item => item.Id == selected);
            ClearService();
            if (!enabled && current.Mode == "api") selected = catalog.FirstOrDefault(item => item.ServiceId == current.ServiceId && item.Mode == "subscription").Id;
            await Mutate("setAiUsagePreferences", new JsonObject { ["showApi"] = enabled, ["providerId"] = selected });
        };
        var saveCredential = ActionButton("保存并验证连接", async () => {
            if (selected is null) return;
            var payload = new JsonObject { ["providerId"] = selected };
            var value = selected == "gemini-api" ? pendingServiceAccount : credential.Password;
            if (!string.IsNullOrWhiteSpace(value)) payload["credential"] = value;
            if (selected == "gemini-api") payload["projectId"] = project.Text.Trim();
            if (await Mutate("configureAiUsage", payload))
            { credential.Password = ""; pendingServiceAccount = null; serviceAccountChosen.Text = ""; }
        });
        AutomationProperties.SetAutomationId(saveCredential, "ai-usage-save-credential");
        credentialPanel.Children.Add(saveCredential);
        var clearCredential = ActionButton("清除已保存凭证", async () => {
            if (selected is null) return;
            if (await Mutate("configureAiUsage", new JsonObject { ["providerId"] = selected, ["clearCredential"] = true }))
            { credential.Password = ""; pendingServiceAccount = null; serviceAccountChosen.Text = ""; }
        });
        AutomationProperties.SetAutomationId(clearCredential, "ai-usage-clear-credential");
        credentialPanel.Children.Add(clearCredential);
        var saveManual = ActionButton("保存手动记录", async () => {
            if (selected is null) return;
            var days = new JsonArray(); var seen = new HashSet<DateOnly>();
            // WinUI TextBox normalizes multiline input to CR; pasted CSV may
            // also contain LF or CRLF. Accept all three without merging rows.
            foreach (var line in manualDays.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(',');
                if (parts.Length != 2 || !DateOnly.TryParseExact(parts[0].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                    date > DateOnly.FromDateTime(DateTime.Now) || !long.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 0 || !seen.Add(date))
                { status.Text = "记录格式应为 YYYY-MM-DD,非负整数；日期不能重复或晚于今天。"; return; }
                days.Add(new JsonObject { ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["tokens"] = count });
            }
            var payload = new JsonObject { ["providerId"] = selected, ["days"] = days };
            if (selected != "deepseek-api" && !string.IsNullOrWhiteSpace(manualRemaining.Text))
            {
                if (!double.TryParse(manualRemaining.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var remaining) || !double.IsFinite(remaining) || remaining < 0 || remaining > 100)
                { status.Text = "剩余百分比应在 0 到 100 之间。"; return; }
                payload["remainingPercent"] = remaining;
            }
            await Mutate("saveAiUsageManual", payload);
        });
        AutomationProperties.SetAutomationId(saveManual, "ai-usage-save-manual");
        manual.Children.Add(saveManual);
        async Task Read(bool force, bool afterMutation = false, JsonObject? supplied = null)
        {
            if (busy || !Current() || (mutation && !afterMutation)) return;
            busy = true;
            var version = generation;
            readCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var requestCancellation = readCancellation;
            if (refresh is not null) refresh.IsEnabled = false;
            try
            {
                var snapshot = supplied ?? await Send("getAiUsage", new JsonObject { ["providerId"] = selected, ["force"] = force }, requestCancellation.Token);
                if (!Current() || version != generation || (mutation && !afterMutation)) return;
                // The provider may publish identical cached snapshots during polling.
                // Keep realized controls (and their keyboard focus) until data changes.
                var activityUtc = snapshot["activityUtc"]?.GetValue<bool>() == true;
                var today = DateOnly.FromDateTime(activityUtc ? DateTime.UtcNow : DateTime.Now);
                var data = snapshot.ToJsonString() + today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + CardHighContrast;
                if (data == renderedData) return;
                renderedData = data;
                dateBasis.Text = $"日期按{(activityUtc ? " UTC" : "本地时间")} · 今日 {today:yyyy-MM-dd}";
                var id = Text(snapshot["providerId"]);
                if (selected is not null && id != selected) return;
                selected = id;
                updating = true;
                try
                {
                    foreach (var service in serviceDefinitions)
                    {
                        var enabled = snapshot["enabledServices"]?[service.Id]?.GetValue<bool>() == true;
                        enabledServices[service.Id] = enabled; serviceToggles[service.Id].IsOn = enabled;
                    }
                    anyServiceEnabled = enabledServices.Values.Any(enabled => enabled);
                    emptyHint.Visibility = anyServiceEnabled ? Visibility.Collapsed : Visibility.Visible;
                    connectionSurface.Visibility = quotasSurface.Visibility = activitySurface.Visibility = anyServiceEnabled ? Visibility.Visible : Visibility.Collapsed;
                    showApi.IsOn = snapshot["showApi"]?.GetValue<bool>() == true;
                    var nextCatalog = (snapshot["providers"] as JsonArray)?.OfType<JsonObject>().Select(item =>
                        (Id: Text(item["id"]), Name: Text(item["name"]), Mode: Text(item["mode"]), ServiceId: Text(item["serviceId"]))).ToList() ?? [];
                    if (!catalog.SequenceEqual(nextCatalog) || !providers.Items.OfType<ComboBoxItem>().Select(item => item.Tag as string)
                        .SequenceEqual(nextCatalog.Where(item => enabledServices.GetValueOrDefault(item.ServiceId) && (showApi.IsOn || item.Mode != "api")).Select(item => item.Id)))
                    {
                        catalog = nextCatalog; providers.Items.Clear();
                        foreach (var item in catalog.Where(item => enabledServices.GetValueOrDefault(item.ServiceId) && (showApi.IsOn || item.Mode != "api")))
                            providers.Items.Add(new ComboBoxItem { Content = item.Name, Tag = item.Id });
                    }
                    providers.SelectedItem = providers.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, selected));
                }
                finally { updating = false; }
                if (!anyServiceEnabled)
                {
                    quotaRows.Children.Clear(); heatmap.Children.Clear();
                    credentialPanel.Visibility = manualSurface.Visibility = Visibility.Collapsed;
                    credential.Password = ""; pendingServiceAccount = null;
                    status.Text = "打开需要查看的 AI 服务。";
                    return;
                }
                var provider = catalog.FirstOrDefault(item => item.Id == selected);
                var state = Text(snapshot["status"]);
                var api = provider.Mode == "api" && showApi.IsOn;
                var unit = Text(snapshot["activityUnit"]) == "requests" ? "次" : "Token";
                sourceNote.Text = selected == "codex"
                    ? "这里显示 ChatGPT 账户下的 Codex 额度，不代表全部 ChatGPT 对话限制。使用本机已登录的官方 Codex，无需 API Key。"
                    : provider.Mode == "subscription" ? "此订阅服务暂不支持自动读取官方用量。可查看官方页面，或明确录入手动记录。" : "API 用量独立于网页订阅；金额、请求和 Token 不能相互换算。";
                sourceNote.Text += "\n" + Text(snapshot["sourceLabel"]);
                official.NavigateUri = selected switch {
                    "deepseek-web" => new Uri("https://chat.deepseek.com/"), "deepseek-api" => new Uri("https://platform.deepseek.com/usage"),
                    "gemini-web" => new Uri("https://gemini.google.com/"), "gemini-api" => new Uri("https://aistudio.google.com/usage"),
                    "claude-web" => new Uri("https://claude.ai/settings/usage"), "claude-api" => new Uri("https://console.anthropic.com/settings/usage"),
                    "openai-api" => new Uri("https://platform.openai.com/usage"), _ => null };
                official.Visibility = official.NavigateUri is null ? Visibility.Collapsed : Visibility.Visible;
                credentialPanel.Visibility = api ? Visibility.Visible : Visibility.Collapsed;
                credential.Visibility = api && selected != "gemini-api" ? Visibility.Visible : Visibility.Collapsed;
                project.Visibility = pick.Visibility = api && selected == "gemini-api" ? Visibility.Visible : Visibility.Collapsed;
                var hasCredential = (snapshot["hasCredential"] ?? snapshot["connection"]?["hasCredential"])?.GetValue<bool>() == true;
                credentialNote.Text = (hasCredential ? "已保存凭证。" : "尚未配置凭证。") + (selected switch {
                    "gemini-api" => "选择 Google 服务账号 JSON，并填写项目 ID；账号需拥有项目 Monitoring Viewer 权限。普通 Gemini API Key 无法读取项目监控。私钥只写入安全存储，不在界面回显。",
                    "openai-api" => "需要 OpenAI Admin API Key 读取组织用量。",
                    "claude-api" => "需要 Anthropic Admin API Key 读取组织用量。", _ => "使用 DeepSeek API Key 读取官方余额。" });
                if (!connectionLoaded && !projectEdited && state is not ("loading" or "idle"))
                {
                    bindingForms = true;
                    try { project.Text = Text(snapshot["projectId"] ?? snapshot["connection"]?["projectId"]); connectionLoaded = true; }
                    finally { bindingForms = false; }
                }
                manualSurface.Visibility = (selected is "deepseek-web" or "gemini-web" or "claude-web" or "deepseek-api") && Text(snapshot["status"]) != "loading" ? Visibility.Visible : Visibility.Collapsed;
                manualRemaining.Visibility = provider.Mode == "subscription" ? Visibility.Visible : Visibility.Collapsed;
                manualDays.Header = $"每日{(unit == "Token" ? "Token" : "次数")}（每行 YYYY-MM-DD,数量）";
                historyNote.Text = Text(snapshot["historyNote"]);
                var updated = DateTimeOffset.TryParse(Text(snapshot["updatedAt"]), out var time) ? $" · 更新于 {time.ToLocalTime():g}" : "";
                status.Text = (state switch { "ready" => "数据已读取", "partial" => "部分数据可用", "loading" => "正在读取用量…", "error" => "读取失败", "unsupported" => "暂不支持自动读取", "unconfigured" => "尚未配置连接", _ => "等待用量数据" }) + updated;
                if (!string.IsNullOrWhiteSpace(Text(snapshot["error"]))) status.Text += "\n" + Text(snapshot["error"]);
                quotaRows.Children.Clear();
                foreach (var balance in (snapshot["balances"] as JsonArray)?.OfType<JsonObject>() ?? [])
                    quotaRows.Children.Add(Copy($"{Text(balance["label"])} · {Text(balance["currency"])} {Text(balance["available"])}"));
                foreach (var quota in (snapshot["quotas"] as JsonArray)?.OfType<JsonObject>() ?? [])
                {
                    var label = Text(quota["label"]);
                    if (quota["windowMinutes"] is JsonValue windowValue && windowValue.TryGetValue<int>(out var minutes) && minutes > 0)
                        label += minutes % 1440 == 0 ? $"（{minutes / 1440} 天窗口）" : minutes % 60 == 0 ? $"（{minutes / 60} 小时窗口）" : $"（{minutes} 分钟窗口）";
                    var used = quota["usedPercent"]?.GetValue<double>();
                    var remaining = used.HasValue && double.IsFinite(used.Value) ? $"剩余 {Math.Clamp(100 - used.Value, 0, 100):0.#}%" : "剩余额度未知";
                    var reset = "重置时间未知";
                    if (quota["resetsAt"] is JsonValue resetValue && resetValue.TryGetValue<long>(out var seconds))
                    {
                        try { reset = $"重置于 {DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime():g}"; }
                        catch (ArgumentOutOfRangeException) { }
                    }
                    quotaRows.Children.Add(Copy($"{label} · {remaining}\n{reset}"));
                    if (used.HasValue && double.IsFinite(used.Value)) quotaRows.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = Math.Clamp(100 - used.Value, 0, 100) });
                }
                if (quotaRows.Children.Count == 0) quotaRows.Children.Add(Copy("尚无可读取的剩余额度或余额。未知不代表为零。"));
                if (!string.IsNullOrWhiteSpace(Text(snapshot["quotaNote"]))) quotaRows.Children.Add(Copy(Text(snapshot["quotaNote"])));
                var days = new Dictionary<DateOnly, long>();
                foreach (var day in (snapshot["days"] as JsonArray)?.OfType<JsonObject>() ?? [])
                    if (DateOnly.TryParseExact(Text(day["date"]), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && day["tokens"] is JsonValue value && value.TryGetValue<long>(out var count) && count >= 0) days[date] = count;
                if (!manualLoaded && !manualEdited && manualSurface.Visibility == Visibility.Visible && state is not ("loading" or "idle"))
                {
                    bindingForms = true;
                    try
                    {
                        manualDays.Text = string.Join("\n", days.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key:yyyy-MM-dd},{pair.Value.ToString(CultureInfo.InvariantCulture)}"));
                        if (provider.Mode == "subscription")
                        {
                            var manualQuota = (snapshot["quotas"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(quota => Text(quota["id"]) == "manual");
                            if (manualQuota?["usedPercent"] is JsonValue usedValue && usedValue.TryGetValue<double>(out var usedPercent) && double.IsFinite(usedPercent))
                                manualRemaining.Text = Math.Clamp(100 - usedPercent, 0, 100).ToString("0.################", CultureInfo.CurrentCulture);
                        }
                        manualLoaded = true;
                    }
                    finally { bindingForms = false; }
                }
                totals.Text = snapshot["lifetimeTokens"] is JsonValue total && total.TryGetValue<long>(out var lifetime)
                    ? $"累计 {lifetime:N0} {unit}" : $"累计{unit}尚未返回。";
                var start = today.AddDays(-((int)today.DayOfWeek + 6) % 7 - 77);
                var peak = days.Where(pair => pair.Key >= start && pair.Key <= today).Select(pair => pair.Value).DefaultIfEmpty().Max();
                heatmap.Children.Clear();
                for (var index = 0; index < 84; index++)
                {
                    var date = start.AddDays(index);
                    var known = days.TryGetValue(date, out var count);
                    var future = date > today;
                    var detail = $"{date:yyyy-MM-dd}{(activityUtc ? "（UTC）" : "")} · " + (future ? "未来日期" : known ? $"{count:N0} {unit}" : "未返回记录");
                    var cell = new Button { Content = future ? "·" : known ? "■" : "□", Padding = new Thickness(0), MinWidth = 0, MinHeight = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
                    AutomationProperties.SetName(cell, detail);
                    ToolTipService.SetToolTip(cell, detail);
                    if (!CardHighContrast && known && !future)
                    {
                        cell.Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
                        cell.Opacity = count == 0 ? 0.2 : 0.35 + 0.65 * Math.Sqrt((double)count / Math.Max(1, peak));
                    }
                    Grid.SetColumn(cell, index / 7); Grid.SetRow(cell, index % 7); heatmap.Children.Add(cell);
                }
                SquareCells();
            }
            catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (Current() && version == generation) status.Text = "暂时无法读取用量。请检查当前服务的连接与配置，然后重试。";
            }
            finally { requestCancellation.Dispose(); if (ReferenceEquals(readCancellation, requestCancellation)) readCancellation = null; busy = false; if (Current() && refresh is not null) refresh.IsEnabled = true; }
        }
        refresh = ActionButton("刷新用量", () => Read(true));
        AutomationProperties.SetAutomationId(refresh, "ai-usage-refresh");
        connection.Children.Add(refresh);
        async Task Poll()
        {
            await Read(false);
            try
            {
                while (Current()) { await Task.Delay(TimeSpan.FromSeconds(2), token); if (anyServiceEnabled) await Read(false); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
        _ = Poll();
    }
}
