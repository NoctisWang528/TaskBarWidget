using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TaskBarWidgets.Media.Integration;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace TaskBarWidgets.UI;

public sealed partial class CardGroup
{
    private const double TrafficPitch = 25;
    private const double TrafficCellSize = 21;
    private readonly TextBlock _trafficTotal = Text("—", 22), _trafficScope = Text("", 9),
        _trafficDetail = Text("", 10), _trafficStatus = Text("", 9);
    private readonly Grid _trafficContent = new() { RowSpacing = 4 };
    private readonly Grid _months = new();
    private readonly Button[] _days = new Button[84];
    private readonly Grid[] _dayGlows = new Grid[84];
    private readonly HashSet<Button> _hoveredDays = [];
    private readonly Dictionary<Button, (SpriteVisual Visual, DropShadow Shadow)> _glows = [];
    private readonly Border[] _legend = new Border[5];
    private readonly StackPanel _trafficLegend = new() { Orientation = Orientation.Horizontal, Spacing = 3 };
    private readonly TextBlock _trafficPeakLegend = Text("峰值", 9);
    private TrafficHeatmap _traffic = TrafficHeatmap.Create(DateOnly.FromDateTime(DateTime.Today));
    private DateOnly? _selectedDate;
    private bool _trafficHighContrast;

    private FrameworkElement BuildTraffic()
    {
        for (var row = 0; row < 3; row++) _trafficContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = Columns(new GridLength(1, GridUnitType.Star), GridLength.Auto);
        var heading = new StackPanel();
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var title = Text("流量消耗", 13); title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        var period = Text("近 12 周", 10); period.Opacity = 0.55; period.VerticalAlignment = VerticalAlignment.Center;
        titleRow.Children.Add(title); titleRow.Children.Add(period);
        heading.Children.Add(titleRow);
        var subtitle = new Grid();
        _trafficScope.Opacity = _trafficStatus.Opacity = 0.6;
        subtitle.Children.Add(_trafficScope); subtitle.Children.Add(_trafficStatus);
        heading.Children.Add(subtitle);
        _trafficStatus.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) =>
        {
            var hasStatus = !string.IsNullOrWhiteSpace(_trafficStatus.Text);
            _trafficStatus.Visibility = hasStatus ? Visibility.Visible : Visibility.Collapsed;
            _trafficScope.Visibility = hasStatus ? Visibility.Collapsed : Visibility.Visible;
        });
        _trafficStatus.Visibility = Visibility.Collapsed;
        _trafficTotal.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _trafficTotal.VerticalAlignment = VerticalAlignment.Center;
        Add(header, heading, 0); Add(header, _trafficTotal, 1);
        _trafficContent.Children.Add(header);

        var calendar = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        _months.Height = 12;
        _months.Margin = new Thickness(20, 0, 0, 3);
        for (var week = 0; week < 12; week++) _months.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TrafficPitch) });
        calendar.Children.Add(_months);
        var grid = new Grid { Width = 20 + 12 * TrafficPitch };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        for (var week = 0; week < 12; week++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TrafficPitch) });
        for (var day = 0; day < 7; day++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TrafficPitch) });
        for (var weekday = 0; weekday < 7; weekday += 2)
        {
            var label = Text(new[] { "一", "三", "五", "日" }[weekday / 2], 9);
            label.Opacity = 0.5; label.VerticalAlignment = VerticalAlignment.Center;
            Add(grid, label, 0, weekday);
        }
        for (var index = 0; index < _days.Length; index++)
        {
            var slot = new Grid { Width = TrafficPitch, Height = TrafficPitch };
            var glow = _dayGlows[index] = new Grid { Width = TrafficCellSize, Height = TrafficCellSize, IsHitTestVisible = false };
            var button = _days[index] = new Button { Width = TrafficCellSize, Height = TrafficCellSize, MinWidth = 0, MinHeight = 0,
                Padding = new Thickness(0), CornerRadius = new CornerRadius(4), UseSystemFocusVisuals = false,
                Background = new SolidColorBrush(), BorderBrush = new SolidColorBrush() };
            // Keep the same brushes in all template states. Updating their colors
            // also updates an already-active hover/pressed state and glow mask.
            button.Resources["ButtonBackgroundPointerOver"] = button.Background;
            button.Resources["ButtonBackgroundPressed"] = button.Background;
            button.Resources["ButtonBackgroundDisabled"] = button.Background;
            button.Resources["ButtonBorderBrushPointerOver"] = button.BorderBrush;
            button.Resources["ButtonBorderBrushPressed"] = button.BorderBrush;
            button.Resources["ButtonBorderBrushDisabled"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            slot.Children.Add(glow); slot.Children.Add(button);
            button.Click += (_, _) =>
            {
                if (button.Tag is not TrafficHeatmapDay day) return;
                _selectedDate = day.Date; RenderTraffic(_traffic);
            };
            button.Loaded += (_, _) => UpdateGlow(button, glow);
            button.GotFocus += (_, _) => UpdateTrafficCell(button, glow);
            button.LostFocus += (_, _) => UpdateTrafficCell(button, glow);
            button.PointerEntered += (_, _) => { _hoveredDays.Add(button); UpdateTrafficCell(button, glow); };
            button.PointerExited += (_, _) => { _hoveredDays.Remove(button); UpdateTrafficCell(button, glow); };
            button.KeyDown += OnDayKeyDown;
            Add(grid, slot, index / 7 + 1, index % 7);
        }
        calendar.Children.Add(grid);
        Add(_trafficContent, calendar, 0, 1);
        var footer = Columns(new GridLength(1, GridUnitType.Star), GridLength.Auto);
        _trafficDetail.Opacity = 0.75; _trafficDetail.VerticalAlignment = VerticalAlignment.Center;
        Add(footer, _trafficDetail, 0);
        var zero = Text("0", 9); zero.Opacity = 0.55;
        _trafficLegend.Children.Add(zero);
        for (var index = 0; index < _legend.Length; index++)
        {
            _legend[index] = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center };
            _trafficLegend.Children.Add(_legend[index]);
        }
        _trafficPeakLegend.Opacity = 0.55;
        _trafficLegend.Children.Add(_trafficPeakLegend);
        Add(footer, _trafficLegend, 1);
        Add(_trafficContent, footer, 0, 2);
        return _trafficContent;
    }

    private void RenderTraffic(TrafficHeatmap traffic)
    {
        if (_disposed) return;
        _traffic = traffic;
        _trafficHighContrast = new AccessibilitySettings().HighContrast;
        if (_selectedDate is null || !traffic.Days.Any(day => day.Date == _selectedDate && !day.IsFuture)) _selectedDate = traffic.Today;
        _trafficTotal.Text = TrafficHeatmap.FormatUsage(traffic.TotalKiB);
        _trafficScope.Text = $"上传 + 下载 · {traffic.Days.Count(day => day.TotalKiB.HasValue)} 天有记录";
        ToolTipService.SetToolTip(_trafficScope, traffic.ScopeLabel);
        _trafficDetail.Text = traffic.Days.First(day => day.Date == _selectedDate).Description;
        _trafficPeakLegend.Text = "峰值 " + TrafficHeatmap.FormatUsage(traffic.PeakKiB);
        ToolTipService.SetToolTip(_trafficLegend, $"相对近 12 周日流量峰值分级 · 峰值 {TrafficHeatmap.FormatUsage(traffic.PeakKiB)}\n空框：无记录 · 灰格：已记录 0\n仅统计监测期间的上传 + 下载");
        for (var index = 0; index < _legend.Length; index++) _legend[index].Background = new SolidColorBrush(ColorForLevel(index + 1));
        _months.Children.Clear();
        for (var week = 0; week < 12; week++)
        {
            var day = traffic.Days[week * 7];
            if (week > 0 && day.Date.Month == traffic.Days[(week - 1) * 7].Date.Month) continue;
            var label = Text(day.Date.Month + "月", 9); label.Opacity = 0.55; label.Margin = new Thickness(2, 0, 0, 0);
            Add(_months, label, week);
        }
        for (var index = 0; index < _days.Length; index++)
        {
            var button = _days[index]; var day = traffic.Days[index];
            button.Tag = day; button.IsEnabled = !day.IsFuture;
            button.UseSystemFocusVisuals = _trafficHighContrast;
            AutomationProperties.SetName(button, day.Description);
            var relative = day.TotalKiB > 0 && traffic.PeakKiB > 0 ? $" · 峰值的 {day.TotalKiB / traffic.PeakKiB:P0}" : "";
            ToolTipService.SetToolTip(button, day.Description + relative);
            UpdateTrafficCell(button, _dayGlows[index]);
        }
    }

    private void UpdateTrafficCell(Button button, Grid glow)
    {
        if (_disposed || button.Tag is not TrafficHeatmapDay day) return;
        var level = _traffic.LevelFor(day);
        var emphasized = day.Date == _selectedDate || _hoveredDays.Contains(button) || button.FocusState == FocusState.Keyboard;
        var accent = IsDark ? Color.FromArgb(255, 141, 216, 255) : Color.FromArgb(255, 37, 104, 177);
        if (_trafficHighContrast) accent = new UISettings().UIElementColor(UIElementType.Highlight);
        var fill = level < 0 ? Microsoft.UI.Colors.Transparent : ColorForLevel(level);
        ((SolidColorBrush)button.Background).Color = fill;
        button.BorderThickness = new Thickness(level >= 0 && (emphasized || level == 0) ? 1 : 0);
        ((SolidColorBrush)button.BorderBrush).Color = emphasized ? accent : _trafficHighContrast ? new UISettings().UIElementColor(UIElementType.WindowText) :
            IsDark ? Color.FromArgb(95, 151, 176, 201) : Color.FromArgb(65, 95, 119, 145);
        if (button.IsLoaded) UpdateGlow(button, glow);
    }

    private bool IsDark => _cards.Length > 0 && _cards[0].Root.ActualTheme == ElementTheme.Dark;

    private Color ColorForLevel(int level)
    {
        if (_trafficHighContrast)
        {
            if (level == 0) return Microsoft.UI.Colors.Transparent;
            var settings = new UISettings();
            var background = settings.UIElementColor(UIElementType.Window);
            var ink = settings.UIElementColor(level == 1 ? UIElementType.WindowText : UIElementType.Highlight);
            var amount = level == 1 ? .18 : (level - 1) / 4d;
            return Color.FromArgb(255, (byte)(background.R + (ink.R - background.R) * amount),
                (byte)(background.G + (ink.G - background.G) * amount), (byte)(background.B + (ink.B - background.B) * amount));
        }
        var light = new[] { 0x00FFFFFFu, 0xFFE4E9EFu, 0xFFD3E6FBu, 0xFF96C3EFu, 0xFF4E97DCu, 0xFF2365AAu };
        var dark = new[] { 0x00202B37u, 0xFF343E4Bu, 0xFF284764u, 0xFF326B9Cu, 0xFF55A1D5u, 0xFFB4E6FFu };
        var value = (IsDark ? dark : light)[level];
        return Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    private void UpdateGlow(Button button, Grid host)
    {
        if (_disposed || button.Tag is not TrafficHeatmapDay day) return;
        var level = _traffic.LevelFor(day);
        var emphasized = day.Date == _selectedDate || _hoveredDays.Contains(button) || button.FocusState == FocusState.Keyboard;
        if (level < 2 || _trafficHighContrast) { host.Visibility = Visibility.Collapsed; return; }
        host.Visibility = Visibility.Visible;
        if (!_glows.TryGetValue(button, out var glow))
        {
            var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
            // A shadow-only sprite uses the actual rounded cell's alpha mask.
            // A color-brush sprite casts a solid rectangle beneath translucent
            // template states and visibly fills the four rounded corners.
            var shape = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = TrafficCellSize, Height = TrafficCellSize,
                RadiusX = 4, RadiusY = 4, Fill = button.Background };
            host.Children.Add(shape);
            var sprite = compositor.CreateSpriteVisual();
            sprite.Size = new Vector2((float)TrafficCellSize, (float)TrafficCellSize);
            var shadow = compositor.CreateDropShadow(); shadow.Mask = shape.GetAlphaMask(); shadow.Offset = Vector3.Zero; sprite.Shadow = shadow;
            ElementCompositionPreview.SetElementChildVisual(host, sprite);
            glow = (sprite, shadow); _glows.Add(button, glow);
        }
        var color = IsDark ? Color.FromArgb(255, 105, 189, 250) : Color.FromArgb(255, 52, 129, 211);
        glow.Shadow.Color = color;
        glow.Shadow.BlurRadius = emphasized ? 8 : 4;
        glow.Shadow.Opacity = emphasized ? 0.6f : IsDark ? 0.2f : 0.1f;
    }

    private void OnDayKeyDown(object sender, KeyRoutedEventArgs args)
    {
        var index = System.Array.IndexOf(_days, (Button)sender);
        if (args.Key is not (VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)) return;
        args.Handled = true;
        var next = args.Key switch { VirtualKey.Left => index - 7, VirtualKey.Right => index + 7,
            VirtualKey.Up when index % 7 > 0 => index - 1, VirtualKey.Down when index % 7 < 6 => index + 1, _ => -1 };
        if (next >= 0 && next < _days.Length && _days[next].IsEnabled) _days[next].Focus(FocusState.Keyboard);
    }
}
