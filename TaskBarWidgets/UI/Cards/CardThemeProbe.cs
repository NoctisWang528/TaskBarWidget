using System.Text.Json;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        foreach (var card in cards._cards)
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
            var path = Path.Combine(output, $"{System.Array.IndexOf(cards._cards, card)}-{expected}.png");
            await UiLayoutProbe.CaptureAsync(card.Root, path);
            result.Add(new { card = card.Window.Title, actual = card.Root.ActualTheme.ToString(), backdrop = backdrop?.Theme.ToString() ?? "unavailable-offscreen", minimumTextContrast = textChecks.Count > 0 ? textChecks.Min() : 0, path });
        }
        foreach (var flyout in cards._flyouts)
            if (!flyout.MenuFlyoutPresenterStyle.Setters.OfType<Setter>().Any(setter => setter.Property == FrameworkElement.RequestedThemeProperty && Equals(setter.Value, expected)))
                throw new InvalidOperationException("A device/source menu kept an old theme.");
        return new { cards = result, stalePollIgnored = true, menuThemeUpdated = true };
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
