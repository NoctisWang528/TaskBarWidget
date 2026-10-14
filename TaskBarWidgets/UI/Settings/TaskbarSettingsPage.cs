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

namespace TaskBarWidgets.UI;

public sealed partial class SettingsWindow
{
    private ModulePointerDrag? _modulePointerDrag;
    private readonly List<ModuleDropTarget> _moduleDropTargets = [];
    private sealed record ModuleDropTarget(Border Surface, int? Row, StackPanel? Chips);
    private sealed class ModulePointerDrag(string id, Button source, Pointer pointer, Windows.Foundation.Point origin)
    {
        public string Id { get; } = id;
        public Button Source { get; } = source;
        public Pointer Pointer { get; } = pointer;
        public Windows.Foundation.Point Origin { get; } = origin;
        public bool Started { get; set; }
        public ModuleDropTarget? Target { get; set; }
    }
    private void InitializeModuleGestures()
    {
        _navigation.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, args) => MoveModulePointerDrag(args)), true);
        _navigation.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, args) => FinishModulePointerDrag(args)), true);
        _navigation.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, _) => CancelModulePointerDrag()), true);
        _navigation.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => CancelModulePointerDrag()), true);
        _navigation.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) => {
            if (args.Key == VirtualKey.Escape && _modulePointerDrag is not null) { CancelModulePointerDrag(); args.Handled = true; }
        }), true);
        Activated += (_, args) => { if (args.WindowActivationState == WindowActivationState.Deactivated) CancelModulePointerDrag(); };
    }
    private void RenderTaskbar()
    {
        _moduleDropTargets.Clear();
        var preview = Section("任务栏预览", "监测与媒体共用字体、背景和透明度。预览数值为示例，组合行与实际任务栏一致。");
        _preview = new StackPanel { Spacing = 2, Padding = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Left };
        preview.Children.Add(new ScrollViewer { Content = _preview, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        RenderPreview();
        var combinations = Section("显示组合", "从模块池拖入组合行即可启用；拖动调整先后或移到另一行。单击模块打开右侧属性。");
        if (NativeFields().FirstOrDefault(field => Text(field["key"]) == "taskbar.horizontal_arrange") is { } mode)
        {
            var descriptor = (JsonObject)mode.DeepClone();
            descriptor["label"] = "单行组合（关闭后使用上下独立两行）";
            combinations.Children.Add(FieldEditor(descriptor, _native));
        }
        if (_layout is not null) {
            for (var row = 0; row < (_layout.SingleRow ? 1 : 2); row++) {
                var index = row;
                combinations.Children.Add(Copy(_layout.SingleRow ? "单行组合" : row == 0 ? "上行组合" : "下行组合"));
                var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, MinHeight = 38 };
                foreach (var module in _layout.InRow(row)) chips.Children.Add(ModuleChip(module));
                var drop = new Border { Child = new ScrollViewer { Content = chips, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled }, MinHeight = 52, Padding = new Thickness(8), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), BorderBrush = ResourceBrush("ControlStrokeColorDefaultBrush") };
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
    private void StartModulePointerDrag(Button source, string id, PointerRoutedEventArgs args)
    {
        if (_busy || _modulePointerDrag is not null || !args.GetCurrentPoint(_navigation).Properties.IsLeftButtonPressed) return;
        var drag = new ModulePointerDrag(id, source, args.Pointer, args.GetCurrentPoint(_navigation).Position);
        // Capture the root, so Button's automatic capture release cannot swallow the drop.
        if (_navigation.CapturePointer(args.Pointer)) _modulePointerDrag = drag;
    }
    private void MoveModulePointerDrag(PointerRoutedEventArgs args)
    {
        var drag = _modulePointerDrag;
        if (drag is null || drag.Pointer.PointerId != args.Pointer.PointerId) return;
        if (_busy) { CancelModulePointerDrag(); return; }
        var point = args.GetCurrentPoint(_navigation).Position;
        if (!drag.Started && Math.Pow(point.X - drag.Origin.X, 2) + Math.Pow(point.Y - drag.Origin.Y, 2) < 36) return;
        if (!drag.Started) _split.IsPaneOpen = false;
        drag.Started = true; drag.Source.Opacity = .45;
        drag.Target = FindModuleDropTarget(point);
        foreach (var target in _moduleDropTargets) {
            var active = ReferenceEquals(target, drag.Target);
            target.Surface.BorderBrush = active ? new SolidColorBrush(AccentColor()) : ResourceBrush("ControlStrokeColorDefaultBrush");
            target.Surface.BorderThickness = new Thickness(active ? 2 : 1);
        }
        _saveStatus.Text = drag.Target is null ? "移动到组合行或模块池后释放；Esc 取消。" : drag.Target.Row is null ? "释放后移回模块池；配置会保留。" : $"释放后加入第 {drag.Target.Row + 1} 行；Esc 取消。";
        args.Handled = true;
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
    private void FinishModulePointerDrag(PointerRoutedEventArgs args)
    {
        var drag = _modulePointerDrag;
        if (drag is null || drag.Pointer.PointerId != args.Pointer.PointerId) return;
        var point = args.GetCurrentPoint(_navigation).Position;
        var target = drag.Started && !_busy ? FindModuleDropTarget(point) : null;
        CancelModulePointerDrag();
        if (!drag.Started) { if (!_busy && ContainsPoint(drag.Source, point)) OpenModule(drag.Id); args.Handled = true; return; }
        if (target is null || _layout is null) return;
        args.Handled = true;
        if (target.Row is null) _layout.Remove(drag.Id);
        else {
            var insertion = 0;
            foreach (var chip in target.Chips!.Children.OfType<Button>()) {
                var origin = chip.TransformToVisual(_navigation).TransformPoint(new Windows.Foundation.Point(0, 0));
                if (point.X > origin.X + chip.ActualWidth / 2) insertion++;
            }
            var source = _layout.InRow(target.Row.Value).FindIndex(module => module.Id == drag.Id);
            if (source >= 0 && source < insertion) insertion--;
            _layout.Move(drag.Id, target.Row.Value, insertion);
        }
        Changed(); RenderPage(); FocusModule(drag.Id);
    }
    private void CancelModulePointerDrag()
    {
        var drag = _modulePointerDrag; _modulePointerDrag = null;
        if (drag is not null) { drag.Source.Opacity = 1; _navigation.ReleasePointerCapture(drag.Pointer); }
        foreach (var target in _moduleDropTargets) { target.Surface.BorderBrush = ResourceBrush("ControlStrokeColorDefaultBrush"); target.Surface.BorderThickness = new Thickness(1); }
        UpdateSaveStatus();
    }
    private static Color AccentColor() => Application.Current.Resources.TryGetValue("SystemAccentColor", out var accent) && accent is Color color ? color : Color.FromArgb(255, 0, 120, 212);
    private Button ModuleChip(Settings.DisplayModule module)
    {
        var button = new Button { Content = module.Name, Height = 32, MinHeight = 32, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(2), Tag = module.Id };
        AutomationProperties.SetName(button, module.Name + "，单击编辑属性；Alt 加方向键移动，Delete 移除，Enter 打开属性");
        ToolTipService.SetToolTip(button, "拖动加入组合或排序；单击编辑属性");
        button.Click += (_, _) => { if (_modulePointerDrag is null) OpenModule(module.Id); };
        // Elevated WinUI processes cannot safely use OLE/DataPackage drag/drop. Keep the gesture inside this HWND.
        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) => StartModulePointerDrag(button, module.Id, args)), true);
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
        background.A = _native.Bool("taskbar.transparent") ? (byte)0 : (byte)Math.Clamp(_native.Number("taskbar.backgroundOpacity", 100) * 2.55, 0, 255);
        _preview.Background = new SolidColorBrush(background);
        for (var row = 0; row < (_layout.SingleRow ? 1 : 2); row++) {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = _native.Number("taskbar.item_space", 8), MinHeight = 20 };
            foreach (var module in _layout.InRow(row)) {
                var text = new TextBlock { FontFamily = new FontFamily(_native.Text("taskbar.fontName", "Segoe UI")), FontSize = _native.Number("taskbar.fontSize", 10) * 96 / 72, FontWeight = _native.Bool("taskbar.fontBold") ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal };
                var labelColor = ParseColor(_native.Text(_layout.Key(module.Id, "labelColor"), "#ffffff"));
                var valueColor = ParseColor(_native.Text(_layout.Key(module.Id, "valueColor"), "#ffffff"));
                if (!_native.Bool("taskbar.specify_each_item_color")) labelColor = valueColor = ParseColor(_native.Text(_layout.Key(_layout.Modules[0].Id, "labelColor"), "#ffffff"));
                text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = _native.Text(_layout.Key(module.Id, "label")), Foreground = new SolidColorBrush(labelColor) });
                text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = module.Sample, Foreground = new SolidColorBrush(valueColor) });
                line.Children.Add(text);
            }
            if (line.Children.Count == 0) line.Children.Add(Copy("空行"));
            _preview.Children.Add(line);
        }
    }
    private static Color ParseColor(string value)
    {
        var hex = value.TrimStart('#');
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var color)) return Colors.White;
        return Color.FromArgb(hex.Length == 8 ? (byte)(color >> 24) : (byte)255, (byte)(color >> 16), (byte)(color >> 8), (byte)color);
    }
}
