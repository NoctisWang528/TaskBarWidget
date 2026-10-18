using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaskBarWidgets.Media.Integration;
using Windows.Graphics;

namespace TaskBarWidgets.UI;

public sealed partial class CardGroup
{
    // Runs on the already-realized offscreen card. No backend, hook, input
    // injection, activation or additional window is needed for this visual fixture.
    internal static async Task<object> ProbeTrafficCardAsync(CardGroup cards, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var today = new DateOnly(2026, 10, 16);
        const double gib = 1024d * 1024;
        var totals = Enumerable.Range(0, 80).Where(index => index % 17 != 0).ToDictionary(index => today.AddDays(-index),
            index => index % 11 == 0 ? 0d : (index % 9 + 1) * .25 * gib);
        totals[today] = 0; totals[today.AddDays(-1)] = 2 * gib; totals[today.AddDays(-2)] = 3 * gib;
        totals[today.AddDays(-7)] = .5 * gib;
        var fixture = TrafficHeatmap.Create(today, totals, "已监测网卡 · 上传 + 下载");
        var card = cards._cards[0];
        var scale = GetDpiForWindow(card.Handle) / 96d;
        card.Window.AppWindow.MoveAndResize(new RectInt32(-30000, -30000, (int)Math.Ceiling(CardWidth * scale), (int)Math.Ceiling(card.Height * scale)));
        cards.RenderTraffic(fixture); cards._trafficStatus.Text = "";
        var selected = cards._days.Single(button => ((TrafficHeatmapDay)button.Tag).Date == today.AddDays(-1));
        var invoke = (IInvokeProvider)new ButtonAutomationPeer(selected).GetPattern(PatternInterface.Invoke);
        invoke.Invoke();
        await Task.Delay(50);
        if (cards._trafficDetail.Text != fixture.Days.Single(day => day.Date == today.AddDays(-1)).Description)
            throw new InvalidOperationException("Traffic selection did not retain the clicked date and actual daily total.");
        var hovered = cards._days.Single(button => ((TrafficHeatmapDay)button.Tag).Date == today.AddDays(-7));
        cards._hoveredDays.Add(hovered);
        var images = new List<object>();
        var errors = new List<string>();
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            card.Root.RequestedTheme = theme;
            await Task.Delay(100);
            cards.RenderTraffic(fixture); card.Root.UpdateLayout();
            VisualStateManager.GoToState(hovered, "PointerOver", false);
            var path = Path.Combine(outputDirectory, "heatmap-" + theme.ToString().ToLowerInvariant() + ".png");
            await UiLayoutProbe.CaptureAsync(card.Root, path);
            var rasterScale = card.Root.XamlRoot.RasterizationScale;
            var cells = cards._days.Select(button => new { button.ActualWidth, button.ActualHeight,
                Point = button.TransformToVisual(cards._trafficContent).TransformPoint(new Windows.Foundation.Point()) }).ToArray();
            var actualSizes = cells.Select(cell => new { width = cell.ActualWidth, height = cell.ActualHeight }).Distinct().ToArray();
            var sizeDiagnostic = string.Join(", ", actualSizes.Select(size => $"{size.width:0.###}x{size.height:0.###}"));
            if (!cards._trafficContent.IsLoaded || cards._trafficContent.DesiredSize.Height <= 0 || cards._trafficContent.DesiredSize.Height > card.Height - 32 + .1)
                errors.Add($"{theme}: content {cards._trafficContent.DesiredSize.Height:0.###} DIP exceeds {card.Height - 32} available.");
            const double pixelTolerance = 1.01;
            if (cards._days.Length != 84 || cells.Any(cell => Math.Abs(cell.ActualWidth - TrafficCellSize) * rasterScale > pixelTolerance ||
                Math.Abs(cell.ActualHeight - TrafficCellSize) * rasterScale > pixelTolerance || Math.Abs(cell.ActualWidth - cell.ActualHeight) * rasterScale > pixelTolerance))
                errors.Add($"{theme}: cells {sizeDiagnostic} DIP at {rasterScale:0.###} raster scale exceed one physical pixel of square/size rounding.");
            for (var index = 0; index < cells.Length; index++)
            {
                if (index >= 7 && Math.Abs(cells[index].Point.X - cells[index - 7].Point.X - TrafficPitch) * rasterScale > pixelTolerance ||
                    index % 7 > 0 && Math.Abs(cells[index].Point.Y - cells[index - 1].Point.Y - TrafficPitch) * rasterScale > pixelTolerance)
                    errors.Add($"{theme}: inconsistent grid pitch at day {index}.");
            }
            if (cards._days.Any(button => button.IsEnabled == ((TrafficHeatmapDay)button.Tag).IsFuture))
                errors.Add($"{theme}: only future cells should be disabled.");
            if (cards._days.Any(button => !ReferenceEquals(button.Background, button.Resources["ButtonBackgroundPointerOver"]) ||
                !ReferenceEquals(button.BorderBrush, button.Resources["ButtonBorderBrushPointerOver"])))
                errors.Add($"{theme}: hovered template state retained a different cell brush.");
            if (!cards._trafficHighContrast && (cards._glows.Count == 0 || cards._glows.Values.Any(glow => glow.Visual.Brush is not null || glow.Shadow.Mask is null)))
                errors.Add($"{theme}: composition glow must use a rounded alpha mask and a shadow-only sprite.");
            if (cards._dayGlows.Where(host => host.Children.Count > 0).Any(host => host.Children.Count != 1 ||
                host.Children[0] is not Microsoft.UI.Xaml.Shapes.Rectangle shape || shape.RadiusX != 4 || shape.RadiusY != 4 ||
                shape.Width != TrafficCellSize || shape.Height != TrafficCellSize))
                errors.Add($"{theme}: glow mask must match the rounded cell dimensions.");
            if (VisualTreeHelper.GetChildrenCount(cards._trafficContent) == 0 || WalkTraffic(cards._trafficContent).Any(element => element is ScrollViewer))
                errors.Add($"{theme}: scrolling or empty visual tree.");
            images.Add(new { theme = theme.ToString(), png = path, contentHeight = cards._trafficContent.DesiredSize.Height,
                availableHeight = card.Height - 32, cellSize = TrafficCellSize, actualSizes, rasterScale, calendarWidth = 20 + 12 * TrafficPitch });
        }
        // Exercise production's high-contrast branch without changing Windows.
        cards._trafficHighContrast = true;
        for (var index = 0; index < cards._days.Length; index++) cards.UpdateTrafficCell(cards._days[index], cards._dayGlows[index]);
        if (cards._dayGlows.Any(host => host.Visibility != Visibility.Collapsed))
            errors.Add("High contrast must suppress every composition glow.");
        cards.RenderTraffic(fixture);
        cards._hoveredDays.Clear();
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "geometry.json"), System.Text.Json.JsonSerializer.Serialize(new { images, errors }));
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors) + " Images and geometry: " + outputDirectory);
        return new { images, dayCount = fixture.Days.Count, totalKiB = fixture.TotalKiB, peakKiB = fixture.PeakKiB,
            grades = fixture.Days.Select(fixture.LevelFor).Distinct().Order().ToArray(), selectedDetail = cards._trafficDetail.Text,
            syntheticHover = true, roundedCompositionMask = true, highContrastGlowSuppression = true,
            systemBackdropCaptured = false, compositionGlowCaptured = false };
    }

    private static IEnumerable<DependencyObject> WalkTraffic(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in WalkTraffic(child)) yield return descendant;
        }
    }
}
