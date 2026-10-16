using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Globalization.NumberFormatting;
using Windows.UI.Text;

namespace TaskBarWidgets.UI;

public sealed partial class SettingsWindow
{
    private readonly List<(string Value, string Label)> _fontOptions = [];
    private bool _fontListLoaded;
    private string? _fontListError;

    private async Task LoadFontsAsync()
    {
        if (_fontListLoaded) return;
        try {
            var result = await _connection.RequestAsync("getFonts", cancellationToken: _lifetime.Token);
            var values = JsonNode.Parse(result.GetRawText()) as JsonArray ?? throw new InvalidDataException("字体列表格式无效。");
            _fontOptions.Clear();
            foreach (var value in values) {
                var name = value is JsonObject option ? Text(option["value"]) : Text(value);
                var label = value is JsonObject labeled ? Text(labeled["label"], name) : name;
                if (name.Length is > 0 and < 32 && !_fontOptions.Any(font => string.Equals(font.Value, name, StringComparison.OrdinalIgnoreCase))) _fontOptions.Add((name, label));
            }
            _fontListLoaded = true; _fontListError = null;
        }
        catch (OperationCanceledException) when (_closed) { throw; }
        catch (Exception error) when (error is IOException or InvalidOperationException or JsonException or OperationCanceledException) {
            _fontListError = "字体列表暂不可用，仍可输入字体名称；重新读取后会重试。";
        }
    }

    private void RenderAppearanceGroup(IReadOnlyList<JsonObject> fields)
    {
        var body = Section("共用任务栏外观", "监测与媒体使用同一套字体、背景色和不透明度。");
        body.Children.Add(AppearanceTypography());
        if (_fontListError is not null) body.Children.Add(Copy(_fontListError));
        body.Children.Add(new Border { Height = 1, Background = ResourceBrush("CardStrokeColorDefaultBrush"), Margin = new Thickness(0, 6, 0, 4) });
        body.Children.Add(new TextBlock { Text = "背景", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var typography = new HashSet<string>(StringComparer.Ordinal) { "taskbar.fontName", "taskbar.fontSize", "taskbar.fontBold", "taskbar.fontItalic", "taskbar.fontUnderline", "taskbar.fontStrikeOut" };
        body.Children.Add(ResponsiveFields(fields.Where(field => !typography.Contains(Text(field["key"]))).Select(field => FieldEditor(field, _native))));
    }

    private FrameworkElement AppearanceTypography()
    {
        var family = new ComboBox { IsEditable = true, IsTextSearchEnabled = true, HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0 };
        AutomationProperties.SetName(family, "任务栏字体"); AutomationProperties.SetAutomationId(family, "setting-taskbar.fontName");
        var current = _native.Text("taskbar.fontName", "Segoe UI");
        foreach (var option in _fontOptions) family.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Value });
        var selected = family.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Tag as string, current, StringComparison.OrdinalIgnoreCase));
        if (selected is null) { selected = new ComboBoxItem { Content = current, Tag = current }; family.Items.Add(selected); }
        family.SelectedItem = selected; family.Text = selected.Content?.ToString() ?? current;
        void CommitFamily(string name)
        {
            name = name.Trim();
            var known = _fontOptions.FirstOrDefault(option => string.Equals(option.Value, name, StringComparison.OrdinalIgnoreCase) || string.Equals(option.Label, name, StringComparison.OrdinalIgnoreCase));
            var value = known.Value ?? name;
            if (value.Length is 0 or >= 32) { ShowError("请选择字体或输入不超过 31 个字符的字体名称。"); return; }
            _native.Set("taskbar.fontName", value); Changed();
        }
        family.SelectionChanged += (_, _) => { if (family.SelectedItem is ComboBoxItem item && item.Tag is string name) CommitFamily(name); };
        family.TextSubmitted += (_, args) => { CommitFamily(args.Text); args.Handled = true; };
        family.LostFocus += (_, _) => CommitFamily(family.Text);
        family.KeyUp += (_, _) => {
            if (_fontOptions.Any(option => string.Equals(option.Value, family.Text, StringComparison.OrdinalIgnoreCase) || string.Equals(option.Label, family.Text, StringComparison.OrdinalIgnoreCase))) CommitFamily(family.Text);
        };

        var sizeField = NativeFields().FirstOrDefault(field => Text(field["key"]) == "taskbar.fontSize");
        var minimum = Number(sizeField?["min"], 5); var maximum = Number(sizeField?["max"], 72);
        var size = new NumberBox { Value = _native.Number("taskbar.fontSize", 10), Minimum = minimum, Maximum = maximum, SmallChange = 1, Width = 104, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, NumberFormatter = new DecimalFormatter { FractionDigits = 0, IntegerDigits = 1 } };
        AutomationProperties.SetName(size, "任务栏字号（磅）"); AutomationProperties.SetAutomationId(size, "setting-taskbar.fontSize");
        void CommitSize(double value)
        {
            if (!double.IsFinite(value) || value < minimum || value > maximum) return;
            _native.Set("taskbar.fontSize", (int)Math.Round(value, MidpointRounding.AwayFromZero)); Changed();
        }
        size.ValueChanged += (_, args) => { CommitSize(args.NewValue); if (double.IsFinite(args.NewValue) && args.NewValue != Math.Round(args.NewValue, MidpointRounding.AwayFromZero)) size.Value = Math.Round(args.NewValue, MidpointRounding.AwayFromZero); };
        size.KeyUp += (_, _) => { if (double.TryParse(size.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var value)) CommitSize(value); };

        var styles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (key, glyph, label) in new[] { ("taskbar.fontBold", "B", "粗体"), ("taskbar.fontItalic", "I", "斜体"), ("taskbar.fontUnderline", "U", "下划线"), ("taskbar.fontStrikeOut", "S", "删除线") }) {
            var text = new TextBlock { Text = glyph, FontSize = 15, FontWeight = key.EndsWith("Bold") ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal, FontStyle = key.EndsWith("Italic") ? FontStyle.Italic : FontStyle.Normal };
            if (key.EndsWith("Underline")) text.TextDecorations = TextDecorations.Underline;
            if (key.EndsWith("StrikeOut")) text.TextDecorations = TextDecorations.Strikethrough;
            var toggle = new ToggleButton { Content = text, IsChecked = _native.Bool(key), Width = 36, Height = 32, MinWidth = 32, Padding = new Thickness(6, 2, 6, 2) };
            AutomationProperties.SetName(toggle, label); AutomationProperties.SetAutomationId(toggle, "setting-" + key); ToolTipService.SetToolTip(toggle, label);
            toggle.Checked += (_, _) => { _native.Set(key, true); Changed(); }; toggle.Unchecked += (_, _) => { _native.Set(key, false); Changed(); };
            styles.Children.Add(toggle);
        }
        static StackPanel Labeled(string label, FrameworkElement control) {
            var stack = new StackPanel { Spacing = 6 }; stack.Children.Add(new TextBlock { Text = label, FontSize = 14 }); stack.Children.Add(control); return stack;
        }
        var familyBlock = Labeled("字体", family); var sizeBlock = Labeled("字号（磅）", size); var styleBlock = Labeled("字形", styles);
        var toolbar = new Grid { ColumnSpacing = 12, RowSpacing = 12, Tag = "settings-typography" };
        toolbar.Children.Add(familyBlock); toolbar.Children.Add(sizeBlock); toolbar.Children.Add(styleBlock);
        var wide = false; var initialized = false;
        void Arrange(double width)
        {
            var next = width >= 620;
            if (initialized && next == wide) return;
            initialized = true; wide = next; toolbar.ColumnDefinitions.Clear(); toolbar.RowDefinitions.Clear();
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (wide) toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (!wide) toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(familyBlock, 0); Grid.SetColumn(familyBlock, 0); Grid.SetRow(sizeBlock, 0); Grid.SetColumn(sizeBlock, 1);
            Grid.SetRow(styleBlock, wide ? 0 : 1); Grid.SetColumn(styleBlock, wide ? 2 : 0); Grid.SetColumnSpan(styleBlock, wide ? 1 : 2);
        }
        toolbar.SizeChanged += (_, args) => Arrange(args.NewSize.Width); Arrange(0);
        return toolbar;
    }
}
