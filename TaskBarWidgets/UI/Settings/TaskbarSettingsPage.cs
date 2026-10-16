using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Text.Json.Nodes;
using Windows.System;
using Windows.UI;
using TaskBarWidgets.UI.Settings;

namespace TaskBarWidgets.UI;

public sealed partial class SettingsWindow
{
    private PointerReorder? _pointerReorder;
    private readonly Canvas _dragOverlay = new() { IsHitTestVisible = false };
    private Border? _previewFrame;
    private readonly List<ModuleDropTarget> _moduleDropTargets = [];
    private sealed record ModuleDropTarget(Border Surface, int? Row, StackPanel? Chips);
    private void InitializeModuleGestures()
    {
        var host = new Grid();
        Content = host; host.Children.Add(_navigation); host.Children.Add(_dragOverlay);
        _pointerReorder = new PointerReorder(_navigation, _dragOverlay, ResetReorderFeedback);
        Activated += (_, args) => { if (args.WindowActivationState == WindowActivationState.Deactivated) _pointerReorder.Cancel("deactivated"); };
    }
    private void RenderTaskbar()
    {
        _moduleDropTargets.Clear();
        var preview = Section("任务栏预览", "预览数值为示例，组合行与实际任务栏一致；透明背景使用对比底色示意。");
        _preview = new StackPanel { Tag = "TaskbarPreview", Spacing = 2, Padding = new Thickness(12), MinWidth = 48, MinHeight = 48, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        var previewScroll = new ScrollViewer { Content = _preview, MinHeight = 64, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled };
        _previewFrame = new Border { Tag = "TaskbarPreviewFrame", Child = previewScroll, MinHeight = 68, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), BorderBrush = ResourceBrush("ControlStrokeColorDefaultBrush"), Background = new SolidColorBrush(PreviewBackdrop()) };
        preview.Children.Add(_previewFrame);
        RenderPreview();
        var combinations = Section("显示组合", "从模块池拖入组合行即可启用；拖动调整先后或移到另一行。单击模块打开右侧属性。");
        if (NativeFields().FirstOrDefault(field => Text(field["key"]) == "taskbar.horizontal_arrange") is { } mode)
        {
            var descriptor = (JsonObject)mode.DeepClone();
            descriptor["label"] = "单行组合（关闭后上下两行按列对齐）";
            combinations.Children.Add(FieldEditor(descriptor, _native));
        }
        if (_layout is not null) {
            for (var row = 0; row < (_layout.SingleRow ? 1 : 2); row++) {
                var index = row;
                combinations.Children.Add(Copy(_layout.SingleRow ? "单行组合" : row == 0 ? "上行组合" : "下行组合"));
                var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, MinHeight = 38 };
                foreach (var module in _layout.InRow(row)) chips.Children.Add(ModuleChip(module));
                var drop = new Border { Tag = "ModuleLane:" + row, Child = new ScrollViewer { Content = chips, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled }, MinHeight = 52, Padding = new Thickness(8), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), BorderBrush = ResourceBrush("ControlStrokeColorDefaultBrush") };
                if (chips.Children.Count == 0) chips.Children.Add(Copy("将模块拖到此处，或在模块属性中点击添加。"));
                _moduleDropTargets.Add(new ModuleDropTarget(drop, index, chips));
                combinations.Children.Add(drop);
            }
        }
        var pool = Section("可用模块", "拖入组合行启用。移除的模块返回这里，标签与颜色继续保留。");
        var available = new GridView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (_layout is not null) foreach (var module in _layout.Ordered().Where(module => !_layout.Enabled(module.Id))) available.Items.Add(ModuleChip(module));
        if (available.Items.Count == 0) pool.Children.Add(Copy("所有模块均已加入组合。"));
        var poolSurface = new Border { Child = available, MinHeight = 52, Padding = new Thickness(4), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), BorderBrush = ResourceBrush("ControlStrokeColorDefaultBrush") };
        _moduleDropTargets.Add(new ModuleDropTarget(poolSurface, null, null));
        pool.Children.Add(poolSurface);
        RenderNativeGroups("taskbar");
    }
    private bool StartModulePointerDrag(PointerDragButton source, string id, PointerRoutedEventArgs args)
    {
        if (_busy) return false;
        return _pointerReorder?.Begin(source, args, () => OpenModule(id), MoveModulePointerDrag, point => FinishModulePointerDrag(id, point)) == true;
    }
    private void MoveModulePointerDrag(Windows.Foundation.Point point)
    {
        if (_busy) { CancelModulePointerDrag(); return; }
        _split.IsPaneOpen = false;
        AutoScrollDrag(point);
        var destination = FindModuleDropTarget(point);
        _pointerReorder?.HideInsertion();
        foreach (var target in _moduleDropTargets) {
            var active = ReferenceEquals(target, destination);
            target.Surface.BorderBrush = active ? new SolidColorBrush(AccentColor()) : ResourceBrush("ControlStrokeColorDefaultBrush");
            target.Surface.BorderThickness = new Thickness(active ? 2 : 1);
        }
        if (destination?.Row is not null) {
            var origin = destination.Surface.TransformToVisual(_navigation).TransformPoint(new Windows.Foundation.Point(0, 0));
            var chips = destination.Chips!.Children.OfType<Button>().ToArray();
            var next = chips.FirstOrDefault(chip => point.X <= chip.TransformToVisual(_navigation).TransformPoint(new Windows.Foundation.Point(0, 0)).X + chip.ActualWidth / 2);
            var x = next is not null ? next.TransformToVisual(_navigation).TransformPoint(new Windows.Foundation.Point(0, 0)).X - 3
                : chips.LastOrDefault() is { } last ? last.TransformToVisual(_navigation).TransformPoint(new Windows.Foundation.Point(0, 0)).X + last.ActualWidth + 3 : origin.X + 10;
            _pointerReorder?.ShowInsertion(new Windows.Foundation.Rect(Math.Clamp(x, origin.X + 6, origin.X + Math.Max(6, destination.Surface.ActualWidth - 8)), origin.Y + 10, 3, Math.Max(16, destination.Surface.ActualHeight - 20)));
        }
        _saveStatus.Text = destination is null ? "移动到组合行或模块池后释放；Esc 取消。" : destination.Row is null ? "释放后移回模块池；配置会保留。" : $"释放后加入第 {destination.Row + 1} 行；Esc 取消。";
    }
    private bool ContainsPoint(FrameworkElement element, Windows.Foundation.Point point)
    {
        var origin = element.TransformToVisual(_navigation).TransformPoint(new Windows.Foundation.Point(0, 0));
        return element.ActualWidth > 0 && element.ActualHeight > 0 && point.X >= origin.X && point.X <= origin.X + element.ActualWidth && point.Y >= origin.Y && point.Y <= origin.Y + element.ActualHeight;
    }
    private ModuleDropTarget? FindModuleDropTarget(Windows.Foundation.Point point)
    {
        if (_pageScroll is null || !ContainsPoint(_pageScroll, point)) return null;
        if (_split.IsPaneOpen && _split.Pane is FrameworkElement pane && ContainsPoint(pane, point)) return null;
        return _moduleDropTargets.FirstOrDefault(target => ContainsPoint(target.Surface, point));
    }
    private void FinishModulePointerDrag(string id, Windows.Foundation.Point point)
    {
        if (_busy || _layout is null || FindModuleDropTarget(point) is not { } target) { _pointerReorder?.Record("module-drop-rejected", "busy-or-no-target"); return; }
        if (target.Row is null) { _layout.Remove(id); _pointerReorder?.Record("module-applied", "target=pool"); }
        else {
            var insertion = 0;
            foreach (var chip in target.Chips!.Children.OfType<Button>()) {
                var origin = chip.TransformToVisual(_navigation).TransformPoint(new Windows.Foundation.Point(0, 0));
                if (point.X > origin.X + chip.ActualWidth / 2) insertion++;
            }
            var source = _layout.InRow(target.Row.Value).FindIndex(module => module.Id == id);
            if (source >= 0 && source < insertion) insertion--;
            _layout.Move(id, target.Row.Value, insertion);
            _pointerReorder?.Record("module-applied", $"target=row{target.Row.Value} index={insertion}");
        }
        Changed(); RenderPage(); FocusModule(id);
    }
    private void CancelModulePointerDrag() => _pointerReorder?.Cancel();
    private void AutoScrollDrag(Windows.Foundation.Point point)
    {
        if (_pageScroll is null || !ContainsPoint(_pageScroll, point)) return;
        var pageOrigin = _pageScroll.TransformToVisual(_navigation).TransformPoint(new Windows.Foundation.Point(0, 0));
        var vertical = point.Y < pageOrigin.Y + 36 ? -14 : point.Y > pageOrigin.Y + _pageScroll.ActualHeight - 36 ? 14 : 0;
        if (vertical != 0) _pageScroll.ChangeView(null, Math.Clamp(_pageScroll.VerticalOffset + vertical, 0, _pageScroll.ScrollableHeight), null, true);
        if (_currentPage != "taskbar") return;
        foreach (var target in _moduleDropTargets.Where(target => target.Row is not null && ContainsPoint(target.Surface, point))) {
            if (target.Surface.Child is not ScrollViewer viewer) continue;
            var origin = viewer.TransformToVisual(_navigation).TransformPoint(new Windows.Foundation.Point(0, 0));
            var horizontal = point.X < origin.X + 28 ? -14 : point.X > origin.X + viewer.ActualWidth - 28 ? 14 : 0;
            if (horizontal != 0) viewer.ChangeView(Math.Clamp(viewer.HorizontalOffset + horizontal, 0, viewer.ScrollableWidth), null, null, true);
        }
    }
    internal string[] PointerGestureEntries => _pointerReorder?.Entries ?? [];
    internal JsonObject PointerProbeLayout => new(_native.Keys.Where(key => key.StartsWith("items.") && (key.EndsWith(".show") || key.EndsWith(".row") || key.EndsWith(".order"))).Select(key => new KeyValuePair<string, JsonNode?>(key, _native.Get(key))));
    internal void CancelPointerProbe() => _pointerReorder?.Cancel("probe-cleanup");
    private void ResetReorderFeedback()
    {
        foreach (var target in _moduleDropTargets) { target.Surface.BorderBrush = ResourceBrush("ControlStrokeColorDefaultBrush"); target.Surface.BorderThickness = new Thickness(1); }
        ResetSourceReorderFeedback();
        UpdateSaveStatus();
    }
    private static Color AccentColor() => Application.Current.Resources.TryGetValue("SystemAccentColor", out var accent) && accent is Color color ? color : Color.FromArgb(255, 0, 120, 212);
    private Button ModuleChip(Settings.DisplayModule module)
    {
        var button = new PointerDragButton { Content = module.Name, DragLabel = module.Name, Height = 32, MinHeight = 32, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(2), Tag = module.Id };
        AutomationProperties.SetName(button, module.Name + "，单击编辑属性；Alt 加方向键移动，Delete 移除，Enter 打开属性");
        ToolTipService.SetToolTip(button, "拖动加入组合或排序；单击编辑属性");
        button.Click += (_, _) => OpenModule(module.Id);
        // Elevated WinUI processes cannot safely use OLE/DataPackage drag/drop. Keep the gesture inside this HWND.
        button.StartPointerDrag = (source, args) => StartModulePointerDrag(source, module.Id, args);
        button.KeyDown += (_, args) => {
            if (_layout is null) return;
            var alt = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            if (args.Key == VirtualKey.Delete && _layout.Enabled(module.Id)) { _layout.Remove(module.Id); Changed(); RenderPage(); args.Handled = true; }
            else if (alt && args.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down) {
                var row = _layout.Row(module.Id); var items = _layout.InRow(row); var position = items.FindIndex(item => item.Id == module.Id);
                if (args.Key is VirtualKey.Up or VirtualKey.Down) row = _layout.SingleRow ? 0 : args.Key == VirtualKey.Up ? 0 : 1;
                else position += args.Key == VirtualKey.Left ? -1 : 1;
                _layout.Move(module.Id, row, Math.Max(0, position)); Changed(); RenderPage(); FocusModule(module.Id); args.Handled = true;
            }
        };
        return button;
    }
    private void FocusModule(string id)
    {
        Button? Find(DependencyObject parent) {
            if (parent is Button button && button.Tag is string key && key == id) return button;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++) { var found = Find(VisualTreeHelper.GetChild(parent, index)); if (found is not null) return found; }
            return null;
        }
        DispatcherQueue.TryEnqueue(() => Find(_page)?.Focus(FocusState.Keyboard));
    }
    private void OpenModule(string id)
    {
        if (_layout is null) return;
        var module = _layout.Modules.First(item => item.Id == id);
        _properties.Children.Clear();
        var close = new Button { Content = "关闭属性", HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => { _split.IsPaneOpen = false; FocusModule(id); }; _properties.Children.Add(close);
        _properties.Children.Add(new TextBlock { Text = module.Name, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        _properties.Children.Add(Copy("修改只作用于这个模块；点击主页面的应用更改保存。"));
        var rowChoice = new ComboBox { Header = "组合行", HorizontalAlignment = HorizontalAlignment.Stretch };
        rowChoice.Items.Add("上行"); if (!_layout.SingleRow) rowChoice.Items.Add("下行"); rowChoice.SelectedIndex = _layout.Row(id);
        rowChoice.SelectionChanged += (_, _) => { if (_layout.Enabled(id)) { _layout.Move(id, rowChoice.SelectedIndex, _layout.InRow(rowChoice.SelectedIndex).Count); Changed(); RenderPage(); OpenModule(id); } };
        _properties.Children.Add(rowChoice);
        var add = new Button { Content = _layout.Enabled(id) ? "移回模块池" : "添加到组合", HorizontalAlignment = HorizontalAlignment.Stretch };
        add.Click += (_, _) => { if (_layout.Enabled(id)) _layout.Remove(id); else _layout.Move(id, Math.Max(0, rowChoice.SelectedIndex), _layout.InRow(Math.Max(0, rowChoice.SelectedIndex)).Count); Changed(); RenderPage(); OpenModule(id); };
        _properties.Children.Add(add);
        if (_layout.Enabled(id)) {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var (label, delta) in new[] { ("前移", -1), ("后移", 1) }) {
                var move = new Button { Content = label }; move.Click += (_, _) => { var row = _layout.Row(id); var index = _layout.InRow(row).FindIndex(item => item.Id == id); _layout.Move(id, row, index + delta); Changed(); RenderPage(); OpenModule(id); }; actions.Children.Add(move);
            }
            _properties.Children.Add(actions);
        }
        foreach (var field in NativeFields().Where(field => Text(field["key"]).StartsWith($"items.{id}.") && new[] { "label", "labelColor", "valueColor" }.Contains(Text(field["key"]).Split('.').Last()))) {
            var descriptor = (JsonObject)field.DeepClone();
            descriptor["label"] = Text(field["key"]).Split('.').Last() switch { "label" => "标签文本", "labelColor" => "标签颜色", _ => "数值颜色" };
            _properties.Children.Add(FieldEditor(descriptor, _native));
        }
        _split.IsPaneOpen = true;
    }
    private void RenderPreview()
    {
        if (_preview is null || _layout is null) return;
        _preview.Children.Clear();
        var background = ParseColor(_native.Text("taskbar.back_color", "#202020"));
        background.A = _native.Bool("taskbar.transparent") ? (byte)0 : (byte)Math.Clamp(Math.Round(_native.Number("taskbar.backgroundOpacity", 100) * 255 / 100), 0, 255);
        _preview.Background = new SolidColorBrush(background);
        if (_previewFrame is not null) _previewFrame.Background = new SolidColorBrush(PreviewBackdrop());
        var gap = Math.Max(0, _native.Number("taskbar.item_space", 8));
        var columns = new Grid { Tag = "TaskbarPreviewColumns", ColumnSpacing = gap, Padding = new Thickness(gap, 0, gap, 0), HorizontalAlignment = HorizontalAlignment.Left };
        columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MinHeight = 20 });
        if (!_layout.SingleRow) columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MinHeight = 20 });
        var decorations = Windows.UI.Text.TextDecorations.None;
        if (_native.Bool("taskbar.fontUnderline")) decorations |= Windows.UI.Text.TextDecorations.Underline;
        if (_native.Bool("taskbar.fontStrikeOut")) decorations |= Windows.UI.Text.TextDecorations.Strikethrough;
        TextBlock Text(string value, string tag, Color color) => new() {
            Tag = tag, Text = value, Foreground = new SolidColorBrush(color), IsTextScaleFactorEnabled = false,
            FontFamily = new FontFamily(_native.Text("taskbar.fontName", "Segoe UI")), FontSize = _native.Number("taskbar.fontSize", 10) * 96 / 72,
            FontWeight = _native.Bool("taskbar.fontBold") ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
            FontStyle = _native.Bool("taskbar.fontItalic") ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            TextDecorations = decorations, VerticalAlignment = VerticalAlignment.Center
        };
        void AddCell(DisplayModule module, int column, int row, bool fullHeight)
        {
            var labelColor = ParseColor(_native.Text(_layout.Key(module.Id, "labelColor"), "#ffffff"));
            var valueColor = ParseColor(_native.Text(_layout.Key(module.Id, "valueColor"), "#ffffff"));
            if (!_native.Bool("taskbar.specify_each_item_color")) labelColor = valueColor = ParseColor(_native.Text(_layout.Key(_layout.Modules[0].Id, "labelColor"), "#ffffff"));
            var label = Text(_native.Text(_layout.Key(module.Id, "label")), "preview-label:" + module.Id, labelColor);
            var value = Text(module.Sample, "preview-value:" + module.Id, valueColor);
            var cell = new Grid { Tag = "preview:" + module.Id, HorizontalAlignment = HorizontalAlignment.Stretch };
            if (fullHeight) {
                // Native exclusive items center label/value in the upper/lower
                // halves; their natural column width is max(label, value).
                cell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                cell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                label.TextAlignment = value.TextAlignment = TextAlignment.Center;
                if (label.Text.Length == 0) { label.Visibility = Visibility.Collapsed; Grid.SetRowSpan(value, 2); }
                else Grid.SetRow(value, 1);
                Grid.SetRowSpan(cell, 2);
            }
            else {
                // Auto columns are measured once across both row children. The
                // value then fills the remaining width of this shared column.
                cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(value, 1);
                value.TextAlignment = _native.Bool("taskbar.value_right_align") ? TextAlignment.Right : TextAlignment.Left;
                if (!_layout.SingleRow) {
                    var margin = _native.Number("taskbar.vertical_margin");
                    var upper = -Math.Truncate(margin / 2);
                    cell.RenderTransform = new TranslateTransform { Y = row == 0 ? upper : upper + margin };
                }
            }
            cell.Children.Add(label); cell.Children.Add(value);
            Grid.SetColumn(cell, column); Grid.SetRow(cell, row); columns.Children.Add(cell);
        }
        var layoutColumns = _layout.Columns();
        for (var column = 0; column < layoutColumns.Count; column++) {
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var items = layoutColumns[column];
            if (items.FullHeight is { } exclusive) AddCell(exclusive, column, 0, true);
            else {
                if (items.Upper is { } upper) AddCell(upper, column, 0, false);
                if (items.Lower is { } lower) AddCell(lower, column, 1, false);
            }
        }
        if (layoutColumns.Count == 0) columns.Children.Add(new TextBlock { Text = "未启用显示模块", Foreground = new SolidColorBrush(PreviewBackdrop().R > 128 ? Colors.Black : Colors.White) });
        _preview.Children.Add(columns);
    }
    private Color PreviewBackdrop()
    {
        // Transparent taskbar content needs a representative surface; otherwise
        // white labels disappear into the light settings page.
        var module = _layout?.Ordered().FirstOrDefault(item => _layout.Enabled(item.Id));
        var key = module is null ? "" : _native.Bool("taskbar.specify_each_item_color")
            ? _layout!.Key(module.Id, "valueColor") : _layout!.Key(_layout.Modules[0].Id, "labelColor");
        var foreground = ParseColor(_native.Text(key, "#ffffff"));
        return foreground.R * .299 + foreground.G * .587 + foreground.B * .114 > 128
            ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 240, 240, 240);
    }
    private static Color ParseColor(string value)
    {
        var hex = value.TrimStart('#');
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var color)) return Colors.White;
        return Color.FromArgb(hex.Length == 8 ? (byte)(color >> 24) : (byte)255, (byte)(color >> 16), (byte)(color >> 8), (byte)color);
    }
}
