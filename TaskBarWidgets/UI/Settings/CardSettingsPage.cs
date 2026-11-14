using System.Text.Json.Nodes;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using TaskBarWidgets.UI.Cards;
using TaskBarWidgets.UI.Settings;

namespace TaskBarWidgets.UI;

public sealed partial class SettingsWindow
{
    private static readonly (string Id, string Label, string Glyph)[] QuickCardDefinitions =
    [
        ("weather", "天气", "\uE9CA"),
        ("traffic", "流量消耗", "\uE9D9"),
        ("device", "音频设备", "\uE7F5"),
        ("media", "媒体", "\uE8D6"),
        ("peripheralBattery", "外设电量", "\uE996")
    ];
    private static readonly (string Id, string Label)[] CardMaterials = [("LiquidGlass", "液态玻璃"), ("Mica", "Mica"), ("Solid", "纯色")];
    private readonly List<(Border Cell, int Column, int Slot)> _cardOrderCells = [];
    private TextBlock? _micaStatus;
    private void UpdateMicaStatus()
    {
        if (_currentPage != "quickCards" || _micaStatus is null) return;
        var theme = _root.ActualTheme == ElementTheme.Dark ? "深色" : "浅色";
        if (DraftCardMaterial != "Mica")
        {
            _micaStatus.Text = CardHighContrast ? "高对比度使用系统纯色背景，材质参数保留。"
                : $"卡片预览：{theme}模式。请打开快捷卡片查看材质；设置窗口背景保持 Mica。";
            return;
        }
        var state = _cardMicaPreviewBackdrop.Diagnostics;
        var detail = CardHighContrast ? "高对比度使用系统纯色背景。"
            : !VividMicaBackdrop.IsSupported ? "当前系统不支持 Mica，使用纯色背景。"
            : _cardMicaPreviewBackdrop.NeedsSolidFallback ? "材质连接失败，暂用纯色背景；调节值仍保留。"
            : !state.Connected ? "正在连接材质；当前尚不能预览。"
            : state.State == "Active" ? "Mica 材质正在显示。"
            : !state.InputActive ? "设置窗口未激活，材质可能回退为纯色；激活后恢复。"
            : "系统当前使用纯色回退（例如关闭了透明效果），调节值保留，材质恢复后生效。";
        _micaStatus.Text = $"当前预览：{theme}模式。另一主题的参数需切换界面主题后预览。\n{detail}";
    }
    private List<string> QuickCardOrderList()
    {
        var value = _media.Get("quickCardOrder") as JsonArray;
        var result = (value ?? []).OfType<JsonValue>()
            .Select(item => item.TryGetValue<string>(out var id) ? id : "")
            .Where(id => QuickCardDefinitions.Any(card => card.Id == id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var card in QuickCardDefinitions)
            if (!result.Contains(card.Id, StringComparer.Ordinal)) result.Add(card.Id);
        return result;
    }

    private Dictionary<string, int> QuickCardColumnValues()
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (_media.Get("quickCardColumns") is JsonObject value)
            foreach (var item in value)
                if (item.Value is JsonValue column && column.TryGetValue<int>(out var number) && (number == 0 || number == 1))
                    result[item.Key] = number;
        var order = QuickCardOrderList();
        for (var index = 0; index < order.Count; index++)
            if (!result.ContainsKey(order[index])) result[order[index]] = order[index] == "peripheralBattery" ? 1 : index % 2;
        foreach (var key in result.Keys.Where(key => !QuickCardDefinitions.Any(card => card.Id == key)).ToArray())
            result.Remove(key);
        return result;
    }

    private (List<string> Left, List<string> Right) QuickCardLayout()
    {
        var order = QuickCardOrderList();
        var columns = QuickCardColumnValues();
        return (order.Where(id => columns[id] == 0).ToList(), order.Where(id => columns[id] == 1).ToList());
    }

    private bool QuickCardEnabled(string id) => _media.Get("quickCardEnabled") is not JsonObject enabled ||
        !enabled.TryGetPropertyValue(id, out var value) || value?.GetValue<bool>() != false;

    private void SetQuickCardLayout(List<string> left, List<string> right)
    {
        var order = left.Concat(right).ToList();
        var columns = new JsonObject();
        foreach (var id in left) columns[id] = 0;
        foreach (var id in right) columns[id] = 1;
        _media.SetNode("quickCardOrder", new JsonArray(order.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()));
        _media.SetNode("quickCardColumns", columns);
        Changed();
        RenderPage();
    }

    private void ToggleQuickCard(string id, bool enabled)
    {
        var values = _media.Get("quickCardEnabled") as JsonObject ?? new JsonObject();
        var current = QuickCardDefinitions.ToDictionary(
            card => card.Id,
            card => QuickCardEnabled(card.Id),
            StringComparer.Ordinal);
        current[id] = enabled;
        foreach (var card in QuickCardDefinitions)
            values[card.Id] = current[card.Id];
        _media.SetNode("quickCardEnabled", values);
        Changed();
        RenderPage();
    }

    private void RenderQuickCards()
    {
        if (_currentPage == "quickCardsWeather")
        {
            RenderQuickCardsWeather();
            return;
        }

        var (left, right) = QuickCardLayout();
        var appearance = Section("卡片字体", "所有快捷卡片的文字使用同一套系统字体。");
        var font = new ComboBox { IsEditable = true, IsTextSearchEnabled = true, HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0 };
        AutomationProperties.SetName(font, "卡片字体");
        AutomationProperties.SetAutomationId(font, "setting-cardFontFamily");
        foreach (var option in _fontOptions) font.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Value });
        var current = _media.Text("cardFontFamily", "Microsoft YaHei UI");
        var selectedFont = font.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Tag as string, current, StringComparison.OrdinalIgnoreCase));
        if (selectedFont is null) { selectedFont = new ComboBoxItem { Content = current, Tag = current }; font.Items.Add(selectedFont); }
        font.SelectedItem = selectedFont;
        font.Text = selectedFont.Content?.ToString() ?? current;
        void CommitCardFont(string value)
        {
            value = value.Trim();
            if (value.Length is 0 or >= 32) { ShowError("请选择字体或输入不超过 31 个字符的字体名称。"); return; }
            _media.Set("cardFontFamily", value); Changed();
        }
        font.SelectionChanged += (_, _) => { if (font.SelectedItem is ComboBoxItem item && item.Tag is string name) CommitCardFont(name); };
        font.TextSubmitted += (_, args) => { CommitCardFont(args.Text); args.Handled = true; };
        font.LostFocus += (_, _) => CommitCardFont(font.Text);
        appearance.Children.Add(new TextBlock { Text = "卡片字体", FontSize = 14 });
        appearance.Children.Add(font);
        appearance.Children.Add(Copy("字体列表不可用时仍可输入系统已安装字体名称。"));

        var material = Section("卡片材质", "材质支持浅色与深色主题，点击“应用更改”后保存。");
        var materialPicker = new ComboBox { Header = "材质", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var option in CardMaterials) materialPicker.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Id });
        materialPicker.SelectedItem = materialPicker.Items.OfType<ComboBoxItem>().First(item => (string)item.Tag == DraftCardMaterial);
        AutomationProperties.SetName(materialPicker, "卡片材质");
        AutomationProperties.SetAutomationId(materialPicker, "setting-cardMaterial");
        materialPicker.SelectionChanged += (_, _) => {
            if (materialPicker.SelectedItem is ComboBoxItem item) { _media.Set("cardMaterial", (string)item.Tag); Changed(); RenderPage(); }
        };
        material.Children.Add(materialPicker);
        if (DraftCardMaterial == "LiquidGlass")
        {
            var blur = new Slider { Header = "透明 — 模糊", Minimum = 0, Maximum = 100, StepFrequency = 1, Value = DraftCardGlassBlur * 100, HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(blur, "液态玻璃透明到模糊");
            AutomationProperties.SetAutomationId(blur, "setting-cardGlassBlur");
            blur.ValueChanged += (_, _) => { _media.Set("cardGlassBlur", blur.Value / 100); Changed(); PreviewCardMica(DraftCardMicaTuning); };
            material.Children.Add(blur);
            material.Children.Add(Copy("两种主题共用此调节。打开快捷卡片查看真实材质；设置窗口背景保持 Mica。"));
            material.Children.Add(Copy("实时折射启用时，玻璃卡片及其菜单不会出现在系统截图和录屏中。需要录制卡片时可切换为 Mica 或纯色。"));
        }
        AutomationProperties.SetAutomationId(material, "section-cardMica");
        var canPreview = CanPreviewCardMaterial;
        if (canPreview) ActivateCardMicaPreview(DraftCardMicaTuning);
        _micaStatus = Copy("");
        AutomationProperties.SetAutomationId(_micaStatus, "cardMicaStatus");
        material.Children.Add(_micaStatus);
        UpdateMicaStatus();
        if (DraftCardMaterial == "Mica")
        {
            material.Children.Add(Copy("Mica 使用壁纸颜色，不透出后方窗口。中性色遮罩和明暗调和不代表透明度。"));
            var initialMica = DraftCardMicaTuning;
            var initialVivid = DraftCardMicaVivid;
            var vivid = SettingsToggle.Row("自定义 Mica", "关闭后卡片改用系统默认 Mica 材质，下面四项调节暂不生效，原参数仍会保留。",
                initialVivid, "自定义 Mica", "setting-cardMicaVivid",
                value => { _media.Set("cardMicaVivid", value); Changed(); RenderPage(); });
            material.Children.Add(vivid);
            Slider MicaSlider(string name, string automationId, double value)
            {
                var slider = new Slider
                {
                    Minimum = 0,
                    Maximum = 100,
                    StepFrequency = 1,
                    SnapsTo = SliderSnapsTo.StepValues,
                    IsThumbToolTipEnabled = true,
                    IsEnabled = !CardHighContrast && VividMicaBackdrop.IsSupported && initialVivid,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Value = Math.Round(value)
                };
                AutomationProperties.SetName(slider, name);
                AutomationProperties.SetAutomationId(slider, automationId);
                return slider;
            }

            var lightTint = MicaSlider("浅色模式卡片 Mica 中性色遮罩", "setting-cardMicaTintOpacity", initialMica.LightTintOpacity * 100);
            var lightLuminosity = MicaSlider("浅色模式卡片 Mica 明暗调和", "setting-cardMicaLuminosityOpacity", initialMica.LightLuminosityOpacity * 100);
            var darkTint = MicaSlider("深色模式卡片 Mica 中性色遮罩", "setting-cardMicaDarkTintOpacity", initialMica.DarkTintOpacity * 100);
            var darkLuminosity = MicaSlider("深色模式卡片 Mica 明暗调和", "setting-cardMicaDarkLuminosityOpacity", initialMica.DarkLuminosityOpacity * 100);

            var suppressMicaEvents = false;
            void UpdateMicaLabels()
            {
                lightTint.Header = $"中性色遮罩 · {lightTint.Value:0}%";
                lightLuminosity.Header = $"明暗调和 · {lightLuminosity.Value:0}%";
                darkTint.Header = $"中性色遮罩 · {darkTint.Value:0}%";
                darkLuminosity.Header = $"明暗调和 · {darkLuminosity.Value:0}%";
            }
            void BindMicaSlider(Slider slider, string key)
            {
                slider.ValueChanged += (_, _) =>
                {
                    if (suppressMicaEvents) return;
                    UpdateMicaLabels();
                    // Only the edited value changes. Other theme values can have
                    // finer precision than the integer slider and must survive.
                    _media.Set(key, slider.Value / 100);
                    Changed();
                    PreviewCardMica(DraftCardMicaTuning);
                };
            }

            suppressMicaEvents = true;
            lightTint.Value = Math.Round(initialMica.LightTintOpacity * 100);
            lightLuminosity.Value = Math.Round(initialMica.LightLuminosityOpacity * 100);
            darkTint.Value = Math.Round(initialMica.DarkTintOpacity * 100);
            darkLuminosity.Value = Math.Round(initialMica.DarkLuminosityOpacity * 100);
            suppressMicaEvents = false;
            UpdateMicaLabels();
            BindMicaSlider(lightTint, "cardMicaTintOpacity");
            BindMicaSlider(lightLuminosity, "cardMicaLuminosityOpacity");
            BindMicaSlider(darkTint, "cardMicaDarkTintOpacity");
            BindMicaSlider(darkLuminosity, "cardMicaDarkLuminosityOpacity");

            var lightGroup = new StackPanel { Spacing = 8 };
            lightGroup.Children.Add(new TextBlock { Text = "浅色模式", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            lightGroup.Children.Add(lightTint);
            lightGroup.Children.Add(lightLuminosity);
            material.Children.Add(lightGroup);
            var darkGroup = new StackPanel { Spacing = 8 };
            darkGroup.Children.Add(new TextBlock { Text = "深色模式", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            darkGroup.Children.Add(darkTint);
            darkGroup.Children.Add(darkLuminosity);
            material.Children.Add(darkGroup);
            if (CardHighContrast || !VividMicaBackdrop.IsSupported)
            {
                material.Children.Add(new InfoBar
                {
                    Severity = InfoBarSeverity.Informational,
                    IsOpen = true,
                    IsClosable = false,
                    Title = CardHighContrast ? "高对比度模式已启用" : "当前系统不支持 Mica",
                    Message = "材质调节暂不可用；卡片会继续使用系统回退色，文字和控件保持可读。"
                });
            }
            material.Children.Add(Copy("设置窗口背景实时预览当前主题；打开快捷卡片时会继承本页草稿。离开本页恢复已保存值，再次进入可继续预览草稿。"));

        }

        var section = Section("卡片与顺序", "卡片可独立启用。拖动手柄把卡片放进左/右任意一列的任意位置，每列数量不限；关闭的卡片仍保留。");
        _cardOrderCells.Clear();
        var list = new Grid { ColumnSpacing = 10 };
        list.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        list.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var (columnIds, column) in new[] { (left, 0), (right, 1) })
        {
            var panel = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Top };
            for (var slot = 0; slot < columnIds.Count; slot++)
            {
                var surface = BuildQuickCardCell(columnIds[slot], column, slot);
                _cardOrderCells.Add((surface, column, slot));
                panel.Children.Add(surface);
            }
            var empty = BuildQuickCardEmptyCell(column);
            _cardOrderCells.Add((empty, column, columnIds.Count));
            panel.Children.Add(empty);
            Grid.SetColumn(panel, column);
            list.Children.Add(panel);
        }
        section.Children.Add(list);
        section.Children.Add(Copy("Alt + ↑ / ↓ 可在本列内调整顺序。更新提示卡不属于普通卡片，不在此列表中，并固定显示在最右侧。"));

        var enabledCount = QuickCardDefinitions.Count(card => QuickCardEnabled(card.Id));
        if (enabledCount == 0)
        {
            var warning = new InfoBar
            {
                Severity = InfoBarSeverity.Warning,
                IsOpen = true,
                IsClosable = false,
                Title = "所有普通卡片均已关闭",
                Message = "保存后，点击任务栏入口不会显示卡片。可以随时在这里重新启用任意一张。"
            };
            section.Children.Add(warning);
        }

        var motion = Section("进出动画", "控制整组卡片从屏幕底部进入和退出的动画，保存后生效。");
        var speed = new Slider
        {
            Minimum = 50, Maximum = 300, StepFrequency = 10, SmallChange = 10, LargeChange = 50,
            Value = Math.Clamp(_media.Number("quickCardAnimationSpeedPercent", 150), 50, 300),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(speed, "卡片动画速度百分比");
        AutomationProperties.SetAutomationId(speed, "setting-quickCardAnimationSpeedPercent");
        speed.Header = $"动画速度 · {speed.Value / 100:0.0}×";
        speed.ValueChanged += (_, _) =>
        {
            speed.Header = $"动画速度 · {speed.Value / 100:0.0}×";
            _media.Set("quickCardAnimationSpeedPercent", (int)Math.Round(speed.Value));
            Changed();
        };
        motion.Children.Add(speed);
        motion.Children.Add(Copy("0.5× 更慢，3× 更快；默认 1.5×。"));
        var frameRates = new[] { 0, 30, 60, 90, 120, 144, 165, 240 };
        var frameRate = new ComboBox { Header = "目标帧率", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var fps in frameRates) frameRate.Items.Add(fps == 0 ? "跟随屏幕（最高 240 FPS）" : $"{fps} FPS");
        var selectedFps = Array.IndexOf(frameRates, (int)_media.Number("quickCardAnimationFps"));
        frameRate.SelectedIndex = selectedFps >= 0 ? selectedFps : 0;
        AutomationProperties.SetName(frameRate, "卡片动画目标帧率");
        AutomationProperties.SetAutomationId(frameRate, "setting-quickCardAnimationFps");
        frameRate.SelectionChanged += (_, _) =>
        {
            if (frameRate.SelectedIndex < 0) return;
            _media.Set("quickCardAnimationFps", frameRates[frameRate.SelectedIndex]);
            Changed();
        };
        motion.Children.Add(frameRate);
        motion.Children.Add(Copy("较高帧率更流畅，也会增加动画期间的资源消耗；实际帧率受屏幕和系统负载影响。系统关闭动画时仍立即显示或隐藏。"));

        var performance = Section("后台资源", "程序启动后会静默预加载一次卡片。常驻模式继续在后台更新画面；按需模式完成预加载后停止隐藏绘制，闲置 10 分钟后释放窗口和绘图缓存。");
        var modes = new[] { "Resident", "OnDemand" };
        var mode = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        mode.Items.Add("常驻（优先速度）");
        mode.Items.Add("按需（优先内存）");
        mode.SelectedIndex = Array.IndexOf(modes, _media.Text("quickCardResourceMode", "Resident")) is var selected && selected >= 0 ? selected : 0;
        AutomationProperties.SetName(mode, "后台资源模式");
        mode.SelectionChanged += (_, _) =>
        {
            if (mode.SelectedIndex < 0) return;
            _media.Set("quickCardResourceMode", modes[mode.SelectedIndex]);
            Changed();
        };
        performance.Children.Add(mode);
        performance.Children.Add(Copy("常驻模式占用更多内存，隐藏时不播放动画；按需模式释放资源后，再次打开需要重新准备画面。"));
    }

    private void RenderQuickCardsWeather()
    {
        var enabled = QuickCardEnabled("weather");
        var status = Section("天气卡状态");
        status.Children.Add(new TextBlock
        {
            Text = enabled ? "已启用" : "未启用",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        status.Children.Add(Copy("天气卡的启停开关位于“快捷卡片 → 卡片与顺序”的天气行。此处配置来源、城市和连接。"));

        RenderWeatherSettings();
    }

    private Border BuildQuickCardCell(string id, int column, int slot)
    {
        var definition = QuickCardDefinitions.First(card => card.Id == id);
        Border? surface = null;
        var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(8, 10, 8, 10), MinHeight = 64, Tag = "CardPriorityRowContent" };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var handle = new PointerDragButton
        {
            Content = ReorderDots(),
            DragLabel = definition.Label,
            Width = 32,
            Height = 36,
            Padding = new Thickness(0),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Tag = "card:" + id
        };
        AutomationProperties.SetName(handle, definition.Label + "，拖动排序，Alt 加上下方向键调整");
        ToolTipService.SetToolTip(handle, "拖动调整卡片顺序；Alt + ↑ / ↓ 在本列内调整");
        handle.StartPointerDrag = (source, args) => StartQuickCardPointerDrag(source, id, args, surface);
        AddCardRow(row, handle, 0);

        var labels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var heading = new Grid { ColumnSpacing = 6, VerticalAlignment = VerticalAlignment.Center };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.Children.Add(new FontIcon { Glyph = definition.Glyph, FontSize = 16, Width = 18, VerticalAlignment = VerticalAlignment.Center });
        var title = new TextBlock { Text = definition.Label, FontSize = 14, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1);
        heading.Children.Add(title);
        labels.Children.Add(heading);
        labels.Children.Add(new TextBlock
        {
            Text = $"{(column == 0 ? "左列" : "右列")} · 第 {slot + 1} 位",
            FontSize = 12,
            Opacity = .72,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        });
        AddCardRow(row, labels, 1);

        var enabled = SettingsToggle.Compact(QuickCardEnabled(id), "启用" + definition.Label,
            "quick-card-enabled-" + id, changed: value => ToggleQuickCard(id, value));
        enabled.KeyDown += (_, args) =>
        {
            if ((InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu) & Windows.UI.Core.CoreVirtualKeyStates.Down) == 0)
                return;
            var direction = args.Key switch { Windows.System.VirtualKey.Up => -1, Windows.System.VirtualKey.Down => 1, _ => 0 };
            if (direction == 0) return;
            MoveQuickCardWithinColumn(id, column, direction);
            args.Handled = true;
        };
        AddCardRow(row, enabled, 2);

        var menu = ThemeFlyout(new MenuFlyout());
        var columnCount = column == 0 ? QuickCardLayout().Left.Count : QuickCardLayout().Right.Count;
        foreach (var (label, delta) in new[] { ("提高优先级", -1), ("降低优先级", 1) })
        {
            var move = new MenuFlyoutItem { Text = label, IsEnabled = slot + delta >= 0 && slot + delta < columnCount };
            move.Click += (_, _) => MoveQuickCardWithinColumn(id, column, delta);
            menu.Items.Add(move);
        }
        var more = new Button
        {
            Content = new FontIcon { Glyph = "\uE712", FontSize = 16 },
            Width = 36,
            Height = 36,
            Padding = new Thickness(0),
            Flyout = menu,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(more, definition.Label + "的更多操作");
        AddCardRow(row, more, 3);

        surface = UiThemeStyles.Apply(new Border
        {
            Child = row,
            MinHeight = 64,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Tag = id
        }, "SettingsSourceSurface");
        AutomationProperties.SetAutomationId(surface, "quick-card-row-" + id);
        surface.ContextFlyout = menu;
        return surface;
    }

    private Border BuildQuickCardEmptyCell(int column) => UiThemeStyles.Apply(new Border
    {
        Child = new TextBlock
        {
            Text = "拖到这里",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = .55,
            FontSize = 12
        },
        MinHeight = 64,
        CornerRadius = new CornerRadius(6),
        BorderThickness = new Thickness(1),
        IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Tag = "quick-card-empty:" + column
    }, "SettingsDropPlaceholder");

    private void MoveQuickCardWithinColumn(string id, int column, int direction)
    {
        var (left, right) = QuickCardLayout();
        var list = column == 0 ? left : right;
        var index = list.IndexOf(id);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= list.Count) return;
        list.RemoveAt(index);
        list.Insert(target, id);
        SetQuickCardLayout(left, right);
    }

    private bool StartQuickCardPointerDrag(PointerDragButton source, string id, PointerRoutedEventArgs args, Border? surface)
    {
        if (_busy) return false;
        return _pointerReorder?.Begin(source, args, null, point => MoveQuickCardDrag(id, point),
            point => FinishQuickCardDrag(id, point), surface,
            PointerReorder.CreateDragGhost(
                QuickCardDefinitions.First(card => card.Id == id).Glyph,
                QuickCardDefinitions.First(card => card.Id == id).Label,
                "快捷卡片")) == true;
    }

    private (Border Cell, int Column, int Gap)? FindQuickCardDropTarget(Windows.Foundation.Point point)
    {
        if (_pageScroll is null || !ContainsPoint(_pageScroll, point)) return null;
        var (left, right) = QuickCardLayout();
        foreach (var (cell, column, slot) in _cardOrderCells)
        {
            var bounds = PointerReorder.LayoutBounds(cell, _navigation);
            if (point.X >= bounds.X && point.X <= bounds.Right && point.Y >= bounds.Y - 4 && point.Y <= bounds.Bottom + 4)
            {
                return (cell, column, CardTargetGap(slot, column == 0 ? left.Count : right.Count,
                    point.Y > bounds.Y + bounds.Height / 2));
            }
        }
        return null;
    }

    private void MoveQuickCardDrag(string id, Windows.Foundation.Point point)
    {
        if (_busy) { CancelModulePointerDrag(); return; }
        AutoScrollDrag(point);
        var target = FindQuickCardDropTarget(point);
        foreach (var (cell, _, _) in _cardOrderCells)
        {
            cell.ClearValue(Border.BorderBrushProperty);
            cell.BorderThickness = new Thickness(1);
            if (cell.Tag is string tag && tag.StartsWith("quick-card-empty:", StringComparison.Ordinal)) cell.Opacity = 1;
        }
        _pointerReorder?.HideInsertion();
        if (target is { Column: var targetColumn, Gap: var targetGap })
        {
            var (left, right) = QuickCardLayout();
            var sourceColumn = QuickCardColumnValues().GetValueOrDefault(id);
            var sourceIds = sourceColumn == 0 ? left : right;
            var targetIds = targetColumn == 0 ? left : right;
            var sourceRows = _cardOrderCells
                .Where(cell => cell.Column == sourceColumn && cell.Slot < sourceIds.Count)
                .OrderBy(cell => cell.Slot)
                .Select(cell => (FrameworkElement)cell.Cell)
                .ToArray();
            var targetRows = _cardOrderCells
                .Where(cell => cell.Column == targetColumn && cell.Slot < targetIds.Count)
                .OrderBy(cell => cell.Slot)
                .Select(cell => (FrameworkElement)cell.Cell)
                .ToArray();
            var sourceIndex = sourceIds.IndexOf(id);
            if (sourceIndex >= 0)
            {
                var extent = sourceRows[sourceIndex].ActualHeight + 10;
                if (sourceColumn == targetColumn)
                    _pointerReorder?.ShowRowPreview(sourceRows, sourceIndex, targetGap, extent);
                else
                    _pointerReorder?.ShowCrossLanePreview(sourceRows, sourceIndex, targetRows, targetGap, extent);
                // A shifted last row occupies the old empty tile. Hide that tile
                // so its text/background cannot show through the moving card.
                if (sourceColumn != targetColumn && targetGap < targetIds.Count)
                    _cardOrderCells.Single(cell => cell.Column == targetColumn && cell.Slot == targetIds.Count).Cell.Opacity = 0;
                // The highlighted vacancy is the final slot after removing the
                // dragged item, while pointer hits retain pre-removal gap semantics.
                var finalSlot = targetGap - (sourceColumn == targetColumn && sourceIndex < targetGap ? 1 : 0);
                var anchor = _cardOrderCells.Single(cell => cell.Column == targetColumn && cell.Slot == finalSlot).Cell;
                _pointerReorder?.ShowDropZone(PointerReorder.LayoutBounds(anchor, _navigation));
            }
        }
        else _pointerReorder?.ClearRowPreview();
        _saveStatus.Text = target is null ? "拖到左/右任意格子上调整布局；Esc 取消。" : "释放到高亮格子以应用新的卡片布局。";
    }

    private void FinishQuickCardDrag(string id, Windows.Foundation.Point point)
    {
        if (_busy || FindQuickCardDropTarget(point) is not { Column: var column, Gap: var gap }) return;
        var (left, right) = QuickCardLayout();
        var moved = MoveQuickCardToGap(left, right, id, column, gap);
        if (moved.Left.SequenceEqual(left) && moved.Right.SequenceEqual(right)) return;
        SetQuickCardLayout(moved.Left, moved.Right);
    }

    private static int CardTargetGap(int slot, int columnCount, bool lowerHalf) =>
        slot >= columnCount ? columnCount : Math.Clamp(slot + (lowerHalf ? 1 : 0), 0, columnCount);

    private static (List<string> Left, List<string> Right) MoveQuickCardToGap(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right,
        string id,
        int targetColumn,
        int targetGap)
    {
        var resultLeft = left.ToList();
        var resultRight = right.ToList();
        var sourceColumn = resultLeft.Contains(id) ? 0 : resultRight.Contains(id) ? 1 : -1;
        if (sourceColumn < 0 || targetColumn is not (0 or 1)) return (resultLeft, resultRight);
        var source = sourceColumn == 0 ? resultLeft : resultRight;
        var target = targetColumn == 0 ? resultLeft : resultRight;
        var sourceIndex = source.IndexOf(id);
        source.RemoveAt(sourceIndex);
        if (sourceColumn == targetColumn && sourceIndex < targetGap) targetGap--;
        targetGap = Math.Clamp(targetGap, 0, target.Count);
        target.Insert(Math.Clamp(targetGap, 0, target.Count), id);
        return (resultLeft, resultRight);
    }

    internal static (string[] Left, string[] Right) ProbeQuickCardMove(
        string[] left,
        string[] right,
        string id,
        int targetColumn,
        int targetGap)
    {
        var moved = MoveQuickCardToGap(left, right, id, targetColumn, targetGap);
        return (moved.Left.ToArray(), moved.Right.ToArray());
    }

    internal int ProbeQuickCardDrop(string id, int targetColumn, int targetGap)
    {
        var before = PageRenderCount;
        var cell = _cardOrderCells.Single(item => item.Column == targetColumn && item.Slot == targetGap).Cell;
        var bounds = PointerReorder.LayoutBounds(cell, _navigation);
        FinishQuickCardDrag(id, new Windows.Foundation.Point(bounds.X + bounds.Width / 2, bounds.Y + 4));
        return PageRenderCount - before;
    }

    private static void AddCardRow(Grid row, FrameworkElement element, int column)
    {
        Grid.SetColumn(element, column);
        row.Children.Add(element);
    }
}
