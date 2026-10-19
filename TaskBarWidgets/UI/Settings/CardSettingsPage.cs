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

    private readonly List<(Border Cell, int Column, int Slot)> _cardOrderCells = [];
    private int _cardOrderDragColumn = -1;
    private int _cardOrderDragSlot = -1;
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
            if (!result.ContainsKey(order[index])) result[order[index]] = index % 2;
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
                var surface = BuildQuickCardCell(columnIds[slot], column);
                _cardOrderCells.Add((surface, column, slot));
                panel.Children.Add(surface);
            }
            var empty = BuildQuickCardEmptyCell();
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
        status.Children.Add(Copy("天气卡的启停开关位于“快捷卡片 → 卡片与顺序”的天气行。此处配置来源、城市和连接。"));

        RenderWeatherSettings();
    }

    private Border BuildQuickCardCell(string id, int column)
    {
        var definition = QuickCardDefinitions.First(card => card.Id == id);
        var row = new Grid { ColumnSpacing = 8, MinHeight = 44 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var handle = new PointerDragButton
        {
            Content = new FontIcon { Glyph = "\uE700", FontSize = 16 },
            DragLabel = definition.Label,
            Width = 24,
            Height = 28,
            MinWidth = 0,
            Padding = new Thickness(2),
            Tag = id
        };
        AutomationProperties.SetName(handle, "拖动" + definition.Label + "调整顺序");
        handle.StartPointerDrag = (source, args) => StartQuickCardPointerDrag(source, id, args);
        AddCardRow(row, handle, 0);

        var icon = new FontIcon { Glyph = definition.Glyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        AddCardRow(row, icon, 1);
        var label = new TextBlock { Text = definition.Label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
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
            var direction = args.Key switch { Windows.System.VirtualKey.Up => -1, Windows.System.VirtualKey.Down => 1, _ => 0 };
            if (direction == 0) return;
            MoveQuickCardWithinColumn(id, column, direction);
            args.Handled = true;
        };
        AddCardRow(row, check, 3);

        return UiThemeStyles.Apply(new Border
        {
            Child = row,
            MinHeight = 64,
            Padding = new Thickness(10, 4, 12, 4),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Tag = id
        }, "SettingsSourceSurface");
    }

    private Border BuildQuickCardEmptyCell() => UiThemeStyles.Apply(new Border
    {
        MinHeight = 64,
        CornerRadius = new CornerRadius(6),
        BorderThickness = new Thickness(1),
        Opacity = .35,
        IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Tag = "quick-card-empty"
    }, "SettingsSourceSurface");

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

    private bool StartQuickCardPointerDrag(PointerDragButton source, string id, PointerRoutedEventArgs args)
    {
        if (_busy) return false;
        _cardOrderDragColumn = -1;
        _cardOrderDragSlot = -1;
        return _pointerReorder?.Begin(source, args, null, point => MoveQuickCardDrag(id, point),
            point => FinishQuickCardDrag(id, point)) == true;
    }

    private void MoveQuickCardDrag(string id, Windows.Foundation.Point point)
    {
        Border? target = null;
        var targetColumn = -1;
        var targetSlot = -1;
        foreach (var (cell, column, slot) in _cardOrderCells)
        {
            var origin = cell.TransformToVisual(_navigation).TransformPoint(new Windows.Foundation.Point(0, 0));
            if (point.X >= origin.X && point.X <= origin.X + cell.ActualWidth && point.Y >= origin.Y - 4 && point.Y <= origin.Y + cell.ActualHeight + 4)
            {
                target = cell;
                targetColumn = column;
                targetSlot = slot;
                break;
            }
        }
        foreach (var (cell, _, _) in _cardOrderCells)
        {
            cell.BorderBrush = cell == target ? new Microsoft.UI.Xaml.Media.SolidColorBrush(AccentColor()) : null;
            cell.BorderThickness = new Thickness(cell == target ? 2 : 1);
        }
        // Each column is its own drop zone; the highlighted cell is the target.
        _pointerReorder?.HideInsertion();
        _cardOrderDragColumn = targetColumn;
        _cardOrderDragSlot = targetSlot;
        _saveStatus.Text = target is null ? "拖到左/右任意格子上调整布局；Esc 取消。" : "释放到高亮格子以应用新的卡片布局。";
    }

    private void FinishQuickCardDrag(string id, Windows.Foundation.Point point)
    {
        if (_cardOrderDragColumn is not (0 or 1)) return;
        var (left, right) = QuickCardLayout();
        var columns = QuickCardColumnValues();
        var source = columns.GetValueOrDefault(id) == 0 ? left : right;
        var sourceIndex = source.IndexOf(id);
        if (sourceIndex < 0) return;
        source.RemoveAt(sourceIndex);
        var target = _cardOrderDragColumn == 0 ? left : right;
        // The slot index was measured before removal, so only a move down inside
        // the same column shifts by one.
        var slot = _cardOrderDragSlot - (ReferenceEquals(source, target) && sourceIndex < _cardOrderDragSlot ? 1 : 0);
        target.Insert(Math.Clamp(slot, 0, target.Count), id);
        SetQuickCardLayout(left, right);
    }

    private static void AddCardRow(Grid row, FrameworkElement element, int column)
    {
        Grid.SetColumn(element, column);
        row.Children.Add(element);
    }
}
