using System.Numerics;
using System.Text.Json.Nodes;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
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
            var baselineOffsets = rows.Select(row => ElementCompositionPreview.GetElementVisual(row).Offset).ToArray();
            var extent = rows[0].ActualHeight + 8;
            reducedMotion.ShowRowPreview(rows, 0, Math.Min(3, rows.Length), extent);
            var movedOffset = ElementCompositionPreview.GetElementVisual(rows[1]).Offset;
            if (Math.Abs((movedOffset - baselineOffsets[1]).Y + extent) > .5 || rows[0].Opacity >= .5)
                throw new InvalidOperationException("Reduced-motion reorder did not apply the row displacement immediately.");
            reducedMotion.Cancel("probe");
            if (rows.Select((row, index) => (ElementCompositionPreview.GetElementVisual(row).Offset - baselineOffsets[index]).Length()).Any(delta => delta > .01) ||
                Math.Abs(rows[0].Opacity - sourceOpacity) > .01)
                throw new InvalidOperationException("Canceling a reorder did not restore row positions and opacity: " +
                    string.Join(", ", rows.Select((row, index) => $"{index}={ElementCompositionPreview.GetElementVisual(row).Offset.Y:F2}/{baselineOffsets[index].Y:F2}/{row.Opacity:F2}")) +
                    $", source={sourceOpacity:F2}.");

            var pointer = window.ProbePointerReorder;
            pointer.ShowVerticalGap(rows[2], after: false);
            var gap = pointer.InsertionBounds;
            if (pointer.InsertionVisibility != Visibility.Visible || gap.Height < 2.5 || gap.Height > 3.5 || gap.Width < 16)
                throw new InvalidOperationException("The reorder target gap was not presented as a thin realized placeholder.");
            pointer.HideInsertion();

            window.ShowPage("quickCards");
            await Task.Delay(120);
            var (leftBefore, _) = window.QuickCardLayout();
            if (leftBefore.Count < 2)
                throw new InvalidOperationException("The fixture must realize at least two cards in one column.");
            var id = leftBefore[0];
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
                dropCommitsOnce = true
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
