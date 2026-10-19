using System.Text.Json.Nodes;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TaskBarWidgets.UI.Settings;

namespace TaskBarWidgets.UI;

public sealed partial class SettingsWindow
{
    private static readonly (string Id, string Label, string Glyph)[] QuickCardDefinitions =
    [
        ("weather", "天气", "\uE9CA"),
        ("traffic", "流量消耗", "\uE9D9"),
        ("device", "音频设备", "\uE7F5"),
        ("media", "媒体", "\uE8D6")
    ];

    private static readonly (string Id, string Label)[] WeatherModuleDefinitions =
    [
        ("current", "当前天气"),
        ("forecast", "三日预报"),
        ("humidity", "湿度"),
        ("wind", "风速风向"),
        ("airQuality", "空气质量"),
        ("precipitation", "降水"),
        ("updatedAt", "更新时间"),
        ("alerts", "天气预警")
    ];

    private readonly List<Border> _cardOrderRows = [];
    private JsonArray QuickCardOrderValues()
    {
        var value = _media.Get("quickCardOrder") as JsonArray;
        var result = (value ?? []).OfType<JsonValue>()
            .Select(item => item.TryGetValue<string>(out var id) ? id : "")
            .Where(id => QuickCardDefinitions.Any(card => card.Id == id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var card in QuickCardDefinitions)
            if (!result.Contains(card.Id, StringComparer.Ordinal)) result.Add(card.Id);
        return new JsonArray(result.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
    }

    private bool QuickCardEnabled(string id) => _media.Get("quickCardEnabled") is not JsonObject enabled ||
        !enabled.TryGetPropertyValue(id, out var value) || value?.GetValue<bool>() != false;

    private void SetQuickCardOrder(JsonArray order)
    {
        _media.SetNode("quickCardOrder", order);
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

        var order = QuickCardOrderValues();
        var section = Section("卡片与顺序", "四张卡片均可独立启用。拖动手柄调整顺序；关闭的卡片仍保留在列表中。");
        _cardOrderRows.Clear();
        var list = new StackPanel { Spacing = 4 };
        for (var index = 0; index < order.Count; index++)
        {
            var id = order[index]?.GetValue<string>() ?? "";
            var definition = QuickCardDefinitions.First(card => card.Id == id);
            var row = new Grid { ColumnSpacing = 10, MinHeight = 44 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var handle = new PointerDragButton
            {
                Content = new FontIcon { Glyph = "\uE700", FontSize = 16 },
                DragLabel = definition.Label,
                Width = 28,
                Height = 32,
                MinWidth = 0,
                Padding = new Thickness(4),
                Tag = id
            };
            AutomationProperties.SetName(handle, "拖动" + definition.Label + "调整顺序");
            handle.StartPointerDrag = (source, args) => StartQuickCardPointerDrag(source, id, args);
            AddCardRow(row, handle, 0);

            var icon = new FontIcon { Glyph = definition.Glyph, FontSize = 18, VerticalAlignment = VerticalAlignment.Center };
            AddCardRow(row, icon, 1);
            var label = new TextBlock { Text = definition.Label, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
            AddCardRow(row, label, 2);
            var check = new CheckBox { IsChecked = QuickCardEnabled(id), MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(check, "启用" + definition.Label);
            AutomationProperties.SetAutomationId(check, "quick-card-enabled-" + id);
            check.Checked += (_, _) => ToggleQuickCard(id, true);
            check.Unchecked += (_, _) => ToggleQuickCard(id, false);
            check.KeyDown += (_, args) =>
            {
                if ((InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu) & Windows.UI.Core.CoreVirtualKeyStates.Down) == 0)
                    return;
                var current = QuickCardOrderValues().OfType<JsonValue>().Select(value => value.GetValue<string>()).ToList();
                var index = current.IndexOf(id);
                var target = args.Key switch
                {
                    Windows.System.VirtualKey.Up => index - 1,
                    Windows.System.VirtualKey.Down => index + 1,
                    _ => index
                };
                if (target == index || target < 0 || target >= current.Count) return;
                current.RemoveAt(index); current.Insert(target, id);
                SetQuickCardOrder(new JsonArray(current.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()));
                args.Handled = true;
            };
            AddCardRow(row, check, 3);

            var surface = UiThemeStyles.Apply(new Border
            {
                Child = row,
                Padding = new Thickness(10, 4, 12, 4),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Tag = id
            }, "SettingsSourceSurface");
            _cardOrderRows.Add(surface);
            list.Children.Add(surface);
        }
        section.Children.Add(list);
        section.Children.Add(Copy("Alt + ↑ / ↓ 可调整顺序。更新提示卡不属于普通卡片，不在此列表中，并固定显示在第二列。"));

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

        var performance = Section("后台资源", "常驻模式保留歌词浏览器和图片缓存，重新打开更快；按需模式在隐藏 10 分钟后释放这些缓存。");
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
        performance.Children.Add(Copy("系统内存压力较大时，常驻模式仍会把歌词浏览器降为低内存优先级，但不会销毁页面。"));
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
        status.Children.Add(Copy("天气卡的启停开关位于“快捷卡片 → 卡片与顺序”的天气行。此处配置来源、城市和卡片显示内容。"));

        var weatherFields = NativeFields().Where(field => Text(field["group"]) == "builtins.weather").ToList();
        RenderWeatherSettings(weatherFields);

        var modules = Section("天气卡内容", "按当前来源可用的数据自动显示；不支持的模块会隐藏并保留配置。");
        var values = _media.Get("weatherCardModules") as JsonObject ?? new JsonObject();
        var moduleChecks = new List<(string Id, string Label, CheckBox Check)>();
        foreach (var (id, label) in WeatherModuleDefinitions)
        {
            var check = new CheckBox
            {
                Content = label,
                MinHeight = 32,
                IsChecked = values[id]?.GetValue<bool>() ?? id is "current" or "forecast" or "updatedAt"
            };
            AutomationProperties.SetAutomationId(check, "weather-card-module-" + id);
            check.Checked += (_, _) => SetWeatherModule(id, true);
            check.Unchecked += (_, _) => SetWeatherModule(id, false);
            moduleChecks.Add((id, label, check));
            modules.Children.Add(check);
        }
        if (_weatherPage is { } weatherEditor)
        {
            void UpdateAvailability()
            {
                foreach (var (id, label, check) in moduleChecks)
                {
                    var available = id switch
                    {
                        "current" => true,
                        "forecast" => weatherEditor.Capabilities?["forecast"]?.GetValue<bool>() ?? true,
                        "humidity" => weatherEditor.Capabilities?["humidity"]?.GetValue<bool>() ?? true,
                        "wind" => weatherEditor.Capabilities?["wind"]?.GetValue<bool>() ?? true,
                        "airQuality" => weatherEditor.Capabilities?["airQuality"]?.GetValue<bool>() ?? true,
                        "precipitation" => weatherEditor.Capabilities?["precipitation"]?.GetValue<bool>() ?? false,
                        "updatedAt" => weatherEditor.Capabilities?["updatedAt"]?.GetValue<bool>() ?? true,
                        "alerts" => weatherEditor.Capabilities?["alerts"]?.GetValue<bool>() ?? true,
                        _ => true
                    };
                    check.Content = available ? label : label + "（当前来源不可用）";
                }
            }
            weatherEditor.CapabilitiesChanged = UpdateAvailability;
            UpdateAvailability();
        }
        var reduced = new CheckBox
        {
            Content = "减少天气卡动画",
            MinHeight = 32,
            IsChecked = _media.Bool("weatherCardReducedMotion")
        };
        AutomationProperties.SetAutomationId(reduced, "weather-card-reduced-motion");
        reduced.Checked += (_, _) => { _media.Set("weatherCardReducedMotion", true); Changed(); };
        reduced.Unchecked += (_, _) => { _media.Set("weatherCardReducedMotion", false); Changed(); };
        modules.Children.Add(reduced);
        modules.Children.Add(Copy("中国天气网不提供湿度、AQI 等部分字段；这些模块不会显示伪造数据。预警在卡片内独立滚动。"));
    }

    private void SetWeatherModule(string id, bool enabled)
    {
        var values = _media.Get("weatherCardModules") as JsonObject ?? new JsonObject();
        values[id] = enabled;
        foreach (var (moduleId, _) in WeatherModuleDefinitions)
            values[moduleId] ??= moduleId is "current" or "forecast" or "updatedAt";
        _media.SetNode("weatherCardModules", values);
        Changed();
    }

    private bool StartQuickCardPointerDrag(PointerDragButton source, string id, PointerRoutedEventArgs args)
    {
        if (_busy) return false;
        return _pointerReorder?.Begin(source, args, null, point => MoveQuickCardDrag(id, point),
            point => FinishQuickCardDrag(id, point)) == true;
    }

    private void MoveQuickCardDrag(string id, Windows.Foundation.Point point)
    {
        Border? target = null;
        foreach (var row in _cardOrderRows)
        {
            var origin = row.TransformToVisual(_navigation).TransformPoint(new Windows.Foundation.Point(0, 0));
            if (point.X >= origin.X && point.X <= origin.X + row.ActualWidth && point.Y >= origin.Y - 4 && point.Y <= origin.Y + row.ActualHeight + 4)
            {
                target = row;
                break;
            }
        }
        foreach (var row in _cardOrderRows)
        {
            row.BorderBrush = target == row ? new Microsoft.UI.Xaml.Media.SolidColorBrush(AccentColor()) : null;
            row.BorderThickness = new Thickness(target == row ? 2 : 1);
        }
        if (target is not null && target.Child is FrameworkElement content)
        {
            var origin = target.TransformToVisual(_navigation).TransformPoint(new Windows.Foundation.Point(0, 0));
            _pointerReorder?.ShowInsertion(new Windows.Foundation.Rect(origin.X + 4, origin.Y + 2, Math.Max(1, target.ActualWidth - 8), 3));
        }
        else _pointerReorder?.HideInsertion();
        _saveStatus.Text = target is null ? "拖到卡片行上调整顺序；Esc 取消。" : "释放后应用新的卡片顺序。";
    }

    private void FinishQuickCardDrag(string id, Windows.Foundation.Point point)
    {
        var order = QuickCardOrderValues().OfType<JsonValue>().Select(value => value.GetValue<string>()).ToList();
        var insertion = _cardOrderRows.Count;
        for (var index = 0; index < _cardOrderRows.Count; index++)
        {
            var row = _cardOrderRows[index];
            var origin = row.TransformToVisual(_navigation).TransformPoint(new Windows.Foundation.Point(0, 0));
            if (point.Y < origin.Y + row.ActualHeight / 2) { insertion = index; break; }
        }
        var next = ReorderSequence.MoveToGap(order, id, insertion);
        SetQuickCardOrder(new JsonArray(next.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()));
    }

    private static void AddCardRow(Grid row, FrameworkElement element, int column)
    {
        Grid.SetColumn(element, column);
        row.Children.Add(element);
    }
}
