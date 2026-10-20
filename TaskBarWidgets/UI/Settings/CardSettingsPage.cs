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
        ("media", "媒体", "\uE8D6")
    ];
    private readonly List<(Border Cell, int Column, int Slot)> _cardOrderCells = [];
    private int _cardOrderDragColumn = -1;
    private int _cardOrderDragSlot = -1;
    private string? _cardOrderDragSourceId;
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

        var material = Section("卡片材质", "调整快捷卡片 Mica 的色彩浓度和通透度。滑块会实时同步设置窗口和已打开的卡片，点击“应用更改”后保存。");
        AutomationProperties.SetAutomationId(material, "section-cardMica");
        var canPreview = CanPreviewCardMica;
        if (canPreview) ActivateCardMicaPreview(DraftCardMicaTuning);
        var initialMica = DraftCardMicaTuning;
        var tint = new Slider
        {
            Minimum = CardMicaTuning.MinimumTintOpacity * 100,
            Maximum = CardMicaTuning.MaximumTintOpacity * 100,
            StepFrequency = 1,
            SnapsTo = SliderSnapsTo.StepValues,
            IsThumbToolTipEnabled = true,
            IsEnabled = canPreview,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var transparency = new Slider
        {
            Minimum = (1 - CardMicaTuning.MaximumLuminosityOpacity) * 100,
            Maximum = (1 - CardMicaTuning.MinimumLuminosityOpacity) * 100,
            StepFrequency = 1,
            SnapsTo = SliderSnapsTo.StepValues,
            IsThumbToolTipEnabled = true,
            IsEnabled = canPreview,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(tint, "卡片 Mica 色彩浓度");
        AutomationProperties.SetAutomationId(tint, "setting-cardMicaTintOpacity");
        AutomationProperties.SetName(transparency, "卡片 Mica 通透度");
        AutomationProperties.SetAutomationId(transparency, "setting-cardMicaTransparency");

        var suppressMicaEvents = false;
        CardMicaTuning SliderMica() => CardMicaTuning.FromUserValues(tint.Value, transparency.Value);
        void UpdateMicaLabels()
        {
            tint.Header = $"色彩浓度 · {tint.Value:0}%";
            transparency.Header = $"通透度 · {transparency.Value:0}%";
        }
        void CommitMica(CardMicaTuning tuning)
        {
            _media.Set("cardMicaTintOpacity", tuning.TintOpacity);
            _media.Set("cardMicaLuminosityOpacity", tuning.LuminosityOpacity);
            Changed();
            PreviewCardMica(tuning);
        }

        suppressMicaEvents = true;
        tint.Value = Math.Round(initialMica.TintOpacity * 100);
        transparency.Value = Math.Round(initialMica.Transparency * 100);
        suppressMicaEvents = false;
        UpdateMicaLabels();
        tint.ValueChanged += (_, _) =>
        {
            if (suppressMicaEvents) return;
            UpdateMicaLabels();
            var tuning = SliderMica();
            CommitMica(tuning);
        };
        transparency.ValueChanged += (_, _) =>
        {
            if (suppressMicaEvents) return;
            UpdateMicaLabels();
            var tuning = SliderMica();
            CommitMica(tuning);
        };
        material.Children.Add(tint);
        material.Children.Add(transparency);
        if (!canPreview)
        {
            material.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Informational,
                IsOpen = true,
                IsClosable = false,
                Title = _accessibility.HighContrast ? "高对比度模式已启用" : "当前系统不支持 Mica",
                Message = "材质调节暂不可用；卡片会继续使用系统回退色，文字和控件保持可读。"
            });
        }
        material.Children.Add(Copy("设置窗口背景即为实时预览；离开本页会恢复已保存值。"));

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
        _cardOrderDragColumn = -1;
        _cardOrderDragSlot = -1;
        _cardOrderDragSourceId = id;
        return _pointerReorder?.Begin(source, args, null, point => MoveQuickCardDrag(id, point),
            point => FinishQuickCardDrag(id, point), surface,
            PointerReorder.CreateDragGhost(
                QuickCardDefinitions.First(card => card.Id == id).Glyph,
                QuickCardDefinitions.First(card => card.Id == id).Label,
                "快捷卡片")) == true;
    }

    private void MoveQuickCardDrag(string id, Windows.Foundation.Point point)
    {
        Border? target = null;
        var targetColumn = -1;
        var targetSlot = -1;
        var lowerHalf = false;
        foreach (var (cell, column, slot) in _cardOrderCells)
        {
            var origin = cell.TransformToVisual(_navigation).TransformPoint(new Windows.Foundation.Point(0, 0));
            if (point.X >= origin.X && point.X <= origin.X + cell.ActualWidth && point.Y >= origin.Y - 4 && point.Y <= origin.Y + cell.ActualHeight + 4)
            {
                target = cell;
                targetColumn = column;
                targetSlot = slot;
                lowerHalf = point.Y > origin.Y + cell.ActualHeight / 2;
                break;
            }
        }
        foreach (var (cell, _, _) in _cardOrderCells)
        {
            cell.BorderBrush = cell == target ? new Microsoft.UI.Xaml.Media.SolidColorBrush(AccentColor()) : null;
            cell.BorderThickness = new Thickness(1);
        }
        _pointerReorder?.HideInsertion();
        var targetGap = -1;
        if (_cardOrderDragSourceId is { } sourceId)
        {
            var (left, right) = QuickCardLayout();
            var sourceColumn = QuickCardColumnValues().GetValueOrDefault(sourceId);
            var sourceIds = sourceColumn == 0 ? left : right;
            var targetIds = targetColumn == 0 ? left : targetColumn == 1 ? right : [];
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
            var sourceIndex = sourceIds.IndexOf(sourceId);
            if (sourceIndex >= 0 && targetColumn >= 0)
            {
                targetGap = CardTargetGap(targetSlot, targetIds.Count, lowerHalf);
                var extent = sourceRows[sourceIndex].ActualHeight + 10;
                if (sourceColumn == targetColumn)
                    _pointerReorder?.ShowRowPreview(sourceRows, sourceIndex, targetGap, extent);
                else
                    _pointerReorder?.ShowCrossLanePreview(sourceRows, sourceIndex, targetRows, targetGap, extent);
                if (targetSlot >= targetIds.Count && target is not null)
                {
                    var origin = target.TransformToVisual(_navigation).TransformPoint(new Windows.Foundation.Point(0, 0));
                    _pointerReorder?.ShowDropZone(new Windows.Foundation.Rect(origin.X, origin.Y, target.ActualWidth, target.ActualHeight));
                }
                else if (targetRows.Length > 0)
                {
                    if (targetGap < targetRows.Length) _pointerReorder?.ShowVerticalGap(targetRows[targetGap], false);
                    else _pointerReorder?.ShowVerticalGap(targetRows[^1], true);
                }
            }
        }
        _cardOrderDragColumn = targetColumn;
        _cardOrderDragSlot = targetGap;
        _saveStatus.Text = target is null ? "拖到左/右任意格子上调整布局；Esc 取消。" : "释放到高亮格子以应用新的卡片布局。";
    }

    private void FinishQuickCardDrag(string id, Windows.Foundation.Point point)
    {
        if (_cardOrderDragColumn is not (0 or 1) || _cardOrderDragSlot < 0) return;
        var (left, right) = QuickCardLayout();
        var moved = MoveQuickCardToGap(left, right, id, _cardOrderDragColumn, _cardOrderDragSlot);
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
        _cardOrderDragColumn = targetColumn;
        _cardOrderDragSlot = targetGap;
        FinishQuickCardDrag(id, new Windows.Foundation.Point());
        return PageRenderCount - before;
    }

    private static void AddCardRow(Grid row, FrameworkElement element, int column)
    {
        Grid.SetColumn(element, column);
        row.Children.Add(element);
    }
}
