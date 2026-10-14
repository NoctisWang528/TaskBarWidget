using System.Text.Json.Nodes;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TaskBarWidgets.UI.Settings;
using Windows.System;

namespace TaskBarWidgets.UI;

public sealed partial class SettingsWindow
{
    private readonly Dictionary<string, JsonNode?> _serviceEvents = new(StringComparer.Ordinal);
    private bool _queryingOffsets;
    private string _offsetSearch = "";
    private int _offsetPage = 1;
    private static readonly (string Id, string Label, string Key)[] Players = [
        ("QQMusic", "QQ 音乐", "enableQQMusic"), ("Netease", "网易云音乐", "enableNetease"),
        ("Kugou", "酷狗音乐", "enableKugou"), ("Spotify", "Spotify", "enableSpotify"), ("Browser", "浏览器", "enableBrowser") ];

    private static JsonObject MediaValues(JsonObject values)
    {
        var result = (JsonObject)values.DeepClone();
        foreach (var (source, alias) in new[] { ("playerLyricOffsets", "playerLyricOffset:"), ("playerLyricProviders", "playerLyricProviders:") })
            if (values[source] is JsonObject items) foreach (var item in items) result[alias + item.Key] = item.Value?.DeepClone();
        if (values["customPlayerSources"] is JsonArray custom) foreach (var item in custom.OfType<JsonObject>()) result["customPlayerEnabled:" + Text(item["sourceAppUserModelId"])] = item["enabled"]?.DeepClone();
        return result;
    }
    private FrameworkElement MediaEditor(string key, string label, string type = "boolean", double min = 0, double max = 100, string? description = null, string[]? choices = null, string[]? labels = null)
    {
        var field = new JsonObject { ["key"] = key, ["label"] = label, ["type"] = type, ["min"] = min, ["max"] = max, ["description"] = description };
        if (choices is not null) {
            var options = new JsonArray(); var numeric = _media.Get(key) is JsonValue value && value.TryGetValue<int>(out _);
            for (var index = 0; index < choices.Length; index++) options.Add(new JsonObject { ["value"] = numeric ? JsonValue.Create(index) : JsonValue.Create(choices[index]), ["label"] = labels?[index] ?? choices[index] });
            field["options"] = options;
        }
        return FieldEditor(field, _media);
    }
    private void RenderMediaPage(string page)
    {
        switch (page) {
            case "sources": RenderSources(); break;
            case "lyrics": RenderLyrics(); break;
            case "displayArea": RenderLyricsAppearance(); break;
            case "audio": RenderAudio(); break;
            case "shortcuts": RenderHotkeys(); break;
            case "trackOffsets": RenderOffsets(); break;
            case "lyricDiagnostics": RenderDiagnostics(); break;
            case "advanced": RenderMaintenance(); break;
            case "about": RenderAbout(); break;
        }
    }
    private void RenderGeneral()
    {
        var section = Section("应用界面");
        section.Children.Add(MediaEditor("toolWindowTheme", "界面主题", "select", choices: ["System", "Light", "Dark"], labels: ["跟随系统", "浅色", "深色"]));
        section.Children.Add(Copy("主题在应用后同时用于设置窗口与快捷面板。开机启动由上方主程序设置管理。"));
    }
    private void RenderLyrics()
    {
        var behavior = Section("歌词显示");
        behavior.Children.Add(MediaEditor("showLyricTranslation", "显示歌词翻译"));
        behavior.Children.Add(MediaEditor("enableWordScanning", "逐词扫描"));
        var spectrum = Section("音频频谱", "启用后会读取系统正在播放的声音，仅用于实时频谱。关闭或撤销授权后停止读取。");
        var modes = new[] { "Disabled", "PureMusicOnly", "PureMusicOrNoLyrics", "Always" };
        var choice = new ComboBox { Header = "显示策略", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var label in new[] { "关闭", "仅纯音乐时", "纯音乐或无歌词时", "始终显示" }) choice.Items.Add(label);
        choice.SelectedIndex = Math.Max(0, Array.IndexOf(modes, _media.Text("spectrumDisplayMode", "Disabled")));
        choice.SelectionChanged += async (_, _) => await GuardAsync(async () => {
            if (choice.SelectedIndex < 0) return;
            var mode = modes[choice.SelectedIndex];
            if (mode != "Disabled" && !_media.Bool("spectrumAudioAccessGranted")) {
                var dialog = new ContentDialog { XamlRoot = _navigation.XamlRoot, Title = "允许读取系统播放声音？", Content = "频谱只在需要显示时读取扬声器或耳机当前播放的声音。可随时在这里撤销。", PrimaryButtonText = "允许并启用", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ServiceCommandAsync("confirmSpectrumAudioAccess", JsonValue.Create(mode));
                else RenderPage();
            }
            else { _media.Set("spectrumDisplayMode", mode); Changed(); }
        });
        spectrum.Children.Add(choice);
        spectrum.Children.Add(Copy(Text((_serviceEvents.GetValueOrDefault("spectrumCaptureState") as JsonObject)?["message"], _media.Bool("spectrumAudioAccessGranted") ? "已授权系统音频访问。" : "尚未授权系统音频访问。")));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(ActionButton("重试采集", () => ServiceCommandAsync("retrySpectrumCapture")));
        actions.Children.Add(ActionButton("停止并撤销授权", () => ServiceCommandAsync("revokeSpectrumAudioAccess"))); spectrum.Children.Add(actions);
        if (_media.Get("spectrumTuning") is JsonObject tuning)
        {
            var tuningDraft = new SettingsDraft(); tuningDraft.Readback(tuning);
            var fields = new StackPanel { Spacing = 4 };
            var definitions = new (string Key, string Label, double Min, double Max, double Step)[] {
                ("sampleWindow", "采样窗口", 256, 8192, 256), ("updateIntervalMs", "更新间隔（毫秒）", 16, 100, 1),
                ("barCount", "频谱柱数量", 8, 32, 1), ("minFrequency", "最低频率（Hz）", 20, 19999, 1),
                ("maxFrequency", "最高频率（Hz）", 21, 20000, 1), ("frequencyDistributionBias", "频率分布偏移", -1, 1, .01),
                ("peakInitial", "初始峰值", .004, .2, .001), ("peakDecay", "峰值衰减", .85, .995, .001),
                ("peakFloor", "峰值下限", .003, .08, .001), ("peakCeiling", "峰值上限", .003, 1, .01),
                ("noiseFloor", "噪声底限", 0, .25, .001), ("outputCurve", "输出曲线", .25, 1.5, .01),
                ("lowBandGain", "低频增益", .2, 4, .01), ("bandGainStep", "频段增益步长", -.05, .12, .001),
                ("frequencyWeightBase", "频率权重基值", .2, 2.5, .01), ("frequencyWeightSlope", "频率权重斜率", -.1, .2, .001),
                ("backendAttack", "采集上升速度", .05, 1, .01), ("backendRelease", "采集下降速度", .02, 1, .01),
                ("frontendRise", "显示上升速度", .01, 1, .01), ("frontendFall", "显示下降速度", .01, 1, .01),
                ("minBarHeight", "频谱柱最小高度", 0, 48, 1), ("barHeightRange", "频谱柱高度范围", 0, 96, 1),
                ("barOpacity", "频谱柱不透明度", 0, 1, .01) };
            foreach (var definition in definitions)
            {
                fields.Children.Add(FieldEditor(new JsonObject { ["key"] = definition.Key, ["label"] = definition.Label, ["type"] = "number", ["min"] = definition.Min, ["max"] = definition.Max, ["step"] = definition.Step }, tuningDraft,
                    () => { var updated = new JsonObject(); foreach (var key in tuningDraft.Keys) updated[key] = tuningDraft.Get(key); _media.SetNode("spectrumTuning", updated); Changed(); }));
            }
            spectrum.Children.Add(new Expander { Header = "频谱高级调节", Content = fields, HorizontalAlignment = HorizontalAlignment.Stretch });
        }
    }
    private void RenderLyricsAppearance()
    {
        var style = Section("歌词样式", "共用字体、字号、背景色与透明度在任务栏与布局中设置。");
        style.Children.Add(MediaEditor("foregroundColorMode", "歌词文字颜色", "select", choices: ["Dark", "Light", "Custom", "System"], labels: ["深色", "浅色", "自定义", "跟随系统"]));
        style.Children.Add(MediaEditor("foregroundColor", "自定义文字颜色", "color"));
        style.Children.Add(MediaEditor("showTextShadow", "文字阴影"));
        style.Children.Add(MediaEditor("showBorder", "显示歌词边框"));
        var cover = Section("封面与布局");
        cover.Children.Add(MediaEditor("showCover", "显示封面"));
        cover.Children.Add(MediaEditor("coverPosition", "封面位置", "select", choices: ["Left", "Right"], labels: ["歌词左侧", "歌词右侧"]));
        cover.Children.Add(MediaEditor("lyricsTextAlignment", "歌词对齐", "select", choices: ["Left", "Center", "Right"], labels: ["左侧", "居中", "右侧"]));
        cover.Children.Add(MediaEditor("lyricsLayoutScalePercent", "封面与间距缩放（%）", "number", 25, 300));
        cover.Children.Add(MediaEditor("coverSize", "基础封面尺寸", "number", 12, 200));
        cover.Children.Add(MediaEditor("coverGap", "封面间距", "number", 0, 240));
        cover.Children.Add(MediaEditor("coverCornerRadius", "封面圆角", "number", 0, 100));
    }
    private void RenderAudio()
    {
        var section = Section("输出与输入", "在快捷面板内选择扬声器、耳机或麦克风，独立调整音量与静音。");
        section.Children.Add(MediaEditor("enableControlPanel", "启用媒体与音频快捷面板"));
        section.Children.Add(MediaEditor("syncDefaultCommunicationsDevice", "切换设备时同时设为默认通信设备", description: "只影响下一次主动切换；更改这个偏好不会立即切换设备。"));
        section.Children.Add(ActionButton("打开音频面板", async () => { await _connection.RequestAsync("openPanel", null, _lifetime.Token); }));
    }
    private IEnumerable<(string Id, string Label, string Key, bool Custom)> SourceList()
    {
        var order = (_media.Get("sourceRecognitionOrder") as JsonArray)?.Select(node => Text(node)).ToList() ?? Players.Select(player => player.Id).ToList();
        var custom = (_media.Get("customPlayerSources") as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        foreach (var id in order) {
            var known = Players.FirstOrDefault(player => player.Id == id);
            if (known.Id is not null) yield return (id, known.Label, known.Key, false);
            else if (custom.FirstOrDefault(item => Text(item["sourceAppUserModelId"]) == id) is { } item) yield return (id, Text(item["displayName"], id), "customPlayerEnabled:" + id, true);
        }
    }
    private void RenderSources()
    {
        var sources = Section("播放器", "按顺序选择同时播放时的媒体源。点击设置可以调整各播放器的歌词源、同步偏移和优先顺序。");
        foreach (var source in SourceList()) {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(MediaEditor(source.Key, source.Label));
            var edit = new Button { Content = "设置", MinHeight = 30 }; edit.Click += (_, _) => OpenSource(source.Id); Grid.SetColumn(edit, 1); row.Children.Add(edit); sources.Children.Add(row);
        }
        sources.Children.Add(ActionButton("发现正在运行的播放器", () => ServiceCommandAsync("discoverPlayerSessions")));
        if (_serviceEvents.GetValueOrDefault("playerSessions") is JsonObject discovered && discovered["sessions"] is JsonArray sessions) {
            if (sessions.Count == 0) sources.Children.Add(Copy("没有发现可添加的媒体会话，请先在播放器内开始播放。"));
            foreach (var session in sessions.OfType<JsonObject>().Where(item => item["isBuiltIn"]?.ToString() != "true")) {
                var add = ActionButton("添加 " + Text(session["displayName"]), () => ServiceCommandAsync("addPlayerSource", new JsonObject { ["sourceAppUserModelId"] = Text(session["sourceAppUserModelId"]), ["displayName"] = Text(session["displayName"]), ["iconDataUrl"] = Text(session["iconDataUrl"]), ["enabled"] = true }));
                sources.Children.Add(add);
            }
        }
        var local = Section("本地歌词", "优先匹配音乐目录内的同名 .lrc、.qrc 或 .krc 文件。");
        local.Children.Add(MediaEditor("enableLocalLyrics", "优先使用本地歌词"));
        var folders = new TextBox { Header = "本地音乐目录（每行一个）", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, Text = string.Join(Environment.NewLine, (_media.Get("localMusicFolders") as JsonArray)?.Select(item => Text(item)) ?? []) };
        folders.TextChanged += (_, _) => { _media.Set("localMusicFolders", folders.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); Changed(); }; local.Children.Add(folders);
    }
    private void OpenSource(string id)
    {
        var source = SourceList().First(item => item.Id == id);
        _properties.Children.Clear();
        var close = new Button { Content = "关闭属性", HorizontalAlignment = HorizontalAlignment.Right }; close.Click += (_, _) => _split.IsPaneOpen = false; _properties.Children.Add(close);
        _properties.Children.Add(new TextBlock { Text = source.Label, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var ordering = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (label, delta) in new[] { ("优先", -1), ("靠后", 1) }) ordering.Children.Add(ActionButton(label, async () => {
            var order = (_media.Get("sourceRecognitionOrder") as JsonArray)?.Select(node => Text(node)).ToList() ?? [];
            var index = order.IndexOf(id); var target = Math.Clamp(index + delta, 0, order.Count - 1);
            if (index < 0 || target == index) return;
            (order[index], order[target]) = (order[target], order[index]); await ServiceCommandAsync("reorderSources", JsonSerializerNode(order)); OpenSource(id);
        }));
        _properties.Children.Add(ordering);
        _properties.Children.Add(MediaEditor("playerLyricOffset:" + id, "歌词同步偏移（毫秒）", "number", -5000, 5000));
        _properties.Children.Add(Copy("歌词源按优先顺序尝试，关闭某个歌词源会保留其位置。浏览器媒体仅显示元数据，不查找歌词。"));
        if (_media.Get("playerLyricProviders:" + id) is JsonArray providers) {
            for (var index = 0; index < providers.Count; index++) {
                if (providers[index] is not JsonObject provider) continue;
                var position = index;
                var line = new Grid { ColumnSpacing = 4 }; line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var check = new CheckBox { Content = ProviderLabel(Text(provider["providerId"])), IsChecked = provider["enabled"]?.ToString() == "true", MinHeight = 30 };
                void Toggle(bool enabled) { var updated = (JsonArray)_media.Get("playerLyricProviders:" + id)!; updated[position]!["enabled"] = enabled; _media.SetNode("playerLyricProviders:" + id, updated); Changed(); }
                check.Checked += (_, _) => Toggle(true); check.Unchecked += (_, _) => Toggle(false); line.Children.Add(check);
                foreach (var (delta, column, glyph) in new[] { (-1, 1, "↑"), (1, 2, "↓") }) {
                    var button = new Button { Content = glyph, MinHeight = 28, MinWidth = 28, Padding = new Thickness(4), IsEnabled = position + delta >= 0 && position + delta < providers.Count };
                    AutomationProperties.SetName(button, (delta < 0 ? "上移" : "下移") + ProviderLabel(Text(provider["providerId"])));
                    button.Click += (_, _) => { var updated = (JsonArray)_media.Get("playerLyricProviders:" + id)!; var moved = updated[position]!.DeepClone(); updated.RemoveAt(position); updated.Insert(position + delta, moved); _media.SetNode("playerLyricProviders:" + id, updated); Changed(); OpenSource(id); };
                    Grid.SetColumn(button, column); line.Children.Add(button);
                }
                _properties.Children.Add(line);
            }
        }
        if (source.Custom && (_media.Get("customPlayerSources") as JsonArray)?.OfType<JsonObject>().FirstOrDefault(item => Text(item["sourceAppUserModelId"]) == id) is { } custom) {
            var name = new TextBox { Header = "显示名称", Text = source.Label }; _properties.Children.Add(name);
            _properties.Children.Add(ActionButton("保存播放器名称", async () => { var value = (JsonObject)custom.DeepClone(); value["displayName"] = name.Text; await ServiceCommandAsync("updatePlayerSource", value); OpenSource(id); }));
            _properties.Children.Add(ActionButton("移除此播放器", () => ServiceCommandAsync("removePlayerSource", JsonValue.Create(id))));
        }
        _split.IsPaneOpen = true;
    }
    private static string ProviderLabel(string id) => id switch { "QQMusic" => "QQ 音乐", "Netease" => "网易云音乐", "Kugou" => "酷狗音乐", _ => id };
    private static JsonNode? JsonSerializerNode(object? value) => System.Text.Json.JsonSerializer.SerializeToNode(value);
    private void RenderHotkeys()
    {
        var section = Section("全局播放器快捷键", "聚焦输入框后直接按组合键，或输入 Ctrl+Alt+P 形式的快捷键。应用后注册到 Windows。");
        section.Children.Add(MediaEditor("enableGlobalMediaHotkeys", "启用全局快捷键"));
        if (_media.Get("mediaHotkeys") is JsonArray hotkeys) foreach (var definition in hotkeys.OfType<JsonObject>()) {
            var key = Text(definition["settingKey"]); var row = new StackPanel { Spacing = 4 };
            var input = new TextBox { Header = Text(definition["displayName"]), Text = _media.Text(key), MinHeight = 32 };
            input.KeyDown += (_, args) => {
                if (args.Key is VirtualKey.Tab or VirtualKey.Escape or VirtualKey.Control or VirtualKey.Menu or VirtualKey.Shift or VirtualKey.LeftWindows or VirtualKey.RightWindows) return;
                bool Down(VirtualKey value) => (InputKeyboardSource.GetKeyStateForCurrentThread(value) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
                var parts = new List<string>(); if (Down(VirtualKey.Control)) parts.Add("Ctrl"); if (Down(VirtualKey.Menu)) parts.Add("Alt"); if (Down(VirtualKey.Shift)) parts.Add("Shift"); if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) parts.Add("Win");
                var name = args.Key.ToString();
                if ((int)args.Key >= (int)VirtualKey.Number0 && (int)args.Key <= (int)VirtualKey.Number9) name = ((int)args.Key - (int)VirtualKey.Number0).ToString();
                else if ((int)args.Key >= (int)VirtualKey.NumberPad0 && (int)args.Key <= (int)VirtualKey.NumberPad9) name = "NumPad" + ((int)args.Key - (int)VirtualKey.NumberPad0);
                parts.Add(name); input.Text = string.Join('+', parts); args.Handled = true;
                _media.Set(key, input.Text); Changed();
            };
            input.LostFocus += (_, _) => { _media.Set(key, input.Text); Changed(); }; row.Children.Add(input);
            var status = (_media.Get("mediaHotkeyStatuses") as JsonObject)?[Text(definition["statusKey"])]; if (status is not null) row.Children.Add(Copy("注册状态：" + Text(status)));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var clear = new Button { Content = "清除" }; clear.Click += (_, _) => { input.Text = ""; _media.Set(key, ""); Changed(); }; buttons.Children.Add(clear);
            buttons.Children.Add(ActionButton("恢复默认", () => ServiceCommandAsync("resetMediaHotkey", definition["action"]?.DeepClone()))); row.Children.Add(buttons); section.Children.Add(row);
        }
    }
    private void RenderOffsets()
    {
        var current = Section("当前歌曲", "偏移按歌曲与歌词源分别保存，修改后立即作用于当前歌词。");
        if (_serviceEvents.GetValueOrDefault("currentTrackOffset") is JsonObject track) {
            current.Children.Add(new TextBlock { Text = Text(track["title"]) + " — " + Text(track["artist"]), TextWrapping = TextWrapping.Wrap });
            current.Children.Add(Copy("歌词源：" + Text(track["lyricSource"]) + "；播放器偏移：" + Text(track["playerOffsetMilliseconds"]) + " ms"));
            var offset = new NumberBox { Header = "单曲偏移（毫秒）", Value = Number(track["trackOffsetMilliseconds"]), Minimum = -5000, Maximum = 5000, SmallChange = 100, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            current.Children.Add(offset); current.Children.Add(ActionButton("应用到当前歌曲", () => ServiceCommandAsync("setCurrentTrackOffset", JsonValue.Create(offset.Value))));
        } else current.Children.Add(Copy("尚无确定歌词源的歌曲。"));
        var history = Section("已保存偏移");
        var search = new TextBox { PlaceholderText = "搜索歌曲或歌手", Text = _offsetSearch }; history.Children.Add(search);
        history.Children.Add(ActionButton("搜索 / 刷新", async () => { _offsetSearch = search.Text; _offsetPage = 1; await LoadOffsetsAsync(); }));
        if (_serviceEvents.GetValueOrDefault("trackOffsetEntries") is JsonObject page && page["entries"] is JsonArray entries) {
            foreach (var entry in entries.OfType<JsonObject>()) {
                var content = new StackPanel { Spacing = 6 };
                var offset = new NumberBox { Header = "偏移（毫秒）", Value = Number(entry["offsetMilliseconds"]), Minimum = -5000, Maximum = 5000, SmallChange = 100, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact }; content.Children.Add(offset);
                content.Children.Add(Copy("歌词源：" + Text(entry["lyricSource"]) + "；播放器：" + Text(entry["sourceApp"])));
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                buttons.Children.Add(ActionButton("保存偏移", () => ServiceCommandAsync("setStoredTrackOffset", new JsonObject { ["key"] = entry["key"]?.DeepClone(), ["offsetMilliseconds"] = offset.Value })));
                buttons.Children.Add(ActionButton("删除这条偏移", () => ServiceCommandAsync("deleteTrackOffset", entry["key"]?.DeepClone()))); content.Children.Add(buttons);
                history.Children.Add(new Expander { Header = Text(entry["title"]) + " — " + Text(entry["artist"]) + $"  {Number(entry["offsetMilliseconds"]):+0;-0;0} ms", Content = content, HorizontalAlignment = HorizontalAlignment.Stretch });
            }
            var paging = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var previous = ActionButton("上一页", async () => { _offsetPage--; await LoadOffsetsAsync(); }); previous.IsEnabled = _offsetPage > 1; paging.Children.Add(previous);
            paging.Children.Add(Copy($"{_offsetPage} / {Number(page["pageCount"], 1):0} 页，共 {Number(page["totalCount"]):0} 条"));
            var next = ActionButton("下一页", async () => { _offsetPage++; await LoadOffsetsAsync(); }); next.IsEnabled = _offsetPage < Number(page["pageCount"], 1); paging.Children.Add(next); history.Children.Add(paging);
        }
        if (!_queryingOffsets && !_serviceEvents.ContainsKey("trackOffsetEntries")) _ = GuardAsync(LoadOffsetsAsync);
    }
    private async Task LoadOffsetsAsync()
    {
        if (_queryingOffsets) return;
        _queryingOffsets = true;
        try { await ServiceCommandAsync("trackOffsetsPageActivated"); await ServiceCommandAsync("queryTrackOffsets", new JsonObject { ["page"] = _offsetPage, ["pageSize"] = 25, ["search"] = _offsetSearch, ["lyricSource"] = "All", ["sort"] = "updated" }); }
        finally { _queryingOffsets = false; }
    }
    private void RenderDiagnostics()
    {
        var section = Section("歌词匹配", "查找当前歌曲的歌词候选，比较标题、歌手、时长和匹配分，再决定是否应用。");
        section.Children.Add(ActionButton("查找当前歌曲", () => ServiceCommandAsync("runLyricDiagnostics")));
        if (_serviceEvents.GetValueOrDefault("lyricDiagnosticsState") is not JsonObject state) return;
        if (state["message"] is { } message) section.Children.Add(Copy(Text(message)));
        if (state["apply"] is JsonObject applied) section.Children.Add(Copy(Text(applied["message"])));
        if (state["report"] is not JsonObject report) return;
        if (report["error"] is { } error) section.Children.Add(Copy(Text(error)));
        if (report["providers"] is JsonArray providers) foreach (var provider in providers.OfType<JsonObject>()) {
            var content = new StackPanel { Spacing = 8 }; var providerId = Text(provider["providerId"]);
            if (provider["candidates"] is JsonArray candidates) foreach (var candidate in candidates.OfType<JsonObject>()) {
                content.Children.Add(new TextBlock { Text = Text(candidate["title"]) + " — " + string.Join(" / ", (candidate["artists"] as JsonArray)?.Select(node => Text(node)) ?? []), TextWrapping = TextWrapping.Wrap });
                content.Children.Add(Copy($"匹配分 {Number(candidate["score"]):0}；时长 {Number(candidate["durationSeconds"]):0} 秒；{Text(candidate["album"])}"));
                if (candidate["rejectionReasons"] is JsonArray reasons && reasons.Count > 0) content.Children.Add(Copy(string.Join("；", reasons.Select(node => Text(node)))));
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                foreach (var (label, mode) in new[] { ("应用本次", "current"), ("应用并记住", "remember") }) {
                    var apply = ActionButton(label, () => ServiceCommandAsync("applyLyricDiagnosticCandidate", new JsonObject { ["providerId"] = providerId, ["candidateId"] = Text(candidate["candidateId"]), ["mode"] = mode })); apply.IsEnabled = candidate["isAdmitted"]?.ToString() == "true"; actions.Children.Add(apply);
                }
                content.Children.Add(actions);
            }
            section.Children.Add(new Expander { Header = ProviderLabel(providerId) + " · " + Text(provider["detail"]), Content = content, HorizontalAlignment = HorizontalAlignment.Stretch });
        }
    }
    private void RenderMaintenance()
    {
        var media = Section("媒体会话诊断", "查看当前媒体标题、来源与 Windows 提供的控制能力。");
        media.Children.Add(ActionButton("刷新媒体状态", async () => { _serviceEvents["mediaSnapshot"] = await RequestAsync("getState"); RenderPage(); }));
        if (_serviceEvents.GetValueOrDefault("mediaSnapshot") is JsonObject snapshot)
        {
            foreach (var (key, label) in new[] { ("title", "歌曲"), ("artist", "歌手"), ("source", "来源"), ("positionSeconds", "播放位置（秒）"), ("durationSeconds", "时长（秒）") })
                media.Children.Add(Copy(label + "：" + Text(snapshot[key], "—")));
            media.Children.Add(Copy(snapshot["isPlaying"]?.ToString() == "true" ? "正在播放" : "已暂停或停止"));
        }
        var section = Section("诊断与维护", "清理歌词缓存不会删除已保存的单曲偏移。");
        section.Children.Add(ActionButton("清理歌词缓存", () => ConfirmCommandAsync("清理歌词缓存？", "已缓存歌词将在下次播放时重新获取。", "clearCache")));
        section.Children.Add(ActionButton("清除全部单曲偏移", () => ConfirmCommandAsync("清除全部单曲偏移？", "这个操作会移除所有歌曲的单独同步调整。", "clearTrackOffsets")));
        section.Children.Add(ActionButton("重置歌词设置", () => ConfirmCommandAsync("重置歌词设置？", "播放器、歌词效果和快捷键会恢复默认值；任务栏监测设置不受影响。", "resetDefaults")));
    }
    private async Task ConfirmCommandAsync(string title, string message, string action)
    {
        var dialog = new ContentDialog { XamlRoot = _navigation.XamlRoot, Title = title, Content = message, PrimaryButtonText = "继续", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ServiceCommandAsync(action);
    }
    private void RenderAbout()
    {
        var section = Section("TaskBarWidgets", "任务栏监测、歌词与媒体音频的统一 Windows 应用。");
        section.Children.Add(Copy("版本 " + _media.Text("appVersion", "0.1.0")));
        section.Children.Add(new HyperlinkButton { Content = "项目主页", NavigateUri = new Uri("https://github.com/NoctisWang528/TaskBarWidget") });
        section.Children.Add(Copy("基于 TrafficMonitor、TaskbarLyrics 与 AF-Media-Bar。包含 Battery、PowerMon、WeatherPro 的集成实现。上游许可证与第三方署名见随程序分发的 THIRD-PARTY-NOTICES.md。"));
    }
    private async Task ServiceCommandAsync(string action, JsonNode? value = null)
    {
        var snapshot = _media.Capture();
        var response = await RequestAsync("settingsCommand", new { action, value });
        if (response["settings"] is JsonObject settings) _media.Readback(MediaValues(settings), snapshot, preservePending: true);
        if (response["events"] is JsonArray events) foreach (var item in events.OfType<JsonObject>()) {
            var type = Text(item["type"]); _serviceEvents[type] = item["payload"]?.DeepClone();
            if (item["payload"] is JsonObject payload && (Text(payload["status"]) == "error" || payload["isSaved"]?.ToString() == "false")) ShowError(Text(payload["message"], "操作未完成，请重试。"));
        }
        ApplyTheme(); RenderPage(); UpdateSaveStatus();
    }
}
