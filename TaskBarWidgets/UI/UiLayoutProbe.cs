using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using TaskBarWidgets.UI.Settings;

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
        var titleBar = AssertWinUiTitleBar(window, content);
        var advancedKeys = (fixture["native"]?["fields"] as JsonArray)?.OfType<JsonObject>()
            .Where(field => field["advanced"]?.ToString() == "true")
            .Select(field => field["key"]?.ToString() ?? "")
            .Where(key => key.Length > 0)
            .ToArray() ?? [];
        var spectrumKeys = (fixture["media"]?["spectrumTuning"] as JsonObject)?.Select(pair => pair.Key).ToArray() ?? [];
        var checks = new List<object>();
        foreach (var width in new[] { 720, 1440, 1920, 2560 })
        {
            window.AppWindow.Resize(new SizeInt32(width, 1000));
            var windowPosition = window.AppWindow.Position;
            if (windowPosition.X + window.AppWindow.Size.Width >= -20000 || windowPosition.Y + window.AppWindow.Size.Height >= -20000) { window.AppWindow.Hide(); throw new InvalidOperationException("Layout probe must remain strictly offscreen."); }
            window.ShowPage("taskbar");
            await SettleAsync(content);
            var pageScroll = PageScroll(content);
            pageScroll.ChangeView(null, 0, null, true);
            await SettleAsync(content);
            var viewport = AssertSettingsViewport(window, content, pageScroll, width);
            AssertAdvancedFieldsDirect(content, advancedKeys);
            var preview = Walk(content).OfType<FrameworkElement>().FirstOrDefault(element => Equals(element.Tag, "TaskbarPreview"));
            if (preview is null || preview.ActualWidth <= 0 || preview.ActualHeight <= 0 || !Walk(preview).OfType<TextBlock>().Any(text => !string.IsNullOrEmpty(text.Text) || text.Inlines.OfType<Microsoft.UI.Xaml.Documents.Run>().Any(run => !string.IsNullOrEmpty(run.Text)))) throw new InvalidOperationException("Taskbar preview has no realized sample content.");
            var previewColumns = PreviewColumns(content, rightAligned: true);
            var columns = Walk(content).OfType<Grid>().Where(grid => Equals(grid.Tag, "settings-responsive-fields")).Select(grid => new { width = grid.ActualWidth, columns = grid.ColumnDefinitions.Count }).ToArray();
            if (columns.Length == 0 || columns.Any(grid => grid.width <= 0 || grid.columns != (grid.width >= 820 ? 2 : 1))) throw new InvalidOperationException("Settings field columns did not adapt to the actual content width.");
            var boxes = Toggles(content);
            var top = Path.Combine(outputDirectory, $"settings-taskbar-{width}.png");
            await CaptureAsync(content, top);
            checks.Add(new { kind = "settingsTaskbarLayout", requestedPhysicalWidth = width, actualWidth = content.ActualWidth, titleBar, viewport, advancedFields = advancedKeys.Length, previewWidth = preview.ActualWidth, previewHeight = preview.ActualHeight, previewColumns, columns, boxes, png = top, systemBackdropCaptured = false });

            if (width == 1920) {
                var drawer = await AssertOverlayDrawerAsync(window, content, pageScroll);
                var before = Walk(content).OfType<Grid>().Single(grid => Equals(grid.Tag, "TaskbarPreviewColumns")).ActualWidth;
                var fontSize = Walk(content).OfType<NumberBox>().Single(box => AutomationProperties.GetAutomationId(box) == "setting-taskbar.fontSize");
                var spacing = Walk(content).OfType<NumberBox>().Single(box => AutomationProperties.GetAutomationId(box) == "setting-taskbar.item_space");
                var alignment = Walk(content).OfType<ToggleSwitch>().Single(toggle => AutomationProperties.GetAutomationId(toggle) == "setting-taskbar.value_right_align");
                fontSize.Value += 2; spacing.Value += 5; alignment.IsOn = false;
                await SettleAsync(content);
                var changed = Walk(content).OfType<Grid>().Single(grid => Equals(grid.Tag, "TaskbarPreviewColumns"));
                var editedColumns = PreviewColumns(content, rightAligned: false);
                if (changed.ActualWidth <= before || Math.Abs(changed.ColumnSpacing - spacing.Value) > .01) throw new InvalidOperationException("Font/spacing edits did not immediately resize shared preview columns.");
                checks.Add(new { kind = "previewSharedColumnEdits", widthBefore = before, widthAfter = changed.ActualWidth, fontSize = fontSize.Value, itemSpace = changed.ColumnSpacing, columns = editedColumns, drawer });
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

            window.ShowPage("lyrics");
            await SettleAsync(content);
            pageScroll.ChangeView(null, 0, null, true);
            await SettleAsync(content);
            var spectrumCount = AssertSpectrumFieldsDirect(content, spectrumKeys);
            checks.Add(new { kind = "settingsSpectrumAdvancedFields", requestedPhysicalWidth = width, fieldCount = spectrumCount });

            window.ShowPage("sources");
            await SettleAsync(content);
            pageScroll.ChangeView(null, 0, null, true);
            await SettleAsync(content);
            var sources = Walk(content).OfType<Border>().Where(border => Equals(border.Tag, "SourcePriorityRow")).ToList();
            if (sources.Count < 5 || sources.Any(row => row.ActualWidth <= 0 || Math.Abs(row.ActualWidth - sources[0].ActualWidth) > 1)) throw new InvalidOperationException("Player source rows are not equal-width realized list rows.");
            var sourceBoxes = Toggles(content);
            if (sources.SelectMany(source => Walk(source).OfType<ToggleSwitch>()).Any(toggle =>
                    !AutomationProperties.GetAutomationId(toggle).StartsWith("source-enabled-", StringComparison.Ordinal)))
                throw new InvalidOperationException("Every player-source switch must expose a stable source-enabled AutomationId.");
            var sourceMetrics = AssertReorderRows(sources);
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
            var heights = cells.Select(cell => (id: cell.Tag, height: Math.Round(cell.ActualHeight, 2))).ToArray();
            if (heights.Select(cell => cell.height).Distinct().Count() != 1)
                throw new InvalidOperationException("Quick-card reorder cells are not equal height: " + string.Join(", ", cells.Select(DescribeCardCell)) + ".");
            // Each column is an independent drop zone, so group the cells by the
            // X they ended up at instead of assuming a balanced two-row table.
            var cardColumns = cells
                .GroupBy(cell => Math.Round(cell.TransformToVisual(content).TransformPoint(new Point(0, 0)).X))
                .OrderBy(group => group.Key)
                .ToArray();
            if (cardColumns.Length > 2) throw new InvalidOperationException("Quick-card reorder cells exceed the two available columns.");
            if (cardColumns.Any(column => column.Select(cell => Math.Round(cell.ActualWidth)).Distinct().Count() != 1))
                throw new InvalidOperationException("Quick-card reorder cells are not equal width.");
            var cardRows = cells.Select(cell => Walk(cell).OfType<ToggleSwitch>().FirstOrDefault()).ToArray();
            if (cardRows.Any(toggle => toggle is null) || cells.Any(cell => Walk(cell).OfType<Button>().All(button => button.Flyout is null)))
                throw new InvalidOperationException("Quick-card rows must expose a standard toggle and a menu action.");
            var rowStyle = AssertUnifiedReorderRows(cells, sourceMetrics);
            checks.Add(new { kind = "settingsQuickCardsGrid", requestedPhysicalWidth = width, cells = cells.Count, columns = cardColumns.Length, cellHeight = cells[0].ActualHeight, rowStyle });
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
    private static object AssertSettingsViewport(SettingsWindow window, FrameworkElement content, ScrollViewer scroll, int requestedPhysicalWidth)
    {
        var pageHost = Walk(content).OfType<Grid>().Single(grid => Equals(grid.Tag, "settings-content-host"));
        var chrome = Walk(content).OfType<Grid>().Single(grid => Equals(grid.Tag, "settings-page-chrome"));
        var viewportWidth = scroll.ViewportWidth > 0 ? scroll.ViewportWidth : scroll.ActualWidth;
        var expectedWidth = Math.Min(1088, viewportWidth);
        var tolerance = 2 / (content.XamlRoot?.RasterizationScale ?? 1);
        if (viewportWidth <= 0 || pageHost.ActualWidth <= 0 || chrome.ActualWidth <= 0 ||
            Math.Abs(pageHost.ActualWidth - expectedWidth) > tolerance ||
            Math.Abs(chrome.ActualWidth - expectedWidth) > tolerance)
            throw new InvalidOperationException("Settings content did not fill its centered 1088-DIP column.");
        var hostOrigin = pageHost.TransformToVisual(scroll).TransformPoint(new Point(0, 0));
        var chromeOrigin = chrome.TransformToVisual(scroll).TransformPoint(new Point(0, 0));
        if (Math.Abs(hostOrigin.X + pageHost.ActualWidth / 2 - viewportWidth / 2) > tolerance + 1 ||
            Math.Abs(chromeOrigin.X + chrome.ActualWidth / 2 - viewportWidth / 2) > tolerance + 1)
            throw new InvalidOperationException("Settings heading and body are not horizontally centered in the scroll viewport.");
        var bar = VerticalBar(scroll);
        var barOrigin = bar?.TransformToVisual(scroll).TransformPoint(new Point(0, 0));
        var barRight = barOrigin is null ? double.NaN : barOrigin.Value.X + (bar?.ActualWidth ?? 0);
        if (bar is null || bar.ActualHeight <= 0 || Math.Abs(barRight - scroll.ActualWidth) > 4)
            throw new InvalidOperationException("The native settings scrollbar is not anchored to the right edge of the content viewport.");
        if (Math.Abs(hostOrigin.X - chromeOrigin.X) > tolerance + 1)
            throw new InvalidOperationException("Settings heading and body do not share the same centered column.");
        if (window.PageRenderCount <= 0) throw new InvalidOperationException("Settings page rendering was not tracked.");
        return new
        {
            requestedPhysicalWidth,
            viewportWidth,
            contentColumnWidth = pageHost.ActualWidth,
            chromeWidth = chrome.ActualWidth,
            scrollbarX = barOrigin!.Value.X,
            scrollbarWidth = bar.ActualWidth,
            scrollbarAtRight = true
        };
    }
    private static object AssertWinUiTitleBar(SettingsWindow window, FrameworkElement content)
    {
        var titleBar = Walk(content).OfType<Grid>().Single(grid => Equals(grid.Tag, "settings-title-bar"));
        var navigation = Walk(content).OfType<NavigationView>().Single();
        var titleBarOrigin = titleBar.TransformToVisual(content).TransformPoint(new Point(0, 0));
        var navigationOrigin = navigation.TransformToVisual(content).TransformPoint(new Point(0, 0));
        if (!window.UsesWinUiTitleBar || !window.ExtendsContentIntoTitleBar || !content.IsLoaded)
            throw new InvalidOperationException("Settings did not enable the WinUI title bar.");
        if (titleBar.ActualHeight < 44 || Math.Abs(titleBar.ActualWidth - content.ActualWidth) > 2 ||
            navigationOrigin.Y < titleBarOrigin.Y + titleBar.ActualHeight - 1)
            throw new InvalidOperationException("The WinUI title bar is not realized above the full-width navigation content.");
        return new
        {
            custom = true,
            height = titleBar.ActualHeight,
            contentWidth = titleBar.ActualWidth,
            navigationTop = navigationOrigin.Y,
            icon = Walk(titleBar).OfType<Image>().Single().Source is not null,
            title = Walk(titleBar).OfType<TextBlock>().Any(text => text.Text.Contains("设置", StringComparison.Ordinal))
        };
    }
    private static ScrollBar? VerticalBar(ScrollViewer scroll) => Walk(scroll).OfType<ScrollBar>()
        .FirstOrDefault(bar => bar.Orientation == Orientation.Vertical && bar.Visibility == Visibility.Visible &&
            bar.ActualHeight > 0 && bar.ActualWidth > 0);
    private static void AssertAdvancedFieldsDirect(FrameworkElement content, string[] advancedKeys)
    {
        if (advancedKeys.Length == 0) throw new InvalidOperationException("The settings fixture must exercise at least one advanced native field.");
        if (Walk(content).OfType<Expander>().Any(expander => HeaderText(expander).Contains("高级选项", StringComparison.Ordinal)))
            throw new InvalidOperationException("Native advanced fields are still hidden inside an advanced-options Expander.");
        foreach (var key in advancedKeys)
        {
            var field = Walk(content).OfType<FrameworkElement>().OfType<Control>()
                .SingleOrDefault(control => AutomationProperties.GetAutomationId(control) == "setting-" + key);
            if (field is null || field.ActualWidth <= 0 || field.ActualHeight <= 0)
                throw new InvalidOperationException($"Advanced setting is not directly realized: {key}.");
            if (Ancestor<Expander>(field) is not null)
                throw new InvalidOperationException($"Advanced setting remained folded: {key}.");
        }
    }
    private static int AssertSpectrumFieldsDirect(FrameworkElement content, string[] spectrumKeys)
    {
        if (spectrumKeys.Length == 0) throw new InvalidOperationException("The settings fixture must include spectrum tuning fields.");
        if (Walk(content).OfType<Expander>().Any(expander => HeaderText(expander).Contains("频谱高级调节", StringComparison.Ordinal)))
            throw new InvalidOperationException("Spectrum tuning fields are still hidden inside an Expander.");
        var realized = 0;
        foreach (var key in spectrumKeys)
        {
            var field = Walk(content).OfType<NumberBox>()
                .SingleOrDefault(control => AutomationProperties.GetAutomationId(control) == "setting-" + key);
            if (field is null || field.ActualWidth <= 0 || field.ActualHeight <= 0 || Ancestor<Expander>(field) is not null)
                throw new InvalidOperationException($"Spectrum field is not directly realized: {key}.");
            realized++;
        }
        return realized;
    }
    private static async Task<object> AssertOverlayDrawerAsync(SettingsWindow window, FrameworkElement content, ScrollViewer scroll)
    {
        var split = Walk(content).OfType<SplitView>().Single(item => Equals(item.Tag, "settings-split"));
        var host = Walk(content).OfType<Grid>().Single(grid => Equals(grid.Tag, "settings-content-host"));
        var widthBefore = host.ActualWidth;
        window.OpenModuleProperties("native_0");
        await SettleAsync(content);
        var pane = split.Pane as FrameworkElement;
        if (!split.IsPaneOpen || pane is null || pane.ActualWidth <= 0 || pane.ActualHeight <= 0)
            throw new InvalidOperationException("Opening a module did not realize the overlay property drawer.");
        var paneOrigin = pane.TransformToVisual(scroll).TransformPoint(new Point(0, 0));
        var bar = VerticalBar(scroll);
        var barOrigin = bar?.TransformToVisual(scroll).TransformPoint(new Point(0, 0));
        if (barOrigin is not null && paneOrigin.X > barOrigin.Value.X + 1)
            throw new InvalidOperationException("The property drawer does not cover the right-edge scrollbar.");
        if (Math.Abs(host.ActualWidth - widthBefore) > 1)
            throw new InvalidOperationException("Opening the overlay property drawer shifted the main content width.");
        window.ShowPage("taskbar");
        await SettleAsync(content);
        return new
        {
            overlay = true,
            paneWidth = pane.ActualWidth,
            paneX = paneOrigin.X,
            contentWidthBefore = widthBefore,
            contentWidthAfter = host.ActualWidth,
            scrollbarCovered = true
        };
    }
    private sealed record ReorderRowMetrics(Thickness Padding, double ColumnSpacing, double MinHeight, string Handle, string Menu);
    private static ReorderRowMetrics AssertReorderRows(IReadOnlyList<Border> rows)
    {
        if (rows.Count == 0) throw new InvalidOperationException("The reorder list has no realized rows.");
        var metrics = rows.Select(AssertReorderRow).ToArray();
        if (metrics.Any(item => item.Padding != metrics[0].Padding || Math.Abs(item.ColumnSpacing - metrics[0].ColumnSpacing) > .01 ||
            Math.Abs(item.MinHeight - metrics[0].MinHeight) > .01 || item.Handle != metrics[0].Handle || item.Menu != metrics[0].Menu))
            throw new InvalidOperationException("Reorder rows do not share consistent row metrics and compact controls.");
        return metrics[0];
    }
    private static ReorderRowMetrics AssertReorderRow(Border surface)
    {
        var row = Walk(surface).OfType<Grid>().Single(grid => Equals(grid.Tag, "SourcePriorityRowContent") || Equals(grid.Tag, "CardPriorityRowContent"));
        var handle = Walk(surface).OfType<PointerDragButton>().Single();
        var toggle = Walk(surface).OfType<ToggleSwitch>().Single();
        var more = Walk(surface).OfType<Button>().Single(button => button.Flyout is not null);
        var labels = Walk(surface).OfType<StackPanel>().FirstOrDefault(panel => Walk(panel).OfType<TextBlock>().Count() >= 2);
        if (Math.Abs(handle.Width - 32) > .01 || Math.Abs(handle.Height - 36) > .01 ||
            Walk(handle).OfType<Microsoft.UI.Xaml.Shapes.Ellipse>().Count() != 6 ||
            toggle.ActualWidth <= 0 || labels is null || labels.ActualWidth <= 0 ||
            Math.Abs(more.Width - 36) > .01 || Math.Abs(more.Height - 36) > .01)
            throw new InvalidOperationException($"A reorder row is missing its six-dot handle, labels, switch or compact menu: tag={surface.Tag}, handle={handle.Width:F1}x{handle.Height:F1}, dots={Walk(handle).OfType<Microsoft.UI.Xaml.Shapes.Ellipse>().Count()}, toggle={toggle.ActualWidth:F1}x{toggle.ActualHeight:F1}, labels={(labels is null ? "null" : $"{labels.ActualWidth:F1}x{labels.ActualHeight:F1}")}, menu={more.Width:F1}x{more.Height:F1}.");
        return new ReorderRowMetrics(row.Padding, row.ColumnSpacing, row.MinHeight, $"{handle.Width:0}x{handle.Height:0}", $"{more.Width:0}x{more.Height:0}");
    }
    private static object AssertUnifiedReorderRows(IReadOnlyList<Border> cards, ReorderRowMetrics sourceMetrics)
    {
        if (cards.Count == 0) throw new InvalidOperationException("The quick-card reorder list has no realized rows.");
        var cardMetrics = AssertReorderRows(cards);
        if (sourceMetrics.Padding != cardMetrics.Padding || Math.Abs(sourceMetrics.ColumnSpacing - cardMetrics.ColumnSpacing) > .01 ||
            Math.Abs(sourceMetrics.MinHeight - cardMetrics.MinHeight) > .01)
            throw new InvalidOperationException("Quick-card and source rows do not share the same spacing and row metrics.");
        return new
        {
            padding = cardMetrics.Padding.ToString(),
            columnSpacing = cardMetrics.ColumnSpacing,
            minHeight = cardMetrics.MinHeight,
            handle = cardMetrics.Handle,
            menu = cardMetrics.Menu,
            unified = true
        };
    }
    private static string DescribeCardCell(Border cell)
    {
        var row = Walk(cell).OfType<Grid>().FirstOrDefault(grid => Equals(grid.Tag, "CardPriorityRowContent"));
        var labels = row is null ? null : Walk(row).OfType<StackPanel>().FirstOrDefault(panel => Walk(panel).OfType<TextBlock>().Count() >= 2);
        var toggle = Walk(cell).OfType<ToggleSwitch>().FirstOrDefault();
        var more = Walk(cell).OfType<Button>().FirstOrDefault(button => button.Flyout is not null);
        var text = labels is null ? "" : string.Join("/", Walk(labels).OfType<TextBlock>().Select(item => $"{item.Text}:{item.ActualWidth:F1}x{item.ActualHeight:F1}"));
        return $"{cell.Tag}={cell.ActualHeight:F2} w={cell.ActualWidth:F1} row={(row?.ActualWidth ?? 0):F1}x{(row?.ActualHeight ?? 0):F1} " +
            $"labels={(labels?.ActualWidth ?? 0):F1}x{(labels?.ActualHeight ?? 0):F1} text=[{text}] toggle={(toggle?.ActualWidth ?? 0):F1}x{(toggle?.ActualHeight ?? 0):F1} menu={(more?.ActualWidth ?? 0):F1}x{(more?.ActualHeight ?? 0):F1}";
    }
    private static string HeaderText(Expander expander) => expander.Header switch
    {
        string text => text,
        TextBlock block => block.Text,
        _ => expander.Header?.ToString() ?? ""
    };
    private static T? Ancestor<T>(DependencyObject item) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(item); parent is not null; parent = VisualTreeHelper.GetParent(parent)) if (parent is T match) return match;
        return null;
    }
    private static object[] Toggles(DependencyObject root)
    {
        var checks = new List<object>();
        foreach (var toggle in Walk(root).OfType<ToggleSwitch>().Where(toggle => toggle.ActualWidth > 0 && toggle.ActualHeight > 0))
        {
            if (toggle.ActualHeight < 32) throw new InvalidOperationException("ToggleSwitch native spacing is compressed.");
            checks.Add(new { label = AutomationProperties.GetName(toggle), id = AutomationProperties.GetAutomationId(toggle), width = toggle.ActualWidth, height = toggle.ActualHeight });
        }
        if (Walk(root).OfType<CheckBox>().Any(box => box.ActualWidth > 0 && box.ActualHeight > 0))
            throw new InvalidOperationException("Settings must not expose visible CheckBox controls.");
        if (checks.Count == 0) throw new InvalidOperationException("No realized settings ToggleSwitch was found.");
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
