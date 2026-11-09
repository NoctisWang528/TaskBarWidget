using System.Numerics;
using System.Text.Json.Nodes;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TaskBarWidgets.UI.Settings;

namespace TaskBarWidgets.UI;

public sealed partial class SettingsWindow
{
    internal static async Task<object> ProbeReorderControlsAsync(SettingsWindow window)
    {
        var original = window._media.Keys.ToDictionary(
            key => key,
            key => window._media.Get(key),
            StringComparer.Ordinal);
        try
        {
            window.ShowPage("sources");
            await Task.Delay(120);
            ((FrameworkElement)window.Content).UpdateLayout();
            var rows = window._sourceRows.Select(item => (FrameworkElement)item.Surface).ToArray();
            if (rows.Length < 3 || rows.Any(row => row.ActualWidth <= 0 || row.ActualHeight <= 0))
                throw new InvalidOperationException("The reorder probe requires realized source rows.");

            if (ReorderSequence.MoveToGap(["a", "b", "c"], "a", 1).SequenceEqual(["a", "b", "c"]) == false ||
                ReorderSequence.MoveToGap(["a", "b", "c"], "a", 2).SequenceEqual(["b", "a", "c"]) == false ||
                ReorderSequence.MoveToGap(["a", "b", "c"], "c", 0).SequenceEqual(["c", "a", "b"]) == false)
                throw new InvalidOperationException("Reorder gap semantics regressed.");

            if (CardTargetGap(0, 3, lowerHalf: false) != 0 ||
                CardTargetGap(0, 3, lowerHalf: true) != 1 ||
                CardTargetGap(3, 3, lowerHalf: true) != 3)
                throw new InvalidOperationException("Quick-card pointer-to-gap mapping regressed.");

            var sameColumn = ProbeQuickCardMove(["a", "b"], ["c", "d"], "a", 0, 2);
            var crossColumn = ProbeQuickCardMove(["a", "b"], ["c", "d"], "b", 1, 1);
            var emptyColumn = ProbeQuickCardMove(["a"], [], "a", 1, 0);
            if (!sameColumn.Left.SequenceEqual(["b", "a"]) ||
                !crossColumn.Left.SequenceEqual(["a"]) ||
                !crossColumn.Right.SequenceEqual(["c", "b", "d"]) ||
                emptyColumn.Left.Length != 0 ||
                !emptyColumn.Right.SequenceEqual(["a"]))
                throw new InvalidOperationException($"Quick-card same-column, cross-column or empty-column movement was incorrect: same={string.Join(",", sameColumn.Left)}/{string.Join(",", sameColumn.Right)} cross={string.Join(",", crossColumn.Left)}/{string.Join(",", crossColumn.Right)} empty={string.Join(",", emptyColumn.Left)}/{string.Join(",", emptyColumn.Right)}.");

            var reducedMotion = new PointerReorder(window._navigation, new Canvas(), () => { }, animationsEnabled: false);
            var sourceOpacity = rows[0].Opacity;
            var baselineOffsets = rows.Select(row => row.Translation).ToArray();
            var extent = rows[0].ActualHeight + 8;
            reducedMotion.ShowRowPreview(rows, 0, Math.Min(3, rows.Length), extent);
            var movedOffset = rows[1].Translation;
            if (Math.Abs((movedOffset - baselineOffsets[1]).Y + extent) > .5 || rows[0].Opacity >= .5)
                throw new InvalidOperationException("Reduced-motion reorder did not apply the row displacement immediately.");
            reducedMotion.Cancel("probe");
            if (rows.Select((row, index) => (row.Translation - baselineOffsets[index]).Length()).Any(delta => delta > .01) ||
                Math.Abs(rows[0].Opacity - sourceOpacity) > .01)
                throw new InvalidOperationException("Canceling a reorder did not restore row positions and opacity: " +
                    string.Join(", ", rows.Select((row, index) => $"{index}={row.Translation.Y:F2}/{baselineOffsets[index].Y:F2}/{row.Opacity:F2}")) +
                    $", source={sourceOpacity:F2}.");

            var pointer = window.ProbePointerReorder;
            pointer.ShowVerticalGap(rows[2], after: false);
            window._root.UpdateLayout();
            var gap = pointer.InsertionBounds;
            var expectedGap = PointerReorder.LayoutBounds(rows[2], window._navigation);
            if (pointer.InsertionVisibility != Visibility.Visible || gap.Height < 2.5 || gap.Height > 3.5 || gap.Width < 16 ||
                Math.Abs(gap.X - expectedGap.X - 6) > 1 || Math.Abs(gap.Y - expectedGap.Y + 2) > 1)
                throw new InvalidOperationException("The reorder target gap was not presented as a thin realized placeholder.");
            pointer.HideInsertion();

            var animated = new PointerReorder(window._navigation, new Canvas(), () => { }, animationsEnabled: true);
            var originalBounds = PointerReorder.LayoutBounds(rows[1], window._navigation);
            animated.ShowRowPreview(rows, 0, 3, extent);
            var starts = animated.RowAnimationStarts;
            for (var repeat = 0; repeat < 5; repeat++)
            {
                await Task.Delay(40);
                animated.ShowRowPreview(rows, 0, 3, extent);
                if (PointerReorder.LayoutBounds(rows[1], window._navigation) != originalBounds)
                    throw new InvalidOperationException("Animated preview changed the row's hit-test layout.");
            }
            if (animated.RowAnimationStarts != starts || Math.Abs(rows[1].Translation.Y + extent) > .5)
                throw new InvalidOperationException("Repeated pointer updates restarted or prevented the row animation from settling.");
            animated.ClearRowPreview();
            await Task.Delay(30);
            animated.ShowRowPreview(rows, 0, 3, extent);
            animated.Cancel("probe-mid-animation");
            await Task.Delay(170);
            if (rows.Where((row, index) => row.Translation != baselineOffsets[index]).Any())
                throw new InvalidOperationException("A canceled preview retained an animation or displaced the next gesture.");

            window.ShowPage("quickCards");
            await Task.Delay(120);
            var speed = WalkTheme(window._page).OfType<Slider>().Single(control =>
                AutomationProperties.GetAutomationId(control) == "setting-quickCardAnimationSpeedPercent");
            var fps = WalkTheme(window._page).OfType<ComboBox>().Single(control =>
                AutomationProperties.GetAutomationId(control) == "setting-quickCardAnimationFps");
            speed.Value = 220;
            fps.SelectedIndex = 4; // 120 FPS.
            if (window._media.Number("quickCardAnimationSpeedPercent") != 220 || window._media.Number("quickCardAnimationFps") != 120)
                throw new InvalidOperationException("Animation controls did not update the settings draft.");
            window.RenderPage();
            await Task.Delay(120);
            speed = WalkTheme(window._page).OfType<Slider>().Single(control =>
                AutomationProperties.GetAutomationId(control) == "setting-quickCardAnimationSpeedPercent");
            fps = WalkTheme(window._page).OfType<ComboBox>().Single(control =>
                AutomationProperties.GetAutomationId(control) == "setting-quickCardAnimationFps");
            if (speed.Value != 220 || fps.SelectedIndex != 4)
                throw new InvalidOperationException("Animation draft was lost after rerendering settings.");
            speed.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
            await Task.Delay(120);
            await UiLayoutProbe.CaptureAsync(window._root, Path.Combine(AppContext.BaseDirectory, "..", "ui-layout-check",
                "settings-card-animation.png"));
            var firstCard = window._cardOrderCells[0].Cell.TransformToVisual(window._pageScroll)
                .TransformPoint(new Windows.Foundation.Point());
            window._pageScroll!.ChangeView(null, Math.Max(0, window._pageScroll.VerticalOffset + firstCard.Y - 80), null, true);
            await Task.Delay(120);
            window._root.UpdateLayout();
            var (leftBefore, _) = window.QuickCardLayout();
            if (leftBefore.Count < 2)
                throw new InvalidOperationException("The fixture must realize at least two cards in one column.");
            var id = leftBefore[0];
            var overlayOrigin = window._dragOverlay.TransformToVisual(window._navigation).TransformPoint(new Windows.Foundation.Point());
            if (Math.Abs(overlayOrigin.X) > .5 || Math.Abs(overlayOrigin.Y) > .5 ||
                Math.Abs(window._dragOverlay.ActualHeight - window._navigation.ActualHeight) > .5)
                throw new InvalidOperationException("Drag ghost and insertion overlay do not share the pointer coordinate space.");
            foreach (var (targetColumn, insertAtStart) in new[] { (0, false), (1, false), (1, true) })
            {
                var cells = window._cardOrderCells.Where(cell => cell.Column == targetColumn).OrderBy(cell => cell.Slot).ToArray();
                var targetSlot = insertAtStart ? 0 : cells.Length - 1;
                var destination = PointerReorder.LayoutBounds(cells[targetSlot].Cell, window._navigation);
                var point = new Windows.Foundation.Point(destination.X + destination.Width / 2, destination.Y + 4);
                window.MoveQuickCardDrag(id, point);
                window._root.UpdateLayout();
                var finalCell = cells[targetSlot - (targetColumn == 0 ? 1 : 0)].Cell;
                var expected = PointerReorder.LayoutBounds(finalCell, window._navigation);
                var actual = pointer.InsertionBounds;
                if (Math.Abs(actual.X - expected.X) > 1 || Math.Abs(actual.Y - expected.Y) > 1 ||
                    Math.Abs(actual.Width - expected.Width) > 1 || Math.Abs(actual.Height - expected.Height) > 1)
                    throw new InvalidOperationException("Card drop highlight does not match the final same/cross-column vacancy.");
                for (var repeat = 0; repeat < 5; repeat++)
                {
                    await Task.Delay(40);
                    window.MoveQuickCardDrag(id, point);
                    if (window.FindQuickCardDropTarget(point) is not { Column: var column, Gap: var targetGap } ||
                        column != targetColumn || targetGap != targetSlot)
                        throw new InvalidOperationException("Card target oscillated while the preview was animating.");
                }
                await UiLayoutProbe.CaptureAsync(window._root, Path.Combine(AppContext.BaseDirectory, "..", "ui-layout-check",
                    $"settings-card-drag-{targetColumn}-{targetSlot}.png"));
                pointer.Cancel("probe-card-preview");
                if (cells[^1].Cell.Opacity != 1)
                    throw new InvalidOperationException("Cancel did not restore the temporarily covered empty target.");
            }
            var outside = new Windows.Foundation.Point(-10, -10);
            var beforeOutside = window.PageRenderCount;
            window.MoveQuickCardDrag(id, outside);
            window.FinishQuickCardDrag(id, outside);
            if (pointer.InsertionVisibility != Visibility.Collapsed || window.PageRenderCount != beforeOutside)
                throw new InvalidOperationException("Releasing outside committed the previous drop target.");
            var beforeDrop = window.PageRenderCount;
            var renders = window.ProbeQuickCardDrop(id, 0, leftBefore.Count);
            var (leftAfter, _) = window.QuickCardLayout();
            if (renders != 1 || window.PageRenderCount != beforeDrop + 1 || leftAfter[^1] != id)
                throw new InvalidOperationException("A completed card drag did not commit exactly once and land at the requested gap.");

            return new
            {
                sameColumn = true,
                crossColumn = true,
                emptyColumn = true,
                pointerGapPresentation = true,
                reducedMotionFallback = true,
                cancelRestoresRows = true,
                dropCommitsOnce = true,
                overlayCoordinates = true,
                stableAnimatedTargets = true,
                outsideReleaseCanceled = true,
                animationSettingsDraft = true
            };
        }
        finally
        {
            window._media.Pending.Clear();
            window._media.Readback(original);
            window.ShowPage("taskbar");
        }
    }
}
