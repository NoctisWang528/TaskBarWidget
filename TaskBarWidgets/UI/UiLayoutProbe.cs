using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TaskBarWidgets.UI;

// Renders synthetic settings data inside the caller's already-offscreen HWND.
// PNGs are visual-tree captures with a solid fallback; system Mica is not captured.
internal static class UiLayoutProbe
{
    internal static async Task<List<object>> RunAsync(SettingsWindow window, string fixturePath, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var content = (FrameworkElement)window.Content;
        foreach (var panel in Walk(content).OfType<Panel>().Where(panel => Equals(panel.Tag, "settings-content"))) panel.ChildrenTransitions = null;
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(fixturePath))!.AsObject();
        window.LoadValidationData(fixture["native"]!.AsObject(), fixture["media"]!.AsObject());
        var checks = new List<object>();
        foreach (var width in new[] { 720, 1440 })
        {
            window.AppWindow.Resize(new SizeInt32(width, 1000));
            var windowPosition = window.AppWindow.Position;
            if (windowPosition.X + width >= -20000 || windowPosition.Y + 1000 >= -20000) { window.AppWindow.Hide(); throw new InvalidOperationException("Layout probe must remain strictly offscreen."); }
            window.ShowPage("taskbar");
            await SettleAsync(content);
            var pageScroll = PageScroll(content);
            pageScroll.ChangeView(null, 0, null, true);
            await SettleAsync(content);
            var preview = Walk(content).OfType<FrameworkElement>().FirstOrDefault(element => Equals(element.Tag, "TaskbarPreview"));
            if (preview is null || preview.ActualWidth <= 0 || preview.ActualHeight <= 0 || !Walk(preview).OfType<TextBlock>().Any(text => !string.IsNullOrEmpty(text.Text) || text.Inlines.OfType<Microsoft.UI.Xaml.Documents.Run>().Any(run => !string.IsNullOrEmpty(run.Text)))) throw new InvalidOperationException("Taskbar preview has no realized sample content.");
            var previewColumns = PreviewColumns(content, rightAligned: true);
            var columns = Walk(content).OfType<Grid>().Where(grid => Equals(grid.Tag, "settings-responsive-fields")).Select(grid => new { width = grid.ActualWidth, columns = grid.ColumnDefinitions.Count }).ToArray();
            if (columns.Length == 0 || columns.Any(grid => grid.width <= 0 || grid.columns != (grid.width >= 820 ? 2 : 1))) throw new InvalidOperationException("Settings field columns did not adapt to the actual content width.");
            var boxes = CheckBoxes(content);
            var top = Path.Combine(outputDirectory, $"settings-taskbar-{width}.png");
            await CaptureAsync(content, top);
            checks.Add(new { kind = "settingsTaskbarLayout", requestedPhysicalWidth = width, actualWidth = content.ActualWidth, previewWidth = preview.ActualWidth, previewHeight = preview.ActualHeight, previewColumns, columns, boxes, png = top, systemBackdropCaptured = false });

            if (width == 1440) {
                var before = Walk(content).OfType<Grid>().Single(grid => Equals(grid.Tag, "TaskbarPreviewColumns")).ActualWidth;
                var fontSize = Walk(content).OfType<NumberBox>().Single(box => AutomationProperties.GetAutomationId(box) == "setting-taskbar.fontSize");
                var spacing = Walk(content).OfType<NumberBox>().Single(box => AutomationProperties.GetAutomationId(box) == "setting-taskbar.item_space");
                var alignment = Walk(content).OfType<CheckBox>().Single(box => AutomationProperties.GetAutomationId(box) == "setting-taskbar.value_right_align");
                fontSize.Value += 2; spacing.Value += 5; alignment.IsChecked = false;
                await SettleAsync(content);
                var changed = Walk(content).OfType<Grid>().Single(grid => Equals(grid.Tag, "TaskbarPreviewColumns"));
                var editedColumns = PreviewColumns(content, rightAligned: false);
                if (changed.ActualWidth <= before || Math.Abs(changed.ColumnSpacing - spacing.Value) > .01) throw new InvalidOperationException("Font/spacing edits did not immediately resize shared preview columns.");
                checks.Add(new { kind = "previewSharedColumnEdits", widthBefore = before, widthAfter = changed.ActualWidth, fontSize = fontSize.Value, itemSpace = changed.ColumnSpacing, columns = editedColumns });
                window.LoadValidationData(fixture["native"]!.AsObject(), fixture["media"]!.AsObject());
                window.ShowPage("taskbar"); await SettleAsync(content);
                checks.Add(await UiPointerProbe.RunAsync(window));
                window.LoadValidationData(fixture["native"]!.AsObject(), fixture["media"]!.AsObject());
                window.ShowPage("taskbar"); await SettleAsync(content);
            }

            var typography = Walk(content).OfType<Grid>().First(grid => Equals(grid.Tag, "settings-typography"));
            var position = typography.TransformToVisual(pageScroll).TransformPoint(new Point(0, 0));
            pageScroll.ChangeView(null, Math.Max(0, pageScroll.VerticalOffset + position.Y - 102), null, true);
            await SettleAsync(content);
            var appearance = Path.Combine(outputDirectory, $"settings-appearance-{width}.png");
            await CaptureAsync(content, appearance);
            checks.Add(new { kind = "settingsAppearanceLayout", requestedPhysicalWidth = width, toolbarWidth = typography.ActualWidth, toolbarRows = typography.RowDefinitions.Count, png = appearance, systemBackdropCaptured = false });

            window.ShowPage("sources");
            await SettleAsync(content);
            pageScroll.ChangeView(null, 0, null, true);
            await SettleAsync(content);
            var sources = Walk(content).OfType<Border>().Where(border => Equals(border.Tag, "SourcePriorityRow")).ToList();
            if (sources.Count < 5 || sources.Any(row => row.ActualWidth <= 0 || Math.Abs(row.ActualWidth - sources[0].ActualWidth) > 1)) throw new InvalidOperationException("Player source rows are not equal-width realized list rows.");
            var sourceBoxes = CheckBoxes(content);
            var sourcePng = Path.Combine(outputDirectory, $"settings-sources-{width}.png");
            await CaptureAsync(content, sourcePng);
            checks.Add(new { kind = "settingsSourcesLayout", requestedPhysicalWidth = width, rowCount = sources.Count, rowWidth = sources[0].ActualWidth, boxes = sourceBoxes, png = sourcePng, systemBackdropCaptured = false });

            window.ShowPage("quickCards");
            await SettleAsync(content);
            pageScroll.ChangeView(null, 0, null, true);
            await SettleAsync(content);
            var cardIds = new[] { "weather", "traffic", "device", "media" };
            var cells = Walk(content).OfType<Border>().Where(border => border.Tag is string tag && cardIds.Contains(tag)).ToList();
            if (cells.Count < 2) throw new InvalidOperationException("Quick-card reorder grid did not realize its cells.");
            var cellPositions = cells.Select(cell => (Row: Grid.GetRow(cell), Column: Grid.GetColumn(cell))).ToArray();
            if (cellPositions.Distinct().Count() != cells.Count || cellPositions.Any(cell => cell.Column is not (0 or 1)))
                throw new InvalidOperationException("Quick-card reorder cells overlap or are not a two-column grid.");
            if (cells.Select(cell => Math.Round(cell.ActualHeight)).Distinct().Count() != 1)
                throw new InvalidOperationException("Quick-card reorder cells are not equal height.");
            checks.Add(new { kind = "settingsQuickCardsGrid", requestedPhysicalWidth = width, cells = cells.Count, rows = cellPositions.Select(cell => cell.Row).Distinct().Count(), cellHeight = cells[0].ActualHeight });
        }
        return checks;
    }

    private sealed record PreviewCell(string Id, int Column, int Row, int RowSpan, double X, double Width, double Height, double NaturalWidth, string ValueAlignment);
    private static PreviewCell[] PreviewColumns(FrameworkElement content, bool rightAligned)
    {
        var grid = Walk(content).OfType<Grid>().Single(item => Equals(item.Tag, "TaskbarPreviewColumns"));
        var tolerance = 1 / (content.XamlRoot?.RasterizationScale ?? 1);
        var result = new List<PreviewCell>();
        foreach (var cell in grid.Children.OfType<Grid>()) {
            if (cell.Tag is not string tag || !tag.StartsWith("preview:")) continue;
            var label = cell.Children.OfType<TextBlock>().Single(text => text.Tag is string id && id.StartsWith("preview-label:"));
            var value = cell.Children.OfType<TextBlock>().Single(text => text.Tag is string id && id.StartsWith("preview-value:"));
            var point = cell.TransformToVisual(grid).TransformPoint(new Point(0, 0));
            var span = Grid.GetRowSpan(cell);
            if (span == 1) {
                if (value.TextAlignment != (rightAligned ? TextAlignment.Right : TextAlignment.Left)) throw new InvalidOperationException("Preview ignored value alignment.");
                // WinUI TextBlock.ActualWidth reports the text ink width (32.474
                // DIP in the verified CPU case), while its assigned value slot is
                // 112 DIP. The slot and TextAlignment control the rendered edge.
                var valueSlot = Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(value);
                if (value.HorizontalAlignment != HorizontalAlignment.Stretch || !double.IsNaN(value.Width) ||
                    Math.Abs(valueSlot.X - cell.ColumnDefinitions[0].ActualWidth) > tolerance ||
                    Math.Abs(valueSlot.Right - cell.ActualWidth) > tolerance)
                    throw new InvalidOperationException($"Value area does not use its shared column's remaining width: {tag}, cellX={point.X:F3}, cellWidth={cell.ActualWidth:F3}, labelColumn={cell.ColumnDefinitions[0].ActualWidth:F3}, valueSlot={valueSlot}, textInkWidth={value.ActualWidth:F3}, valueAlignment={value.HorizontalAlignment}.");
            }
            else if (span != 2 || value.TextAlignment != TextAlignment.Center) throw new InvalidOperationException("Exclusive preview item must span both rows and center its value.");
            result.Add(new PreviewCell(tag[8..], Grid.GetColumn(cell), Grid.GetRow(cell), span, point.X, cell.ActualWidth, cell.ActualHeight,
                span == 2 ? Math.Max(label.DesiredSize.Width, value.DesiredSize.Width) : label.DesiredSize.Width + value.DesiredSize.Width, value.TextAlignment.ToString()));
        }
        var pairs = result.GroupBy(cell => cell.Column).Where(column => column.Count() == 2).ToList();
        if (!pairs.Any(column => Math.Abs(column.First().NaturalWidth - column.Last().NaturalWidth) > 1)) throw new InvalidOperationException("Preview fixture must exercise unequal sample text widths.");
        foreach (var pair in pairs) {
            var first = pair.First(); var second = pair.Last();
            if (first.RowSpan != 1 || second.RowSpan != 1 || first.Row == second.Row || Math.Abs(first.X - second.X) > tolerance || Math.Abs(first.Width - second.Width) > tolerance)
                throw new InvalidOperationException("Paired preview items do not share column X/width.");
        }
        if (!result.Any(cell => cell.RowSpan == 2) || !result.GroupBy(cell => cell.Column).Any(column => column.Count() == 1 && column.First().RowSpan == 1))
            throw new InvalidOperationException("Preview fixture must include an exclusive item and an unpaired ordinary tail.");
        return result.ToArray();
    }

    private static async Task SettleAsync(FrameworkElement content)
    {
        await Task.Delay(220);
        content.UpdateLayout();
        await Task.Delay(80);
        content.UpdateLayout();
        if (!content.IsLoaded || content.ActualWidth <= 0 || content.ActualHeight <= 0) throw new InvalidOperationException("Offscreen settings did not realize a nonzero layout.");
    }
    private static ScrollViewer PageScroll(DependencyObject root) => Walk(root).OfType<Panel>().Where(panel => Equals(panel.Tag, "settings-content")).Select(panel => Ancestor<ScrollViewer>(panel)).First(viewer => viewer is not null)!;
    private static T? Ancestor<T>(DependencyObject item) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(item); parent is not null; parent = VisualTreeHelper.GetParent(parent)) if (parent is T match) return match;
        return null;
    }
    private static object[] CheckBoxes(DependencyObject root)
    {
        var checks = new List<object>();
        foreach (var box in Walk(root).OfType<CheckBox>().Where(box => box.ActualWidth > 0 && box.ActualHeight > 0 && box.Content is string))
        {
            if (box.ActualHeight < 32 || box.Padding.Left < 6) throw new InvalidOperationException("Checkbox native spacing is compressed.");
            var glyph = Walk(box).OfType<FrameworkElement>().FirstOrDefault(element => element.Name == "NormalRectangle");
            var text = Walk(box).OfType<ContentPresenter>().FirstOrDefault(element => element.Name == "ContentPresenter");
            double? gap = null, centerDifference = null;
            if (glyph is not null && text is not null && glyph.ActualWidth > 0 && text.ActualWidth > 0)
            {
                var glyphPoint = glyph.TransformToVisual(box).TransformPoint(new Point(0, 0));
                var textPoint = text.TransformToVisual(box).TransformPoint(new Point(0, 0));
                gap = textPoint.X - glyphPoint.X - glyph.ActualWidth;
                centerDifference = Math.Abs(textPoint.Y + text.ActualHeight / 2 - glyphPoint.Y - glyph.ActualHeight / 2);
                if (gap < 6 || centerDifference > 4) throw new InvalidOperationException("Checkbox glyph and caption are not aligned with native spacing.");
            }
            checks.Add(new { label = box.Content.ToString(), id = AutomationProperties.GetAutomationId(box), width = box.ActualWidth, height = box.ActualHeight, gap, centerDifference });
        }
        if (checks.Count == 0) throw new InvalidOperationException("No realized settings checkbox was found.");
        return checks.ToArray();
    }
    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) foreach (var child in Walk(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    internal static async Task CaptureAsync(FrameworkElement content, string path)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(content);
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) throw new InvalidOperationException("RenderTargetBitmap produced an empty image.");
        var buffer = await bitmap.GetPixelsAsync();
        var pixels = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
        // Backdrop brushes live outside the XAML visual tree. Composite only the
        // missing alpha onto a neutral theme color, without pretending to render Mica.
        var backdrop = content.ActualTheme == ElementTheme.Dark ? 32 : 243;
        for (var index = 0; index < pixels.Length; index += 4) {
            var missing = 255 - pixels[index + 3];
            for (var channel = 0; channel < 3; channel++) pixels[index + channel] = (byte)Math.Min(255, pixels[index + channel] + backdrop * missing / 255);
            pixels[index + 3] = 255;
        }
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        var dpi = 96 * (content.XamlRoot?.RasterizationScale ?? 1);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, dpi, dpi, pixels);
        await encoder.FlushAsync();
        using var encoded = new DataReader(stream.GetInputStreamAt(0));
        await encoded.LoadAsync((uint)stream.Size);
        var bytes = new byte[stream.Size]; encoded.ReadBytes(bytes);
        await File.WriteAllBytesAsync(path, bytes);
    }
}
