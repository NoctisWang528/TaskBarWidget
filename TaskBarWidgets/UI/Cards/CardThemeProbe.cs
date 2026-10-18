using System.Text.Json;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinRT;

namespace TaskBarWidgets.UI;

public sealed partial class CardGroup
{
    // Runs on the existing offscreen cards, using the same settings event and
    // backend-reply path as production. No user settings or OS theme are changed.
    internal static async Task<object> ProbeThemeAsync(CardGroup cards, ElementTheme expected)
    {
        cards.ApplyBackendTheme(JsonSerializer.SerializeToElement(new { toolWindowTheme = expected == ElementTheme.Dark ? 1 : 2 }));
        await Task.Delay(80);
        var output = Path.Combine(AppContext.BaseDirectory, "..", "ui-theme-check");
        Directory.CreateDirectory(output);
        var result = new List<object>();
        foreach (var card in cards.DisplayedCards)
        {
            card.Root.UpdateLayout();
            card.MeasureHeight(); card.Root.UpdateLayout();
            if (card.Root.RequestedTheme != expected) throw new InvalidOperationException("A stale media poll overwrote the settings theme preview.");
            var backdrop = card.Window.SystemBackdrop.GetDefaultSystemBackdropConfiguration(card.Window.As<ICompositionSupportsSystemBackdrop>(), card.Root.XamlRoot);
            var dark = card.Root.ActualTheme == ElementTheme.Dark;
            if (backdrop is not null && backdrop.Theme != SystemBackdropTheme.Default && backdrop.Theme != (dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light))
                throw new InvalidOperationException("Mica configuration did not follow the card's actual theme.");
            var background = dark ? Microsoft.UI.ColorHelper.FromArgb(255, 32, 32, 32) : Microsoft.UI.ColorHelper.FromArgb(255, 243, 243, 243);
            var textChecks = new List<double>();
            foreach (var text in WalkCardTheme(card.Root).OfType<TextBlock>().Where(text => text.Text.Length > 0 && text.Visibility == Visibility.Visible && text.FontSize <= 12 && text.Foreground is SolidColorBrush))
            {
                // Disabled control labels follow the platform's disabled palette.
                if (DisabledParent(text)) continue;
                var color = ((SolidColorBrush)text.Foreground).Color;
                var ratio = Contrast(color, background, text.Opacity);
                if (ratio < 4.5) throw new InvalidOperationException($"Low card text contrast: {card.Window.Title}, {text.Text}, {ratio:0.00}.");
                textChecks.Add(ratio);
            }
            var path = Path.Combine(output, $"{System.Array.IndexOf(cards.CardWindows.ToArray(), card)}-{expected}.png");
            await UiLayoutProbe.CaptureAsync(card.Root, path);
            result.Add(new { card = card.Window.Title, actual = card.Root.ActualTheme.ToString(), backdrop = backdrop?.Theme.ToString() ?? "unavailable-offscreen", minimumTextContrast = textChecks.Count > 0 ? textChecks.Min() : 0, path });
        }
        var menus = await ProbeOpenMenusAsync(cards, expected, output);
        return new { cards = result, stalePollIgnored = true, menuThemeUpdated = true, menus };
    }

    private static async Task<object> ProbeOpenMenusAsync(CardGroup cards, ElementTheme theme, string output)
    {
        var oldOutputs = cards._outputs; var oldInputs = cards._inputs; var oldSources = cards._sources;
        var oldSelection = cards._selectedSourceId;
        var targets = new[] { cards._output, cards._input, cards._source };
        var enabled = targets.Select(target => target.IsEnabled).ToArray();
        cards._outputs = [new("speaker", "Test speakers", true, true, .5, false, true), new("headset", "Test headset", false, false, .5, false, true)];
        cards._inputs = [new("microphone", "Test microphone", true, true, .5, false, true), new("headset-mic", "Test headset microphone", false, false, .5, false, true)];
        cards._sources = [new("player", "Test music player", ""), new("browser", "Test browser", "")];
        cards._selectedSourceId = "player";
        var result = new List<object>();
        try
        {
            for (var index = 0; index < targets.Length; index++)
            {
                var target = targets[index]; target.IsEnabled = true;
                var flyout = (MenuFlyout)target.Flyout;
                try
                {
                    // Keep the real popup inside the existing offscreen no-activate test HWND.
                    if (!flyout.ShouldConstrainToRootBounds) flyout.ShouldConstrainToRootBounds = true;
                    flyout.AreOpenCloseAnimationsEnabled = false;
                    flyout.ShowAt(target, new FlyoutShowOptions { ShowMode = FlyoutShowMode.Transient, Placement = FlyoutPlacementMode.Full });
                    await Task.Delay(80);
                    var presenters = VisualTreeHelper.GetOpenPopupsForXamlRoot(target.XamlRoot)
                        .SelectMany(popup => WalkCardTheme(popup.Child).Prepend(popup.Child)).OfType<MenuFlyoutPresenter>().ToArray();
                    var presenter = presenters.Single(); presenter.UpdateLayout();
                    UiTemplateProbe.AssertOwnedWindowsOffscreen();
                    if (!flyout.IsOpen || !presenter.IsLoaded || presenter.ActualWidth <= 0)
                        throw new InvalidOperationException($"Menu popup state: open={flyout.IsOpen}, constrained={flyout.IsConstrainedToRootBounds}, requestedConstrain={flyout.ShouldConstrainToRootBounds}, loaded={presenter.IsLoaded}, size={presenter.ActualWidth}x{presenter.ActualHeight}.");
                    if (presenter.ActualTheme != target.ActualTheme || presenter.SystemBackdrop is not null ||
                        presenter.Background is not SolidColorBrush { Color.A: 255 } fill)
                        throw new InvalidOperationException("The menu kept an independently themed Acrylic or transparent background.");
                    var contrasts = new List<double>();
                    foreach (var state in new[] { "Normal", "PointerOver", "Pressed" })
                    {
                        foreach (var item in flyout.Items.OfType<MenuFlyoutItem>()) VisualStateManager.GoToState(item, state, false);
                        presenter.UpdateLayout();
                        foreach (var element in flyout.Items.OfType<MenuFlyoutItem>().SelectMany(WalkCardTheme))
                        {
                            if (element is not FrameworkElement { ActualWidth: > 0, ActualHeight: > 0, Visibility: Visibility.Visible } visible || DisabledParent(visible)) continue;
                            var brush = element switch { TextBlock text when text.Text.Length > 0 => text.Foreground, FontIcon icon => icon.Foreground, _ => null };
                            if (brush is not SolidColorBrush ink) continue;
                            var ratio = Contrast(ink.Color, fill.Color, element is FrameworkElement textElement ? textElement.Opacity : 1);
                            if (ratio < (element is FontIcon ? 3 : 4.5)) throw new InvalidOperationException($"Unreadable popup {index}/{theme}/{state}, {element.GetType().Name}: contrast {ratio:0.00}.");
                            contrasts.Add(ratio);
                        }
                    }
                    if (contrasts.Count == 0) throw new InvalidOperationException("Menu text and checkmark were not measured.");
                    var path = Path.Combine(output, $"menu-{index}-{theme}.png");
                    await UiLayoutProbe.CaptureAsync(presenter, path);
                    result.Add(new { menu = index, actualTheme = presenter.ActualTheme.ToString(), background = fill.Color.ToString(), minimumContrast = contrasts.Min(), path });
                }
                finally { flyout.Hide(); }
            }
        }
        finally
        {
            cards._outputs = oldOutputs; cards._inputs = oldInputs; cards._sources = oldSources; cards._selectedSourceId = oldSelection;
            for (var index = 0; index < targets.Length; index++) targets[index].IsEnabled = enabled[index];
        }
        return result;
    }
    private static bool DisabledParent(FrameworkElement element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is Control { IsEnabled: false }) return true;
        return false;
    }
    private static IEnumerable<DependencyObject> WalkCardTheme(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index); yield return child;
            foreach (var descendant in WalkCardTheme(child)) yield return descendant;
        }
    }
    private static double Contrast(Color foreground, Color background, double opacity)
    {
        static double Linear(double value) => value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        static double Luminance(double r, double g, double b) => .2126 * Linear(r / 255) + .7152 * Linear(g / 255) + .0722 * Linear(b / 255);
        var alpha = foreground.A / 255d * opacity;
        var ink = Luminance(foreground.R * alpha + background.R * (1 - alpha), foreground.G * alpha + background.G * (1 - alpha), foreground.B * alpha + background.B * (1 - alpha));
        var surface = Luminance(background.R, background.G, background.B);
        return (Math.Max(ink, surface) + .05) / (Math.Min(ink, surface) + .05);
    }
}
