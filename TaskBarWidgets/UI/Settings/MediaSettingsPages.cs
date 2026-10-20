using System.Text.Json.Nodes;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
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
            case "trackOffsets": RenderOffsets(); break;
            case "lyricDiagnostics": RenderDiagnostics(); break;
            case "advanced": RenderMaintenance(); break;
            case "about": RenderAbout(); break;
        }
    }
    private void RenderGeneral()
    {
        if (_schema["startup"] is JsonObject startup)
        {
            var registration = Section("登录启动状态");
            registration.Children.Add(Copy(Text(startup["status"]) switch {
                "enabled" => "已通过当前用户的登录计划任务启用。",
                "repairRequired" => "启动项需要修复，请重新注册到当前程序位置。",
                "error" => "无法读取或验证启动项。",
                _ => "未启用登录启动。" }));
            var path = Text(startup["registeredPath"], Text(startup["legacyPath"]));
            if (!string.IsNullOrEmpty(path)) registration.Children.Add(Copy("已登记位置：" + path));
            registration.Children.Add(Copy("当前程序：" + Text(startup["currentPath"])));
            if (!string.IsNullOrEmpty(Text(startup["error"]))) registration.Children.Add(Copy(Text(startup["error"])));
            var repair = ActionButton("修复并启用登录启动", async () => {
                var result = await RequestAsync("applyNativeSettings", new Dictionary<string, object> { ["general.repair_startup"] = true });
                SetNativeReadback(result, new SettingsDraft.SaveSnapshot(-1, new()));
                RenderPage(); UpdateSaveStatus();
            });
            repair.IsEnabled = !_native.Pending.ContainsKey("general.auto_run");
            if (_native.Pending.ContainsKey("general.auto_run")) registration.Children.Add(Copy("请先应用登录启动开关的更改。"));
            registration.Children.Add(repair);
            if (startup["needsRepair"]?.GetValue<bool>() == true)
                registration.Children.Add(ActionButton("清除旧启动项", async () => {
                    var result = await RequestAsync("applyNativeSettings", new Dictionary<string, object> { ["general.auto_run"] = false });
                    SetNativeReadback(result, new SettingsDraft.SaveSnapshot(-1, new()));
                    RenderPage(); UpdateSaveStatus();
                }));
        }
        var section = Section("应用界面");
        section.Children.Add(MediaEditor("toolWindowTheme", "界面主题", "select", choices: ["System", "Light", "Dark"], labels: ["跟随系统", "浅色", "深色"]));
        section.Children.Add(Copy("主题立即在设置窗口和快捷卡片中同步预览，点击“应用更改”后保存；重新读取可撤销未保存的修改。开机启动由上方主程序设置管理。"));
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
                var dialog = new ContentDialog { XamlRoot = _root.XamlRoot, RequestedTheme = _root.RequestedTheme, Title = "允许读取系统播放声音？", Content = "频谱只在需要显示时读取扬声器或耳机当前播放的声音。可随时在这里撤销。", PrimaryButtonText = "允许并启用", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
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
            spectrum.Children.Add(fields);
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
    private readonly List<(string Id, Border Surface, Button Handle)> _sourceRows = [];
    private Border? _sourceOrderSurface;
    private bool _sourceReorderBusy;
    private string? _sourceDragId;
    private void RenderSources()
    {
        _sourceRows.Clear();
        var sources = Section("播放器优先级", "同时有多个播放器播放时，优先识别靠前的来源。拖动左侧六点柄调整顺序，右侧菜单打开设置。");
        var rows = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        _sourceOrderSurface = new Border { Child = rows, HorizontalAlignment = HorizontalAlignment.Stretch, Tag = "SourcePriorityList" };
        sources.Children.Add(_sourceOrderSurface);
        var list = SourceList().ToList();
        for (var index = 0; index < list.Count; index++) {
            var source = list[index];
            var position = index;
            var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(8, 10, 8, 10), MinHeight = 64, HorizontalAlignment = HorizontalAlignment.Stretch, Tag = "SourcePriorityRowContent" };
            foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) row.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            Border? surface = null;
            var handle = new PointerDragButton { Content = ReorderDots(), DragLabel = source.Label, Width = 32, Height = 36, Padding = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Tag = "source:" + source.Id, IsEnabled = !_sourceReorderBusy };
            AutomationProperties.SetName(handle, source.Label + "，优先级 " + (position + 1) + "；拖动排序，Alt 加上下方向键调整，Enter 打开设置");
            ToolTipService.SetToolTip(handle, "拖动调整优先级；Alt + ↑ / ↓ 调整，Enter 打开设置");
            handle.StartPointerDrag = (button, args) => {
                if (_busy || _sourceReorderBusy) return false;
                _sourceDragId = source.Id;
                return _pointerReorder?.Begin(button, args, null, MoveSourcePointerDrag, point => {
                    if (SourceDropGap(point) is { } gap) { _pointerReorder.Record("source-drop", $"gap={gap}"); _ = GuardAsync(() => ReorderSourceAsync(source.Id, gap)); }
                    else _pointerReorder?.Record("source-drop-rejected", "no-target");
                }, surface, PointerReorder.CreateDragGhost("\uE8D6", source.Label, source.Custom ? "自定义播放器" : "内置播放器")) == true;
            };
            handle.Click += (_, _) => OpenSource(source.Id);
            handle.KeyDown += async (_, args) => {
                var alt = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
                if (!alt || args.Key is not (VirtualKey.Up or VirtualKey.Down)) return;
                args.Handled = true;
                await GuardAsync(() => MoveSourceAsync(source.Id, args.Key == VirtualKey.Up ? -1 : 1));
            };
            row.Children.Add(handle);
            var labels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(new TextBlock { Text = source.Label, FontSize = 14, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 });
            labels.Children.Add(new TextBlock
            {
                Text = $"优先级 {position + 1} · " + (source.Custom ? "自定义播放器" : "内置播放器"),
                FontSize = 12,
                Opacity = .72,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1
            });
            Grid.SetColumn(labels, 1); row.Children.Add(labels);
            var enabled = SettingsToggle.Compact(_media.Bool(source.Key), "启用" + source.Label,
                "source-enabled-" + source.Id,
                changed: value => { _media.Set(source.Key, value); Changed(); });
            Grid.SetColumn(enabled, 2); row.Children.Add(enabled);
            var menu = ThemeFlyout(new MenuFlyout());
            var settings = new MenuFlyoutItem { Text = "设置" }; settings.Click += (_, _) => OpenSource(source.Id); menu.Items.Add(settings);
            foreach (var (text, delta) in new[] { ("提高优先级", -1), ("降低优先级", 1) }) {
                var move = new MenuFlyoutItem { Text = text, IsEnabled = !_sourceReorderBusy && position + delta >= 0 && position + delta < list.Count };
                move.Click += async (_, _) => await GuardAsync(() => MoveSourceAsync(source.Id, delta)); menu.Items.Add(move);
            }
            var more = new Button { Content = new FontIcon { Glyph = "\uE712", FontSize = 16 }, Width = 36, Height = 36, Padding = new Thickness(0), Flyout = menu, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(more, source.Label + "的更多操作");
            Grid.SetColumn(more, 3); row.Children.Add(more);
            surface = UiThemeStyles.Apply(new Border { Child = row, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Stretch, Tag = "SourcePriorityRow" }, "SettingsSourceSurface");
            surface.ContextFlyout = menu;
            _sourceRows.Add((source.Id, surface, handle)); rows.Children.Add(surface);
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
    private static Grid ReorderDots()
    {
        var dots = new Grid { Width = 14, Height = 20, IsHitTestVisible = false };
        for (var index = 0; index < 6; index++) dots.Children.Add(UiThemeStyles.Apply(new Microsoft.UI.Xaml.Shapes.Ellipse {
            Width = 3, Height = 3, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(index % 2 * 7 + 2, index / 2 * 6 + 2, 0, 0) }, "SettingsReorderDot"));
        return dots;
    }
    private int? SourceDropGap(Point point)
    {
        if (_sourceOrderSurface is null || _pageScroll is null || !ContainsPoint(_pageScroll, point) || !ContainsPoint(_sourceOrderSurface, point)) return null;
        for (var index = 0; index < _sourceRows.Count; index++) {
            var row = _sourceRows[index].Surface;
            var origin = row.TransformToVisual(_navigation).TransformPoint(new Point(0, 0));
            if (point.Y < origin.Y + row.ActualHeight / 2) return index;
        }
        return _sourceRows.Count;
    }
    private void MoveSourcePointerDrag(Point point)
    {
        if (_busy || _sourceReorderBusy) { CancelModulePointerDrag(); return; }
        _split.IsPaneOpen = false;
        AutoScrollDrag(point);
        ResetSourceReorderFeedback();
        if (SourceDropGap(point) is not { } gap)
        {
            _pointerReorder?.HideInsertion();
            _saveStatus.Text = "将来源拖到播放器列表内后释放；Esc 取消。";
            return;
        }
        var rows = _sourceRows.Select(item => (FrameworkElement)item.Surface).ToArray();
        var sourceIndex = _sourceDragId is null ? -1 : _sourceRows.FindIndex(item => item.Id == _sourceDragId);
        if (sourceIndex >= 0)
            _pointerReorder?.ShowRowPreview(rows, sourceIndex, gap, rows[sourceIndex].ActualHeight + 8);
        if (rows.Length > 0)
        {
            if (gap < rows.Length) _pointerReorder?.ShowVerticalGap(rows[gap], false);
            else _pointerReorder?.ShowVerticalGap(rows[^1], true);
        }
        if (_sourceRows.Count > 0) {
            var row = _sourceRows[Math.Min(gap, _sourceRows.Count - 1)].Surface;
            row.BorderBrush = new SolidColorBrush(AccentColor());
            row.BorderThickness = new Thickness(1);
        }
        _saveStatus.Text = "释放后调整播放器优先级；Esc 取消。";
    }
    private void ResetSourceReorderFeedback()
    {
        foreach (var row in _sourceRows) { row.Surface.ClearValue(Border.BorderBrushProperty); row.Surface.BorderThickness = new Thickness(1); }
    }
    private Task MoveSourceAsync(string id, int delta)
    {
        var order = SourceList().Select(source => source.Id).ToList();
        var index = order.IndexOf(id);
        if (index < 0 || index + delta < 0 || index + delta >= order.Count) return Task.CompletedTask;
        return ReorderSourceAsync(id, index + delta + (delta > 0 ? 1 : 0));
    }
    private async Task ReorderSourceAsync(string id, int gap)
    {
        if (_busy || _sourceReorderBusy) return;
        var order = SourceList().Select(source => source.Id).ToArray();
        var updated = ReorderSequence.MoveToGap(order, id, gap);
        if (order.SequenceEqual(updated)) return;
        _sourceReorderBusy = true;
        _saveStatus.Text = "正在保存播放器优先级…";
        try { await ServiceCommandAsync("reorderSources", JsonSerializerNode(updated)); }
        finally { _sourceReorderBusy = false; if (_currentPage == "sources") { RenderPage(); FocusModule("source:" + id); } UpdateSaveStatus(); }
    }
    private void OpenSource(string id)
    {
        var source = SourceList().First(item => item.Id == id);
        _properties.Children.Clear();
        var close = new Button { Content = "关闭属性", HorizontalAlignment = HorizontalAlignment.Right }; close.Click += (_, _) => _split.IsPaneOpen = false; _properties.Children.Add(close);
        _properties.Children.Add(new TextBlock { Text = source.Label, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var ordering = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (label, delta) in new[] { ("优先", -1), ("靠后", 1) }) ordering.Children.Add(ActionButton(label, async () => { await MoveSourceAsync(id, delta); OpenSource(id); }));
        _properties.Children.Add(ordering);
        _properties.Children.Add(MediaEditor("playerLyricOffset:" + id, "歌词同步偏移（毫秒）", "number", -5000, 5000));
        _properties.Children.Add(Copy("歌词源按优先顺序尝试，关闭某个歌词源会保留其位置。浏览器媒体仅显示元数据，不查找歌词。"));
        if (_media.Get("playerLyricProviders:" + id) is JsonArray providers) {
            for (var index = 0; index < providers.Count; index++) {
                if (providers[index] is not JsonObject provider) continue;
                var position = index;
                var line = new Grid { ColumnSpacing = 4 };
                for (var column = 0; column < 4; column++)
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = column == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
                var check = SettingsToggle.Compact(provider["enabled"]?.ToString() == "true", ProviderLabel(Text(provider["providerId"])),
                    $"lyric-provider-enabled-{id}-{Text(provider["providerId"])}");
                void Toggle(bool enabled) { var updated = (JsonArray)_media.Get("playerLyricProviders:" + id)!; updated[position]!["enabled"] = enabled; _media.SetNode("playerLyricProviders:" + id, updated); Changed(); }
                check.Toggled += (_, _) => Toggle(check.IsOn);
                line.Children.Add(new TextBlock { Text = ProviderLabel(Text(provider["providerId"])), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
                Grid.SetColumn(check, 1); line.Children.Add(check);
                foreach (var (delta, column, glyph) in new[] { (-1, 2, "↑"), (1, 3, "↓") }) {
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
        try { await ServiceCommandBatchAsync(
            ("trackOffsetsPageActivated", null),
            ("queryTrackOffsets", new JsonObject { ["page"] = _offsetPage, ["pageSize"] = 25, ["search"] = _offsetSearch, ["lyricSource"] = "All", ["sort"] = "updated" })); }
        finally { _queryingOffsets = false; }
    }
    internal Task ProbeLoadOffsetsAsync(string search, int page)
    {
        _offsetSearch = search;
        _offsetPage = page;
        return LoadOffsetsAsync();
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
        section.Children.Add(ActionButton("重置歌词设置", () => ConfirmCommandAsync("重置歌词设置？", "播放器和歌词效果会恢复默认值；任务栏监测设置不受影响。", "resetDefaults")));
    }
    private async Task ConfirmCommandAsync(string title, string message, string action)
    {
        var dialog = new ContentDialog { XamlRoot = _root.XamlRoot, RequestedTheme = _root.RequestedTheme, Title = title, Content = message, PrimaryButtonText = "继续", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ServiceCommandAsync(action);
    }
    private void RenderAbout()
    {
        var section = Section("TaskBarWidgets", "任务栏监测、歌词与媒体音频的统一 Windows 应用。");
        section.Children.Add(Copy("版本 " + _media.Text("appVersion", "0.1.0")));
        section.Children.Add(new HyperlinkButton { Content = "项目主页", NavigateUri = new Uri("https://github.com/NoctisWang528/TaskBarWidget") });
        section.Children.Add(Copy("基于 TrafficMonitor、TaskbarLyrics 与 AF-Media-Bar。包含 Battery、PowerMon、WeatherPro 的集成实现。上游许可证与第三方署名见随程序分发的 THIRD-PARTY-NOTICES.md。"));
        RenderUpdateSettings();
    }
    private async Task ServiceCommandAsync(string action, JsonNode? value = null)
    {
        await ServiceCommandBatchAsync((action, value));
    }

    private async Task ServiceCommandBatchAsync(params (string Action, JsonNode? Value)[] commands)
    {
        var snapshot = _media.Capture();
        foreach (var (action, value) in commands)
            ApplyServiceResponse(await RequestAsync("settingsCommand", new { action, value }), snapshot);
        ApplyTheme(); RenderPage(); UpdateSaveStatus();
    }

    private void ApplyServiceResponse(JsonObject response, SettingsDraft.SaveSnapshot snapshot)
    {
        if (response["settings"] is JsonObject settings) _media.Readback(MediaValues(settings), snapshot, preservePending: true);
        if (response["events"] is JsonArray events) foreach (var item in events.OfType<JsonObject>()) {
            var type = Text(item["type"]); _serviceEvents[type] = item["payload"]?.DeepClone();
            if (item["payload"] is JsonObject payload && (Text(payload["status"]) == "error" || payload["isSaved"]?.ToString() == "false")) ShowError(Text(payload["message"], "操作未完成，请重试。"));
        }
    }
}
